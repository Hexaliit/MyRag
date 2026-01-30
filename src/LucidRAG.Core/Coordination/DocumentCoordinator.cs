using System.Diagnostics;
using LucidRAG.Manifests;
using LucidRAG.Services.Waves;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.Coordination;

/// <summary>
/// Default coordinator implementation.
/// Orders waves by manifest priority, checks signal dependencies, executes, accumulates signals.
/// Falls back to IAnalysisWave properties when no YAML manifest exists.
/// </summary>
public sealed class DocumentCoordinator : ICoordinator
{
    private readonly IEnumerable<IWave> _waves;
    private readonly IWaveRegistry _registry;
    private readonly ILogger<DocumentCoordinator> _logger;

    public DocumentCoordinator(
        IEnumerable<IWave> waves,
        IWaveRegistry registry,
        ILogger<DocumentCoordinator> logger)
    {
        _waves = waves;
        _registry = registry;
        _logger = logger;
    }

    public async Task<CoordinatorResult> ExecuteAsync(
        WaveContext context,
        CoordinatorProfile? profile = null,
        CancellationToken ct = default)
    {
        profile ??= CoordinatorProfile.Default;
        var sw = Stopwatch.StartNew();
        var log = new List<WaveExecutionLog>();

        // Build ordered wave list with manifest data
        var orderedWaves = _waves
            .Select(w => (Wave: w, Manifest: ResolveManifest(w)))
            .Where(wm => wm.Manifest.Enabled)
            .OrderByDescending(wm => wm.Manifest.Priority)
            .ToList();

        using var lanes = new LaneManager(profile);

        foreach (var (wave, manifest) in orderedWaves)
        {
            if (ct.IsCancellationRequested) break;

            if (!MatchesDomain(manifest, context))
            {
                log.Add(MakeLog(wave.Name, WaveStatus.Skipped, reason: "domain mismatch"));
                continue;
            }

            if (!AreTriggersMet(manifest, context.Signals))
            {
                log.Add(MakeLog(wave.Name, WaveStatus.Skipped, reason: "triggers not met"));
                continue;
            }

            if (ShouldSkip(manifest, context.Signals))
            {
                log.Add(MakeLog(wave.Name, WaveStatus.Skipped, reason: "skip condition met"));
                continue;
            }

            var laneName = manifest.Lane?.Name ?? "fast";

            await lanes.AcquireAsync(laneName, ct);
            try
            {
                var waveStart = Stopwatch.StartNew();

                EmitLifecycleSignals(wave, context, manifest.Emits?.OnStart);
                context.Progress?.Invoke($"Running {wave.Name}...");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutMs = manifest.Defaults?.Timing?.GetValueOrDefault("timeout_ms");
                cts.CancelAfter(timeoutMs is int t ? t : profile.WaveTimeoutMs);

                var signals = await wave.ExecuteAsync(context, cts.Token);
                context.Signals.AddRange(signals);

                log.Add(MakeLog(wave.Name, WaveStatus.Success,
                    durationMs: waveStart.ElapsedMilliseconds,
                    signalCount: signals.Count,
                    lane: laneName));

                _logger.LogDebug(
                    "Wave {Wave} completed in {Duration}ms, produced {Signals} signals",
                    wave.Name, waveStart.ElapsedMilliseconds, signals.Count);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.Add(MakeLog(wave.Name, WaveStatus.Timeout, lane: laneName));
                _logger.LogWarning("Wave {Wave} timed out", wave.Name);
            }
            catch (Exception ex)
            {
                var failureKey = manifest.Emits?.OnFailure?.FirstOrDefault()
                                 ?? $"{wave.Name.ToLowerInvariant()}.failed";
                context.Signals.Add(new Signal
                {
                    Key = failureKey,
                    Value = ex.Message,
                    Confidence = 0,
                    Source = wave.Name,
                    Tags = ["error"]
                });

                log.Add(MakeLog(wave.Name, WaveStatus.Error, error: ex.Message, lane: laneName));
                _logger.LogError(ex, "Wave {Wave} failed", wave.Name);
            }
            finally
            {
                lanes.Release(laneName);
            }
        }

        sw.Stop();

        return new CoordinatorResult
        {
            Signals = context.Signals.All,
            Log = log,
            TotalDurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Resolve manifest from registry. Falls back to IAnalysisWave properties if no YAML manifest.
    /// </summary>
    private WaveManifest ResolveManifest(IWave wave)
    {
        var manifest = _registry.GetWave(wave.Name);
        if (manifest is not null) return manifest;

        // Fallback: build manifest from IAnalysisWave properties
        if (wave is IAnalysisWave analysisWave)
        {
            return new WaveManifest
            {
                Name = analysisWave.Name,
                DisplayName = analysisWave.Name,
                Description = analysisWave.Description,
                Priority = analysisWave.Priority,
                Enabled = analysisWave.Enabled,
                Tags = analysisWave.Tags.ToList(),
                Kind = "analysis"
            };
        }

        // Minimal default
        return new WaveManifest
        {
            Name = wave.Name,
            DisplayName = wave.Name,
            Enabled = true,
            Kind = "analysis"
        };
    }

    private static bool MatchesDomain(WaveManifest manifest, WaveContext context)
    {
        var waveDomain = manifest.Domain;
        if (string.IsNullOrEmpty(waveDomain) || waveDomain == "any")
            return true;

        return context.Domain == "any" || waveDomain.Equals(context.Domain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreTriggersMet(WaveManifest manifest, SignalBag signals)
    {
        var triggers = manifest.Triggers;
        if (triggers?.Requires is not { Count: > 0 })
            return true;

        return triggers.Requires.All(req =>
        {
            if (!signals.Has(req.Signal)) return false;
            if (!string.IsNullOrEmpty(req.Condition))
                return EvaluateCondition(signals.Get(req.Signal), req.Condition);
            return true;
        });
    }

    private static bool ShouldSkip(WaveManifest manifest, SignalBag signals)
    {
        var skipWhen = manifest.Triggers?.SkipWhen;
        return skipWhen is { Count: > 0 } && skipWhen.Any(signals.Has);
    }

    private static bool EvaluateCondition(Signal? signal, string condition)
    {
        if (signal is null) return false;

        return condition switch
        {
            "!IsNullOrEmpty" => signal.Value is string s ? !string.IsNullOrEmpty(s) : signal.Value is not null,
            "== true" => signal.Value is true or "true",
            "== false" => signal.Value is false or "false",
            _ when condition.StartsWith("> ") && double.TryParse(condition[2..], out var t)
                => signal.Value is double d && d > t,
            _ when condition.StartsWith(">= ") && double.TryParse(condition[3..], out var t2)
                => signal.Value is double d2 && d2 >= t2,
            _ when condition.StartsWith("< ") && double.TryParse(condition[2..], out var t3)
                => signal.Value is double d3 && d3 < t3,
            _ => true
        };
    }

    private static void EmitLifecycleSignals(IWave wave, WaveContext context, List<string>? keys)
    {
        if (keys is not { Count: > 0 }) return;

        foreach (var key in keys)
            context.Signals.Add(new Signal { Key = key, Value = true, Source = wave.Name, Confidence = 1.0 });
    }

    private static WaveExecutionLog MakeLog(
        string waveName, WaveStatus status, long durationMs = 0,
        int signalCount = 0, string? error = null, string? reason = null, string? lane = null) => new()
    {
        WaveName = waveName, Status = status, DurationMs = durationMs,
        SignalCount = signalCount, Error = error, Reason = reason, Lane = lane
    };
}
