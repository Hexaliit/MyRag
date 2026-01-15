using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Scanning;

/// <summary>
/// Orchestrates document scanning waves.
/// Executes waves in priority order, passing signals between them.
/// </summary>
public class DocumentWaveOrchestrator
{
    private readonly IEnumerable<IDocumentWave> _waves;
    private readonly IDocumentEscalationService _escalationService;
    private readonly ILogger<DocumentWaveOrchestrator>? _logger;

    public DocumentWaveOrchestrator(
        IEnumerable<IDocumentWave> waves,
        IDocumentEscalationService escalationService,
        ILogger<DocumentWaveOrchestrator>? logger = null)
    {
        // Sort by priority (highest first)
        _waves = waves.OrderByDescending(w => w.Priority).ToList();
        _escalationService = escalationService;
        _logger = logger;
    }

    /// <summary>
    /// Get all registered waves.
    /// </summary>
    public IReadOnlyList<IDocumentWave> GetWaves() => _waves.ToList();

    /// <summary>
    /// Process a document through all applicable waves.
    /// </summary>
    public async Task<DocumentScanResult> ProcessAsync(
        string documentPath,
        DocumentScanOptions? options = null,
        IProgress<DocumentScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new DocumentScanOptions();
        var context = new DocumentScanContext
        {
            DocumentPath = documentPath,
            Options = options
        };

        var startTime = DateTime.UtcNow;
        var processedWaves = new List<string>();

        _logger?.LogInformation("Starting document scan: {Document}", Path.GetFileName(documentPath));

        try
        {
            var applicableWaves = _waves.Where(w => w.ShouldRun(context)).ToList();
            var totalWaves = applicableWaves.Count;
            var currentWave = 0;

            foreach (var wave in applicableWaves)
            {
                ct.ThrowIfCancellationRequested();

                currentWave++;
                progress?.Report(new DocumentScanProgress
                {
                    CurrentWave = wave.Name,
                    WaveIndex = currentWave,
                    TotalWaves = totalWaves,
                    Stage = $"Running {wave.Name}"
                });

                _logger?.LogDebug("Executing wave: {Wave} (priority {Priority})", wave.Name, wave.Priority);

                try
                {
                    var signals = await wave.ProcessAsync(documentPath, context, ct);
                    context.AddSignals(signals);
                    processedWaves.Add(wave.Name);

                    _logger?.LogDebug("Wave {Wave} emitted {Count} signals", wave.Name, signals.Count);

                    // Check for escalation after each wave
                    var escalationDecision = _escalationService.ShouldEscalate(context, wave.Name);
                    if (escalationDecision.ShouldEscalate)
                    {
                        _logger?.LogInformation(
                            "Escalating from {Wave} to {Target}: {Reason}",
                            wave.Name, escalationDecision.TargetWave, escalationDecision.Reason);

                        // Find and run the escalation target
                        var targetWave = _waves.FirstOrDefault(w => w.Name == escalationDecision.TargetWave);
                        if (targetWave != null && !processedWaves.Contains(targetWave.Name))
                        {
                            var escalationSignals = await targetWave.ProcessAsync(documentPath, context, ct);
                            context.AddSignals(escalationSignals);
                            processedWaves.Add(targetWave.Name);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Wave {Wave} failed, continuing", wave.Name);
                    context.AddSignal(new DocumentSignal
                    {
                        Key = $"error.{wave.Name}",
                        Value = ex.Message,
                        Confidence = 1.0,
                        Source = wave.Name
                    });
                }
            }

            var elapsed = DateTime.UtcNow - startTime;

            return new DocumentScanResult
            {
                Success = true,
                DocumentPath = documentPath,
                Signals = context.Signals.Values.ToList(),
                ProcessedWaves = processedWaves,
                ProcessingTime = elapsed
            };
        }
        catch (OperationCanceledException)
        {
            return new DocumentScanResult
            {
                Success = false,
                DocumentPath = documentPath,
                Error = "Processing cancelled",
                ProcessedWaves = processedWaves,
                ProcessingTime = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Document scan failed: {Document}", documentPath);
            return new DocumentScanResult
            {
                Success = false,
                DocumentPath = documentPath,
                Error = ex.Message,
                ProcessedWaves = processedWaves,
                ProcessingTime = DateTime.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Process with specific waves only.
    /// </summary>
    public async Task<DocumentScanResult> ProcessWithWavesAsync(
        string documentPath,
        IEnumerable<string> waveNames,
        DocumentScanOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DocumentScanOptions();
        var context = new DocumentScanContext
        {
            DocumentPath = documentPath,
            Options = options
        };

        var waveSet = waveNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedWaves = _waves.Where(w => waveSet.Contains(w.Name)).ToList();

        var startTime = DateTime.UtcNow;
        var processedWaves = new List<string>();

        foreach (var wave in selectedWaves)
        {
            ct.ThrowIfCancellationRequested();

            if (!wave.ShouldRun(context))
                continue;

            var signals = await wave.ProcessAsync(documentPath, context, ct);
            context.AddSignals(signals);
            processedWaves.Add(wave.Name);
        }

        return new DocumentScanResult
        {
            Success = true,
            DocumentPath = documentPath,
            Signals = context.Signals.Values.ToList(),
            ProcessedWaves = processedWaves,
            ProcessingTime = DateTime.UtcNow - startTime
        };
    }
}

/// <summary>
/// Result of document scanning.
/// </summary>
public class DocumentScanResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public required string DocumentPath { get; init; }
    public IReadOnlyList<DocumentSignal> Signals { get; init; } = [];
    public IReadOnlyList<string> ProcessedWaves { get; init; } = [];
    public TimeSpan ProcessingTime { get; init; }

    /// <summary>
    /// Get the extracted markdown content.
    /// </summary>
    public string? GetMarkdown()
    {
        return Signals.FirstOrDefault(s => s.Key == "content.markdown")?.Value as string;
    }

    /// <summary>
    /// Get the extraction method used.
    /// </summary>
    public string? GetExtractionMethod()
    {
        return Signals.FirstOrDefault(s => s.Key == "extraction.method")?.Value as string;
    }

    /// <summary>
    /// Get average text density.
    /// </summary>
    public int GetTextDensity()
    {
        return Signals.FirstOrDefault(s => s.Key == "extraction.text_density")?.Value as int? ?? 0;
    }
}

/// <summary>
/// Progress during document scanning.
/// </summary>
public class DocumentScanProgress
{
    public string CurrentWave { get; init; } = "";
    public int WaveIndex { get; init; }
    public int TotalWaves { get; init; }
    public string Stage { get; init; } = "";
    public double ProgressPercent => TotalWaves > 0 ? (double)WaveIndex / TotalWaves * 100 : 0;
}
