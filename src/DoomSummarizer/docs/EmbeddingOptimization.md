# Embedding Pipeline Optimization

This document covers the embedding subsystem: model selection, GPU acceleration, caching, and the
multi-stage deduplication pipeline that reduces compute cost while preserving retrieval quality.

## Architecture Overview

```
                     ┌──────────────────────────────────────────────────────┐
                     │              Ingestion Pipeline                      │
                     │                                                      │
  Document ──► Chunk ──► Pre-Dedup (cheap text signals) ──► Embed (ONNX)   │
                     │       ▲ removes obvious dupes          │             │
                     │       │ before expensive inference      ▼             │
                     │       │                          Semantic Dedup      │
                     │       │                          (cosine on embeddings)
                     │       │                                │             │
                     │       │                                ▼             │
                     │       │                          Index survivors     │
                     │       │                          (SQLite + Lucene    │
                     │       │                           + HNSW + FTS5)     │
                     └──────────────────────────────────────────────────────┘
                                          │
                     ┌────────────────────▼────────────────────────────────┐
                     │              Retrieval Pipeline                      │
                     │                                                      │
  Query ──► Embed ──► Parallel Search (Lucene BM25F ∥ HNSW cosine)        │
                     │       │                                              │
                     │       ▼                                              │
                     │  RRF Fusion ──► Concentration Detection ──► Expand   │
                     │       │              (document-level focus boost)     │
                     │       ▼                                              │
                     │  Evidence Selection ──► LLM Synthesis                │
                     └──────────────────────────────────────────────────────┘
```

All embedding operations flow through `EmbeddingFactory`, which handles model resolution,
GPU selection, ONNX session creation, and wraps the service in an LFU cache.

## Embedding Models

Five ONNX embedding models are available, all producing 384-dimensional vectors. Models are
downloaded automatically on first use to `~/.doomsummarizer/models/embeddings/`.

| Model | Aliases | Max Seq | Size (INT8) | Size (FP32) | Best For |
|-------|---------|---------|-------------|-------------|----------|
| **all-MiniLM-L6-v2** | `minilm` | 256 | ~23 MB | ~90 MB | General purpose (default) |
| bge-small-en-v1.5 | `bge-small`, `bge` | 512 | ~34 MB | ~130 MB | Highest quality for size |
| gte-small | `gte` | 512 | ~34 MB | ~130 MB | Good all-around |
| multi-qa-MiniLM-L6-cos-v1 | `multi-qa`, `multiqa` | 512 | ~23 MB | ~90 MB | Q&A-optimized retrieval |
| paraphrase-MiniLM-L3-v2 | `paraphrase` | 128 | ~17 MB | ~65 MB | Smallest, fastest |

All models use BERT-style tokenization with `[CLS]`, `[SEP]`, `[PAD]` special tokens. The
quantized (INT8) variants are used by default - roughly 3-4x smaller with ~1-2% quality loss.

### Choosing a Model

- **Default (`all-MiniLM-L6-v2`)**: Good balance of speed and quality. 256 max sequence tokens
  is sufficient for most chunked content.
- **`bge-small-en-v1.5`**: Best retrieval quality among the five. Uses instruction-prefixed
  queries. Choose this if you need maximum precision and can tolerate slightly larger model size.
- **`multi-qa-MiniLM-L6-cos-v1`**: Trained specifically on question-answer pairs. Good choice
  if your primary workflow is `ask` (interactive Q&A) over ingested documents.
- **`paraphrase-MiniLM-L3-v2`**: Fastest inference, smallest model. Use on constrained devices
  (Raspberry Pi, CI runners) where speed matters more than quality.
- **`gte-small`**: Solid alternative to the default with 2x longer sequence support (512 tokens).

### Configuration

```yaml
# config.yaml
embedding:
  model: all-MiniLM-L6-v2    # or: bge, gte, multi-qa, paraphrase
  quantized: true             # false = FP32 full precision
  execution_provider: auto    # auto, cpu, cuda, directml
  gpu_device_id: 0            # 0 = first GPU, 1 = second, etc.
```

```json
// config.json
{
  "embedding": {
    "model": "all-MiniLM-L6-v2",
    "quantized": true,
    "executionProvider": "auto",
    "gpuDeviceId": 0
  }
}
```

The model name is resolved flexibly - `EmbeddingFactory.ParseModel()` accepts the full HuggingFace
name (`all-MiniLM-L6-v2`), the enum name (`AllMiniLmL6V2`), or short aliases (`minilm`, `bge`, etc.).
Unrecognized names fall back to the default model.

## GPU Acceleration

ONNX Runtime supports multiple execution providers for GPU-accelerated inference:

| Provider | Platform | Notes |
|----------|----------|-------|
| **Auto** (default) | All | Tries DirectML → CUDA → CPU |
| CPU | All | Always works, stable baseline. Used in no-GPU builds (`-p:ExcludeGpu=true`) |
| CUDA | NVIDIA | Requires [CUDA Toolkit 12](https://developer.nvidia.com/cuda-downloads) (not just GPU driver) |
| DirectML | Windows | AMD, Intel, NVIDIA via DirectX 12 |

> **CUDA detection**: The runtime probes for `cublasLt64_12.dll` (Windows) / `libcublasLt.so.12`
> (Linux) using `NativeLibrary.TryLoad` before attempting the CUDA execution provider. If the CUDA
> Toolkit is not installed, CUDA is silently skipped - no native error messages are printed.
> Having an NVIDIA GPU driver (nvidia-smi) alone is **not sufficient**; the Toolkit must be
> separately installed.

### DirectML Constraints

DirectML's graph optimizer fuses operators (e.g., `MatMul+Scale` → `FusedMatMul`) for the
tensor shapes seen during the first inference call. This creates two constraints:

1. **Batch dimension is fixed at 1.** The fused kernels are compiled for `batch_size=1`. Passing
   multi-item tensors (batch_size > 1) causes `E_INVALIDARG` or `0xC0000005` access violations.
   `OnnxEmbeddingService` handles this automatically - when GPU is active, batch requests are
   routed through sequential single-item inference. Each item still runs on the GPU; only the
   grouping changes.

2. **`InferenceSession.Run` is not thread-safe.** Unlike CPU execution, DML sessions crash when
   multiple threads call `Run` concurrently. This affects scenarios like `Parallel.ForEachAsync`
   in article processing. `OnnxEmbeddingService` uses a `SemaphoreSlim` inference lock to
   serialize GPU access. CPU sessions have no lock and no contention.

Both constraints are handled transparently - no configuration needed. GPU inference remains
GPU-accelerated with no CPU fallback.

### Multi-GPU Systems

On systems with multiple GPUs (e.g., integrated + discrete), use `--list-gpus` to see available
devices and `--gpu N` to select one:

```bash
doomsummarizer scroll --list-gpus
# GPU 0: AMD Radeon(TM) Graphics (2048 MB)
# GPU 1: NVIDIA RTX A4000 (4096 MB)

doomsummarizer scroll "my topic" --gpu 1    # Use the discrete NVIDIA GPU
```

For persistent override, set `gpu_device_id` in config. The selected device is used for **all**
ONNX sessions in the pipeline, including:

- Primary embedding service (queries, document chunks)
- ArticleProcessor sessions (segment analysis in `page` and `scroll` commands)
- NER model inference (when `--entities` is enabled)

Prior to the GPU propagation fix, secondary ONNX sessions (ArticleProcessor) would silently
default to device 0 even when the user specified `--gpu 1`. This was fixed by routing all ONNX
session creation through `EmbeddingFactory.BuildOnnxConfig()`, which reads the user's GPU config
consistently.

## LFU Embedding Cache

All embedding services are wrapped in a `CachingEmbeddingService` using a Least Frequently Used
(LFU) eviction cache. This avoids recomputing embeddings for repeated inputs.

**Configuration**: 8192 entries, ~12 MB memory overhead (8192 × 384 floats × 4 bytes).

**What gets cached**:

- Repeated user queries (interactive `ask` sessions)
- Sentiment and topic anchor phrases (computed at startup, reused every query)
- Entity name embeddings (same entity appears across documents)
- Deduplication comparisons (same text chunks across ingestion runs)
- PRF centroid inputs (top-K document embeddings reused in refinement)

**Measured performance** (RTX A4000, all-MiniLM-L6-v2 quantized):

| Metric | Value |
|--------|-------|
| Cold embed (single) | ~72 ms |
| Hot embed (cache hit) | ~0.02 ms |
| Cache speedup | ~2400x |
| Hit rate (typical session) | 40-60% |

The cache is session-scoped (in-memory, not persisted). Stats are shown in interactive mode
after the first conversation turn.

## Batch Embedding

All bulk embedding operations use `EmbedBatchAsync`. On CPU, this runs a single ONNX forward pass
for N items. On GPU (DirectML/CUDA), items are processed sequentially (one per forward pass, still
GPU-accelerated) due to the batch dimension constraint described above. This applies to:

- **Ingestion**: All document chunks embedded in one batch call
- **Anchor computation**: Sentiment + topic anchors computed in one call at startup
- **Synthesis re-ranking**: Evidence items re-ranked in one batch call
- **TextRank**: Sentence embeddings for extraction computed in one batch call

**Measured throughput** (RTX A4000, all-MiniLM-L6-v2 quantized):

| Batch Size | Total (ms) | Per-Item (ms) | Items/sec |
|------------|------------|---------------|-----------|
| 1 | 72 | 72 | 14 |
| 8 | 465 | 58 | 17 |
| 32 | 2,178 | 68 | 15 |
| 64 | 4,259 | 67 | 15 |

Batch sizes 8-32 show the best throughput-to-latency ratio. The pipeline automatically batches
chunks per document during ingestion.

## Pre-Embedding Deduplication

Before spending compute on embeddings, a cheap text-signal filter eliminates obvious duplicates.
This saves 20-50% of embedding compute on repetitive documents (books with recurring phrases,
technical docs with boilerplate).

### How It Works

Each pair of chunks is scored using four fast signals:

| Signal | Weight | Method |
|--------|--------|--------|
| **Word Jaccard** | 0.50 | Intersection/union of word sets (bag-of-words) |
| **Trigram Jaccard** | 0.30 | Intersection/union of character trigram sets |
| **Length similarity** | 0.10 | `1.0 - |len_a - len_b| / max(len_a, len_b)` |
| **Heading overlap** | 0.10 | Shared heading/title text between chunks |

Pairs scoring above the threshold (default 0.80) are pre-disposed - the lower-salience chunk is
discarded before embedding. This is O(N) per chunk (fingerprint comparison), compared to O(N×model)
for embedding.

### Configuration

```yaml
ingestion:
  pre_dedup:
    word_jaccard: 0.50
    trigram: 0.30
    length: 0.10
    heading: 0.10
    threshold: 0.80
```

Set all weights to 0 to disable pre-dedup entirely. To let more chunks through (for "resampling"),
lower the weights or raise the threshold.

## Semantic Deduplication

After embedding, a second deduplication pass uses cosine similarity on the actual embedding vectors
to catch semantic near-duplicates that the cheap text filter missed (paraphrases, reworded content).

### Algorithm

```
1. Sort chunks by salience score (highest first - these become canonical)
2. For each chunk (highest salience first):
   a. Skip if already absorbed by a higher-salience chunk
   b. Compute cosine similarity against all remaining chunks
   c. Chunks with similarity >= threshold → mark as near-duplicates
   d. Absorb duplicates: boost this chunk's salience (logarithmic decay)
3. Apply adaptive limits (min/max survivors based on document type)
4. Return surviving chunks for indexing
```

### Adaptive Chunk Limits

After dedup, the number of surviving chunks is bounded by document type and size:

| Document Type | Min Survivors | Max Survivors | Dedup Threshold |
|--------------|---------------|---------------|-----------------|
| Fiction (short, <50p) | 15 | 40 | 0.92 |
| Fiction (novel, 50-300p) | 30 | 120 | 0.88 |
| Fiction (epic, 300+p) | 50 | 200 | 0.85 |
| Technical (<50p) | 15 | 80 | 0.92 |
| Technical (large, 50+p) | 40 | 150 | 0.88 |
| Academic | 15 | 80 | 0.90 |
| Non-fiction | 20 | 120 | 0.88 |

Min survivors prevent over-deduplication. Max survivors cap storage for massive documents.

### Configuration

```yaml
ingestion:
  deduplication_enabled: true
  deduplication_threshold: 0.90
  salience_boost_enabled: true
  max_chunks_override: 0      # 0 = use adaptive defaults
  min_chunks_override: 0      # 0 = use adaptive defaults
```

### Salience Boosting

When a chunk absorbs N near-duplicates, its salience score is boosted:

```
boost = 0.15 × log₂(1 + N)
```

This rewards chunks that represent commonly-repeated content - they're more likely to be central
themes. The logarithmic decay prevents any single chunk from dominating.

## Document Concentration Detection

During retrieval, when results concentrate on a single document (≥40% of top-K from one source
with average relevance ≥0.6), the pipeline automatically expands retrieval from that document.

This is useful for focused queries like "Who is Mr. Darcy?" when Pride and Prejudice is in the KB
alongside many other documents. Without expansion, the flat top-K might undersample the most
relevant source.

### Configuration

```yaml
expansion:
  concentration_threshold: 0.4    # fraction of top-K from one source
  min_relevance: 0.6              # minimum average relevance
  expansion_count: 8              # extra chunks to pull
  deferred_embedding: true        # embed low-salience chunks on demand
```

## Heuristic Salience Scoring

Before embedding, each chunk is scored for likely importance using cheap heuristic signals:

| Signal | Effect | Weight |
|--------|--------|--------|
| Heading presence | `#`-prefixed lines boost score | +0.20 |
| Length sweet spot | 800-2000 chars is ideal | +0.10 |
| Too short | < 200 chars penalized | -0.15 |
| Position bonus | First/last chunks of document | +0.10 |
| Code blocks | Technical docs with ``` blocks | +0.15 |
| Dialogue | Fiction with quoted speech | +0.15 |

The resulting score (0.0-1.0) is used for:
- Pre-dedup tiebreaking (lower-salience chunk is discarded)
- Semantic dedup ordering (higher-salience chunks are canonical)
- Retrieval scoring (salience contributes 0.25 weight in evidence assignment)

## Complete Ingestion Pipeline

Putting it all together, the full ingestion flow for local files:

```
1. Extract text (PDF/DOCX/Markdown/HTML/TXT handler)
2. Detect document type (Fiction, Technical, Academic, NonFiction)
3. Chunk by structure (headings, pages, paragraphs)
4. Score heuristic salience per chunk
5. Pre-dedup: cheap text-signal filter (word Jaccard + trigrams + length + heading)
6. Batch embed surviving chunks (single ONNX forward pass)
7. Semantic dedup: cosine similarity filter with salience boosting
8. Apply adaptive chunk limits (min/max by doc type)
9. Score sentiment and topic via anchor embeddings
10. Batch index survivors (SQLite + FTS5 + DuckDB HNSW + Lucene)
11. NER entity extraction (optional, with --entities)
```

Progress output shows dedup stats:

```
[cyan]Pre-dedup: 247 → 198 chunks (20% reduction)[/]
[cyan]Computing embeddings for 198 chunks[/]
[cyan]Deduplicating: 198 → 142 chunks (28% reduction, Fiction)[/]
[cyan]Indexing 142 chunks[/]
[green]Ingested 142 segments from 1 file(s) (Fiction), 47 entities[/]
```

## Running Benchmarks

Integration-level benchmarks are included in the test suite:

```bash
# Compare all models (quantized + FP32) - single/batch latency, dimensions
dotnet test src/DoomSummarizer.Tests -c Release --filter "FullyQualifiedName~EmbeddingModelBenchmarks.CompareAllModels"

# Default model batch throughput at various batch sizes
dotnet test src/DoomSummarizer.Tests -c Release --filter "FullyQualifiedName~DefaultModel_BatchThroughput"

# LFU cache hit rate and speedup measurement
dotnet test src/DoomSummarizer.Tests -c Release --filter "FullyQualifiedName~CachingService_HitRateAndSpeedup"
```

These tests have the `[Trait("Category", "Integration")]` attribute and use real ONNX inference.
They skip gracefully if models aren't downloaded.

## Key Source Files

| File | Purpose |
|------|---------|
| `DoomSummarizer.Core/Services/EmbeddingFactory.cs` | Model resolution, GPU config, ONNX session creation, cache wrapping |
| `DoomSummarizer.Core/Services/CachingEmbeddingService.cs` | LFU embedding cache (8192 entries) |
| `DoomSummarizer.Core/Models/DoomConfig.cs` | `EmbeddingConfig`, `IngestionConfig`, `ExpansionConfig` records |
| `DoomSummarizer/Commands/ScrollCommand.Ingest.cs` | Full ingestion pipeline with pre-dedup + semantic dedup |
| `Mostlylucid.DocSummarizer.Core/Services/Onnx/OnnxEmbeddingService.cs` | ONNX Runtime session management |
| `Mostlylucid.DocSummarizer.Core/Services/Onnx/OnnxModelRegistry.cs` | Model metadata registry (dimensions, URLs, seq lengths) |
| `DoomSummarizer.Tests/EmbeddingFactoryTests.cs` | ParseModel unit tests (17 cases) |
| `DoomSummarizer.Tests/Benchmarks/EmbeddingModelBenchmarks.cs` | Integration benchmarks |
| `DoomSummarizer.Tests/IngestionDedupTests.cs` | Pre-dedup + semantic dedup tests |
