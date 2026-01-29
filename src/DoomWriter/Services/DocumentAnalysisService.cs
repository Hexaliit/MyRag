using System.IO.Hashing;
using System.Text;
using Mostlylucid.Summarizer.Core.Analysis;
using DoomSummarizer.Services;
using DoomWriter.Models;
using DoomWriter.Waves;

namespace DoomWriter.Services;

/// <summary>
/// Reactive document analysis pipeline using WaveCoordinator.
/// On every content change (debounced), runs analysis waves that produce signals:
/// headings, segments, entities, topics, drift.
/// Uses XxHash64 for content change detection to skip re-analysis of unchanged content.
/// Waves execute with concurrency lanes: fast (regex/text), ml (ONNX NER).
/// </summary>
public class DocumentAnalysisService
{
    private readonly WriterSettingsService _settings;
    private readonly WaveCoordinator _coordinator;
    private CancellationTokenSource? _debounceCts;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private ulong _lastContentHash;
    private DocumentSignals? _cachedSignals;
    private CoordinatorResult? _lastCoordinatorResult;

    /// <summary>
    /// Raised when analysis completes with new signals.
    /// </summary>
    public event EventHandler<DocumentSignals>? AnalysisCompleted;

    /// <summary>
    /// The last coordinator result with execution telemetry (wave durations, lanes, errors).
    /// </summary>
    public CoordinatorResult? LastCoordinatorResult => _lastCoordinatorResult;

    public DocumentAnalysisService(WriterSettingsService settings, NerService nerService)
    {
        _settings = settings;

        // Build wave coordinator with all analysis waves
        _coordinator = new WaveCoordinator();
        _coordinator.RegisterWaves([
            new HeadingExtractionWave(),     // Priority 90, fast lane
            new SegmentExtractionWave(),     // Priority 85, fast lane
            new WordCountWave(),             // Priority 80, fast lane
            new EntityExtractionWave(nerService), // Priority 60, ml lane
            new TopicInferenceWave()         // Priority 50, fast lane (depends on headings + segments)
        ]);
    }

    /// <summary>
    /// Compute XxHash64 of the content for change detection.
    /// </summary>
    private static ulong ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return XxHash64.HashToUInt64(bytes);
    }

    /// <summary>
    /// Analyze markdown content. Debounced — cancels any pending analysis.
    /// Skips analysis if content hash hasn't changed.
    /// </summary>
    public async Task AnalyzeAsync(string markdown)
    {
        // Cancel any pending debounced analysis
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;

        try
        {
            // Debounce wait
            await Task.Delay(_settings.Config.DebounceMs, ct);

            // XxHash64 change detection — skip if content unchanged
            var hash = ComputeHash(markdown);
            if (hash == _lastContentHash && _cachedSignals != null)
            {
                AnalysisCompleted?.Invoke(this, _cachedSignals);
                return;
            }

            await _analysisLock.WaitAsync(ct);
            try
            {
                var signals = await RunWavePipelineAsync(markdown, ct);
                _lastContentHash = hash;
                _cachedSignals = signals;
                AnalysisCompleted?.Invoke(this, signals);
            }
            finally
            {
                _analysisLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when debounce is superseded by a newer change
        }
    }

    /// <summary>
    /// Analyze markdown content immediately (no debounce).
    /// Use for initial document load — populates signal panel right away.
    /// Still uses XxHash64 to skip if content is unchanged.
    /// </summary>
    public async Task AnalyzeImmediateAsync(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var hash = ComputeHash(markdown);
        if (hash == _lastContentHash && _cachedSignals != null)
        {
            AnalysisCompleted?.Invoke(this, _cachedSignals);
            return;
        }

        await _analysisLock.WaitAsync();
        try
        {
            var signals = await RunWavePipelineAsync(markdown, CancellationToken.None);
            _lastContentHash = hash;
            _cachedSignals = signals;
            AnalysisCompleted?.Invoke(this, signals);
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    /// <summary>
    /// Execute the wave coordinator and map results to DocumentSignals.
    /// </summary>
    private async Task<DocumentSignals> RunWavePipelineAsync(string markdown, CancellationToken ct)
    {
        var result = await _coordinator.ExecuteAsync(
            markdown,
            profile: CoordinatorProfile.Default,
            ct: ct);

        _lastCoordinatorResult = result;
        var ctx = result.Context;

        // Map wave signals → DocumentSignals
        var signals = new DocumentSignals
        {
            Headings = ctx.GetValue<List<HeadingItem>>("doc.structure.headings") ?? [],
            Segments = ctx.GetValue<List<AnalyzedSegment>>("doc.structure.segments") ?? [],
            WordCount = ctx.GetValue<int>("doc.metrics.word_count"),
            Entities = ctx.GetValue<List<TrackedEntity>>("doc.entities.tracked") ?? [],
            Topics = ctx.GetValue<List<TopicScore>>("doc.topics.inferred") ?? [],
            DominantTopic = ctx.GetValue<string>("doc.topics.dominant") ?? "",
            DriftScore = 0f, // Placeholder — embedding-based drift in future wave
            CoherenceScore = 1f
        };

        signals.SegmentCount = signals.Segments.Count;
        signals.EntityCount = signals.Entities.Count;

        return signals;
    }
}
