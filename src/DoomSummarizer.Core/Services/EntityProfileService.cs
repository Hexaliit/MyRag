using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Computes weighted entity profile embeddings for documents using TF × IDF × confidence weighting.
///
/// Each document gets a 384-dim entity profile embedding that encodes:
/// - Which entities appear (via their text embeddings)
/// - How distinctive each entity is (Entity IDF - rare entities matter more)
/// - How frequently each entity appears (saturating TF to prevent boilerplate dominance)
/// - How confident the NER was (confidence weighting with floor to prevent vanishing)
///
/// This enables semantic entity-to-entity matching via HNSW instead of naive shared-count.
///
/// Key design decisions (per review feedback):
/// 1. TF saturation: Uses 1 + log(mentions) to prevent long docs from dominating
/// 2. IDF smoothing: Uses log((N+1)/(df+1)) + 1 for stable behavior at extremes
/// 3. Confidence floor: Clamps to [0.2, 1.0] so low-confidence distinctive entities don't vanish
/// 4. Normalization: L2-normalize only (no divide-by-totalWeight, which is redundant for cosine)
/// 5. Entity embeddings: Fetches from storage when available, falls back to on-demand embedding
/// </summary>
public class EntityProfileService
{
    private readonly IEmbeddingService _embedding;
    private readonly IEntityGraphStore? _vectorStore;
    private readonly int _dim;

    // Type weights: ORG and PER slightly more distinctive than LOC/MISC
    private static readonly Dictionary<string, float> TypeWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ORG"] = 1.2f,
        ["PER"] = 1.1f,
        ["LOC"] = 1.0f,
        ["MISC"] = 0.9f
    };

    public EntityProfileService(IEmbeddingService embedding, int embeddingDimension = 384)
    {
        _embedding = embedding;
        _dim = embeddingDimension;
    }

    public EntityProfileService(IEmbeddingService embedding, IEntityGraphStore vectorStore, int embeddingDimension = 384)
    {
        _embedding = embedding;
        _vectorStore = vectorStore;
        _dim = embeddingDimension;
    }

    /// <summary>
    /// Compute a weighted entity profile embedding for a document.
    /// Combines entity text embeddings weighted by saturating TF × smoothed IDF × clamped confidence.
    ///
    /// Formula:
    ///   TF(e, d) = 1 + log(mentions_in_doc(e, d))   // Saturates to prevent boilerplate dominance
    ///   IDF(e) = log((N+1)/(df(e)+1)) + 1           // Smoothed for stable extremes
    ///   confidence' = clamp(confidence, 0.2, 1.0)  // Floor prevents vanishing
    ///   weight = TF × IDF × confidence' × type_weight
    ///   doc_entity_profile = L2_normalize(Σ entity_embedding(e) × weight)
    /// </summary>
    /// <param name="docEntities">Entities found in the document: (entityId, name, type, confidence, mentions)</param>
    /// <param name="entityDocCounts">Document count per entity for IDF computation</param>
    /// <param name="totalDocs">Total number of documents in corpus</param>
    /// <param name="entityEmbeddings">Optional pre-fetched entity embeddings (entityId → embedding)</param>
    /// <param name="newlyComputedEmbeddings">Output: embeddings that were computed on-demand (for caching)</param>
    /// <returns>
    /// Tuple of (L2-normalized 384-dim entity profile, top contributing entities for explain/debug).
    /// Returns (empty array, empty list) if no entities.
    /// </returns>
    public async Task<(float[] profile, List<(string entityId, string name, float weight)> topEntities)> ComputeProfileWithExplainAsync(
        List<(string entityId, string name, string type, float confidence, int mentions)> docEntities,
        Dictionary<string, int> entityDocCounts,
        int totalDocs,
        Dictionary<string, float[]>? entityEmbeddings = null,
        Dictionary<string, float[]>? newlyComputedEmbeddings = null)
    {
        if (docEntities.Count == 0) return ([], []);

        var profile = new float[_dim];
        var entityWeights = new List<(string entityId, string name, float weight, float[] embedding)>();

        foreach (var (entityId, name, type, confidence, mentions) in docEntities)
        {
            // Get entity embedding (prefer cached/stored, fall back to on-demand)
            float[]? entityEmbed = null;
            var wasCached = false;
            if (entityEmbeddings != null && entityEmbeddings.TryGetValue(entityId, out var cached))
            {
                entityEmbed = cached;
                wasCached = true;
            }
            else
            {
                // Fall back to on-demand embedding
                entityEmbed = await _embedding.EmbedAsync(name);

                // Track newly computed embeddings for caching
                if (newlyComputedEmbeddings != null && !wasCached)
                {
                    newlyComputedEmbeddings[entityId] = entityEmbed;
                }
            }

            // Saturating TF: 1 + log(mentions) prevents boilerplate dominance
            var tf = 1.0f + (float)Math.Log(Math.Max(1, mentions));

            // Smoothed IDF: log((N+1)/(df+1)) + 1 for stable behavior at extremes
            var docCount = entityDocCounts.GetValueOrDefault(entityId, 1);
            var idf = (float)(Math.Log((totalDocs + 1.0) / (docCount + 1.0)) + 1.0);

            // Confidence floor: clamp to [0.2, 1.0] so low-confidence distinctive entities don't vanish
            var clampedConfidence = Math.Clamp(confidence, 0.2f, 1.0f);

            // Type weight: ORG/PER slightly more distinctive than LOC/MISC
            var typeWeight = TypeWeights.GetValueOrDefault(type, 1.0f);

            // Combined weight
            var weight = tf * idf * clampedConfidence * typeWeight;
            entityWeights.Add((entityId, name, weight, entityEmbed));
        }

        // Accumulate weighted embeddings
        foreach (var (_, _, weight, entityEmbed) in entityWeights)
        {
            VectorMath.AddScaled(profile, entityEmbed, weight);
        }

        // L2 normalize only (no divide-by-totalWeight, which is redundant for cosine similarity)
        VectorMath.L2Normalize(profile);

        // Return top entities by weight for explain/debug
        var topEntities = entityWeights
            .OrderByDescending(e => e.weight)
            .Take(5)
            .Select(e => (e.entityId, e.name, e.weight))
            .ToList();

        return (profile, topEntities);
    }

    /// <summary>
    /// Simplified ComputeProfile that doesn't return explain data.
    /// </summary>
    public async Task<float[]> ComputeProfileAsync(
        List<(string entityId, string name, float confidence, int mentions)> docEntities,
        Dictionary<string, int> entityDocCounts,
        int totalDocs)
    {
        // Convert to the extended format with type (default to MISC for backwards compatibility)
        var withType = docEntities
            .Select(e => (e.entityId, e.name, type: InferTypeFromId(e.entityId), e.confidence, e.mentions))
            .ToList();

        var (profile, _) = await ComputeProfileWithExplainAsync(withType, entityDocCounts, totalDocs);
        return profile;
    }

    /// <summary>
    /// Infer entity type from ID (e.g., "org_abc123" → "ORG")
    /// </summary>
    private static string InferTypeFromId(string entityId)
    {
        if (entityId.StartsWith("org_", StringComparison.OrdinalIgnoreCase)) return "ORG";
        if (entityId.StartsWith("per_", StringComparison.OrdinalIgnoreCase)) return "PER";
        if (entityId.StartsWith("loc_", StringComparison.OrdinalIgnoreCase)) return "LOC";
        return "MISC";
    }

    /// <summary>
    /// Compute an aggregate entity profile from multiple documents (e.g., top-K search results).
    /// Used to find related documents via HNSW when we have multiple seed documents.
    /// </summary>
    /// <param name="itemProfiles">Entity profiles for each item</param>
    /// <returns>L2-normalized aggregate profile, or empty array if no profiles</returns>
    public float[] ComputeAggregateProfile(List<float[]> itemProfiles)
    {
        if (itemProfiles.Count == 0) return [];

        var aggregate = new float[_dim];
        var count = 0;

        foreach (var profile in itemProfiles)
        {
            if (profile.Length != _dim) continue;
            VectorMath.AddScaled(aggregate, profile, 1f);
            count++;
        }

        if (count == 0) return [];

        // L2 normalize (no averaging needed since we're normalizing anyway)
        VectorMath.L2Normalize(aggregate);

        return aggregate;
    }

    /// <summary>
    /// Compute entity profile for a query based on extracted query entities.
    /// Uses the same TF × IDF × confidence formula as document profiles.
    ///
    /// Note: Query NER is often sparse for short queries. Caller should fall back
    /// to regular embedding search when query entities are fewer than 2.
    /// </summary>
    /// <param name="queryEntities">Entities extracted from the query: (name, type, confidence)</param>
    /// <param name="entityDocCounts">Document count per entity for IDF computation</param>
    /// <param name="totalDocs">Total number of documents in corpus</param>
    /// <returns>L2-normalized query entity profile, or empty array if no entities</returns>
    public async Task<float[]> ComputeQueryProfileAsync(
        List<(string name, string type, float confidence)> queryEntities,
        Dictionary<string, int> entityDocCounts,
        int totalDocs)
    {
        if (queryEntities.Count == 0) return [];

        // Convert to the extended format (mentions=1 for query entities)
        var asDocEntities = queryEntities
            .Select(e => (
                entityId: KnowledgeGraphService.GenerateEntityId(e.name, e.type),
                name: e.name,
                type: e.type,
                confidence: e.confidence,
                mentions: 1))
            .ToList();

        var (profile, _) = await ComputeProfileWithExplainAsync(asDocEntities, entityDocCounts, totalDocs);
        return profile;
    }

    /// <summary>
    /// Infer entity type from a string entity name using heuristics.
    /// Used when Sentinel provides entities without types.
    /// </summary>
    /// <param name="entityName">The entity name to classify</param>
    /// <returns>Inferred entity type: ORG, PER, LOC, or MISC</returns>
    public static string InferEntityType(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return "MISC";

        var name = entityName.Trim();

        // Check known locations first (before org, since some are ambiguous like EU)
        if (KnownLocations.Contains(name))
            return "LOC";

        // Check for known organization patterns
        if (IsLikelyOrganization(name))
            return "ORG";

        // Check for known location patterns
        if (IsLikelyLocation(name))
            return "LOC";

        // Check for person name patterns
        if (IsLikelyPerson(name))
            return "PER";

        return "MISC";
    }

    // Common organization suffixes
    private static readonly HashSet<string> OrgSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inc", "Inc.", "Corp", "Corp.", "Corporation", "LLC", "Ltd", "Ltd.",
        "Company", "Co", "Co.", "Group", "Holdings", "Partners", "Foundation",
        "Institute", "University", "College", "School", "Agency", "Commission",
        "Board", "Council", "Association", "Organization", "Society", "Club",
        "Bank", "Fund", "Trust", "Labs", "Lab", "Technologies", "Tech",
        "Solutions", "Services", "Systems", "Network", "Media", "News",
        "Times", "Post", "Journal", "Tribune", "AI", "Research"
    };

    // Known major organizations (partial list for common tech/news entities)
    private static readonly HashSet<string> KnownOrganizations = new(StringComparer.OrdinalIgnoreCase)
    {
        "OpenAI", "Anthropic", "Google", "Microsoft", "Apple", "Amazon", "Meta",
        "Facebook", "Twitter", "X", "Tesla", "SpaceX", "Nvidia", "Intel", "AMD",
        "IBM", "Oracle", "Salesforce", "Adobe", "Netflix", "Spotify", "Uber",
        "Airbnb", "LinkedIn", "GitHub", "GitLab", "Stripe", "Shopify", "Zoom",
        "Slack", "Discord", "Reddit", "TikTok", "Snap", "Pinterest", "Dropbox",
        "NASA", "ESA", "CERN", "WHO", "UN", "NATO", "EU", "FBI", "CIA", "NSA",
        "SEC", "FTC", "FDA", "EPA", "IRS", "DOJ", "DOD", "DHS",
        "BBC", "CNN", "Reuters", "AP", "NPR", "NYT", "WSJ", "WaPo",
        "MIT", "Stanford", "Harvard", "Berkeley", "Oxford", "Cambridge",
        "DeepMind", "Hugging Face", "Stability AI", "Cohere", "Mistral"
    };

    // Known locations (countries, major cities, regions)
    private static readonly HashSet<string> KnownLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "US", "UK", "EU", "China", "Japan", "India", "Russia", "Germany",
        "France", "Italy", "Spain", "Canada", "Australia", "Brazil", "Mexico",
        "California", "Texas", "New York", "Florida", "Washington",
        "London", "Paris", "Tokyo", "Beijing", "Shanghai", "Mumbai", "Delhi",
        "Berlin", "Moscow", "Sydney", "Toronto", "San Francisco", "Los Angeles",
        "Seattle", "Boston", "Chicago", "Miami", "Austin", "Denver",
        "Silicon Valley", "Wall Street", "Hollywood", "Capitol Hill",
        "Europe", "Asia", "Africa", "Middle East", "Latin America"
    };

    private static bool IsLikelyOrganization(string name)
    {
        // Check known organizations
        if (KnownOrganizations.Contains(name))
            return true;

        // Check for organization suffixes
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && OrgSuffixes.Contains(words[^1]))
            return true;

        // Check for "The X" pattern where X is capitalized (The New York Times)
        if (words.Length >= 2 && words[0].Equals("The", StringComparison.OrdinalIgnoreCase))
        {
            // Multi-word proper noun after "The" is likely an org
            if (words.Skip(1).All(w => char.IsUpper(w[0])))
                return true;
        }

        // All-caps acronym with 2-5 letters is likely an org (IBM, NASA, NATO)
        if (name.Length >= 2 && name.Length <= 5 && name.All(c => char.IsUpper(c) || c == '.'))
            return true;

        return false;
    }

    private static bool IsLikelyLocation(string name)
    {
        // Check known locations
        if (KnownLocations.Contains(name))
            return true;

        // Check for location patterns (City, State format)
        if (name.Contains(',') && name.Split(',').Length == 2)
        {
            var parts = name.Split(',');
            if (parts.All(p => char.IsUpper(p.Trim()[0])))
                return true;
        }

        return false;
    }

    private static bool IsLikelyPerson(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Person names are typically 2-4 words, all capitalized
        if (words.Length < 2 || words.Length > 4)
            return false;

        // All words should start with capital letter
        if (!words.All(w => w.Length > 0 && char.IsUpper(w[0])))
            return false;

        // Should not be a known org or location
        if (KnownOrganizations.Contains(name) || KnownLocations.Contains(name))
            return false;

        // Should not end with org suffixes
        if (OrgSuffixes.Contains(words[^1]))
            return false;

        // Common person name patterns: First Last, First Middle Last
        // Heuristic: if it's 2-3 capitalized words and not an org/location, probably a person
        if (words.Length >= 2 && words.Length <= 3)
            return true;

        return false;
    }
}
