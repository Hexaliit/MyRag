using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
///     Deterministic query preprocessing pipeline that runs BEFORE the LLM sentinel.
///     Extracts entities via NER, embeds them to find cached segments, and builds
///     structured context for search API filtering and deduplication.
/// </summary>
public class QueryPreprocessor
{
    /// <summary>
    ///     Run NER + embedding lookup on the user query. Returns structured context
    ///     that the PromptInterpreter and fetch pipeline can use for:
    ///     - Precise search API filters (entity names as quoted queries)
    ///     - Cached segment reuse (previously fetched content about the same entities)
    ///     - URL dedup (skip re-fetching known URLs)
    ///     - Entity-typed routing (ORG → business sources, PER → biography, LOC → regional)
    /// </summary>
    public static async Task<QueryNerContext> PreprocessAsync(
        string query,
        IEmbeddingService embedding,
        StorageService storage,
        string locale = "en-us",
        CancellationToken ct = default)
    {
        var context = new QueryNerContext { RawQuery = query };

        // Step 0: Extract structured signals (dates, numbers, URLs, etc.)
        // Fast deterministic extraction with locale-aware parsing
        var signals = TextRecognizerService.ExtractAll(query, locale);
        context = context with { RecognizerSignals = signals };

        // Step 1: NER entity extraction (deterministic, ONNX, ~50ms)
        try
        {
            using var ner = new NerService();
            if (!ner.IsAvailable)
                return context;

            await ner.InitializeAsync(ct);
            var entities = await ner.ExtractEntitiesAsync(query, ct);

            if (entities.Count == 0)
                return context;

            context = context with { Entities = entities };

            // Classify entities by type
            foreach (var entity in entities)
                switch (entity.Type)
                {
                    case "PER":
                        context.PersonNames.Add(entity.Text);
                        break;
                    case "ORG":
                        context.Organizations.Add(entity.Text);
                        break;
                    case "LOC":
                        context.Locations.Add(entity.Text);
                        break;
                    case "MISC":
                        context.MiscEntities.Add(entity.Text);
                        break;
                }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Query NER extraction failed: {ex.Message}");
            return context;
        }

        // Step 2: Embed entities and search for cached segments
        try
        {
            foreach (var entity in context.Entities)
            {
                var entityEmbedding = await embedding.EmbedAsync(entity.Text, ct);

                // Search storage for similar items (previously fetched content about this entity)
                var matches = await storage.FindSimilarAsync(entityEmbedding, 5, 0.70);
                foreach (var match in matches)
                {
                    if (!context.CachedItemIds.Contains(match.Id))
                    {
                        context.CachedItemIds.Add(match.Id);
                        context.CachedItems.Add(match);
                    }

                    // Track known URLs for ETag-based conditional fetching
                    if (!string.IsNullOrEmpty(match.Url) && !context.KnownUrls.Contains(match.Url))
                        context.KnownUrls.Add(match.Url);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Query cache lookup failed: {ex.Message}");
        }

        // Step 3: Build entity-specific search queries for APIs
        context.EntityQueries = BuildEntityQueries(context);

        return context;
    }

    /// <summary>
    ///     Build precise search queries from extracted entities.
    ///     NER only adds search-based sources (gnews, search) — it does NOT decide
    ///     news outlet categories (e.g., bbc:business vs bbc:entertainment).
    ///     The sentinel LLM handles category classification; NER provides precise entity queries.
    /// </summary>
    private static List<EntitySearchQuery> BuildEntityQueries(QueryNerContext context)
    {
        var queries = new List<EntitySearchQuery>();

        // Organizations get exact-match quoted queries
        foreach (var org in context.Organizations)
            queries.Add(new EntitySearchQuery
            {
                Query = $"\"{org}\"",
                EntityText = org,
                EntityType = "ORG",
                PreferredSources = ["gnews", "search"]
            });

        // Person names get quoted queries
        foreach (var person in context.PersonNames)
            queries.Add(new EntitySearchQuery
            {
                Query = $"\"{person}\"",
                EntityText = person,
                EntityType = "PER",
                PreferredSources = ["gnews", "search"]
            });

        // Locations get search queries
        foreach (var loc in context.Locations)
            queries.Add(new EntitySearchQuery
            {
                Query = loc,
                EntityText = loc,
                EntityType = "LOC",
                PreferredSources = ["gnews", "search"]
            });

        return queries;
    }
}

/// <summary>
///     Structured context from NER preprocessing of the user query.
///     Passed to PromptInterpreter and fetch pipeline for better targeting.
/// </summary>
public record QueryNerContext
{
    public string RawQuery { get; init; } = "";
    public List<NerEntity> Entities { get; init; } = [];
    public List<string> PersonNames { get; init; } = [];
    public List<string> Organizations { get; init; } = [];
    public List<string> Locations { get; init; } = [];
    public List<string> MiscEntities { get; init; } = [];

    /// <summary>
    ///     Previously stored items matching extracted entities (cache hits).
    /// </summary>
    public List<StoredItem> CachedItems { get; init; } = [];

    public HashSet<string> CachedItemIds { get; init; } = [];

    /// <summary>
    ///     Known URLs from cached items — can use ETag/conditional GET.
    /// </summary>
    public List<string> KnownUrls { get; init; } = [];

    /// <summary>
    ///     Entity-specific search queries for API filtering.
    /// </summary>
    public List<EntitySearchQuery> EntityQueries { get; set; } = [];

    /// <summary>
    ///     True if NER found any entities worth using for structured search.
    /// </summary>
    public bool HasEntities => Entities.Count > 0;

    /// <summary>
    ///     True if we found cached segments matching the extracted entities.
    /// </summary>
    public bool HasCachedData => CachedItems.Count > 0;

    /// <summary>
    ///     All unique entity names (for display/debug).
    /// </summary>
    public IEnumerable<string> AllEntityNames =>
        PersonNames.Concat(Organizations).Concat(Locations).Concat(MiscEntities);

    // --- Recognizer Signals (Microsoft.Recognizers.Text) ---

    /// <summary>
    ///     Structured signals extracted via Microsoft.Recognizers.Text.
    ///     Stores position, value, type for filtering and confirmation.
    /// </summary>
    public RecognizedSignals? RecognizerSignals { get; init; }

    /// <summary>
    ///     True if recognizer found any date/time expressions (confirms temporal intent).
    /// </summary>
    public bool HasTemporalSignals => RecognizerSignals?.HasTemporalSignals == true;
}

/// <summary>
///     A search query derived from an extracted entity.
///     Includes preferred sources based on entity type.
/// </summary>
public record EntitySearchQuery
{
    public string Query { get; init; } = "";
    public string EntityText { get; init; } = "";
    public string EntityType { get; init; } = "";
    public List<string> PreferredSources { get; init; } = [];
}