# Spec: Ingestion-Time Semantic Deduplication

## Problem

When ingesting large documents (books, technical manuals, research papers), the current pipeline embeds and indexes every chunk equally. A 500-page novel produces ~200 chunks, many of which are near-duplicates (repeated themes, transitional paragraphs, boilerplate). This wastes:

- **HNSW index space**: Near-duplicate chunks pollute the vector index, reducing search precision
- **Retrieval quality**: The LLM receives redundant evidence, diluting signal-to-noise
- **Storage**: SQLite + FTS5 + DuckDB all store what is effectively duplicate content

## Solution: Embed All, Dedup Before Indexing

Replace the partial embedding approach with a full-embed + semantic dedup pipeline:

```
Chunk → Embed ALL → Semantic Dedup (per-document) → Index survivors → HNSW + FTS5 + Lucene
```

### Key Principles

1. **Embed everything** - All chunks get embeddings (needed for dedup comparison)
2. **Dedup before indexing** - Near-duplicates are merged before storage, not after
3. **Salience boosting** - Surviving chunks absorb duplicates as a popularity signal
4. **Adaptive limits** - Smart min/max chunk targets based on document type and length
5. **Savings at retrieval** - Fewer, better chunks = faster search, less noise for the LLM

## Design

### Phase 1: Embed All Chunks

After chunking, embed all chunks in batch (unchanged from current):

```
chunks → EmbedBatchAsync(texts) → all chunks have embeddings
```

### Phase 2: Per-Document Semantic Dedup

For each document's chunk set, find and merge near-duplicates:

```
Input: N embedded chunks from one document
Output: M surviving chunks (M <= N), each with salience boost from absorbed duplicates

Algorithm:
1. Sort chunks by salience score (descending) - high-salience chunks are canonical
2. For each chunk (highest salience first):
   a. If already absorbed by another chunk → skip
   b. Compute cosine similarity against all remaining unprocessed chunks
   c. Chunks with similarity >= threshold (0.90) → mark as near-duplicates
   d. Absorb near-duplicates: boost this chunk's salience, increment merge count
3. Return surviving (non-absorbed) chunks
```

**Reuses existing infrastructure**: `DeduplicationService` in `Mostlylucid.DocSummarizer.Core` already implements this algorithm with salience boosting and configurable decay modes (linear/logarithmic). The CLI ingestion path needs to adapt its `ContentItem` chunks to `Segment` format for the dedup service, or implement the same algorithm directly over `ContentItem` + embeddings.

### Phase 3: Adaptive Chunk Limits

After dedup, apply adaptive min/max limits based on document type and length:

| Document Type | Raw Chunk Count | Min Survivors | Max Survivors | Dedup Threshold |
|--------------|----------------|---------------|---------------|-----------------|
| Fiction (short, <50 pages) | 20-40 | 15 | 40 | 0.92 (conservative) |
| Fiction (novel, 50-300 pages) | 40-200 | 30 | 120 | 0.88 |
| Fiction (epic, 300+ pages) | 200-1000 | 50 | 200 | 0.85 (aggressive) |
| Technical (<50 pages) | 20-100 | 15 | 80 | 0.92 |
| Technical (large, 50+ pages) | 100-500 | 40 | 150 | 0.88 |
| Academic | 20-100 | 15 | 80 | 0.90 |
| Non-fiction | 20-200 | 20 | 120 | 0.88 |

**Min survivors** prevents over-deduplication (don't reduce a 200-page novel to 10 chunks).
**Max survivors** caps storage for massive documents (Shakespeare's Complete Works shouldn't produce 2000 chunks).

When dedup produces fewer than min: keep all (no further reduction).
When dedup produces more than max: keep top-N by salience score.

```csharp
internal static (int min, int max, float threshold) GetAdaptiveLimits(
    int rawChunkCount, IngestDocumentType docType)
{
    return docType switch
    {
        IngestDocumentType.Fiction when rawChunkCount > 200 =>
            (50, 200, 0.85f),
        IngestDocumentType.Fiction when rawChunkCount > 40 =>
            (30, 120, 0.88f),
        IngestDocumentType.Fiction =>
            (15, 40, 0.92f),

        IngestDocumentType.Technical when rawChunkCount > 100 =>
            (40, 150, 0.88f),
        IngestDocumentType.Technical =>
            (15, 80, 0.92f),

        IngestDocumentType.Academic =>
            (15, 80, 0.90f),

        IngestDocumentType.NonFiction when rawChunkCount > 40 =>
            (20, 120, 0.88f),

        // Default / Unknown
        _ => (15, Math.Max(rawChunkCount, 80), 0.90f)
    };
}
```

### Phase 4: Index Survivors

Only surviving chunks get indexed in SQLite + FTS5 + DuckDB HNSW + Lucene. Absorbed chunks are discarded entirely (not stored).

Each surviving chunk carries:
- `SalienceScore`: original heuristic score + boost from absorbed duplicates
- `MergeCount`: number of near-duplicates absorbed (diagnostics)
- `Embedding`: pre-computed (all chunks were embedded in Phase 1)

## Changes to Current Implementation

### Remove

- `IsEmbedded` field on `ContentItem` - all chunks will be embedded
- `PartialEmbedding` config flag - replaced by dedup config
- High/low salience split in `IngestLocalFilesAsync` - embed all, dedup after
- `GetUnembeddedItemsBySourceAsync` in `StorageService.Items.cs` - no deferred embedding
- On-demand embedding in `ExpandDocumentAsync` - all chunks already embedded

### Keep

- `SalienceScore` on `ContentItem` - still used for tiebreaking and boost tracking
- `EstimateChunkSalience` - still scores chunks for dedup tiebreaking
- `salience_score` column in SQLite - stores final boosted score
- `ExpansionConfig` - document concentration detection still works (all chunks are embedded)
- `TryExpandConcentratedDocumentAsync` - expansion is now instant (no embedding needed)

### Add/Modify

| File | Change |
|------|--------|
| `ScrollCommand.Ingest.cs` | Replace high/low split with: embed all → dedup → adaptive limits → index survivors |
| `ScrollCommand.Ingest.cs` | Add `GetAdaptiveLimits()` method |
| `ScrollCommand.Ingest.cs` | Add `DeduplicateChunks()` method (cosine dedup with salience boosting) |
| `DoomConfig.cs` | Replace `IngestionConfig.PartialEmbedding` with dedup settings |
| `DoomConfig.cs` | Add adaptive limit overrides to `IngestionConfig` |
| `default-config.yaml` | Update ingestion section with dedup settings |
| `ContentItem.cs` | Remove `IsEmbedded`, keep `SalienceScore` |
| `StorageService.cs` | Remove `is_embedded` column migration (or keep for backward compat, just unused) |
| `StorageService.Items.cs` | Remove `GetUnembeddedItemsBySourceAsync` |
| `RetrievalPipeline.cs` | Simplify `ExpandDocumentAsync` - no on-demand embedding (all already embedded) |
| `DuckDbVectorStore.cs` | Remove null-embedding guard (all items will have embeddings) |

### `DoomConfig.cs` Updated Config

```csharp
public record IngestionConfig
{
    /// Enable semantic dedup during ingestion (default: true)
    public bool DeduplicationEnabled { get; init; } = true;

    /// Cosine similarity threshold for near-duplicate detection.
    /// Adaptive limits may override this per document type.
    public float DeduplicationThreshold { get; init; } = 0.90f;

    /// Enable salience boosting for near-duplicate absorption
    public bool SalienceBoostEnabled { get; init; } = true;

    /// Override max chunk survivors (0 = use adaptive default)
    public int MaxChunksOverride { get; init; } = 0;

    /// Override min chunk survivors (0 = use adaptive default)
    public int MinChunksOverride { get; init; } = 0;
}
```

### `default-config.yaml` Updated

```yaml
ingestion:
  deduplication_enabled: true
  deduplication_threshold: 0.90
  salience_boost_enabled: true
  max_chunks_override: 0      # 0 = adaptive
  min_chunks_override: 0      # 0 = adaptive
```

## Algorithm: DeduplicateChunks

```csharp
internal static List<ContentItem> DeduplicateChunks(
    List<ContentItem> items,
    float threshold,
    int minSurvivors,
    int maxSurvivors)
{
    if (items.Count <= minSurvivors)
        return items; // Already under minimum - keep all

    // Sort by salience (highest first = canonical chunks)
    var sorted = items
        .OrderByDescending(i => i.SalienceScore ?? 0f)
        .ToList();

    var absorbed = new HashSet<string>();
    var survivors = new List<ContentItem>();

    foreach (var item in sorted)
    {
        if (absorbed.Contains(item.Id)) continue;

        var mergeCount = 0;

        // Check remaining items for near-duplicates
        foreach (var candidate in sorted)
        {
            if (candidate.Id == item.Id || absorbed.Contains(candidate.Id))
                continue;

            if (item.Embedding == null || candidate.Embedding == null)
                continue;

            var sim = VectorMath.CosineSimilarity(item.Embedding, candidate.Embedding);
            if (sim >= threshold)
            {
                absorbed.Add(candidate.Id);
                mergeCount++;
            }
        }

        // Boost salience by absorbed count (logarithmic decay)
        if (mergeCount > 0)
        {
            var boost = 0.15f * (float)Math.Log2(1 + mergeCount);
            item.SalienceScore = Math.Clamp(
                (item.SalienceScore ?? 0.3f) + boost, 0f, 1f);
        }

        survivors.Add(item);
    }

    // Apply max limit: if still over max, keep top-N by salience
    if (survivors.Count > maxSurvivors)
        survivors = survivors
            .OrderByDescending(i => i.SalienceScore ?? 0f)
            .Take(maxSurvivors)
            .ToList();

    return survivors;
}
```

**Complexity**: O(N^2) pairwise comparisons where N = chunks per document. For a 500-page novel with ~200 chunks, this is 40,000 comparisons - negligible compared to the embedding cost. For Shakespeare's Complete Works (~1000 chunks), it's 1M comparisons (~50ms total for cosine similarity on 384-dim vectors).

## Modified Ingestion Flow

```
IngestLocalFilesAsync:
1. For each file:
   a. Extract text via document handler
   b. Detect document type (fiction, technical, academic, etc.)
   c. Chunk by structure (headings, pages, paragraphs)
   d. Score each chunk's heuristic salience (EstimateChunkSalience)
   e. Collect (item, embedText) pairs

2. Batch embed ALL chunks (single EmbedBatchAsync call)

3. Per-document semantic dedup:
   a. Group chunks by source file
   b. For each file's chunk set:
      - Get adaptive limits: (min, max, threshold) from doc type + chunk count
      - DeduplicateChunks(chunks, threshold, min, max)
   c. Merge all surviving chunks

4. Score sentiment and topic for survivors

5. Batch index survivors (SQLite + FTS5 + DuckDB HNSW + Lucene)

6. NER entity extraction on survivors
```

## Progress Reporting

Update progress messages to show dedup stats:

```
[cyan]Computing embeddings for 247 chunks[/]
[cyan]Deduplicating: 247 → 142 chunks (42% reduction, Fiction)[/]
[cyan]Indexing 142 chunks[/]
[green]Ingested 142 segments from 1 file(s) (Fiction), 47 entities, 105 deduped[/]
```

## Testing

### Unit Tests

1. `DeduplicateChunks_RemovesNearDuplicates` - two chunks with sim >= 0.90 → one survives
2. `DeduplicateChunks_RespectsMinSurvivors` - don't reduce below minimum
3. `DeduplicateChunks_RespectsMaxSurvivors` - cap at maximum
4. `DeduplicateChunks_BoostsSalience` - survivor gets logarithmic boost
5. `DeduplicateChunks_KeepsHighSalience` - highest-salience chunk is the survivor
6. `GetAdaptiveLimits_FictionNovel` - returns (30, 120, 0.88) for 100-chunk novel
7. `GetAdaptiveLimits_TechnicalSmall` - returns (15, 80, 0.92) for 50-chunk tech doc

### Integration Test

1. Ingest `prideandprejudice_janeausten_6x9_spaced.pdf` from sample data
2. Verify chunk count is reduced (expect 40-60% reduction for fiction)
3. Query "Who is Mr. Darcy?" - verify relevant chunks are returned
4. Query "What is Pride and Prejudice about?" - verify summary-level chunks survive dedup

## Relationship to Existing Dedup Infrastructure

The `DeduplicationService` in `Mostlylucid.DocSummarizer.Core` operates on `Segment` objects (web app pipeline). The CLI ingestion operates on `ContentItem` objects. Rather than creating a DI dependency, the CLI implements the same algorithm directly over `ContentItem` + embeddings. The algorithm is identical:

1. Sort by salience (descending)
2. Pairwise cosine comparison
3. Absorb near-duplicates (threshold >= 0.90)
4. Logarithmic salience boost for survivors
5. Cap at max survivors

The config structure (`IngestionConfig`) mirrors `IngestionDeduplicationConfig` but is self-contained in `DoomConfig` to avoid cross-project dependency.

## Backward Compatibility

- The `is_embedded` SQLite column remains (migration is idempotent) but is unused - all items are embedded
- The `PartialEmbedding` config key is ignored (dedup replaces it)
- Existing indexed collections are unaffected (dedup only runs at ingestion time)
- `--force` re-ingestion applies dedup to the full document
