using DomainClassifier.Core.Extensions;
using DomainClassifier.Financial.Extensions;
using DomainClassifier.Narrative.Extensions;
using DomainClassifier.Technical.Extensions;
using DoomSummarizer.Services;
using LucidRAG.Data;
using LucidRAG.Services;
using LucidRAG.UltraResearch;
using LucidRAG.UltraResearch.Extensions;
using LucidResearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Extensions;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Extensions;
using Serilog;

namespace LucidResearch;

public static class ServiceRegistration
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Serilog file-only logging (TUI owns stdout/stderr)
        var serilogLogger = new LoggerConfiguration()
            .WriteTo.File(
                "logs/lucidresearch-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(serilogLogger, dispose: true);
        });

        // EF Core with SQLite
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? "Data Source=lucidresearch.db";
        services.AddDbContext<RagDocumentsDbContext>(options =>
            options.UseSqlite(connectionString));

        // DocSummarizer pipeline (chunk, embed, entity extract)
        // Use DuckDB for persistent vector storage so embeddings survive restarts
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "lucidrag");
        Directory.CreateDirectory(dataDir);

        services.AddDocSummarizer(opt =>
        {
            opt.EmbeddingBackend = EmbeddingBackend.Onnx;
            opt.Onnx.EmbeddingModel = OnnxEmbeddingModel.AllMiniLmL6V2;
            opt.BertRag.VectorStore = VectorStoreBackend.InMemory;
            opt.BertRag.CollectionName = "ragdocuments";
            opt.BertRag.ReindexOnStartup = false;
        });
        services.AddPipelineRegistry();

        // DoomSummarizer services needed by UltraResearch
        services.AddHttpClient<ArxivFetcher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", AcademicPatterns.AcademicUserAgent);
        });
        services.AddHttpClient<CitationResolver>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", AcademicPatterns.AcademicUserAgent);
        });
        services.AddScoped<ICitationResolver>(sp => sp.GetRequiredService<CitationResolver>());
        services.AddScoped<CitationGraphQueries>();

        // ResearchPaperFetcher with typed HttpClient (AddUltraResearch TryAddScoped will skip)
        services.AddHttpClient<ResearchPaperFetcher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", AcademicPatterns.AcademicUserAgent);
        });

        // UltraResearch orchestrator + services
        services.AddUltraResearch();

        // Domain classifiers
        services.AddDomainClassifierCore();
        services.AddDomainTechnical();
        services.AddDomainFinancial();
        services.AddDomainNarrative();
        services.AddDomainClassifierRegistry();

        // Entity matching for author dedup + graph entity normalization
        services.AddScoped<IEntityMatcher, EntityMatcher>();

        // App services — real ingester using DocSummarizer pipeline
        services.AddSingleton<IDocumentIngester, DocumentIngester>();
        services.AddSingleton<AppState>();
        services.AddSingleton<StatePoller>();
        services.AddSingleton<ResearchChatService>();
    }

    public static ServiceProvider BuildServiceProvider(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        ConfigureServices(services, configuration);

        return services.BuildServiceProvider();
    }
}
