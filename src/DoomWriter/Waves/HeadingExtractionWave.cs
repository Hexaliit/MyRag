using System.Text.RegularExpressions;
using DoomWriter.Models;
using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Waves;

/// <summary>
///     Extracts markdown headings (H1-H6) for TOC display.
///     Fast lane, high priority — other waves depend on heading structure.
/// </summary>
public sealed partial class HeadingExtractionWave : ITypedAnalysisWave<string>
{
    public string Name => "doc.headings";
    public string Description => "Extract markdown headings for TOC";
    public int Priority => 90;
    public IReadOnlyList<string> Tags => [SignalTags.Structure];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(string content, AnalysisContext context)
    {
        return !string.IsNullOrWhiteSpace(content);
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string markdown, AnalysisContext context, CancellationToken ct = default)
    {
        var headings = new List<HeadingItem>();
        var lines = markdown.Split('\n');
        var charOffset = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var match = HeadingRegex().Match(line);
            if (match.Success)
                headings.Add(new HeadingItem
                {
                    Level = match.Groups[1].Value.Length,
                    Text = match.Groups[2].Value.Trim(),
                    LineNumber = i + 1,
                    CharOffset = charOffset
                });
            charOffset += lines[i].Length + 1;
        }

        // Cache headings for downstream waves (topic inference, etc.)
        context.SetCached("headings", headings);

        var signals = new List<Signal>
        {
            new()
            {
                Key = "doc.structure.headings",
                Value = headings,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Structure]
            },
            new()
            {
                Key = "doc.structure.heading_count",
                Value = headings.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Structure]
            }
        };

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();
}