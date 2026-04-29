using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

public class SpeechTranscriptionService
{
    private readonly IConfiguration _configuration;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly HttpClient _httpClient;

    public SpeechTranscriptionService(
        IConfiguration configuration,
        BlobServiceClient blobServiceClient,
        HttpClient httpClient)
    {
        _configuration = configuration;
        _blobServiceClient = blobServiceClient;
        _httpClient = httpClient;
    }

    public async Task<SpeechConversationResult> ProcessWavFileAsync(
        string wavFilePath,
        string caseId,
        string fileId,
        CancellationToken cancellationToken = default)
    {
        string audioContainer = _configuration["Storage:AudioContainer"]!;
        string outputContainer = _configuration["Storage:OutputContainer"]!;
        string locale = _configuration["Speech:Locale"] ?? "en-US";

        string wavBlobName = $"{caseId}/audio/{fileId}/{Path.GetFileName(wavFilePath)}";

        // 2. Upload WAV to blob
        BlobClient wavBlobClient = await UploadFileToBlobAsync(
            audioContainer,
            wavBlobName,
            wavFilePath,
            cancellationToken);

        // 3. Generate SAS URL
        Uri wavSasUrl = GenerateReadSasUrl(wavBlobClient, TimeSpan.FromHours(6));

        // 4. Call Speech Transcription Service
        string transcriptionUrl = await CreateTranscriptionJobAsync(
            wavSasUrl,
            locale,
            cancellationToken);

        await WaitForTranscriptionToFinishAsync(
            transcriptionUrl,
            cancellationToken);

        JsonDocument rawResult = await DownloadTranscriptionResultAsync(
            transcriptionUrl,
            cancellationToken);

        SpeechConversationResult cleanResult = ParseConversationResult(
            rawResult,
            Path.GetFileName(wavFilePath),
            locale);

        // 5. Save transcript JSON to output container
        string outputBlobName = $"{caseId}/transcripts/{fileId}/conversation.json";

        await SaveConversationJsonAsync(
            outputContainer,
            outputBlobName,
            cleanResult,
            cancellationToken);

        return cleanResult;
    }

    private async Task<BlobClient> UploadFileToBlobAsync(
        string containerName,
        string blobName,
        string filePath,
        CancellationToken cancellationToken)
    {
        BlobContainerClient containerClient =
            _blobServiceClient.GetBlobContainerClient(containerName);

        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        BlobClient blobClient = containerClient.GetBlobClient(blobName);

        await using FileStream stream = File.OpenRead(filePath);

        await blobClient.UploadAsync(
            stream,
            overwrite: true,
            cancellationToken);

        return blobClient;
    }

    private Uri GenerateReadSasUrl(BlobClient blobClient, TimeSpan expiresIn)
    {
        if (!blobClient.CanGenerateSasUri)
            throw new InvalidOperationException("Cannot generate SAS URL. Use a connection string with account key.");

        BlobSasBuilder sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = blobClient.BlobContainerName,
            BlobName = blobClient.Name,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn)
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder);
    }

    private async Task<string> CreateTranscriptionJobAsync(
        Uri wavSasUrl,
        string locale,
        CancellationToken cancellationToken)
    {
        string speechKey = _configuration["Speech:Key"]!;
        string speechRegion = _configuration["Speech:Region"]!;

        string url =
            $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/transcriptions:submit?api-version=2024-11-15";

        var requestBody = new
        {
            contentUrls = new[]
            {
                wavSasUrl.ToString()
            },
            locale = locale,
            displayName = $"Interview transcription {DateTime.UtcNow:yyyyMMddHHmmss}",
            properties = new
            {
                wordLevelTimestampsEnabled = true,
                displayFormWordLevelTimestampsEnabled = true,
                diarization = new
                {
                    enabled = true,
                    maxSpeakers = 10
                },
                punctuationMode = "DictatedAndAutomatic",
                profanityFilterMode = "Masked",
                timeToLiveHours = 48
            }
        };

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
        request.Content = JsonContent.Create(requestBody);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken);

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Speech transcription submit failed: {response.StatusCode} - {responseText}");

        using JsonDocument json = JsonDocument.Parse(responseText);

        return json.RootElement.GetProperty("self").GetString()
               ?? throw new Exception("Speech response did not return self URL.");
    }

    private async Task WaitForTranscriptionToFinishAsync(
        string transcriptionUrl,
        CancellationToken cancellationToken)
    {
        string speechKey = _configuration["Speech:Key"]!;

        while (true)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, transcriptionUrl);
            request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

            using HttpResponseMessage response =
                await _httpClient.SendAsync(request, cancellationToken);

            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Speech transcription status failed: {response.StatusCode} - {responseText}");

            using JsonDocument json = JsonDocument.Parse(responseText);

            string status = json.RootElement.GetProperty("status").GetString() ?? "";

            if (status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
                return;

            if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Speech transcription failed: {responseText}");

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private async Task<JsonDocument> DownloadTranscriptionResultAsync(
        string transcriptionUrl,
        CancellationToken cancellationToken)
    {
        string speechKey = _configuration["Speech:Key"]!;

        string filesUrl = transcriptionUrl.TrimEnd('/') + "/files?api-version=2024-11-15";

        using HttpRequestMessage filesRequest = new HttpRequestMessage(HttpMethod.Get, filesUrl);
        filesRequest.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

        using HttpResponseMessage filesResponse =
            await _httpClient.SendAsync(filesRequest, cancellationToken);

        string filesJsonText = await filesResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!filesResponse.IsSuccessStatusCode)
            throw new Exception($"Speech list files failed: {filesResponse.StatusCode} - {filesJsonText}");

        using JsonDocument filesJson = JsonDocument.Parse(filesJsonText);

        string? resultUrl = null;

        foreach (JsonElement file in filesJson.RootElement.GetProperty("values").EnumerateArray())
        {
            string kind = file.GetProperty("kind").GetString() ?? "";

            if (kind.Equals("Transcription", StringComparison.OrdinalIgnoreCase))
            {
                resultUrl = file
                    .GetProperty("links")
                    .GetProperty("contentUrl")
                    .GetString();

                break;
            }
        }

        if (string.IsNullOrWhiteSpace(resultUrl))
            throw new Exception("No transcription result file was found.");

        using HttpResponseMessage resultResponse =
            await _httpClient.GetAsync(resultUrl, cancellationToken);

        string resultText = await resultResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!resultResponse.IsSuccessStatusCode)
            throw new Exception($"Download transcription result failed: {resultResponse.StatusCode} - {resultText}");

        return JsonDocument.Parse(resultText);
    }

    private SpeechConversationResult ParseConversationResult(
        JsonDocument rawJson,
        string fileName,
        string locale)
    {
        SpeechConversationResult result = new SpeechConversationResult
        {
            FileName = fileName,
            Locale = locale
        };

        if (!rawJson.RootElement.TryGetProperty("recognizedPhrases", out JsonElement phrases))
            return result;

        foreach (JsonElement phrase in phrases.EnumerateArray())
        {
            string speaker = "Speaker 1";

            if (phrase.TryGetProperty("speaker", out JsonElement speakerElement))
            {
                speaker = $"Speaker {speakerElement.GetInt32()}";
            }

            TimeSpan start = TimeSpan.Zero;
            TimeSpan end = TimeSpan.Zero;

            if (phrase.TryGetProperty("offset", out JsonElement offsetElement))
            {
                start = TimeSpan.FromTicks(offsetElement.GetInt64());
            }

            if (phrase.TryGetProperty("duration", out JsonElement durationElement))
            {
                end = start.Add(TimeSpan.FromTicks(durationElement.GetInt64()));
            }

            string text = "";

            if (phrase.TryGetProperty("nBest", out JsonElement nBest) &&
                nBest.GetArrayLength() > 0)
            {
                JsonElement best = nBest[0];

                if (best.TryGetProperty("display", out JsonElement display))
                    text = display.GetString() ?? "";
                else if (best.TryGetProperty("lexical", out JsonElement lexical))
                    text = lexical.GetString() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Segments.Add(new SpeechConversationSegment
                {
                    Speaker = speaker,
                    StartTime = start,
                    EndTime = end,
                    Text = text
                });
            }
        }

        return result;
    }

    private async Task SaveConversationJsonAsync(
        string containerName,
        string blobName,
        SpeechConversationResult result,
        CancellationToken cancellationToken)
    {
        BlobContainerClient containerClient =
            _blobServiceClient.GetBlobContainerClient(containerName);

        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        BlobClient blobClient = containerClient.GetBlobClient(blobName);

        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        BinaryData data = BinaryData.FromString(json);

        await blobClient.UploadAsync(
            data,
            overwrite: true,
            cancellationToken);
    }
}
