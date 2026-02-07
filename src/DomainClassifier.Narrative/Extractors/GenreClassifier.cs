namespace DomainClassifier.Narrative.Extractors;

/// <summary>
///     Keyword-based sub-classifier for narrative genre detection.
///     Returns the most likely genre with confidence.
/// </summary>
public static class GenreClassifier
{
    private static readonly Dictionary<string, string[]> GenreKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mystery"] =
        [
            "detective", "clue", "suspect", "murder", "crime", "investigate",
            "evidence", "alibi", "mystery", "whodunit", "sleuth", "inspector",
            "corpse", "victim", "witness", "motive", "deduction", "case"
        ],
        ["romance"] =
        [
            "love", "heart", "passion", "kiss", "marriage", "courtship",
            "affection", "beloved", "devotion", "embrace", "tender", "wedding",
            "suitor", "admirer", "handsome", "beautiful", "charming", "ball",
            "dance", "proposal"
        ],
        ["sci-fi"] =
        [
            "spaceship", "alien", "planet", "galaxy", "robot", "android",
            "laser", "warp", "teleport", "technology", "cybernetic", "mutation",
            "experiment", "laboratory", "creation", "creature", "scientific",
            "galvanism", "electricity", "unnatural"
        ],
        ["horror"] =
        [
            "horror", "terror", "dread", "fear", "nightmare", "monster",
            "creature", "demon", "ghost", "haunted", "darkness", "scream",
            "blood", "death", "corpse", "grave", "tomb", "gothic",
            "hideous", "wretched", "fiend", "daemon", "agony", "misery"
        ],
        ["fantasy"] =
        [
            "magic", "wizard", "dragon", "spell", "enchantment", "sword",
            "quest", "kingdom", "throne", "prophecy", "sorcerer", "fairy",
            "elf", "dwarf", "ogre", "mythical", "realm", "legend"
        ],
        ["historical"] =
        [
            "king", "queen", "war", "battle", "empire", "throne",
            "medieval", "ancient", "dynasty", "conquest", "revolution",
            "colonial", "victorian", "regency", "aristocracy", "nobility"
        ],
        ["literary"] =
        [
            "society", "manners", "propriety", "reputation", "estate",
            "gentleman", "lady", "fortune", "inheritance", "class",
            "observation", "irony", "wit", "sensibility", "disposition",
            "temperament", "virtue", "pride", "prejudice"
        ]
    };

    /// <summary>
    ///     Classify the genre of narrative text.
    ///     Returns the top genre with confidence score.
    /// </summary>
    public static (string Genre, double Confidence) Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("unknown", 0.0);

        var textLower = text.ToLowerInvariant();
        var scores = new Dictionary<string, int>();

        foreach (var (genre, keywords) in GenreKeywords)
        {
            var hits = 0;
            foreach (var keyword in keywords)
                if (textLower.Contains(keyword))
                    hits++;

            scores[genre] = hits;
        }

        var best = scores.MaxBy(kv => kv.Value);

        if (best.Value == 0)
            return ("unknown", 0.0);

        // Normalize: confidence based on how many keywords matched vs total
        var maxPossible = GenreKeywords[best.Key].Length;
        var confidence = Math.Min(1.0, (double)best.Value / maxPossible * 2.0);

        return (best.Key, Math.Round(confidence, 4));
    }

    /// <summary>
    ///     Classify and return all genre scores above zero for multi-genre works.
    /// </summary>
    public static List<(string Genre, double Confidence)> ClassifyAll(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var textLower = text.ToLowerInvariant();
        var results = new List<(string Genre, double Confidence)>();

        foreach (var (genre, keywords) in GenreKeywords)
        {
            var hits = 0;
            foreach (var keyword in keywords)
                if (textLower.Contains(keyword))
                    hits++;

            if (hits > 0)
            {
                var maxPossible = keywords.Length;
                var confidence = Math.Min(1.0, (double)hits / maxPossible * 2.0);
                results.Add((genre, Math.Round(confidence, 4)));
            }
        }

        return results.OrderByDescending(r => r.Confidence).ToList();
    }
}
