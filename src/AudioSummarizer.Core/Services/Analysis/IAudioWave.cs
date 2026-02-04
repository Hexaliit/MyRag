using AudioSummarizer.Core.Models;

namespace AudioSummarizer.Core.Services.Analysis;

/// <summary>
///     Interface for audio analysis waves.
///     Each wave extracts specific signals from audio files in the wave-based pipeline.
/// </summary>
public interface IAudioWave
{
    /// <summary>
    ///     Wave name for identification
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Wave execution priority (higher = runs first)
    ///     Priority ranges:
    ///     - 100: Identity (SHA-256, file metadata)
    ///     - 90: Fingerprinting (perceptual hashing)
    ///     - 80: Acoustic profile (RMS, spectral features)
    ///     - 70: Content classification (speech vs music)
    ///     - 60: Transcription (speech-to-text)
    ///     - 50: Speaker diarization
    ///     - 30: Voice embeddings
    /// </summary>
    int Priority { get; }

    /// <summary>
    ///     Whether this wave should run for the given audio file
    /// </summary>
    /// <param name="audioPath">Path to the audio file</param>
    /// <param name="context">Shared analysis context with signals from previous waves</param>
    /// <returns>True if the wave should execute</returns>
    bool ShouldRun(string audioPath, AnalysisContext context);

    /// <summary>
    ///     Analyze the audio file and extract signals
    /// </summary>
    /// <param name="audioPath">Path to the audio file</param>
    /// <param name="context">Shared analysis context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of signals extracted by this wave</returns>
    Task<IEnumerable<Signal>> AnalyzeAsync(string audioPath, AnalysisContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Shared context passed between waves during analysis.
///     Allows later waves to access signals from earlier waves.
/// </summary>
public sealed class AnalysisContext
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Signal> _signals = new();

    /// <summary>
    ///     All signals collected so far from previous waves
    /// </summary>
    public IReadOnlyDictionary<string, Signal> Signals
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, Signal>(_signals);
            }
        }
    }

    /// <summary>
    ///     Add signals from a wave
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        lock (_lock)
        {
            foreach (var signal in signals) _signals[signal.Name] = signal;
        }
    }

    /// <summary>
    ///     Get a signal value by name
    /// </summary>
    public T? GetValue<T>(string name)
    {
        lock (_lock)
        {
            if (_signals.TryGetValue(name, out var signal))
            {
                if (signal.Value is T typedValue) return typedValue;

                try
                {
                    return (T)Convert.ChangeType(signal.Value, typeof(T));
                }
                catch
                {
                    return default;
                }
            }

            return default;
        }
    }

    /// <summary>
    ///     Check if a signal exists
    /// </summary>
    public bool HasSignal(string name)
    {
        lock (_lock)
        {
            return _signals.ContainsKey(name);
        }
    }
}