using System.Diagnostics;
using AudioSummarizer.Core.Config;
using FftSharp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioSummarizer.Core.Services.SourceSeparation;

/// <summary>
///     HTDemucs source separation using ONNX Runtime.
///     Separates audio into 4 stems: drums, bass, other, vocals.
///     Model: https://huggingface.co/gentij/htdemucs-ort
///     Processes audio in chunks of ~7.8 seconds with 50% overlap.
///     Model inputs:
///     - input: [1, 2, 343980] - Stereo waveform
///     - x: [1, 4, 2048, 336] - Complex-as-channels spectrogram (STFT)
///     Model outputs:
///     - add_67: [1, 4, 2, 343980] - Separated stems waveform
/// </summary>
public class DemucsSourceSeparationService : ISourceSeparationService, IDisposable
{
    private const int SampleRate = 44100; // HTDemucs expects 44.1kHz
    private const int NumStems = 4;
    private const int NumChannels = 2; // Stereo

    // Chunk processing parameters from htdemucs-ort
    private const int ChunkSamples = 343980; // ~7.8 seconds
    private const int HopSamples = 171990; // 50% overlap

    // STFT parameters matching model expectations
    // Model expects 2048 bins and 336 frames
    // Using nfft=4096 (power of 2) gives 2049 bins, we'll use first 2048
    private const int NFFt = 4096;
    private const int HopLength = 1024;
    private const int ExpectedBins = 2048;
    private const int ExpectedFrames = 336;

    // HTDemucs outputs 4 stems in order: drums, bass, other, vocals
    private static readonly string[] StemNames = { "drums", "bass", "other", "vocals" };
    private readonly AudioConfig _config;
    private readonly ILogger<DemucsSourceSeparationService> _logger;
    private readonly DemucsModelDownloader _modelDownloader;

    private InferenceSession? _session;
    private string? _spectrogramInputName;
    private string? _waveformInputName;
    private string? _waveformOutputName;

    public DemucsSourceSeparationService(
        ILogger<DemucsSourceSeparationService> logger,
        DemucsModelDownloader modelDownloader,
        IOptions<AudioConfig> config)
    {
        _logger = logger;
        _modelDownloader = modelDownloader;
        _config = config.Value;
    }

    private bool CanDownload => true; // Model can always be downloaded

    public void Dispose()
    {
        _session?.Dispose();
    }

    public bool IsAvailable => _modelDownloader.IsModelAvailable || CanDownload;

    public async Task<SeparationResult> SeparateAsync(
        string audioPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new SeparationResult { Stems = new Dictionary<string, string>() };

        try
        {
            // Ensure model is loaded
            await EnsureModelLoadedAsync(cancellationToken);

            _logger.LogInformation("Separating audio: {AudioPath}", audioPath);

            // Load and preprocess audio
            var (leftChannel, rightChannel) = await LoadAudioAsync(audioPath, cancellationToken);
            var totalSamples = leftChannel.Length;

            _logger.LogDebug("Loaded audio: {Samples} samples per channel ({Duration:F1}s)",
                totalSamples, totalSamples / (double)SampleRate);

            // Initialize output buffers for each stem
            var stemOutputsLeft = new float[NumStems][];
            var stemOutputsRight = new float[NumStems][];
            var windowWeights = new float[totalSamples];

            for (var s = 0; s < NumStems; s++)
            {
                stemOutputsLeft[s] = new float[totalSamples];
                stemOutputsRight[s] = new float[totalSamples];
            }

            // Process in chunks with overlap
            var numChunks = (int)Math.Ceiling((double)(totalSamples - ChunkSamples) / HopSamples) + 1;
            if (totalSamples <= ChunkSamples) numChunks = 1;

            _logger.LogInformation("Processing {Chunks} chunks...", numChunks);

            for (var chunkIdx = 0; chunkIdx < numChunks; chunkIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startSample = chunkIdx * HopSamples;
                var endSample = Math.Min(startSample + ChunkSamples, totalSamples);
                var chunkLength = endSample - startSample;

                // Pad chunk to expected size if needed
                var chunkLeft = new float[ChunkSamples];
                var chunkRight = new float[ChunkSamples];

                Array.Copy(leftChannel, startSample, chunkLeft, 0, Math.Min(chunkLength, ChunkSamples));
                Array.Copy(rightChannel, startSample, chunkRight, 0, Math.Min(chunkLength, ChunkSamples));

                // Process this chunk
                var chunkStems = await ProcessChunkAsync(chunkLeft, chunkRight, cancellationToken);

                // Create triangular window for overlap-add blending
                var window = CreateTriangularWindow(ChunkSamples, chunkIdx == 0, chunkIdx == numChunks - 1);

                // Add to output buffers with windowing
                for (var s = 0; s < NumStems; s++)
                for (var i = 0; i < chunkLength && startSample + i < totalSamples; i++)
                {
                    stemOutputsLeft[s][startSample + i] += chunkStems[s].left[i] * window[i];
                    stemOutputsRight[s][startSample + i] += chunkStems[s].right[i] * window[i];
                }

                // Accumulate window weights for normalization
                for (var i = 0; i < chunkLength && startSample + i < totalSamples; i++)
                    windowWeights[startSample + i] += window[i];

                if ((chunkIdx + 1) % 5 == 0 || chunkIdx == numChunks - 1)
                    _logger.LogInformation("Processed chunk {Current}/{Total}", chunkIdx + 1, numChunks);
            }

            // Normalize by window weights
            for (var s = 0; s < NumStems; s++)
            for (var i = 0; i < totalSamples; i++)
                if (windowWeights[i] > 0.001f)
                {
                    stemOutputsLeft[s][i] /= windowWeights[i];
                    stemOutputsRight[s][i] /= windowWeights[i];
                }

            // Save stems
            Directory.CreateDirectory(outputDirectory);

            for (var stemIdx = 0; stemIdx < NumStems; stemIdx++)
            {
                var stemName = StemNames[stemIdx];
                var stemPath = Path.Combine(outputDirectory,
                    $"{Path.GetFileNameWithoutExtension(audioPath)}_{stemName}.wav");

                await SaveStereoWavAsync(stemPath, stemOutputsLeft[stemIdx], stemOutputsRight[stemIdx], SampleRate,
                    cancellationToken);

                result.Stems[stemName] = stemPath;
                _logger.LogInformation("Saved {Stem} to {Path}", stemName, stemPath);

                switch (stemName)
                {
                    case "vocals":
                        result.VocalsPath = stemPath;
                        break;
                    case "drums":
                        result.DrumsPath = stemPath;
                        break;
                    case "bass":
                        result.BassPath = stemPath;
                        break;
                    case "other":
                        result.OtherPath = stemPath;
                        break;
                }
            }

            // Create instrumentals (everything except vocals)
            var instrumentalsPath = Path.Combine(outputDirectory,
                $"{Path.GetFileNameWithoutExtension(audioPath)}_instrumentals.wav");
            await CreateInstrumentalsAsync(result, instrumentalsPath, totalSamples, cancellationToken);
            result.InstrumentalsPath = instrumentalsPath;
            result.Stems["instrumentals"] = instrumentalsPath;

            result.Success = true;
            sw.Stop();
            result.ProcessingTime = sw.Elapsed;

            _logger.LogInformation("Source separation completed in {Time:F1}s", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source separation failed for {AudioPath}", audioPath);
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<string> ExtractVocalsAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"demucs_{Guid.NewGuid():N}");

        try
        {
            var result = await SeparateAsync(audioPath, tempDir, cancellationToken);

            if (!result.Success || string.IsNullOrEmpty(result.VocalsPath))
            {
                _logger.LogWarning("Vocal extraction failed: {Error}", result.Error);
                return audioPath; // Return original if separation fails
            }

            return result.VocalsPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vocal extraction failed, returning original audio");
            return audioPath;
        }
    }

    public async Task<string> ExtractInstrumentalsAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"demucs_{Guid.NewGuid():N}");

        try
        {
            var result = await SeparateAsync(audioPath, tempDir, cancellationToken);

            if (!result.Success || string.IsNullOrEmpty(result.InstrumentalsPath))
            {
                _logger.LogWarning("Instrumental extraction failed: {Error}", result.Error);
                return audioPath;
            }

            return result.InstrumentalsPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instrumental extraction failed, returning original audio");
            return audioPath;
        }
    }

    private async Task<(float[] left, float[] right)[]> ProcessChunkAsync(
        float[] leftChannel,
        float[] rightChannel,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            // 1. Prepare waveform input tensor: [1, 2, 343980]
            var waveformData = new float[1 * NumChannels * ChunkSamples];
            Array.Copy(leftChannel, 0, waveformData, 0, ChunkSamples);
            Array.Copy(rightChannel, 0, waveformData, ChunkSamples, ChunkSamples);
            var waveformTensor = new DenseTensor<float>(waveformData, new[] { 1, NumChannels, ChunkSamples });

            // 2. Compute STFT and prepare spectrogram input: [1, 4, 2048, 336]
            var spectrogramData = ComputeComplexSpectrogram(leftChannel, rightChannel);
            var spectrogramTensor =
                new DenseTensor<float>(spectrogramData, new[] { 1, 4, ExpectedBins, ExpectedFrames });

            // 3. Build inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_waveformInputName!, waveformTensor),
                NamedOnnxValue.CreateFromTensor(_spectrogramInputName!, spectrogramTensor)
            };

            // 4. Run inference - get waveform output (add_67)
            using var results = _session!.Run(inputs, new[] { _waveformOutputName! });

            // 5. Process output: [1, 4, 2, 343980] = [batch, stems, channels, samples]
            var outputTensor = results.First().AsEnumerable<float>().ToArray();
            _logger.LogDebug("Output tensor has {Length} elements (expected {Expected})",
                outputTensor.Length, NumStems * NumChannels * ChunkSamples);

            var stemResults = new (float[] left, float[] right)[NumStems];

            for (var s = 0; s < NumStems; s++)
            {
                var leftOut = new float[ChunkSamples];
                var rightOut = new float[ChunkSamples];

                // Output layout: [stem][channel][sample]
                var stemOffset = s * NumChannels * ChunkSamples;
                if (stemOffset + ChunkSamples * 2 <= outputTensor.Length)
                {
                    Array.Copy(outputTensor, stemOffset, leftOut, 0, ChunkSamples);
                    Array.Copy(outputTensor, stemOffset + ChunkSamples, rightOut, 0, ChunkSamples);
                }

                stemResults[s] = (leftOut, rightOut);
            }

            return stemResults;
        }, cancellationToken);
    }

    /// <summary>
    ///     Compute complex-as-channels spectrogram for HTDemucs model.
    ///     Output format: [4, 2048, 336] flattened - channels are [L_real, L_imag, R_real, R_imag]
    /// </summary>
    private float[] ComputeComplexSpectrogram(float[] leftChannel, float[] rightChannel)
    {
        // Compute STFT for both channels
        var leftSpec = ComputeStft(leftChannel);
        var rightSpec = ComputeStft(rightChannel);

        // Create output: [4, bins, frames] = [4, 2048, 336]
        var result = new float[4 * ExpectedBins * ExpectedFrames];

        var actualFrames = Math.Min(leftSpec.GetLength(0), ExpectedFrames);
        var actualBins = Math.Min(leftSpec.GetLength(1), ExpectedBins);

        // Layout: channel-first, then bins, then frames
        for (var f = 0; f < actualFrames; f++)
        for (var b = 0; b < actualBins; b++)
        {
            // Channel 0: Left real
            result[0 * ExpectedBins * ExpectedFrames + b * ExpectedFrames + f] = leftSpec[f, b, 0];
            // Channel 1: Left imaginary
            result[1 * ExpectedBins * ExpectedFrames + b * ExpectedFrames + f] = leftSpec[f, b, 1];
            // Channel 2: Right real
            result[2 * ExpectedBins * ExpectedFrames + b * ExpectedFrames + f] = rightSpec[f, b, 0];
            // Channel 3: Right imaginary
            result[3 * ExpectedBins * ExpectedFrames + b * ExpectedFrames + f] = rightSpec[f, b, 1];
        }

        return result;
    }

    /// <summary>
    ///     Compute STFT of audio signal.
    ///     Returns [frames, bins, 2] where last dim is [real, imag]
    /// </summary>
    private float[,,] ComputeStft(float[] audio)
    {
        var numFrames = (audio.Length - NFFt) / HopLength + 1;
        var numBins = NFFt / 2 + 1;

        var spectrogram = new float[numFrames, numBins, 2];
        var window = CreateHannWindow(NFFt);

        for (var frame = 0; frame < numFrames; frame++)
        {
            var startIdx = frame * HopLength;

            // Extract frame and apply window
            var windowedFrame = new double[NFFt];
            for (var i = 0; i < NFFt && startIdx + i < audio.Length; i++)
                windowedFrame[i] = audio[startIdx + i] * window[i];

            // Compute FFT
            var fft = FFT.Forward(windowedFrame);

            // Store positive frequencies
            for (var bin = 0; bin < numBins && bin < ExpectedBins; bin++)
            {
                spectrogram[frame, bin, 0] = (float)fft[bin].Real;
                spectrogram[frame, bin, 1] = (float)fft[bin].Imaginary;
            }
        }

        return spectrogram;
    }

    private static double[] CreateHannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++) window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        return window;
    }

    private static float[] CreateTriangularWindow(int size, bool isFirst, bool isLast)
    {
        var window = new float[size];

        if (isFirst && isLast)
        {
            // Single chunk - no windowing needed
            Array.Fill(window, 1.0f);
            return window;
        }

        var halfSize = size / 2;

        for (var i = 0; i < size; i++)
            if (isFirst)
                // First chunk: ramp down at end
                window[i] = i < halfSize ? 1.0f : 1.0f - (float)(i - halfSize) / halfSize;
            else if (isLast)
                // Last chunk: ramp up at start
                window[i] = i < halfSize ? (float)i / halfSize : 1.0f;
            else
                // Middle chunk: triangular window
                window[i] = i < halfSize ? (float)i / halfSize : 1.0f - (float)(i - halfSize) / halfSize;

        return window;
    }

    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (_session != null)
            return;

        await _modelDownloader.EnsureModelDownloadedAsync(cancellationToken);

        _logger.LogInformation("Loading HTDemucs model from {ModelPath}", _modelDownloader.ModelPath);

        await Task.Run(() =>
        {
            var options = new SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            _session = new InferenceSession(_modelDownloader.ModelPath, options);

            // Get input/output names
            var inputMeta = _session.InputMetadata;
            var outputMeta = _session.OutputMetadata;

            // Identify inputs by shape:
            // - Waveform input: [1, 2, 343980] - 3D with channels=2
            // - Spectrogram input: [1, 4, 2048, 336] - 4D with channels=4
            foreach (var input in inputMeta)
            {
                var dims = input.Value.Dimensions;
                if (dims.Length == 3 && dims[1] == 2)
                {
                    _waveformInputName = input.Key;
                    _logger.LogInformation("Identified waveform input: {Name} [{Shape}]",
                        input.Key, string.Join(", ", dims));
                }
                else if (dims.Length == 4 && dims[1] == 4)
                {
                    _spectrogramInputName = input.Key;
                    _logger.LogInformation("Identified spectrogram input: {Name} [{Shape}]",
                        input.Key, string.Join(", ", dims));
                }
            }

            // Identify waveform output by shape [1, 4, 2, 343980]
            foreach (var output in outputMeta)
            {
                var dims = output.Value.Dimensions;
                if (dims.Length == 4 && dims[1] == NumStems && dims[2] == NumChannels)
                {
                    _waveformOutputName = output.Key;
                    _logger.LogInformation("Identified waveform output: {Name} [{Shape}]",
                        output.Key, string.Join(", ", dims));
                }
            }

            if (_waveformInputName == null || _spectrogramInputName == null || _waveformOutputName == null)
            {
                _logger.LogError("Failed to identify model inputs/outputs");
                _logger.LogError("Inputs: {Inputs}", string.Join(", ", inputMeta.Keys));
                _logger.LogError("Outputs: {Outputs}", string.Join(", ", outputMeta.Keys));
                throw new InvalidOperationException("HTDemucs model has unexpected structure");
            }

            _logger.LogInformation("HTDemucs model loaded successfully");
        }, cancellationToken);
    }

    private async Task<(float[] left, float[] right)> LoadAudioAsync(
        string audioPath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var reader = new AudioFileReader(audioPath);

            // Resample to 44.1kHz if needed
            ISampleProvider provider = reader;
            if (reader.WaveFormat.SampleRate != SampleRate)
                provider = new WdlResamplingSampleProvider(reader, SampleRate);

            // Read all samples
            var samples = new List<float>();
            var buffer = new float[SampleRate]; // 1 second buffer
            int samplesRead;

            while ((samplesRead = provider.Read(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i < samplesRead; i++)
                    samples.Add(buffer[i]);

            // Split into stereo channels
            var channels = provider.WaveFormat.Channels;
            var samplesPerChannel = samples.Count / channels;

            var left = new float[samplesPerChannel];
            var right = new float[samplesPerChannel];

            if (channels == 1)
                // Mono - duplicate to stereo
                for (var i = 0; i < samplesPerChannel; i++)
                {
                    left[i] = samples[i];
                    right[i] = samples[i];
                }
            else
                // Stereo - deinterleave
                for (var i = 0; i < samplesPerChannel; i++)
                {
                    left[i] = samples[i * channels];
                    right[i] = samples[i * channels + 1];
                }

            return (left, right);
        }, cancellationToken);
    }

    private async Task SaveStereoWavAsync(
        string path,
        float[] leftChannel,
        float[] rightChannel,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

            using var writer = new WaveFileWriter(path, format);

            // Interleave stereo samples
            for (var i = 0; i < leftChannel.Length; i++)
            {
                writer.WriteSample(leftChannel[i]);
                writer.WriteSample(rightChannel[i]);
            }
        }, cancellationToken);
    }

    private async Task CreateInstrumentalsAsync(
        SeparationResult result,
        string outputPath,
        int samplesPerChannel,
        CancellationToken cancellationToken)
    {
        // Mix drums + bass + other to create instrumentals
        var stemPaths = new[] { result.DrumsPath, result.BassPath, result.OtherPath }
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .ToList();

        if (stemPaths.Count == 0)
        {
            _logger.LogWarning("No stems available for instrumentals");
            return;
        }

        await Task.Run(() =>
        {
            var leftMix = new float[samplesPerChannel];
            var rightMix = new float[samplesPerChannel];

            foreach (var stemPath in stemPaths)
            {
                using var reader = new AudioFileReader(stemPath!);
                var buffer = new float[samplesPerChannel * 2];
                var samplesRead = reader.Read(buffer, 0, buffer.Length);

                for (var i = 0; i < samplesPerChannel && i * 2 + 1 < samplesRead; i++)
                {
                    leftMix[i] += buffer[i * 2];
                    rightMix[i] += buffer[i * 2 + 1];
                }
            }

            // Normalize to prevent clipping
            var maxAbs = Math.Max(
                leftMix.Max(x => Math.Abs(x)),
                rightMix.Max(x => Math.Abs(x)));

            if (maxAbs > 1.0f)
            {
                var scale = 0.95f / maxAbs;
                for (var i = 0; i < samplesPerChannel; i++)
                {
                    leftMix[i] *= scale;
                    rightMix[i] *= scale;
                }
            }

            var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
            using var writer = new WaveFileWriter(outputPath, format);

            for (var i = 0; i < samplesPerChannel; i++)
            {
                writer.WriteSample(leftMix[i]);
                writer.WriteSample(rightMix[i]);
            }
        }, cancellationToken);

        _logger.LogInformation("Created instrumentals at {Path}", outputPath);
    }
}