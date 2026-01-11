using Microsoft.Extensions.Logging;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Coordinates execution of video analysis waves in priority order.
/// </summary>
public class VideoWaveCoordinator
{
    private readonly IEnumerable<IVideoWave> _waves;
    private readonly ILogger<VideoWaveCoordinator> _logger;

    public VideoWaveCoordinator(
        IEnumerable<IVideoWave> waves,
        ILogger<VideoWaveCoordinator> logger)
    {
        _waves = waves.OrderByDescending(w => w.Priority).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Execute all waves in priority order.
    /// </summary>
    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var orderedWaves = _waves.ToList();
        var totalWaves = orderedWaves.Count;
        var completedWaves = 0;

        _logger.LogInformation("Starting video analysis with {WaveCount} waves", totalWaves);

        foreach (var wave in orderedWaves)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!wave.ShouldRun(context))
                {
                    _logger.LogDebug("Skipping wave {WaveName} (preconditions not met)", wave.Name);
                    completedWaves++;
                    continue;
                }

                _logger.LogInformation("Running wave: {WaveName} (priority: {Priority})",
                    wave.Name, wave.Priority);

                var sw = System.Diagnostics.Stopwatch.StartNew();

                await wave.ProcessAsync(context, ct);

                sw.Stop();
                completedWaves++;

                var progress = (double)completedWaves / totalWaves * 100;
                context.ReportProgress(wave.Name, progress);

                _logger.LogInformation("Wave {WaveName} completed in {ElapsedMs}ms",
                    wave.Name, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Wave {WaveName} was cancelled", wave.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wave {WaveName} failed: {Message}", wave.Name, ex.Message);

                // Add error signal
                context.AddSignal(new VideoSignal
                {
                    Key = $"error.{wave.Name}",
                    Value = ex.Message,
                    Source = wave.Name,
                    Confidence = 1.0,
                    Tags = ["error"]
                });

                // Continue with next wave (fail gracefully)
            }
        }

        _logger.LogInformation("Video analysis completed. Shots: {Shots}, Scenes: {Scenes}, TextTracks: {TextTracks}, Utterances: {Utterances}",
            context.Shots.Count,
            context.Scenes.Count,
            context.TextTracks.Count,
            context.Utterances.Count);
    }

    /// <summary>
    /// Execute a specific subset of waves by tag.
    /// </summary>
    public async Task ProcessByTagAsync(VideoContext context, string tag, CancellationToken ct = default)
    {
        var taggedWaves = _waves
            .Where(w => w.Tags.Contains(tag))
            .OrderByDescending(w => w.Priority)
            .ToList();

        _logger.LogInformation("Running {Count} waves with tag '{Tag}'", taggedWaves.Count, tag);

        foreach (var wave in taggedWaves)
        {
            ct.ThrowIfCancellationRequested();

            if (!wave.ShouldRun(context)) continue;

            await wave.ProcessAsync(context, ct);
        }
    }

    /// <summary>
    /// Execute waves up to a specific phase (for incremental processing).
    /// </summary>
    public async Task ProcessToPhaseAsync(VideoContext context, VideoPhase phase, CancellationToken ct = default)
    {
        var minPriority = phase switch
        {
            VideoPhase.Normalize => 900,
            VideoPhase.ShotDetection => 800,
            VideoPhase.KeyframeExtraction => 700,
            VideoPhase.OcrExtraction => 600,
            VideoPhase.Transcription => 500,
            VideoPhase.SceneClustering => 400,
            VideoPhase.Evidence => 300,
            _ => 0
        };

        var phasedWaves = _waves
            .Where(w => w.Priority >= minPriority)
            .OrderByDescending(w => w.Priority)
            .ToList();

        _logger.LogInformation("Running waves to phase {Phase} ({Count} waves)", phase, phasedWaves.Count);

        foreach (var wave in phasedWaves)
        {
            ct.ThrowIfCancellationRequested();

            if (!wave.ShouldRun(context)) continue;

            await wave.ProcessAsync(context, ct);
        }
    }

    /// <summary>
    /// Get list of registered waves.
    /// </summary>
    public IReadOnlyList<(string Name, int Priority, IReadOnlyList<string> Tags)> GetWaveInfo() =>
        _waves.Select(w => (w.Name, w.Priority, w.Tags)).ToList();
}

/// <summary>
/// Processing phases for incremental video analysis.
/// </summary>
public enum VideoPhase
{
    /// <summary>Demux, metadata extraction (priority 900+)</summary>
    Normalize,

    /// <summary>Shot boundary detection (priority 800+)</summary>
    ShotDetection,

    /// <summary>Keyframe extraction and embedding (priority 700+)</summary>
    KeyframeExtraction,

    /// <summary>OCR text tracking (priority 600+)</summary>
    OcrExtraction,

    /// <summary>Speech transcription (priority 500+)</summary>
    Transcription,

    /// <summary>Scene clustering (priority 400+)</summary>
    SceneClustering,

    /// <summary>Evidence generation (priority 300+)</summary>
    Evidence,

    /// <summary>All phases</summary>
    Complete
}
