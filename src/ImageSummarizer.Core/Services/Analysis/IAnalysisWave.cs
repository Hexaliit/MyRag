using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SharedAnalysis = Mostlylucid.Summarizer.Core.Analysis;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis;

/// <summary>
///     Interface for pluggable image analysis components that contribute signals to a dynamic profile.
///     Each wave is an independent analyzer that produces signals about different aspects of an image.
///     Uses the shared Signal type from Summarizer.Core (extended with ValueType in the Image layer).
/// </summary>
public interface IAnalysisWave
{
    /// <summary>
    ///     Unique name identifying this analysis wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Priority for execution order. Higher priority waves run first.
    ///     Allows dependencies between waves (e.g., forensics wave may depend on color wave results).
    /// </summary>
    int Priority { get; }

    /// <summary>
    ///     Tags describing what category of analysis this wave provides.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    ///     Check if this wave should run based on cheap preconditions.
    ///     Expensive waves can override this to skip processing when preconditions aren't met.
    ///     Default implementation always returns true.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="context">Shared context with results from previously executed waves</param>
    /// <returns>True if the wave should execute, false to skip</returns>
    bool ShouldRun(string imagePath, AnalysisContext context)
    {
        return true;
    }

    /// <summary>
    ///     Analyze an image and produce signals.
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="context">Shared context with results from previously executed waves</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Collection of signals produced by this wave</returns>
    Task<IEnumerable<Signal>> AnalyzeAsync(string imagePath, AnalysisContext context, CancellationToken ct = default);
}

/// <summary>
///     Shared context passed between analysis waves.
///     Allows waves to access results from higher-priority waves.
///     Wraps the shared AnalysisContext from Summarizer.Core, adding image-specific routing.
/// </summary>
public class AnalysisContext
{
    public AnalysisContext()
    {
        Shared = new SharedAnalysis.AnalysisContext();
    }

    /// <summary>
    ///     Create a local context wrapping a shared context.
    /// </summary>
    public AnalysisContext(SharedAnalysis.AnalysisContext shared)
    {
        Shared = shared;
    }

    /// <summary>
    ///     Get the underlying shared context.
    /// </summary>
    public SharedAnalysis.AnalysisContext Shared { get; }

    /// <summary>
    ///     Check if we're in fast mode (skip expensive operations).
    /// </summary>
    public bool IsFastRoute => GetSelectedRoute() == "fast";

    /// <summary>
    ///     Check if we're in quality mode (run everything).
    /// </summary>
    public bool IsQualityRoute => GetSelectedRoute() == "quality";

    /// <summary>
    ///     Add a signal to the context.
    /// </summary>
    public void AddSignal(Signal signal)
    {
        Shared.AddSignal(signal);
    }

    /// <summary>
    ///     Add multiple signals to the context.
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals)
            Shared.AddSignal(signal);
    }

    /// <summary>
    ///     Get all signals for a given key.
    /// </summary>
    public IEnumerable<Signal> GetSignals(string key)
    {
        return Shared.GetSignals(key).OfType<Signal>();
    }

    /// <summary>
    ///     Get the most confident signal for a key.
    /// </summary>
    public Signal? GetBestSignal(string key)
    {
        return Shared.GetBestSignal(key) as Signal;
    }

    /// <summary>
    ///     Get value from the most confident signal.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        return Shared.GetValue<T>(key);
    }

    /// <summary>
    ///     Check if a signal exists for a key.
    /// </summary>
    public bool HasSignal(string key)
    {
        return Shared.HasSignal(key);
    }

    /// <summary>
    ///     Get all signals.
    /// </summary>
    public IEnumerable<Signal> GetAllSignals()
    {
        return Shared.GetAllSignals().OfType<Signal>();
    }

    /// <summary>
    ///     Cache arbitrary data for sharing between waves.
    /// </summary>
    public void SetCached<T>(string key, T value) where T : notnull
    {
        Shared.SetCached(key, value);
    }

    /// <summary>
    ///     Retrieve cached data.
    /// </summary>
    public T? GetCached<T>(string key)
    {
        return Shared.GetCached<T>(key);
    }

    /// <summary>
    ///     Clear all cached data (useful for freeing memory after analysis).
    /// </summary>
    public void ClearCache()
    {
        Shared.ClearCache();
    }

    /// <summary>
    ///     Check if a wave is skipped by auto-routing.
    ///     Waves should call this in ShouldRun() to respect routing decisions.
    /// </summary>
    public bool IsWaveSkippedByRouting(string waveName)
    {
        return GetValue<bool>($"route.skip.{waveName}");
    }

    /// <summary>
    ///     Get the selected route (fast/balanced/quality).
    /// </summary>
    public string? GetSelectedRoute()
    {
        return GetValue<string>("route.selected");
    }
}