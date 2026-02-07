# Architecture (high level)

This is a "local-first RAG-ish" console app with two ingestion paths:

**Web sources** (scroll, page, crawl with URL):

1. **Fetch** content from sources (RSS, HTML scraping, and optional API providers)
2. **Normalize** content into `ContentItem` records

**Local files** (scroll with path, crawl with path):

1. **Ingest** documents (PDF, DOCX, Markdown, HTML, TXT, PPTX) via handler registry + processor plugins
2. **Detect** document type (Fiction, NonFiction, Academic, Technical) via heuristic scoring
3. **Chunk** content adaptively - books use 5000-char chunks for narrative continuity; technical docs use 2000-char
   chunks. PDFs chunked by page markers, text by headings/paragraphs.

Both paths then converge:

4. **Embed** items using ONNX all-MiniLM-L6-v2 (384-dim). Batch embedding: single forward pass for all items, not
   sequential calls.
5. **Score** sentiment and infer topic using pre-computed anchor embeddings (batch-computed at startup)
6. **Rank** items using a multi-signal pipeline (Lucene BM25F + embeddings + freshness + authority + diversity)
7. **Enrich** items (optional link following, TextRank excerpts, entity extraction)
8. **Store** items, embeddings, and metadata locally (SQLite + Lucene.NET index). Batch indexing: single SQLite
   transaction + Lucene commit for all items.
9. **Synthesize** outputs using local or cloud LLMs (budgeted + fallback)

## Data stores

SQLite (`$HOME/.doomsummarizer/doom.db`):

- Items, embeddings, query log, URL cache, trends, entity tables, and disambiguation caches
- FTS5 index (legacy, lightweight backup for KB enrichment and pre-filtering)

Lucene.NET (`$HOME/.doomsummarizer/lucene/<collection>/`):

- Per-collection Apache Lucene index for full-text search
- BM25F field weighting: title (2x), keywords (2.5x), content (1x)
- Porter stemming, fuzzy matching, phrase boosting
- LLM-generated query optimization (sentinel converts natural language to Lucene syntax)

DuckDB (`$HOME/.doomsummarizer/vectors.duckdb`, when `--graph` is enabled):

- HNSW vector indexes for item/entity similarity search
- Entity graph (mentions + co-occurrence edges)

## LLM providers and fallback

When an LLM call is needed, DoomSummarizer routes requests through `LlmRouter`:

- Cloud providers (OpenAI/Anthropic) if configured and within budget
- Always falls back to local Ollama if available
- If no LLM is available, DoomSummarizer still runs ranking/enrichment and can emit evidence-only outputs

## Query preprocessing

Before ranking, queries are analyzed and optionally decomposed:

1. **Sentinel Analysis** - LLM classifies intent, extracts keywords, detects temporal requirements
2. **NER Extraction** - ONNX-based entity recognition (PER, ORG, LOC, MISC)
3. **Composite Detection** - Multi-part questions ("X and Y?") decomposed into subqueries
4. **Temporal Parsing** - Microsoft.Recognizers.Text confirms date/time expressions

For composite queries, each subquery gets its own embedding vector. Items are scored using **max similarity** across all
subqueries (not averaged), ensuring articles matching ANY part of the question rank highly.

## Retrieval pipeline

The retrieval pipeline uses three concurrent search layers fused with Reciprocal Rank Fusion (RRF). Lucene and HNSW
retrieval run in parallel via `Task.WhenAll` for lower latency.

### Layer 1: Lucene.NET full-text search

Why Lucene.NET instead of SQLite FTS5? Lucene provides BM25F field weighting (title 2x, keywords 2.5x, content 1x),
Porter stemming ("running" matches "run"), fuzzy matching for typos, phrase boosting, and boolean query composition. The
sentinel LLM converts natural language queries into optimized Lucene syntax.

### Layer 2: Embedding HNSW similarity

384-dim all-MiniLM-L6-v2 embeddings with cosine similarity. For composite queries, items are scored against the
best-matching subquery (max-sim, not average).

### Layer 3: Entity profile HNSW (optional)

When `--entities` is enabled, documents get TF-IDF-confidence-weighted entity profile embeddings. HNSW search on entity
profiles finds related documents in O(log N).

### Ranking pipeline (as implemented in `scroll`)

At a glance:

- Deduplicate items by URL/title
- **Phase 1**: fast scoring + discard low-salience tail (Lucene BM25F + freshness + authority + similarity)
- Compute embeddings for remaining items
- **Phase 2**: full RRF with query similarity + vibe alignment (max-sim for composite queries)
- Apply source weights, LFU diversity decay, and in-corpus link authority boosts
- Entity profile HNSW similarity (when `--entities` enabled)
- Optionally follow one-hop links to enrich content
- Extract key sentences via TextRank (no LLM required)

Use `doomsummarizer scroll --debug` to see the scoring stages and discards on your machine/data.

## Synthesis pipeline (`SynthesizeSummaryAsync`)

Both `scroll` and `ask` use the same synthesis engine (`OllamaService.SynthesizeSummaryAsync`). This is the unified
answer generation path - `ask` mode no longer has a separate, degraded generation method.

Key design:

1. **Smart evidence budgeting** - each evidence item gets a character budget proportional to its relevance. Short items
   that don't use their full budget donate surplus to longer items.
2. **TextRank compression** - long evidence items are compressed using PageRank-style sentence centrality extraction.
   Sentences are embedded (batch ONNX call), a similarity graph is built, and the most central sentences are selected.
   This is deterministic (no LLM needed).
3. **Semantic re-ranking** - evidence items are re-ranked by cosine similarity to the query using batch embedding.
   Re-ranking embeddings use a single batch ONNX call, not sequential per-item calls.
4. **Full content** - the LLM sees full content snippets (not truncated summaries), compressed only when they exceed the
   per-item budget.
5. **Clean prompt** - evidence headers contain only sequential numbering and title. No metadata (topic, relevance
   scores) is leaked to the LLM.

## Performance optimizations

### Batch ONNX embedding

All embedding operations use `EmbedBatchAsync` where possible - a single ONNX forward pass for N items instead of N
sequential calls. This applies to:

- Ingestion: all document chunks embedded in one batch call
- Anchor computation: sentiment + topic anchors computed in one call via `ItemProcessor.CreateAsync`
- Synthesis re-ranking: all evidence items re-ranked in one batch call
- TextRank: all sentence embeddings computed in one batch call

### Batch indexing

`IndexBatchAsync` wraps SaveItemAsync + IndexDocumentFtsAsync + UpdateKeywordCorpusAsync in a single SQLite transaction
for N items, instead of 3 operations x N sequential calls.

### Parallel retrieval

Lucene FTS and embedding HNSW searches run concurrently via `Task.WhenAll` in `RetrievalPipeline`, halving retrieval
latency when both layers are active.

## Long-form generation pipeline (blog templates)

When using `-t blog-article`, `-t blog-timeline`, or any YAML template (e.g. `deep-dive`, `problem-solution`,
`pros-cons`), `scroll` activates a six-phase evidence-grounded pipeline instead of the standard digest synthesis:

```
Phase 1  Evidence Preparation     ArticleProcessor segments → EvidenceCorpus
Phase 2  Document Planning        Sentinel LLM → JSON outline with theme keywords
Phase 3  Evidence Assignment      Embedding similarity (0.60) + salience (0.25) + relevance (0.15)
Phase 4  Section Generation       Main LLM per section, with running summary + entity tracker
Phase 5  Output Validation        URL whitelist, entity fuzzy match, fact grounding
Phase 6  Assembly                 Stitch sections → BlogArticleResult → template render
```

Key design principles:

- **Minimize LLM calls**: Only 2 phases use LLMs (planning + generation). Everything else is deterministic using ONNX
  embeddings and salience scores from the ranking pipeline.
- **Single ONNX model**: Both segment embeddings (from `ArticleProcessor`) and theme embeddings (for
  assignment/validation) use the same `all-MiniLM-L6-v2` model via `OnnxEmbeddingService`.
- **Per-section evidence curation**: Each section gets its own slice of evidence based on theme keyword embedding
  similarity, allowing the document to evolve through different facets without context stuffing.
- **Running summary**: Top-salience segments from completed sections carry context forward (no LLM compression).
  Two-tier: recent sections = full digest, older = first sentence.
- **Entity continuity**: String-matching tracker tells each section which entities to maintain or reintroduce.
- **Drift detection**: Cosine similarity to plan's theme embedding; corrective guidance if < 0.35.
- **Validation**: Every URL verified against fetched evidence whitelist. Hallucinated URLs removed, fabricated titles
  replaced.

Implementation lives in `Services/LongFormGeneration/` and `Models/LongFormGeneration/`.

