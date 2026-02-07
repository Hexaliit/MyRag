using System.Text.RegularExpressions;
using DomainClassifier.Core.Models;

namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Extracts character names from narrative text using dialogue attribution,
///     title patterns, and introduction patterns.
/// </summary>
public static partial class CharacterExtractor
{
    // Common English words that look like proper nouns but aren't characters
    private static readonly HashSet<string> FalsePositives = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "Then", "There", "These", "Those", "Thus",
        "Here", "Where", "When", "What", "Which", "While", "Although",
        "However", "Perhaps", "Indeed", "Chapter", "Part", "Act", "Scene",
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "English", "French", "German", "Italian", "Spanish", "American", "British",
        "London", "Paris", "England", "France", "God", "Heaven", "Earth",
        "North", "South", "East", "West", "Church", "Dear", "Sir", "Madam"
    };

    // Pattern 1: After-verb attribution: "said Elizabeth", "Mr. Darcy replied"
    [GeneratedRegex(
        @"(?:said|replied|exclaimed|whispered|murmured|shouted|cried|sighed|gasped|stammered|muttered|answered|asked|demanded|declared|remarked|observed|continued|added|interrupted|protested|insisted|suggested|agreed|admitted|confessed)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex AfterVerbAttributionRegex();

    // Pattern 2: Before-verb attribution: "Elizabeth said", "Mr. Darcy replied"
    [GeneratedRegex(
        @"([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)\s+(?:said|replied|exclaimed|whispered|murmured|shouted|cried|sighed|gasped|stammered|muttered|answered|asked|demanded|declared|remarked|observed|continued|added|interrupted|protested|insisted|suggested|agreed|admitted|confessed)",
        RegexOptions.Compiled)]
    private static partial Regex BeforeVerbAttributionRegex();

    // Pattern 3: Title + Name: Mr.|Mrs.|Miss|Lord|Lady|Sir|Dr.|Captain|Colonel + proper noun
    [GeneratedRegex(
        @"\b(Mr\.|Mrs\.|Miss|Ms\.|Lord|Lady|Sir|Dr\.|Captain|Colonel|Professor|King|Queen|Prince|Princess|Duke|Duchess|Count|Countess|Baron|Baroness)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex TitleNameRegex();

    // Pattern 4: Introduction: "named|called|known as" + proper noun
    [GeneratedRegex(
        @"(?:named|called|known\s+as|introduced\s+as|christened)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)",
        RegexOptions.Compiled)]
    private static partial Regex IntroductionRegex();

    public static List<DomainEntity> Extract(string text)
    {
        var characterMentions = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var titleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<DomainEntity>();

        // Pattern 1: After-verb
        foreach (Match match in AfterVerbAttributionRegex().Matches(text))
        {
            var name = match.Groups[1].Value.Trim();
            if (IsValidCharacterName(name))
                TrackMention(characterMentions, name, match.Index);
        }

        // Pattern 2: Before-verb
        foreach (Match match in BeforeVerbAttributionRegex().Matches(text))
        {
            var name = match.Groups[1].Value.Trim();
            if (IsValidCharacterName(name))
                TrackMention(characterMentions, name, match.Index);
        }

        // Pattern 3: Title + Name
        foreach (Match match in TitleNameRegex().Matches(text))
        {
            var title = match.Groups[1].Value;
            var name = match.Groups[2].Value.Trim();
            if (IsValidCharacterName(name))
            {
                var fullName = $"{title} {name}";
                TrackMention(characterMentions, name, match.Index);
                titleMap.TryAdd(name, fullName);
            }
        }

        // Pattern 4: Introduction
        foreach (Match match in IntroductionRegex().Matches(text))
        {
            var name = match.Groups[1].Value.Trim();
            if (IsValidCharacterName(name))
                TrackMention(characterMentions, name, match.Index);
        }

        // Convert mentions to entities with confidence based on frequency
        foreach (var (name, offsets) in characterMentions)
        {
            var confidence = offsets.Count switch
            {
                >= 10 => 0.95,
                >= 5 => 0.85,
                _ => 0.70
            };

            var displayName = titleMap.TryGetValue(name, out var titled) ? titled : name;

            entities.Add(new DomainEntity(
                Text: displayName,
                EntityType: "character",
                DomainId: "narrative",
                StartOffset: offsets[0],
                EndOffset: offsets[0] + name.Length,
                Confidence: confidence,
                Metadata: new Dictionary<string, object?>
                {
                    ["mentions"] = offsets.Count,
                    ["hasTitle"] = titleMap.ContainsKey(name)
                }));
        }

        return entities.OrderByDescending(e =>
            e.Metadata?.TryGetValue("mentions", out var val) == true && val is int count ? count : 0).ToList();
    }

    private static bool IsValidCharacterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            return false;
        if (FalsePositives.Contains(name))
            return false;
        // Must start with uppercase
        if (!char.IsUpper(name[0]))
            return false;
        return true;
    }

    private static void TrackMention(Dictionary<string, List<int>> mentions, string name, int offset)
    {
        if (!mentions.TryGetValue(name, out var offsets))
        {
            offsets = [];
            mentions[name] = offsets;
        }

        offsets.Add(offset);
    }
}
