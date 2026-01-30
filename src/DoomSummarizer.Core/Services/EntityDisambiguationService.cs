using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

// --- Models ---

public record EntityFeatures(int ItemIndex, string? OrgName, string? Location, string? Industry, string? Description);

public record EntityCluster
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ContentItem> Items { get; init; } = [];
    public double AverageRelevance { get; init; }
    public List<string> Locations { get; init; } = [];
    public List<string> Domains { get; init; } = [];
}

public record DisambiguationResult
{
    public bool IsAmbiguous { get; init; }
    public bool TooMany { get; init; }
    public List<EntityCluster> Clusters { get; init; } = [];
    public string Method { get; init; } = "";
}

// --- Service ---

public class EntityDisambiguationService
{
    private const double ClusterThreshold = 0.65;

    /// <summary>
    /// Full disambiguation with sentinel LLM feature extraction.
    /// Use in AskCommand where an interactive LLM call is acceptable.
    /// Falls back: sentinel → NER → domain+title → ONNX embed → cluster.
    /// </summary>
    public async Task<DisambiguationResult> DisambiguateAsync(
        List<ContentItem> evidence,
        string query,
        IEmbeddingService embedding,
        StorageService storage,
        OllamaService? ollama,
        bool ollamaAvailable,
        CancellationToken ct)
    {
        if (TrySkip(evidence, query) is { } skip)
            return skip;

        // Tiered extraction: sentinel LLM → NER → domain+title
        var (features, method) = await ExtractFeaturesFullAsync(
            evidence, query, ollama, ollamaAvailable, ct);

        return await ClusterAndLabelAsync(evidence, features, method, embedding, storage);
    }

    /// <summary>
    /// Fast deterministic disambiguation — zero LLM/NER calls.
    /// Uses only data already on ContentItem (topic, domain, title, existing embedding).
    /// Fingerprints are embedded via ONNX (~1ms each) with feature_cache.
    /// Use in ScrollCommand where the sentinel already contributed upfront.
    /// </summary>
    public async Task<DisambiguationResult> DisambiguateFastAsync(
        List<ContentItem> evidence,
        string query,
        IEmbeddingService embedding,
        StorageService storage)
    {
        if (TrySkip(evidence, query) is { } skip)
            return skip;

        // Build features from existing ContentItem metadata — no model calls
        var features = ExtractExistingFeatures(evidence);

        return await ClusterAndLabelAsync(evidence, features, "existing+embedding", embedding, storage);
    }

    // --- Common Pipeline ---

    private static DisambiguationResult? TrySkip(List<ContentItem> evidence, string query)
    {
        if (evidence.Count == 0)
            return new DisambiguationResult { IsAmbiguous = false, Clusters = [], Method = "skip" };

        if (evidence.Count < 4)
        {
            return new DisambiguationResult
            {
                IsAmbiguous = false,
                Clusters =
                [
                    new EntityCluster
                    {
                        Items = evidence,
                        Label = query,
                        AverageRelevance = evidence.Average(e => e.RelevanceScore)
                    }
                ],
                Method = "skip"
            };
        }

        return null; // don't skip
    }

    private async Task<DisambiguationResult> ClusterAndLabelAsync(
        List<ContentItem> evidence,
        List<EntityFeatures> features,
        string method,
        IEmbeddingService embedding,
        StorageService storage)
    {
        var featureEmbeddings = await EmbedFeaturesAsync(features, embedding, storage);
        var clusters = ClusterByCentroid(evidence, features, featureEmbeddings);
        LabelClusters(clusters, evidence, features);

        return new DisambiguationResult
        {
            IsAmbiguous = clusters.Count >= 2,
            TooMany = clusters.Count >= 4,
            Clusters = clusters,
            Method = method
        };
    }

    // --- Feature Extraction: Full (LLM → NER → Domain) ---

    private async Task<(List<EntityFeatures>, string)> ExtractFeaturesFullAsync(
        List<ContentItem> evidence,
        string query,
        OllamaService? ollama,
        bool ollamaAvailable,
        CancellationToken ct)
    {
        if (ollamaAvailable && ollama != null)
        {
            var sentinelFeatures = await TrySentinelExtractionAsync(evidence, query, ollama, ct);
            if (sentinelFeatures != null)
                return (sentinelFeatures, "sentinel+embedding");
        }

        var nerFeatures = await TryNerExtractionAsync(evidence, ct);
        if (nerFeatures != null)
            return (nerFeatures, "ner+embedding");

        return (ExtractDomainFeatures(evidence), "domain+embedding");
    }

    // --- Feature Extraction: Fast (existing ContentItem data only) ---

    /// <summary>
    /// Build features from data already on ContentItem — zero model calls.
    /// Uses: DetectedTopic, URL domain, Source, Title keywords.
    /// </summary>
    private static List<EntityFeatures> ExtractExistingFeatures(List<ContentItem> evidence)
    {
        var features = new List<EntityFeatures>(evidence.Count);
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];
            var domain = ExtractDomain(item.Url);
            var topic = item.DetectedTopic;

            // Use domain as a proxy for org, topic as industry
            // Description combines title + source for fingerprinting
            var desc = $"{item.Title} [{domain}]";
            if (!string.IsNullOrEmpty(item.Source))
                desc += $" ({item.Source})";

            features.Add(new EntityFeatures(
                i,
                OrgName: null, // no org extraction without NER/LLM
                Location: null,
                Industry: topic,
                Description: desc));
        }
        return features;
    }

    // --- Feature Extraction: Sentinel LLM ---

    private static async Task<List<EntityFeatures>?> TrySentinelExtractionAsync(
        List<ContentItem> evidence,
        string query,
        OllamaService ollama,
        CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Extract entity features from these search results about \"{query}\".");
            sb.AppendLine();
            sb.AppendLine("Items:");

            for (var i = 0; i < evidence.Count; i++)
            {
                var item = evidence[i];
                var domain = ExtractDomain(item.Url);
                var summary = item.Summary ?? item.Content ?? item.Title;
                if (summary.Length > 200)
                    summary = summary[..200];
                sb.AppendLine($"[{i}] \"{item.Title}\" [{domain}]: {summary}");
            }

            sb.AppendLine();
            sb.AppendLine("For each, extract the primary entity discussed.");
            sb.AppendLine("Respond JSON only: {\"items\":[{\"idx\":0,\"org\":\"name\",\"loc\":\"location\",\"industry\":\"sector\",\"desc\":\"one phrase\"},...]}");
            sb.AppendLine("Use \"unknown\" for missing fields.");

            var response = await ollama.SentinelGenerateAsync(sb.ToString(), null, 0.1, ct);

            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            var parsed = JsonSerializer.Deserialize(jsonStr, OllamaJsonContext.Default.FeatureExtractionResponse);
            if (parsed?.Items == null || parsed.Items.Count == 0)
                return null;

            // Build validated lookup — reject out-of-range Idx values
            var lookup = new Dictionary<int, FeatureExtractionItem>();
            foreach (var item in parsed.Items)
            {
                if (item.Idx >= 0 && item.Idx < evidence.Count)
                    lookup.TryAdd(item.Idx, item);
            }

            // Normalize: exactly evidence.Count features, fill gaps with domain fallback
            var features = new List<EntityFeatures>(evidence.Count);
            for (var i = 0; i < evidence.Count; i++)
            {
                if (lookup.TryGetValue(i, out var llmItem))
                {
                    features.Add(new EntityFeatures(
                        i,
                        NormalizeUnknown(llmItem.Org),
                        NormalizeUnknown(llmItem.Loc),
                        NormalizeUnknown(llmItem.Industry),
                        NormalizeUnknown(llmItem.Desc)));
                }
                else
                {
                    var domain = ExtractDomain(evidence[i].Url);
                    features.Add(new EntityFeatures(i, null, null, null,
                        $"{evidence[i].Title} [{domain}]"));
                }
            }

            return features;
        }
        catch
        {
            return null;
        }
    }

    // --- Feature Extraction: NER ---

    private static async Task<List<EntityFeatures>?> TryNerExtractionAsync(
        List<ContentItem> evidence,
        CancellationToken ct)
    {
        using var ner = new NerService();
        if (!ner.IsAvailable)
            return null;

        try
        {
            await ner.InitializeAsync(ct);

            var features = new List<EntityFeatures>(evidence.Count);
            for (var i = 0; i < evidence.Count; i++)
            {
                var item = evidence[i];
                var text = $"{item.Title} {item.Summary ?? item.Content ?? ""}";
                if (text.Length > 512)
                    text = text[..512];

                var entities = await ner.ExtractEntitiesAsync(text, ct);

                features.Add(new EntityFeatures(
                    i,
                    entities.FirstOrDefault(e => e.Type == "ORG")?.Text,
                    entities.FirstOrDefault(e => e.Type == "LOC")?.Text,
                    entities.FirstOrDefault(e => e.Type == "MISC")?.Text,
                    null));
            }

            return features.Any(f => f.OrgName != null) ? features : null;
        }
        catch
        {
            return null;
        }
    }

    // --- Feature Extraction: Domain + Title (fallback) ---

    private static List<EntityFeatures> ExtractDomainFeatures(List<ContentItem> evidence)
    {
        var features = new List<EntityFeatures>(evidence.Count);
        for (var i = 0; i < evidence.Count; i++)
        {
            var domain = ExtractDomain(evidence[i].Url);
            features.Add(new EntityFeatures(i, null, null, null,
                $"{evidence[i].Title} [{domain}]"));
        }
        return features;
    }

    // --- Feature Embedding with KV Cache ---

    private static async Task<float[][]> EmbedFeaturesAsync(
        List<EntityFeatures> features,
        IEmbeddingService embedding,
        StorageService storage)
    {
        var embeddings = new float[features.Count][];
        var termsToCache = new List<(string term, string category)>();

        for (var i = 0; i < features.Count; i++)
        {
            var fingerprint = BuildFingerprint(features[i]);
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                embeddings[i] = new float[384];
                continue;
            }

            // Check cache first
            var cached = await storage.GetCachedFeatureEmbeddingAsync(fingerprint);
            if (cached.HasValue)
            {
                embeddings[i] = EmbeddingCompat.FromBytes(cached.Value.embedding);
            }
            else
            {
                embeddings[i] = await embedding.EmbedAsync(fingerprint);
                await storage.UpsertFeatureCacheAsync(fingerprint, null,
                    EmbeddingCompat.ToBytes(embeddings[i]));
            }

            // Collect individual terms for batch caching
            if (!string.IsNullOrWhiteSpace(features[i].OrgName))
                termsToCache.Add((features[i].OrgName!, "ORG"));
            if (!string.IsNullOrWhiteSpace(features[i].Location))
                termsToCache.Add((features[i].Location!, "LOC"));
            if (!string.IsNullOrWhiteSpace(features[i].Industry))
                termsToCache.Add((features[i].Industry!, "INDUSTRY"));
        }

        // Batch cache individual terms (deduplicated)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, category) in termsToCache)
        {
            if (!seen.Add(term)) continue;
            var existing = await storage.GetCachedFeatureEmbeddingAsync(term);
            if (existing.HasValue) continue;
            var emb = await embedding.EmbedAsync(term);
            await storage.UpsertFeatureCacheAsync(term, category, EmbeddingCompat.ToBytes(emb));
        }

        return embeddings;
    }

    private static string BuildFingerprint(EntityFeatures f)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.OrgName)) parts.Add(f.OrgName);
        if (!string.IsNullOrWhiteSpace(f.Location)) parts.Add(f.Location);
        if (!string.IsNullOrWhiteSpace(f.Industry)) parts.Add(f.Industry);
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(f.Description))
            parts.Add(f.Description);
        return string.Join(" | ", parts);
    }

    // --- Centroid-Based Clustering ---

    private static List<EntityCluster> ClusterByCentroid(
        List<ContentItem> evidence,
        List<EntityFeatures> features,
        float[][] featureEmbeddings)
    {
        var indices = Enumerable.Range(0, evidence.Count)
            .OrderByDescending(i => evidence[i].RelevanceScore)
            .ToList();

        var clusterCentroids = new List<float[]>();
        var clusterMembers = new List<List<int>>();

        foreach (var idx in indices)
        {
            var emb = featureEmbeddings[idx];
            if (IsZeroVector(emb))
                continue;

            var bestSim = -1.0f;
            var bestCluster = -1;
            for (var c = 0; c < clusterCentroids.Count; c++)
            {
                var sim = VectorMath.CosineSimilarity(emb, clusterCentroids[c]);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestCluster = c;
                }
            }

            if (bestSim >= ClusterThreshold && bestCluster >= 0)
            {
                clusterMembers[bestCluster].Add(idx);
                var centroid = clusterCentroids[bestCluster];
                var count = clusterMembers[bestCluster].Count;
                for (var d = 0; d < centroid.Length; d++)
                    centroid[d] = ((centroid[d] * (count - 1)) + emb[d]) / count;
                L2Normalize(centroid);
            }
            else
            {
                var centroid = (float[])emb.Clone();
                L2Normalize(centroid);
                clusterCentroids.Add(centroid);
                clusterMembers.Add([idx]);
            }
        }

        // Collect orphans: zero-vectors + singletons
        var allAssigned = new HashSet<int>(clusterMembers.SelectMany(m => m));
        var orphans = new List<int>();
        for (var i = 0; i < evidence.Count; i++)
        {
            if (!allAssigned.Contains(i))
                orphans.Add(i);
        }
        for (var c = clusterMembers.Count - 1; c >= 0; c--)
        {
            if (clusterMembers[c].Count == 1)
            {
                orphans.Add(clusterMembers[c][0]);
                clusterMembers.RemoveAt(c);
                clusterCentroids.RemoveAt(c);
            }
        }

        // Assign orphans to nearest multi-member cluster
        if (clusterCentroids.Count > 0)
        {
            foreach (var orphanIdx in orphans)
            {
                var emb = featureEmbeddings[orphanIdx];
                var bestCluster = 0;
                var bestSim = -1.0f;
                for (var c = 0; c < clusterCentroids.Count; c++)
                {
                    var sim = IsZeroVector(emb) ? 0f
                        : VectorMath.CosineSimilarity(emb, clusterCentroids[c]);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        bestCluster = c;
                    }
                }
                clusterMembers[bestCluster].Add(orphanIdx);
            }
        }

        // Build cluster objects
        var clusters = new List<EntityCluster>();
        for (var c = 0; c < clusterMembers.Count; c++)
        {
            var memberIndices = clusterMembers[c];
            if (memberIndices.Count == 0) continue;

            var items = memberIndices.Select(i => evidence[i]).ToList();
            var locations = memberIndices
                .Select(i => features[i].Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
            var domains = items
                .Select(i => ExtractDomain(i.Url))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;

            clusters.Add(new EntityCluster
            {
                Items = items,
                AverageRelevance = items.Average(i => i.RelevanceScore),
                Locations = locations!,
                Domains = domains!
            });
        }

        if (clusters.Count == 0)
        {
            clusters.Add(new EntityCluster
            {
                Items = evidence.ToList(),
                AverageRelevance = evidence.Average(e => e.RelevanceScore),
                Label = "All results"
            });
        }

        clusters.Sort((a, b) => b.AverageRelevance.CompareTo(a.AverageRelevance));
        return clusters;
    }

    // --- Cluster Labeling ---

    private static void LabelClusters(
        List<EntityCluster> clusters,
        List<ContentItem> evidence,
        List<EntityFeatures> features)
    {
        var evidenceIndex = new Dictionary<ContentItem, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < evidence.Count; i++)
            evidenceIndex[evidence[i]] = i;

        foreach (var cluster in clusters)
        {
            if (!string.IsNullOrEmpty(cluster.Label))
                continue;

            var bestItem = cluster.Items.OrderByDescending(i => i.RelevanceScore).First();

            if (evidenceIndex.TryGetValue(bestItem, out var bestIdx) && bestIdx < features.Count)
            {
                var f = features[bestIdx];
                var labelParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(f.OrgName)) labelParts.Add(f.OrgName);

                var contextParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(f.Location)) contextParts.Add(f.Location);
                if (!string.IsNullOrWhiteSpace(f.Industry)) contextParts.Add(f.Industry);

                // For fast path: use top domain as additional context
                if (contextParts.Count == 0 && cluster.Domains.Count > 0)
                    contextParts.Add(cluster.Domains[0]);

                if (labelParts.Count > 0 && contextParts.Count > 0)
                    cluster.Label = $"{string.Join(", ", labelParts)} ({string.Join(", ", contextParts)})";
                else if (labelParts.Count > 0)
                    cluster.Label = string.Join(", ", labelParts);
                else if (contextParts.Count > 0)
                    cluster.Label = $"{TruncateTitle(bestItem.Title)} ({string.Join(", ", contextParts)})";
                else
                    cluster.Label = TruncateTitle(bestItem.Title);

                cluster.Description = f.Description ?? "";
            }
            else
            {
                cluster.Label = TruncateTitle(bestItem.Title);
            }
        }
    }

    // --- Helpers ---

    private static string TruncateTitle(string title)
        => title.Length > 60 ? title[..57] + "..." : title;

    private static string ExtractDomain(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return ""; }
    }

    private static string? NormalizeUnknown(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null : value;

    private static bool IsZeroVector(float[] v)
    {
        for (var i = 0; i < v.Length; i++)
            if (v[i] != 0f) return false;
        return true;
    }

    private static void L2Normalize(float[] v)
    {
        var norm = 0f;
        for (var i = 0; i < v.Length; i++)
            norm += v[i] * v[i];
        norm = MathF.Sqrt(norm);
        if (norm <= 0) return;
        for (var i = 0; i < v.Length; i++)
            v[i] /= norm;
    }
}
