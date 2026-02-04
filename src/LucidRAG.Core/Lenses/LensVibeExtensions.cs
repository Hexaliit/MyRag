using DoomSummarizer.Models;
using LucidRAG.Lenses;

namespace LucidRAG.Services.Lenses;

/// <summary>
///     Extension methods that bridge LucidRAG lenses and DoomSummarizer vibes.
///     A lens's personality section maps directly to a vibe, closing the loop
///     between the two systems: a vibe IS the personality facet of a lens.
/// </summary>
public static class LensVibeExtensions
{
    /// <summary>
    ///     Convert a LensPackage to a DoomSummarizer Vibe.
    ///     Extracts the personality configuration and builds the prompt instruction.
    /// </summary>
    public static Vibe ToVibe(this LensPackage lens)
    {
        var personality = lens.Manifest.Personality;
        var prompt = personality != null
            ? BuildVibePrompt(personality)
            : lens.Manifest.Description;

        return new Vibe
        {
            Name = lens.Manifest.Id,
            Prompt = prompt,
            LensId = lens.Manifest.Id,
            Tone = personality?.Tone,
            SpellingVariant = personality?.SpellingVariant,
            Persona = personality?.Persona,
            StyleNotes = personality?.StyleNotes?.AsReadOnly(),
            PhrasePreferences = personality?.PhrasePreferences != null
                ? new Dictionary<string, string>(personality.PhrasePreferences)
                : null
        };
    }

    /// <summary>
    ///     Export all registered lenses as vibes.
    /// </summary>
    public static IReadOnlyList<Vibe> ToVibes(this ILensRegistry registry)
    {
        return registry.AvailableLenses.Select(l => l.ToVibe()).ToList();
    }

    private static string BuildVibePrompt(LensPersonality personality)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(personality.Tone))
            parts.Add($"Tone: {personality.Tone}.");

        if (!string.IsNullOrEmpty(personality.Persona))
            parts.Add($"Write as {personality.Persona}.");

        if (personality.StyleNotes?.Count > 0)
            parts.AddRange(personality.StyleNotes);

        if (!string.IsNullOrEmpty(personality.SpellingVariant))
            parts.Add($"Use {personality.SpellingVariant} spelling.");

        if (personality.PhrasePreferences?.Count > 0)
            foreach (var (avoid, use) in personality.PhrasePreferences)
                if (string.IsNullOrEmpty(use))
                    parts.Add($"Avoid \"{avoid}\".");
                else
                    parts.Add($"Use \"{use}\" instead of \"{avoid}\".");

        return parts.Count > 0
            ? string.Join(" ", parts)
            : "Objective, balanced. Just the facts.";
    }
}