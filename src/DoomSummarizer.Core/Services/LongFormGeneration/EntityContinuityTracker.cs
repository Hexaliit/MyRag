using System.Text;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
///     Deterministic entity tracking across sections.
///     Uses string matching against the evidence corpus's known entities.
///     Provides guidance to section prompts about entity continuity.
/// </summary>
public class EntityContinuityTracker
{
    private readonly Dictionary<string, EntityTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Initialize from the evidence corpus's global entities.
    /// </summary>
    public void Initialize(EvidenceCorpus corpus)
    {
        foreach (var entity in corpus.GlobalEntities.Take(30))
            _tracks[entity.NormalizedName] = new EntityTrack
            {
                Name = entity.Name,
                NormalizedName = entity.NormalizedName,
                EvidenceMentions = entity.MentionCount,
                LastMentionedSection = -1
            };
    }

    /// <summary>
    ///     Scan a section's assigned evidence for entity mentions.
    ///     Call this after evidence is assigned but before section generation.
    /// </summary>
    public void ScanEvidence(PlannedSection section, int sectionIndex)
    {
        foreach (var evidence in section.AssignedEvidence)
        {
            var text = evidence.Segment.Text;
            foreach (var (name, track) in _tracks)
                if (text.Contains(track.Name, StringComparison.OrdinalIgnoreCase))
                {
                    track.SectionMentions.Add(sectionIndex);
                    track.LastMentionedSection = sectionIndex;
                }
        }
    }

    /// <summary>
    ///     Scan generated section text for entity mentions.
    ///     Call after section text is generated.
    /// </summary>
    public void ScanGenerated(string generatedText, int sectionIndex)
    {
        foreach (var (_, track) in _tracks)
            if (generatedText.Contains(track.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (!track.SectionMentions.Contains(sectionIndex))
                    track.SectionMentions.Add(sectionIndex);
                track.LastMentionedSection = sectionIndex;
            }
    }

    /// <summary>
    ///     Build entity continuity guidance for the next section prompt.
    ///     Tells the LLM which entities to maintain continuity with.
    /// </summary>
    public string BuildGuidance(int currentSectionIndex)
    {
        var reintroduce = _tracks.Values
            .Where(t => t.SectionMentions.Count > 0
                        && t.LastMentionedSection >= 0
                        && t.LastMentionedSection < currentSectionIndex - 1)
            .OrderByDescending(t => t.EvidenceMentions)
            .Take(3)
            .ToList();

        var active = _tracks.Values
            .Where(t => t.SectionMentions.Count > 0
                        && t.LastMentionedSection >= currentSectionIndex - 1)
            .OrderByDescending(t => t.EvidenceMentions)
            .Take(5)
            .ToList();

        if (reintroduce.Count == 0 && active.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("KEY ENTITIES (maintain continuity):");

        if (reintroduce.Count > 0)
        {
            sb.AppendLine("Re-introduce if relevant to this section:");
            foreach (var e in reintroduce)
                sb.AppendLine($"  - {e.Name} (last mentioned: section {e.LastMentionedSection + 1})");
        }

        if (active.Count > 0)
        {
            sb.AppendLine("Active entities (referenced recently):");
            foreach (var e in active)
                sb.AppendLine($"  - {e.Name}");
        }

        return sb.ToString();
    }

    private class EntityTrack
    {
        public string Name { get; init; } = "";
        public string NormalizedName { get; init; } = "";
        public int EvidenceMentions { get; init; }
        public int LastMentionedSection { get; set; } = -1;
        public List<int> SectionMentions { get; } = [];
    }
}