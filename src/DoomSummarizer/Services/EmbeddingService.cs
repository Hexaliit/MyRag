using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Spectre.Console;

namespace DoomSummarizer.Services;

public class EmbeddingService : IDisposable
{
    private const string ModelName = "all-MiniLM-L6-v2";
    private const int EmbeddingDim = 384;
    private const int MaxTokens = 256;

    private readonly string _modelDir;
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _initialized;

    public EmbeddingService()
    {
        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".doomsummarizer",
            "models",
            ModelName);
    }

    public bool IsSetup => Directory.Exists(_modelDir) &&
                           File.Exists(Path.Combine(_modelDir, "model.onnx")) &&
                           File.Exists(Path.Combine(_modelDir, "vocab.txt"));

    public async Task SetupAsync(IProgress<string>? progress = null)
    {
        if (IsSetup)
        {
            progress?.Report("ONNX model already downloaded");
            return;
        }

        Directory.CreateDirectory(_modelDir);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        // Download from Hugging Face
        var files = new Dictionary<string, string>
        {
            ["model.onnx"] = $"https://huggingface.co/sentence-transformers/{ModelName}/resolve/main/onnx/model.onnx",
            ["vocab.txt"] = $"https://huggingface.co/sentence-transformers/{ModelName}/resolve/main/vocab.txt",
            ["tokenizer_config.json"] = $"https://huggingface.co/sentence-transformers/{ModelName}/resolve/main/tokenizer_config.json"
        };

        foreach (var (fileName, url) in files)
        {
            var filePath = Path.Combine(_modelDir, fileName);
            if (File.Exists(filePath)) continue;

            progress?.Report($"Downloading {fileName}...");

            try
            {
                var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(filePath);
                await stream.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to download {fileName}: {ex.Message}", ex);
            }
        }

        progress?.Report("ONNX model setup complete");
    }

    /// <summary>
    /// Ensure models are downloaded and initialize. Auto-downloads if needed.
    /// </summary>
    public async Task EnsureReadyAsync(Action<string>? onStatus = null)
    {
        if (_initialized) return;

        if (!IsSetup)
        {
            onStatus?.Invoke("Downloading embedding model (first run)...");
            await SetupAsync(new Progress<string>(msg => onStatus?.Invoke(msg)));
        }

        Initialize();
    }

    public void Initialize()
    {
        if (_initialized) return;
        if (!IsSetup) throw new InvalidOperationException("Embedding models not found — run 'doomsummarizer setup' or allow auto-download");

        var modelPath = Path.Combine(_modelDir, "model.onnx");
        var vocabPath = Path.Combine(_modelDir, "vocab.txt");

        // Create session options for efficiency
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };

        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = BertTokenizer.Create(vocabPath);
        _initialized = true;
    }

    public float[] Embed(string text)
    {
        if (!_initialized) Initialize();

        // Truncate long text
        if (text.Length > 2000)
            text = text[..2000];

        // Tokenize - use EncodeToIds with max length
        var inputIds = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _).ToArray();
        var attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

        // Create tensors
        var inputIdsTensor = new DenseTensor<long>(inputIds.Select(i => (long)i).ToArray(), [1, inputIds.Length]);
        var attentionTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
        var tokenTypeTensor = new DenseTensor<long>(new long[inputIds.Length], [1, inputIds.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeTensor)
        };

        // Run inference
        using var results = _session!.Run(inputs);

        // Get the sentence embedding (mean pooling of last hidden state)
        var lastHiddenState = results.First().AsTensor<float>();
        var embedding = MeanPooling(lastHiddenState, attentionMask);

        // L2 normalize
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < embedding.Length; i++)
                embedding[i] /= norm;
        }

        return embedding;
    }

    private static float[] MeanPooling(Tensor<float> hiddenState, long[] attentionMask)
    {
        var seqLen = attentionMask.Length;
        var embedding = new float[EmbeddingDim];
        var validTokens = attentionMask.Sum();

        if (validTokens == 0) return embedding;

        for (var i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] == 0) continue;
            for (var j = 0; j < EmbeddingDim; j++)
            {
                embedding[j] += hiddenState[0, i, j];
            }
        }

        for (var j = 0; j < EmbeddingDim; j++)
        {
            embedding[j] /= validTokens;
        }

        return embedding;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }

    public static byte[] ToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
