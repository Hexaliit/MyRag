using SharedAnalysis = Mostlylucid.Summarizer.Core.Analysis;

namespace Mostlylucid.DocSummarizer.Scanning;

/// <summary>
///     Interface for document scanning waves.
///     Waves are composable processing units that extract information from documents.
///     Each wave emits signals that downstream waves can consume.
/// </summary>
public interface IDocumentWave
{
    /// <summary>
    ///     Unique identifier for this wave.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Human-readable description.
    /// </summary>
    string Description { get; }

    /// <summary>
    ///     Execution priority (higher = runs first).
    /// </summary>
    int Priority { get; }

    /// <summary>
    ///     Tags for filtering and categorization.
    /// </summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    ///     Signals this wave requires to run.
    /// </summary>
    IReadOnlyList<string> RequiredSignals { get; }

    /// <summary>
    ///     Signals this wave can emit.
    /// </summary>
    IReadOnlyList<string> EmittedSignals { get; }

    /// <summary>
    ///     Check if this wave should run given the current context.
    /// </summary>
    bool ShouldRun(DocumentScanContext context);

    /// <summary>
    ///     Execute the wave and return signals.
    /// </summary>
    Task<IReadOnlyList<DocumentSignal>> ProcessAsync(
        string documentPath,
        DocumentScanContext context,
        CancellationToken ct = default);
}

/// <summary>
///     A signal emitted by a document wave.
///     Signals are key-value pairs with metadata about confidence and source.
/// </summary>
public class DocumentSignal
{
    /// <summary>
    ///     Signal key using dot notation (e.g., "content.text", "table.0.rows").
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    ///     The signal value (any type - string, int, List, etc.).
    /// </summary>
    public required object? Value { get; init; }

    /// <summary>
    ///     Confidence in this signal (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    ///     Source wave/model that produced this signal.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    ///     Tags for categorization.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    ///     Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Convert to the shared Signal type from Summarizer.Core.
    /// </summary>
    public SharedAnalysis.Signal ToSharedSignal()
    {
        return new SharedAnalysis.Signal
        {
            Key = Key,
            Value = Value,
            Confidence = Confidence,
            Source = Source,
            Tags = Tags?.ToList(),
            Metadata = Metadata
        };
    }

    /// <summary>
    ///     Create a DocumentSignal from a shared Signal.
    /// </summary>
    public static DocumentSignal FromSharedSignal(SharedAnalysis.Signal signal)
    {
        return new DocumentSignal
        {
            Key = signal.Key,
            Value = signal.Value,
            Confidence = signal.Confidence,
            Source = signal.Source,
            Tags = signal.Tags ?? [],
            Metadata = signal.Metadata
        };
    }
}

/// <summary>
///     Context shared across document waves during processing.
///     Accumulates signals and tracks processing state.
/// </summary>
public class DocumentScanContext
{
    private readonly Dictionary<string, object> _cache = new();
    private readonly Dictionary<string, DocumentSignal> _signals = new();

    /// <summary>
    ///     Source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    ///     Document extension (lowercase, with dot).
    /// </summary>
    public string Extension => Path.GetExtension(DocumentPath).ToLowerInvariant();

    /// <summary>
    ///     All signals collected so far.
    /// </summary>
    public IReadOnlyDictionary<string, DocumentSignal> Signals => _signals;

    /// <summary>
    ///     Processing options.
    /// </summary>
    public DocumentScanOptions Options { get; init; } = new();

    /// <summary>
    ///     Whether processing has been cancelled.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    ///     Whether to force VLM OCR (skip native extraction).
    /// </summary>
    public bool ForceVlmOcr { get; set; }

    /// <summary>
    ///     Add a signal to the context.
    /// </summary>
    public void AddSignal(DocumentSignal signal)
    {
        _signals[signal.Key] = signal;
    }

    /// <summary>
    ///     Add multiple signals.
    /// </summary>
    public void AddSignals(IEnumerable<DocumentSignal> signals)
    {
        foreach (var signal in signals)
            _signals[signal.Key] = signal;
    }

    /// <summary>
    ///     Check if a signal exists.
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key);
    }

    /// <summary>
    ///     Get a signal value.
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        if (_signals.TryGetValue(key, out var signal) && signal.Value is T value)
            return value;
        return default;
    }

    /// <summary>
    ///     Get signal confidence.
    /// </summary>
    public double GetConfidence(string key)
    {
        return _signals.TryGetValue(key, out var signal) ? signal.Confidence : 0.0;
    }

    /// <summary>
    ///     Check if a signal pattern matches (supports wildcards).
    /// </summary>
    public bool HasSignalPattern(string pattern)
    {
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return _signals.Keys.Any(k => k.StartsWith(prefix));
        }

        return _signals.ContainsKey(pattern);
    }

    /// <summary>
    ///     Get or create a cached value.
    /// </summary>
    public T GetOrCreate<T>(string key, Func<T> factory)
    {
        if (_cache.TryGetValue(key, out var cached) && cached is T value)
            return value;

        var created = factory();
        _cache[key] = created!;
        return created;
    }

    /// <summary>
    ///     Set a cached value.
    /// </summary>
    public void SetCached<T>(string key, T value)
    {
        _cache[key] = value!;
    }

    /// <summary>
    ///     Get a cached value.
    /// </summary>
    public T? GetCached<T>(string key)
    {
        return _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }
}

/// <summary>
///     Options for document scanning.
/// </summary>
public class DocumentScanOptions
{
    /// <summary>
    ///     Pipeline to use (e.g., "auto", "fast", "quality").
    /// </summary>
    public string Pipeline { get; set; } = "auto";

    /// <summary>
    ///     Preferred VLM model for OCR.
    /// </summary>
    public string? PreferredOcrModel { get; set; }

    /// <summary>
    ///     Whether to extract tables.
    /// </summary>
    public bool ExtractTables { get; set; } = true;

    /// <summary>
    ///     Whether to extract charts/figures.
    /// </summary>
    public bool ExtractCharts { get; set; } = false;

    /// <summary>
    ///     DPI for rendering PDF pages.
    /// </summary>
    public int RenderDpi { get; set; } = 300;

    /// <summary>
    ///     Specific pages to process (null = all).
    /// </summary>
    public List<int>? Pages { get; set; }

    /// <summary>
    ///     Maximum time for processing in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 300000; // 5 minutes

    /// <summary>
    ///     Enable caching of results.
    /// </summary>
    public bool EnableCache { get; set; } = true;
}