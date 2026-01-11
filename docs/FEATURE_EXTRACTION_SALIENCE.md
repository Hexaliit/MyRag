# Feature Extraction & Salience System

LucidRAG implements a multi-signal salience pipeline that dramatically reduces storage while maintaining retrieval quality. This document covers the architecture, algorithms, and tuning parameters.

## Overview

The salience system operates at three levels:

1. **Segment-level Salience** - Per-segment scoring during ingestion
2. **Collection-level Salient Terms** - TF-IDF + Entity RRF for autocomplete
3. **Search-time RRF Fusion** - Dense + BM25 + Salience ranking

```
Document → Chunker → SegmentSelector → Evidence Store
                ↓           ↓
           Embeddings   Salient Terms
                ↓           ↓
            Qdrant     PostgreSQL FTS
                    ↘   ↙
                  RRF Fusion
                      ↓
                  LLM Synthesis
```

## 1. Segment Selection (`SegmentSelector`)

**Location:** `src/LucidRAG.Core/Services/SegmentSelector.cs`

Reduces segment storage by 70-90% while preserving coverage through three-stage filtering:

### Stage 1: Salience Threshold

```csharp
var effectiveThreshold = maxSalience > 0 ? _salienceThreshold : 0;
var salientSegments = segments
    .Where(s => s.SalienceScore >= effectiveThreshold)
    .OrderByDescending(s => s.SalienceScore)
    .ToList();
```

- Default threshold: `0.05` (captures important content)
- Adaptive: if all segments have 0 salience (plain text), threshold becomes 0
- Minimum coverage: at least 20% or 50 segments, whichever is larger

### Stage 2: Semantic Deduplication

```csharp
foreach (var existingEmbedding in selectedEmbeddings)
{
    var similarity = CosineSimilarity(segment.Embedding, existingEmbedding);
    if (similarity >= _similarityThreshold)
    {
        isTooSimilar = true;
        break;
    }
}
```

- Default threshold: `0.80` cosine similarity
- Greedy selection: keeps highest-salience segment from each cluster
- **Fallback sampling**: if dedup is too aggressive, evenly samples from document

### Stage 3: Coverage Selection

Prioritizes segments with unique features:

```csharp
var features = ExtractEntities(segment);
// Features include:
// - section:{SectionTitle}
// - heading:{HeadingPath}
// - type:{SegmentType}
// - page:{PageNumber}
```

Ensures coverage of:
- Different sections/headings
- Different content types (text, table, code)
- Different pages (for PDFs)

### Configuration

```csharp
var selector = new SegmentSelector(
    salienceThreshold: 0.05,      // Min salience to consider
    similarityThreshold: 0.80,    // Max similarity before dedup
    maxSegmentsPerDocument: 250   // Hard cap
);
```

### Typical Results

| Document Type | Original Segments | After Selection | Reduction |
|---------------|-------------------|-----------------|-----------|
| Novel (pg174.txt) | 372 | 50 | 87% |
| Technical PDF | 1200 | 180 | 85% |
| Short blog post | 25 | 25 | 0% |

## 2. Salient Terms Service

**Location:** `src/LucidRAG.Core/Services/SalientTermsService.cs`

Extracts collection-wide important terms for autocomplete and query expansion.

### Three Ranking Signals

#### Signal 1: TF-IDF Terms
```csharp
var idf = Math.Log((double)totalDocs / df);
var avgTf = termDocIds.Average(docId => count / maxTf); // Normalized TF
var tfidf = avgTf * idf;
```

- Extracts unigrams, bigrams, trigrams
- Filters stop words
- Length constraints: 3-50 characters

#### Signal 2: Entity Terms
```csharp
var entities = await db.DocumentEntityLinks
    .Where(del => del.Document.CollectionId == collectionId)
    .GroupBy(e => e.CanonicalName.ToLower())
    .Select(g => new { Term = g.First().CanonicalName, Count = g.Count() })
```

- Uses GraphRAG extracted entities
- Canonical names normalized
- Scored by document frequency

#### Signal 3: Query Terms (Future)
- Track popular search queries
- Boost terms users frequently search for

### RRF Combination

```csharp
foreach (var ranking in rankings)
{
    if (ranking.TryGetValue(term, out var termScore))
    {
        var rank = FindRank(termScore);
        rrfScore += 1.0 / (60.0 + rank + 1);
    }
}
```

Formula: `RRF(term) = Σ 1/(k + rank_i)` where k=60

### Entity Schema

```csharp
public class CollectionSalientTerm
{
    public Guid CollectionId { get; set; }
    public string Term { get; set; }           // "machine learning"
    public string NormalizedTerm { get; set; } // "machine learning"
    public double Score { get; set; }          // RRF score
    public string Source { get; set; }         // "tfidf"|"entity"|"combined"
    public int DocumentFrequency { get; set; } // # docs containing term
}
```

### Background Updates

```csharp
// src/LucidRAG.Core/Services/Background/SalientTermsUpdaterService.cs
// Runs periodically to refresh collection terms
services.AddHostedService<SalientTermsUpdaterService>();
```

## 3. Search-Time RRF Fusion

**Location:** `src/LucidRAG.Core/Services/AgenticSearchService.cs`

Combines four signals at search time:

### Four-Way RRF

```csharp
// Dense embedding similarity
rrfScores[id] = denseWeight * (1.0 / (rrfK + denseRank + 1));

// BM25 lexical match
rrfScores[id] += bm25Weight * (1.0 / (rrfK + bm25Rank + 1));

// Segment salience
rrfScores[id] += salienceWeight * (1.0 / (rrfK + salienceRank + 1));

// Document freshness
rrfScores[id] += freshnessWeight * (1.0 / (rrfK + freshnessRank + 1));
```

### Default Weights (Lens-Configurable)

| Signal | Default Weight | Technical Docs | Blog Posts |
|--------|---------------|----------------|------------|
| Dense | 0.35 | 0.25 | 0.40 |
| BM25 | 0.30 | 0.25 | 0.25 |
| Salience | 0.25 | 0.35 | 0.20 |
| Freshness | 0.10 | 0.15 | 0.15 |

### PostgreSQL Hybrid Query

```sql
-- Three-way RRF in PostgreSQL (PostgresBM25Service.HybridSearchAsync)
WITH dense_ranks AS (
    SELECT "Id", ROW_NUMBER() OVER (ORDER BY embedding <=> $1::vector) as rank
    FROM evidence_artifacts WHERE embedding IS NOT NULL
),
bm25_ranks AS (
    SELECT "Id", ROW_NUMBER() OVER (
        ORDER BY ts_rank_cd(content_tokens, websearch_to_tsquery('english', $2), 32) DESC
    ) as rank
    FROM evidence_artifacts WHERE content_tokens @@ websearch_to_tsquery('english', $2)
),
salience_ranks AS (
    SELECT "Id", ROW_NUMBER() OVER (
        ORDER BY (metadata->>'salience_score')::float DESC NULLS LAST
    ) as rank
    FROM evidence_artifacts WHERE metadata ? 'salience_score'
)
SELECT ea."Id",
    (1.0 / ($3 + COALESCE(d.rank, 1000)) +
     1.0 / ($3 + COALESCE(b.rank, 1000)) +
     1.0 / ($3 + COALESCE(s.rank, 1000))) as rrf_score
FROM evidence_artifacts ea
LEFT JOIN dense_ranks d ON ea."Id" = d."Id"
LEFT JOIN bm25_ranks b ON ea."Id" = b."Id"
LEFT JOIN salience_ranks s ON ea."Id" = s."Id"
ORDER BY rrf_score DESC LIMIT $4
```

## 4. Evidence Storage Architecture

### Two-Tier Storage

```
Qdrant (Vector Store)          PostgreSQL (Evidence)
┌─────────────────────┐        ┌─────────────────────┐
│ Selected Segments   │───────▶│ EvidenceArtifacts   │
│ • Embeddings        │  hash  │ • Full Text         │
│ • Segment Hash      │  match │ • Metadata          │
│ • Basic Metadata    │        │ • content_tokens    │
└─────────────────────┘        └─────────────────────┘
        50 points                    50 rows
```

### Hash-Based Hydration

```csharp
// DocumentQueueProcessor applies selection BEFORE indexing
var selectedSegments = selector.SelectForEvidence(segments);

// Index only selected segments
await vectorStore.UpsertSegmentsAsync("ragdocs", selectedSegments, ct);

// Store text in evidence (same segments)
foreach (var segment in selectedSegments)
{
    await evidenceRepository.StoreAsync(entityId, "segment_text", ...);
}
```

### Query-Time Text Retrieval

```csharp
// AgenticSearchService hydrates from evidence
var contentHashes = segments.Select(s => s.ContentHash);
var texts = await evidenceRepository.GetSegmentTextsByHashesAsync(contentHashes, ct);

foreach (var segment in segments)
{
    if (texts.TryGetValue(segment.ContentHash, out var text))
        segment.Text = text;
}
```

## 5. Lens Configuration

Lenses allow per-collection tuning of salience weights:

```yaml
# manifests/lenses/technical.lens.yaml
scoring:
  dense_weight: 0.25
  bm25_weight: 0.25
  salience_weight: 0.35      # Higher for technical terms
  freshness_weight: 0.15

defaults:
  features:
    enable_entity_boost: true  # Boost API names, class names
```

## 6. Performance Characteristics

### Storage Savings

| Metric | Without SegmentSelector | With SegmentSelector |
|--------|------------------------|---------------------|
| Qdrant points/doc | 372 | 50 |
| Evidence rows/doc | 372 | 50 |
| Storage per 1000 docs | ~10 GB | ~1.4 GB |

### Query Performance

| Operation | Time |
|-----------|------|
| Embedding generation | 50-100ms |
| Qdrant vector search | 5-15ms |
| PostgreSQL FTS | 5-20ms |
| RRF fusion | <1ms |
| Evidence hydration | 10-30ms |
| **Total search** | ~100-150ms |

## 7. Future Enhancements

### Planned

1. **Feature Embeddings in pgvector**
   - Store semantic embeddings for extracted features
   - Enable "yellow → gold" similarity queries

2. **Intra-Document Segment Graphs**
   - Connect segments within a document
   - Use connectivity for additional salience signal

3. **International Date Parsing**
   - Robust datetime extraction from features
   - Locality pattern detection

### Under Consideration

1. **Query-Term Learning**
   - Track search patterns per collection
   - Boost frequently-searched terms

2. **Adaptive Deduplication Thresholds**
   - Tune threshold based on document type
   - Literary text needs lower threshold than technical docs

## 8. Debugging & Monitoring

### Logs to Watch

```
[SegmentSelector] Step 1: After salience filter: 150 (min=50, maxSalience=0.82)
[SegmentSelector] Step 2: After deduplication: 48
[SegmentSelector] Step 2.5: Deduplication too aggressive, adding evenly-sampled segments
[SegmentSelector] Final: 50/372 segments

PostgreSQL FTS query completed in 12ms: 'portrait' returned 10 results
Hydrated 50/50 segments with text from evidence
```

### Health Checks

```bash
# Check Qdrant collection size
curl http://localhost:6333/collections/ragdocs

# Check evidence count
SELECT COUNT(*) FROM evidence_artifacts WHERE artifact_type = 'segment_text';

# Check salient terms
SELECT term, score, source FROM salient_terms
WHERE collection_id = '...' ORDER BY score DESC LIMIT 20;
```

---

*Last updated: 2026-01-11*
*Part of LucidRAG v2 architecture*
