using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Services;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Enhanced OCR processing for title/credits sequences.
///     Extracts additional frames from title/credits segments and runs
///     quality OCR using ImageSummarizer's bounding box optimization.
///     This wave runs after TitleCreditsDetectionWave and uses its detections
///     to know which regions need enhanced processing.
/// </summary>
public class EnhancedCreditsOcrWave : IVideoWave
{
    // Configuration
    private const int FramesPerSecond = 2; // Sample 2 frames per second in credits
    private const int MaxCreditsFrames = 30; // Limit frames to process
    private const int MaxTitleFrames = 10; // Titles are usually shorter
    private const int ChunkSize = 5; // Process 5 frames per chunk
    private const double ChunkOverlapSeconds = 0.5; // 0.5s overlap between chunks for deduplication
    private const int MaxParallelChunks = 4; // Process up to 4 chunks in parallel
    private readonly FFmpegAnalysisService _ffmpegService;
    private readonly WaveOrchestrator? _imageOrchestrator;
    private readonly ILogger<EnhancedCreditsOcrWave> _logger;

    public EnhancedCreditsOcrWave(
        FFmpegAnalysisService ffmpegService,
        ILogger<EnhancedCreditsOcrWave> logger,
        WaveOrchestrator? imageOrchestrator = null)
    {
        _ffmpegService = ffmpegService;
        _imageOrchestrator = imageOrchestrator;
        _logger = logger;
    }

    public string Name => "enhanced_credits_ocr";
    public int Priority => 780; // After title detection (785), before subtitle extraction (750)
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual];

    public bool ShouldRun(VideoContext context)
    {
        // Only run if we have ImageSummarizer and detected title/credits
        if (_imageOrchestrator == null)
            return false;

        var titleSequence = context.GetCached<TitleCreditsSegment>("title_sequence");
        var creditsSequence = context.GetCached<TitleCreditsSegment>("credits_sequence");

        return titleSequence != null || creditsSequence != null;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Enhanced OCR for title/credits", 0);

        var titleSequence = context.GetCached<TitleCreditsSegment>("title_sequence");
        var creditsSequence = context.GetCached<TitleCreditsSegment>("credits_sequence");

        var frameDir = Path.Combine(context.WorkingDirectory, "credits_ocr");
        Directory.CreateDirectory(frameDir);

        var allOcrResults = new List<CreditsOcrResult>();

        // Process title sequence
        if (titleSequence != null && titleSequence.Confidence > 0.4)
        {
            context.ReportProgress("Processing title sequence", 20);
            var titleResults = await ProcessSequenceAsync(
                context, titleSequence, frameDir, MaxTitleFrames, "title_", ct);
            allOcrResults.AddRange(titleResults);

            _logger.LogInformation("Title OCR: extracted {Count} text segments from {Frames} frames",
                titleResults.Sum(r => r.TextSegments.Count), titleResults.Count);
        }

        // Process credits sequence
        if (creditsSequence != null && creditsSequence.Confidence > 0.4)
        {
            context.ReportProgress("Processing credits sequence", 60);
            var creditsResults = await ProcessSequenceAsync(
                context, creditsSequence, frameDir, MaxCreditsFrames, "credits_", ct);
            allOcrResults.AddRange(creditsResults);

            _logger.LogInformation("Credits OCR: extracted {Count} text segments from {Frames} frames",
                creditsResults.Sum(r => r.TextSegments.Count), creditsResults.Count);
        }

        // Aggregate results using temporal voting
        var aggregatedText = AggregateOcrResults(allOcrResults);

        // Store results
        context.SetCached("enhanced_credits_ocr.results", allOcrResults);
        context.SetCached("enhanced_credits_ocr.aggregated_text", aggregatedText);

        // Add text tracks for detected credit names
        CreateTextTracksFromCredits(context, allOcrResults);

        // Add summary signals
        context.AddSignals([
            new VideoSignal
            {
                Key = "enhanced_credits_ocr.frames_processed",
                Value = allOcrResults.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "enhanced_credits_ocr.total_text_segments",
                Value = allOcrResults.Sum(r => r.TextSegments.Count),
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            },
            new VideoSignal
            {
                Key = "enhanced_credits_ocr.unique_names",
                Value = aggregatedText.Count,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            }
        ]);

        context.ReportProgress("Enhanced credits OCR complete", 100);
    }

    /// <summary>
    ///     Process a title or credits sequence by extracting frames and running OCR.
    ///     Uses parallel chunk processing with overlap for efficient deduplication.
    /// </summary>
    private async Task<List<CreditsOcrResult>> ProcessSequenceAsync(
        VideoContext context,
        TitleCreditsSegment segment,
        string outputDir,
        int maxFrames,
        string prefix,
        CancellationToken ct)
    {
        var duration = segment.EndTime - segment.StartTime;
        var interval = 1.0 / FramesPerSecond;
        var frameCount = Math.Min((int)(duration * FramesPerSecond), maxFrames);

        // Generate timestamps for frame extraction
        var timestamps = new List<double>();
        for (var i = 0; i < frameCount; i++)
        {
            var timestamp = segment.StartTime + i * interval;
            if (timestamp < segment.EndTime)
                timestamps.Add(timestamp);
        }

        _logger.LogInformation(
            "Extracting {Count} frames from {Type} sequence ({Start:F1}s - {End:F1}s) with parallel chunks",
            timestamps.Count, segment.Type, segment.StartTime, segment.EndTime);

        // Extract all frames first (single FFmpeg pass is more efficient)
        var extractedFrames = await _ffmpegService.ExtractFramesAtTimestampsAsync(
            context.VideoPath,
            timestamps,
            outputDir,
            ct,
            prefix);

        // Create chunks with overlap for parallel processing
        var frameList = extractedFrames.OrderBy(f => f.Key).ToList();
        var chunks = CreateOverlappingChunks(frameList, ChunkSize, ChunkOverlapSeconds);

        _logger.LogInformation("Processing {ChunkCount} chunks in parallel (max {MaxParallel})",
            chunks.Count, MaxParallelChunks);

        // Process chunks in parallel with limited concurrency
        var semaphore = new SemaphoreSlim(MaxParallelChunks);
        var chunkTasks = chunks.Select(async chunk =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ProcessChunkAsync(chunk, segment.Type, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var chunkResults = await Task.WhenAll(chunkTasks);

        // Flatten and deduplicate results from overlapping chunks
        var allResults = chunkResults.SelectMany(r => r).ToList();
        var deduplicatedResults = DeduplicateResults(allResults);

        _logger.LogInformation(
            "Processed {Total} frames, {Unique} unique results after deduplication",
            allResults.Count, deduplicatedResults.Count);

        return deduplicatedResults;
    }

    /// <summary>
    ///     Create overlapping chunks from a list of frames for parallel processing.
    ///     Overlap ensures we don't miss text that spans chunk boundaries.
    /// </summary>
    private List<List<KeyValuePair<double, string>>> CreateOverlappingChunks(
        List<KeyValuePair<double, string>> frames,
        int chunkSize,
        double overlapSeconds)
    {
        var chunks = new List<List<KeyValuePair<double, string>>>();

        for (var i = 0; i < frames.Count; i += chunkSize)
        {
            var chunk = new List<KeyValuePair<double, string>>();

            // Add main frames for this chunk
            for (var j = i; j < Math.Min(i + chunkSize, frames.Count); j++) chunk.Add(frames[j]);

            // Add overlap frames from next chunk (if they exist and are within overlap window)
            var lastTimestamp = chunk.Last().Key;
            for (var j = i + chunkSize; j < frames.Count; j++)
                if (frames[j].Key <= lastTimestamp + overlapSeconds)
                    chunk.Add(frames[j]);
                else
                    break;

            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    ///     Process a single chunk of frames with ImageSummarizer.
    /// </summary>
    private async Task<List<CreditsOcrResult>> ProcessChunkAsync(
        List<KeyValuePair<double, string>> chunk,
        SequenceType sequenceType,
        CancellationToken ct)
    {
        var results = new List<CreditsOcrResult>();

        foreach (var (timestamp, framePath) in chunk)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Run ImageSummarizer analysis - uses bounding box detection internally
                var profile = await _imageOrchestrator!.AnalyzeAsync(framePath, ct);

                // Extract OCR text
                var ocrText = profile.GetValue<string>("ocr.text")
                              ?? profile.GetValue<string>("ml_ocr.final_text")
                              ?? "";

                var ocrConfidence = profile.GetValue<double?>("ocr.confidence") ?? 0.5;

                // Get bounding boxes if available
                var boundingBoxes = profile.GetValue<List<object>>("text_detection.regions")
                                    ?? profile.GetValue<List<object>>("ocr.boxes");

                if (!string.IsNullOrWhiteSpace(ocrText))
                    results.Add(new CreditsOcrResult
                    {
                        Timestamp = timestamp,
                        SequenceType = sequenceType,
                        TextSegments = ParseTextSegments(ocrText),
                        Confidence = ocrConfidence,
                        HasBoundingBoxes = boundingBoxes?.Count > 0
                    });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to OCR frame at {Timestamp:F2}s", timestamp);
            }
        }

        return results;
    }

    /// <summary>
    ///     Deduplicate results from overlapping chunks.
    ///     Same timestamp appears once, preferring higher confidence results.
    /// </summary>
    private List<CreditsOcrResult> DeduplicateResults(List<CreditsOcrResult> results)
    {
        return results
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.Confidence).First())
            .OrderBy(r => r.Timestamp)
            .ToList();
    }

    /// <summary>
    ///     Parse OCR text into individual text segments (names, roles, etc.)
    /// </summary>
    private List<TextSegment> ParseTextSegments(string text)
    {
        var segments = new List<TextSegment>();

        // Split by newlines and filter empty lines
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 2)
            .ToList();

        foreach (var line in lines)
        {
            // Credits often have patterns like "Director: John Smith" or "John Smith - Director"
            var isRole = line.Contains(':') || line.Contains('-') || line.Contains("...");

            segments.Add(new TextSegment
            {
                Text = line,
                IsRole = isRole,
                Confidence = 0.7 // Base confidence, adjusted by voting
            });
        }

        return segments;
    }

    /// <summary>
    ///     Aggregate OCR results using temporal voting to get consensus text.
    ///     Same text appearing in multiple frames increases confidence.
    /// </summary>
    private List<AggregatedTextEntry> AggregateOcrResults(List<CreditsOcrResult> results)
    {
        var textVotes = new Dictionary<string, TextVoteInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        foreach (var segment in result.TextSegments)
        {
            var normalizedText = NormalizeText(segment.Text);
            if (string.IsNullOrWhiteSpace(normalizedText))
                continue;

            if (!textVotes.ContainsKey(normalizedText))
                textVotes[normalizedText] = new TextVoteInfo
                {
                    OriginalText = segment.Text,
                    FirstSeen = result.Timestamp,
                    LastSeen = result.Timestamp,
                    IsRole = segment.IsRole
                };

            var vote = textVotes[normalizedText];
            vote.VoteCount++;
            vote.ConfidenceSum += result.Confidence;
            vote.LastSeen = Math.Max(vote.LastSeen, result.Timestamp);
        }

        // Convert to aggregated entries, boosting confidence for repeated text
        return textVotes.Values
            .Select(v => new AggregatedTextEntry
            {
                Text = v.OriginalText,
                VoteCount = v.VoteCount,
                Confidence = Math.Min(0.95, v.ConfidenceSum / v.VoteCount + v.VoteCount * 0.05),
                FirstSeen = v.FirstSeen,
                LastSeen = v.LastSeen,
                IsRole = v.IsRole
            })
            .OrderByDescending(e => e.VoteCount)
            .ThenByDescending(e => e.Confidence)
            .ToList();
    }

    /// <summary>
    ///     Normalize text for comparison (lowercase, remove extra whitespace).
    /// </summary>
    private static string NormalizeText(string text)
    {
        return string.Join(" ", text.ToLowerInvariant().Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    ///     Create text tracks from aggregated credits OCR results.
    /// </summary>
    private void CreateTextTracksFromCredits(VideoContext context, List<CreditsOcrResult> results)
    {
        // Group by sequence type and create text tracks
        var titleResults = results.Where(r => r.SequenceType == SequenceType.Title).ToList();
        var creditsResults = results.Where(r => r.SequenceType == SequenceType.Credits).ToList();

        if (titleResults.Count > 0)
        {
            var titleText = string.Join("\n",
                titleResults.SelectMany(r => r.TextSegments)
                    .Select(s => s.Text)
                    .Distinct()
                    .Take(10));

            if (!string.IsNullOrWhiteSpace(titleText))
                context.TextTracks.Add(new TextTrack
                {
                    Id = Guid.NewGuid(),
                    VideoId = context.Metadata!.Id,
                    StartTime = titleResults.Min(r => r.Timestamp),
                    EndTime = titleResults.Max(r => r.Timestamp) + 0.5,
                    Text = $"[Title Sequence]\n{titleText}",
                    TextType = TextTrackType.TitleCard,
                    Confidence = titleResults.Average(r => r.Confidence)
                });
        }

        if (creditsResults.Count > 0)
        {
            var creditsText = string.Join("\n",
                creditsResults.SelectMany(r => r.TextSegments)
                    .Select(s => s.Text)
                    .Distinct()
                    .Take(50)); // Credits can have many names

            if (!string.IsNullOrWhiteSpace(creditsText))
                context.TextTracks.Add(new TextTrack
                {
                    Id = Guid.NewGuid(),
                    VideoId = context.Metadata!.Id,
                    StartTime = creditsResults.Min(r => r.Timestamp),
                    EndTime = creditsResults.Max(r => r.Timestamp) + 0.5,
                    Text = $"[Credits]\n{creditsText}",
                    TextType = TextTrackType.Credits, // Credits track type
                    Confidence = creditsResults.Average(r => r.Confidence)
                });
        }
    }
}

/// <summary>
///     OCR result from a single credits/title frame.
/// </summary>
public record CreditsOcrResult
{
    public double Timestamp { get; init; }
    public SequenceType SequenceType { get; init; }
    public List<TextSegment> TextSegments { get; init; } = [];
    public double Confidence { get; init; }
    public bool HasBoundingBoxes { get; init; }
}

/// <summary>
///     Individual text segment from OCR.
/// </summary>
public record TextSegment
{
    public string Text { get; init; } = "";
    public bool IsRole { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
///     Aggregated text entry after temporal voting.
/// </summary>
public record AggregatedTextEntry
{
    public string Text { get; init; } = "";
    public int VoteCount { get; init; }
    public double Confidence { get; init; }
    public double FirstSeen { get; init; }
    public double LastSeen { get; init; }
    public bool IsRole { get; init; }
}

/// <summary>
///     Internal class for vote tracking.
/// </summary>
file class TextVoteInfo
{
    public string OriginalText { get; set; } = "";
    public int VoteCount { get; set; }
    public double ConfidenceSum { get; set; }
    public double FirstSeen { get; set; }
    public double LastSeen { get; set; }
    public bool IsRole { get; set; }
}