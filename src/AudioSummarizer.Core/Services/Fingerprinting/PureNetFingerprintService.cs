using System.Security.Cryptography;
using FftSharp;
using NAudio.Wave;

namespace AudioSummarizer.Core.Services.Fingerprinting;

/// <summary>
/// Pure .NET audio fingerprinting using spectral peak hashing (Shazam-style algorithm).
/// Cross-platform, no native dependencies.
/// </summary>
public sealed class PureNetFingerprintService : IFingerprintService
{
    private readonly ILogger<PureNetFingerprintService> _logger;

    // FFT parameters
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int SampleRate = 44100; // Resample to standard rate

    // Frequency bands for peak detection (Hz)
    private static readonly (int Low, int High)[] FrequencyBands =
    {
        (0, 250),       // Sub-bass
        (250, 500),     // Bass
        (500, 2000),    // Midrange
        (2000, 4000),   // Upper midrange
        (4000, 8000),   // Presence
        (8000, 16000)   // Brilliance
    };

    public string ProviderName => "PureNet";

    public PureNetFingerprintService(ILogger<PureNetFingerprintService> logger)
    {
        _logger = logger;
    }

    public async Task<AudioFingerprint> GenerateFingerprintAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load and resample audio to mono at standard sample rate
            var audioData = await LoadAudioAsync(audioPath, cancellationToken);

            // Extract spectral peaks
            var peaks = ExtractSpectralPeaks(audioData);

            // Generate hash from peak constellation
            var hash = HashPeakConstellation(peaks);
            var rawData = SerializePeaks(peaks);

            return new AudioFingerprint
            {
                Type = "spectral_peaks",
                Hash = hash,
                RawData = rawData,
                DurationSeconds = audioData.Length / (double)SampleRate,
                Provider = ProviderName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate fingerprint for {AudioPath}", audioPath);
            throw;
        }
    }

    public double CalculateSimilarity(AudioFingerprint fingerprint1, AudioFingerprint fingerprint2)
    {
        if (fingerprint1.Type != "spectral_peaks" || fingerprint2.Type != "spectral_peaks")
        {
            throw new ArgumentException("Both fingerprints must be of type 'spectral_peaks'");
        }

        // Exact match check
        if (fingerprint1.Hash == fingerprint2.Hash)
        {
            return 1.0;
        }

        // If we have raw data, compute Hamming distance
        if (fingerprint1.RawData != null && fingerprint2.RawData != null)
        {
            return CalculateHammingSimilarity(fingerprint1.RawData, fingerprint2.RawData);
        }

        // Otherwise, compare hashes (less accurate)
        return fingerprint1.Hash == fingerprint2.Hash ? 1.0 : 0.0;
    }

    private async Task<float[]> LoadAudioAsync(string audioPath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(audioPath);
        var format = reader.WaveFormat;

        // Read all samples
        var buffer = new List<float>();
        var readBuffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int samplesRead;

        while ((samplesRead = await Task.Run(() => reader.Read(readBuffer, 0, readBuffer.Length), cancellationToken)) > 0)
        {
            buffer.AddRange(readBuffer.Take(samplesRead));
        }

        var samples = buffer.ToArray();

        // Convert to mono if stereo
        if (format.Channels > 1)
        {
            samples = ConvertToMono(samples, format.Channels);
        }

        // Resample to standard rate if needed
        if (format.SampleRate != SampleRate)
        {
            samples = Resample(samples, format.SampleRate, SampleRate);
        }

        return samples;
    }

    private float[] ConvertToMono(float[] samples, int channels)
    {
        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += samples[i * channels + ch];
            }
            mono[i] = sum / channels;
        }
        return mono;
    }

    private float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate) return samples;

        var ratio = (double)toRate / fromRate;
        var newLength = (int)(samples.Length * ratio);
        var resampled = new float[newLength];

        for (int i = 0; i < newLength; i++)
        {
            var srcIndex = i / ratio;
            var srcIndexInt = (int)srcIndex;
            var frac = srcIndex - srcIndexInt;

            if (srcIndexInt + 1 < samples.Length)
            {
                // Linear interpolation
                resampled[i] = (float)(samples[srcIndexInt] * (1 - frac) + samples[srcIndexInt + 1] * frac);
            }
            else
            {
                resampled[i] = samples[srcIndexInt];
            }
        }

        return resampled;
    }

    private List<SpectralPeak> ExtractSpectralPeaks(float[] audioData)
    {
        var peaks = new List<SpectralPeak>();
        var frameCount = (audioData.Length - FftSize) / HopSize;

        for (int frame = 0; frame < frameCount; frame++)
        {
            var frameStart = frame * HopSize;
            var frameData = new double[FftSize];

            // Copy frame with Hann window
            for (int i = 0; i < FftSize && frameStart + i < audioData.Length; i++)
            {
                var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / FftSize));
                frameData[i] = audioData[frameStart + i] * window;
            }

            // Compute FFT using modern FftSharp API
            var fft = FftSharp.FFT.Forward(frameData);
            var magnitude = FftSharp.FFT.Magnitude(fft);

            // Find peaks in each frequency band
            foreach (var (low, high) in FrequencyBands)
            {
                var lowBin = FrequencyToBin(low, SampleRate, FftSize);
                var highBin = FrequencyToBin(high, SampleRate, FftSize);

                var peakBin = FindPeakInRange(magnitude, lowBin, highBin);
                if (peakBin >= 0 && magnitude[peakBin] > 0.01) // Minimum magnitude threshold
                {
                    peaks.Add(new SpectralPeak
                    {
                        TimeFrame = frame,
                        FrequencyBin = peakBin,
                        Magnitude = magnitude[peakBin]
                    });
                }
            }
        }

        return peaks;
    }

    private int FrequencyToBin(int frequency, int sampleRate, int fftSize)
    {
        return (int)Math.Round((double)frequency * fftSize / sampleRate);
    }

    private int FindPeakInRange(double[] magnitude, int low, int high)
    {
        var maxBin = -1;
        var maxMagnitude = 0.0;

        for (int i = low; i < high && i < magnitude.Length; i++)
        {
            if (magnitude[i] > maxMagnitude)
            {
                maxMagnitude = magnitude[i];
                maxBin = i;
            }
        }

        return maxBin;
    }

    private string HashPeakConstellation(List<SpectralPeak> peaks)
    {
        // Sort peaks by time frame
        var sortedPeaks = peaks.OrderBy(p => p.TimeFrame).ToList();

        // Create hash from peak constellation
        using var sha256 = SHA256.Create();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        foreach (var peak in sortedPeaks.Take(1000)) // Limit to first 1000 peaks
        {
            writer.Write(peak.TimeFrame);
            writer.Write(peak.FrequencyBin);
        }

        var hash = sha256.ComputeHash(ms.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private byte[] SerializePeaks(List<SpectralPeak> peaks)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(peaks.Count);
        foreach (var peak in peaks.Take(1000))
        {
            writer.Write(peak.TimeFrame);
            writer.Write((ushort)peak.FrequencyBin);
            writer.Write((float)peak.Magnitude);
        }

        return ms.ToArray();
    }

    private double CalculateHammingSimilarity(byte[] data1, byte[] data2)
    {
        var minLength = Math.Min(data1.Length, data2.Length);
        var maxLength = Math.Max(data1.Length, data2.Length);

        if (maxLength == 0) return 1.0;

        var differingBits = 0;
        var totalBits = maxLength * 8;

        for (int i = 0; i < minLength; i++)
        {
            var xor = data1[i] ^ data2[i];
            differingBits += CountSetBits(xor);
        }

        // Account for length difference
        differingBits += (maxLength - minLength) * 8;

        var similarity = 1.0 - (differingBits / (double)totalBits);
        return Math.Max(0.0, similarity);
    }

    private int CountSetBits(int b)
    {
        var count = 0;
        while (b != 0)
        {
            count += b & 1;
            b >>= 1;
        }
        return count;
    }

    private struct SpectralPeak
    {
        public int TimeFrame;
        public int FrequencyBin;
        public double Magnitude;
    }
}
