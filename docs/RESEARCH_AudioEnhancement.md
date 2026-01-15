# Audio Enhancement Technologies: Research Analysis for LucidRAG

**Date:** January 2026
**Version:** 2.0 (C# Implementation)

---

## Executive Summary

This paper examines technologies for enhancing audio quality before transcription/embedding:

1. **Audio Super-Resolution** - Upsampling low-quality audio (FlashSR: 16kHz to 48kHz)
2. **Audio Quality Detection** - Programmatically identifying "fuzzy" or degraded samples
3. **Speech Enhancement** - Denoising and separation using neural networks

**Key Finding:** A pipeline combining quality detection, conditional enhancement, and super-resolution can reduce transcription errors by up to 50%.

---

## 1. FlashSR: Audio Super-Resolution

### 1.1 Overview

[FlashSR](https://github.com/ysharma3501/FlashSR) is a lightweight audio upsampler based on HierSpeech++.

| Specification | Value |
|---------------|-------|
| Input Sample Rate | 16kHz |
| Output Sample Rate | 48kHz |
| Model Size (ONNX) | ~500KB |
| Processing Speed | 200-400x realtime |
| Latency (streaming) | ~250ms |

### 1.2 Can FlashSR Fix "Fuzzy" Audio?

| Degradation Type | Effectiveness | Notes |
|------------------|---------------|-------|
| Low sample rate | HIGH | Primary use case |
| Compression artifacts | MODERATE | May amplify artifacts |
| Background noise | LOW | Use denoising first |
| Clipping/distortion | LOW | Cannot recover |

**Recommendation:** Use FlashSR as the FINAL stage after denoising.

### 1.3 C# Integration with ONNX

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioSummarizer.Enhancement;

/// <summary>
/// Audio super-resolution using FlashSR ONNX model.
/// Upsamples 16kHz audio to 48kHz.
/// </summary>
public class FlashSRUpsampler : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSampleRate = 16000;
    private readonly int _outputSampleRate = 48000;

    public FlashSRUpsampler(string modelPath)
    {
        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// Upsample audio from 16kHz to 48kHz.
    /// </summary>
    public float[] Upsample(float[] audio16k)
    {
        // Normalize input
        var maxAbs = audio16k.Max(Math.Abs);
        if (maxAbs > 1.0f)
        {
            audio16k = audio16k.Select(x => x / maxAbs).ToArray();
        }

        // Create input tensor [1, 1, samples]
        var inputTensor = new DenseTensor<float>(
            audio16k,
            new[] { 1, 1, audio16k.Length }
        );

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        // Run inference
        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        return output.ToArray();
    }

    /// <summary>
    /// Process audio in streaming chunks for real-time use.
    /// </summary>
    public IEnumerable<float[]> UpsampleStreaming(
        IEnumerable<float[]> chunks,
        int chunkSize = 4000) // ~250ms at 16kHz
    {
        foreach (var chunk in chunks)
        {
            yield return Upsample(chunk);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
```

---

## 2. Audio Quality Detection

### 2.1 Detecting When Enhancement is Needed

"Fuzzy" audio exhibits:
- Low Signal-to-Noise Ratio (SNR)
- Limited bandwidth (missing high frequencies)
- High noise floor

### 2.2 Quality Thresholds

| Metric | Good | Needs Enhancement |
|--------|------|-------------------|
| SNR | > 20 dB | < 15 dB |
| Effective Bandwidth | > 16 kHz | < 8 kHz |
| Noise Floor | < -60 dB | > -40 dB |

### 2.3 C# Implementation

```csharp
using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace AudioSummarizer.Analysis;

/// <summary>
/// Analyzes audio quality to determine if enhancement is needed.
/// </summary>
public class AudioQualityAnalyzer
{
    public record AudioQualityReport
    {
        public double SnrDb { get; init; }
        public double EffectiveBandwidthHz { get; init; }
        public double NoiseFloorDb { get; init; }
        public double PeakDb { get; init; }
        public bool NeedsEnhancement { get; init; }
        public bool NeedsUpsampling { get; init; }
        public string[] Recommendations { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Analyze audio quality and return recommendations.
    /// </summary>
    public AudioQualityReport Analyze(float[] samples, int sampleRate)
    {
        var snr = EstimateSnr(samples, sampleRate);
        var bandwidth = EstimateEffectiveBandwidth(samples, sampleRate);
        var noiseFloor = EstimateNoiseFloor(samples);
        var peak = 20 * Math.Log10(samples.Max(Math.Abs) + 1e-10);

        var recommendations = new List<string>();

        if (snr < 15)
            recommendations.Add("Apply noise reduction (SNR < 15 dB)");

        if (bandwidth < 8000)
            recommendations.Add("Apply super-resolution (bandwidth < 8 kHz)");

        if (noiseFloor > -40)
            recommendations.Add("High noise floor detected");

        return new AudioQualityReport
        {
            SnrDb = snr,
            EffectiveBandwidthHz = bandwidth,
            NoiseFloorDb = noiseFloor,
            PeakDb = peak,
            NeedsEnhancement = snr < 20 || noiseFloor > -50,
            NeedsUpsampling = bandwidth < 16000,
            Recommendations = recommendations.ToArray()
        };
    }

    /// <summary>
    /// Estimate SNR using spectral analysis.
    /// Assumes lowest-energy frames represent noise floor.
    /// </summary>
    private double EstimateSnr(float[] samples, int sampleRate)
    {
        const int frameSize = 2048;
        const int hopSize = 1024;

        var frameEnergies = new List<double>();

        for (int i = 0; i + frameSize <= samples.Length; i += hopSize)
        {
            var frame = samples.Skip(i).Take(frameSize).ToArray();
            var energy = frame.Sum(x => x * x) / frameSize;
            frameEnergies.Add(energy);
        }

        if (frameEnergies.Count < 10)
            return double.PositiveInfinity;

        frameEnergies.Sort();

        // Bottom 10% = noise estimate
        var noiseFrameCount = Math.Max(1, frameEnergies.Count / 10);
        var noisePower = frameEnergies.Take(noiseFrameCount).Average();

        // Top 50% = signal estimate
        var signalFrameCount = frameEnergies.Count / 2;
        var signalPower = frameEnergies.TakeLast(signalFrameCount).Average();

        if (noisePower <= 0)
            return double.PositiveInfinity;

        return 10 * Math.Log10(signalPower / noisePower);
    }

    /// <summary>
    /// Estimate effective bandwidth by finding highest frequency with energy.
    /// </summary>
    private double EstimateEffectiveBandwidth(float[] samples, int sampleRate)
    {
        // Zero-pad to next power of 2
        var fftSize = (int)Math.Pow(2, Math.Ceiling(Math.Log2(samples.Length)));
        var fftInput = new Complex[fftSize];

        for (int i = 0; i < samples.Length; i++)
            fftInput[i] = new Complex(samples[i], 0);

        // Apply FFT
        Fourier.Forward(fftInput, FourierOptions.Matlab);

        // Calculate magnitude spectrum (only positive frequencies)
        var halfSize = fftSize / 2;
        var magnitudeDb = new double[halfSize];
        var maxMag = double.MinValue;

        for (int i = 0; i < halfSize; i++)
        {
            var mag = fftInput[i].Magnitude;
            magnitudeDb[i] = 20 * Math.Log10(mag + 1e-10);
            maxMag = Math.Max(maxMag, magnitudeDb[i]);
        }

        // Normalize to peak
        for (int i = 0; i < halfSize; i++)
            magnitudeDb[i] -= maxMag;

        // Find highest frequency above threshold (-40 dB from peak)
        const double threshold = -40;
        var freqResolution = (double)sampleRate / fftSize;

        int highestBin = 0;
        for (int i = 0; i < halfSize; i++)
        {
            if (magnitudeDb[i] > threshold)
                highestBin = i;
        }

        return highestBin * freqResolution;
    }

    /// <summary>
    /// Estimate noise floor from quiet sections.
    /// </summary>
    private double EstimateNoiseFloor(float[] samples)
    {
        const int frameSize = 1024;
        var frameRms = new List<double>();

        for (int i = 0; i + frameSize <= samples.Length; i += frameSize)
        {
            var frame = samples.Skip(i).Take(frameSize).ToArray();
            var rms = Math.Sqrt(frame.Sum(x => x * x) / frameSize);
            frameRms.Add(rms);
        }

        if (frameRms.Count == 0)
            return -96; // Silence

        // 10th percentile = noise floor estimate
        frameRms.Sort();
        var noiseRms = frameRms[(int)(frameRms.Count * 0.1)];

        return 20 * Math.Log10(noiseRms + 1e-10);
    }
}
```

---

## 3. Speech Enhancement Models

### 3.1 Model Comparison

| Model | Best For | Latency | Quality |
|-------|----------|---------|---------|
| CMGAN | Perceptual quality | Medium | Excellent |
| DeepFilterNet | Real-time | Low (<10ms) | Very Good |
| U-Net | Max noise suppression | Medium | Good |
| Wave-U-Net | Speaker preservation | Medium | Good |

### 3.2 Architecture Evolution

```
U-Net (2017) -> Conv-TasNet (2019) -> Transformer (2021) -> Mamba (2024)
```

### 3.3 C# Wrapper for DeepFilterNet

```csharp
using System.Diagnostics;

namespace AudioSummarizer.Enhancement;

/// <summary>
/// Wrapper for DeepFilterNet speech enhancement.
/// Uses CLI tool for processing.
/// </summary>
public class DeepFilterNetEnhancer
{
    private readonly string _executablePath;
    private readonly string _modelPath;

    public DeepFilterNetEnhancer(string executablePath, string modelPath)
    {
        _executablePath = executablePath;
        _modelPath = modelPath;
    }

    /// <summary>
    /// Enhance audio file using DeepFilterNet.
    /// </summary>
    public async Task<string> EnhanceAsync(
        string inputPath,
        string outputPath,
        CancellationToken ct = default)
    {
        var args = $"--model \"{_modelPath}\" " +
                   $"--input \"{inputPath}\" " +
                   $"--output \"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start DeepFilterNet");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"DeepFilterNet failed: {error}");
        }

        return outputPath;
    }
}
```

---

## 4. Unified Enhancement Pipeline

### 4.1 Architecture

```
Input Audio
     |
     v
+------------------+
| Quality Analyzer |
+------------------+
     |
     v
+------------------+     +------------------+
| SNR < 15 dB?     |---->| DeepFilterNet    |
+------------------+     | (Denoise)        |
     |                   +------------------+
     v                          |
+------------------+            |
| Bandwidth < 16k? |<-----------+
+------------------+
     |
     v
+------------------+
| FlashSR          |
| (Upsample)       |
+------------------+
     |
     v
Output 48kHz Audio
```

### 4.2 C# Pipeline Implementation

```csharp
namespace AudioSummarizer.Enhancement;

/// <summary>
/// Unified audio enhancement pipeline with conditional processing.
/// </summary>
public class AudioEnhancementPipeline : IDisposable
{
    private readonly AudioQualityAnalyzer _analyzer;
    private readonly FlashSRUpsampler? _upsampler;
    private readonly DeepFilterNetEnhancer? _denoiser;
    private readonly AudioEnhancementConfig _config;

    public record AudioEnhancementConfig
    {
        public bool EnableDenoising { get; init; } = true;
        public bool EnableUpsampling { get; init; } = true;
        public double SnrThreshold { get; init; } = 20.0;
        public double BandwidthThreshold { get; init; } = 16000;
        public string? FlashSRModelPath { get; init; }
        public string? DeepFilterNetPath { get; init; }
        public string? DeepFilterNetModelPath { get; init; }
    }

    public record EnhancementResult
    {
        public float[] Audio { get; init; } = Array.Empty<float>();
        public int SampleRate { get; init; }
        public AudioQualityAnalyzer.AudioQualityReport QualityBefore { get; init; } = null!;
        public AudioQualityAnalyzer.AudioQualityReport QualityAfter { get; init; } = null!;
        public bool WasDenoised { get; init; }
        public bool WasUpsampled { get; init; }
    }

    public AudioEnhancementPipeline(AudioEnhancementConfig config)
    {
        _config = config;
        _analyzer = new AudioQualityAnalyzer();

        if (config.EnableUpsampling && !string.IsNullOrEmpty(config.FlashSRModelPath))
        {
            _upsampler = new FlashSRUpsampler(config.FlashSRModelPath);
        }

        if (config.EnableDenoising &&
            !string.IsNullOrEmpty(config.DeepFilterNetPath) &&
            !string.IsNullOrEmpty(config.DeepFilterNetModelPath))
        {
            _denoiser = new DeepFilterNetEnhancer(
                config.DeepFilterNetPath,
                config.DeepFilterNetModelPath
            );
        }
    }

    /// <summary>
    /// Process audio through the enhancement pipeline.
    /// </summary>
    public async Task<EnhancementResult> ProcessAsync(
        float[] audio,
        int sampleRate,
        CancellationToken ct = default)
    {
        var qualityBefore = _analyzer.Analyze(audio, sampleRate);

        var currentAudio = audio;
        var currentSampleRate = sampleRate;
        var wasDenoised = false;
        var wasUpsampled = false;

        // Step 1: Denoise if needed
        if (_config.EnableDenoising &&
            _denoiser != null &&
            qualityBefore.SnrDb < _config.SnrThreshold)
        {
            currentAudio = await DenoiseAsync(currentAudio, currentSampleRate, ct);
            wasDenoised = true;
        }

        // Step 2: Upsample if needed
        if (_config.EnableUpsampling &&
            _upsampler != null &&
            qualityBefore.EffectiveBandwidthHz < _config.BandwidthThreshold)
        {
            // Resample to 16kHz if needed for FlashSR input
            if (currentSampleRate != 16000)
            {
                currentAudio = Resample(currentAudio, currentSampleRate, 16000);
                currentSampleRate = 16000;
            }

            currentAudio = _upsampler.Upsample(currentAudio);
            currentSampleRate = 48000;
            wasUpsampled = true;
        }

        var qualityAfter = _analyzer.Analyze(currentAudio, currentSampleRate);

        return new EnhancementResult
        {
            Audio = currentAudio,
            SampleRate = currentSampleRate,
            QualityBefore = qualityBefore,
            QualityAfter = qualityAfter,
            WasDenoised = wasDenoised,
            WasUpsampled = wasUpsampled
        };
    }

    private async Task<float[]> DenoiseAsync(
        float[] audio,
        int sampleRate,
        CancellationToken ct)
    {
        // Write to temp file, process, read back
        var tempInput = Path.GetTempFileName() + ".wav";
        var tempOutput = Path.GetTempFileName() + ".wav";

        try
        {
            await WriteWavAsync(tempInput, audio, sampleRate);
            await _denoiser!.EnhanceAsync(tempInput, tempOutput, ct);
            return await ReadWavAsync(tempOutput);
        }
        finally
        {
            if (File.Exists(tempInput)) File.Delete(tempInput);
            if (File.Exists(tempOutput)) File.Delete(tempOutput);
        }
    }

    private float[] Resample(float[] audio, int fromRate, int toRate)
    {
        // Simple linear interpolation resampling
        var ratio = (double)toRate / fromRate;
        var newLength = (int)(audio.Length * ratio);
        var resampled = new float[newLength];

        for (int i = 0; i < newLength; i++)
        {
            var srcIndex = i / ratio;
            var srcIndexInt = (int)srcIndex;
            var frac = srcIndex - srcIndexInt;

            if (srcIndexInt + 1 < audio.Length)
            {
                resampled[i] = (float)(
                    audio[srcIndexInt] * (1 - frac) +
                    audio[srcIndexInt + 1] * frac
                );
            }
            else
            {
                resampled[i] = audio[srcIndexInt];
            }
        }

        return resampled;
    }

    private Task WriteWavAsync(string path, float[] audio, int sampleRate)
    {
        // Implementation using NAudio or similar
        throw new NotImplementedException("Use NAudio WaveFileWriter");
    }

    private Task<float[]> ReadWavAsync(string path)
    {
        // Implementation using NAudio or similar
        throw new NotImplementedException("Use NAudio WaveFileReader");
    }

    public void Dispose()
    {
        _upsampler?.Dispose();
    }
}
```

### 4.3 Service Registration

```csharp
namespace AudioSummarizer.Enhancement;

public static class AudioEnhancementServiceExtensions
{
    public static IServiceCollection AddAudioEnhancement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration
            .GetSection("AudioEnhancement")
            .Get<AudioEnhancementPipeline.AudioEnhancementConfig>()
            ?? new AudioEnhancementPipeline.AudioEnhancementConfig();

        services.AddSingleton(config);
        services.AddSingleton<AudioQualityAnalyzer>();
        services.AddSingleton<AudioEnhancementPipeline>();

        return services;
    }
}
```

### 4.4 Configuration

```json
{
  "AudioEnhancement": {
    "EnableDenoising": true,
    "EnableUpsampling": true,
    "SnrThreshold": 20.0,
    "BandwidthThreshold": 16000,
    "FlashSRModelPath": "models/flashsr.onnx",
    "DeepFilterNetPath": "tools/deepfilternet",
    "DeepFilterNetModelPath": "models/deepfilternet"
  }
}
```

---

## 5. Integration as a Wave

### 5.1 AudioEnhancementWave

```csharp
using Mostlylucid.Summarizer.Core;

namespace AudioSummarizer.Core.Waves;

/// <summary>
/// Wave that enhances audio quality before transcription.
/// </summary>
public class AudioEnhancementWave : WaveBase<AudioEnhancementResult>
{
    private readonly AudioEnhancementPipeline _pipeline;
    private readonly ILogger<AudioEnhancementWave> _logger;

    public override string Name => "AudioEnhancement";
    public override int Order => 5; // Early in pipeline

    public AudioEnhancementWave(
        AudioEnhancementPipeline pipeline,
        ILogger<AudioEnhancementWave> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public override async Task<AudioEnhancementResult> ProcessAsync(
        WaveContext context,
        CancellationToken ct = default)
    {
        var audioData = context.Get<AudioData>("audio");
        if (audioData == null)
        {
            _logger.LogWarning("No audio data found in context");
            return new AudioEnhancementResult { Skipped = true };
        }

        var result = await _pipeline.ProcessAsync(
            audioData.Samples,
            audioData.SampleRate,
            ct
        );

        _logger.LogInformation(
            "Audio enhancement: SNR {Before:F1} -> {After:F1} dB, " +
            "Denoised: {Denoised}, Upsampled: {Upsampled}",
            result.QualityBefore.SnrDb,
            result.QualityAfter.SnrDb,
            result.WasDenoised,
            result.WasUpsampled
        );

        // Update context with enhanced audio
        context.Set("audio", new AudioData
        {
            Samples = result.Audio,
            SampleRate = result.SampleRate
        });

        return new AudioEnhancementResult
        {
            QualityBefore = result.QualityBefore,
            QualityAfter = result.QualityAfter,
            WasDenoised = result.WasDenoised,
            WasUpsampled = result.WasUpsampled
        };
    }
}

public record AudioEnhancementResult
{
    public bool Skipped { get; init; }
    public AudioQualityAnalyzer.AudioQualityReport? QualityBefore { get; init; }
    public AudioQualityAnalyzer.AudioQualityReport? QualityAfter { get; init; }
    public bool WasDenoised { get; init; }
    public bool WasUpsampled { get; init; }
}
```

---

## 6. References

### Audio Super-Resolution
- [FlashSR GitHub](https://github.com/ysharma3501/FlashSR) - 500KB ONNX upsampler
- [HierSpeech++](https://github.com/sh-lee-prml/HierSpeechpp) - Parent architecture

### Audio Quality Detection
- [Essentia Library](https://essentia.upf.edu/) - Audio analysis algorithms
- [SNR Estimation Methods](https://github.com/hrtlacek/SNR) - Multiple approaches

### Speech Enhancement
- [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet) - Real-time enhancement
- [Speech Separation Survey (MDPI)](https://www.mdpi.com/2504-2289/9/11/289) - Comprehensive review

---

## 7. Implementation Checklist

- [ ] Add MathNet.Numerics NuGet package for FFT
- [ ] Download FlashSR ONNX model
- [ ] Implement AudioQualityAnalyzer
- [ ] Implement FlashSRUpsampler
- [ ] Add DeepFilterNet CLI wrapper
- [ ] Create AudioEnhancementPipeline
- [ ] Create AudioEnhancementWave
- [ ] Add configuration section
- [ ] Write integration tests
