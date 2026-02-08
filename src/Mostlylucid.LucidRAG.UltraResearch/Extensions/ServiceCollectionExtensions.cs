using DoomSummarizer.Services;
using LucidRAG.UltraResearch.Waves;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Extensions;

/// <summary>
///     DI registration for UltraResearch services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Register UltraResearch services: orchestrator, fetcher, frontier manager, sentinel.
    ///     Requires DoomSummarizer.Core services (ArxivFetcher, CitationResolver, OllamaService)
    ///     and LucidRAG.Core services (RagDocumentsDbContext, CitationGraphQueries) to be registered.
    /// </summary>
    public static IServiceCollection AddUltraResearch(
        this IServiceCollection services,
        Action<UltraResearchOptions>? configure = null)
    {
        var options = new UltraResearchOptions();
        configure?.Invoke(options);

        // SemanticScholarClient — singleton with its own HttpClient
        services.TryAddSingleton(sp =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.Add("User-Agent", AcademicPatterns.AcademicUserAgent);

            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                .CreateLogger<SemanticScholarClient>();

            var client = new SemanticScholarClient(httpClient, logger);

            if (!string.IsNullOrEmpty(options.SemanticScholarApiKey))
                client.SetApiKey(options.SemanticScholarApiKey);

            return client;
        });

        // Core services — scoped to match DbContext lifetime
        services.TryAddScoped<ResearchPaperFetcher>();
        services.TryAddScoped<ResearchFrontierManager>();
        services.TryAddScoped<ResearchSentinelEvaluator>();

        // Orchestrator — singleton (manages active sessions across requests)
        services.TryAddSingleton<UltraResearchOrchestrator>();

        // Wave-coordinated paper processing
        services.TryAddScoped<ITypedAnalysisWave<FetchCandidate>, MetadataFetchWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, ContentFetchWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, VenueResolutionWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, MarkdownSaveWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, CitationExtractionWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, IngestWave>();
        services.AddScoped<ITypedAnalysisWave<FetchCandidate>, AuthorPromotionWave>();
        services.TryAddScoped<PaperCoordinator>();

        return services;
    }
}

/// <summary>
///     Configuration options for UltraResearch services.
/// </summary>
public class UltraResearchOptions
{
    /// <summary>
    ///     Semantic Scholar API key for dedicated rate limit.
    ///     Set via appsettings.json or SEMANTIC_SCHOLAR_API_KEY env var.
    /// </summary>
    public string? SemanticScholarApiKey { get; set; }
}
