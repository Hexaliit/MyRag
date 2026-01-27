# DoomSummarizer

A local-first intelligent research assistant that aggregates, ranks, and synthesizes content from 30+ news sources — with interactive Q&A, knowledge base crawling, and evidence-grounded answers.

Single binary, no API keys required. ONNX embeddings + multi-signal RRF ranking + local LLM synthesis.

## Quick Start

```bash
# Download binary from releases, or build from source
dotnet build src/DoomSummarizer/DoomSummarizer.csproj

# First run auto-downloads ONNX embedding model (~80MB one-time)
doomsummarizer scroll

# Natural language queries
doomsummarizer scroll "AI security news" --vibe snarky

# Interactive Q&A over stored knowledge
doomsummarizer ask "What happened with the SSH vulnerability?"

# Build a knowledge base from any website
doomsummarizer crawl https://docs.example.com --name mydocs
doomsummarizer ask --source crawl:mydocs "how does authentication work?"
```

### Requirements

- **Ollama** (optional but recommended): `ollama serve` + `ollama pull gemma3:4b` + `ollama pull qwen3:0.6b`
- **No API keys**: All sources use free RSS/REST APIs. Embeddings and NER run locally via ONNX.
- Without Ollama, `--nollm` mode still runs full RRF ranking with embeddings, BM25, sentiment, and topic inference.

## Commands

### `scroll` — Aggregate and summarize

```bash
doomsummarizer scroll                                        # Default sources
doomsummarizer scroll "new pharmaceutical news" --vibe hopeful  # Topic routing
doomsummarizer scroll -s hn -s reddit -s bbc --vibe doom     # Manual sources
doomsummarizer scroll -s search:rust -s factcheck            # Search + fact-check
doomsummarizer scroll --json --nollm                         # Fast JSON (no LLM)
doomsummarizer scroll --local "query"                        # Stored KB only
doomsummarizer scroll -o report.md -t newsletter             # File export
doomsummarizer scroll --entities --graph                     # NER + knowledge graph
doomsummarizer scroll --vibe "excited about space"           # Custom vibe text
```

### `ask` — Interactive Q&A

Chat-style interface over your stored knowledge base. Multi-turn with conversation context.

```bash
doomsummarizer ask "What's the latest on AI regulation?"
doomsummarizer ask --source crawl:docs "how does auth work?"
doomsummarizer ask --once "latest AI news"    # Single answer, no loop
```

Inside the loop: type follow-up questions, `sources` to list evidence, `history` to review, `clear` to reset, `quit` to exit.

### `crawl` — Build a knowledge base

Indexes a website with embedded vectors for semantic search.

```bash
doomsummarizer crawl https://docs.example.com
doomsummarizer crawl https://wiki.local -n wiki --depth 5 --max-pages 500
doomsummarizer crawl https://intranet.company.com --entities
```

Query crawled sites: `doomsummarizer scroll --local -s crawl:wiki "search query"`

### `benchmark` — Compare Ollama models

Tests models for speed and output quality on your hardware.

```bash
doomsummarizer benchmark                                    # Auto-detect available
doomsummarizer benchmark "qwen3:4b,gemma3:4b,phi4-mini"    # Specific models
doomsummarizer benchmark --role sentinel --rounds 3         # Sentinel only
doomsummarizer benchmark "qwen3:4b" --pull                  # Auto-download first
```

### `trends` — Sentiment over time

```bash
doomsummarizer trends              # Last 7 days
doomsummarizer trends --days 14
```

### `setup` / `config` / `sources`

```bash
doomsummarizer setup               # Verify all components
doomsummarizer setup --ner         # Download BERT NER model (~430MB)
doomsummarizer setup --playwright  # Install browser for JS sites
doomsummarizer config --show       # Display current config
doomsummarizer config --init       # Create config file
doomsummarizer sources             # List all sources and routing
```

## Sources

All sources are free, no API keys required.

| Category | Sources | Example |
|----------|---------|---------|
| Tech | Hacker News, Reddit, Lobsters, Slashdot, Dev.to, HackerNoon | `-s hn`, `-s reddit:dotnet` |
| News | BBC, CNN, Reuters, Guardian, Ars Technica, The Verge, Wired, TechCrunch | `-s bbc:health`, `-s guardian` |
| Search | Google News (topic + query), DuckDuckGo | `-s "search:rust programming"` |
| Academic | arXiv papers | `-s arxiv` |
| Q&A | StackOverflow (hot, by tag, search) | `-s so:csharp` |
| Fact Check | Snopes, PolitiFact, FactCheck.org, FullFact | `-s factcheck:snopes` |
| Space | Spaceflight News (NASA, ESA, SpaceX) | `-s spaceflight` |
| Seismic | USGS Earthquakes (real-time GeoJSON) | `-s earthquake:significant_week` |
| Reference | Wikipedia (current events, on-this-day) | `-s wiki:news` |
| Custom | Any URL, RSS feed, or crawled website | `scroll https://example.com` |

Sources auto-selected via semantic topic routing (e.g., "pharmaceutical news" routes to health feeds).

## Processing Pipeline

Content goes through a multi-stage ranking pipeline:

```
Query -> PromptInterpreter -> SourceRouter (YAML) -> Parallel Fetchers
  -> Cache Check (reuse segments for similar queries)
  -> URL/Title Dedup
  -> Phase 1 RRF (BM25 + Freshness + Authority) -> Discard bottom 25%
  -> ONNX Embeddings (384-dim, always runs)
  -> Phase 2 RRF (+ Query Similarity + Vibe Alignment)
  -> Source Reliability Weights
  -> LFU Diversity Decay (frequently-returned items penalized)
  -> In-Corpus PageRank (cross-reference authority boost)
  -> One-Hop Link Following (content enrichment)
  -> TextRank Sentence Extraction (graph centrality)
  -> LLM Synthesis (evidence-grounded, never hallucinates URLs)
  -> Query Feedback (log for segment reuse)
```

### Ranking Signals (RRF Fusion)

| Signal | Weight | Phase | Description |
|--------|--------|-------|-------------|
| BM25 | 1.0 | 1 | TF-IDF keyword match against query |
| Freshness | 0.5 | 1 | Exponential decay (48h half-life) |
| Authority | 0.3 | 1 | Platform score (HN upvotes, etc.) |
| Query Similarity | 0.8 | 2 | Embedding cosine similarity to query |
| Vibe Alignment | 0.4 | 2 | Embedding cosine similarity to vibe |

### Query Feedback & Segment Reuse

Similar queries (>85% embedding similarity within 4 hours) reuse cached segments instead of re-fetching. Items returned frequently get mild LFU diversity decay: `1/(1 + 0.1 * log2(accessCount))`.

### `--nollm` Mode

Without Ollama, the full signal pipeline still runs:
- ONNX embeddings for all items
- BM25 + TF-IDF keyword matching
- Embedding-based sentiment scoring
- Embedding-based topic inference
- Full RRF ranking
- NER entity extraction (with `--entities`)
- All signals stored to SQLite

## Two-Tier Model Architecture

| Role | Default Model | Purpose |
|------|--------------|---------|
| Synthesis | `gemma3:4b` | Digest generation, evidence-grounded answers |
| Sentinel | `qwen3:0.6b` | Per-article triage, JSON analysis, fast classification |

Selected via benchmarking. Synthesis alternatives: `qwen3:8b` (higher quality, slower). Sentinel alternatives: `qwen2.5:1.5b` (fastest wall-clock), `gemma3:1b`.

Use `benchmark` to find the best model for your hardware.

## Vibes

- **doom** — Pessimistic, problem-focused
- **hopeful** — Optimistic, opportunity-focused
- **snarky** — Witty, cynical commentary
- **neutral** — Objective, balanced facts
- **Custom** — Any text: `--vibe "excited about space exploration"`

## Flags

| Flag | Description |
|------|-------------|
| `--vibe` | Tone: doom, hopeful, snarky, neutral, or custom text |
| `--source` | Override sources (hn, reddit, bbc, gnews:query, etc.) |
| `--limit N` | Maximum items to fetch (default: 30) |
| `--force` | Ignore cache and fetch fresh |
| `--nollm` | Skip LLM — still runs embeddings, BM25, sentiment, topic |
| `--entities` | Enable NER entity extraction |
| `--graph` | Enable knowledge graph build and display |
| `--no-links` | Skip one-hop link following |
| `--output FILE` | Export to file (.md, .json, .html, .txt) |
| `--template` | Output template: default, console, compact, detailed, email, newsletter, slack, json |
| `--json` | Output as JSON (for automation/LLM tools) |
| `--local` | Query stored knowledge base only — no fetching |
| `--debug` | Show pipeline diagnostics: RRF scores, discards, salience |
| `--raw` | Show raw fetched content |
| `--images` | Display inline thumbnails |
| `-q, --quiet` | Minimal output |

## Configuration

Config file: `~/.doomsummarizer/config.json`

```json
{
  "sources": {
    "hackerNews": { "enabled": true, "sections": ["top", "best"], "maxStories": 30, "minScore": 50 },
    "reddit": { "enabled": true, "subreddits": ["programming", "csharp", "dotnet"], "minScore": 100 }
  },
  "sourceFilter": {
    "allowedDomains": [],
    "blockedDomains": ["facebook.com"],
    "weights": { "reuters": 1.4, "bbc": 1.3, "hn": 1.1, "reddit": 0.9 }
  },
  "ollama": {
    "model": "gemma3:4b",
    "sentinelModel": "qwen3:0.6b",
    "temperature": 0.4,
    "timeoutSeconds": 300
  },
  "embedding": { "backend": "onnx", "model": "all-MiniLM-L6-v2" },
  "linkFollowing": { "enabled": true, "maxLinksPerArticle": 3, "maxTotalLinks": 15 }
}
```

Output templates: `default`, `console`, `compact`, `detailed`, `file`, `email`, `newsletter`, `slack`, `json`. Custom Liquid templates: `~/.doomsummarizer/templates/`

## Storage

- **SQLite** (`~/.doomsummarizer/doom.db`) — Articles, embeddings, query logs, trends, usage tracking
- **DuckDB** (`~/.doomsummarizer/vectors.duckdb`) — HNSW vector index (when `--graph` enabled)
- **Retention:** 30 days default (configurable)

## Platforms

Pre-built binaries for:
- Windows x64, ARM64
- Linux x64, ARM64
- macOS x64 (Intel), ARM64 (Apple Silicon)

## Building from Source

```bash
git clone https://github.com/scottgal/LucidRAG.git
cd LucidRAG
dotnet build src/DoomSummarizer/DoomSummarizer.csproj
dotnet run --project src/DoomSummarizer/DoomSummarizer.csproj -- scroll
```

## Tests

```bash
dotnet test src/DoomSummarizer.Tests/DoomSummarizer.Tests.csproj
```

157 tests covering storage, ranking, markdown processing, URL handling, configuration, source filtering, and query feedback.

## Adding New Sources

Create a fetcher in `Services/`, register in `ScrollCommand.cs`, and add topic routing in `Resources/sources.yaml`. See [the guide in the existing README](#adding-new-sources) — a new source is ~50 lines.

No-auth APIs: [github.com/public-api-lists/public-api-lists](https://github.com/public-api-lists/public-api-lists)

## License

MIT
