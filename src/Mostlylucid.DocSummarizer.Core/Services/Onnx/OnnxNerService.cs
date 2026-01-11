using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mostlylucid.DocSummarizer.Services.Onnx;

/// <summary>
///     ONNX-based Named Entity Recognition service for enhancing TF-IDF.
///     Uses BERT-based NER model to identify persons, organizations, locations, etc.
/// </summary>
public sealed class OnnxNerService : IDisposable
{
    // BIO tag pattern
    private static readonly Regex BioTagRx = new(@"^([BI])-(.+)$", RegexOptions.Compiled);

    // Default NER labels (CoNLL-2003)
    private static readonly string[] DefaultLabels =
        ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-MISC", "I-MISC"];

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly int _maxSequenceLength;
    private readonly string _modelPath;
    private bool _initialized;
    private string[]? _labels;
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;

    public OnnxNerService(string? modelPath = null, int maxSequenceLength = 512)
    {
        _modelPath = modelPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocSummarizer", "models", "ner");
        _maxSequenceLength = Math.Min(maxSequenceLength, 512);
    }

    /// <summary>
    ///     Check if NER model is available.
    /// </summary>
    public bool IsAvailable => File.Exists(Path.Combine(_modelPath, "model.onnx")) &&
                               File.Exists(Path.Combine(_modelPath, "vocab.txt"));

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

            var modelFile = Path.Combine(_modelPath, "model.onnx");
            var tokenizerFile = Path.Combine(_modelPath, "vocab.txt");
            var configFile = Path.Combine(_modelPath, "config.json");

            if (!File.Exists(modelFile))
                throw new FileNotFoundException(
                    $"NER model not found: {modelFile}. Download from protectai/bert-base-NER-onnx");

            // Load model
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount),
                InterOpNumThreads = 1
            };
            _session = new InferenceSession(modelFile, options);

            // Load tokenizer (cased BERT)
            if (!File.Exists(tokenizerFile))
                throw new FileNotFoundException($"Tokenizer not found: {tokenizerFile}");

            var bertOptions = new BertOptions
            {
                LowerCaseBeforeTokenization = false, // BERT-base-NER is cased
                ClassificationToken = "[CLS]",
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                UnknownToken = "[UNK]",
                MaskingToken = "[MASK]"
            };

            await using var stream = File.OpenRead(tokenizerFile);
            _tokenizer = BertTokenizer.Create(stream, bertOptions);

            // Load label mapping
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

            _labels ??= DefaultLabels;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Extract named entities from text.
    /// </summary>
    public async Task<List<NerEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (_session == null || _tokenizer == null || _labels == null)
            return [];

        // Tokenize
        var encoded = _tokenizer.EncodeToTokens(text, out _);

        // Get CLS/SEP token IDs
        var clsTokens = _tokenizer.EncodeToTokens("[CLS]", out _);
        var sepTokens = _tokenizer.EncodeToTokens("[SEP]", out _);
        var clsId = clsTokens.Count > 0 ? clsTokens[0].Id : 101;
        var sepId = sepTokens.Count > 0 ? sepTokens[0].Id : 102;

        // Build sequence: [CLS] + content + [SEP]
        var contentIds = encoded.Select(t => t.Id).ToArray();
        var rawIds = new int[contentIds.Length + 2];
        rawIds[0] = clsId;
        Array.Copy(contentIds, 0, rawIds, 1, contentIds.Length);
        rawIds[^1] = sepId;

        var tokens = new string[encoded.Count + 2];
        tokens[0] = "[CLS]";
        for (var i = 0; i < encoded.Count; i++)
            tokens[i + 1] = encoded[i].Value;
        tokens[^1] = "[SEP]";

        // Bucketing
        var buckets = new[] { 32, 64, 128, 256, 512 };
        var targetLength = buckets.FirstOrDefault(b => b >= rawIds.Length);
        if (targetLength == 0) targetLength = 512;

        if (rawIds.Length > _maxSequenceLength)
            targetLength = _maxSequenceLength;

        // Pad
        var inputIds = new long[targetLength];
        var attentionMask = new long[targetLength];
        for (var i = 0; i < targetLength; i++)
        {
            inputIds[i] = i < rawIds.Length ? rawIds[i] : 0;
            attentionMask[i] = i < rawIds.Length ? 1 : 0;
        }

        // Create tensors
        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, targetLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, targetLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        if (_session.InputNames.Contains("token_type_ids"))
        {
            var tokenTypeIds = new long[targetLength];
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(tokenTypeIds, [1, targetLength])));
        }

        // Run inference
        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "logits" || r.Name == "output_0");
        var logits = output.AsTensor<float>();

        return ExtractEntitiesFromLogits(logits, tokens);
    }

    private List<NerEntity> ExtractEntitiesFromLogits(Tensor<float> logits, string[] tokens)
    {
        var entities = new List<NerEntity>();
        var dims = logits.Dimensions.ToArray();
        var seqLen = dims[1];
        var numLabels = dims[2];

        // First pass: collect predictions with subword info
        var predictions = new List<(string token, string label, float confidence, bool isSubword)>();
        for (var i = 0; i < seqLen && i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token is "[CLS]" or "[SEP]" or "[PAD]") continue;

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

            predictions.Add((token, _labels![maxIdx], Softmax(logits, i, numLabels, maxIdx), token.StartsWith("##")));
        }

        // Second pass: merge entities with improved WordPiece handling
        NerEntity? current = null;
        var currentTokens = new List<string>();
        float confidenceSum = 0;
        var confidenceCount = 0;

        for (var i = 0; i < predictions.Count; i++)
        {
            var (token, label, confidence, isSubword) = predictions[i];
            var match = BioTagRx.Match(label);

            // Subwords always continue previous entity
            if (isSubword && current != null)
            {
                currentTokens.Add(token);
                confidenceSum += confidence;
                confidenceCount++;
                continue;
            }

            if (match.Success)
            {
                var bioTag = match.Groups[1].Value;
                var entityType = match.Groups[2].Value;

                if (bioTag == "B")
                {
                    SaveEntity(entities, ref current, currentTokens, confidenceSum, confidenceCount);
                    current = new NerEntity { Type = entityType, Confidence = confidence };
                    currentTokens = [token];
                    confidenceSum = confidence;
                    confidenceCount = 1;
                }
                else if (bioTag == "I" && current != null)
                {
                    currentTokens.Add(token);
                    confidenceSum += confidence;
                    confidenceCount++;
                }
                else if (bioTag == "I")
                {
                    current = new NerEntity { Type = entityType, Confidence = confidence };
                    currentTokens = [token];
                    confidenceSum = confidence;
                    confidenceCount = 1;
                }
            }
            else if (label == "O")
            {
                SaveEntity(entities, ref current, currentTokens, confidenceSum, confidenceCount);
                currentTokens.Clear();
                confidenceSum = 0;
                confidenceCount = 0;
            }
        }

        SaveEntity(entities, ref current, currentTokens, confidenceSum, confidenceCount);

        // Post-process: reclassify and filter
        return entities
            .Select(ReclassifyEntity)
            .Where(e => e.Confidence >= 0.5 && e.Text.Length >= 2 && !IsNoise(e.Text))
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.OrderByDescending(e => e.Confidence).First())
            .ToList();
    }

    private static void SaveEntity(List<NerEntity> list, ref NerEntity? current,
        List<string> tokens, float confSum, int confCount)
    {
        if (current != null && tokens.Count > 0)
        {
            current.Text = MergeTokens(tokens);
            current.Confidence = confCount > 0 ? confSum / confCount : current.Confidence;
            if (current.Text.Length >= 2 && !IsNoise(current.Text))
                list.Add(current);
        }

        current = null;
    }

    private static NerEntity ReclassifyEntity(NerEntity e)
    {
        var techProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PostgreSQL", "MySQL", "MongoDB", "Redis", "DuckDB", "Qdrant", "Entity Framework",
            "Entity Framework Core", ".NET", "ML.NET", "ASP.NET", "TensorFlow", "PyTorch",
            "BERT", "GPT", "CLIP", "Florence", "Azure", "Docker", "Kubernetes", "ONNX", "RAG"
        };
        var techCompanies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Google", "Amazon", "Apple", "OpenAI", "Anthropic", "GitHub"
        };

        if (techProducts.Contains(e.Text) ||
            techProducts.Any(p => e.Text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            e.Type = "MISC";
        else if (techCompanies.Contains(e.Text))
            e.Type = "ORG";

        e.Text = Regex.Replace(e.Text, @"\s*-\s*", "-").Trim();
        return e;
    }

    private static bool IsNoise(string text)
    {
        if (text.StartsWith("##") || text.Length < 2) return true;
        var noiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
            "is", "are", "was", "were", "be", "been", "have", "has", "had", "this", "that",
            "generation", "system", "project", "model", "data", "file", "code", "document"
        };
        return noiseWords.Contains(text.ToLowerInvariant()) ||
               Regex.IsMatch(text, @"^[\d\s\.\-_,;:!?\(\)\[\]\{\}#]+$");
    }

    private static string MergeTokens(List<string> tokens)
    {
        if (tokens.Count == 0) return "";
        var merged = tokens[0];
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            merged += token.StartsWith("##") ? token[2..] : " " + token;
        }

        return merged.Trim();
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
///     Named entity extracted by NER.
/// </summary>
public class NerEntity
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = ""; // PER, ORG, LOC, MISC
    public double Confidence { get; set; }
}

/// <summary>
///     NER model download helper.
/// </summary>
public static class NerModelDownloader
{
    private const string DefaultRepo = "protectai/bert-base-NER-onnx";

    /// <summary>
    ///     Download NER model from HuggingFace if not present.
    /// </summary>
    public static async Task<bool> EnsureModelAsync(
        string modelPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelPath);

        var modelFile = Path.Combine(modelPath, "model.onnx");
        var vocabFile = Path.Combine(modelPath, "vocab.txt");
        var configFile = Path.Combine(modelPath, "config.json");

        if (File.Exists(modelFile) && File.Exists(vocabFile))
        {
            progress?.Report("NER model already downloaded");
            return true;
        }

        progress?.Report("Downloading BERT-NER model (~430MB)...");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            if (!File.Exists(modelFile))
            {
                progress?.Report("Downloading model.onnx...");
                var bytes = await http.GetByteArrayAsync(
                    $"https://huggingface.co/{DefaultRepo}/resolve/main/model.onnx", ct);
                await File.WriteAllBytesAsync(modelFile, bytes, ct);
            }

            if (!File.Exists(vocabFile))
            {
                progress?.Report("Downloading vocab.txt...");
                var bytes = await http.GetByteArrayAsync($"https://huggingface.co/{DefaultRepo}/resolve/main/vocab.txt",
                    ct);
                await File.WriteAllBytesAsync(vocabFile, bytes, ct);
            }

            if (!File.Exists(configFile))
                try
                {
                    var bytes = await http.GetByteArrayAsync(
                        $"https://huggingface.co/{DefaultRepo}/resolve/main/config.json", ct);
                    await File.WriteAllBytesAsync(configFile, bytes, ct);
                }
                catch
                {
                    /* optional */
                }

            progress?.Report("NER model download complete");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Download failed: {ex.Message}");
            return false;
        }
    }
}