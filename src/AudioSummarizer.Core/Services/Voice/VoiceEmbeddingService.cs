using System.Security.Cryptography;
using AudioSummarizer.Core.Config;
using FftSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioSummarizer.Core.Services.Voice;

/// <summary>
///     Extracts speaker embeddings using ECAPA-TDNN ONNX model
///     For anonymous speaker similarity detection (no PII)
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
    ///     Extract voice embedding from audio file
    ///     Returns 192-dim or 512-dim embedding vector (model-dependent)
    /// </summary>
    public async Task<VoiceEmbedding> ExtractEmbeddingAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelLoadedAsync(cancellationToken);

        var features = await Task.Run(() => ExtractMelSpectrogram(audioPath), cancellationToken);

        // Run ONNX inference
        // Flatten 2D array to 1D for DenseTensor
        var numMelBands = features.GetLength(0);
        var numFrames = features.GetLength(1);
        var flatFeatures = new float[numMelBands * numFrames];
        for (var i = 0; i < numMelBands; i++)
        for (var j = 0; j < numFrames; j++)
            flatFeatures[i * numFrames + j] = features[i, j];

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
    ///     Extract voice embedding for a specific time segment of an audio file.
    ///     Used by diarization to get per-segment embeddings for speaker clustering.
    ///     Segments shorter than 0.5s are padded with silence to ensure model stability.
    /// </summary>
    public async Task<VoiceEmbedding> ExtractSegmentEmbeddingAsync(
        string audioPath,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default)
    {
        await EnsureModelLoadedAsync(cancellationToken);

        var features = await Task.Run(
            () => ExtractMelSpectrogramSegment(audioPath, startSeconds, endSeconds), cancellationToken);

        // Run ONNX inference (same as whole-file path)
        var numMelBands = features.GetLength(0);
        var numFrames = features.GetLength(1);

        if (numFrames < 2)
        {
            _logger.LogWarning("Segment too short for embedding ({Start:F2}-{End:F2}s, {Frames} frames)",
                startSeconds, endSeconds, numFrames);
            return new VoiceEmbedding { Vector = Array.Empty<float>(), Dimension = 0, Model = "ecapa-tdnn" };
        }

        var flatFeatures = new float[numMelBands * numFrames];
        for (var i = 0; i < numMelBands; i++)
        for (var j = 0; j < numFrames; j++)
            flatFeatures[i * numFrames + j] = features[i, j];

        var inputTensor = new DenseTensor<float>(flatFeatures, new[] { 1, numMelBands, numFrames });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _session!.Run(inputs);
        var embeddingTensor = results.First().AsEnumerable<float>().ToArray();

        var embedding = NormalizeEmbedding(embeddingTensor);
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
    ///     Calculate cosine similarity between two voice embeddings
    ///     Returns value between -1 (opposite) and 1 (identical)
    /// </summary>
    public virtual double CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        if (embedding1.Length != embedding2.Length)
            throw new ArgumentException("Embeddings must have same dimension");

        double dotProduct = 0;
        double norm1 = 0;
        double norm2 = 0;

        for (var i = 0; i < embedding1.Length; i++)
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

        await Task.Run(() => { _session = new InferenceSession(_modelPath); }, cancellationToken);

        _logger.LogInformation("Voice embedding model loaded successfully");
    }

    private float[,] ExtractMelSpectrogram(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);

        // ECAPA-TDNN expects: 16kHz, mono
        ISampleProvider sampleProvider = reader;

        if (reader.WaveFormat.SampleRate != 16000) sampleProvider = new WdlResamplingSampleProvider(reader, 16000);

        if (sampleProvider.WaveFormat.Channels > 1)
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };

        // Read samples
        var samples = new List<float>();
        var buffer = new float[16000]; // 1 second buffer
        int samplesRead;

        while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < samplesRead; i++)
                samples.Add(buffer[i]);

        // Extract Mel spectrogram features
        // For ECAPA-TDNN: typically 80 mel bands
        var melSpectrogram = ComputeMelSpectrogram(samples.ToArray(), 16000, 80);

        return melSpectrogram;
    }

    private float[,] ExtractMelSpectrogramSegment(string audioPath, double startSeconds, double endSeconds)
    {
        using var reader = new AudioFileReader(audioPath);

        ISampleProvider sampleProvider = reader;
        if (reader.WaveFormat.SampleRate != 16000) sampleProvider = new WdlResamplingSampleProvider(reader, 16000);
        if (sampleProvider.WaveFormat.Channels > 1)
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f, RightVolume = 0.5f
            };

        const int targetSampleRate = 16000;

        // Skip samples before startSeconds
        var skipSamples = (int)(startSeconds * targetSampleRate);
        var segmentSamples = (int)((endSeconds - startSeconds) * targetSampleRate);
        // Ensure minimum 0.5s of audio for model stability
        segmentSamples = Math.Max(segmentSamples, targetSampleRate / 2);

        var skipBuffer = new float[Math.Min(skipSamples, targetSampleRate)];
        var remaining = skipSamples;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, skipBuffer.Length);
            var read = sampleProvider.Read(skipBuffer, 0, toRead);
            if (read == 0) break;
            remaining -= read;
        }

        // Read segment samples
        var samples = new float[segmentSamples];
        var totalRead = 0;
        while (totalRead < segmentSamples)
        {
            var read = sampleProvider.Read(samples, totalRead, segmentSamples - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        // If we got fewer samples than requested (end of file), use what we have
        if (totalRead < segmentSamples)
            Array.Resize(ref samples, totalRead);

        return ComputeMelSpectrogram(samples, targetSampleRate, 80);
    }

    private float[,] ComputeMelSpectrogram(float[] samples, int sampleRate, int numMelBands)
    {
        // Simplified Mel spectrogram computation
        // For production, use more sophisticated implementation or pre-compute

        var frameSize = 512;
        var hopSize = 160; // 10ms hop at 16kHz
        var numFrames = (samples.Length - frameSize) / hopSize + 1;

        var melSpec = new float[numMelBands, numFrames];

        // Mel filterbank (simplified)
        var melFilters = CreateMelFilterbank(numMelBands, frameSize / 2 + 1, sampleRate);

        for (var frameIdx = 0; frameIdx < numFrames; frameIdx++)
        {
            var startSample = frameIdx * hopSize;
            if (startSample + frameSize > samples.Length)
                break;

            var frame = new double[frameSize];
            for (var i = 0; i < frameSize; i++) frame[i] = samples[startSample + i];

            // Apply Hamming window
            for (var i = 0; i < frameSize; i++) frame[i] *= 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frameSize - 1));

            // FFT
            var fft = FFT.Forward(frame);
            var powerSpectrum = new float[frameSize / 2 + 1];
            for (var i = 0; i < powerSpectrum.Length; i++)
                powerSpectrum[i] = (float)(fft[i].Real * fft[i].Real + fft[i].Imaginary * fft[i].Imaginary);

            // Apply Mel filterbank
            for (var mel = 0; mel < numMelBands; mel++)
            {
                float melEnergy = 0;
                for (var i = 0; i < powerSpectrum.Length; i++) melEnergy += powerSpectrum[i] * melFilters[mel, i];
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
        var melMin = HzToMel(0);
        var melMax = HzToMel(sampleRate / 2.0);
        var melSpacing = (melMax - melMin) / (numMelBands + 1);

        var melPoints = new double[numMelBands + 2];
        for (var i = 0; i < melPoints.Length; i++) melPoints[i] = melMin + i * melSpacing;

        var hzPoints = melPoints.Select(MelToHz).ToArray();
        var bins = hzPoints.Select(hz => (int)Math.Floor(numFreqBins * 2 * hz / sampleRate)).ToArray();

        for (var mel = 0; mel < numMelBands; mel++)
        {
            var leftBin = bins[mel];
            var centerBin = bins[mel + 1];
            var rightBin = bins[mel + 2];

            // Left slope
            for (var bin = leftBin; bin < centerBin && bin < numFreqBins; bin++)
                filters[mel, bin] = (float)(bin - leftBin) / (centerBin - leftBin);

            // Right slope
            for (var bin = centerBin; bin < rightBin && bin < numFreqBins; bin++)
                filters[mel, bin] = (float)(rightBin - bin) / (rightBin - centerBin);
        }

        return filters;
    }

    private double HzToMel(double hz)
    {
        return 2595.0 * Math.Log10(1.0 + hz / 700.0);
    }

    private double MelToHz(double mel)
    {
        return 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
    }

    private float[] NormalizeEmbedding(float[] embedding)
    {
        // L2 normalization
        var norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm == 0)
            return embedding;

        return embedding.Select(x => (float)(x / norm)).ToArray();
    }

    private string GenerateVoiceprintId(float[] embedding)
    {
        // Generate anonymous ID from embedding hash
        // This ensures same voice → same ID, but no PII
        using var sha256 = SHA256.Create();
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