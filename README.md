public class SpeechTranscriptionHelper
{
    public async Task<SpeechConversationResult> ProcessWavFileAsync(
        string azureWebJobsStorage,
        string speechKey,
        string speechRegion,
        string locale,
        string wavFilePath,
        string caseId,
        string fileId,
        string audioContainerName,
        string outputContainerName,
        CancellationToken cancellationToken = default)
    {
        BlobServiceClient blobServiceClient = new BlobServiceClient(azureWebJobsStorage);
        HttpClient httpClient = new HttpClient();

        string wavBlobName = $"{caseId}/audio/{fileId}/{Path.GetFileName(wavFilePath)}";

        BlobClient wavBlobClient = await UploadFileToBlobAsync(
            blobServiceClient,
            audioContainerName,
            wavBlobName,
            wavFilePath,
            cancellationToken);

        Uri wavSasUrl = GenerateReadSasUrl(
            wavBlobClient,
            TimeSpan.FromHours(6));

        string transcriptionUrl = await CreateTranscriptionJobAsync(
            httpClient,
            speechKey,
            speechRegion,
            locale,
            wavSasUrl,
            cancellationToken);

        await WaitForTranscriptionToFinishAsync(
            httpClient,
            speechKey,
            transcriptionUrl,
            cancellationToken);

        JsonDocument rawResult = await DownloadTranscriptionResultAsync(
            httpClient,
            speechKey,
            transcriptionUrl,
            cancellationToken);

        SpeechConversationResult cleanResult = ParseConversationResult(
            rawResult,
            Path.GetFileName(wavFilePath),
            locale);

        string outputBlobName =
            $"{caseId}/transcripts/{fileId}/conversation.json";

        await SaveConversationJsonAsync(
            blobServiceClient,
            outputContainerName,
            outputBlobName,
            cleanResult,
            cancellationToken);

        return cleanResult;
    }

    private async Task<BlobClient> UploadFileToBlobAsync(
        BlobServiceClient blobServiceClient,
        string containerName,
        string blobName,
        string filePath,
        CancellationToken cancellationToken)
    {
        BlobContainerClient containerClient =
            blobServiceClient.GetBlobContainerClient(containerName);

        await containerClient.CreateIfNotExistsAsync(
            cancellationToken: cancellationToken);

        BlobClient blobClient =
            containerClient.GetBlobClient(blobName);

        await using FileStream stream =
            File.OpenRead(filePath);

        await blobClient.UploadAsync(
            stream,
            overwrite: true,
            cancellationToken);

        return blobClient;
    }

    private Uri GenerateReadSasUrl(
        BlobClient blobClient,
        TimeSpan expiresIn)
    {
        if (!blobClient.CanGenerateSasUri)
            throw new Exception("Cannot generate SAS URL.");

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
        HttpClient httpClient,
        string speechKey,
        string speechRegion,
        string locale,
        Uri wavSasUrl,
        CancellationToken cancellationToken)
    {
        string url =
            $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/transcriptions:submit?api-version=2024-11-15";

        var requestBody = new
        {
            contentUrls = new[]
            {
                wavSasUrl.ToString()
            },
            locale = locale,
            displayName = $"Interview {DateTime.UtcNow:yyyyMMddHHmmss}",
            properties = new
            {
                wordLevelTimestampsEnabled = true,
                displayFormWordLevelTimestampsEnabled = true,
                diarization = new
                {
                    enabled = true,
                    maxSpeakers = 10
                }
            }
        };

        using HttpRequestMessage request =
            new HttpRequestMessage(HttpMethod.Post, url);

        request.Headers.Add(
            "Ocp-Apim-Subscription-Key",
            speechKey);

        request.Content =
            JsonContent.Create(requestBody);

        using HttpResponseMessage response =
            await httpClient.SendAsync(
                request,
                cancellationToken);

        string responseText =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception(responseText);

        using JsonDocument json =
            JsonDocument.Parse(responseText);

        return json.RootElement
            .GetProperty("self")
            .GetString()!;
    }

    private async Task WaitForTranscriptionToFinishAsync(
        HttpClient httpClient,
        string speechKey,
        string transcriptionUrl,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using HttpRequestMessage request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    transcriptionUrl);

            request.Headers.Add(
                "Ocp-Apim-Subscription-Key",
                speechKey);

            using HttpResponseMessage response =
                await httpClient.SendAsync(
                    request,
                    cancellationToken);

            string responseText =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using JsonDocument json =
                JsonDocument.Parse(responseText);

            string status =
                json.RootElement
                    .GetProperty("status")
                    .GetString() ?? "";

            if (status == "Succeeded")
                return;

            if (status == "Failed")
                throw new Exception(responseText);

            await Task.Delay(
                TimeSpan.FromSeconds(15),
                cancellationToken);
        }
    }

    private async Task<JsonDocument> DownloadTranscriptionResultAsync(
        HttpClient httpClient,
        string speechKey,
        string transcriptionUrl,
        CancellationToken cancellationToken)
    {
        string filesUrl =
            transcriptionUrl.TrimEnd('/')
            + "/files?api-version=2024-11-15";

        using HttpRequestMessage request =
            new HttpRequestMessage(
                HttpMethod.Get,
                filesUrl);

        request.Headers.Add(
            "Ocp-Apim-Subscription-Key",
            speechKey);

        using HttpResponseMessage response =
            await httpClient.SendAsync(
                request,
                cancellationToken);

        string responseText =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        using JsonDocument filesJson =
            JsonDocument.Parse(responseText);

        string resultUrl = "";

        foreach (JsonElement file in filesJson.RootElement
                     .GetProperty("values")
                     .EnumerateArray())
        {
            string kind =
                file.GetProperty("kind")
                    .GetString() ?? "";

            if (kind == "Transcription")
            {
                resultUrl = file
                    .GetProperty("links")
                    .GetProperty("contentUrl")
                    .GetString() ?? "";

                break;
            }
        }

        using HttpResponseMessage resultResponse =
            await httpClient.GetAsync(
                resultUrl,
                cancellationToken);

        string resultText =
            await resultResponse.Content
                .ReadAsStringAsync(cancellationToken);

        return JsonDocument.Parse(resultText);
    }

    private SpeechConversationResult ParseConversationResult(
        JsonDocument rawJson,
        string fileName,
        string locale)
    {
        SpeechConversationResult result =
            new SpeechConversationResult
            {
                FileName = fileName,
                Locale = locale
            };

        if (!rawJson.RootElement.TryGetProperty(
                "recognizedPhrases",
                out JsonElement phrases))
            return result;

        foreach (JsonElement phrase in phrases.EnumerateArray())
        {
            string speaker = "Speaker 1";

            if (phrase.TryGetProperty(
                "speaker",
                out JsonElement speakerElement))
            {
                speaker =
                    $"Speaker {speakerElement.GetInt32()}";
            }

            string text = "";

            if (phrase.TryGetProperty(
                    "nBest",
                    out JsonElement nBest)
                && nBest.GetArrayLength() > 0)
            {
                text = nBest[0]
                    .GetProperty("display")
                    .GetString() ?? "";
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Segments.Add(
                    new SpeechConversationSegment
                    {
                        Speaker = speaker,
                        Text = text
                    });
            }
        }

        return result;
    }

    private async Task SaveConversationJsonAsync(
        BlobServiceClient blobServiceClient,
        string containerName,
        string blobName,
        SpeechConversationResult result,
        CancellationToken cancellationToken)
    {
        BlobContainerClient containerClient =
            blobServiceClient.GetBlobContainerClient(
                containerName);

        await containerClient.CreateIfNotExistsAsync(
            cancellationToken: cancellationToken);

        BlobClient blobClient =
            containerClient.GetBlobClient(blobName);

        string json =
            JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        await blobClient.UploadAsync(
            BinaryData.FromString(json),
            overwrite: true,
            cancellationToken);
    }
}
