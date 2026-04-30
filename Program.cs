using AIChat_IngestionFunctions;
using AIChat_IngestionFunctions.Classes;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using FFMpegCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights isn't enabled by default. See https://aka.ms/AAt8mw4.
//builder.Services
//    .AddApplicationInsightsTelemetryWorkerService()
//    .ConfigureFunctionsApplicationInsights();




//Services for BlobServiceClient and QueueServiceClient Triggers
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new BlobServiceClient(cfg["AzureWebJobsStorage"]);
});

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new QueueServiceClient(cfg["AzureWebJobsStorage"]);
});



//Setup Http
builder.Services.AddHttpClient();


builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new DocumentIntelligenceClient(
        new Uri(cfg["DocInt:Endpoint"]!),
        new Azure.AzureKeyCredential(cfg["DocInt:ApiKey"]!)
    );
});

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new AzureOpenAIClient(
        new Uri(cfg["AOAI:Endpoint"]!),
        new Azure.AzureKeyCredential(cfg["AOAI:ApiKey"]!)
    );
});

builder.Services.AddSingleton(sp =>
{

    var cfg = sp.GetRequiredService<IConfiguration>();
    return new SearchClient(
        new Uri(cfg["Search:Endpoint"]!),
        cfg["Search:IndexName"]!,
        new Azure.AzureKeyCredential(cfg["Search:ApiKey"]!)
    );
});


// Configure FFmpeg path here
string ffmpegFolder = Path.Combine(
    Environment.CurrentDirectory,
    "tools",
    "ffmpeg");
GlobalFFOptions.Configure(new FFOptions
{
    BinaryFolder = ffmpegFolder
});






builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<TextExtractor>();
builder.Services.AddSingleton<SearchIndexer>();
builder.Services.AddSingleton<FileSummarizer>();
builder.Services.AddSingleton<AudioTranscriptExtractor>();










builder.Build().Run();
