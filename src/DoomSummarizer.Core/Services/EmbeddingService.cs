using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
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

    // Lock for thread-safe tokenization (BertTokenizer.EncodeToIds is NOT thread-safe)
    private readonly object _tokenizeLock = new();

    // Lock for GPU inference - DirectML/CUDA sessions are NOT thread-safe
    // CPU sessions are thread-safe, so this lock is only used when GPU is active
    private readonly object _gpuInferenceLock = new();

    /// <summary>
    /// Execution provider used for inference (set after Initialize).
    /// </summary>
    public string ExecutionProvider { get; private set; } = "CPU";

    /// <summary>
    /// Whether GPU acceleration is being used.
    /// </summary>
    public bool IsGpuAccelerated => ExecutionProvider != "CPU";

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

        // Try GPU providers first, fall back to CPU
        _session = CreateSessionWithGpuFallback(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath);
        _initialized = true;
    }

    /// <summary>
    /// Create an ONNX session with automatic GPU detection and fallback.
    /// Tries: DirectML (Windows GPU) → CUDA (NVIDIA) → CPU
    /// </summary>
    private InferenceSession CreateSessionWithGpuFallback(string modelPath)
    {
        var baseOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR // Suppress warnings
        };

        // Try DirectML first (Windows only, works with AMD/Intel/NVIDIA)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var dmlOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR // Suppress warnings
                };
                dmlOptions.AppendExecutionProvider_DML(0); // Device 0 (primary GPU)
                var session = new InferenceSession(modelPath, dmlOptions);
                ExecutionProvider = "DirectML (GPU)";
                return session;
            }
            catch
            {
                // DirectML not available or failed, try next
            }
        }

        // Try CUDA (NVIDIA only) - requires Microsoft.ML.OnnxRuntime.Gpu package
        try
        {
            var cudaOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            cudaOptions.AppendExecutionProvider_CUDA(0);
            var session = new InferenceSession(modelPath, cudaOptions);
            ExecutionProvider = "CUDA (NVIDIA GPU)";
            return session;
        }
        catch
        {
            // CUDA not available, fall back to CPU
        }

        // Fall back to CPU
        ExecutionProvider = "CPU";
        return new InferenceSession(modelPath, baseOptions);
    }

    public float[] Embed(string text)
    {
        if (!_initialized) Initialize();

        // Truncate long text
        if (text.Length > 2000)
            text = text[..2000];

        // BertTokenizer.EncodeToIds is NOT thread-safe — it uses internal buffers
        // that can cause "Index out of range" errors under concurrent access.
        // Lock tokenization only; ONNX InferenceSession.Run() is thread-safe.
        int[] inputIds;
        long[] attentionMask;
        lock (_tokenizeLock)
        {
            inputIds = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _).ToArray();
            attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();
        }

        // Create tensors (local, no shared state)
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
        // GPU providers (DirectML/CUDA) are NOT thread-safe — lock for concurrent access
        // CPU provider IS thread-safe, so we skip the lock for better parallelism
        float[] embedding;
        if (IsGpuAccelerated)
        {
            lock (_gpuInferenceLock)
            {
                using var results = _session!.Run(inputs);
                var lastHiddenState = results.First().AsTensor<float>();
                embedding = VectorMath.MeanPool(lastHiddenState, attentionMask, EmbeddingDim);
            }
        }
        else
        {
            using var results = _session!.Run(inputs);
            var lastHiddenState = results.First().AsTensor<float>();
            embedding = VectorMath.MeanPool(lastHiddenState, attentionMask, EmbeddingDim);
        }

        // L2 normalize (SIMD-accelerated)
        VectorMath.L2Normalize(embedding);

        return embedding;
    }

    /// <summary>
    /// Cosine similarity using SIMD-accelerated vector math.
    /// For L2-normalized vectors (as produced by <see cref="Embed"/>), delegates to
    /// the optimized dot-product-only path.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b) =>
        VectorMath.CosineSimilarity(a, b);

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
