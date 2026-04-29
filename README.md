
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

public class AudioTranscriptionFunction
{
    private readonly SpeechTranscriptionService _speechService;
    private readonly ILogger<AudioTranscriptionFunction> _logger;

    public AudioTranscriptionFunction(
        SpeechTranscriptionService speechService,
        ILogger<AudioTranscriptionFunction> logger)
    {
        _speechService = speechService;
        _logger = logger;
    }

    [Function("ProcessInterviewAudio")]
    public async Task Run(
        [BlobTrigger("aichat-input/{caseId}/{fileName}", Connection = "AzureWebJobsStorage")]
        Stream inputStream,
        string caseId,
        string fileName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing audio file: {FileName}", fileName);

        string fileId = Path.GetFileNameWithoutExtension(fileName);

        // You said this part is already done.
        // So here we assume you already have this path:
        string wavFilePath = Path.Combine(Path.GetTempPath(), $"{fileId}.wav");

        /*
            Your existing conversion code should create:
            wavFilePath
        */

        SpeechConversationResult result =
            await _speechService.ProcessWavFileAsync(
                wavFilePath,
                caseId,
                fileId,
                cancellationToken);

        _logger.LogInformation(
            "Transcription completed. Segments found: {Count}",
            result.Segments.Count);
    }
}
