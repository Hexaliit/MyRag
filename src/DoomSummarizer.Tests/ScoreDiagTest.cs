using DoomSummarizer.Services;
using Xunit;
using Xunit.Abstractions;

namespace DoomSummarizer.Tests;

public class ScoreDiagTest(ITestOutputHelper output)
{
    [Fact]
    public void DumpScoresForKeyScenarios()
    {
        var router = SourceRouter.Load();

        var scenarios = new (string Query, string Intent, Dictionary<string, double> Categories, string TimeSensitivity)[]
        {
            // === QA / Knowledge ===
            ("How much can a swallow carry?", "qa", new() { ["science"] = 0.8 }, "any"),
            ("What is the speed of light?", "qa", new() { ["science"] = 0.9 }, "any"),
            ("Who invented the telephone?", "qa", new() { ["science"] = 0.5 }, "any"),
            ("how do I cook risotto", "howto", new(), "any"),
            ("What is the capital of France?", "qa", new(), "any"),
            ("explain quantum entanglement", "deep_dive", new() { ["science"] = 0.9 }, "any"),
            ("weather in London", "search_only", new(), "any"),

            // === News ===
            ("latest AI news", "news", new() { ["ai"] = 0.9, ["technology"] = 0.5 }, "today"),
            ("stock market crash today", "news", new() { ["finance"] = 0.9, ["business"] = 0.7 }, "today"),
            ("new COVID variant spreading", "news", new() { ["health"] = 0.9 }, "today"),
            ("semiconductor export controls", "news", new() { ["business"] = 0.7, ["politics"] = 0.6, ["technology"] = 0.5 }, "today"),

            // === Breaking ===
            ("earthquake just hit Turkey", "roundup", new() { ["disaster"] = 0.9, ["world"] = 0.5 }, "breaking"),
            ("massive wildfire in California", "roundup", new() { ["disaster"] = 0.8, ["environment"] = 0.5 }, "breaking"),

            // === Research / Academic ===
            ("latest papers on transformer scaling", "research", new() { ["ai"] = 0.9 }, "any"),
            ("CRISPR gene editing clinical trials", "research", new() { ["health"] = 0.7, ["science"] = 0.9 }, "any"),

            // === Entertainment ===
            ("Who is hosting SNL this week?", "qa", new() { ["entertainment"] = 0.9 }, "any"),
            ("best movies of 2025", "roundup", new() { ["entertainment"] = 0.9 }, "any"),

            // === Politics / World ===
            ("UK parliament debate on immigration", "news", new() { ["politics"] = 0.9 }, "today"),
            ("US election polls 2028", "news", new() { ["politics"] = 0.9 }, "any"),

            // === Space ===
            ("SpaceX Starship launch", "news", new() { ["space"] = 0.9, ["technology"] = 0.4 }, "today"),
            ("James Webb telescope discoveries", "research", new() { ["space"] = 0.8, ["science"] = 0.7 }, "any"),

            // === Niche ===
            ("is Bigfoot real", "qa", new() { ["entertainment"] = 0.3 }, "any"),
            ("how to file UK taxes", "howto", new() { ["business"] = 0.5 }, "any"),
            ("best programming languages 2026", "roundup", new() { ["programming"] = 0.9, ["technology"] = 0.6 }, "any"),

            // === Historical / reference QA ===
            ("When was the French Revolution?", "qa", new(), "any"),
            ("What year did World War 2 end?", "qa", new(), "any"),

            // === Health QA (not news) ===
            ("What are the symptoms of diabetes?", "qa", new() { ["health"] = 0.9 }, "any"),

            // === Tech QA (not news) ===
            ("What is a REST API?", "qa", new() { ["technology"] = 0.7 }, "any"),

            // === Opinion / trend ===
            ("Is remote work better than office work?", "opinion", new() { ["business"] = 0.5 }, "any"),

            // === No categories at all ===
            ("something interesting", "news", new(), "any"),
        };

        var issues = new List<string>();

        foreach (var (query, intent, categories, timeSensitivity) in scenarios)
        {
            output.WriteLine($"\n{'=',0} \"{query}\" (intent={intent}, time={timeSensitivity})");
            output.WriteLine($"  Categories: {string.Join(", ", categories.Select(c => $"{c.Key}:{c.Value:F1}"))}");

            var scores = router.AllSources
                .Select(s => (Name: s, Score: router.ScoreSource(s, intent, categories, timeSensitivity)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            output.WriteLine("  Top 15 scores:");
            foreach (var (name, score) in scores.Take(15))
            {
                var src = router.GetSource(name)!;
                var caps = src.Capabilities != null ? string.Join(",", src.Capabilities) : "none";
                var aff = src.IntentAffinity?.GetValueOrDefault(intent, -1) ?? -1;
                var isSearch = src.Search ? " [SEARCH]" : "";
                output.WriteLine($"    {name,-20} {score:F3}  (affinity={aff:F2}, caps: {caps}){isSearch}");
            }

            var sentinelIntent = new SentinelIntent
            {
                Intent = intent,
                Categories = categories,
                TimeSensitivity = timeSensitivity,
                SearchQueries = [query.Replace("?", "").Trim()],
                Tone = "neutral"
            };

            var sources = SentinelSourceMapper.MapToSources(sentinelIntent, router, query);
            output.WriteLine($"  MapToSources ({sources.Count}):");
            for (var i = 0; i < sources.Count; i++)
                output.WriteLine($"    [{i}] {sources[i]}");

            // === Quality checks ===

            // QA should have search + knowledge sources before news
            if (intent is "qa" or "howto")
            {
                var hasSearch = sources.Any(s => s.StartsWith("search:"));
                if (!hasSearch)
                    issues.Add($"MISSING search for QA: \"{query}\"");

                var gnewsIdx = sources.FindIndex(s => s.StartsWith("gnews:"));
                var searchIdx = sources.FindIndex(s => s.StartsWith("search:"));
                if (gnewsIdx >= 0 && searchIdx >= 0 && gnewsIdx < searchIdx)
                    issues.Add($"gnews BEFORE search for QA: \"{query}\" (gnews@{gnewsIdx}, search@{searchIdx})");

                // QA should NOT waste slots on arxiv (academic papers don't answer trivia)
                var hasArxivForQA = sources.Any(s => s.StartsWith("arxiv"));
                if (hasArxivForQA)
                    issues.Add($"arxiv in QA results (wasteful): \"{query}\"");

                // Wikipedia should appear in top 3 for QA queries
                var wikiIdx = sources.FindIndex(s => s == "wikipedia");
                if (wikiIdx > 3)
                    issues.Add($"wikipedia too low for QA: \"{query}\" (position {wikiIdx}, want ≤3)");
            }

            // Research should include arxiv
            if (intent is "research" or "deep_dive")
            {
                var hasArxiv = sources.Any(s => s.StartsWith("arxiv"));
                if (!hasArxiv)
                    issues.Add($"MISSING arxiv for {intent}: \"{query}\"");
            }

            // Breaking/roundup+today should NOT have archive sources
            if (intent is "roundup" && timeSensitivity is "breaking" or "today")
            {
                var hasArchive = sources.Any(s => s is "wikipedia" || s.StartsWith("arxiv"));
                if (hasArchive)
                    issues.Add($"Archive source in breaking roundup: \"{query}\"");
            }

            // News should have gnews
            if (intent == "news")
            {
                var hasGnews = sources.Any(s => s.StartsWith("gnews:"));
                if (!hasGnews)
                    issues.Add($"MISSING gnews for news: \"{query}\"");
            }

            // Entertainment QA should NOT have tech sources
            if (intent is "qa" && categories.ContainsKey("entertainment") && !categories.ContainsKey("technology"))
            {
                var hasTech = sources.Any(s => s is "hn" or "lobsters" or "techcrunch");
                if (hasTech)
                    issues.Add($"Tech source in entertainment QA: \"{query}\"");
            }

            // Science QA should include wikipedia
            if (intent is "qa" && categories.ContainsKey("science") && categories["science"] >= 0.7)
            {
                var hasWiki = sources.Any(s => s == "wikipedia");
                if (!hasWiki)
                    issues.Add($"MISSING wikipedia for science QA: \"{query}\"");
            }

            // Space queries should include spaceflight
            if (categories.ContainsKey("space") && categories["space"] >= 0.7)
            {
                var hasSpace = sources.Any(s => s == "spaceflight");
                if (!hasSpace)
                    issues.Add($"MISSING spaceflight for space query: \"{query}\"");
            }

            // Howto should have wikipedia (knowledge source)
            if (intent is "howto")
            {
                var hasWiki = sources.Any(s => s == "wikipedia");
                if (!hasWiki)
                    issues.Add($"MISSING wikipedia for howto: \"{query}\"");
            }

            // Health QA should include factcheck or wikipedia
            if (intent is "qa" && categories.ContainsKey("health") && categories["health"] >= 0.7)
            {
                var hasRef = sources.Any(s => s is "wikipedia" or "factcheck");
                if (!hasRef)
                    issues.Add($"MISSING reference source for health QA: \"{query}\"");
            }

            // Tech QA should NOT have news-only feeds in top 3
            if (intent is "qa" && categories.ContainsKey("technology") && !categories.ContainsKey("entertainment"))
            {
                var searchIdx = sources.FindIndex(s => s.StartsWith("search:"));
                if (searchIdx < 0)
                    issues.Add($"MISSING web search for tech QA: \"{query}\"");
            }

            // Default/empty-category news should still get gnews + search
            if (intent is "news" && categories.Count == 0)
            {
                var hasSearch = sources.Any(s => s.StartsWith("search:") || s.StartsWith("gnews:"));
                if (!hasSearch)
                    issues.Add($"MISSING search for default news: \"{query}\"");
            }
        }

        output.WriteLine("\n\n=== QUALITY REPORT ===");
        if (issues.Count == 0)
        {
            output.WriteLine("  ALL CHECKS PASSED");
        }
        else
        {
            output.WriteLine($"  {issues.Count} ISSUE(S):");
            foreach (var issue in issues)
                output.WriteLine($"  - {issue}");
        }

        // Fail test if any issues found
        Assert.Empty(issues);
    }
}
