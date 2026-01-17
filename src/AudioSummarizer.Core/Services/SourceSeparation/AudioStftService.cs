using System.Numerics;

namespace AudioSummarizer.Core.Services.SourceSeparation;

/// <summary>
/// Short-Time Fourier Transform (STFT) and Inverse STFT for Demucs.
/// Demucs uses: nfft=4096, hop_length=1024
/// </summary>
public class AudioStftService
{
    private readonly ILogger<AudioStftService> _logger;

    // Demucs STFT parameters
    public const int NFFt = 4096;
    public const int HopLength = 1024;
    public const int WindowSize = NFFt;

    // Precomputed Hann window
    private readonly double[] _window;

    public AudioStftService(ILogger<AudioStftService> logger)
    {
        _logger = logger;
        _window = CreateHannWindow(WindowSize);
    }

    /// <summary>
    /// Compute STFT of audio signal
    /// Returns complex spectrogram as [frames, freq_bins, 2] where last dim is [real, imag]
    /// </summary>
    public float[,,] ComputeStft(float[] audio)
    {
        var numFrames = (audio.Length - NFFt) / HopLength + 1;
        var numBins = NFFt / 2 + 1; // Only positive frequencies

        var spectrogram = new float[numFrames, numBins, 2]; // [frames, bins, real/imag]

        for (int frame = 0; frame < numFrames; frame++)
        {
            var startIdx = frame * HopLength;

            // Extract frame and apply window
            var windowedFrame = new double[NFFt];
            for (int i = 0; i < NFFt && startIdx + i < audio.Length; i++)
            {
                windowedFrame[i] = audio[startIdx + i] * _window[i];
            }

            // Compute FFT
            var fft = FftSharp.FFT.Forward(windowedFrame);

            // Store positive frequencies only
            for (int bin = 0; bin < numBins; bin++)
            {
                spectrogram[frame, bin, 0] = (float)fft[bin].Real;
                spectrogram[frame, bin, 1] = (float)fft[bin].Imaginary;
            }
        }

        return spectrogram;
    }

    /// <summary>
    /// Compute STFT for stereo audio
    /// Returns [2, frames, freq_bins, 2] for [channels, frames, bins, real/imag]
    /// </summary>
    public float[,,,] ComputeStftStereo(float[] leftChannel, float[] rightChannel)
    {
        var leftSpec = ComputeStft(leftChannel);
        var rightSpec = ComputeStft(rightChannel);

        var frames = leftSpec.GetLength(0);
        var bins = leftSpec.GetLength(1);

        var stereoSpec = new float[2, frames, bins, 2];

        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bins; b++)
            {
                stereoSpec[0, f, b, 0] = leftSpec[f, b, 0];
                stereoSpec[0, f, b, 1] = leftSpec[f, b, 1];
                stereoSpec[1, f, b, 0] = rightSpec[f, b, 0];
                stereoSpec[1, f, b, 1] = rightSpec[f, b, 1];
            }
        }

        return stereoSpec;
    }

    /// <summary>
    /// Inverse STFT to reconstruct audio from complex spectrogram
    /// </summary>
    public float[] ComputeIstft(float[,,] spectrogram, int outputLength)
    {
        var numFrames = spectrogram.GetLength(0);
        var numBins = spectrogram.GetLength(1);

        var output = new double[outputLength];
        var windowSum = new double[outputLength];

        for (int frame = 0; frame < numFrames; frame++)
        {
            var startIdx = frame * HopLength;

            // Build full spectrum (add conjugate symmetric part)
            var fullSpectrum = new Complex[NFFt];
            for (int bin = 0; bin < numBins; bin++)
            {
                fullSpectrum[bin] = new Complex(spectrogram[frame, bin, 0], spectrogram[frame, bin, 1]);
            }

            // Mirror for negative frequencies (conjugate symmetric)
            for (int bin = 1; bin < numBins - 1; bin++)
            {
                fullSpectrum[NFFt - bin] = Complex.Conjugate(fullSpectrum[bin]);
            }

            // Inverse FFT (modifies array in-place)
            FftSharp.FFT.Inverse(fullSpectrum);

            // Overlap-add with window
            for (int i = 0; i < NFFt && startIdx + i < outputLength; i++)
            {
                output[startIdx + i] += fullSpectrum[i].Real * _window[i];
                windowSum[startIdx + i] += _window[i] * _window[i];
            }
        }

        // Normalize by window sum
        var result = new float[outputLength];
        for (int i = 0; i < outputLength; i++)
        {
            result[i] = windowSum[i] > 1e-8 ? (float)(output[i] / windowSum[i]) : 0f;
        }

        return result;
    }

    /// <summary>
    /// Inverse STFT for stereo
    /// </summary>
    public (float[] left, float[] right) ComputeIstftStereo(float[,,,] stereoSpec, int outputLength)
    {
        var frames = stereoSpec.GetLength(1);
        var bins = stereoSpec.GetLength(2);

        // Extract left channel spectrogram
        var leftSpec = new float[frames, bins, 2];
        var rightSpec = new float[frames, bins, 2];

        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bins; b++)
            {
                leftSpec[f, b, 0] = stereoSpec[0, f, b, 0];
                leftSpec[f, b, 1] = stereoSpec[0, f, b, 1];
                rightSpec[f, b, 0] = stereoSpec[1, f, b, 0];
                rightSpec[f, b, 1] = stereoSpec[1, f, b, 1];
            }
        }

        return (ComputeIstft(leftSpec, outputLength), ComputeIstft(rightSpec, outputLength));
    }

    /// <summary>
    /// Convert complex spectrogram to magnitude spectrogram with complex-as-channels format.
    /// Format: [batch, channels*2, freq_bins, frames] where channels*2 = [left_real, left_imag, right_real, right_imag]
    /// </summary>
    public float[] ToComplexAsChannels(float[,,,] stereoSpec)
    {
        var frames = stereoSpec.GetLength(1);
        var bins = stereoSpec.GetLength(2);

        // Output: [1, 4, bins, frames] - batch, 4 channels (L_real, L_imag, R_real, R_imag), freq, time
        var result = new float[1 * 4 * bins * frames];

        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bins; b++)
            {
                // Layout: [channel][bin][frame]
                result[0 * bins * frames + b * frames + f] = stereoSpec[0, f, b, 0]; // L real
                result[1 * bins * frames + b * frames + f] = stereoSpec[0, f, b, 1]; // L imag
                result[2 * bins * frames + b * frames + f] = stereoSpec[1, f, b, 0]; // R real
                result[3 * bins * frames + b * frames + f] = stereoSpec[1, f, b, 1]; // R imag
            }
        }

        return result;
    }

    /// <summary>
    /// Convert complex-as-channels output back to stereo spectrogram
    /// </summary>
    public float[,,,] FromComplexAsChannels(float[] data, int bins, int frames)
    {
        var result = new float[2, frames, bins, 2];

        for (int f = 0; f < frames; f++)
        {
            for (int b = 0; b < bins; b++)
            {
                result[0, f, b, 0] = data[0 * bins * frames + b * frames + f]; // L real
                result[0, f, b, 1] = data[1 * bins * frames + b * frames + f]; // L imag
                result[1, f, b, 0] = data[2 * bins * frames + b * frames + f]; // R real
                result[1, f, b, 1] = data[3 * bins * frames + b * frames + f]; // R imag
            }
        }

        return result;
    }

    private static double[] CreateHannWindow(int size)
    {
        var window = new double[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        }
        return window;
    }
}
