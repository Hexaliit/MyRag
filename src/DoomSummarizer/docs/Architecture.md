# Architecture (high level)

This is a “local-first RAG-ish” console app:

1. **Fetch** content from sources (RSS, HTML scraping, and optional API providers)
2. **Normalize** content into `ContentItem` records
3. **Rank** items using a multi-signal pipeline (BM25 + embeddings + freshness + authority + diversity)
4. **Enrich** items (optional link following, TextRank excerpts, entity extraction)
5. **Store** items, embeddings, and metadata locally (SQLite; optional DuckDB vector store for graphs)
6. **Synthesize** outputs using local or cloud LLMs (budgeted + fallback)

## Data stores

SQLite (`$HOME/.doomsummarizer/doom.db`):
- Items, embeddings, query log, URL cache, trends, entity tables, and disambiguation caches

DuckDB (`$HOME/.doomsummarizer/vectors.duckdb`, when `--graph` is enabled):
- HNSW vector indexes for item/entity similarity search
- Entity graph (mentions + co-occurrence edges)

## LLM providers and fallback

When an LLM call is needed, DoomSummarizer routes requests through `LlmRouter`:
- Cloud providers (OpenAI/Anthropic) if configured and within budget
- Always falls back to local Ollama if available
- If no LLM is available, DoomSummarizer still runs ranking/enrichment and can emit evidence-only outputs

## Ranking pipeline (as implemented in `scroll`)

At a glance:
- Deduplicate items by URL/title
- **Phase 1**: fast scoring + discard low-salience tail (BM25 + freshness + authority + similarity)
- Compute embeddings for remaining items
- **Phase 2**: full RRF with query similarity + vibe alignment
- Apply source weights, LFU diversity decay, and in-corpus link authority boosts
- Optionally follow one-hop links to enrich content
- Extract key sentences via TextRank (no LLM required)

Use `doomsummarizer scroll --debug` to see the scoring stages and discards on your machine/data.

## Long-form generation pipeline (blog templates)

When using `-t blog-article`, `-t blog-timeline`, or any YAML template (e.g. `deep-dive`, `problem-solution`, `pros-cons`), `scroll` activates a six-phase evidence-grounded pipeline instead of the standard digest synthesis:

```
Phase 1  Evidence Preparation     ArticleProcessor segments → EvidenceCorpus
Phase 2  Document Planning        Sentinel LLM → JSON outline with theme keywords
Phase 3  Evidence Assignment      Embedding similarity (0.60) + salience (0.25) + relevance (0.15)
Phase 4  Section Generation       Main LLM per section, with running summary + entity tracker
Phase 5  Output Validation        URL whitelist, entity fuzzy match, fact grounding
Phase 6  Assembly                 Stitch sections → BlogArticleResult → template render
```

Key design principles:
- **Minimize LLM calls**: Only 2 phases use LLMs (planning + generation). Everything else is deterministic using ONNX embeddings and salience scores from the ranking pipeline.
- **Single ONNX model**: Both segment embeddings (from `ArticleProcessor`) and theme embeddings (for assignment/validation) use the same `all-MiniLM-L6-v2` model via `OnnxEmbeddingService`.
- **Per-section evidence curation**: Each section gets its own slice of evidence based on theme keyword embedding similarity, allowing the document to evolve through different facets without context stuffing.
- **Running summary**: Top-salience segments from completed sections carry context forward (no LLM compression). Two-tier: recent sections = full digest, older = first sentence.
- **Entity continuity**: String-matching tracker tells each section which entities to maintain or reintroduce.
- **Drift detection**: Cosine similarity to plan's theme embedding; corrective guidance if < 0.35.
- **Validation**: Every URL verified against fetched evidence whitelist. Hallucinated URLs removed, fabricated titles replaced.

Implementation lives in `Services/LongFormGeneration/` and `Models/LongFormGeneration/`.

