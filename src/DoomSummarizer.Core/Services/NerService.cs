using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Services;

/// <summary>
///     ONNX-based Named Entity Recognition for extracting people, organizations, locations.
/// </summary>
public sealed class NerService : INerService
{
    private static readonly Regex BioTagRx = new(@"^([BI])-(.+)$", RegexOptions.Compiled);

    private static readonly string[] DefaultLabels =
        ["O", "B-PER", "I-PER", "B-ORG", "I-ORG", "B-LOC", "I-LOC", "B-MISC", "I-MISC"];

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly string _modelPath;
    private bool _initialized;
    private string[]? _labels;
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;

    public NerService(string? modelPath = null)
    {
        _modelPath = modelPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoomSummarizer", "models", "ner");
    }

    public bool IsAvailable => File.Exists(Path.Combine(_modelPath, "model.onnx")) &&
                               File.Exists(Path.Combine(_modelPath, "vocab.txt"));

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var modelFile = Path.Combine(_modelPath, "model.onnx");
            var vocabFile = Path.Combine(_modelPath, "vocab.txt");
            var configFile = Path.Combine(_modelPath, "config.json");

            if (!File.Exists(modelFile)) return;

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount)
            };
            _session = new InferenceSession(modelFile, options);

            var bertOptions = new BertOptions
            {
                LowerCaseBeforeTokenization = false,
                ClassificationToken = "[CLS]",
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                UnknownToken = "[UNK]"
            };

            await using var stream = File.OpenRead(vocabFile);
            _tokenizer = BertTokenizer.Create(stream, bertOptions);

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

    public async Task<List<NerEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (_session == null || _tokenizer == null || _labels == null) return [];

        var encoded = _tokenizer.EncodeToTokens(text, out _);
        var clsTokens = _tokenizer.EncodeToTokens("[CLS]", out _);
        var sepTokens = _tokenizer.EncodeToTokens("[SEP]", out _);
        var clsId = clsTokens.Count > 0 ? clsTokens[0].Id : 101;
        var sepId = sepTokens.Count > 0 ? sepTokens[0].Id : 102;

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

        var buckets = new[] { 32, 64, 128, 256, 512 };
        var targetLength = buckets.FirstOrDefault(b => b >= rawIds.Length);
        if (targetLength == 0) targetLength = 512;

        var inputIds = new long[targetLength];
        var attentionMask = new long[targetLength];
        for (var i = 0; i < targetLength; i++)
        {
            inputIds[i] = i < rawIds.Length ? rawIds[i] : 0;
            attentionMask[i] = i < rawIds.Length ? 1 : 0;
        }

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

        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "logits" || r.Name == "output_0");
        var logits = output.AsTensor<float>();

        return ExtractFromLogits(logits, tokens);
    }

    public async Task<bool> EnsureModelAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_modelPath);

        var modelFile = Path.Combine(_modelPath, "model.onnx");
        var vocabFile = Path.Combine(_modelPath, "vocab.txt");
        var configFile = Path.Combine(_modelPath, "config.json");

        if (File.Exists(modelFile) && File.Exists(vocabFile))
        {
            progress?.Invoke("NER model already downloaded");
            return true;
        }

        progress?.Invoke("Downloading BERT-NER model (~430MB)...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            const string repo = "protectai/bert-base-NER-onnx";

            if (!File.Exists(modelFile))
            {
                progress?.Invoke("Downloading model.onnx...");
                var bytes = await http.GetByteArrayAsync($"https://huggingface.co/{repo}/resolve/main/model.onnx", ct);
                await File.WriteAllBytesAsync(modelFile, bytes, ct);
            }

            if (!File.Exists(vocabFile))
            {
                progress?.Invoke("Downloading vocab.txt...");
                var bytes = await http.GetByteArrayAsync($"https://huggingface.co/{repo}/resolve/main/vocab.txt", ct);
                await File.WriteAllBytesAsync(vocabFile, bytes, ct);
            }

            if (!File.Exists(configFile))
                try
                {
                    var bytes = await http.GetByteArrayAsync($"https://huggingface.co/{repo}/resolve/main/config.json",
                        ct);
                    await File.WriteAllBytesAsync(configFile, bytes, ct);
                }
                catch
                {
                    /* optional */
                }

            progress?.Invoke("NER model download complete");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"NER download failed: {ex.Message}");
            return false;
        }
    }

    private List<NerEntity> ExtractFromLogits(Tensor<float> logits, string[] tokens)
    {
        var entities = new List<NerEntity>();
        var dims = logits.Dimensions.ToArray();
        var seqLen = dims[1];
        var numLabels = dims[2];

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

        NerEntity? current = null;
        var currentTokens = new List<string>();
        float confSum = 0;
        var confCount = 0;

        foreach (var (token, label, confidence, isSubword) in predictions)
        {
            if (isSubword && current != null)
            {
                currentTokens.Add(token);
                confSum += confidence;
                confCount++;
                continue;
            }

            var match = BioTagRx.Match(label);
            if (match.Success)
            {
                var bio = match.Groups[1].Value;
                var type = match.Groups[2].Value;

                if (bio == "B")
                {
                    SaveEntity(entities, ref current, currentTokens, confSum, confCount);
                    current = new NerEntity { Type = type };
                    currentTokens = [token];
                    confSum = confidence;
                    confCount = 1;
                }
                else if (bio == "I" && current != null)
                {
                    currentTokens.Add(token);
                    confSum += confidence;
                    confCount++;
                }
            }
            else if (label == "O")
            {
                SaveEntity(entities, ref current, currentTokens, confSum, confCount);
                currentTokens.Clear();
                confSum = 0;
                confCount = 0;
            }
        }

        SaveEntity(entities, ref current, currentTokens, confSum, confCount);

        return entities
            .Where(e => e.Confidence >= 0.5 && e.Text.Length >= 2)
            .GroupBy(e => e.Text.ToLowerInvariant())
            .Select(g => g.MaxBy(e => e.Confidence)!)
            .ToList();
    }

    private static void SaveEntity(List<NerEntity> list, ref NerEntity? current, List<string> tokens, float confSum,
        int confCount)
    {
        if (current != null && tokens.Count > 0)
        {
            current.Text = MergeTokens(tokens);
            current.Confidence = confCount > 0 ? confSum / confCount : 0.5f;
            if (current.Text.Length >= 2)
                list.Add(current);
        }

        current = null;
    }

    private static string MergeTokens(List<string> tokens)
    {
        if (tokens.Count == 0) return "";
        var merged = tokens[0];
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            merged += t.StartsWith("##") ? t[2..] : " " + t;
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

// NerEntity is now defined in Mostlylucid.DocSummarizer.Services.Onnx