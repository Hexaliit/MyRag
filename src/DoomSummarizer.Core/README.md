# Mostlylucid.DoomSummarizer.Core

Core signal extraction, retrieval, and synthesis pipeline for news and content analysis.

## Features

- **Signal Extraction**: Wave-based analysis pipeline (entities, topics, knowledge graphs)
- **LLM Routing**: Budget-enforced, circuit-breaking multi-provider routing (Ollama primary, cloud providers disabled by default)
- **Relevance Scoring**: 6-signal RRF fusion with query-type adaptive ranking
- **Content Extraction**: SmartReader + Markdown analysis for web content
- **Knowledge Graphs**: Entity extraction and relationship mapping
- **Resilience**: Circuit breakers, API budget tracking, adaptive rate limiting
- **Local-First**: ONNX embeddings, DuckDB vector search, Lucene.NET full-text search

## Key Services

### Retrieval Pipeline (`RetrievalPipeline.cs`)

Three-layer retrieval with parallel execution:

1. **Lucene.NET FTS** — BM25F with field weighting (title 2x, keywords 2.5x, content 1x), Porter stemming, fuzzy matching
2. **Embedding HNSW** — 384-dim cosine similarity with max-sim for composite queries
3. **Entity Profile HNSW** — TF-IDF-confidence weighted entity fingerprints

Lucene and HNSW layers execute concurrently via `Task.WhenAll`, with results fused through RRF.

### Synthesis (`OllamaService.SynthesizeSummaryAsync`)

Unified synthesis engine used by both `scroll` and `ask`:

- **Smart evidence budgeting** — per-item character budgets proportional to relevance; short items donate surplus to long ones
- **TextRank compression** — PageRank-style sentence centrality extraction using batch ONNX embeddings (no LLM needed)
- **Semantic re-ranking** — batch cosine similarity against the query for evidence ordering
- **Clean prompts** — evidence headers contain only sequential numbering and title; no metadata leaks to the LLM

### Batch Operations (`ItemProcessor.cs`)

Optimized for throughput:

- **Batch embedding** — single ONNX forward pass for N items via `EmbedBatchAsync`
- **Batch anchor computation** — sentiment + topic anchors computed in one call at startup
- **Batch indexing** — `IndexBatchAsync` wraps save + FTS5 index + keyword corpus update in a single SQLite transaction

### TextRank Extraction (`TextRankExtractor.cs`)

Deterministic key-sentence extraction without LLM:

- Sentence tokenization and embedding (batch ONNX)
- Cosine similarity graph construction
- PageRank iteration for sentence centrality
- Top-K sentence selection within character budget

### Storage (`StorageService.cs`)

SQLite-backed storage with:

- Items, embeddings, FTS5 index, query log, URL cache
- Entity tables (entities, mentions, relationships)
- Batch operations for high-throughput ingestion
- Thread-safe semaphore-protected database access

## Dependencies

- [Mostlylucid.DocSummarizer](https://www.nuget.org/packages/Mostlylucid.DocSummarizer) - Shared NER, content extraction, RRF scoring, wave/signal types
- [Mostlylucid.Summarizer.Core](https://www.nuget.org/packages/Mostlylucid.Summarizer.Core) - Foundation pipeline interfaces

## License

MIT
