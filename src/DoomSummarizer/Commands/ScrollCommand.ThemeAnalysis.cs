using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
///     Theme extraction, story connections, fallback summary generation, key theme analysis.
/// </summary>
public partial class ScrollCommand
{
    /// <summary>
    ///     Display story connections based on shared named entities.
    ///     Shows which articles are linked by people, organizations, locations.
    /// </summary>
    private static void DisplayStoryConnections(
        List<(ContentItem item, List<NerEntity> entities)> articleEntityMap)
    {
        // Build entity → articles index (normalize entity text)
        var entityToArticles = new Dictionary<string, List<(ContentItem item, NerEntity entity)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (item, entities) in articleEntityMap)
        foreach (var entity in entities.Where(e => e.Confidence >= 0.6))
        {
            var key = entity.Text.Trim();
            if (key.Length < 2) continue;
            if (!entityToArticles.ContainsKey(key))
                entityToArticles[key] = [];
            // Avoid duplicate articles per entity
            if (entityToArticles[key].All(a => a.item.Id != item.Id))
                entityToArticles[key].Add((item, entity));
        }

        // Find entities that connect 2+ articles (these are the interesting ones)
        var connections = entityToArticles
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Max(a => a.entity.Confidence))
            .Take(10)
            .ToList();

        if (connections.Count == 0) return;

        AnsiConsole.WriteLine();
        var tree = new Tree("[bold yellow]Story Connections[/]")
            .Style(Style.Parse("dim"));

        foreach (var (entityText, articles) in connections)
        {
            var typeColor = articles[0].entity.Type switch
            {
                "PER" => "green",
                "ORG" => "blue",
                "LOC" => "yellow",
                _ => "grey"
            };
            var typeLabel = articles[0].entity.Type switch
            {
                "PER" => "person",
                "ORG" => "org",
                "LOC" => "place",
                "MISC" => "topic",
                _ => "entity"
            };

            var node = tree.AddNode(
                $"[{typeColor} bold]{Markup.Escape(entityText)}[/] [dim]({typeLabel}, {articles.Count} stories)[/]");

            foreach (var (item, _) in articles.Take(5))
            {
                var title = item.Title.Length > 60
                    ? item.Title[..57] + "..."
                    : item.Title;
                var source = GetSourceFromUrl(item.Url ?? "");
                node.AddNode($"[dim]{source}[/] {Markup.Escape(title)}");
            }
        }

        AnsiConsole.Write(tree);
    }

    private static string GenerateFallbackSummary(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
        string vibe,
        IngestDocumentType docType = IngestDocumentType.Unknown)
    {
        var sb = new StringBuilder();
        var heading = docType switch
        {
            IngestDocumentType.Fiction => "Document Analysis",
            IngestDocumentType.NonFiction => "Book Summary",
            IngestDocumentType.Academic => "Paper Summary",
            IngestDocumentType.Technical => "Technical Overview",
            _ => "Doom Scroll Digest"
        };
        sb.AppendLine($"# {heading} ({vibe})");
        sb.AppendLine();
        sb.AppendLine($"*Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} | {items.Count} items*");
        sb.AppendLine();

        // === TOP STORIES: Show the highest-confidence items first ===
        var topItems = items
            .OrderByDescending(i => i.relevance)
            .Take(Math.Min(5, items.Count))
            .ToList();

        if (topItems.Count > 0)
        {
            var sectionTitle = docType is IngestDocumentType.Unknown ? "Top Stories" : "Key Sections";
            sb.AppendLine($"## {sectionTitle}");
            sb.AppendLine();

            var maxRelevance = topItems.Max(i => i.relevance);

            foreach (var item in topItems)
            {
                // Confidence bar: normalize to 0-100% relative to max score
                var confidence = maxRelevance > 0 ? item.relevance / maxRelevance : 0;
                var pct = (int)(confidence * 100);
                var bar = new string('#', pct / 10) + new string('.', 10 - pct / 10);
                var sentimentIcon = item.sentiment switch
                {
                    > 0.15f => "+",
                    < -0.15f => "-",
                    _ => "~"
                };

                sb.AppendLine($"  [{sentimentIcon}] [{bar}] {pct}% | {item.title}");

                // Show content snippet for top stories — this is the "segment" the user wants
                if (!string.IsNullOrEmpty(item.summary) && item.summary != item.title)
                {
                    var truncated = item.summary.Length > 300
                        ? item.summary[..300] + "..."
                        : item.summary;
                    sb.AppendLine($"      {truncated}");
                }

                if (!string.IsNullOrEmpty(item.url))
                {
                    if (item.url.Contains("news.google.com/rss/articles/", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("      -> [via Google News — direct URL unavailable]");
                    else
                        sb.AppendLine($"      -> {item.url}");
                }

                sb.AppendLine();
            }
        }

        // === TOPIC BREAKDOWN: Group remaining items by topic ===
        var byTopic = items
            .OrderByDescending(i => i.relevance)
            .GroupBy(x => x.topic)
            .OrderByDescending(g => g.Max(i => i.relevance));

        sb.AppendLine("## By Topic");
        sb.AppendLine();

        foreach (var group in byTopic)
        {
            var topicTitle = group.Key.Length > 0
                ? char.ToUpper(group.Key[0]) + group.Key[1..]
                : "Uncategorized";
            var avgSentiment = group.Average(g => g.sentiment);
            var topRelevance = group.Max(g => g.relevance);
            var sentimentIndicator = avgSentiment switch
            {
                > 0.1f => "+",
                < -0.1f => "-",
                _ => "~"
            };
            sb.AppendLine(
                $"### {topicTitle} [{sentimentIndicator}] ({group.Count()} items, top relevance: {topRelevance:F2})");

            foreach (var item in group.OrderByDescending(i => i.relevance).Take(5))
            {
                var pct = items.Max(i => i.relevance) > 0
                    ? (int)(item.relevance / items.Max(i => i.relevance) * 100)
                    : 0;
                var sentimentIcon = item.sentiment switch
                {
                    > 0.15f => "[+]",
                    < -0.15f => "[-]",
                    _ => "[~]"
                };

                sb.AppendLine($"- {sentimentIcon} {pct}% {item.title}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Build a structured theme briefing with named themes, evidence counts,
    ///     supporting snippets (tagged to evidence IDs), and key entities/terms.
    /// </summary>
    internal static ThemeBriefing ExtractThemeBriefing(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<ContentItem> contentItems,
        Dictionary<string, List<(string name, string type, double confidence)>>? itemEntities = null)
    {
        var briefing = new ThemeBriefing();

        // Map content items by URL for quick lookup
        var contentByUrl = contentItems
            .Where(c => !string.IsNullOrEmpty(c.Url))
            .GroupBy(c => c.Url!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Build URL → entity list lookup from item ID-keyed entity data
        var entitiesByUrl = new Dictionary<string, List<(string name, string type, double confidence)>>(
            StringComparer.OrdinalIgnoreCase);
        if (itemEntities != null)
            foreach (var ci in contentItems)
                if (!string.IsNullOrEmpty(ci.Url) && itemEntities.TryGetValue(ci.Id, out var ents))
                    entitiesByUrl.TryAdd(ci.Url, ents);

        // Build evidence index (E1, E2...) from analyzed items
        var evidenceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < analyzedItems.Count; i++)
            if (!string.IsNullOrEmpty(analyzedItems[i].url) && !evidenceIndex.ContainsKey(analyzedItems[i].url))
                evidenceIndex[analyzedItems[i].url] = i + 1;

        // Group analyzed items by topic
        var topicGroups = analyzedItems
            .GroupBy(i => i.topic, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        // Stop words for term extraction
        var stopWords = BuildStopWordSet();

        // For each topic group, extract theme data
        foreach (var group in topicGroups.Take(6))
        {
            var theme = new ThemeEntry
            {
                TopicLabel = group.Key,
                SegmentCount = group.Count()
            };

            // Find evidence IDs for this group
            var groupEvidenceIds = new List<int>();
            foreach (var item in group)
                if (!string.IsNullOrEmpty(item.url) && evidenceIndex.TryGetValue(item.url, out var eid))
                    groupEvidenceIds.Add(eid);
            theme.EvidenceIds = groupEvidenceIds.Distinct().OrderBy(x => x).ToList();

            // Extract NER entities from the knowledge graph for this theme group
            var groupGraphEntities = new Dictionary<string, (string name, string type, int count, double maxConf)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in group)
                if (!string.IsNullOrEmpty(item.url) && entitiesByUrl.TryGetValue(item.url, out var ents))
                    foreach (var (name, type, conf) in ents)
                    {
                        var key = name.ToLowerInvariant();
                        if (groupGraphEntities.TryGetValue(key, out var existing))
                            groupGraphEntities[key] = (name, type, existing.count + 1,
                                Math.Max(existing.maxConf, conf));
                        else
                            groupGraphEntities[key] = (name, type, 1, conf);
                    }

            // Use NER entities as GraphEntities (ranked by cross-article count, then confidence)
            // Deduplicate: remove entities that are substrings of higher-ranked entities
            var rankedEntities = groupGraphEntities.Values
                .OrderByDescending(e => e.count)
                .ThenByDescending(e => e.maxConf)
                .ToList();
            var selectedEntities = new List<(string name, string type, int count, double maxConf)>();
            foreach (var ent in rankedEntities)
            {
                // Skip if overlaps with an already-selected higher-ranked entity
                var overlaps = selectedEntities.Any(s =>
                    s.name.Contains(ent.name, StringComparison.OrdinalIgnoreCase)
                    || ent.name.Contains(s.name, StringComparison.OrdinalIgnoreCase));
                if (!overlaps)
                    selectedEntities.Add(ent);
                if (selectedEntities.Count >= 8) break;
            }

            theme.GraphEntities = selectedEntities
                .Select(e => (e.name, e.type))
                .ToList();

            // Extract key terms: prefer NER entities, fall back to frequency tokens
            if (theme.GraphEntities.Count >= 3)
            {
                // Use top NER entity names as key terms (already ranked)
                theme.KeyTerms = theme.GraphEntities.Take(5)
                    .Select(e => e.name)
                    .ToList();
            }
            else
            {
                // Fall back to frequency-based token extraction
                var groupTermCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in group)
                {
                    ContentItem? ci = null;
                    if (!string.IsNullOrEmpty(item.url))
                        contentByUrl.TryGetValue(item.url, out ci);

                    var rawText = $"{item.title} {item.summary} {ci?.Content ?? ""}";
                    var text = CleanMarkdownForSnippet(rawText);
                    var tokens = text
                        .Split(
                            [
                                ' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '"', '\'',
                                '\u2014', '\u2013', '-', '/'
                            ],
                            StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim().ToLowerInvariant())
                        .Where(t => t.Length >= 4 && t.Length <= 25
                                                  && !stopWords.Contains(t) && !int.TryParse(t, out _)
                                                  && !IsUrlFragment(t))
                        .ToList();

                    foreach (var token in tokens.Distinct())
                    {
                        groupTermCounts.TryGetValue(token, out var c);
                        groupTermCounts[token] = c + 1;
                    }
                }

                // Merge any available NER entities into the front of the list
                var nerNames = new HashSet<string>(
                    theme.GraphEntities.Select(e => e.name), StringComparer.OrdinalIgnoreCase);
                var frequencyTerms = groupTermCounts
                    .Where(kv => kv.Value >= Math.Min(2, group.Count()) && !nerNames.Contains(kv.Key))
                    .OrderByDescending(kv => kv.Value)
                    .Take(5 - theme.GraphEntities.Count)
                    .Select(kv => kv.Key)
                    .ToList();

                theme.KeyTerms = theme.GraphEntities
                    .Select(e => e.name)
                    .Concat(frequencyTerms)
                    .Take(5)
                    .ToList();
            }

            // Extract 2 representative snippets from content
            foreach (var item in group.OrderByDescending(i => i.relevance).Take(3))
            {
                ContentItem? ci = null;
                if (!string.IsNullOrEmpty(item.url))
                    contentByUrl.TryGetValue(item.url, out ci);

                var content = ci?.Content ?? item.summary;
                if (string.IsNullOrEmpty(content)) continue;

                var snippet = ExtractBestSentence(content, theme.KeyTerms);
                if (snippet != null && theme.Snippets.Count < 2)
                {
                    var eid = !string.IsNullOrEmpty(item.url) && evidenceIndex.TryGetValue(item.url, out var id)
                        ? id
                        : (int?)null;
                    theme.Snippets.Add((snippet, eid));
                }
            }

            // Build a thesis-style name from topic + key terms
            theme.ThesisName = BuildThesisName(group.Key, theme.KeyTerms, group.Count());

            briefing.Themes.Add(theme);
        }

        // Corpus coverage
        var itemsInThemes = topicGroups.Sum(g => g.Count());
        briefing.TotalEvidenceItems = analyzedItems.Count;
        briefing.ItemsInThemes = itemsInThemes;
        briefing.SourceCount = analyzedItems
            .Select(i => i.url)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u =>
            {
                try
                {
                    return new Uri(u!).Host;
                }
                catch
                {
                    return u!;
                }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Count distinct graph entities across all themes
        briefing.GraphEntityCount = briefing.Themes
            .SelectMany(t => t.GraphEntities)
            .Select(e => e.name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Missing themes: common topics NOT represented
        var allKnownTopics = new[]
        {
            "technology", "science", "security", "health", "business",
            "politics", "world", "climate", "space", "ai", "entertainment"
        };
        var presentTopics = new HashSet<string>(
            topicGroups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        briefing.MissingTopics = allKnownTopics
            .Where(t => !presentTopics.Contains(t))
            .Take(4)
            .ToList();

        return briefing;
    }

    /// <summary>
    ///     Extract the most relevant sentence from content that mentions key terms.
    /// </summary>
    private static string? ExtractBestSentence(string content, List<string> keyTerms)
    {
        // Clean markdown formatting before extracting sentences
        var cleanContent = CleanMarkdownForSnippet(content);

        // Split into sentences
        var sentences = cleanContent.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Select(s => s.TrimStart(')', '(', '>', '-', '*', ' '))
            .Where(s => s.Length is >= 30 and <= 200)
            // Filter out boilerplate / navigation patterns
            .Where(s => !s.StartsWith("Series Navigation", StringComparison.OrdinalIgnoreCase))
            .Where(s => !s.StartsWith("Part ", StringComparison.OrdinalIgnoreCase) || s.Length > 60)
            .Where(s => !s.Contains("http://") && !s.Contains("https://"))
            .Where(s => !s.StartsWith("Subscribe") && !s.StartsWith("Click"))
            // Ensure sentence starts with a letter (not punctuation fragments)
            .Where(s => s.Length > 0 && char.IsLetter(s[0]))
            .ToList();

        if (sentences.Count == 0) return null;

        // Score sentences by key term overlap + prefer informative content
        var best = sentences
            .Select(s =>
            {
                var lower = s.ToLowerInvariant();
                var hits = keyTerms.Count(t => lower.Contains(t, StringComparison.Ordinal));
                // Penalize very short or formulaic sentences
                var lengthBonus = s.Length > 60 ? 1 : 0;
                return (sentence: s, score: hits + lengthBonus);
            })
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.sentence.Length)
            .First();

        return best.sentence;
    }

    /// <summary>
    ///     Strip markdown syntax from content for clean snippet extraction.
    /// </summary>
    private static string CleanMarkdownForSnippet(string content)
    {
        // Remove markdown headings (# ## ###)
        content = MarkdownHeadingRegex().Replace(content, "");
        // Remove markdown links [text](url) → text
        content = MarkdownLinkRegex().Replace(content, "$1");
        // Remove bold/italic markers
        content = content.Replace("**", "").Replace("__", "").Replace("*", "").Replace("_", " ");
        // Remove code blocks
        content = CodeBlockRegex().Replace(content, " ");
        content = InlineCodeRegex().Replace(content, " ");
        // Remove bullet markers
        content = BulletMarkerRegex().Replace(content, "");
        // Collapse whitespace
        content = WhitespaceRegex().Replace(content, " ");
        return content.Trim();
    }

    /// <summary>
    ///     Build a thesis-style theme name from topic + key terms.
    /// </summary>
    private static string BuildThesisName(string topic, List<string> keyTerms, int count)
    {
        // Capitalize topic
        var topicLabel = char.ToUpper(topic[0]) + topic[1..].Replace('_', ' ');

        if (keyTerms.Count == 0)
            return topicLabel;

        // Deduplicate terms: remove terms that are substrings of other terms
        var deduped = keyTerms
            .Where(t => !keyTerms.Any(other =>
                !string.Equals(other, t, StringComparison.OrdinalIgnoreCase)
                && other.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (deduped.Count == 0) deduped = keyTerms; // fallback if all are substrings

        // Build descriptive terms (respect existing casing for NER entities)
        var terms = deduped.Take(3)
            .Select(t => t.Length > 1 && (char.IsUpper(t[0]) || t.All(c => char.IsUpper(c) || !char.IsLetter(c)))
                ? t // Already properly cased (NER entity or acronym)
                : char.ToUpper(t[0]) + t[1..])
            .ToList();

        // Use topic-specific framing for thesis-style names
        var termPhrase = terms.Count switch
        {
            1 => terms[0],
            2 => $"{terms[0]} & {terms[1]}",
            _ => $"{terms[0]}, {terms[1]} & {terms[2]}"
        };

        return $"{topicLabel}: {termPhrase}";
    }

    private static HashSet<string> BuildStopWordSet()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "as", "is", "was", "are", "were", "be",
            "been", "being", "have", "has", "had", "do", "does", "did", "will",
            "would", "could", "should", "may", "might", "shall", "can", "need",
            "it", "its", "this", "that", "these", "those", "he", "she", "they",
            "we", "you", "me", "my", "your", "his", "her", "our", "their",
            "not", "no", "nor", "so", "if", "then", "than", "too", "very",
            "just", "about", "also", "more", "most", "some", "any", "all",
            "each", "every", "both", "few", "many", "much", "such", "own",
            "same", "other", "new", "old", "first", "last", "long", "great",
            "said", "says", "like", "well", "back", "even", "still",
            "take", "come", "make", "know", "get", "got", "see", "look",
            "think", "give", "use", "find", "tell", "ask", "work", "seem",
            "news", "articles", "article", "latest", "generated", "content",
            "page", "pages", "site", "website", "click", "link", "links",
            "share", "comment", "comments", "posted", "updated", "subscribe",
            "read", "related", "view", "here", "there", "only", "into",
            "over", "after", "before", "between", "under", "since", "during",
            "through", "against", "now", "where", "when", "what", "which",
            "who", "how", "why", "because", "although", "though", "whether",
            "already", "yet", "never", "always", "often", "ever", "really",
            "using", "used", "been", "being", "having", "going", "made",
            "based", "including", "another", "several", "less", "given",
            // Blog/web boilerplate
            "introduction", "conclusion", "summary", "overview", "section",
            "series", "navigation", "part", "chapter", "previous", "next",
            "blog", "post", "author", "date", "tags", "category",
            // URL fragments
            "http", "https", "www", "html", "com", "org", "net"
        };
    }

    /// <summary>
    ///     Check if a token looks like a URL fragment and should be excluded from key terms.
    /// </summary>
    private static bool IsUrlFragment(string token)
    {
        return token.Contains("//") || token.Contains("www.") || token.Contains(".com")
               || token.Contains(".net") || token.Contains(".org") || token.Contains(".io")
               || token.StartsWith("http") || token.Contains("/blog/")
               || token.All(c => c == '/' || c == '.' || c == ':');
    }

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"`[^`]+`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex BulletMarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    internal static (
        List<(string topic, int count)> topics,
        List<(string term, int articles)> terms)
        ExtractKeyThemes(
            List<(string title, string summary, string topic, float sentiment, string url, double relevance)>
                analyzedItems,
            List<ContentItem> contentItems)
    {
        // Topic distribution from analyzed items
        var topics = analyzedItems
            .GroupBy(i => i.topic, StringComparer.OrdinalIgnoreCase)
            .Select(g => (topic: g.Key, count: g.Count()))
            .OrderByDescending(t => t.count)
            .Take(8)
            .ToList();

        // Cross-article term extraction from content
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "as", "is", "was", "are", "were", "be",
            "been", "being", "have", "has", "had", "do", "does", "did", "will",
            "would", "could", "should", "may", "might", "shall", "can", "need",
            "it", "its", "this", "that", "these", "those", "he", "she", "they",
            "we", "you", "i", "me", "my", "your", "his", "her", "our", "their",
            "not", "no", "nor", "so", "if", "then", "than", "too", "very",
            "just", "about", "also", "more", "most", "some", "any", "all",
            "each", "every", "both", "few", "many", "much", "such", "own",
            "same", "other", "new", "old", "first", "last", "long", "great",
            "little", "right", "big", "high", "small", "large", "next", "early",
            "young", "important", "public", "bad", "good", "best", "said", "says",
            "like", "well", "back", "even", "still", "way", "take", "come",
            "make", "know", "get", "got", "go", "see", "look", "think", "give",
            "use", "find", "tell", "ask", "work", "seem", "feel", "try", "leave",
            "call", "keep", "let", "put", "show", "turn", "start", "run", "move",
            "play", "live", "believe", "bring", "happen", "write", "provide",
            "sit", "stand", "lose", "pay", "meet", "include", "continue",
            "set", "learn", "change", "lead", "understand", "watch", "follow",
            "stop", "create", "speak", "read", "add", "spend", "grow", "open",
            "walk", "win", "offer", "remember", "love", "consider", "appear",
            "buy", "wait", "serve", "die", "send", "expect", "build", "stay",
            "fall", "cut", "reach", "kill", "remain", "suggest", "raise", "pass",
            "sell", "require", "report", "decide", "pull", "develop", "report",
            "one", "two", "three", "four", "five", "six", "seven", "eight",
            "nine", "ten", "year", "years", "day", "days", "time", "week",
            "month", "per", "people", "world", "part", "group", "number",
            "fact", "however", "while", "after", "before", "between", "under",
            "since", "during", "through", "against", "into", "over", "only",
            "now", "where", "when", "what", "which", "who", "how", "why",
            "here", "there", "because", "although", "though", "whether",
            "already", "yet", "never", "always", "often", "ever", "really",
            "according", "around", "without", "within", "across", "along",
            "using", "used", "says", "also", "been", "being", "having",
            "going", "made", "making", "getting", "doing", "done", "called",
            "based", "including", "according", "among", "became", "become",
            "another", "several", "less", "given", "among",
            "news", "articles", "article", "latest", "generated", "content",
            "page", "pages", "site", "website", "click", "link", "links",
            "share", "comment", "comments", "posted", "updated", "subscribe",
            "sign", "login", "search", "menu", "home", "about", "contact",
            "section", "topics", "topic", "read", "related", "more", "view"
        };

        // Count terms that appear in 2+ articles (document frequency)
        var termDocFreq = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var topItems = contentItems
            .OrderByDescending(i => i.RelevanceScore)
            .Take(25)
            .ToList();

        foreach (var item in topItems)
        {
            var text = $"{item.Title} {item.Content ?? ""}";
            var tokens = text
                .Split(
                    [
                        ' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\'',
                        '\u2014', '\u2013', '-', '/', '\\'
                    ],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 4 && t.Length <= 30
                                          && !stopWords.Contains(t) && !int.TryParse(t, out _)
                                          && !t.StartsWith("http") && !t.Contains("www.")
                                          && !t.Contains(".com") && !t.Contains(".org") && !t.Contains(".net"))
                .Distinct()
                .ToList();

            foreach (var token in tokens)
            {
                if (!termDocFreq.ContainsKey(token))
                    termDocFreq[token] = [];
                termDocFreq[token].Add(item.Id);
            }
        }

        // Terms appearing across 3+ articles are key themes
        var terms = termDocFreq
            .Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key)
            .Take(12)
            .Select(kv => (term: kv.Key, articles: kv.Value.Count))
            .ToList();

        return (topics, terms);
    }

    /// <summary>
    ///     Theme briefing data model.
    /// </summary>
    internal class ThemeBriefing
    {
        public List<ThemeEntry> Themes { get; set; } = [];
        public int TotalEvidenceItems { get; set; }
        public int ItemsInThemes { get; set; }
        public int SourceCount { get; set; }
        public int GraphEntityCount { get; set; }
        public List<string> MissingTopics { get; set; } = [];

        public int CoveragePercent =>
            TotalEvidenceItems > 0 ? (int)(ItemsInThemes * 100.0 / TotalEvidenceItems) : 0;

        public bool HasGraphEntities => GraphEntityCount > 0;
    }

    internal class ThemeEntry
    {
        public string TopicLabel { get; set; } = "";
        public string ThesisName { get; set; } = "";
        public int SegmentCount { get; set; }
        public List<int> EvidenceIds { get; set; } = [];
        public List<string> KeyTerms { get; set; } = [];
        public List<(string name, string type)> GraphEntities { get; set; } = [];
        public List<(string text, int? evidenceId)> Snippets { get; set; } = [];
    }
}