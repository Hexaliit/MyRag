using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
/// Coordinates wave execution with priority ordering, concurrency lanes, and signal aggregation.
/// Each lane (fast/io/ml/llm) has its own SemaphoreSlim to prevent resource contention.
/// Waves execute in priority order (highest first) within their lane constraints.
/// </summary>
public class WaveCoordinator
{
    private readonly List<IAnalysisWave> _waves = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lanes = new();

    /// <summary>
    /// Register a wave for coordination.
    /// </summary>
    public void RegisterWave(IAnalysisWave wave)
    {
        _waves.Add(wave);
    }

    /// <summary>
    /// Register multiple waves.
    /// </summary>
    public void RegisterWaves(IEnumerable<IAnalysisWave> waves)
    {
        foreach (var wave in waves)
            RegisterWave(wave);
    }

    /// <summary>
    /// Execute all registered typed waves for in-memory content.
    /// </summary>
    public async Task<CoordinatorResult> ExecuteAsync<T>(
        T content,
        AnalysisContext? context = null,
        CoordinatorProfile? profile = null,
        CancellationToken ct = default)
    {
        context ??= new AnalysisContext();
        profile ??= CoordinatorProfile.Default;

        var sw = Stopwatch.StartNew();
        var signals = new ConcurrentBag<Signal>();
        var executionLog = new ConcurrentBag<WaveExecutionLog>();

        // Get typed waves sorted by priority (highest first)
        var orderedWaves = _waves
            .OfType<ITypedAnalysisWave<T>>()
            .Where(w => w.Enabled)
            .OrderByDescending(w => w.Priority)
            .ToList();

        // Group waves by lane for parallel execution within lanes
        var laneGroups = orderedWaves
            .GroupBy(w => GetLaneForWave(w, profile))
            .ToList();

        // Execute lane groups — waves within same priority can run in parallel across lanes
        // Within a lane, execute sequentially by priority
        var laneTasks = laneGroups.Select(async laneGroup =>
        {
            var lane = laneGroup.Key;
            var semaphore = GetLaneSemaphore(lane);

            foreach (var wave in laneGroup)
            {
                if (ct.IsCancellationRequested) break;

                // Check preconditions
                if (!wave.ShouldRun(content, context))
                {
                    executionLog.Add(new WaveExecutionLog
                    {
                        WaveName = wave.Name,
                        Status = WaveStatus.Skipped,
                        Reason = "ShouldRun returned false"
                    });
                    continue;
                }

                if (context.IsWaveSkipped(wave.Name))
                {
                    executionLog.Add(new WaveExecutionLog
                    {
                        WaveName = wave.Name,
                        Status = WaveStatus.Skipped,
                        Reason = "Skipped by routing"
                    });
                    continue;
                }

                try
                {
                    await semaphore.WaitAsync(ct);
                    var waveStart = Stopwatch.StartNew();

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(profile.WaveTimeoutMs);

                        var waveSignals = await wave.AnalyzeAsync(content, context, cts.Token);
                        var signalList = waveSignals.ToList();

                        context.AddSignals(signalList);
                        foreach (var signal in signalList)
                            signals.Add(signal);

                        executionLog.Add(new WaveExecutionLog
                        {
                            WaveName = wave.Name,
                            Status = WaveStatus.Success,
                            DurationMs = waveStart.ElapsedMilliseconds,
                            SignalCount = signalList.Count,
                            Lane = lane.Name
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        executionLog.Add(new WaveExecutionLog
                        {
                            WaveName = wave.Name,
                            Status = WaveStatus.Timeout,
                            DurationMs = waveStart.ElapsedMilliseconds,
                            Lane = lane.Name
                        });
                    }
                    catch (Exception ex)
                    {
                        executionLog.Add(new WaveExecutionLog
                        {
                            WaveName = wave.Name,
                            Status = WaveStatus.Error,
                            Error = ex.Message,
                            DurationMs = waveStart.ElapsedMilliseconds,
                            Lane = lane.Name
                        });

                        signals.Add(new Signal
                        {
                            Key = $"{wave.Name.ToLowerInvariant()}.error",
                            Value = ex.Message,
                            Confidence = 1.0,
                            Source = wave.Name,
                            Tags = ["error"]
                        });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        await Task.WhenAll(laneTasks);
        sw.Stop();

        return new CoordinatorResult
        {
            Context = context,
            Signals = signals.ToList(),
            ExecutionLog = executionLog.OrderBy(e => e.DurationMs).ToList(),
            TotalDurationMs = sw.ElapsedMilliseconds,
            Profile = profile.Name
        };
    }

    private LaneConfig GetLaneForWave(IAnalysisWave wave, CoordinatorProfile profile)
    {
        if (wave.Tags.Contains("llm"))
            return profile.Lanes.GetValueOrDefault("llm", LaneConfig.Llm);
        if (wave.Tags.Contains("ml"))
            return profile.Lanes.GetValueOrDefault("ml", LaneConfig.Ml);
        if (wave.Tags.Contains("io"))
            return profile.Lanes.GetValueOrDefault("io", LaneConfig.Io);

        return profile.Lanes.GetValueOrDefault("fast", LaneConfig.Fast);
    }

    private SemaphoreSlim GetLaneSemaphore(LaneConfig lane)
    {
        return _lanes.GetOrAdd(lane.Name, _ => new SemaphoreSlim(lane.MaxConcurrency, lane.MaxConcurrency));
    }
}

/// <summary>
/// Result from coordinator execution.
/// </summary>
public record CoordinatorResult
{
    public AnalysisContext Context { get; init; } = new();
    public IReadOnlyList<Signal> Signals { get; init; } = [];
    public IReadOnlyList<WaveExecutionLog> ExecutionLog { get; init; } = [];
    public long TotalDurationMs { get; init; }
    public string Profile { get; init; } = "default";
}

/// <summary>
/// Execution status for a wave.
/// </summary>
public enum WaveStatus
{
    Success,
    Skipped,
    Timeout,
    Error
}

/// <summary>
/// Execution log entry for a single wave.
/// </summary>
public record WaveExecutionLog
{
    public required string WaveName { get; init; }
    public required WaveStatus Status { get; init; }
    public long DurationMs { get; init; }
    public int SignalCount { get; init; }
    public string? Error { get; init; }
    public string? Reason { get; init; }
    public string? Lane { get; init; }
}

/// <summary>
/// Lane configuration for concurrency control.
/// </summary>
public sealed class LaneConfig
{
    public string Name { get; init; } = "fast";
    public int MaxConcurrency { get; init; } = 4;

    public static LaneConfig Fast => new() { Name = "fast", MaxConcurrency = 8 };
    public static LaneConfig Io => new() { Name = "io", MaxConcurrency = 4 };
    public static LaneConfig Ml => new() { Name = "ml", MaxConcurrency = 2 };
    public static LaneConfig Llm => new() { Name = "llm", MaxConcurrency = 1 };

    public override int GetHashCode() => Name.GetHashCode();
    public override bool Equals(object? obj) => obj is LaneConfig lc && lc.Name == Name;
}

/// <summary>
/// Profile for coordinator execution.
/// Controls concurrency limits, timeouts, and wave selection.
/// </summary>
public class CoordinatorProfile
{
    public string Name { get; init; } = "default";
    public int WaveTimeoutMs { get; init; } = 30000;
    public Dictionary<string, LaneConfig> Lanes { get; init; } = new();

    public static CoordinatorProfile Default => new()
    {
        Name = "default",
        WaveTimeoutMs = 30000,
        Lanes = new()
        {
            ["fast"] = LaneConfig.Fast,
            ["io"] = LaneConfig.Io,
            ["ml"] = LaneConfig.Ml,
            ["llm"] = LaneConfig.Llm
        }
    };

    /// <summary>
    /// Fast profile — skip expensive ML/LLM operations, high parallelism.
    /// </summary>
    public static CoordinatorProfile Fast => new()
    {
        Name = "fast",
        WaveTimeoutMs = 5000,
        Lanes = new()
        {
            ["fast"] = new() { Name = "fast", MaxConcurrency = 16 },
            ["io"] = new() { Name = "io", MaxConcurrency = 8 }
        }
    };

    /// <summary>
    /// Quality profile — run all waves including LLM, lower parallelism.
    /// </summary>
    public static CoordinatorProfile Quality => new()
    {
        Name = "quality",
        WaveTimeoutMs = 60000,
        Lanes = new()
        {
            ["fast"] = new() { Name = "fast", MaxConcurrency = 4 },
            ["io"] = new() { Name = "io", MaxConcurrency = 2 },
            ["ml"] = new() { Name = "ml", MaxConcurrency = 2 },
            ["llm"] = new() { Name = "llm", MaxConcurrency = 1 }
        }
    };
}
