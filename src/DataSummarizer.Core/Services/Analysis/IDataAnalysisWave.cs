using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis;

/// <summary>
/// Interface for pluggable data analysis components that contribute signals to a dynamic profile.
/// Each wave is an independent analyzer that produces signals about different aspects of data.
/// </summary>
public interface IDataAnalysisWave
{
    /// <summary>
    /// Unique name identifying this analysis wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority for execution order. Higher priority waves run first.
    /// Allows dependencies between waves (e.g., StatisticsWave may depend on TypeInferenceWave results).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Tags describing what category of analysis this wave provides.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Check if this wave should run based on cheap preconditions.
    /// Expensive waves can override this to skip processing when preconditions aren't met.
    /// Default implementation always returns true.
    /// </summary>
    /// <param name="file">The data file being analyzed</param>
    /// <param name="context">Shared context with results from previously executed waves</param>
    /// <returns>True if the wave should execute, false to skip</returns>
    bool ShouldRun(DataFile file, DataAnalysisContext context) => true;

    /// <summary>
    /// Analyze a data file and produce signals.
    /// </summary>
    /// <param name="file">The data file to analyze</param>
    /// <param name="context">Shared context with results from previously executed waves</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of signals produced by this wave</returns>
    Task<IEnumerable<DataSignal>> AnalyzeAsync(DataFile file, DataAnalysisContext context, CancellationToken ct = default);
}
