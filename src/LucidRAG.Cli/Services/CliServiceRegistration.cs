using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Extensions;
using LucidRAG.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Data.Extensions;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Extensions;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Extensions;
using Serilog;
using Serilog.Events;

namespace LucidRAG.Cli.Services;

/// <summary>
///     Service registration for CLI-specific DI container
///     Uses SQLite + in-memory vectors for zero-dependency local storage
/// </summary>
public static class CliServiceRegistration
{
    /// <summary>
    ///     Build a service provider configured for CLI usage with local storage
    /// </summary>
    public static ServiceProvider BuildServiceProvider(CliConfig config, bool verbose = false)
    {
        var services = new ServiceCollection();

        // Logging via Serilog - suppress DocSummarizer internal messages unless verbose
        var logLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;
        var docSummarizerLogLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Warning;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            // Suppress all DocSummarizer.* namespace logs unless verbose
            .MinimumLevel.Override("Mostlylucid.DocSummarizer", docSummarizerLogLevel)
            .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services", docSummarizerLogLevel)
            .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services.DocSummarizerInitializer", docSummarizerLogLevel)
            .MinimumLevel.Override("Mostlylucid.DocSummarizer.Services.Onnx", docSummarizerLogLevel)
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Database - SQLite for local storage
        var dbPath = Path.Combine(config.DataDirectory, "lucidrag.db");
        services.AddDbContext<RagDocumentsDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // DocSummarizer.Core with in-memory vector store
        services.AddDocSummarizer(opt =>
        {
            // Use ONNX for embeddings (no external service required)
            opt.EmbeddingBackend = EmbeddingBackend.Onnx;
            opt.Onnx.EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2;

            // Use in-memory for vector storage (CLI mode)
            opt.BertRag.VectorStore = VectorStoreBackend.InMemory;
            opt.BertRag.CollectionName = "ragdocuments";
            opt.BertRag.ReindexOnStartup = false;

            // Verbose output
            opt.Output.Verbose = verbose;

            // Configure Ollama if available
            if (!string.IsNullOrEmpty(config.OllamaUrl))
            {
                opt.Ollama.BaseUrl = config.OllamaUrl;
                opt.Ollama.Model = config.OllamaModel;
            }
        });

        // DocSummarizer.Images with advanced OCR pipeline
        services.AddDocSummarizerImages(opt =>
        {
            opt.ModelsDirectory = Path.Combine(config.DataDirectory, "models");
            opt.EnableOcr = true;
            opt.Ocr.UseAdvancedPipeline = true;
            opt.Ocr.QualityMode = OcrQualityMode.Fast;
            opt.Ocr.TextDetectionConfidenceThreshold = 0; // Always run OCR, don't skip based on text-likeliness
            opt.Ocr.ConfidenceThresholdForEarlyExit = 0.95;
            opt.Ocr.EnableStabilization = true;
            opt.Ocr.EnableTemporalMedian = true;
            opt.Ocr.EnableTemporalVoting = true;
            opt.Ocr.EnablePostCorrection = false;
            opt.Ocr.EnableSpellChecking = true;
            opt.Ocr.SpellCheckQualityThreshold = 0.5;
            opt.Ocr.SpellCheckLanguage = "en_US";
            opt.Ocr.MaxFramesForVoting = 5;
            opt.Ocr.EmitPerformanceMetrics = verbose;

            // OCR Benchmark configuration
            opt.Ocr.Benchmark.Enabled = config.EnableOcrBenchmark;
            opt.Ocr.Benchmark.ReportOutputPath = config.OcrBenchmarkOutputPath;
            opt.Ocr.Benchmark.AppendToReport = true;
            opt.Ocr.Benchmark.IncludeFullText = true;

            // When benchmarking, enable all OCR systems to compare
            if (config.EnableOcrBenchmark)
            {
                // DeepSeek OCR (Ollama - local)
                opt.Ocr.EnableDeepseekOcr = true;
                opt.Ocr.DeepseekOcrBaseUrl = "http://localhost:11434";
                opt.Ocr.DeepseekOcrModelName = "deepseek-ocr:latest";

                // Enable Vision LLM for comparison (using llava)
                opt.EnableVisionLlm = true;
                opt.VisionLlmModel = "llava:7b";
                opt.VisionLlmTimeout = 120000; // 2 minutes for large images
            }
        });

        // AudioSummarizer.Core - Forensic audio characterization
        services.AddAudioSummarizer(opt =>
        {
            opt.TranscriptionBackend = TranscriptionBackend.Whisper;
            opt.Whisper.ModelPath = Path.Combine(config.DataDirectory, "models", "whisper-base.en.bin");
            opt.Whisper.Language = "en";
            opt.SupportedFormats = new[] { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".wma", ".aac" };
            opt.Verbose = verbose;
            opt.FingerprintProvider = FingerprintProvider.PureNet;
            opt.Pipeline.EnableFingerprinting = true;
            opt.Pipeline.EnableAcousticProfiling = true;
            opt.Pipeline.EnableContentClassification = true;
            opt.Pipeline.EnableTranscription = true;
            opt.EnableVoiceEmbeddings = false; // Requires model download
            opt.EnableSpeakerDiarization = false; // Phase 5 (not yet implemented)
        });

        // DataSummarizer.Core for CSV, JSON, Excel, Parquet
        services.AddDataSummarizer(opt =>
        {
            opt.ChunkSize = 50;
            opt.ChunkOverlap = 5;
        });

        // CLI-specific services
        services.AddSingleton(config);
        services.AddSingleton<CliProgressRenderer>();
        services.AddScoped<CliDocumentProcessor>();

        // Pipeline registry - discovers all registered pipelines
        services.AddPipelineRegistry();

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Ensure database is created and up to date
    /// </summary>
    public static async Task EnsureDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}

/// <summary>
///     CLI configuration
/// </summary>
public class CliConfig
{
    public string DataDirectory { get; set; } = Program.GetDefaultDataDirectory();
    public string? OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2:3b";
    public bool Verbose { get; set; }

    /// <summary>
    /// Enable OCR benchmarking to compare all OCR systems.
    /// </summary>
    public bool EnableOcrBenchmark { get; set; }

    /// <summary>
    /// Output path for OCR benchmark report.
    /// </summary>
    public string OcrBenchmarkOutputPath { get; set; } = "./OCR Test.md";
}