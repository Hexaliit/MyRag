using Mostlylucid.Summarizer.Core.Analysis;
using DoomWriter.Models;

namespace DoomWriter.Waves;

/// <summary>
/// Infers document topics from headings and segment salience.
/// Fast lane — uses heading text as topic proxy for nearby segments.
/// Lower priority — runs after headings and segments are extracted.
/// </summary>
public sealed class TopicInferenceWave : ITypedAnalysisWave<string>
{
    public string Name => "doc.topics";
    public string Description => "Infer topics from headings and segments";
    public int Priority => 50;
    public IReadOnlyList<string> Tags => [SignalTags.Topic, SignalTags.Semantic];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(string content, AnalysisContext context) =>
        context.HasCached("headings") && context.HasCached("segments");

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string markdown, AnalysisContext context, CancellationToken ct = default)
    {
        var headings = context.GetCached<List<HeadingItem>>("headings") ?? [];
        var segments = context.GetCached<List<AnalyzedSegment>>("segments") ?? [];

        var topics = new List<TopicScore>();

        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var nextHeadingOffset = i + 1 < headings.Count ? headings[i + 1].CharOffset : int.MaxValue;

            var sectionSegments = segments.Where(s =>
                s.CharOffset >= heading.CharOffset && s.CharOffset < nextHeadingOffset).ToList();

            if (sectionSegments.Count > 0)
            {
                topics.Add(new TopicScore
                {
                    Topic = heading.Text,
                    Score = sectionSegments.Average(s => s.Salience),
                    SectionIndex = i
                });
            }
        }

        var dominantTopic = topics.Count > 0
            ? topics.MaxBy(t => t.Score)?.Topic ?? ""
            : "";

        var signals = new List<Signal>
        {
            new()
            {
                Key = "doc.topics.inferred",
                Value = topics,
                Confidence = 0.7, // Heading-based inference is moderate confidence
                Source = Name,
                Tags = [SignalTags.Topic]
            },
            new()
            {
                Key = "doc.topics.dominant",
                Value = dominantTopic,
                Confidence = topics.Count > 0 ? 0.7 : 0.0,
                Source = Name,
                Tags = [SignalTags.Topic]
            }
        };

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }
}
