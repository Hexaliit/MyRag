using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Detects title sequences and credit rolls in video.
/// Title sequences typically appear in the first 5% of the video with text overlays.
/// Credits appear in the last 10% with scrolling text on dark backgrounds.
/// These segments need enhanced OCR processing.
/// </summary>
public class TitleCreditsDetectionWave : IVideoWave
{
    private readonly ILogger<TitleCreditsDetectionWave> _logger;

    // Detection thresholds
    private const double TitleWindowPercent = 0.05; // First 5% of video
    private const double CreditsWindowPercent = 0.10; // Last 10% of video
    private const double MinTitleDuration = 5.0; // Minimum 5 seconds
    private const double MinCreditsDuration = 10.0; // Minimum 10 seconds
    private const double TextDensityThreshold = 0.3; // 30% of frame contains text regions
    private const double DarkBackgroundThreshold = 0.7; // 70% dark pixels

    public string Name => "title_credits_detection";
    public int Priority => 785; // After shot thumbnails, before subtitle extraction
    public IReadOnlyList<string> Tags => [VideoSignalTags.Visual, VideoSignalTags.Shot];

    public TitleCreditsDetectionWave(ILogger<TitleCreditsDetectionWave> logger)
    {
        _logger = logger;
    }

    public bool ShouldRun(VideoContext context) =>
        context.Metadata != null && context.Shots.Count > 0;

    public Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Detecting title/credit sequences", 0);

        var duration = context.Metadata!.Duration;
        var titleWindowEnd = duration * TitleWindowPercent;
        var creditsWindowStart = duration * (1 - CreditsWindowPercent);

        // Detect title sequence (beginning of video)
        context.ReportProgress("Analyzing title sequence region", 20);
        var titleSequence = DetectTitleSequence(context, titleWindowEnd);

        // Detect credits sequence (end of video)
        context.ReportProgress("Analyzing credits region", 60);
        var creditsSequence = DetectCreditsSequence(context, creditsWindowStart, duration);

        // Mark shots that need enhanced OCR
        MarkShotsForEnhancedOcr(context, titleSequence, creditsSequence);

        // Add detection signals
        AddDetectionSignals(context, titleSequence, creditsSequence);

        context.ReportProgress("Title/credits detection complete", 100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detect potential title sequence at the beginning of the video.
    /// </summary>
    private TitleCreditsSegment? DetectTitleSequence(VideoContext context, double windowEnd)
    {
        // Get shots in title window
        var titleShots = context.Shots
            .Where(s => s.StartTime < windowEnd)
            .OrderBy(s => s.StartTime)
            .ToList();

        if (titleShots.Count == 0)
            return null;

        // Calculate title characteristics
        var titleEndTime = 0.0;
        var textDensitySum = 0.0;
        var darkBackgroundCount = 0;
        var shotCount = 0;

        foreach (var shot in titleShots)
        {
            var profile = context.GetCached<dynamic>($"image_profile.{shot.KeyframeIndex}");
            if (profile == null) continue;

            shotCount++;

            // Check for high text likeliness (from ImageSummarizer)
            var textLikeliness = GetSignalValue<double>(profile, "content.text_likeliness") ?? 0;
            textDensitySum += textLikeliness;

            // Check for dark background
            var avgLuminance = GetSignalValue<double>(profile, "color.luminance.mean") ?? 0.5;
            if (avgLuminance < 0.3) // Dark background
                darkBackgroundCount++;

            // Check for OCR text presence
            var ocrText = GetSignalValue<string>(profile, "ocr.text") ?? "";
            if (!string.IsNullOrEmpty(ocrText) && ocrText.Length > 10)
            {
                titleEndTime = Math.Max(titleEndTime, shot.EndTime);
            }
        }

        if (shotCount == 0) return null;

        // Calculate confidence based on indicators
        var avgTextDensity = textDensitySum / shotCount;
        var darkRatio = (double)darkBackgroundCount / shotCount;

        var confidence = CalculateTitleConfidence(avgTextDensity, darkRatio, titleShots.Count);

        if (confidence < 0.3 || titleEndTime < MinTitleDuration)
            return null;

        _logger.LogInformation(
            "Detected title sequence: 0:00 - {EndTime:F1}s, confidence={Confidence:F2}",
            titleEndTime, confidence);

        return new TitleCreditsSegment
        {
            Type = SequenceType.Title,
            StartTime = 0,
            EndTime = titleEndTime,
            Confidence = confidence,
            ShotCount = titleShots.Count(s => s.EndTime <= titleEndTime),
            TextDensity = avgTextDensity,
            DarkBackgroundRatio = darkRatio
        };
    }

    /// <summary>
    /// Detect potential credits sequence at the end of the video.
    /// Credits typically have scrolling text, dark backgrounds, and high text density.
    /// </summary>
    private TitleCreditsSegment? DetectCreditsSequence(VideoContext context, double windowStart, double duration)
    {
        // Get shots in credits window
        var creditsShots = context.Shots
            .Where(s => s.EndTime > windowStart)
            .OrderBy(s => s.StartTime)
            .ToList();

        if (creditsShots.Count == 0)
            return null;

        // Calculate credits characteristics
        var creditsStartTime = duration;
        var textDensitySum = 0.0;
        var darkBackgroundCount = 0;
        var verticalMotionCount = 0;
        var shotCount = 0;

        foreach (var shot in creditsShots)
        {
            var profile = context.GetCached<dynamic>($"image_profile.{shot.KeyframeIndex}");
            if (profile == null) continue;

            shotCount++;

            // Check for high text likeliness
            var textLikeliness = GetSignalValue<double>(profile, "content.text_likeliness") ?? 0;
            textDensitySum += textLikeliness;

            // Check for dark background
            var avgLuminance = GetSignalValue<double>(profile, "color.luminance.mean") ?? 0.5;
            if (avgLuminance < 0.3)
                darkBackgroundCount++;

            // Check for vertical motion (scrolling credits)
            var motionDirection = GetSignalValue<string>(profile, "motion.direction") ?? "";
            if (motionDirection.Contains("up", StringComparison.OrdinalIgnoreCase) ||
                motionDirection.Contains("down", StringComparison.OrdinalIgnoreCase))
                verticalMotionCount++;

            // If we have text + dark background, this is likely credits
            var ocrText = GetSignalValue<string>(profile, "ocr.text") ?? "";
            if (!string.IsNullOrEmpty(ocrText) && ocrText.Length > 20 && avgLuminance < 0.3)
            {
                creditsStartTime = Math.Min(creditsStartTime, shot.StartTime);
            }
        }

        if (shotCount == 0) return null;

        // Calculate confidence
        var avgTextDensity = textDensitySum / shotCount;
        var darkRatio = (double)darkBackgroundCount / shotCount;
        var verticalMotionRatio = (double)verticalMotionCount / shotCount;

        var confidence = CalculateCreditsConfidence(avgTextDensity, darkRatio, verticalMotionRatio);

        var creditsDuration = duration - creditsStartTime;
        if (confidence < 0.3 || creditsDuration < MinCreditsDuration)
            return null;

        _logger.LogInformation(
            "Detected credits sequence: {StartTime:F1}s - {Duration:F1}s, confidence={Confidence:F2}",
            creditsStartTime, duration, confidence);

        return new TitleCreditsSegment
        {
            Type = SequenceType.Credits,
            StartTime = creditsStartTime,
            EndTime = duration,
            Confidence = confidence,
            ShotCount = creditsShots.Count(s => s.StartTime >= creditsStartTime),
            TextDensity = avgTextDensity,
            DarkBackgroundRatio = darkRatio,
            HasScrollingText = verticalMotionRatio > 0.3
        };
    }

    private double CalculateTitleConfidence(double textDensity, double darkRatio, int shotCount)
    {
        // Title indicators:
        // - Some text (titles, show name) but not overwhelming
        // - May have dark backgrounds with text
        // - Usually multiple short shots (montage)

        var confidence = 0.0;

        // Text density: titles have moderate text
        if (textDensity > 0.1 && textDensity < 0.6)
            confidence += 0.3;
        else if (textDensity > 0.05)
            confidence += 0.1;

        // Dark backgrounds suggest stylized title cards
        if (darkRatio > 0.5)
            confidence += 0.2;

        // Multiple short shots suggest title montage
        if (shotCount >= 3)
            confidence += 0.2;

        // Bonus for very stylized openings
        if (darkRatio > 0.7 && textDensity > 0.3)
            confidence += 0.2;

        return Math.Min(confidence, 1.0);
    }

    private double CalculateCreditsConfidence(double textDensity, double darkRatio, double verticalMotionRatio)
    {
        // Credits indicators:
        // - High text density (lots of names)
        // - Dark background (typically black)
        // - Vertical scrolling motion

        var confidence = 0.0;

        // High text density is a strong indicator
        if (textDensity > 0.5)
            confidence += 0.35;
        else if (textDensity > 0.3)
            confidence += 0.2;

        // Dark background is typical
        if (darkRatio > 0.8)
            confidence += 0.25;
        else if (darkRatio > 0.5)
            confidence += 0.15;

        // Vertical motion (scrolling) is very strong indicator
        if (verticalMotionRatio > 0.5)
            confidence += 0.3;
        else if (verticalMotionRatio > 0.2)
            confidence += 0.15;

        return Math.Min(confidence, 1.0);
    }

    /// <summary>
    /// Mark shots in title/credits sequences for enhanced OCR processing.
    /// </summary>
    private void MarkShotsForEnhancedOcr(
        VideoContext context,
        TitleCreditsSegment? titleSequence,
        TitleCreditsSegment? creditsSequence)
    {
        foreach (var shot in context.Shots)
        {
            var needsEnhancedOcr = false;
            var sequenceType = SequenceType.None;

            if (titleSequence != null &&
                shot.StartTime >= titleSequence.StartTime &&
                shot.EndTime <= titleSequence.EndTime)
            {
                needsEnhancedOcr = true;
                sequenceType = SequenceType.Title;
            }
            else if (creditsSequence != null &&
                     shot.StartTime >= creditsSequence.StartTime &&
                     shot.EndTime <= creditsSequence.EndTime)
            {
                needsEnhancedOcr = true;
                sequenceType = SequenceType.Credits;
            }

            if (needsEnhancedOcr)
            {
                context.SetCached($"shot_needs_enhanced_ocr.{shot.Id}", true);
                context.SetCached($"shot_sequence_type.{shot.Id}", sequenceType.ToString());
            }
        }
    }

    private void AddDetectionSignals(
        VideoContext context,
        TitleCreditsSegment? titleSequence,
        TitleCreditsSegment? creditsSequence)
    {
        // Title sequence signals
        if (titleSequence != null)
        {
            context.AddSignals([
                new VideoSignal
                {
                    Key = "title_sequence.detected",
                    Value = true,
                    Confidence = titleSequence.Confidence,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "title_sequence.start_time",
                    Value = titleSequence.StartTime,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "title_sequence.end_time",
                    Value = titleSequence.EndTime,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "title_sequence.shot_count",
                    Value = titleSequence.ShotCount,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                }
            ]);

            // Store segment in context
            context.SetCached("title_sequence", titleSequence);
        }
        else
        {
            context.AddSignal(new VideoSignal
            {
                Key = "title_sequence.detected",
                Value = false,
                Confidence = 0.8,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            });
        }

        // Credits sequence signals
        if (creditsSequence != null)
        {
            context.AddSignals([
                new VideoSignal
                {
                    Key = "credits_sequence.detected",
                    Value = true,
                    Confidence = creditsSequence.Confidence,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "credits_sequence.start_time",
                    Value = creditsSequence.StartTime,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "credits_sequence.end_time",
                    Value = creditsSequence.EndTime,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "credits_sequence.shot_count",
                    Value = creditsSequence.ShotCount,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                },
                new VideoSignal
                {
                    Key = "credits_sequence.has_scrolling_text",
                    Value = creditsSequence.HasScrollingText,
                    Source = Name,
                    Tags = [VideoSignalTags.Visual]
                }
            ]);

            // Store segment in context
            context.SetCached("credits_sequence", creditsSequence);
        }
        else
        {
            context.AddSignal(new VideoSignal
            {
                Key = "credits_sequence.detected",
                Value = false,
                Confidence = 0.8,
                Source = Name,
                Tags = [VideoSignalTags.Visual]
            });
        }

        // Add count of shots needing enhanced OCR
        var shotsNeedingOcr = context.Shots.Count(s =>
            context.GetCached<bool>($"shot_needs_enhanced_ocr.{s.Id}"));

        context.AddSignal(new VideoSignal
        {
            Key = "title_credits.shots_needing_enhanced_ocr",
            Value = shotsNeedingOcr,
            Source = Name,
            Tags = [VideoSignalTags.Visual]
        });

        _logger.LogInformation(
            "Title/credits detection complete: title={HasTitle}, credits={HasCredits}, shots needing OCR={OcrCount}",
            titleSequence != null,
            creditsSequence != null,
            shotsNeedingOcr);
    }

    private T? GetSignalValue<T>(dynamic? profile, string key)
    {
        if (profile == null) return default;

        try
        {
            // Profile is an ImageProfile from ImageSummarizer
            return profile.GetValue<T>(key);
        }
        catch
        {
            return default;
        }
    }
}

/// <summary>
/// Type of title/credits sequence.
/// </summary>
public enum SequenceType
{
    None,
    Title,
    Credits
}

/// <summary>
/// Detected title or credits sequence information.
/// </summary>
public record TitleCreditsSegment
{
    public SequenceType Type { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public double Confidence { get; init; }
    public int ShotCount { get; init; }
    public double TextDensity { get; init; }
    public double DarkBackgroundRatio { get; init; }
    public bool HasScrollingText { get; init; }
}
