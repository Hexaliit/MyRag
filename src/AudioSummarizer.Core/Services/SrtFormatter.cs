using System.Text;
using AudioSummarizer.Core.Services.Voice;

namespace AudioSummarizer.Core.Services;

/// <summary>
/// Formats transcription segments with optional speaker diarization into SRT subtitle format.
/// SRT format: sequence number, timecode --> timecode, text (with optional speaker label).
/// </summary>
public sealed class SrtFormatter
{
    /// <summary>
    /// Format transcript segments to SRT format without speaker labels.
    /// </summary>
    public string FormatToSrt(IEnumerable<TranscriptSegment> segments)
    {
        return FormatToSrt(segments, null);
    }

    /// <summary>
    /// Format transcript segments to SRT format with speaker labels from diarization.
    /// </summary>
    public string FormatToSrt(
        IEnumerable<TranscriptSegment> segments,
        IEnumerable<SpeakerTurn>? speakerTurns,
        bool includeSpeakerLabels = true)
    {
        var sb = new StringBuilder();
        var segmentList = segments.OrderBy(s => s.Start).ToList();
        var turnsList = speakerTurns?.OrderBy(t => t.StartSeconds).ToList() ?? [];

        var index = 1;
        foreach (var segment in segmentList)
        {
            // Skip empty segments
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            // Find speaker for this segment (if diarization available)
            string? speakerLabel = null;
            if (includeSpeakerLabels && turnsList.Count > 0)
            {
                speakerLabel = FindSpeakerForSegment(segment, turnsList);
            }

            // Sequence number
            sb.AppendLine(index.ToString());

            // Timecode line: 00:00:01,234 --> 00:00:05,678
            var startTime = FormatTimecode(segment.Start);
            var endTime = FormatTimecode(segment.End);
            sb.AppendLine($"{startTime} --> {endTime}");

            // Text (with optional speaker prefix)
            if (!string.IsNullOrEmpty(speakerLabel))
            {
                sb.AppendLine($"[{speakerLabel}]: {segment.Text.Trim()}");
            }
            else
            {
                sb.AppendLine(segment.Text.Trim());
            }

            // Blank line between entries
            sb.AppendLine();
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Format transcript segments to WebVTT format with optional speaker labels.
    /// </summary>
    public string FormatToWebVtt(
        IEnumerable<TranscriptSegment> segments,
        IEnumerable<SpeakerTurn>? speakerTurns = null,
        bool includeSpeakerLabels = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        var segmentList = segments.OrderBy(s => s.Start).ToList();
        var turnsList = speakerTurns?.OrderBy(t => t.StartSeconds).ToList() ?? [];

        foreach (var segment in segmentList)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            string? speakerLabel = null;
            if (includeSpeakerLabels && turnsList.Count > 0)
            {
                speakerLabel = FindSpeakerForSegment(segment, turnsList);
            }

            // WebVTT timecode uses period instead of comma for ms
            var startTime = FormatTimecode(segment.Start).Replace(',', '.');
            var endTime = FormatTimecode(segment.End).Replace(',', '.');
            sb.AppendLine($"{startTime} --> {endTime}");

            if (!string.IsNullOrEmpty(speakerLabel))
            {
                sb.AppendLine($"<v {speakerLabel}>{segment.Text.Trim()}");
            }
            else
            {
                sb.AppendLine(segment.Text.Trim());
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Merge transcript segments with speaker diarization to create speaker-labeled segments.
    /// </summary>
    public IEnumerable<DiarizedSegment> MergeWithDiarization(
        IEnumerable<TranscriptSegment> segments,
        IEnumerable<SpeakerTurn> speakerTurns)
    {
        var segmentList = segments.OrderBy(s => s.Start).ToList();
        var turnsList = speakerTurns.OrderBy(t => t.StartSeconds).ToList();

        foreach (var segment in segmentList)
        {
            var speaker = FindSpeakerForSegment(segment, turnsList);

            yield return new DiarizedSegment
            {
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                Confidence = segment.Confidence,
                SpeakerId = speaker
            };
        }
    }

    /// <summary>
    /// Parse SRT file content into transcript segments with optional speaker labels.
    /// </summary>
    public IEnumerable<DiarizedSegment> ParseSrt(string srtContent)
    {
        if (string.IsNullOrWhiteSpace(srtContent))
            yield break;

        var lines = srtContent.Split('\n', StringSplitOptions.None);
        int i = 0;

        while (i < lines.Length)
        {
            // Skip empty lines and look for sequence number
            while (i < lines.Length && !int.TryParse(lines[i].Trim(), out _))
            {
                i++;
            }

            if (i >= lines.Length)
                break;

            i++; // Move past sequence number

            // Parse timecode line
            if (i >= lines.Length)
                break;

            var timecodeLine = lines[i].Trim();
            if (!TryParseTimecodes(timecodeLine, out var start, out var end))
            {
                i++;
                continue;
            }

            i++; // Move past timecode

            // Collect text lines until blank line
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                textLines.Add(lines[i].Trim());
                i++;
            }

            if (textLines.Count == 0)
                continue;

            var fullText = string.Join(" ", textLines);

            // Check for speaker label: [SPEAKER_00]: text or SPEAKER_00: text
            string? speakerId = null;
            var text = fullText;

            if (fullText.StartsWith('['))
            {
                var closeBracket = fullText.IndexOf(']');
                if (closeBracket > 0)
                {
                    speakerId = fullText[1..closeBracket];
                    text = fullText[(closeBracket + 1)..].TrimStart(':', ' ');
                }
            }
            else if (fullText.Contains(':') && !fullText.Contains("://"))
            {
                var colonIndex = fullText.IndexOf(':');
                var potentialSpeaker = fullText[..colonIndex].Trim();
                if (potentialSpeaker.StartsWith("SPEAKER", StringComparison.OrdinalIgnoreCase) ||
                    potentialSpeaker.Length <= 20) // Reasonable speaker name length
                {
                    speakerId = potentialSpeaker;
                    text = fullText[(colonIndex + 1)..].Trim();
                }
            }

            yield return new DiarizedSegment
            {
                Start = start,
                End = end,
                Text = text,
                SpeakerId = speakerId
            };

            i++; // Move past blank line
        }
    }

    private static string? FindSpeakerForSegment(TranscriptSegment segment, List<SpeakerTurn> turns)
    {
        // Find the speaker turn that contains the midpoint of this segment
        var segmentMid = (segment.Start + segment.End) / 2.0;

        var turn = turns.FirstOrDefault(t =>
            t.StartSeconds <= segmentMid && t.EndSeconds >= segmentMid);

        if (turn != null)
            return turn.SpeakerId;

        // Fallback: find turn with most overlap
        double maxOverlap = 0;
        SpeakerTurn? bestTurn = null;

        foreach (var t in turns)
        {
            var overlapStart = Math.Max(segment.Start, t.StartSeconds);
            var overlapEnd = Math.Min(segment.End, t.EndSeconds);
            var overlap = Math.Max(0, overlapEnd - overlapStart);

            if (overlap > maxOverlap)
            {
                maxOverlap = overlap;
                bestTurn = t;
            }
        }

        return bestTurn?.SpeakerId;
    }

    private static string FormatTimecode(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static bool TryParseTimecodes(string line, out double start, out double end)
    {
        start = 0;
        end = 0;

        var parts = line.Split(" --> ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        return TryParseTimecode(parts[0].Trim(), out start) &&
               TryParseTimecode(parts[1].Trim(), out end);
    }

    private static bool TryParseTimecode(string timecode, out double seconds)
    {
        seconds = 0;

        // Format: HH:MM:SS,mmm or HH:MM:SS.mmm (WebVTT)
        timecode = timecode.Replace('.', ',');
        var parts = timecode.Split(',');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[1], out var ms))
            return false;

        var timeParts = parts[0].Split(':');
        if (timeParts.Length != 3)
            return false;

        if (!int.TryParse(timeParts[0], out var hours) ||
            !int.TryParse(timeParts[1], out var minutes) ||
            !int.TryParse(timeParts[2], out var secs))
            return false;

        seconds = hours * 3600 + minutes * 60 + secs + ms / 1000.0;
        return true;
    }
}

/// <summary>
/// Transcript segment with timing.
/// </summary>
public record TranscriptSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public required string Text { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// Transcript segment with speaker attribution.
/// </summary>
public record DiarizedSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public required string Text { get; init; }
    public double Confidence { get; init; }
    public string? SpeakerId { get; init; }
}
