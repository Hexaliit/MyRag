namespace AudioSummarizer.Core.Models;

/// <summary>
///     Complete audio characterization profile containing all extracted signals.
///     Follows the "Constrained Fuzziness" philosophy: deterministic substrate + probabilistic proposers.
/// </summary>
public sealed class AudioProfile
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Signal> _signals = new();

    /// <summary>
    ///     Path to the audio file being analyzed
    /// </summary>
    public required string AudioPath { get; init; }

    /// <summary>
    ///     All signals extracted from the audio file
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
    ///     Processing errors encountered during analysis
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    ///     When the profile was created
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Total processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    ///     Add a signal to the profile
    /// </summary>
    public void AddSignal(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        lock (_lock)
        {
            _signals[signal.Name] = signal;
        }
    }

    /// <summary>
    ///     Add a signal with simplified parameters
    /// </summary>
    public void AddSignal(string name, object value, double? confidence = null, SignalType type = SignalType.Metadata,
        string? source = null)
    {
        var signal = new Signal
        {
            Name = name,
            Value = value,
            Confidence = confidence,
            Type = type,
            Source = source
        };
        AddSignal(signal);
    }

    /// <summary>
    ///     Add multiple signals at once
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals) AddSignal(signal);
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

                // Try to convert
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

    /// <summary>
    ///     Get all signals of a specific type
    /// </summary>
    public IEnumerable<Signal> GetSignalsByType(SignalType type)
    {
        lock (_lock)
        {
            return _signals.Values
                .Where(s => s.Type == type)
                .ToList();
        }
    }

    /// <summary>
    ///     Get all signals from a specific source
    /// </summary>
    public IEnumerable<Signal> GetSignalsBySource(string source)
    {
        lock (_lock)
        {
            return _signals.Values
                .Where(s => s.Source == source)
                .ToList();
        }
    }

    /// <summary>
    ///     Convert profile to JSON for storage as EvidenceTypes.SignalDump
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    ///     Get a formatted summary of the profile
    /// </summary>
    public string GetSummary()
    {
        var signalCount = Signals.Count;
        var errorCount = Errors.Count;
        var duration = GetValue<double>("audio.duration_seconds");
        var format = GetValue<string>("audio.format");

        return $"AudioProfile: {Path.GetFileName(AudioPath)}\n" +
               $"  Format: {format ?? "unknown"}\n" +
               $"  Duration: {duration:F1}s\n" +
               $"  Signals: {signalCount}\n" +
               $"  Errors: {errorCount}\n" +
               $"  Processing Time: {ProcessingTimeMs}ms";
    }
}