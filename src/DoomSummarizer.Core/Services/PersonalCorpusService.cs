using System.Text.Json;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
/// Manages user's personal facts as a first-class corpus (personal:{name}).
/// Uses existing pipeline infrastructure: embed → NER → entity graph → entity profiles.
/// Personal facts flow through the same knowledge graph as document entities,
/// enabling gap-filling (resolving "here" → user's location) via entity freshness.
/// </summary>
public sealed class PersonalCorpusService
{
    private readonly StorageService _storage;
    private readonly IEmbeddingService _embedding;
    private readonly INerService? _ner;
    private readonly KnowledgeGraphService? _knowledgeGraph;
    private readonly OllamaService? _ollama;
    private readonly bool _ollamaAvailable;

    private const string DefaultCorpusName = "default";

    /// <summary>
    /// The currently active corpus name. Starts as "default", switches when the user
    /// identifies themselves (e.g., "My name is Scott" → "scott").
    /// </summary>
    public string ActiveCorpusName { get; private set; } = DefaultCorpusName;

    public PersonalCorpusService(
        StorageService storage,
        IEmbeddingService embedding,
        INerService? ner,
        KnowledgeGraphService? knowledgeGraph,
        OllamaService? ollama,
        bool ollamaAvailable)
    {
        _storage = storage;
        _embedding = embedding;
        _ner = ner;
        _knowledgeGraph = knowledgeGraph;
        _ollama = ollama;
        _ollamaAvailable = ollamaAvailable;
    }

    /// <summary>
    /// Detect if the user is introducing themselves. If so, migrate the default corpus
    /// to a named one and switch to it. Returns the detected name, or null.
    /// Patterns: "My name is X", "I'm X", "I am X", "Call me X" (when X looks like a name).
    /// </summary>
    public async Task<string?> DetectAndSetNameAsync(string question, CancellationToken ct)
    {
        var name = DetectName(question);
        if (name == null) return null;

        var normalized = name.ToLowerInvariant();

        // Already using this corpus
        if (ActiveCorpusName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            return name;

        // If we have facts in the default corpus, migrate them to the named one
        if (ActiveCorpusName == DefaultCorpusName)
        {
            var defaultFacts = await GetAllFactsAsync(DefaultCorpusName, ct);
            if (defaultFacts.Count > 0)
            {
                foreach (var fact in defaultFacts)
                    await IndexFactAsync(normalized, fact.Content ?? fact.Title, null, ct);
                await ForgetAsync(DefaultCorpusName, "all", ct);
            }
        }

        ActiveCorpusName = normalized;

        // Index the name itself as a fact
        await IndexFactAsync(normalized, $"User's name is {name}", question, ct);

        return name;
    }

    /// <summary>
    /// Get all corpus names that have at least one stored fact.
    /// Used for disambiguation when multiple personal corpuses exist.
    /// </summary>
    public async Task<List<string>> GetActiveCorpusNamesAsync(CancellationToken ct = default)
    {
        // Query personal sources directly via SQL to avoid the auto-exclusion filter
        var sources = await _storage.GetDistinctSourcesAsync("personal:");
        return sources
            .Select(s => s["personal:".Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Switch active corpus to a different named corpus.
    /// </summary>
    public void SetActiveCorpus(string name) =>
        ActiveCorpusName = name.ToLowerInvariant();

    /// <summary>
    /// Detect self-disclosure in user's question via sentinel LLM.
    /// Returns the extracted fact statement, or null if no personal facts detected.
    /// </summary>
    public async Task<string?> DetectSelfDisclosureAsync(string question, CancellationToken ct)
    {
        // Quick exit: no first-person pronouns = very unlikely to contain self-disclosure
        if (!ContainsFirstPerson(question))
            return null;

        if (!_ollamaAvailable || _ollama == null)
            return DetectRuleBasedFallback(question);

        var prompt = $$"""
            Does this message contain a PERSONAL FACT the user is sharing
            about themselves (location, job, name, tech stack, interests,
            preferences, employer, role)?

            Message: "{{question}}"

            Reply JSON: {"has_fact": true/false, "fact_statement": "..."}
            If has_fact, extract ONLY the personal fact as a clean statement.
            Example: "I live in London and want weather" → "User lives in London"
            Example: "I work at Anthropic on AI safety" → "User works at Anthropic on AI safety"
            Example: "What's the best IDE? I use VS Code" → "User uses VS Code"
            If the message is ONLY a question with no personal info, return has_fact: false.
            """;

        try
        {
            var json = await _ollama.SentinelGenerateJsonAsync(prompt, temperature: 0.1, ct: ct);
            var jsonStart = json.IndexOf('{');
            var jsonEnd = json.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                using var doc = JsonDocument.Parse(json[jsonStart..(jsonEnd + 1)]);
                var root = doc.RootElement;
                if (root.TryGetProperty("has_fact", out var hasFact) && hasFact.GetBoolean()
                    && root.TryGetProperty("fact_statement", out var stmt))
                {
                    var statement = stmt.GetString();
                    if (!string.IsNullOrWhiteSpace(statement))
                        return statement;
                }
            }
        }
        catch
        {
            // LLM failure: try rule-based fallback
            return DetectRuleBasedFallback(question);
        }

        return null;
    }

    /// <summary>
    /// Index a personal fact statement into the personal:{name} corpus.
    /// Full pipeline: embed → keywords → FTS5 → NER → entity graph.
    /// </summary>
    public async Task IndexFactAsync(
        string corpusName, string statement,
        string? rawQuestion, CancellationToken ct)
    {
        var source = $"personal:{corpusName}";
        var itemId = $"personal:{corpusName}:{Guid.NewGuid():N}"[..32];

        var item = new ContentItem
        {
            Id = itemId,
            Source = source,
            Title = statement.Length > 80 ? statement[..77] + "..." : statement,
            Content = statement,
            Summary = statement,
            CreatedAt = DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            Tags = ["personal", "user-fact"],
        };

        // Step 1: Embed
        try
        {
            var embedding = await _embedding.EmbedAsync(statement, ct);
            item = item with { Embedding = embedding };
        }
        catch
        {
            // Embedding failure is non-fatal
        }

        // Step 2: Keywords + SQLite + FTS5
        var profile = DocumentProfileService.ExtractProfile(item.Title, statement);
        item = item with { Keywords = profile.KeywordsText };
        await _storage.SaveItemAsync(item);
        await _storage.IndexDocumentFtsAsync(item.Id, item.Title,
            profile.KeywordsText, statement);

        // Step 3: NER → entity graph → co-occurrence → entity profiles
        if (_ner is { IsAvailable: true } && _knowledgeGraph != null)
        {
            try
            {
                var entities = await _ner.ExtractEntitiesAsync(statement, ct);
                if (entities.Count > 0)
                {
                    await _knowledgeGraph.IngestEntitiesAsync(
                        [(item, entities)], ct);
                }
            }
            catch
            {
                // NER/graph failure is non-fatal
            }
        }
    }

    /// <summary>
    /// Search personal corpus entities by type (LOC, ORG, PER, MISC).
    /// Returns most recent/relevant entities, ordered by freshness score.
    /// </summary>
    public async Task<List<(string name, string type, DateTimeOffset lastSeen)>>
        GetPersonalEntitiesAsync(
            string corpusName, string? entityType = null,
            int limit = 5, CancellationToken ct = default)
    {
        var source = $"personal:{corpusName}";

        // Query entities that are linked to personal: items via entity_mentions
        var results = new List<(string name, string type, DateTimeOffset lastSeen)>();

        // Get items from personal corpus
        var personalItems = await _storage.GetRecentItemsAsync(365 * 10, [source]);
        if (personalItems.Count == 0)
            return results;

        var itemIds = personalItems.Select(i => i.Id).ToList();

        // Get entities for these items
        var entityMap = await _storage.GetEntitiesForItemsAsync(itemIds);
        if (entityMap.Count == 0)
            return results;

        // Flatten and deduplicate by entity name, keeping highest confidence
        var allEntities = entityMap.Values
            .SelectMany(e => e)
            .Where(e => entityType == null ||
                        e.type.Equals(entityType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.name.ToLowerInvariant())
            .Select(g =>
            {
                var best = g.OrderByDescending(e => e.confidence).First();
                return (best.name, best.type, lastSeen: DateTimeOffset.UtcNow);
            })
            .Take(limit)
            .ToList();

        // Enrich with actual last_seen from entities table
        foreach (var entity in allEntities)
        {
            var entityId = KnowledgeGraphService.GenerateEntityId(entity.name, entity.type);
            var topEntities = await _storage.GetTopEntitiesAsync(1, entity.type);
            var match = topEntities.FirstOrDefault(e =>
                e.Name.Equals(entity.name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                results.Add((match.Name, match.Type, match.LastSeen));
            else
                results.Add(entity);
        }

        return results
            .OrderByDescending(e =>
            {
                // Freshness score: recent mentions rank higher
                var ageHours = (DateTimeOffset.UtcNow - e.lastSeen).TotalHours;
                return Math.Exp(-ageHours / 72.0);
            })
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Get all personal corpus items (for /me command).
    /// </summary>
    public async Task<List<ContentItem>> GetAllFactsAsync(
        string corpusName, CancellationToken ct = default)
    {
        var source = $"personal:{corpusName}";
        var stored = await _storage.GetRecentItemsAsync(365 * 10, [source]);
        return stored.Select(s => s.ToContentItem()).ToList();
    }

    /// <summary>
    /// Delete personal facts by entity type or keyword.
    /// Passing null category deletes all personal items for this corpus.
    /// </summary>
    public async Task<int> ForgetAsync(
        string corpusName, string? category = null,
        CancellationToken ct = default)
    {
        var source = $"personal:{corpusName}";

        // "forget all" — use efficient source-level delete
        if (category == null || category.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var all = await _storage.GetRecentItemsAsync(365 * 10, [source]);
            if (all.Count == 0) return 0;
            await _storage.DeleteItemsBySourceAsync(source);
            return all.Count;
        }

        var items = await _storage.GetRecentItemsAsync(365 * 10, [source]);
        if (items.Count == 0) return 0;

        // Filter items by entity type or keyword
        List<StoredItem> itemsToDelete;
        var entityType = NormalizeCategoryToEntityType(category);
        if (entityType != null)
        {
            var entityMap = await _storage.GetEntitiesForItemsAsync(
                items.Select(i => i.Id));
            itemsToDelete = items
                .Where(i => entityMap.TryGetValue(i.Id, out var entities) &&
                            entities.Any(e => e.type.Equals(entityType,
                                StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        else
        {
            // Category is a keyword — filter by content match
            itemsToDelete = items
                .Where(i => (i.Content ?? "").Contains(category,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var item in itemsToDelete)
        {
            await _storage.DeleteItemByIdAsync(item.Id);
        }

        return itemsToDelete.Count;
    }

    // --- Private helpers ---

    private static bool ContainsFirstPerson(string text)
    {
        var lower = text.ToLowerInvariant();
        // Check for first-person pronouns and possessives
        return lower.Contains(" i ") || lower.StartsWith("i ")
            || lower.Contains(" my ") || lower.StartsWith("my ")
            || lower.Contains(" i'm ") || lower.StartsWith("i'm ")
            || lower.Contains(" i am ") || lower.StartsWith("i am ")
            || lower.Contains(" i've ") || lower.Contains(" i have ")
            || lower.Contains(" i work ") || lower.Contains(" i live ")
            || lower.Contains(" i use ") || lower.Contains(" i prefer ");
    }

    /// <summary>
    /// Minimal rule-based fallback when LLM is unavailable.
    /// Only catches very explicit patterns.
    /// </summary>
    private static string? DetectRuleBasedFallback(string question)
    {
        var lower = question.ToLowerInvariant();

        // "I live in X" / "I'm based in X"
        if (lower.Contains("i live in") || lower.Contains("i'm based in")
            || lower.Contains("i am based in"))
        {
            return ExtractAfterPattern(question, ["i live in", "i'm based in", "i am based in"]);
        }

        // "I work at X" / "I work for X"
        if (lower.Contains("i work at") || lower.Contains("i work for"))
        {
            return ExtractAfterPattern(question, ["i work at", "i work for"]);
        }

        // "I'm a X" / "I am a X" (role/profession)
        if (lower.Contains("i'm a ") || lower.Contains("i am a "))
        {
            return ExtractAfterPattern(question, ["i'm a", "i am a"]);
        }

        // "I use X" / "I prefer X"
        if (lower.Contains("i use ") || lower.Contains("i prefer "))
        {
            return ExtractAfterPattern(question, ["i use", "i prefer"]);
        }

        return null;
    }

    private static string? ExtractAfterPattern(string text, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var after = text[(idx + pattern.Length)..].Trim();
            // Take up to the first sentence boundary or comma
            var endIdx = after.IndexOfAny(['.', ',', '?', '!', '\n']);
            var fact = endIdx > 0 ? after[..endIdx].Trim() : after.Trim();

            if (fact.Length >= 2)
            {
                // Normalize the pattern verb: "i live in" → "lives in", etc.
                var verb = pattern.ToLowerInvariant() switch
                {
                    "i live in" or "i'm based in" or "i am based in" => "lives in",
                    "i work at" => "works at",
                    "i work for" => "works for",
                    "i'm a" or "i am a" => "is a",
                    "i use" => "uses",
                    "i prefer" => "prefers",
                    _ => pattern
                };
                return $"User {verb} {fact}";
            }
        }
        return null;
    }

    /// <summary>
    /// Detect a name introduction in the user's message.
    /// Returns the extracted name, or null if no introduction detected.
    /// </summary>
    private static string? DetectName(string text)
    {
        var lower = text.ToLowerInvariant();

        // Patterns: "My name is X", "I'm X", "I am X", "Call me X"
        // Only match when the rest looks like a proper name (capitalized, 1-3 words, no verbs)
        string? rawName = null;

        foreach (var prefix in new[] { "my name is ", "call me " })
        {
            var idx = lower.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) continue;
            rawName = ExtractNameAfter(text, idx + prefix.Length);
            if (rawName != null) return rawName;
        }

        // "I'm Scott" / "I am Scott" — only match if the word after is capitalized
        // and NOT a common role/adjective (to avoid "I'm a developer", "I'm happy")
        foreach (var prefix in new[] { "i'm ", "i am " })
        {
            var idx = lower.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) continue;

            var afterIdx = idx + prefix.Length;
            if (afterIdx >= text.Length) continue;

            // Must be at sentence start or after punctuation
            if (idx > 0 && char.IsLetterOrDigit(text[idx - 1])) continue;

            rawName = ExtractNameAfter(text, afterIdx);
            if (rawName != null && !IsCommonRole(rawName.ToLowerInvariant()))
                return rawName;
        }

        return null;
    }

    private static string? ExtractNameAfter(string text, int startIdx)
    {
        if (startIdx >= text.Length) return null;

        var rest = text[startIdx..].TrimStart();
        // Take up to first punctuation or "and"/"but" conjunction
        var endIdx = rest.IndexOfAny(['.', ',', '?', '!', '\n']);
        var andIdx = rest.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
        var butIdx = rest.IndexOf(" but ", StringComparison.OrdinalIgnoreCase);

        var limit = rest.Length;
        if (endIdx > 0) limit = Math.Min(limit, endIdx);
        if (andIdx > 0) limit = Math.Min(limit, andIdx);
        if (butIdx > 0) limit = Math.Min(limit, butIdx);

        var name = rest[..limit].Trim();

        // Validate: 1-3 words, first char uppercase, no common words
        if (name.Length < 2 || name.Length > 40) return null;
        if (!char.IsUpper(name[0])) return null;

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 3) return null;

        // All words should start with uppercase (proper name)
        if (!words.All(w => char.IsUpper(w[0]))) return null;

        return name;
    }

    private static bool IsCommonRole(string lower) =>
        lower is "a" or "an" or "the" or "not" or "so" or "very" or "just" or "really"
            or "happy" or "sad" or "tired" or "busy" or "sorry" or "fine" or "good" or "great"
            or "new" or "old" or "interested" or "based" or "looking" or "trying" or "working"
            or "using" or "building" or "developing" or "learning" or "studying"
            or "frontend" or "backend" or "fullstack" or "devops" or "software"
            or "developer" or "engineer" or "designer" or "manager" or "student"
            or "data" or "scientist" or "analyst" or "researcher" or "founder"
            or "here" or "from" or "going" or "getting" or "doing";

    private static string? NormalizeCategoryToEntityType(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "location" or "loc" or "place" or "city" => "LOC",
            "org" or "organization" or "company" or "employer" => "ORG",
            "person" or "per" or "name" => "PER",
            "misc" or "tech" or "stack" or "skill" => "MISC",
            "all" => null, // null means delete everything
            _ => null       // Try as keyword instead
        };
    }
}
