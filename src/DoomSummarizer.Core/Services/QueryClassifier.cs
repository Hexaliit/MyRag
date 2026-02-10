using System.Diagnostics;
using System.Reflection;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
///     Embedding-based deterministic query classifier.
///     Pre-embeds representative questions per topic/type at startup (single ONNX batch call),
///     then classifies incoming queries via cosine similarity — no LLM needed.
///     Uses IDF-weighted multi-match voting: max_sim + CountBoost * log2(count) * idf(label).
///     IDF (Inverse Document Frequency) automatically corrects for label frequency imbalance —
///     rare types like "howto" get stronger count boosts than dominant types like "roundup".
///     Exemplars are loaded from embedded defaults + user YAML files in ~/.doomsummarizer/exemplars/.
///     All thresholds are configurable via <see cref="ClassifierConfig" /> (config YAML: classifier section).
/// </summary>
public class QueryClassifier
{
    private readonly ClassifierConfig _config;

    /// <summary>Flat list of all exemplar embeddings (scored once per query).</summary>
    private List<ExemplarEmbedding>? _allExemplars;

    /// <summary>IDF weights per topic label — rare topics get higher weight in count boost.</summary>
    private Dictionary<string, double>? _topicIdf;

    /// <summary>IDF weights per type label — rare types (howto, comparison) get higher weight.</summary>
    private Dictionary<string, double>? _typeIdf;

    private IEmbeddingService? _embedding;

    public QueryClassifier() : this(new ClassifierConfig()) { }

    public QueryClassifier(ClassifierConfig config)
    {
        _config = config ?? new ClassifierConfig();
    }

    /// <summary>Whether the classifier has been initialized with embeddings.</summary>
    public bool IsInitialized => _allExemplars != null;

    /// <summary>Number of exemplars loaded.</summary>
    public int ExemplarCount => _allExemplars?.Count ?? 0;

    /// <summary>
    ///     Load all exemplar YAML files (embedded defaults + user overrides) and
    ///     batch-embed them in a single ONNX call.
    /// </summary>
    public async Task InitializeAsync(IEmbeddingService embedding, CancellationToken ct = default)
    {
        _embedding = embedding;

        var exemplars = LoadAllExemplars();
        if (exemplars.Count == 0)
        {
            Debug.WriteLine("QueryClassifier: no exemplars found");
            return;
        }

        // Batch-embed all exemplar questions in one ONNX call
        var questions = exemplars.Select(e => e.Question).ToList();
        var sw = Stopwatch.StartNew();
        var embeddings = await embedding.EmbedBatchAsync(questions, ct);
        sw.Stop();
        Debug.WriteLine($"QueryClassifier: embedded {questions.Count} exemplars in {sw.ElapsedMilliseconds}ms");

        // Build flat list — scored linearly per query (SIMD cosine sim is fast enough)
        _allExemplars = new List<ExemplarEmbedding>(exemplars.Count);
        for (var i = 0; i < exemplars.Count; i++)
            _allExemplars.Add(new ExemplarEmbedding(exemplars[i], embeddings[i]));

        // Compute IDF weights per label dimension (inverse document frequency)
        // idf(label) = log2(1 + total / count_for_label) — rare labels get higher weight
        _topicIdf = ComputeIdf(_allExemplars, e => e.Exemplar.Topic);
        _typeIdf = ComputeIdf(_allExemplars, e => e.Exemplar.Type);

        var topicCount = _allExemplars.Select(e => e.Exemplar.Topic).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var typeCount = _allExemplars.Select(e => e.Exemplar.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Debug.WriteLine(
            $"QueryClassifier: {topicCount} topics, {typeCount} types, {_allExemplars.Count} total exemplars");

        // Log IDF weights for transparency
        foreach (var (type, idf) in _typeIdf.OrderByDescending(kv => kv.Value))
            Debug.WriteLine($"  IDF({type}) = {idf:F2}");
    }

    /// <summary>
    ///     Classify a query using multi-match weighted voting.
    ///     1. Score ALL exemplars against query embedding (single pass)
    ///     2. Take candidates above threshold → candidate set
    ///     3. Weighted vote per dimension: max_sim + 0.05 * (count - 1)
    ///     4. Detect composite/complex from type/complexity votes
    /// </summary>
    public async Task<QueryClassification> ClassifyAsync(string query, CancellationToken ct = default)
    {
        if (_allExemplars == null || _embedding == null)
            return new QueryClassification();

        // 0. Feature decomposition (runs in parallel with embedding conceptually — sub-0.02ms)
        var features = QueryFeatures.Extract(query);
        var embeddingInput = _config.SynonymExpansionEnabled
            ? QueryFeatures.ExpandSynonyms(query, _config.ShortQueryMaxWords + 1)
            : query;
        var queryEmbedding = await _embedding.EmbedAsync(embeddingInput, ct);

        // 1. Score ALL exemplars in a single pass — no centroid pre-filter.
        //    With SIMD cosine sim, scoring ~450 exemplars is <1ms.
        //    Simultaneously track: candidates, best match, vibe, composite top-2, complex.
        var scored = new List<ScoredExemplarInternal>(_allExemplars.Count / 3);
        ScoredExemplarInternal? bestOverall = null;
        ScoredExemplarInternal? bestVibeMatch = null;
        double compositeTop1 = 0, compositeTop2 = 0;
        var isComplex = false;

        foreach (var exemplar in _allExemplars)
        {
            var sim = (double)VectorMath.CosineSimilarity(queryEmbedding, exemplar.Embedding);
            if (sim < _config.MinCandidateThreshold)
                continue;

            var item = new ScoredExemplarInternal(exemplar, sim);
            scored.Add(item);

            if (bestOverall == null || sim > bestOverall.Score)
                bestOverall = item;

            // Vibe: track best vibe match by raw similarity
            if (exemplar.Exemplar.Vibe != null
                && (bestVibeMatch == null || sim > bestVibeMatch.Score))
                bestVibeMatch = item;

            // Composite: track top 2 scores for consensus check
            if (exemplar.Exemplar.Type.Equals("composite", StringComparison.OrdinalIgnoreCase))
            {
                if (sim > compositeTop1)
                {
                    compositeTop2 = compositeTop1;
                    compositeTop1 = sim;
                }
                else if (sim > compositeTop2)
                {
                    compositeTop2 = sim;
                }
            }

            // Complex: any match above threshold
            if (!isComplex && exemplar.Exemplar.Complexity == "complex"
                && sim > _config.ComplexThreshold)
                isComplex = true;
        }

        if (scored.Count == 0)
        {
            return new QueryClassification
            {
                Categories = new Dictionary<string, double>(),
                QueryType = "roundup",
                QueryTypeConfidence = 0
            };
        }

        // 2. Multi-match weighted vote for each dimension (IDF-weighted)
        var topicScores = WeightedVote(scored, s => s.Exemplar.Exemplar.Topic, _topicIdf);
        var typeScores = WeightedVote(scored, s => s.Exemplar.Exemplar.Type, _typeIdf);

        // Filter topic scores below threshold
        var filteredTopics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, score) in topicScores)
        {
            if (score >= _config.MinTopicThreshold)
                filteredTopics[topic] = score;
        }

        // 3. Feature-based type score adjustments for short queries
        var isShort = features.IsShortQuery(_config.ShortQueryMaxWords);
        if (isShort)
        {
            if (features.HasHowtoMarker)
            {
                typeScores.TryGetValue("howto", out var howtoScore);
                typeScores["howto"] = howtoScore + _config.HowtoFeatureBoost;
            }

            if (features.HasComparisonMarker)
            {
                typeScores.TryGetValue("comparison", out var compScore);
                typeScores["comparison"] = compScore + _config.ComparisonFeatureBoost;
            }

            if (features.HasQaMarker && !features.HasSearchOnlyMarker)
            {
                typeScores.TryGetValue("qa", out var qaScore);
                typeScores["qa"] = qaScore + _config.QaFeatureBoost;
            }

            // Short queries without intent markers are overwhelmingly roundups
            if (!features.HasQuestionWord && !features.HasComparisonMarker
                && !features.HasHowtoMarker && !features.HasSearchOnlyMarker
                && !features.HasQaMarker)
            {
                typeScores.TryGetValue("roundup", out var roundupScore);
                typeScores["roundup"] = roundupScore + _config.DefaultRoundupBoost;
            }
        }

        // Feature-based search_only fast path (applies to all query lengths)
        var forceSearchOnly = false;
        if (features.HasSearchOnlyMarker)
        {
            var topEmbeddingType = bestOverall?.Exemplar.Exemplar.Type;
            if (bestOverall == null || bestOverall.Score < 0.85
                || topEmbeddingType == "search_only" || topEmbeddingType == "qa")
                forceSearchOnly = true;
        }

        // 4. Best type — exclude composite (handled by IsComposite flag)
        var bestType = "roundup";
        var bestTypeScore = 0.0;
        foreach (var (type, score) in typeScores)
        {
            if (type.Equals("composite", StringComparison.OrdinalIgnoreCase))
                continue;

            if (score > bestTypeScore && score >= _config.MinTypeThreshold)
            {
                bestTypeScore = score;
                bestType = type;
            }
        }

        if (forceSearchOnly)
        {
            bestType = "search_only";
            bestTypeScore = Math.Max(bestTypeScore, _config.SearchOnlyFeatureThreshold);
        }

        // 5. Vibe from single-pass tracking
        string? vibe = null;
        var vibeConfidence = 0.0;
        if (bestVibeMatch != null && bestVibeMatch.Score > _config.VibeThreshold)
        {
            vibe = bestVibeMatch.Exemplar.Exemplar.Vibe;
            vibeConfidence = bestVibeMatch.Score;
        }

        // 6. Composite from single-pass top-2 consensus
        var compositeThreshold = _config.CompositeRawThreshold;
        if (features.HasCompositeConjunction && features.WordCount >= 5)
            compositeThreshold *= 0.85;
        var isComposite = compositeTop2 > 0
                          && compositeTop1 > compositeThreshold
                          && compositeTop2 > compositeThreshold * 0.85;

        // 7. Source hints from best match
        List<string>? sourceHints = bestOverall?.Exemplar.Exemplar.Sources;

        // 8. Top matches for debug output
        var topMatches = scored
            .OrderByDescending(s => s.Score)
            .Take(5)
            .Select(s => new ScoredExemplar(
                s.Exemplar.Exemplar.Question,
                s.Exemplar.Exemplar.Topic,
                s.Exemplar.Exemplar.Type,
                s.Score))
            .ToList();

        // 9. Short-query confidence scaling
        if (isShort)
            bestTypeScore *= _config.ShortQueryConfidenceScale;

        return new QueryClassification
        {
            Categories = filteredTopics,
            QueryType = bestType,
            QueryTypeConfidence = bestTypeScore,
            Vibe = vibe,
            VibeConfidence = vibeConfidence,
            IsComposite = isComposite,
            IsComplex = isComplex,
            SourceHints = sourceHints,
            BestMatch = bestOverall?.Exemplar.Exemplar.Question,
            BestMatchScore = bestOverall?.Score ?? 0,
            TopMatches = topMatches,
            Features = isShort ? features : null
        };
    }

    /// <summary>
    ///     IDF-weighted voting: score = max_sim + CountBoost * log2(count) * idf(label).
    ///     Combines three statistical principles:
    ///     1. Max anchoring — best individual match dominates
    ///     2. Logarithmic count — diminishing returns from additional matches
    ///     3. IDF weighting — rare labels (howto, comparison) get stronger count boosts
    ///        than frequent labels (roundup) to correct for class imbalance
    /// </summary>
    private Dictionary<string, double> WeightedVote(
        List<ScoredExemplarInternal> scored,
        Func<ScoredExemplarInternal, string> labelSelector,
        Dictionary<string, double>? idf = null)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var maxByLabel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var countByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scored)
        {
            var label = labelSelector(item);

            if (!maxByLabel.TryGetValue(label, out var currentMax) || item.Score > currentMax)
                maxByLabel[label] = item.Score;

            countByLabel.TryGetValue(label, out var count);
            countByLabel[label] = count + 1;
        }

        foreach (var (label, maxSim) in maxByLabel)
        {
            var count = countByLabel[label];
            var labelIdf = idf?.GetValueOrDefault(label, 1.0) ?? 1.0;
            // IDF-weighted log2 count boost:
            //   roundup (idf≈1.5): 40 matches → 0.05 * 5.3 * 1.5 = 0.40
            //   howto   (idf≈4.8): 5 matches  → 0.05 * 2.3 * 4.8 = 0.55
            // Rare types get stronger boost per match, correcting for class imbalance
            result[label] = maxSim + _config.CountBoost * Math.Log2(Math.Max(count, 1)) * labelIdf;
        }

        return result;
    }

    /// <summary>
    ///     Compute IDF (Inverse Document Frequency) weights for each label value.
    ///     idf(label) = log2(1 + total / count_for_label)
    ///     Rare labels get higher weight, frequent labels get lower weight.
    /// </summary>
    private static Dictionary<string, double> ComputeIdf(
        List<ExemplarEmbedding> exemplars,
        Func<ExemplarEmbedding, string> labelSelector)
    {
        var total = (double)exemplars.Count;
        return exemplars
            .GroupBy(e => labelSelector(e), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Math.Log2(1.0 + total / g.Count()),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Load exemplars from embedded defaults + user YAML files.
    ///     User files in ~/.doomsummarizer/exemplars/ are loaded after defaults.
    ///     If a user file contains exemplars with the same question text, they override the default.
    /// </summary>
    internal static List<QueryExemplar> LoadAllExemplars()
    {
        var exemplars = new List<QueryExemplar>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Load embedded defaults
        var embedded = LoadEmbeddedExemplars();
        foreach (var e in embedded)
        {
            if (seen.Add(e.Question))
                exemplars.Add(e);
        }

        // 2. Load user exemplars from ~/.doomsummarizer/exemplars/
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        if (Directory.Exists(userDir))
        {
            var userFiles = Directory.GetFiles(userDir, "*.yaml")
                .Concat(Directory.GetFiles(userDir, "*.yml"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var file in userFiles)
            {
                try
                {
                    var userExemplars = LoadExemplarsFromFile(file);
                    foreach (var e in userExemplars)
                    {
                        if (seen.Add(e.Question))
                        {
                            exemplars.Add(e);
                        }
                        else
                        {
                            // Override: replace existing with user's version
                            var idx = exemplars.FindIndex(x =>
                                x.Question.Equals(e.Question, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                                exemplars[idx] = e;
                        }
                    }

                    Debug.WriteLine($"QueryClassifier: loaded {userExemplars.Count} exemplars from {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"QueryClassifier: failed to load {file}: {ex.Message}");
                }
            }
        }

        return exemplars;
    }

    private static List<QueryExemplar> LoadEmbeddedExemplars()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("exemplars") && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var all = new List<QueryExemplar>();
        var deserializer = CreateDeserializer();

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            try
            {
                var doc = deserializer.Deserialize<ExemplarDocument>(yaml);
                if (doc?.Exemplars != null)
                    all.AddRange(doc.Exemplars);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QueryClassifier: failed to parse embedded {resourceName}: {ex.Message}");
            }
        }

        return all;
    }

    internal static List<QueryExemplar> LoadExemplarsFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = CreateDeserializer();
        var doc = deserializer.Deserialize<ExemplarDocument>(yaml);
        return doc?.Exemplars ?? [];
    }

    private static IDeserializer CreateDeserializer()
    {
        return new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private record ExemplarEmbedding(QueryExemplar Exemplar, float[] Embedding);

    private record ScoredExemplarInternal(ExemplarEmbedding Exemplar, double Score);
}
