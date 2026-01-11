using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mostlylucid.GraphRag.Extraction;

/// <summary>
///     ONNX-based Named Entity Recognition service.
///     Uses transformer models (BERT-based) for entity span detection.
///     The NER model finds WHERE entities are in the text (spans).
///     Entity TYPE classification is done separately using EntityTypeProfiles.
/// </summary>
public sealed class OnnxNerService : IDisposable
{
    // BIO tag patterns
    private static readonly Regex BioTagRx = new(@"^([BI])-(.+)$", RegexOptions.Compiled);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly int _maxSequenceLength;
    private readonly NerModelInfo _modelInfo;
    private readonly string _modelPath;
    private bool _initialized;
    private string[]? _labels;
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;

    public OnnxNerService(string modelPath, NerModelInfo? modelInfo = null, int maxSequenceLength = 512)
    {
        _modelPath = modelPath;
        _modelInfo = modelInfo ?? NerModelRegistry.BertBaseNer;
        _maxSequenceLength = Math.Min(maxSequenceLength, _modelInfo.MaxSequenceLength);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }

    /// <summary>
    ///     Initialize the NER model and tokenizer.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var modelFile = Path.Combine(_modelPath, _modelInfo.ModelFile);
            var tokenizerFile = Path.Combine(_modelPath, _modelInfo.TokenizerFile);
            var configFile = Path.Combine(_modelPath, "config.json");

            if (!File.Exists(modelFile))
                throw new FileNotFoundException($"NER model not found: {modelFile}");

            // Load model with limited threading to prevent CPU overload
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount), // Limit to 4 threads max
                InterOpNumThreads = 1 // Single inter-op thread
            };
            _session = new InferenceSession(modelFile, options);

            // Debug: Log model inputs/outputs
            Console.WriteLine($"[NER] Model inputs: {string.Join(", ", _session.InputNames)}");
            Console.WriteLine($"[NER] Model outputs: {string.Join(", ", _session.OutputNames)}");

            // Load tokenizer using Microsoft.ML.Tokenizers
            if (!File.Exists(tokenizerFile))
                throw new FileNotFoundException($"Tokenizer not found: {tokenizerFile}");

            // Configure BertTokenizer for CASED model (bert-base-NER is cased)
            var bertOptions = new BertOptions
            {
                LowerCaseBeforeTokenization = false, // CRITICAL: bert-base-NER is a cased model
                ClassificationToken = "[CLS]",
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                UnknownToken = "[UNK]",
                MaskingToken = "[MASK]"
            };

            using var stream = File.OpenRead(tokenizerFile);
            _tokenizer = BertTokenizer.Create(stream, bertOptions);

            // Load label mapping from config.json
            if (File.Exists(configFile))
            {
                var configJson = await File.ReadAllTextAsync(configFile, ct);
                var config = JsonDocument.Parse(configJson);
                if (config.RootElement.TryGetProperty("id2label", out var id2label))
                {
                    var maxId = id2label.EnumerateObject().Max(p => int.Parse(p.Name));
                    _labels = new string[maxId + 1];
                    foreach (var prop in id2label.EnumerateObject())
                        _labels[int.Parse(prop.Name)] = prop.Value.GetString() ?? "O";
                }
            }

            // Fallback to default labels if config doesn't have id2label
            _labels ??= _modelInfo.DefaultLabels;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Extract entity spans from text.
    ///     Returns raw spans with BIO-derived entity types.
    /// </summary>
    public async Task<List<EntitySpan>> ExtractSpansAsync(string text, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        Console.WriteLine(
            $"[NER] ExtractSpansAsync starting for {text.Length} chars: \"{text.Substring(0, Math.Min(100, text.Length))}...\"");

        await InitializeAsync(ct);
        Console.WriteLine($"[NER] Initialized in {totalSw.ElapsedMilliseconds}ms");

        if (_session == null || _tokenizer == null || _labels == null)
            throw new InvalidOperationException("NER model not initialized");

        // Tokenize using Microsoft.ML.Tokenizers
        var sw = Stopwatch.StartNew();
        var encoded = _tokenizer.EncodeToTokens(text, out var normalizedText);

        // Get CLS and SEP token IDs for BERT format: [CLS] tokens [SEP]
        // Standard BERT vocab has [CLS]=101, [SEP]=102, [PAD]=0
        // We encode them to get the correct IDs for this vocab
        var clsTokens = _tokenizer.EncodeToTokens("[CLS]", out _);
        var sepTokens = _tokenizer.EncodeToTokens("[SEP]", out _);
        var clsId = clsTokens.Count > 0 ? clsTokens[0].Id : 101;
        var sepId = sepTokens.Count > 0 ? sepTokens[0].Id : 102;

        // Build full sequence: [CLS] + content tokens + [SEP]
        var contentIds = encoded.Select(t => t.Id).ToArray();
        var rawIds = new int[contentIds.Length + 2];
        rawIds[0] = clsId; // [CLS] at start
        Array.Copy(contentIds, 0, rawIds, 1, contentIds.Length);
        rawIds[rawIds.Length - 1] = sepId; // [SEP] at end
        var rawLength = rawIds.Length;

        // Build tokens array with special tokens for entity extraction
        var contentTokens = encoded.Select(t => t.Value).ToArray();
        var tokens = new string[contentTokens.Length + 2];
        tokens[0] = "[CLS]";
        Array.Copy(contentTokens, 0, tokens, 1, contentTokens.Length);
        tokens[tokens.Length - 1] = "[SEP]";

        Console.WriteLine($"[NER] Tokens: [{string.Join(", ", tokens.Take(10))}...]");

        // Smart bucketing: pad to nearest power-of-2-ish bucket for efficiency
        // Buckets: 32, 64, 128, 256, 512
        var buckets = new[] { 32, 64, 128, 256, 512 };
        var targetLength = buckets.FirstOrDefault(b => b >= rawLength);
        if (targetLength == 0) targetLength = 512;

        // Truncate if longer than max
        if (rawLength > _maxSequenceLength)
        {
            targetLength = _maxSequenceLength;
            rawLength = _maxSequenceLength;
        }

        // Pad to bucket size
        var inputIds = new int[targetLength];
        var attentionMask = new int[targetLength];

        Array.Copy(rawIds, inputIds, Math.Min(rawLength, targetLength));

        // Attention mask: 1 for real tokens, 0 for padding
        for (var i = 0; i < targetLength; i++) attentionMask[i] = i < rawLength ? 1 : 0;

        Console.WriteLine(
            $"[NER] Tokenized in {sw.ElapsedMilliseconds}ms ({rawLength} tokens → {targetLength} bucket, {targetLength - rawLength} padding)");

        // Create tensors with bucketed length
        sw.Restart();
        var inputIdsLong = new long[targetLength];
        var attentionMaskLong = new long[targetLength];
        for (var i = 0; i < targetLength; i++)
        {
            inputIdsLong[i] = inputIds[i];
            attentionMaskLong[i] = attentionMask[i];
        }

        var inputIdsTensor = new DenseTensor<long>(inputIdsLong, new[] { 1, targetLength });
        var attentionMaskTensor = new DenseTensor<long>(attentionMaskLong, new[] { 1, targetLength });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        // Only add token_type_ids if the model expects it
        if (_session.InputNames.Contains("token_type_ids"))
        {
            var tokenTypeIds = new long[targetLength]; // All zeros for single sentence
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, new[] { 1, targetLength });
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor));
        }

        Console.WriteLine($"[NER] Created tensors in {sw.ElapsedMilliseconds}ms (bucket={targetLength} tokens)");

        // Run inference
        sw.Restart();
        using var results = _session.Run(inputs);
        Console.WriteLine($"[NER] ONNX inference completed in {sw.ElapsedMilliseconds}ms");

        // Get logits output [1, seq_len, num_labels]
        sw.Restart();
        Console.WriteLine($"[NER] Available outputs: {string.Join(", ", results.Select(r => r.Name))}");
        var output = results.First(r => r.Name == "logits" || r.Name == "output_0");
        var logits = output.AsTensor<float>();
        var dims = logits.Dimensions.ToArray();
        Console.WriteLine($"[NER] Extracted logits in {sw.ElapsedMilliseconds}ms, shape: [{string.Join(", ", dims)}]");

        // Convert logits to predictions and spans
        sw.Restart();
        var entities = ExtractEntitiesFromLogits(logits, tokens, text);
        Console.WriteLine($"[NER] Converted to entities in {sw.ElapsedMilliseconds}ms ({entities.Count} entities)");
        Console.WriteLine($"[NER] Total time: {totalSw.ElapsedMilliseconds}ms");

        return entities;
    }

    /// <summary>
    ///     Extract entities with profile-aware type mapping.
    ///     Maps generic NER types to profile-specific types.
    /// </summary>
    public async Task<List<EntityCandidate>> ExtractWithProfileAsync(
        string text,
        EntityProfile profile,
        CancellationToken ct = default)
    {
        var spans = await ExtractSpansAsync(text, ct);

        return spans.Select(s => new EntityCandidate
        {
            Name = s.Text,
            Type = MapToProfileType(s.EntityType, profile),
            Confidence = s.Confidence,
            Signals = ["onnx_ner"]
        }).ToList();
    }

    private List<EntitySpan> ExtractEntitiesFromLogits(Tensor<float> logits, string[] tokens, string originalText)
    {
        var spans = new List<EntitySpan>();
        var dims = logits.Dimensions.ToArray();
        var seqLen = dims[1];
        var numLabels = dims[2];

        // First pass: collect all predictions with confidence scores
        var predictions = new List<(string token, string label, float confidence, bool isSubword)>();
        var labelCounts = new Dictionary<string, int>();

        for (var i = 0; i < seqLen && i < tokens.Length; i++)
        {
            var token = tokens[i];

            // Skip special tokens
            if (token is "[CLS]" or "[SEP]" or "[PAD]")
                continue;

            // Get prediction (argmax)
            var maxProb = float.MinValue;
            var maxIdx = 0;
            for (var j = 0; j < numLabels; j++)
            {
                var prob = logits[0, i, j];
                if (prob > maxProb)
                {
                    maxProb = prob;
                    maxIdx = j;
                }
            }

            var label = _labels![maxIdx];
            var confidence = Softmax(logits, i, numLabels, maxIdx);
            var isSubword = token.StartsWith("##");

            labelCounts[label] = labelCounts.GetValueOrDefault(label, 0) + 1;
            predictions.Add((token, label, confidence, isSubword));
        }

        // Debug: log label distribution
        if (labelCounts.Count > 0)
        {
            var labelSummary = string.Join(", ",
                labelCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));
            Console.WriteLine($"[NER] Label distribution: {labelSummary}");
        }

        // Second pass: merge entities with improved WordPiece handling
        EntitySpan? currentEntity = null;
        var currentTokens = new List<string>();
        float confidenceSum = 0;
        var confidenceCount = 0;

        for (var i = 0; i < predictions.Count; i++)
        {
            var (token, label, confidence, isSubword) = predictions[i];
            var match = BioTagRx.Match(label);

            // IMPROVED: Subword tokens (##xxx) should ALWAYS continue previous entity if one exists
            // This handles cases like "Entity" -> ["En", "##ti", "##ty"] where each token might get different tags
            if (isSubword && currentEntity != null)
            {
                currentTokens.Add(token);
                confidenceSum += confidence;
                confidenceCount++;
                continue;
            }

            // IMPROVED: If current token is a subword continuation of previous non-entity token,
            // and this subword has an entity tag, start the entity from previous token
            if (isSubword && currentEntity == null && match.Success && i > 0)
            {
                // Look back to find the word start
                var wordTokens = new List<string> { token };
                var entityType = match.Groups[2].Value;

                // This subword is an entity - we missed the start, so just start from here
                currentEntity = new EntitySpan { EntityType = entityType, Confidence = confidence };
                currentTokens = [token];
                confidenceSum = confidence;
                confidenceCount = 1;
                continue;
            }

            if (match.Success)
            {
                var bioTag = match.Groups[1].Value;
                var entityType = match.Groups[2].Value;

                if (bioTag == "B")
                {
                    // Save previous entity
                    SaveCurrentEntity(spans, ref currentEntity, currentTokens, confidenceSum, confidenceCount);

                    // Start new entity
                    currentEntity = new EntitySpan { EntityType = entityType, Confidence = confidence };
                    currentTokens = [token];
                    confidenceSum = confidence;
                    confidenceCount = 1;
                }
                else if (bioTag == "I")
                {
                    if (currentEntity != null)
                    {
                        // Continue current entity
                        currentTokens.Add(token);
                        confidenceSum += confidence;
                        confidenceCount++;
                    }
                    else
                    {
                        // I- without B- : Start new entity (robustness)
                        currentEntity = new EntitySpan { EntityType = entityType, Confidence = confidence };
                        currentTokens = [token];
                        confidenceSum = confidence;
                        confidenceCount = 1;
                    }
                }
            }
            else if (label == "O")
            {
                // IMPROVED: If next token is a subword with entity tag, don't close yet
                // This handles "ML.NET" -> ["M", "##L", ".", "N", "##E", "##T"] better
                var nextIsEntitySubword = i + 1 < predictions.Count &&
                                          predictions[i + 1].isSubword &&
                                          BioTagRx.IsMatch(predictions[i + 1].label);

                if (!nextIsEntitySubword)
                {
                    SaveCurrentEntity(spans, ref currentEntity, currentTokens, confidenceSum, confidenceCount);
                    currentTokens.Clear();
                    confidenceSum = 0;
                    confidenceCount = 0;
                }
            }
        }

        // Don't forget last entity
        SaveCurrentEntity(spans, ref currentEntity, currentTokens, confidenceSum, confidenceCount);

        // Post-process: merge adjacent entities of same type (handles split entities)
        spans = MergeAdjacentEntities(spans, originalText);

        // Post-process: reclassify technical terms
        spans = ReclassifyTechnicalTerms(spans);

        // Filter and deduplicate
        return spans
            .Where(s => s.Confidence >= 0.5 && s.Text.Length >= 2 && !string.IsNullOrWhiteSpace(s.Text))
            .Where(s => !IsNoiseTerm(s.Text))
            .GroupBy(s => s.Text.ToLowerInvariant())
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .ToList();
    }

    private static void SaveCurrentEntity(List<EntitySpan> spans, ref EntitySpan? current,
        List<string> tokens, float confidenceSum, int confidenceCount)
    {
        if (current != null && tokens.Count > 0)
        {
            current.Text = MergeTokens(tokens);
            current.Confidence = confidenceCount > 0 ? confidenceSum / confidenceCount : current.Confidence;

            // Only add if the merged text is meaningful
            if (current.Text.Length >= 2 && !IsNoiseTerm(current.Text)) spans.Add(current);
        }

        current = null;
    }

    /// <summary>
    ///     Merge adjacent entities of the same type that were incorrectly split.
    /// </summary>
    private static List<EntitySpan> MergeAdjacentEntities(List<EntitySpan> spans, string originalText)
    {
        if (spans.Count < 2) return spans;

        var merged = new List<EntitySpan>();
        var current = spans[0];

        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];

            // Check if entities should be merged:
            // 1. Same type
            // 2. Texts appear adjacent in original text (with optional space/punctuation between)
            var shouldMerge = current.EntityType == next.EntityType &&
                              AreAdjacentInText(current.Text, next.Text, originalText);

            if (shouldMerge)
            {
                // Merge: combine texts
                current = new EntitySpan
                {
                    Text = current.Text + " " + next.Text,
                    EntityType = current.EntityType,
                    Confidence = (current.Confidence + next.Confidence) / 2
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);

        return merged;
    }

    private static bool AreAdjacentInText(string first, string second, string originalText)
    {
        // Find first text in original
        var idx1 = originalText.IndexOf(first, StringComparison.OrdinalIgnoreCase);
        if (idx1 < 0) return false;

        var idx2 = originalText.IndexOf(second, idx1 + first.Length, StringComparison.OrdinalIgnoreCase);
        if (idx2 < 0) return false;

        // Check if they're close (within 3 characters - allowing for space/punctuation)
        var gap = idx2 - (idx1 + first.Length);
        return gap >= 0 && gap <= 3;
    }

    /// <summary>
    ///     Reclassify technical terms that BERT misclassifies.
    /// </summary>
    private static List<EntitySpan> ReclassifyTechnicalTerms(List<EntitySpan> spans)
    {
        // Known technology companies (should be ORG)
        var techCompanies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Google", "Amazon", "Apple", "Meta", "Facebook", "OpenAI",
            "Anthropic", "AWS", "IBM", "Oracle", "Intel", "AMD", "NVIDIA",
            "GitHub", "GitLab", "Elastic", "Confluent", "Databricks", "Snowflake"
        };

        // Known technologies/products/databases (should be MISC, not ORG)
        var techProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Databases
            "PostgreSQL", "MySQL", "MariaDB", "SQLite", "MongoDB", "Redis", "Cassandra",
            "DynamoDB", "Elasticsearch", "Neo4j", "DuckDB", "Qdrant", "Pinecone", "Weaviate",
            // Frameworks & Libraries
            "Entity Framework", "Entity Framework Core", ".NET", "ML.NET", "ASP.NET",
            "TensorFlow", "PyTorch", "BERT", "GPT", "CLIP", "Florence", "Whisper",
            "React", "Angular", "Vue", "Svelte", "Next.js", "Nuxt", "Blazor",
            // Cloud Services (products, not companies)
            "Azure", "GCP", "Kubernetes", "Docker", "Terraform", "Ansible",
            // Languages
            "C#", "Python", "JavaScript", "TypeScript", "Rust", "Go", "Java", "Kotlin",
            // Protocols & Standards
            "SQL", "NoSQL", "GraphQL", "REST", "gRPC", "WebSocket", "ONNX", "HTTP",
            // AI/ML
            "RAG", "LLM", "GPT-4", "Claude", "Gemini", "DALL-E", "Midjourney", "Stable Diffusion",
            // Projects
            "LucidRAG"
        };

        foreach (var span in spans)
        {
            var normalizedText = span.Text.Trim();

            // First check if it's a known tech product - these should be MISC
            if (techProducts.Contains(normalizedText) ||
                techProducts.Any(p => normalizedText.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                span.EntityType = "MISC";
                span.Confidence = Math.Max(span.Confidence, 0.85);
            }
            // Then check if it's a known company - these should be ORG
            else if (techCompanies.Contains(normalizedText))
            {
                span.EntityType = "ORG";
                span.Confidence = Math.Max(span.Confidence, 0.9);
            }
            // Handle partial matches for compound names
            else if (span.EntityType == "ORG")
            {
                // If tagged as ORG but contains tech product keywords, reclassify as MISC
                if (techProducts.Any(p => normalizedText.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    span.EntityType = "MISC";
            }
        }

        // Also clean up spaces around hyphens/dashes in entity names
        foreach (var span in spans) span.Text = Regex.Replace(span.Text, @"\s*-\s*", "-").Trim();

        return spans;
    }

    /// <summary>
    ///     Check if a term is noise (common words, punctuation, etc.)
    /// </summary>
    private static bool IsNoiseTerm(string text)
    {
        var cleaned = text.Trim();
        var lower = cleaned.ToLowerInvariant();

        // Starts with ## (subword token that escaped)
        if (cleaned.StartsWith("##")) return true;

        // Too short
        if (lower.Length < 2) return true;

        // Common noise patterns - generic words that aren't useful entities
        var noisePatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Articles and conjunctions
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
            // Verbs
            "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
            "use", "uses", "used", "using", "run", "runs", "running", "ran",
            "build", "builds", "building", "built", "create", "creates", "creating",
            // Pronouns
            "this", "that", "these", "those", "it", "its", "he", "she", "they", "we", "you",
            // Generic words often mis-tagged
            "generation", "system", "project", "framework", "model", "data", "file", "code",
            "image", "document", "text", "user", "application", "service", "process",
            "retrieval", "augmented", "new", "local", "support", "features",
            // Punctuation
            ".", ",", "!", "?", "-", "_", ":", ";", "(", ")", "[", "]", "{", "}"
        };

        if (noisePatterns.Contains(lower)) return true;

        // Only punctuation/numbers/symbols
        if (Regex.IsMatch(lower, @"^[\d\s\.\-_,;:!?\(\)\[\]\{\}#]+$")) return true;

        // Single character (except valid single-char entities)
        if (lower.Length == 1) return true;

        // Two-letter common words
        if (lower.Length == 2 && !char.IsUpper(cleaned[0])) return true;

        return false;
    }

    /// <summary>
    ///     Merge WordPiece tokens back to original text.
    ///     Handles ## prefixes from BERT tokenization.
    /// </summary>
    private static string MergeTokens(List<string> tokens)
    {
        if (tokens.Count == 0) return "";

        var merged = tokens[0];
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("##"))
                merged += token[2..]; // Remove ## and append directly
            else
                merged += " " + token;
        }

        return merged.Trim();
    }

    /// <summary>
    ///     Map generic NER type (PER, ORG, LOC, MISC) to profile-specific type.
    /// </summary>
    private static string MapToProfileType(string nerType, EntityProfile profile)
    {
        // Map standard NER types to profile types
        var mappedType = nerType.ToUpperInvariant() switch
        {
            "PER" or "PERSON" => FindBestMatch(profile, ["person", "party", "individual"]),
            "ORG" or "ORGANIZATION" => FindBestMatch(profile, ["organization", "company", "party"]),
            "LOC" or "LOCATION" => FindBestMatch(profile, ["location", "jurisdiction"]),
            "MISC" or "MISCELLANEOUS" => FindBestMatch(profile, ["concept", "technology", "product"]),
            "DATE" or "TIME" => FindBestMatch(profile, ["date"]),
            "MONEY" or "PERCENT" or "QUANTITY" => FindBestMatch(profile, ["amount", "metric"]),
            "PRODUCT" => FindBestMatch(profile, ["product", "technology", "tool"]),
            "EVENT" => FindBestMatch(profile, ["event", "concept"]),
            "LAW" => FindBestMatch(profile, ["clause", "term", "concept"]),
            "LANGUAGE" => FindBestMatch(profile, ["language"]),
            "FAC" or "FACILITY" => FindBestMatch(profile, ["location", "organization"]),
            "GPE" => FindBestMatch(profile, ["location", "jurisdiction"]),
            "NORP" => FindBestMatch(profile, ["organization", "concept"]),
            "WORK_OF_ART" => FindBestMatch(profile, ["product", "concept"]),
            _ => profile.EntityTypes.FirstOrDefault()?.Name ?? "concept"
        };

        return mappedType;
    }

    private static string FindBestMatch(EntityProfile profile, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = profile.EntityTypes.FirstOrDefault(t =>
                t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                t.Aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (match != null)
                return match.Name;
        }

        return profile.EntityTypes.FirstOrDefault()?.Name ?? "concept";
    }

    private static float Softmax(Tensor<float> logits, int seqIdx, int numLabels, int targetIdx)
    {
        var maxLogit = float.MinValue;
        for (var j = 0; j < numLabels; j++)
            maxLogit = Math.Max(maxLogit, logits[0, seqIdx, j]);

        var sumExp = 0f;
        for (var j = 0; j < numLabels; j++)
            sumExp += MathF.Exp(logits[0, seqIdx, j] - maxLogit);

        return MathF.Exp(logits[0, seqIdx, targetIdx] - maxLogit) / sumExp;
    }
}

/// <summary>
///     Entity span detected by NER model.
/// </summary>
public class EntitySpan
{
    /// <summary>Entity text (merged from tokens).</summary>
    public string Text { get; set; } = "";

    /// <summary>NER entity type (PER, ORG, LOC, MISC, etc.).</summary>
    public string EntityType { get; set; } = "";

    /// <summary>Confidence score (0-1).</summary>
    public double Confidence { get; set; }

    /// <summary>Character offset in original text.</summary>
    public int StartOffset { get; set; }

    /// <summary>Character length in original text.</summary>
    public int Length { get; set; }
}

/// <summary>
///     Registry of available NER ONNX models.
/// </summary>
public static class NerModelRegistry
{
    /// <summary>
    ///     protectai/bert-base-NER-onnx - Pre-exported ONNX model from dslim/bert-base-NER.
    ///     Has both model.onnx and vocab.txt (compatible with Microsoft.ML.Tokenizers.BertTokenizer).
    ///     Labels: O, B-PER, I-PER, B-ORG, I-ORG, B-LOC, I-LOC, B-MISC, I-MISC
    ///     License: MIT
    /// </summary>
    public static readonly NerModelInfo BertBaseNer = new()
    {
        Name = "bert-base-NER-onnx",
        HuggingFaceRepo = "protectai/bert-base-NER-onnx",
        ModelFile = "model.onnx",
        TokenizerFile = "vocab.txt", // vocab.txt works with BertTokenizer
        MaxSequenceLength = 512,
        SizeBytes = 431_000_000,
        DefaultLabels = ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-MISC", "I-MISC"]
    };

    /// <summary>
    ///     Multilingual NER model supporting 9 languages.
    /// </summary>
    public static readonly NerModelInfo DistilBertMultilingual = new()
    {
        Name = "distilbert-multilingual-NER",
        HuggingFaceRepo = "Davlan/distilbert-base-multilingual-cased-ner-hrl",
        ModelFile = "model.onnx",
        TokenizerFile = "tokenizer.json",
        MaxSequenceLength = 512,
        SizeBytes = 530_000_000,
        DefaultLabels = ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-DATE", "I-DATE"]
    };

    /// <summary>
    ///     WikiNEuRal multilingual NER (high quality but non-commercial license).
    /// </summary>
    public static readonly NerModelInfo WikiNeural = new()
    {
        Name = "wikineural-multilingual-ner",
        HuggingFaceRepo = "Babelscape/wikineural-multilingual-ner",
        ModelFile = "model.onnx",
        TokenizerFile = "tokenizer.json",
        MaxSequenceLength = 512,
        SizeBytes = 710_000_000,
        DefaultLabels = ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-MISC", "I-MISC"]
    };

    /// <summary>
    ///     Get download URL for HuggingFace file.
    /// </summary>
    public static string GetDownloadUrl(string repo, string file)
    {
        return $"https://huggingface.co/{repo}/resolve/main/{file}";
    }

    /// <summary>
    ///     Download NER model files from HuggingFace if they don't exist.
    /// </summary>
    public static async Task<bool> EnsureModelDownloadedAsync(
        string modelPath,
        NerModelInfo modelInfo,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelPath);

        var modelFile = Path.Combine(modelPath, modelInfo.ModelFile);
        var tokenizerFile = Path.Combine(modelPath, modelInfo.TokenizerFile);
        var configFile = Path.Combine(modelPath, "config.json");

        // Create parent directories for model file (e.g., onnx/ subdirectory)
        var modelDir = Path.GetDirectoryName(modelFile);
        if (!string.IsNullOrEmpty(modelDir)) Directory.CreateDirectory(modelDir);

        var needsDownload = !File.Exists(modelFile) || !File.Exists(tokenizerFile);
        if (!needsDownload)
        {
            progress?.Report($"NER model already downloaded: {modelInfo.Name}");
            return true;
        }

        progress?.Report($"Downloading NER model: {modelInfo.Name} ({modelInfo.SizeBytes / 1_000_000}MB)...");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            // Download model.onnx
            if (!File.Exists(modelFile))
            {
                var modelUrl = modelInfo.GetModelUrl();
                progress?.Report($"Downloading {modelInfo.ModelFile}...");
                var modelBytes = await httpClient.GetByteArrayAsync(modelUrl, ct);
                await File.WriteAllBytesAsync(modelFile, modelBytes, ct);
                progress?.Report($"Downloaded {modelInfo.ModelFile} ({modelBytes.Length / 1_000_000}MB)");
            }

            // Download tokenizer.json
            if (!File.Exists(tokenizerFile))
            {
                var tokenizerUrl = modelInfo.GetTokenizerUrl();
                progress?.Report($"Downloading {modelInfo.TokenizerFile}...");
                var tokenizerBytes = await httpClient.GetByteArrayAsync(tokenizerUrl, ct);
                await File.WriteAllBytesAsync(tokenizerFile, tokenizerBytes, ct);
                progress?.Report($"Downloaded {modelInfo.TokenizerFile}");
            }

            // Download config.json (optional, has label mappings)
            if (!File.Exists(configFile))
                try
                {
                    var configUrl = GetDownloadUrl(modelInfo.HuggingFaceRepo, "config.json");
                    progress?.Report("Downloading config.json...");
                    var configBytes = await httpClient.GetByteArrayAsync(configUrl, ct);
                    await File.WriteAllBytesAsync(configFile, configBytes, ct);
                    progress?.Report("Downloaded config.json");
                }
                catch
                {
                    // config.json is optional
                    progress?.Report("config.json not available (optional)");
                }

            progress?.Report($"NER model download complete: {modelInfo.Name}");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to download NER model: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
///     NER model metadata.
/// </summary>
public sealed class NerModelInfo
{
    public required string Name { get; init; }
    public required string HuggingFaceRepo { get; init; }
    public required string ModelFile { get; init; }
    public required string TokenizerFile { get; init; }
    public required int MaxSequenceLength { get; init; }
    public required long SizeBytes { get; init; }
    public required string[] DefaultLabels { get; init; }

    public string GetModelUrl()
    {
        return NerModelRegistry.GetDownloadUrl(HuggingFaceRepo, ModelFile);
    }

    public string GetTokenizerUrl()
    {
        return NerModelRegistry.GetDownloadUrl(HuggingFaceRepo, TokenizerFile);
    }
}