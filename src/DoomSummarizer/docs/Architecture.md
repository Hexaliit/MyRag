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

