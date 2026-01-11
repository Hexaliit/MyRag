using AudioSummarizer.Core.Models;
using FftSharp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Analyzes music characteristics: BPM, key, energy, rhythm patterns
/// Uses signal processing (no ML models required for MVP)
/// Based on 2025 research showing effective deterministic techniques
/// </summary>
public class MusicAnalysisWave : IAudioWave
{
    private readonly ILogger<MusicAnalysisWave> _logger;

    public int Priority => 65; // After content classification, before transcription
    public string Name => "MusicAnalysisWave";

    public MusicAnalysisWave(ILogger<MusicAnalysisWave> logger)
    {
        _logger = logger;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Only run if content type suggests music is present
        // For MVP, always run - can optimize later based on ContentClassifierWave results
        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var signals = new List<Signal>();

        try
        {
            var analysis = await Task.Run(() => AnalyzeMusic(audioPath), cancellationToken);

            // BPM signals
            signals.Add(CreateSignal("music.bpm", analysis.Bpm, analysis.BpmConfidence));
            signals.Add(CreateSignal("music.tempo_category", analysis.TempoCategory, null));

            // Key detection signals
            signals.Add(CreateSignal("music.key", analysis.Key, analysis.KeyConfidence));
            signals.Add(CreateSignal("music.mode", analysis.Mode, null));

            // Energy signals
            signals.Add(CreateSignal("music.energy_db", analysis.EnergyDb, null));
            signals.Add(CreateSignal("music.dynamic_range_db", analysis.DynamicRangeDb, null));
            signals.Add(CreateSignal("music.energy.low_hz", analysis.EnergyLowHz, null));
            signals.Add(CreateSignal("music.energy.mid_hz", analysis.EnergyMidHz, null));
            signals.Add(CreateSignal("music.energy.high_hz", analysis.EnergyHighHz, null));

            // Rhythm signals
            signals.Add(CreateSignal("music.beat_strength", analysis.BeatStrength, null));
            signals.Add(CreateSignal("music.rhythm_regularity", analysis.RhythmRegularity, null));

            sw.Stop();
            _logger.LogInformation(
                "Music analysis: BPM={Bpm:F1} ({Tempo}), Key={Key} {Mode}, Energy={Energy:F1}dB, Time={Time}ms",
                analysis.Bpm, analysis.TempoCategory, analysis.Key, analysis.Mode,
                analysis.EnergyDb, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Music analysis failed for {AudioPath}", audioPath);
            signals.Add(CreateSignal("music.bpm", 0.0, 0.0));
            signals.Add(CreateSignal("music.key", "unknown", 0.0));
        }

        return signals;
    }

    private MusicAnalysis AnalyzeMusic(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);

        // Convert to mono for analysis
        ISampleProvider sampleProvider = reader.WaveFormat.Channels == 1
            ? reader
            : new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };

        var sampleRate = sampleProvider.WaveFormat.SampleRate;
        var samples = new List<float>();

        // Read all samples
        var buffer = new float[sampleRate];
        int samplesRead;
        while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        if (samples.Count == 0)
            return new MusicAnalysis();

        // Analyze different aspects
        var bpmResult = DetectBpm(samples, sampleRate);
        var keyResult = DetectKey(samples, sampleRate);
        var energyResult = AnalyzeEnergy(samples, sampleRate);
        var rhythmResult = AnalyzeRhythm(samples, sampleRate);

        return new MusicAnalysis
        {
            Bpm = bpmResult.Bpm,
            BpmConfidence = bpmResult.Confidence,
            TempoCategory = CategorizeTempoFromBpm(bpmResult.Bpm),
            Key = keyResult.Key,
            Mode = keyResult.Mode,
            KeyConfidence = keyResult.Confidence,
            EnergyDb = energyResult.AverageDb,
            DynamicRangeDb = energyResult.DynamicRangeDb,
            EnergyLowHz = energyResult.LowBandRatio,
            EnergyMidHz = energyResult.MidBandRatio,
            EnergyHighHz = energyResult.HighBandRatio,
            BeatStrength = rhythmResult.BeatStrength,
            RhythmRegularity = rhythmResult.Regularity
        };
    }

    #region BPM Detection

    private (double Bpm, double Confidence) DetectBpm(List<float> samples, int sampleRate)
    {
        // 1. Calculate onset strength envelope
        var onsetEnvelope = CalculateOnsetEnvelope(samples, sampleRate);

        if (onsetEnvelope.Count < 2)
            return (0, 0);

        // 2. Autocorrelation to find periodicity
        var autocorr = CalculateAutocorrelation(onsetEnvelope);

        // 3. Find peaks in autocorrelation (representing beat intervals)
        var bpmRange = (min: 60.0, max: 180.0); // Typical music range
        var hopSize = 512;
        var frameRate = sampleRate / (double)hopSize;

        var peakLag = FindStrongestPeak(autocorr, bpmRange, frameRate);

        if (peakLag == 0)
            return (0, 0);

        var bpm = 60.0 * frameRate / peakLag;
        var confidence = autocorr[peakLag] / autocorr.Max();

        return (bpm, confidence);
    }

    private List<double> CalculateOnsetEnvelope(List<float> samples, int sampleRate)
    {
        var hopSize = 512;
        var frameSize = 2048;
        var envelope = new List<double>();

        var previousSpectrum = new double[frameSize / 2];

        for (int i = 0; i < samples.Count - frameSize; i += hopSize)
        {
            var frame = samples.Skip(i).Take(frameSize).Select(s => (double)s).ToArray();

            // Simple spectral flux: sum of positive differences in magnitude spectrum
            var spectrum = CalculateMagnitudeSpectrum(frame);

            double flux = 0;
            for (int j = 0; j < spectrum.Length; j++)
            {
                var diff = spectrum[j] - previousSpectrum[j];
                if (diff > 0) flux += diff;
            }

            envelope.Add(flux);
            Array.Copy(spectrum, previousSpectrum, spectrum.Length);
        }

        return envelope;
    }

    private double[] CalculateMagnitudeSpectrum(double[] frame)
    {
        // FFT requires power-of-2 length, so pad if necessary
        var fftSize = NextPowerOf2(frame.Length);
        var paddedFrame = new double[fftSize];
        Array.Copy(frame, paddedFrame, frame.Length);

        // Use FftSharp for fast FFT calculation
        var fft = FftSharp.FFT.Forward(paddedFrame);
        var spectrum = new double[fft.Length / 2];

        for (int i = 0; i < spectrum.Length; i++)
        {
            var real = fft[i].Real;
            var imag = fft[i].Imaginary;
            spectrum[i] = Math.Sqrt(real * real + imag * imag);
        }

        return spectrum;
    }

    private static int NextPowerOf2(int n)
    {
        if (n <= 0) return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }

    private List<double> CalculateAutocorrelation(List<double> signal)
    {
        var maxLag = Math.Min(signal.Count / 2, 500); // Limit computation
        var autocorr = new List<double>(maxLag);

        for (int lag = 0; lag < maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i < signal.Count - lag; i++)
            {
                sum += signal[i] * signal[i + lag];
            }
            autocorr.Add(sum);
        }

        return autocorr;
    }

    private int FindStrongestPeak(List<double> autocorr, (double min, double max) bpmRange, double frameRate)
    {
        var minLag = (int)(60.0 * frameRate / bpmRange.max);
        var maxLag = (int)(60.0 * frameRate / bpmRange.min);

        if (minLag >= autocorr.Count || maxLag >= autocorr.Count)
            return 0;

        var peakLag = 0;
        var peakValue = 0.0;

        for (int lag = minLag; lag < Math.Min(maxLag, autocorr.Count); lag++)
        {
            // Simple peak detection: higher than neighbors
            if (lag > 0 && lag < autocorr.Count - 1 &&
                autocorr[lag] > autocorr[lag - 1] &&
                autocorr[lag] > autocorr[lag + 1] &&
                autocorr[lag] > peakValue)
            {
                peakValue = autocorr[lag];
                peakLag = lag;
            }
        }

        return peakLag;
    }

    private string CategorizeTempoFromBpm(double bpm)
    {
        if (bpm == 0) return "unknown";
        if (bpm < 60) return "very_slow";
        if (bpm < 90) return "slow";
        if (bpm < 120) return "moderate";
        if (bpm < 140) return "fast";
        return "very_fast";
    }

    #endregion

    #region Key Detection

    private (string Key, string Mode, double Confidence) DetectKey(List<float> samples, int sampleRate)
    {
        // Chroma feature extraction + template matching
        var chroma = CalculateChroma(samples, sampleRate);

        if (chroma == null || chroma.Length == 0)
            return ("unknown", "unknown", 0);

        // Major and minor key templates (Krumhansl-Schmuckler profiles)
        var majorTemplate = new[] { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        var minorTemplate = new[] { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        var keys = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        var bestKey = "C";
        var bestMode = "major";
        var bestCorrelation = -1.0;

        // Try all 24 keys (12 major + 12 minor)
        for (int shift = 0; shift < 12; shift++)
        {
            var majorCorr = CalculateCorrelation(chroma, Rotate(majorTemplate, shift));
            var minorCorr = CalculateCorrelation(chroma, Rotate(minorTemplate, shift));

            if (majorCorr > bestCorrelation)
            {
                bestCorrelation = majorCorr;
                bestKey = keys[shift];
                bestMode = "major";
            }

            if (minorCorr > bestCorrelation)
            {
                bestCorrelation = minorCorr;
                bestKey = keys[shift];
                bestMode = "minor";
            }
        }

        var confidence = (bestCorrelation + 1) / 2; // Normalize correlation to 0-1

        return (bestKey, bestMode, confidence);
    }

    private double[] CalculateChroma(List<float> samples, int sampleRate)
    {
        // Simplified chroma feature extraction
        // Maps frequency content to 12 pitch classes (C, C#, D, ..., B)
        var chroma = new double[12];
        var frameSize = 4096;
        var hopSize = 2048;

        for (int i = 0; i < samples.Count - frameSize; i += hopSize)
        {
            var frame = samples.Skip(i).Take(frameSize).Select(s => (double)s).ToArray();
            var spectrum = CalculateMagnitudeSpectrum(frame);

            // Map frequency bins to pitch classes
            for (int bin = 1; bin < spectrum.Length; bin++)
            {
                var freq = bin * sampleRate / (double)frameSize;
                if (freq < 50 || freq > 5000) continue; // Focus on musical range

                var pitchClass = FrequencyToPitchClass(freq);
                chroma[pitchClass] += spectrum[bin];
            }
        }

        // Normalize
        var sum = chroma.Sum();
        if (sum > 0)
        {
            for (int i = 0; i < chroma.Length; i++)
                chroma[i] /= sum;
        }

        return chroma;
    }

    private int FrequencyToPitchClass(double freq)
    {
        // Convert frequency to MIDI note, then to pitch class (0-11)
        var midiNote = 69 + 12 * Math.Log2(freq / 440.0); // A4 = 440 Hz = MIDI 69
        var pitchClass = ((int)Math.Round(midiNote)) % 12;
        return pitchClass < 0 ? pitchClass + 12 : pitchClass;
    }

    private double CalculateCorrelation(double[] a, double[] b)
    {
        if (a.Length != b.Length) return 0;

        var meanA = a.Average();
        var meanB = b.Average();

        double numerator = 0, denomA = 0, denomB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            var devA = a[i] - meanA;
            var devB = b[i] - meanB;
            numerator += devA * devB;
            denomA += devA * devA;
            denomB += devB * devB;
        }

        var denom = Math.Sqrt(denomA * denomB);
        return denom > 0 ? numerator / denom : 0;
    }

    private double[] Rotate(double[] array, int shift)
    {
        var result = new double[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array[(i + shift) % array.Length];
        }
        return result;
    }

    #endregion

    #region Energy Analysis

    private (double AverageDb, double DynamicRangeDb, double LowBandRatio, double MidBandRatio, double HighBandRatio)
        AnalyzeEnergy(List<float> samples, int sampleRate)
    {
        var frameSize = sampleRate / 10; // 100ms frames
        var frameEnergies = new List<double>();
        double lowBandEnergy = 0, midBandEnergy = 0, highBandEnergy = 0;

        for (int i = 0; i < samples.Count - frameSize; i += frameSize)
        {
            var frame = samples.Skip(i).Take(frameSize).ToList();
            var rms = Math.Sqrt(frame.Sum(s => s * s) / frame.Count);
            frameEnergies.Add(rms);

            // Frequency band analysis (simplified)
            var spectrum = CalculateMagnitudeSpectrum(frame.Select(s => (double)s).ToArray());
            var binHz = sampleRate / (double)(frameSize * 2);

            for (int bin = 0; bin < spectrum.Length; bin++)
            {
                var freq = bin * binHz;
                if (freq < 250) lowBandEnergy += spectrum[bin];
                else if (freq < 4000) midBandEnergy += spectrum[bin];
                else highBandEnergy += spectrum[bin];
            }
        }

        var avgRms = frameEnergies.Average();
        var avgDb = 20 * Math.Log10(avgRms + 1e-10);

        var maxRms = frameEnergies.Max();
        var minRms = frameEnergies.Where(e => e > 1e-6).DefaultIfEmpty(1e-6).Min();
        var dynamicRange = 20 * Math.Log10(maxRms / minRms);

        var totalBandEnergy = lowBandEnergy + midBandEnergy + highBandEnergy;
        var lowRatio = totalBandEnergy > 0 ? lowBandEnergy / totalBandEnergy : 0;
        var midRatio = totalBandEnergy > 0 ? midBandEnergy / totalBandEnergy : 0;
        var highRatio = totalBandEnergy > 0 ? highBandEnergy / totalBandEnergy : 0;

        return (avgDb, dynamicRange, lowRatio, midRatio, highRatio);
    }

    #endregion

    #region Rhythm Analysis

    private (double BeatStrength, double Regularity) AnalyzeRhythm(List<float> samples, int sampleRate)
    {
        // Calculate beat strength from onset envelope variance
        var onsetEnvelope = CalculateOnsetEnvelope(samples, sampleRate);

        if (onsetEnvelope.Count < 2)
            return (0, 0);

        var mean = onsetEnvelope.Average();
        var variance = onsetEnvelope.Sum(v => Math.Pow(v - mean, 2)) / onsetEnvelope.Count;
        var beatStrength = Math.Sqrt(variance) / (mean + 1e-10);

        // Regularity: how consistent are beat intervals
        var autocorr = CalculateAutocorrelation(onsetEnvelope);
        var maxAutocorr = autocorr.Skip(10).Take(autocorr.Count - 20).Max(); // Skip DC and very short lags
        var regularity = maxAutocorr / (autocorr[0] + 1e-10);

        return (Math.Clamp(beatStrength, 0, 1), Math.Clamp(regularity, 0, 1));
    }

    #endregion

    private Signal CreateSignal(string name, object value, double? confidence)
    {
        var type = name switch
        {
            "music.key" or "music.mode" or "music.tempo_category" => SignalType.Classification,
            _ => SignalType.Acoustic
        };

        return new Signal
        {
            Name = name,
            Value = value,
            Confidence = confidence,
            Type = type,
            Source = Name
        };
    }

    private class MusicAnalysis
    {
        public double Bpm { get; set; }
        public double BpmConfidence { get; set; }
        public string TempoCategory { get; set; } = "unknown";
        public string Key { get; set; } = "unknown";
        public string Mode { get; set; } = "unknown";
        public double KeyConfidence { get; set; }
        public double EnergyDb { get; set; }
        public double DynamicRangeDb { get; set; }
        public double EnergyLowHz { get; set; }
        public double EnergyMidHz { get; set; }
        public double EnergyHighHz { get; set; }
        public double BeatStrength { get; set; }
        public double RhythmRegularity { get; set; }
    }
}
