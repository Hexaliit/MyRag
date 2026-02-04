using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Waves;

namespace VideoSummarizer.Core.Coordination;

/// <summary>
///     Coordinates wave execution based on signal dependencies.
///     Unlike the static VideoWaveCoordinator, this dynamically determines
///     which waves can run based on available signals.
/// </summary>
public sealed class SignalAwareWaveCoordinator : IDisposable
{
    private readonly HashSet<string> _availableSignals = new();
    private readonly List<(string Wave, TimeSpan Duration, int SignalsEmitted)> _executionLog = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _laneSemaphores = new();
    private readonly ILogger<SignalAwareWaveCoordinator> _logger;
    private readonly IEnumerable<IVideoWave> _waves;

    public SignalAwareWaveCoordinator(
        IEnumerable<IVideoWave> waves,
        ILogger<SignalAwareWaveCoordinator> logger)
    {
        _waves = waves.OrderByDescending(w => w.Priority).ToList();
        _logger = logger;
    }

    /// <summary>
    ///     Maximum concurrent waves in the default lane.
    /// </summary>
    public int DefaultLaneConcurrency { get; set; } = 1;

    /// <summary>
    ///     Lane concurrency limits by lane name.
    /// </summary>
    public Dictionary<string, int> LaneConcurrency { get; } = new()
    {
        ["gpu"] = 1, // GPU operations are sequential
        ["cpu"] = 4, // CPU-bound work can parallelize
        ["io"] = 8, // IO-bound work can highly parallelize
        ["default"] = 1 // Default is sequential for safety
    };

    public void Dispose()
    {
        foreach (var semaphore in _laneSemaphores.Values) semaphore.Dispose();
        _laneSemaphores.Clear();
    }

    /// <summary>
    ///     Event raised when a signal is emitted.
    ///     Enables reactive composition patterns.
    /// </summary>
    public event Action<VideoSignalEvent>? OnSignalEmitted;

    /// <summary>
    ///     Execute all waves respecting signal dependencies.
    ///     Waves only run when their required signals are available.
    /// </summary>
    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var orderedWaves = _waves.ToList();
        _logger.LogInformation("Starting signal-aware video analysis with {WaveCount} waves", orderedWaves.Count);

        // Track which waves have been executed
        var executedWaves = new HashSet<string>();
        var failedWaves = new HashSet<string>();
        var maxIterations = orderedWaves.Count * 2; // Safety limit
        var iteration = 0;

        while (executedWaves.Count + failedWaves.Count < orderedWaves.Count && iteration++ < maxIterations)
        {
            ct.ThrowIfCancellationRequested();

            // Find waves that can run based on current signals
            var runnableWaves = orderedWaves
                .Where(w => !executedWaves.Contains(w.Name) && !failedWaves.Contains(w.Name))
                .Where(w => CanRun(w, context))
                .OrderByDescending(w => w.Priority)
                .ToList();

            if (runnableWaves.Count == 0)
            {
                // No more waves can run - check if we're stuck
                var pendingWaves = orderedWaves
                    .Where(w => !executedWaves.Contains(w.Name) && !failedWaves.Contains(w.Name))
                    .ToList();

                if (pendingWaves.Count > 0)
                {
                    _logger.LogWarning(
                        "No waves can run. Pending: {Pending}. Available signals: {Signals}",
                        string.Join(", ", pendingWaves.Select(w => w.Name)),
                        string.Join(", ", _availableSignals.Take(10)));

                    // Mark remaining waves as skipped
                    foreach (var wave in pendingWaves)
                    {
                        failedWaves.Add(wave.Name);
                        EmitSignal($"wave.skipped.{wave.Name}", "dependencies_not_met", wave.Name, context);
                    }
                }

                break;
            }

            // Execute runnable waves (potentially in parallel based on lanes)
            var waveTasks = new List<Task>();
            var wavesByLane = runnableWaves.GroupBy(GetWaveLane);

            foreach (var laneGroup in wavesByLane)
            {
                var lane = laneGroup.Key;
                var semaphore = GetLaneSemaphore(lane);

                foreach (var wave in laneGroup)
                    waveTasks.Add(ExecuteWaveAsync(wave, context, semaphore, executedWaves, failedWaves, ct));
            }

            await Task.WhenAll(waveTasks);

            // Report progress
            var progress = (double)(executedWaves.Count + failedWaves.Count) / orderedWaves.Count * 100;
            context.ReportProgress("Processing waves", progress);
        }

        LogExecutionSummary(context);
    }

    /// <summary>
    ///     Check if a wave can run based on signal dependencies.
    /// </summary>
    private bool CanRun(IVideoWave wave, VideoContext context)
    {
        // First check ShouldRun (cheap preconditions)
        if (!wave.ShouldRun(context))
        {
            _logger.LogDebug("Wave {Wave} preconditions not met", wave.Name);
            return false;
        }

        // If wave implements ISignalAwareVideoWave, check signal dependencies
        if (wave is ISignalAwareVideoWave signalAware)
            foreach (var required in signalAware.RequiredSignals)
                if (!_availableSignals.Contains(required) && !context.HasSignal(required))
                {
                    _logger.LogDebug("Wave {Wave} missing required signal: {Signal}", wave.Name, required);
                    return false;
                }

        return true;
    }

    /// <summary>
    ///     Execute a single wave with proper signal emission.
    /// </summary>
    private async Task ExecuteWaveAsync(
        IVideoWave wave,
        VideoContext context,
        SemaphoreSlim semaphore,
        HashSet<string> executedWaves,
        HashSet<string> failedWaves,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Running wave: {WaveName} (priority: {Priority})", wave.Name, wave.Priority);

            // Emit start signal
            EmitSignal($"wave.started.{wave.Name}", true, wave.Name, context);

            var sw = Stopwatch.StartNew();
            var signalCountBefore = context.GetAllSignals().Count();

            try
            {
                await wave.ProcessAsync(context, ct);

                sw.Stop();
                var signalsEmitted = context.GetAllSignals().Count() - signalCountBefore;

                // Track available signals from this wave
                if (wave is ISignalAwareVideoWave signalAware)
                    foreach (var signal in signalAware.EmittedSignals)
                        _availableSignals.Add(signal);

                // Also track signals that were actually added to context
                foreach (var signal in context.GetAllSignals().Skip(signalCountBefore))
                    _availableSignals.Add(signal.Key);

                // Emit completion signal
                EmitSignal($"wave.completed.{wave.Name}", sw.ElapsedMilliseconds, wave.Name, context);

                _executionLog.Add((wave.Name, sw.Elapsed, signalsEmitted));
                executedWaves.Add(wave.Name);

                _logger.LogInformation("Wave {WaveName} completed in {ElapsedMs}ms, emitted {Signals} signals",
                    wave.Name, sw.ElapsedMilliseconds, signalsEmitted);

                // Check for escalation
                await CheckEscalationAsync(wave, context, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Wave {WaveName} was cancelled", wave.Name);
                failedWaves.Add(wave.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wave {WaveName} failed: {Message}", wave.Name, ex.Message);

                // Emit failure signal
                EmitSignal($"wave.failed.{wave.Name}", ex.Message, wave.Name, context);

                context.AddSignal(new VideoSignal
                {
                    Key = $"error.{wave.Name}",
                    Value = ex.Message,
                    Source = wave.Name,
                    Confidence = 1.0,
                    Tags = ["error"]
                });

                failedWaves.Add(wave.Name);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    ///     Check if escalation is needed based on wave results.
    /// </summary>
    private async Task CheckEscalationAsync(IVideoWave wave, VideoContext context, CancellationToken ct)
    {
        // Check common escalation patterns

        // OCR escalation: if OCR confidence is low, escalate to vision LLM
        if (wave.Name == "keyframe_ocr" || wave.Name == "keyframe_analysis")
        {
            var ocrConfidence = context.GetValue<double?>("ocr.avg_confidence");
            if (ocrConfidence.HasValue && ocrConfidence.Value < 0.5)
            {
                _logger.LogInformation("Low OCR confidence ({Confidence:F2}) - escalating to vision LLM",
                    ocrConfidence.Value);
                EmitSignal(VideoSignals.EscalationVisionLlm, true, wave.Name, context);
            }
        }

        // Embedding escalation: if CLIP embeddings are missing, escalate
        if (wave.Name == "clip_embedding")
        {
            var embeddingCount = context.KeyframeEmbeddings.Count;
            var keyframeCount = context.Keyframes.Count;

            if (keyframeCount > 0 && embeddingCount < keyframeCount * 0.5)
            {
                _logger.LogWarning("Only {Embeddings}/{Keyframes} embeddings generated - escalating", embeddingCount,
                    keyframeCount);
                EmitSignal(VideoSignals.EscalationRequired, "embeddings_incomplete", wave.Name, context);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Get the execution lane for a wave.
    /// </summary>
    private string GetWaveLane(IVideoWave wave)
    {
        // Waves that use GPU should be in GPU lane
        if (wave.Name.Contains("clip") || wave.Name.Contains("embedding") || wave.Name.Contains("vision"))
            return "gpu";

        // Waves that are IO-bound
        if (wave.Name.Contains("extract") || wave.Name.Contains("ffmpeg"))
            return "io";

        // CPU-bound analysis waves
        if (wave.Name.Contains("detection") || wave.Name.Contains("clustering"))
            return "cpu";

        return "default";
    }

    private SemaphoreSlim GetLaneSemaphore(string lane)
    {
        return _laneSemaphores.GetOrAdd(lane, _ =>
        {
            var concurrency = LaneConcurrency.GetValueOrDefault(lane, DefaultLaneConcurrency);
            return new SemaphoreSlim(concurrency, concurrency);
        });
    }

    private void EmitSignal(string key, object? value, string source, VideoContext context)
    {
        var signal = new VideoSignal
        {
            Key = key,
            Value = value,
            Source = source,
            Confidence = 1.0,
            Tags = ["coordinator"]
        };

        context.AddSignal(signal);
        _availableSignals.Add(key);

        OnSignalEmitted?.Invoke(new VideoSignalEvent(
            key,
            value,
            source,
            1.0,
            DateTimeOffset.UtcNow
        ));
    }

    private void LogExecutionSummary(VideoContext context)
    {
        _logger.LogInformation(
            "Video analysis completed. Shots: {Shots}, Scenes: {Scenes}, TextTracks: {TextTracks}, Utterances: {Utterances}, Evidence: {Evidence}",
            context.Shots.Count,
            context.Scenes.Count,
            context.TextTracks.Count,
            context.Utterances.Count,
            context.Evidence.Count);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var summary = string.Join(", ", _executionLog.Select(e => $"{e.Wave}:{e.Duration.TotalMilliseconds:F0}ms"));
            _logger.LogDebug("Wave execution: {Summary}", summary);
        }
    }

    /// <summary>
    ///     Get waves that would run given current signals.
    /// </summary>
    public IReadOnlyList<string> GetRunnableWaves(VideoContext context)
    {
        return _waves
            .Where(w => CanRun(w, context))
            .Select(w => w.Name)
            .ToList();
    }

    /// <summary>
    ///     Get the dependency graph for visualization.
    /// </summary>
    public Dictionary<string, List<string>> GetDependencyGraph()
    {
        var graph = new Dictionary<string, List<string>>();

        foreach (var wave in _waves)
            if (wave is ISignalAwareVideoWave signalAware)
                graph[wave.Name] = signalAware.RequiredSignals.ToList();
            else
                graph[wave.Name] = new List<string>();

        return graph;
    }
}