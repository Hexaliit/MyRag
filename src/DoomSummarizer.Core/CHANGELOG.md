# Changelog - DoomSummarizer.Core

## 1.3.0 — Unified Media Pipeline, No-GPU Build, CUDA Detection Fix

### New Features

#### Unified Media Pipeline (`FEATURE_COMPLETE`)

Image, audio, and video files ingested via `scroll` now go through the full analysis pipelines
instead of lightweight filename-based descriptions:

- **Images**: 22-wave ImagePipeline (Florence-2 captioning, OCR, entity detection, color analysis,
  motion detection, scene classification) produces searchable content items
- **Audio**: AudioPipeline with Whisper transcription, speaker diarization via ECAPA-TDNN,
  acoustic profiling, fingerprinting, and optional Demucs source separation
- **Video**: VideoPipeline with shot detection, scene segmentation, keyframe OCR, and transcript
  extraction into 60-second searchable windows

All pipeline signals (captions, OCR text, entities, transcripts, metadata) become `ContentItem`
records — searchable and queryable like any document chunk.

- `BuildMediaPipelineProvider()` creates a mini DI container with ImagePipeline + AudioPipeline +
  VideoPipeline + PipelineRegistry
- `IPipelineRegistry.FindForFile()` auto-routes by extension to the correct pipeline
- Audio (`.mp3`, `.wav`, `.m4a`, `.flac`, `.ogg`, `.wma`, `.aac`, `.opus`) and video (`.mp4`,
  `.mkv`, `.avi`, `.webm`, `.mov`, `.wmv`, `.flv`, `.m4v`, `.mpeg`, `.mpg`) extensions added to
  `ResolveLocalSources`

#### No-GPU Build Variant (`-p:ExcludeGpu=true`)

New `ExcludeGpu` MSBuild property produces a CPU-only `lucidrag` binary without GPU-specific
native libraries:

| Variant | Command | Size |
|---------|---------|------|
| `doomsummarizer` (slim) | `dotnet publish -c Release` | ~30 MB |
| `lucidrag` (full + GPU) | `dotnet publish -c Release -p:CompleteBuild=true` | ~1.1 GB |
| `lucidrag` (full, CPU-only) | `dotnet publish -c Release -p:CompleteBuild=true -p:ExcludeGpu=true` | ~560 MB |

`ExcludeGpu=true`:
- Swaps `OnnxRuntime.DirectML` / `OnnxRuntime.Gpu` for base `OnnxRuntime` (CPU-only)
- Forces `LLamaSharp.Backend.Cpu` instead of `LLamaSharp.Backend.Cuda12`
- Saves ~600 MB (318 MB `onnxruntime_providers_cuda.dll` + 275 MB `ggml-cuda.dll`)

#### CUDA Toolkit Detection Fix

Fixed ONNX Runtime printing ugly native error messages (`[E:onnxruntime:CSharpOnnxRuntime...]`)
to stderr when CUDA Toolkit is not installed:

- **Root cause**: `OnnxSessionFactory.TryDetectCuda()` found `nvidia-smi.exe` (GPU driver) and
  assumed CUDA Toolkit was installed. The native `AppendExecutionProvider_CUDA()` call then failed
  loudly when `cublasLt64_12.dll` was missing.
- **Fix**: All three CUDA registration sites now probe for the actual Toolkit DLL using
  `NativeLibrary.TryLoad("cublasLt64_12")` before attempting the CUDA EP. If the Toolkit is not
  installed, CUDA is silently skipped with no native error output.
- Applied in: `OnnxSessionFactory` (ImageSummarizer.Core), `OnnxEmbeddingService`
  (DocSummarizer.Core), `OnnxEmbeddingService` (DataSummarizer)

## Unreleased — Score-Based Source Routing & Deduplication

### New Features

#### Self-Describing Source Routing

Sources in `Resources/sources.yaml` now declare their own routing metadata:

- **`intent_affinity`** (dict of intent → score 0–1): How well a source serves each intent type
  (`news`, `qa`, `research`, `howto`, `roundup`, `deep_dive`, `search_only`, `trend`)
- **`capabilities`** (list of tags): `search`, `knowledge`, `news`, `realtime`, `tech_only`,
  `archive`, `academic`, `reference`, `government`, `satire`

New `SourceRouter` methods:

| Method | Purpose |
|--------|---------|
| `ScoreSource(name, intent, categories, timeSensitivity)` | Score a source using `(intentAffinity × 0.6) + (categoryMatch × 0.3) + (capabilityBonus × 0.1)` |
| `HasCapability(name, capability)` | Check if a source has a specific capability tag |
| `GetSourcesWithCapability(capability)` | Find all sources with a given capability |

`SentinelSourceMapper.MapToSources()` now uses score-based selection instead of hardcoded
phase-by-phase logic. The old `TechOnlySources` and `ArchiveSources` static HashSets are replaced
by YAML capability queries.

**Affected files**:
- `Models/SourceConfig.cs` — `IntentAffinity` and `Capabilities` properties on `SourceDefinition`
- `Resources/sources.yaml` — All ~30 sources annotated with `intent_affinity` + `capabilities`
- `Services/SourceRouter.cs` — `ScoreSource()`, `HasCapability()`, `GetSourcesWithCapability()`
- `Services/SentinelSourceMapper.cs` — Score-based selection replacing phases 2–4
- `Services/PromptInterpreter.cs` — Removed duplicate HashSets, improved sentinel QA guidance

#### Ingestion Deduplication

Two-phase deduplication pipeline for document ingestion:

1. **Pre-embedding dedup** — Cheap text signals (word Jaccard, trigram overlap, length, heading)
   filter obvious duplicates before embedding. O(N) per chunk.
2. **Semantic dedup** — Cosine similarity on embedding vectors catches paraphrases. Survivors
   absorb duplicates as logarithmic salience boosts.

Adaptive chunk limits by document type (fiction, technical, academic, non-fiction) with
configurable min/max survivors and threshold overrides.

#### Routing Quality Tuning

Score-based routing quality pass across 30 diverse query scenarios:

- **Academic filter for QA**: `arxiv` and other `academic`-capability search sources are now
  hard-filtered from QA/howto intent. Academic papers don't answer factual trivia — this frees
  a source slot for more relevant feeds like Wikipedia and ScienceDaily.
- **Knowledge source promotion**: Wikipedia and other `knowledge`-capability feeds are promoted
  ahead of regular news feeds for QA/howto, ensuring they appear at position ≤3 instead of last.
- **Affinity tuning**: Lowered QA affinity for domain-specific sources that shouldn't appear in
  general factual queries: `spaceflight` (0.4→0.15), `arxiv` (0.4→0.15), `earthquake` (0.5→0.25),
  `parliament` (0.7→0.3), `ukpolice` (0.7→0.3), `ukflood` (0.6→0.35).
- **Routing rule fixes**: Removed `spaceflight` from `science` routing (space ≠ science), added
  `duckduckgo` to `science` routing (web search is essential for science QA).
- **30-scenario diagnostic test** (`ScoreDiagTest.cs`) covering QA, news, breaking, research,
  entertainment, politics, space, niche, historical, health, tech, and opinion queries.

#### Code Consolidation & Dead Code Removal

Two-pass refactoring eliminating ~175 lines of duplicate/dead code:

- **Consolidated `MapYamlSourceToCli`**: Removed duplicate from `PromptInterpreter`; both files
  now use the single `SentinelSourceMapper.MapYamlSourceToCli()` method. Collapsed the redundant
  20-case identity switch arm (the `_ => yamlSource` default already handled it).
- **Consolidated `ExtractTopicTerms` + `StopWords`**: Removed duplicate definitions from
  `PromptInterpreter`; now calls `SentinelSourceMapper.ExtractTopicTerms()` and
  `SentinelSourceMapper.ExtractTopicTermsExcluding()`.
- **Replaced hardcoded `newsSources` array** in `PromptInterpreter` with YAML-driven
  `router.AllSources` query + small legacy fallback set. New sources added to `sources.yaml`
  are now automatically detectable in the keyword fallback path.
- **Removed dead `FilterSourcesByScope()`** from `SourceRouter` (public method with zero callers).
- **Removed legacy `ParsedPrompt` fallback path**: The intermediate backward-compat code path
  (`ParsedPrompt` record, `PromptJsonContext`, `EnrichWithYamlRoutingAsync`) was unreachable
  with the current sentinel prompt format. The keyword `FallbackInterpretAsync` already handles
  sentinel failure gracefully. Removed ~65 lines.
- **Extracted `FindBestRouting` helper**: Deduplicated identical "find best routing context for
  source" loop that appeared twice in `MapToSources` (knowledge promotion + feed selection).
- **Tightened access modifiers**: `TechCategories` and `StopWords` narrowed from `internal` to
  `private` after removing their only external consumer (`EnrichWithYamlRoutingAsync`).
- **Removed unused `using`**: Cleaned up `System.Text.Json.Serialization` import from
  `PromptInterpreter` after `PromptJsonContext` removal.
- **Fixed `ValidCategories` gap**: Added 4 UK-specific routing categories (`uk_politics`, `crime`,
  `flooding`, `uk`) that existed in `sources.yaml` routing but were missing from sentinel
  validation. Added corresponding `CategoryAliases` (`flood`→`flooding`, `policing`→`crime`,
  `parliament`→`uk_politics`).

### Breaking Changes

- `SourceDefinition` gains two new nullable properties (`IntentAffinity`, `Capabilities`). Existing
  YAML files without these fields will use type-derived defaults — no action needed.
- `SentinelSourceMapper.TechOnlySources` and `ArchiveSources` static fields are removed. Use
  `SourceRouter.HasCapability(name, "tech_only")` / `HasCapability(name, "archive")` instead.

### Tests

- 12 new `SourceRouterTests` (scoring, capabilities, range validation, fallback behavior)
- 3 new `SentinelSourceMapperTests` regression tests (factual QA routing, Wikipedia inclusion, news
  intent preservation)
- 30-scenario `ScoreDiagTest` covering routing quality across all intent types
- All 863 tests passing
