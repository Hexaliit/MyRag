using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.Coordination;

/// <summary>
/// Per-document orchestrator. Runs waves in manifest-declared order,
/// checks signal dependencies, and accumulates signals.
/// </summary>
public interface ICoordinator
{
    /// <summary>
    /// Execute all applicable waves for the given context.
    /// </summary>
    /// <param name="context">Per-document context with signals and caches.</param>
    /// <param name="profile">Optional execution profile (fast/default/quality).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Coordinator result with all signals and execution log.</returns>
    Task<CoordinatorResult> ExecuteAsync(
        WaveContext context,
        CoordinatorProfile? profile = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result from a coordinator run.
/// </summary>
public record CoordinatorResult
{
    /// <summary>
    /// All signals produced during the run.
    /// </summary>
    public required IReadOnlyList<Signal> Signals { get; init; }

    /// <summary>
    /// Execution log for each wave (success, skip, error, timeout).
    /// </summary>
    public required IReadOnlyList<WaveExecutionLog> Log { get; init; }

    /// <summary>
    /// Total wall-clock duration of the coordinator run.
    /// </summary>
    public long TotalDurationMs { get; init; }
}
