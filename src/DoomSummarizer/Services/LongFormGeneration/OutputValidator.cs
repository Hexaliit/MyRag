using System.Text.RegularExpressions;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 5: Output Validation (Deterministic — No LLM).
/// Validates every URL, entity, and factual claim in the generated text
/// against the evidence corpus. Detects hallucinated URLs, fabricated sources,
/// and ungrounded facts using embedding similarity.
/// </summary>
public static class OutputValidator
{
    // Match markdown links [text](url) and bare URLs
    private static readonly Regex UrlRegex = new(
        @"(?:\[([^\]]*)\]\((https?://[^\s\)]+)\))|(https?://[^\s\)>\]]+)",
        RegexOptions.Compiled);

    // Match source references: "according to [Title]", "reported by [Title]"
    private static readonly Regex TitleRefRegex = new(
        @"(?:according to|reported by|says|notes|per|from)\s+\[?([A-Z][^,.\[\]]{3,60})\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Google News redirect pattern
    private static readonly Regex GoogleRedirectRegex = new(
        @"https?://news\.google\.com/rss/articles/",
        RegexOptions.Compiled);

    // Transitional/structural sentence patterns (exempt from grounding)
    private static readonly Regex TransitionalRegex = new(
        @"^(In this section|As we|Moving on|Let's|Now,|Here,|Below|Above|First|Second|Finally|In conclusion|To summarize|Overall|In summary)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Run all validation checks on the generated document.
    /// </summary>
    public static ValidationResult Validate(
        DocumentPlan plan,
        EvidenceCorpus corpus,
        Func<string, float[]> embedder)
    {
        var allText = string.Join("\n\n",
            plan.Sections
                .Where(s => s.GeneratedContent != null)
                .Select(s => s.GeneratedContent!));

        var urlResult = ValidateUrls(allText, corpus);
        var entityResult = ValidateEntities(allText, corpus);
        var titleResult = ValidateTitles(allText, corpus);
        var factResult = ValidateFacts(plan, corpus, embedder);

        var totalSentences = factResult.TotalSentences;
        var groundedSentences = factResult.GroundedCount;
        var groundingScore = totalSentences > 0
            ? (double)groundedSentences / totalSentences
            : 1.0;

        var passesValidation = groundingScore >= 0.7
            && urlResult.HallucinatedUrls.Count == 0;

        return new ValidationResult
        {
            HallucinatedUrls = urlResult.HallucinatedUrls,
            UngroundedEntities = entityResult.UngroundedEntities,
            FabricatedTitles = titleResult.FabricatedTitles,
            UngroundedFacts = factResult.UngroundedFacts,
            OverallGroundingScore = groundingScore,
            PassesValidation = passesValidation,
            VerifiedUrlCount = urlResult.VerifiedCount,
            GroundedEntityCount = entityResult.GroundedCount
        };
    }

    /// <summary>
    /// Apply auto-fix strategies to the generated sections.
    /// </summary>
    public static void AutoFix(DocumentPlan plan, ValidationResult validation, EvidenceCorpus corpus)
    {
        foreach (var section in plan.Sections.Where(s => s.GeneratedContent != null))
        {
            var text = section.GeneratedContent!;

            // Remove hallucinated URLs — keep link text, strip the URL
            foreach (var issue in validation.HallucinatedUrls)
            {
                var markdownLink = $"[{issue.Context}]({issue.Url})";
                if (text.Contains(markdownLink))
                {
                    text = text.Replace(markdownLink, issue.Context);
                }
                else
                {
                    // Bare URL removal
                    text = text.Replace(issue.Url, "");
                }
            }

            // Replace fabricated titles with closest real match
            foreach (var issue in validation.FabricatedTitles.Where(t => t.ClosestRealTitle != null))
            {
                text = text.Replace(issue.ClaimedTitle, issue.ClosestRealTitle!);
            }

            section.GeneratedContent = text;
        }
    }

    private static (List<UrlIssue> HallucinatedUrls, int VerifiedCount) ValidateUrls(
        string text, EvidenceCorpus corpus)
    {
        var hallucinated = new List<UrlIssue>();
        var verifiedCount = 0;

        foreach (Match match in UrlRegex.Matches(text))
        {
            var linkText = match.Groups[1].Success ? match.Groups[1].Value : "";
            var url = match.Groups[2].Success ? match.Groups[2].Value
                    : match.Groups[3].Success ? match.Groups[3].Value : "";

            if (string.IsNullOrEmpty(url)) continue;

            if (GoogleRedirectRegex.IsMatch(url))
            {
                hallucinated.Add(new UrlIssue
                {
                    Url = url,
                    Context = linkText,
                    Type = UrlIssueType.GoogleRedirect
                });
                continue;
            }

            // Check against whitelist
            if (corpus.UrlFetchDates.ContainsKey(url))
            {
                verifiedCount++;
            }
            else
            {
                // Check if it's a prefix/variant match (trailing slashes, query params)
                var isKnown = corpus.UrlFetchDates.Keys
                    .Any(k => url.StartsWith(k, StringComparison.OrdinalIgnoreCase)
                           || k.StartsWith(url, StringComparison.OrdinalIgnoreCase));

                if (isKnown)
                    verifiedCount++;
                else
                    hallucinated.Add(new UrlIssue
                    {
                        Url = url,
                        Context = linkText,
                        Type = UrlIssueType.Hallucinated
                    });
            }
        }

        return (hallucinated, verifiedCount);
    }

    private static (List<EntityIssue> UngroundedEntities, int GroundedCount) ValidateEntities(
        string text, EvidenceCorpus corpus)
    {
        var ungrounded = new List<EntityIssue>();
        var groundedCount = 0;

        // Extract capitalized multi-word phrases from generated text
        var entityRegex = new Regex(
            @"\b([A-Z][a-z]+(?:\s+(?:[A-Z][a-z]+|of|the|and|for))*(?:\s+[A-Z][a-z]+)+)\b");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in entityRegex.Matches(text))
        {
            var entity = match.Value.Trim();
            if (entity.Length < 4 || seen.Contains(entity)) continue;
            seen.Add(entity);

            if (corpus.KnownEntityNames.Contains(entity.ToLowerInvariant()))
            {
                groundedCount++;
            }
            else
            {
                // Fuzzy match: check if any known entity is close
                var closest = corpus.KnownEntityNames
                    .Select(k => (Name: k, Distance: LevenshteinDistance(entity.ToLowerInvariant(), k)))
                    .Where(x => x.Distance <= 2)
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();

                if (closest.Name != null)
                {
                    groundedCount++;
                }
                else
                {
                    ungrounded.Add(new EntityIssue
                    {
                        EntityName = entity,
                        Context = GetSurroundingContext(text, match.Index, 50),
                        ClosestMatchScore = 0,
                        ClosestMatch = null
                    });
                }
            }
        }

        return (ungrounded, groundedCount);
    }

    private static (List<TitleIssue> FabricatedTitles, int VerifiedCount) ValidateTitles(
        string text, EvidenceCorpus corpus)
    {
        var fabricated = new List<TitleIssue>();
        var verifiedCount = 0;

        foreach (Match match in TitleRefRegex.Matches(text))
        {
            var title = match.Groups[1].Value.Trim();
            if (title.Length < 4) continue;

            if (corpus.KnownTitles.Contains(title))
            {
                verifiedCount++;
                continue;
            }

            // Fuzzy match against known titles
            var closest = corpus.KnownTitles
                .Select(t => (Title: t, Score: JaccardSimilarity(title, t)))
                .Where(x => x.Score > 0.3)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (closest.Score > 0.6)
            {
                verifiedCount++;
            }
            else
            {
                fabricated.Add(new TitleIssue
                {
                    ClaimedTitle = title,
                    Context = GetSurroundingContext(text, match.Index, 60),
                    ClosestRealTitle = closest.Title,
                    MatchScore = closest.Score
                });
            }
        }

        return (fabricated, verifiedCount);
    }

    private static (List<FactIssue> UngroundedFacts, int TotalSentences, int GroundedCount) ValidateFacts(
        DocumentPlan plan, EvidenceCorpus corpus, Func<string, float[]> embedder)
    {
        var ungrounded = new List<FactIssue>();
        var totalSentences = 0;
        var groundedCount = 0;

        // Pre-compute: only embed evidence segments that have embeddings
        var evidenceEmbeddings = corpus.Segments
            .Where(s => s.Segment.Embedding != null)
            .ToList();

        for (var si = 0; si < plan.Sections.Count; si++)
        {
            var section = plan.Sections[si];
            if (section.GeneratedContent == null) continue;

            var sentences = SplitIntoSentences(section.GeneratedContent);
            foreach (var sentence in sentences)
            {
                if (sentence.Length < 15) continue;
                if (TransitionalRegex.IsMatch(sentence)) continue;

                totalSentences++;

                var sentenceEmb = embedder(sentence);
                var bestSim = 0f;

                foreach (var evidence in evidenceEmbeddings)
                {
                    var sim = EmbeddingService.CosineSimilarity(sentenceEmb, evidence.Segment.Embedding!);
                    if (sim > bestSim) bestSim = sim;
                    if (bestSim > 0.6f) break; // Early exit — grounded
                }

                FactGroundingLevel level;
                if (bestSim > 0.6f)
                {
                    level = FactGroundingLevel.Grounded;
                    groundedCount++;
                }
                else if (bestSim > 0.4f)
                {
                    level = FactGroundingLevel.WeaklyGrounded;
                    groundedCount++; // Weakly grounded still counts
                }
                else
                {
                    level = FactGroundingLevel.Ungrounded;
                    ungrounded.Add(new FactIssue
                    {
                        Sentence = sentence,
                        SectionIndex = si,
                        BestGroundingScore = bestSim,
                        Level = level
                    });
                }
            }
        }

        return (ungrounded, totalSentences, groundedCount);
    }

    private static List<string> SplitIntoSentences(string text)
    {
        // Split on sentence-ending punctuation followed by space or end
        return Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(s => s.Length > 10)
            .Select(s => s.Trim())
            .ToList();
    }

    private static string GetSurroundingContext(string text, int position, int radius)
    {
        var start = Math.Max(0, position - radius);
        var end = Math.Min(text.Length, position + radius);
        return text[start..end];
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var m = s.Length;
        var n = t.Length;
        var d = new int[m + 1, n + 1];

        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[m, n];
    }

    private static double JaccardSimilarity(string a, string b)
    {
        var wordsA = a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (double)intersection / union : 0;
    }
}
