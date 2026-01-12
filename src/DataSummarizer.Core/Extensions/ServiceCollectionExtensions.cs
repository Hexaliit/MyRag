using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.DocSummarizer.Data.Config;
using Mostlylucid.DocSummarizer.Data.Pipeline;
using Mostlylucid.DocSummarizer.Data.Services;
using Mostlylucid.DocSummarizer.Data.Services.Analysis;
using Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Data.Extensions;

/// <summary>
/// Extension methods for registering DataSummarizer.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add DataSummarizer.Core services for processing CSV, JSON, Excel, and Parquet files.
    /// </summary>
    public static IServiceCollection AddDataSummarizer(this IServiceCollection services)
    {
        return services.AddDataSummarizer(_ => { });
    }

    /// <summary>
    /// Add DataSummarizer.Core services with custom configuration.
    /// </summary>
    public static IServiceCollection AddDataSummarizer(
        this IServiceCollection services,
        Action<DataProcessorOptions> configure)
    {
        // Register configuration
        services.Configure(configure);

        // Core data processor service (legacy, still available)
        services.AddScoped<IDataProcessor, DataProcessorService>();

        // Database readers (SQLite + Access)
        services.TryAddSingleton<SqliteDatabaseReader>();
        services.TryAddSingleton<AccessDatabaseReader>();
        services.TryAddSingleton<CompositeDatabaseReader>();
        services.TryAddSingleton<IDatabaseReader>(sp => sp.GetRequiredService<CompositeDatabaseReader>());

        // DuckDB analyzer (singleton for connection pooling)
        services.TryAddSingleton<DuckDbAnalyzer>();

        // Register analysis waves (use AddSingleton for multiple IDataAnalysisWave implementations)
        // Priority order: higher priority waves run first
        services.AddSingleton<IDataAnalysisWave, IdentityWave>();       // P=110
        services.AddSingleton<IDataAnalysisWave, SamplingWave>();       // P=100
        services.AddSingleton<IDataAnalysisWave, TypeInferenceWave>();  // P=95
        services.AddSingleton<IDataAnalysisWave, ProfileWave>();        // P=90
        services.AddSingleton<IDataAnalysisWave, RelationshipWave>();   // P=85 - FK detection for databases
        services.AddSingleton<IDataAnalysisWave, QualityWave>();        // P=80
        services.AddSingleton<IDataAnalysisWave, StatisticsWave>();     // P=70
        services.AddSingleton<IDataAnalysisWave, OutlierWave>();        // P=60
        services.AddSingleton<IDataAnalysisWave, CorrelationWave>();    // P=50
        services.AddSingleton<IDataAnalysisWave, PiiDetectionWave>();   // P=40
        services.AddSingleton<IDataAnalysisWave, DataCharacterizationWave>(); // P=30

        // Analysis orchestrator
        services.TryAddSingleton<DataAnalysisOrchestrator>();

        // Register the pipeline for unified pipeline registry
        // Pipeline is scoped because it depends on scoped IDataProcessor
        services.AddScoped<DataPipeline>();
        services.AddScoped<IPipeline>(sp => sp.GetRequiredService<DataPipeline>());

        return services;
    }
}
