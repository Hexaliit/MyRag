# Music Analysis Wave - Design Document

## Overview

Add structural music characterization following "Constrained Fuzziness" principles:
- ✅ Analyze: BPM, tempo, key, energy, rhythm patterns
- ❌ Do NOT: Identify songs, artists, or download lyrics

## Features

### 1. Deterministic Features (Signal Processing)

**Tempo/BPM Analysis**
- Beat detection using onset strength
- Autocorrelation for tempo estimation
- BPM range: 60-180 typical, support 40-200
- Confidence score based on periodicity strength

**Key Detection**
- Chroma feature extraction (12-bin pitch class profile)
- Template matching against major/minor keys
- Output: Key (C, C#, D, etc.) + Mode (major/minor)
- Confidence score

**Rhythm Analysis**
- Beat grid strength (how regular the beats are)
- Syncopation measure
- Time signature estimation (4/4, 3/4, 6/8, etc.)

**Energy/Dynamics**
- RMS energy over time
- Dynamic range
- Energy distribution (low/mid/high frequency bands)

### 2. ML-Based Features (ONNX Models)

**Genre Classification**
- Model: Music genre CNN (ONNX)
- Input: Mel spectrogram
- Output: Genre probabilities (rock, jazz, classical, electronic, hip-hop, etc.)
- Store as signals with confidence scores

**Mood/Emotion Detection**
- Model: Audio mood classifier (Yamnet-based or custom)
- Output: Valence (positive/negative) + Arousal (calm/energetic)
- Categories: Happy, Sad, Energetic, Calm, Aggressive, Peaceful

**Vocal/Instrumental Detection**
- Feature-based or lightweight ML
- Binary classification: Has vocals / Instrumental only
- Confidence score

**Instrument Detection** (Optional)
- Multi-label classification
- Common instruments: piano, guitar, drums, bass, strings, brass, synth
- Confidence per instrument

## Implementation Plan

### Phase 3.5: Music Analysis Wave (Priority 65)

**Files to Create**:
- `src/AudioSummarizer.Core/Services/Analysis/Waves/MusicAnalysisWave.cs`
- `src/AudioSummarizer.Core/Services/Music/BpmDetector.cs`
- `src/AudioSummarizer.Core/Services/Music/KeyDetector.cs`
- `src/AudioSummarizer.Core/Services/Music/GenreClassifier.cs` (ONNX)
- `src/AudioSummarizer.Core/Models/MusicProfile.cs`

**Dependencies**:
- FftSharp (already included) - For spectral analysis
- Microsoft.ML.OnnxRuntime (already included) - For ML models

**Signals to Extract**:
```csharp
// Deterministic
music.bpm = 128.5
music.bpm_confidence = 0.92
music.key = "G"
music.mode = "major"
music.key_confidence = 0.78
music.time_signature = "4/4"
music.beat_strength = 0.85  // How regular the beats are
music.syncopation = 0.23    // 0=steady, 1=highly syncopated
music.energy_db = -12.5     // Average RMS energy
music.dynamic_range_db = 18.5

// Frequency band energy (for music characterization)
music.energy.low_hz = 0.65    // 20-250 Hz (bass)
music.energy.mid_hz = 0.82    // 250-4000 Hz (vocals, instruments)
music.energy.high_hz = 0.45   // 4000-20000 Hz (cymbals, air)

// ML-based (probabilistic)
music.genre = "rock"
music.genre_confidence = 0.73
music.genre_distribution = {"rock": 0.73, "alternative": 0.15, "pop": 0.08, "other": 0.04}

music.has_vocals = true
music.vocal_confidence = 0.89

music.mood_valence = 0.65    // -1 (negative) to +1 (positive)
music.mood_arousal = 0.72    // 0 (calm) to 1 (energetic)
music.mood_category = "energetic"
```

## ONNX Models Research

### Available Pre-trained Models

**1. Music Genre Classification**
- **Yamnet** (Google) - Audio event classifier, can be fine-tuned for genre
  - HuggingFace: `google/yamnet`
  - Can export to ONNX
  
- **Music Genre CNN** - Custom models on HuggingFace
  - Search: "music genre onnx"
  - GTZAN dataset-trained models available

**2. Mood/Emotion Detection**
- **Essentia Audio Mood** - Research models from MTG
  - Valence-Arousal models
  - May need ONNX export

**3. Lightweight Alternative: Feature-Based**
- Calculate mel spectrograms using FftSharp
- Use statistical features (spectral centroid, rolloff, zero-crossing rate)
- Simple heuristics for genre/mood classification
- No ML needed, fully deterministic

## Recommended Approach

### MVP (No ML Models Yet)

Start with pure signal processing:

1. **BPM Detection** - Using onset strength + autocorrelation
2. **Key Detection** - Using chroma features + template matching
3. **Energy Analysis** - RMS and frequency band distribution
4. **Rhythm Features** - Beat strength, syncopation estimation

This gives us valuable music characterization without ML dependencies.

### Future Enhancement (ML Models)

Once core features work:

1. Add genre classification ONNX model
2. Add mood detection based on audio features
3. Add vocal detection model

## Implementation Priority

**Priority Order**:
1. BPM Detection (most requested, useful)
2. Energy/Dynamics Analysis (easy, valuable)
3. Key Detection (moderate difficulty)
4. Rhythm Analysis (beat strength, time signature)
5. Genre Classification (ML, optional)
6. Mood Detection (ML, optional)

## Code Sketch

```csharp
public class MusicAnalysisWave : IAudioWave
{
    public int Priority => 65;  // After fingerprint, before transcription
    public string Name => "MusicAnalysisWave";

    public async Task<Dictionary<string, AudioSignal>> AnalyzeAsync(
        string audioPath, 
        AnalysisContext context, 
        CancellationToken ct)
    {
        var signals = new Dictionary<string, AudioSignal>();
        
        // Load audio
        using var reader = new AudioFileReader(audioPath);
        var samples = LoadSamples(reader);
        
        // BPM Detection
        var bpm = BpmDetector.Detect(samples, reader.WaveFormat.SampleRate);
        signals.Add("music.bpm", CreateSignal(bpm.Value, bpm.Confidence));
        
        // Key Detection
        var key = KeyDetector.Detect(samples, reader.WaveFormat.SampleRate);
        signals.Add("music.key", CreateSignal(key.Key, key.Confidence));
        signals.Add("music.mode", CreateSignal(key.Mode));
        
        // Energy Analysis
        var energy = EnergyAnalyzer.Analyze(samples);
        signals.Add("music.energy_db", CreateSignal(energy.AverageDb));
        signals.Add("music.energy.low_hz", CreateSignal(energy.LowBandRatio));
        
        return signals;
    }
}
```

## Testing

**Test Audio Files**:
- Rock song (120-140 BPM, 4/4, minor key)
- Classical music (variable tempo, major key)
- Electronic/Dance (consistent 128 BPM, 4/4)
- Jazz (swing rhythm, 3/4 or 4/4)
- Hip-hop (90-110 BPM, strong beat)

## Success Criteria

- BPM detection within ±3 BPM for music with steady beats
- Key detection >70% accuracy for clear tonality
- Genre classification >60% top-1 accuracy (if using ML)
- Processing time <2x transcription time

---

**Status**: Planned for Phase 3.5
**Fits Design**: ✅ Yes - Structural analysis, no song identification
