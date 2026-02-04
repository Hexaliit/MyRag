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
using VideoSummarizer.Core.Extensions;

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
            opt.Ocr.Benchmark.ForceRunAllSystems = config.EnableOcrBenchmark; // Force all systems when benchmarking

            // When benchmarking, enable all OCR systems to compare
            // Iteration 3: Using minicpm-v:8b (proven fast+accurate) for all VLM slots
            if (config.EnableOcrBenchmark)
            {
                // DeepSeek-OCR (real model!) - requires Ollama v0.13.0+
                // Pull with: ollama pull deepseek-ocr
                // Specialized for document OCR with Markdown output
                // Handles PDFs, tables, handwritten text, complex layouts
                opt.Ocr.EnableDeepseekOcr = true;
                opt.Ocr.DeepseekOcrBaseUrl = "http://localhost:11434";
                opt.Ocr.DeepseekOcrModelName = "deepseek-ocr";
                opt.Ocr.DeepseekOcrTimeoutSeconds = 180;

                // Vision LLM → minicpm-v:8b (fast, good for OCR)
                opt.EnableVisionLlm = true;
                opt.VisionLlmModel = "minicpm-v:8b";
                opt.VisionLlmTimeout = 120000;

                // Florence2 OCR (local ONNX model - auto-downloads)
                opt.EnableFlorence2 = true;

                // Nanonets-OCR-s (real model!) - on Ollama
                // Pull with: ollama pull benhaotang/Nanonets-OCR-s
                // 4B param model: LaTeX, tables, markdown output
                opt.Ocr.EnableNanonetsOcr = true;
                opt.Ocr.NanonetsOcrBaseUrl = "http://localhost:11434";
                opt.Ocr.NanonetsOcrModelName = "benhaotang/Nanonets-OCR-s";
                opt.Ocr.NanonetsOcrTimeoutSeconds = 180;

                // OlmOCR-2 (real model!) - 7B OCR specialist
                // Pull with: ollama pull richardyoung/olmocr2:7b-q8
                // 82.4 points on olmOCR-Bench, handles tables/charts/math
                opt.Ocr.EnableOlmOcr2 = true;
                opt.Ocr.OlmOcr2BaseUrl = "http://localhost:11434";
                opt.Ocr.OlmOcr2ModelName = "richardyoung/olmocr2:7b-q8";
                opt.Ocr.OlmOcr2TimeoutSeconds = 180;
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
            opt.EnableVoiceEmbeddings = true; // ECAPA-TDNN embeddings (auto-downloaded ~18MB)
            opt.EnableSpeakerDiarization = true; // Speaker ID via VAD + ECAPA-TDNN clustering
            opt.EnableSourceSeparation = config.EnableSourceSeparation; // Demucs for music (~210MB model)
        });

        // DataSummarizer.Core for CSV, JSON, Excel, Parquet
        services.AddDataSummarizer(opt =>
        {
            opt.ChunkSize = 50;
            opt.ChunkOverlap = 5;
        });

        // VideoSummarizer.Core for video files (mp4, mkv, avi, etc.)
        services.AddVideoSummarizer();

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
    ///     Enable OCR benchmarking to compare all OCR systems.
    /// </summary>
    public bool EnableOcrBenchmark { get; set; }

    /// <summary>
    ///     Output path for OCR benchmark report.
    /// </summary>
    public string OcrBenchmarkOutputPath { get; set; } = "./OCR Test.md";

    /// <summary>
    ///     Enable source separation (Demucs) for music files.
    ///     Extracts vocals, drums, bass, other stems. Requires ~210MB model download.
    /// </summary>
    public bool EnableSourceSeparation { get; set; }
}