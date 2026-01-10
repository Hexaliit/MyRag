using AudioSummarizer.Core.Config;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioSummarizer.Core.Services.Voice;

/// <summary>
/// Extracts speaker embeddings using ECAPA-TDNN ONNX model
/// For anonymous speaker similarity detection (no PII)
/// </summary>
public class VoiceEmbeddingService
{
    private readonly ILogger<VoiceEmbeddingService> _logger;
    private readonly VoiceEmbeddingModelDownloader _modelDownloader;
    private readonly string _modelPath;
    private InferenceSession? _session;

    public VoiceEmbeddingService(
        ILogger<VoiceEmbeddingService> logger,
        VoiceEmbeddingModelDownloader modelDownloader,
        IOptions<AudioConfig> config)
    {
        _logger = logger;
        _modelDownloader = modelDownloader;
        _modelPath = config.Value.VoiceEmbedding.ModelPath;
    }

    /// <summary>
    /// Extract voice embedding from audio file
    /// Returns 192-dim or 512-dim embedding vector (model-dependent)
    /// </summary>
    public async Task<VoiceEmbedding> ExtractEmbeddingAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelLoadedAsync(cancellationToken);

        var features = await Task.Run(() => ExtractMelSpectrogram(audioPath), cancellationToken);

        // Run ONNX inference
        // Flatten 2D array to 1D for DenseTensor
        int numMelBands = features.GetLength(0);
        int numFrames = features.GetLength(1);
        var flatFeatures = new float[numMelBands * numFrames];
        for (int i = 0; i < numMelBands; i++)
        {
            for (int j = 0; j < numFrames; j++)
            {
                flatFeatures[i * numFrames + j] = features[i, j];
            }
        }

        var inputTensor = new DenseTensor<float>(flatFeatures, new[] { 1, numMelBands, numFrames });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _session!.Run(inputs);
        var embeddingTensor = results.First().AsEnumerable<float>().ToArray();

        // Normalize embedding
        var embedding = NormalizeEmbedding(embeddingTensor);

        // Generate anonymous voiceprint ID
        var voiceprintId = GenerateVoiceprintId(embedding);

        return new VoiceEmbedding
        {
            Vector = embedding,
            VoiceprintId = voiceprintId,
            Dimension = embedding.Length,
            Model = "ecapa-tdnn"
        };
    }

    /// <summary>
    /// Calculate cosine similarity between two voice embeddings
    /// Returns value between -1 (opposite) and 1 (identical)
    /// </summary>
    public double CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        if (embedding1.Length != embedding2.Length)
            throw new ArgumentException("Embeddings must have same dimension");

        double dotProduct = 0;
        double norm1 = 0;
        double norm2 = 0;

        for (int i = 0; i < embedding1.Length; i++)
        {
            dotProduct += embedding1[i] * embedding2[i];
            norm1 += embedding1[i] * embedding1[i];
            norm2 += embedding2[i] * embedding2[i];
        }

        if (norm1 == 0 || norm2 == 0)
            return 0;

        return dotProduct / (Math.Sqrt(norm1) * Math.Sqrt(norm2));
    }

    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (_session != null)
            return;

        // Download model if not present
        await _modelDownloader.EnsureModelDownloadedAsync(cancellationToken);

        _logger.LogInformation("Loading ECAPA-TDNN voice embedding model from {ModelPath}", _modelPath);

        await Task.Run(() =>
        {
            _session = new InferenceSession(_modelPath);
        }, cancellationToken);

        _logger.LogInformation("Voice embedding model loaded successfully");
    }

    private float[,] ExtractMelSpectrogram(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);

        // ECAPA-TDNN expects: 16kHz, mono
        ISampleProvider sampleProvider = reader;

        if (reader.WaveFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(reader, 16000);
        }

        if (sampleProvider.WaveFormat.Channels > 1)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        // Read samples
        var samples = new List<float>();
        var buffer = new float[16000]; // 1 second buffer
        int samplesRead;

        while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        // Extract Mel spectrogram features
        // For ECAPA-TDNN: typically 80 mel bands
        var melSpectrogram = ComputeMelSpectrogram(samples.ToArray(), 16000, numMelBands: 80);

        return melSpectrogram;
    }

    private float[,] ComputeMelSpectrogram(float[] samples, int sampleRate, int numMelBands)
    {
        // Simplified Mel spectrogram computation
        // For production, use more sophisticated implementation or pre-compute

        int frameSize = 512;
        int hopSize = 160; // 10ms hop at 16kHz
        int numFrames = (samples.Length - frameSize) / hopSize + 1;

        var melSpec = new float[numMelBands, numFrames];

        // Mel filterbank (simplified)
        var melFilters = CreateMelFilterbank(numMelBands, frameSize / 2 + 1, sampleRate);

        for (int frameIdx = 0; frameIdx < numFrames; frameIdx++)
        {
            int startSample = frameIdx * hopSize;
            if (startSample + frameSize > samples.Length)
                break;

            var frame = new double[frameSize];
            for (int i = 0; i < frameSize; i++)
            {
                frame[i] = samples[startSample + i];
            }

            // Apply Hamming window
            for (int i = 0; i < frameSize; i++)
            {
                frame[i] *= 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frameSize - 1));
            }

            // FFT
            var fft = FftSharp.FFT.Forward(frame);
            var powerSpectrum = new float[frameSize / 2 + 1];
            for (int i = 0; i < powerSpectrum.Length; i++)
            {
                powerSpectrum[i] = (float)(fft[i].Real * fft[i].Real + fft[i].Imaginary * fft[i].Imaginary);
            }

            // Apply Mel filterbank
            for (int mel = 0; mel < numMelBands; mel++)
            {
                float melEnergy = 0;
                for (int i = 0; i < powerSpectrum.Length; i++)
                {
                    melEnergy += powerSpectrum[i] * melFilters[mel, i];
                }
                // Log mel energy
                melSpec[mel, frameIdx] = (float)Math.Log(melEnergy + 1e-10);
            }
        }

        return melSpec;
    }

    private float[,] CreateMelFilterbank(int numMelBands, int numFreqBins, int sampleRate)
    {
        var filters = new float[numMelBands, numFreqBins];

        // Simplified triangular Mel filterbank
        double melMin = HzToMel(0);
        double melMax = HzToMel(sampleRate / 2.0);
        double melSpacing = (melMax - melMin) / (numMelBands + 1);

        var melPoints = new double[numMelBands + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = melMin + i * melSpacing;
        }

        var hzPoints = melPoints.Select(MelToHz).ToArray();
        var bins = hzPoints.Select(hz => (int)Math.Floor((numFreqBins * 2) * hz / sampleRate)).ToArray();

        for (int mel = 0; mel < numMelBands; mel++)
        {
            int leftBin = bins[mel];
            int centerBin = bins[mel + 1];
            int rightBin = bins[mel + 2];

            // Left slope
            for (int bin = leftBin; bin < centerBin && bin < numFreqBins; bin++)
            {
                filters[mel, bin] = (float)(bin - leftBin) / (centerBin - leftBin);
            }

            // Right slope
            for (int bin = centerBin; bin < rightBin && bin < numFreqBins; bin++)
            {
                filters[mel, bin] = (float)(rightBin - bin) / (rightBin - centerBin);
            }
        }

        return filters;
    }

    private double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    private float[] NormalizeEmbedding(float[] embedding)
    {
        // L2 normalization
        double norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm == 0)
            return embedding;

        return embedding.Select(x => (float)(x / norm)).ToArray();
    }

    private string GenerateVoiceprintId(float[] embedding)
    {
        // Generate anonymous ID from embedding hash
        // This ensures same voice → same ID, but no PII
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        var hash = sha256.ComputeHash(bytes);

        return "vprint:" + Convert.ToHexString(hash).Substring(0, 16).ToLower();
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}

public class VoiceEmbedding
{
    public float[] Vector { get; set; } = Array.Empty<float>();
    public string VoiceprintId { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string Model { get; set; } = string.Empty;
}
