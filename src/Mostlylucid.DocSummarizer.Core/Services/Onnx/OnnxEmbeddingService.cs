using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DocSummarizer.Config;

namespace Mostlylucid.DocSummarizer.Services.Onnx;

/// <summary>
///     ONNX-based embedding service - no external dependencies required
/// </summary>
public class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly OnnxConfig _config;
    private readonly OnnxModelDownloader _downloader;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly int _maxSequenceLength;
    private readonly EmbeddingModelInfo _modelInfo;
    private readonly bool _verbose;
    private bool _initialized;
    private InferenceSession? _session;
    private HuggingFaceTokenizer? _tokenizer;

    public OnnxEmbeddingService(OnnxConfig config, bool verbose = false)
    {
        _config = config;
        _modelInfo = OnnxModelRegistry.GetEmbeddingModel(config.EmbeddingModel, config.UseQuantized);
        _maxSequenceLength = Math.Min(config.MaxEmbeddingSequenceLength, _modelInfo.MaxSequenceLength);
        _downloader = new OnnxModelDownloader(config, verbose);
        _verbose = verbose;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }

    /// <summary>
    ///     Embedding dimension for this model
    /// </summary>
    public int EmbeddingDimension => _modelInfo.EmbeddingDimension;

    /// <summary>
    ///     Initialize the model (downloads if needed)
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var paths = await _downloader.EnsureEmbeddingModelAsync(_modelInfo, ct);

            var options = CreateSessionOptions();
            var usingGpu = _config.ExecutionProvider is not OnnxExecutionProvider.Cpu;

            _session = new InferenceSession(paths.ModelPath, options);

            if (ProgressService.ShouldShowVerbose(_verbose))
                Console.WriteLine($"[ONNX] Model loaded: {_modelInfo.Name} ({_modelInfo.EmbeddingDimension}d)");

            // Prefer tokenizer.json (universal format) with vocab.txt fallback
            _tokenizer = File.Exists(paths.TokenizerPath)
                ? HuggingFaceTokenizer.FromFile(paths.TokenizerPath)
                : HuggingFaceTokenizer.FromVocabFile(paths.VocabPath);

            // Warmup: validate the execution provider actually works by running a tiny inference.
            // DirectML can successfully register but then crash (0xC0000005) on the first Run()
            // when the GPU driver doesn't support the required DML operations (FusedMatMul, Gather).
            // A warmup catches this as a managed exception and falls back to CPU.
            if (usingGpu)
            {
                if (!TryWarmupInference())
                {
                    Console.WriteLine("[ONNX] GPU warmup failed, falling back to CPU-only session");
                    _session.Dispose();
                    var cpuOptions = CreateCpuSessionOptions();
                    _session = new InferenceSession(paths.ModelPath, cpuOptions);
                    Console.WriteLine("[ONNX] CPU fallback session created");
                }
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Generate embedding for text
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (_session == null || _tokenizer == null)
            throw new InvalidOperationException("Model not initialized");

        // Prepend instruction if model requires it
        if (_modelInfo.RequiresInstruction && !string.IsNullOrEmpty(_modelInfo.QueryInstruction))
            text = _modelInfo.QueryInstruction + text;

        // Tokenize
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, _maxSequenceLength);

        // Create tensors
        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { 1, inputIds.Length });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, attentionMask.Length });
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, new[] { 1, tokenTypeIds.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = _session.Run(inputs);

        // Get last_hidden_state output
        var output = results.First(r => r.Name == "last_hidden_state" || r.Name == "output_0");
        var outputTensor = output.AsTensor<float>();

        // Mean pooling with attention mask
        return MeanPool(outputTensor, attentionMask, _modelInfo.EmbeddingDimension);
    }

    /// <summary>
    ///     Generate embeddings for multiple texts using true batched inference
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (_session == null || _tokenizer == null)
            throw new InvalidOperationException("Model not initialized");

        var textList = texts.ToList();
        if (textList.Count == 0) return Array.Empty<float[]>();
        if (textList.Count == 1) return new[] { await EmbedAsync(textList[0], ct) };

        var allResults = new float[textList.Count][];
        var batchSize = _config.EmbeddingBatchSize;

        // Process in batches for true batched inference
        for (var batchStart = 0; batchStart < textList.Count; batchStart += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + batchSize, textList.Count);
            var batchTexts = textList.GetRange(batchStart, batchEnd - batchStart);

            var batchResults = EmbedBatchInternal(batchTexts);

            for (var i = 0; i < batchResults.Length; i++) allResults[batchStart + i] = batchResults[i];
        }

        return allResults;
    }

    /// <summary>
    ///     True batched inference - processes multiple samples in a single forward pass
    /// </summary>
    private float[][] EmbedBatchInternal(List<string> texts)
    {
        if (_session == null || _tokenizer == null)
            throw new InvalidOperationException("Model not initialized");

        var batchSize = texts.Count;

        // Preprocess all texts (add instruction prefix if needed)
        var processedTexts = texts.Select(text =>
        {
            if (_modelInfo.RequiresInstruction && !string.IsNullOrEmpty(_modelInfo.QueryInstruction))
                return _modelInfo.QueryInstruction + text;
            return text;
        }).ToList();

        // Tokenize all texts and find max length
        var tokenizedBatch = processedTexts.Select(t => _tokenizer.Encode(t, _maxSequenceLength)).ToList();
        var maxLen = tokenizedBatch.Max(t => t.InputIds.Length);

        // Safety check: if batch would be too large (>100MB tensor), fall back to sequential
        var estimatedTensorSize = (long)batchSize * maxLen * 3 * sizeof(long); // 3 tensors
        if (estimatedTensorSize > 100_000_000) // 100MB limit
            return EmbedSequential(texts);

        // Create padded tensors for the entire batch
        var batchInputIds = new long[batchSize * maxLen];
        var batchAttentionMask = new long[batchSize * maxLen];
        var batchTokenTypeIds = new long[batchSize * maxLen];

        // Fill batch tensors with padding
        for (var b = 0; b < batchSize; b++)
        {
            var (InputIds, AttentionMask, TokenTypeIds) = tokenizedBatch[b];
            var seqLen = InputIds.Length;

            for (var s = 0; s < maxLen; s++)
            {
                var idx = b * maxLen + s;
                if (s < seqLen)
                {
                    batchInputIds[idx] = InputIds[s];
                    batchAttentionMask[idx] = AttentionMask[s];
                    batchTokenTypeIds[idx] = TokenTypeIds[s];
                }
                else
                {
                    // Padding
                    batchInputIds[idx] = 0;
                    batchAttentionMask[idx] = 0;
                    batchTokenTypeIds[idx] = 0;
                }
            }
        }

        // Create batch tensors
        var inputIdsTensor = new DenseTensor<long>(batchInputIds, new[] { batchSize, maxLen });
        var attentionMaskTensor = new DenseTensor<long>(batchAttentionMask, new[] { batchSize, maxLen });
        var tokenTypeIdsTensor = new DenseTensor<long>(batchTokenTypeIds, new[] { batchSize, maxLen });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run batched inference
        using var results = _session.Run(inputs);

        // Get output tensor — expected shape: [batch_size, seq_len, hidden_size]
        var output = results.First(r => r.Name == "last_hidden_state" || r.Name == "output_0");
        var outputTensor = output.AsTensor<float>();

        // Validate output batch dimension. Some ONNX models are exported with fixed
        // batch_size=1 and silently ignore additional batch items. When this happens,
        // the output tensor has shape [1, seq_len, hidden_size] regardless of input batch
        // size, causing all items to read from the same hidden states — producing identical
        // embeddings for all inputs. Fall back to sequential processing in this case.
        var outputDims = outputTensor.Dimensions.ToArray();
        if (outputDims.Length >= 1 && outputDims[0] != batchSize) return EmbedSequential(texts);

        // Mean pool each sample in the batch
        var embeddings = new float[batchSize][];
        for (var b = 0; b < batchSize; b++)
        {
            var attentionMask = tokenizedBatch[b].AttentionMask;
            embeddings[b] = MeanPoolBatchItem(outputTensor, b, maxLen, attentionMask, _modelInfo.EmbeddingDimension);
        }

        return embeddings;
    }

    /// <summary>
    ///     Mean pool a single item from a batched output tensor
    /// </summary>
    private static float[] MeanPoolBatchItem(Tensor<float> hiddenStates, int batchIndex, int seqLen,
        long[] attentionMask, int hiddenSize)
    {
        var result = new float[hiddenSize];

        float maskSum = attentionMask.Sum();
        if (maskSum == 0) maskSum = 1;

        for (var h = 0; h < hiddenSize; h++)
        {
            float sum = 0;
            for (var s = 0; s < Math.Min(seqLen, attentionMask.Length); s++)
                if (attentionMask[s] == 1)
                    sum += hiddenStates[batchIndex, s, h];
            result[h] = sum / maskSum;
        }

        // L2 normalize
        var norm = MathF.Sqrt(result.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < result.Length; i++)
                result[i] /= norm;

        return result;
    }

    /// <summary>
    ///     Synchronous single-text embedding. Requires prior InitializeAsync call.
    ///     Use when you need a Func&lt;string, float[]&gt; embedder after initialization.
    /// </summary>
    public float[] Embed(string text)
    {
        return EmbedSingleSync(text);
    }

    /// <summary>
    ///     Internal single embedding (synchronous, no init check)
    /// </summary>
    private float[] EmbedSingleSync(string text)
    {
        if (_session == null || _tokenizer == null)
            throw new InvalidOperationException("Model not initialized");

        // Prepend instruction if model requires it
        if (_modelInfo.RequiresInstruction && !string.IsNullOrEmpty(_modelInfo.QueryInstruction))
            text = _modelInfo.QueryInstruction + text;

        // Tokenize
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode(text, _maxSequenceLength);

        // Create tensors
        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { 1, inputIds.Length });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, attentionMask.Length });
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, new[] { 1, tokenTypeIds.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = _session.Run(inputs);

        // Get last_hidden_state output
        var output = results.First(r => r.Name == "last_hidden_state" || r.Name == "output_0");
        var outputTensor = output.AsTensor<float>();

        // Mean pooling with attention mask
        return MeanPool(outputTensor, attentionMask, _modelInfo.EmbeddingDimension);
    }

    /// <summary>
    ///     Sequential embedding fallback for very large batches
    /// </summary>
    private float[][] EmbedSequential(List<string> texts)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++) results[i] = EmbedSingleSync(texts[i]);
        return results;
    }

    private static float[] MeanPool(Tensor<float> hiddenStates, long[] attentionMask, int hiddenSize)
    {
        var result = new float[hiddenSize];
        var dims = hiddenStates.Dimensions.ToArray();
        var seqLen = dims[1];

        float maskSum = attentionMask.Sum();
        if (maskSum == 0) maskSum = 1; // Avoid division by zero

        for (var h = 0; h < hiddenSize; h++)
        {
            float sum = 0;
            for (var s = 0; s < seqLen; s++)
                if (attentionMask[s] == 1)
                    sum += hiddenStates[0, s, h];
            result[h] = sum / maskSum;
        }

        // L2 normalize
        var norm = MathF.Sqrt(result.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < result.Length; i++)
                result[i] /= norm;

        return result;
    }

    private SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Use parallel execution for better throughput with batched inference
            ExecutionMode = _config.UseParallelExecution
                ? ExecutionMode.ORT_PARALLEL
                : ExecutionMode.ORT_SEQUENTIAL
        };

        // Intra-op threads: parallelism within a single operation (matrix multiply, etc.)
        if (_config.InferenceThreads > 0)
            options.IntraOpNumThreads = _config.InferenceThreads;
        else
            // Auto: use all available cores for intra-op parallelism
            options.IntraOpNumThreads = Environment.ProcessorCount;

        // Inter-op threads: parallelism across independent graph nodes
        if (_config.UseParallelExecution)
        {
            var interOpThreads = _config.InterOpThreads > 0
                ? _config.InterOpThreads
                : Math.Max(2, Environment.ProcessorCount / 2);
            options.InterOpNumThreads = interOpThreads;
        }

        // Configure execution provider based on config
        switch (_config.ExecutionProvider)
        {
            case OnnxExecutionProvider.Cuda:
                try
                {
                    options.AppendExecutionProvider_CUDA(_config.GpuDeviceId);
                    Console.WriteLine($"[ONNX] Using CUDA GPU device {_config.GpuDeviceId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ONNX] CUDA not available: {ex.Message}, falling back to CPU");
                }

                break;

            case OnnxExecutionProvider.DirectMl:
                try
                {
                    options.AppendExecutionProvider_DML(_config.GpuDeviceId);
                    Console.WriteLine($"[ONNX] Using DirectML GPU device {_config.GpuDeviceId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ONNX] DirectML not available: {ex.Message}, falling back to CPU");
                }

                break;

            case OnnxExecutionProvider.Auto:
                // Try DirectML first (has package installed), then CUDA, then CPU
                var gpuSelected = false;
                try
                {
                    options.AppendExecutionProvider_DML(_config.GpuDeviceId);
                    Console.WriteLine($"[ONNX] Auto-selected DirectML GPU device {_config.GpuDeviceId}");
                    gpuSelected = true;
                }
                catch (Exception dmlEx)
                {
                    ProgressService.WriteVerbose(_verbose, $"[ONNX] DirectML not available: {dmlEx.Message}");
                    try
                    {
                        options.AppendExecutionProvider_CUDA(_config.GpuDeviceId);
                        Console.WriteLine($"[ONNX] Auto-selected CUDA GPU device {_config.GpuDeviceId}");
                        gpuSelected = true;
                    }
                    catch (Exception cudaEx)
                    {
                        ProgressService.WriteVerbose(_verbose, $"[ONNX] CUDA not available: {cudaEx.Message}");
                    }
                }

                if (!gpuSelected) Console.WriteLine("[ONNX] No GPU available, using CPU");
                break;

            case OnnxExecutionProvider.Cpu:
            default:
                ProgressService.WriteVerbose(_verbose, "[ONNX] Using CPU (explicit)");
                break;
        }

        return options;
    }

    /// <summary>
    ///     Create CPU-only session options (used as fallback when GPU warmup fails).
    /// </summary>
    private SessionOptions CreateCpuSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = _config.UseParallelExecution
                ? ExecutionMode.ORT_PARALLEL
                : ExecutionMode.ORT_SEQUENTIAL
        };

        if (_config.InferenceThreads > 0)
            options.IntraOpNumThreads = _config.InferenceThreads;
        else
            options.IntraOpNumThreads = Environment.ProcessorCount;

        if (_config.UseParallelExecution)
        {
            var interOpThreads = _config.InterOpThreads > 0
                ? _config.InterOpThreads
                : Math.Max(2, Environment.ProcessorCount / 2);
            options.InterOpNumThreads = interOpThreads;
        }

        // CPU only — no GPU providers appended
        return options;
    }

    /// <summary>
    ///     Run a minimal inference to validate the execution provider works.
    ///     DirectML can register successfully but crash (0xC0000005) on first Run()
    ///     when GPU drivers don't support required DML kernels (FusedMatMul, Gather).
    ///     Returns true if warmup succeeds, false if it throws a managed exception.
    ///     NOTE: If the GPU driver causes a native access violation instead of a managed
    ///     exception, this will crash the process — but it would crash anyway on first
    ///     real inference. The warmup at least fails fast during init instead of mid-work.
    /// </summary>
    private bool TryWarmupInference()
    {
        if (_session == null || _tokenizer == null)
            return false;

        try
        {
            // Minimal input: single short token sequence
            var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Encode("warmup", 8);

            var inputIdsTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
            var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
            };

            using var results = _session.Run(inputs);

            // Verify output exists and has valid shape
            var output = results.FirstOrDefault(r => r.Name is "last_hidden_state" or "output_0");
            if (output == null)
            {
                Console.WriteLine("[ONNX] Warmup: no recognized output tensor");
                return false;
            }

            var tensor = output.AsTensor<float>();
            if (tensor.Dimensions[0] != 1)
            {
                Console.WriteLine("[ONNX] Warmup: unexpected output batch dimension");
                return false;
            }

            ProgressService.WriteVerbose(_verbose, "[ONNX] GPU warmup succeeded");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ONNX] GPU warmup failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}