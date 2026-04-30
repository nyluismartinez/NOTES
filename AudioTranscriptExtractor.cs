using AIChat_IngestionFunctions.Helper;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using FFMpegCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIChat_IngestionFunctions.Classes;

public sealed class AudioTranscriptExtractor
{
    private readonly BlobServiceClient _blobSvc;
    private readonly IConfiguration _cfg;
    private readonly HttpClient _httpClient;

    public AudioTranscriptExtractor(
        BlobServiceClient blobSvc,
        IConfiguration cfg,
        IHttpClientFactory httpClientFactory)
    {
        _blobSvc = blobSvc;
        _cfg = cfg;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<string> ExtractAsync(
        string localFilePath,
        IngestQueueMessage msg,
        CancellationToken cancellationToken = default)
    {
        var procContainerName = _cfg["Storage:ProcContainer"]!;
        var outputContainerName = _cfg["Storage:OutputContainer"]!;

        var proc = _blobSvc.GetBlobContainerClient(procContainerName);
        var output = _blobSvc.GetBlobContainerClient(outputContainerName);

        await proc.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await output.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var tempWavPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}_converted.wav");

        try
        {
            await ConvertToWavAsync(localFilePath, tempWavPath, cancellationToken);

            var wavBlobPath = $"{msg.CaseId}/{msg.CaseFolder}/{msg.CaseFileId}/converted.wav";

            var wavBlob = proc.GetBlobClient(wavBlobPath);

            await using (var wavStream = File.OpenRead(tempWavPath))
            {
                await wavBlob.UploadAsync(
                    wavStream,
                    overwrite: true,
                    cancellationToken);
            }

            var wavSasUrl = CreateReadSasUrl(wavBlob, TimeSpan.FromHours(6));

            var transcriptionJson = await RunAzureSpeechBatchTranscriptionAsync(
                wavSasUrl,
                msg,
                cancellationToken);

            var outputJsonPath =
                $"{msg.CaseId}/{msg.CaseFolder}/{msg.CaseFileId}/transcribed.json";

            await output.GetBlobClient(outputJsonPath)
                .UploadAsync(
                    new MemoryStream(Encoding.UTF8.GetBytes(transcriptionJson)),
                    overwrite: true,
                    cancellationToken);

            return ConvertSpeechJsonToPlainText(transcriptionJson);
        }
        catch (Exception ex)
        {


            return "";        
        }
        finally
        {
            if (File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); }
                catch { }
            }
        }
    }

    private static async Task ConvertToWavAsync(
        string inputPath,
        string outputWavPath,
        CancellationToken cancellationToken)
    {


        await FFMpegArguments
             .FromFileInput(inputPath)
             .OutputToFile(outputWavPath, overwrite: true, options => options
                 .WithAudioCodec("pcm_s16le")     // 16-bit PCM
                 .WithAudioSamplingRate(16000)    // 16kHz
                 .WithCustomArgument("-ac 1")     // mono (fix here)
                 .ForceFormat("wav"))
             .ProcessAsynchronously(true);


        //return outputWavPath;

        //var psi = new ProcessStartInfo
        //{
        //    FileName = "ffmpeg",
        //    Arguments = $"-y -i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le \"{outputWavPath}\"",
        //    RedirectStandardError = true,
        //    RedirectStandardOutput = true,
        //    UseShellExecute = false,
        //    CreateNoWindow = true
        //};

        //using var process = Process.Start(psi);

        //if (process == null)
        //    throw new InvalidOperationException("Could not start FFmpeg process.");

        //var errorTask = process.StandardError.ReadToEndAsync();

        //await process.WaitForExitAsync(cancellationToken);

        //var error = await errorTask;

        //if (process.ExitCode != 0)
        //    throw new InvalidOperationException($"FFmpeg failed: {error}");
    }

    private static Uri CreateReadSasUrl(BlobClient blobClient, TimeSpan validFor)
    {
        if (!blobClient.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "Cannot generate SAS URL. AzureWebJobsStorage must use a storage account connection string with account key.");
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = blobClient.BlobContainerName,
            BlobName = blobClient.Name,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(validFor)
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder);
    }

    private async Task<string> RunAzureSpeechBatchTranscriptionAsync(
        Uri audioSasUrl,
        IngestQueueMessage msg,
        CancellationToken cancellationToken)
    {
        var speechKey = _cfg["Speech:Key"]!;
        var speechRegion = _cfg["Speech:Region"]!;
        var locale = _cfg["Speech:Locale"] ?? "en-US";

        var endpoint =
            $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/v3.1/transcriptions";

        var body = new
        {
            contentUrls = new[] { audioSasUrl.ToString() },
            locale = locale,
            displayName = $"Transcription_{msg.CaseFileId}_{DateTime.UtcNow:yyyyMMddHHmmss}",
            properties = new
            {
                diarizationEnabled = true,
                wordLevelTimestampsEnabled = true,
                displayFormWordLevelTimestampsEnabled = true,
                punctuationMode = "DictatedAndAutomatic",
                profanityFilterMode = "Masked",
                timeToLive = "PT6H"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Speech transcription submit failed: {response.StatusCode} - {responseText}");
        }

        using var createdDoc = JsonDocument.Parse(responseText);

        var selfUrl = createdDoc.RootElement.GetProperty("self").GetString();

        if (string.IsNullOrWhiteSpace(selfUrl))
            throw new InvalidOperationException("Speech transcription response did not return self URL.");

        await WaitForSpeechJobToFinishAsync(selfUrl, speechKey, cancellationToken);

        var filesUrl = $"{selfUrl}/files";

        using var filesRequest = new HttpRequestMessage(HttpMethod.Get, filesUrl);
        filesRequest.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

        using var filesResponse = await _httpClient.SendAsync(filesRequest, cancellationToken);
        var filesJson = await filesResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!filesResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Speech files request failed: {filesResponse.StatusCode} - {filesJson}");
        }

        using var filesDoc = JsonDocument.Parse(filesJson);

        string? transcriptionFileUrl = null;

        foreach (var file in filesDoc.RootElement.GetProperty("values").EnumerateArray())
        {
            var kind = file.GetProperty("kind").GetString();

            if (string.Equals(kind, "Transcription", StringComparison.OrdinalIgnoreCase))
            {
                transcriptionFileUrl = file.GetProperty("links").GetProperty("contentUrl").GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptionFileUrl))
            throw new InvalidOperationException("No transcription result file was found.");

        return await _httpClient.GetStringAsync(transcriptionFileUrl, cancellationToken);
    }

    private async Task WaitForSpeechJobToFinishAsync(
        string selfUrl,
        string speechKey,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 120;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, selfUrl);
            request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Speech status check failed: {response.StatusCode} - {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Speech transcription failed: {json}");

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        throw new TimeoutException("Speech transcription did not finish in time.");
    }

    private static string ConvertSpeechJsonToPlainText(string transcriptionJson)
    {
        using var doc = JsonDocument.Parse(transcriptionJson);
        var root = doc.RootElement;

        var sb = new StringBuilder();

        if (!root.TryGetProperty("recognizedPhrases", out var phrases))
            return transcriptionJson;

        foreach (var phrase in phrases.EnumerateArray())
        {
            var speaker = phrase.TryGetProperty("speaker", out var speakerElement)
                ? speakerElement.GetInt32().ToString()
                : "?";

            var offset = phrase.TryGetProperty("offset", out var offsetElement)
                ? offsetElement.GetString()
                : "";

            var duration = phrase.TryGetProperty("duration", out var durationElement)
                ? durationElement.GetString()
                : "";

            var text = "";

            if (phrase.TryGetProperty("nBest", out var nbest) && nbest.GetArrayLength() > 0)
            {
                var best = nbest[0];

                if (best.TryGetProperty("display", out var displayElement))
                    text = displayElement.GetString() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"Speaker {speaker} [{offset} - {duration}]");
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }
}