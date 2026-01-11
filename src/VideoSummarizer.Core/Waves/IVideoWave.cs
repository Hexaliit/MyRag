using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Interface for pluggable video analysis components.
/// Each wave processes the video and contributes to the shared context.
/// Waves are deterministic-first: expensive LLM operations are deferred or optional.
/// </summary>
public interface IVideoWave
{
    /// <summary>
    /// Unique name identifying this wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority for execution order. Higher priority waves run first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Tags describing what category of analysis this wave provides.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Check if this wave should run based on cheap preconditions.
    /// </summary>
    bool ShouldRun(VideoContext context) => true;

    /// <summary>
    /// Process the video and contribute results to the context.
    /// </summary>
    Task ProcessAsync(VideoContext context, CancellationToken ct = default);
}

/// <summary>
/// Shared context passed between video analysis waves.
/// Contains all extracted signals and intermediate results.
/// </summary>
public class VideoContext
{
    /// <summary>Path to the source video file</summary>
    public required string VideoPath { get; init; }

    /// <summary>Working directory for intermediate files</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Video metadata (populated by NormalizeWave)</summary>
    public VideoMetadata? Metadata { get; set; }

    /// <summary>Path to extracted audio track</summary>
    public string? AudioPath { get; set; }

    /// <summary>Extracted shots</summary>
    public List<ShotSegment> Shots { get; } = [];

    /// <summary>Detected scenes</summary>
    public List<SceneSegment> Scenes { get; } = [];

    /// <summary>Tracked OCR text</summary>
    public List<TextTrack> TextTracks { get; } = [];

    /// <summary>Transcribed utterances</summary>
    public List<Utterance> Utterances { get; } = [];

    /// <summary>Detected speakers</summary>
    public List<Speaker> Speakers { get; } = [];

    /// <summary>Evidence pointers for retrieval</summary>
    public List<VideoEvidence> Evidence { get; } = [];

    /// <summary>Extracted keyframe paths indexed by frame number</summary>
    public Dictionary<int, string> Keyframes { get; } = [];

    /// <summary>Keyframe embeddings indexed by frame number</summary>
    public Dictionary<int, float[]> KeyframeEmbeddings { get; } = [];

    /// <summary>Perceptual hashes indexed by frame number</summary>
    public Dictionary<int, string> PerceptualHashes { get; } = [];

    /// <summary>Frame timestamps (frame index -> seconds)</summary>
    public Dictionary<int, double> FrameTimestamps { get; } = [];

    /// <summary>Arbitrary cache for sharing data between waves</summary>
    private readonly Dictionary<string, object> _cache = new();

    /// <summary>Signals produced by waves</summary>
    private readonly Dictionary<string, List<VideoSignal>> _signals = new();

    /// <summary>Progress callback for reporting status</summary>
    public Action<string, double>? OnProgress { get; set; }

    /// <summary>
    /// Add a signal to the context.
    /// </summary>
    public void AddSignal(VideoSignal signal)
    {
        if (!_signals.ContainsKey(signal.Key))
            _signals[signal.Key] = [];
        _signals[signal.Key].Add(signal);
    }

    /// <summary>
    /// Add multiple signals.
    /// </summary>
    public void AddSignals(IEnumerable<VideoSignal> signals)
    {
        foreach (var signal in signals)
            AddSignal(signal);
    }

    /// <summary>
    /// Get all signals for a key.
    /// </summary>
    public IEnumerable<VideoSignal> GetSignals(string key) =>
        _signals.TryGetValue(key, out var list) ? list : [];

    /// <summary>
    /// Get the best signal for a key (highest confidence).
    /// </summary>
    public VideoSignal? GetBestSignal(string key) =>
        GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();

    /// <summary>
    /// Get value from best signal.
    /// </summary>
    public T? GetValue<T>(string key) =>
        GetBestSignal(key)?.Value is T val ? val : default;

    /// <summary>
    /// Check if a signal exists.
    /// </summary>
    public bool HasSignal(string key) =>
        _signals.ContainsKey(key) && _signals[key].Count > 0;

    /// <summary>
    /// Get all signals.
    /// </summary>
    public IEnumerable<VideoSignal> GetAllSignals() =>
        _signals.Values.SelectMany(s => s);

    /// <summary>
    /// Cache data for sharing between waves.
    /// </summary>
    public void SetCached<T>(string key, T value) => _cache[key] = value!;

    /// <summary>
    /// Retrieve cached data.
    /// </summary>
    public T? GetCached<T>(string key) =>
        _cache.TryGetValue(key, out var val) && val is T typed ? typed : default;

    /// <summary>
    /// Clear cache to free memory.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Report progress.
    /// </summary>
    public void ReportProgress(string stage, double percent) =>
        OnProgress?.Invoke(stage, percent);
}

/// <summary>
/// A signal produced by a video analysis wave.
/// </summary>
public record VideoSignal
{
    /// <summary>Unique key (e.g., "shot.motion_score", "audio.snr")</summary>
    public required string Key { get; init; }

    /// <summary>The measured value</summary>
    public object? Value { get; init; }

    /// <summary>Confidence (0-1)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Source wave</summary>
    public required string Source { get; init; }

    /// <summary>Optional time range this signal applies to</summary>
    public double? StartTime { get; init; }
    public double? EndTime { get; init; }

    /// <summary>When generated</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Optional metadata</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>Tags for categorization</summary>
    public List<string>? Tags { get; init; }
}

/// <summary>
/// Tags for video signal categorization.
/// </summary>
public static class VideoSignalTags
{
    public const string Visual = "visual";
    public const string Audio = "audio";
    public const string Speech = "speech";
    public const string Ocr = "ocr";
    public const string Motion = "motion";
    public const string Scene = "scene";
    public const string Shot = "shot";
    public const string Metadata = "metadata";
    public const string Quality = "quality";
}
