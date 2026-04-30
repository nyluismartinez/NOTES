using AIChat_IngestionFunctions.Classes;
using AIChat_IngestionFunctions.Helper;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text;
using UglyToad.PdfPig;

namespace AIChat_IngestionFunctions;

public sealed class TextExtractor
{
    private readonly DocumentIntelligenceClient _docInt;
    private readonly AudioTranscriptExtractor _audioExtractor;

    public TextExtractor(
        DocumentIntelligenceClient docInt,
        AudioTranscriptExtractor audioExtractor)
    {
        _docInt = docInt;
        _audioExtractor = audioExtractor;
    }

    public async Task<string> ExtractAsync(
        string localFilePath,
        IngestQueueMessage msg,
        CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(msg.FileName).ToLowerInvariant();

        return ext switch
        {
            ".pdf" => await ExtractPdfAsync(localFilePath, cancellationToken),
            ".docx" => ExtractDocx(localFilePath),

            ".mp3" => await _audioExtractor.ExtractAsync(localFilePath, msg, cancellationToken),
            ".m4a" => await _audioExtractor.ExtractAsync(localFilePath, msg, cancellationToken),
            ".wav" => await _audioExtractor.ExtractAsync(localFilePath, msg, cancellationToken),
            ".ogg" => await _audioExtractor.ExtractAsync(localFilePath, msg, cancellationToken),

            _ => await ExtractWithDocIntelligenceAsync(localFilePath, cancellationToken)
        };
    }

    private async Task<string> ExtractPdfAsync(string localFilePath, CancellationToken cancellationToken)
    {
        var text = ExtractPdfWithPdfPig(localFilePath);

        // If PdfPig got useful text, keep it.
        if (!string.IsNullOrWhiteSpace(text) && text.Length > 300)
            return text;

        // Fallback for scanned/image PDFs
        return await ExtractWithDocIntelligenceAsync(localFilePath, cancellationToken);
    }

    private static string ExtractPdfWithPdfPig(string localFilePath)
    {
        var sb = new StringBuilder();

        using var pdf = PdfDocument.Open(localFilePath);

        foreach (var page in pdf.GetPages())
        {
            var pageText = page.Text?.Trim();

            if (!string.IsNullOrWhiteSpace(pageText))
            {
                sb.AppendLine($"[Page {page.Number}]");
                sb.AppendLine(pageText);
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractDocx(string localFilePath)
    {
        using var fs = File.OpenRead(localFilePath);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        ms.Position = 0;

        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;

        var sb = new StringBuilder();

        foreach (var p in body.Descendants<Paragraph>())
        {
            var t = p.InnerText?.Trim();
            if (!string.IsNullOrWhiteSpace(t))
                sb.AppendLine(t);
        }

        return sb.ToString().Trim();
    }

    private async Task<string> ExtractWithDocIntelligenceAsync(
        string localFilePath,
        CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(localFilePath);

        var op = await _docInt.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-read",
            BinaryData.FromStream(fs),
            cancellationToken: cancellationToken);

        var result = op.Value;

        var sb = new StringBuilder();

        foreach (var page in result.Pages)
        {
            sb.AppendLine($"[Page {page.PageNumber}]");

            foreach (var line in page.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Content))
                    sb.AppendLine(line.Content);
            }

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }







}