# DoomSummarizer

> **⚠️ PREVIEW / ALPHA** — This project is in active development. APIs, commands, and features may change without notice until v1.0. Use at your own risk and expect rough edges.

[![GitHub release](https://img.shields.io/github/v/release/scottgal/lucidrag?include_prereleases&label=Release&logo=github)](https://github.com/scottgal/lucidrag/releases)
[![GitHub Downloads](https://img.shields.io/github/downloads/scottgal/lucidrag/total?label=Downloads&logo=github)](https://github.com/scottgal/lucidrag/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](https://github.com/scottgal/lucidrag/releases)

A distillation of [***lucid*RAG**](https://github.com/scottgal/lucidrag) principles — hybrid search, entity extraction, knowledge graph construction, evidence-grounded synthesis — into a console-first, local-first research assistant and personal knowledge base.

- **Scroll** — Fetch + rank news/search results into a digest, article, or newsletter
- **Ask** — Interactive Q&A over your stored knowledge base
- **Crawl** — Index any website for semantic search (incremental with HTTP ETag caching)
- **Page** — Summarize a single URL
- **Show** — Browse knowledge base collections
- **Long-form** — Generate evidence-grounded multi-section articles with validation
- **MCP Server** — Expose KB, search, and entity graph to AI agents via Model Context Protocol

Works fully offline after initial model downloads. No API keys required for default sources. Optional cloud LLM and search providers are budget-controlled.

**Download**: Grab pre-built binaries for Windows, Linux, and macOS from [**Releases**](https://github.com/scottgal/lucidrag/releases).

## Quick Start

```bash
# Build
dotnet build DoomSummarizer.csproj

# Daily digest (auto-downloads ONNX model on first run)
doomsummarizer scroll

# Topic query with tone
doomsummarizer scroll "AI regulation news" -v snarky

# Long-form article (evidence-grounded, 6-phase pipeline)
doomsummarizer scroll "history of transformers" -t blog-article -o article.md

# Deep-dive with custom sources
doomsummarizer scroll "Rust vs Go" -t deep-dive -s hn -s reddit -o rust-vs-go.md

# Newsletter to file
doomsummarizer scroll "dotnet news" -t newsletter -o weekly.html

# Q&A over stored evidence
doomsummarizer ask "What's the latest on SSH vulnerabilities?"

# Build a knowledge base, then query it
doomsummarizer crawl https://docs.example.com -n mydocs
doomsummarizer ask -s crawl:mydocs "how does authentication work?"
```

### Requirements

- **.NET 10** SDK
- **Ollama** (optional, recommended): `ollama serve` + pull models — see `doomsummarizer setup`
- **First run**: downloads ONNX embedding model (~23 MB) to `~/.doomsummarizer/models/`
- **No API keys** for default RSS/HTML sources. Optional: Brave/Serper/Tavily/NewsAPI + Anthropic/OpenAI

## Commands

### `scroll` — Aggregate and summarize

```bash
doomsummarizer scroll                              # Default sources, neutral vibe
doomsummarizer scroll "pharmaceutical news" -v hopeful  # Topic + vibe
doomsummarizer scroll -s hn -s bbc -s reddit -v doom    # Manual sources
doomsummarizer scroll -s search:rust -s factcheck       # Search + fact-check
doomsummarizer scroll --json --no-llm                   # Fast JSON, no LLM
doomsummarizer scroll --local "query"                   # Stored KB only
doomsummarizer scroll -t newsletter -o report.html      # Template + file
doomsummarizer scroll --entities --graph                # NER + knowledge graph
doomsummarizer scroll -v "excited about space"          # Custom vibe text
doomsummarizer scroll --list-templates                  # List all templates

# Long-form articles
doomsummarizer scroll "AI safety" -t blog-article -o ai-safety.md
doomsummarizer scroll "history of computing" -t blog-timeline -o timeline.md
doomsummarizer scroll "microservices" -t problem-solution -o micro.md
doomsummarizer scroll "React vs Svelte" -t pros-cons -o comparison.md
```

| Option | Short | Description |
|--------|-------|-------------|
| `--vibe TEXT` | `-v` | Tone: doom, hopeful, snarky, neutral, or any custom text |
| `--source NAME` | `-s` | Add source (repeatable) — see Sources below |
| `--template NAME` | `-t` | Output template — see Templates below |
| `--output FILE` | `-o` | Export to file (.md, .html, .json, .txt) |
| `--limit N` | `-l` | Max items to fetch (default: 30) |
| `--force` | `-f` | Ignore cache, fetch fresh |
| `--quiet` | `-q` | Minimal output |
| `--no-llm` | | Skip LLM — still runs embeddings, BM25, ranking |
| `--json` | | JSON output for automation |
| `--entities` | | NER entity extraction |
| `--graph` | | Knowledge graph build + display |
| `--no-links` | | Skip one-hop link following |
| `--debug` | | Pipeline diagnostics: RRF scores, salience |
| `--raw` | | Show raw fetched content |
| `--images` | | Inline thumbnails |
| `--local` | | Query stored KB only — no fetching |
| `--locale CODE` | | Locale for date/number parsing (default: en-us) |
| `--email` | | Send digest via email |
| `--email-to ADDR` | | Override email recipient(s) |

### `ask` — Interactive Q&A

Chat-style interface over stored evidence. Multi-turn with conversation context.

```bash
doomsummarizer ask "What's new in .NET 10?"
doomsummarizer ask -s crawl:mydocs "how does auth work?"
doomsummarizer ask --once "latest AI news"           # Single answer, exit
doomsummarizer ask --days 7 "this week's highlights"
```

Inside the loop: `sources`, `history`, `clear`, `quit`.

| Option | Short | Description |
|--------|-------|-------------|
| `--source NAME` | `-s` | Filter to source (e.g. `crawl:mysite`, `hn`) |
| `--days N` | | How far back to search (default: 30) |
| `--top N` | | Evidence items to use (default: 10) |
| `--once` | | Answer once, no interactive loop |
| `--quiet` | `-q` | Hide evidence, show answer only |

### `crawl` — Build a knowledge base

Incremental by default — uses HTTP ETags and content hashing to skip unchanged pages on re-crawl.

```bash
doomsummarizer crawl https://docs.example.com
doomsummarizer crawl https://wiki.local -n wiki -d 5 -m 500
doomsummarizer crawl https://blog.example.com -g "/blog/*" --entities
doomsummarizer crawl https://docs.example.com --force  # Bypass cache
```

Query crawled sites: `doomsummarizer ask -s crawl:wiki "search query"`
Browse contents: `doomsummarizer show wiki`

| Option | Short | Description |
|--------|-------|-------------|
| `--name NAME` | `-n` | Knowledge base name |
| `--depth N` | `-d` | Max crawl depth (default: 3) |
| `--max-pages N` | `-m` | Max pages (default: 200) |
| `--glob PATTERN` | `-g` | URL path filter (e.g., `/blog/*`, `/docs/**`) |
| `--force` | `-f` | Re-process all pages, ignore cache |
| `--delay MS` | | Request delay in ms (default: 500) |
| `--concurrency N` | | Concurrent requests (default: 3) |
| `--entities` | | NER entity extraction + knowledge graph |
| `--quiet` | `-q` | Minimal output |

### `show` — Browse knowledge base

```bash
doomsummarizer show                    # List all collections with stats
doomsummarizer show docs               # Items in the 'docs' collection
doomsummarizer show docs --full        # With content preview
```

| Option | Short | Description |
|--------|-------|-------------|
| `[name]` | | Collection name (omit to list all) |
| `--limit N` | `-l` | Max items (default: 50) |
| `--full` | | Show content preview |

### `page` — Summarize a single URL

```bash
doomsummarizer page https://example.com/article
doomsummarizer page https://example.com/article -t blog-article -o article.md
doomsummarizer page https://example.com/article --no-llm --raw
```

| Option | Short | Description |
|--------|-------|-------------|
| `--vibe TEXT` | `-v` | Tone (default: neutral) |
| `--template NAME` | `-t` | Template: default, blog-article, blog-timeline, detailed, json |
| `--output FILE` | `-o` | Export to file |
| `--quiet` | `-q` | Minimal output |
| `--raw` | | Show raw extracted content |
| `--no-llm` | | Skip LLM, show signals only |

### `benchmark` — Compare Ollama models

```bash
doomsummarizer benchmark                              # Auto-detect available
doomsummarizer benchmark "qwen3:4b,gemma3:4b"        # Specific models
doomsummarizer benchmark --role sentinel --rounds 3   # Sentinel only
doomsummarizer benchmark "qwen3:4b" --pull            # Auto-download first
```

### `trends` / `setup` / `config` / `sources`

```bash
doomsummarizer trends                # Sentiment over last 7 days
doomsummarizer trends -d 14          # Last 14 days
doomsummarizer setup                 # Verify all components
doomsummarizer setup --ner           # Download NER model (~430 MB)
doomsummarizer config --show         # Display current config
doomsummarizer config --init         # Create config file
doomsummarizer sources               # List all sources + API status
```

## Query Intelligence

DoomSummarizer uses multiple extraction layers to understand queries and filter results:

### Sentinel LLM Analysis

The sentinel LLM analyzes your query to extract:

- **Temporal intent**: Detects "recent", "last week", "breaking" to filter by date
- **Topic categories**: Classifies query into topics (technology, politics, health, etc.)
- **Search queries**: Generates optimized search terms, fixes spelling, expands abbreviations
- **Time sensitivity**: `breaking` (past hour), `today` (24-48h), `week` (7 days), `any`

```bash
# Temporal detection example
doomsummarizer scroll "recent court cases in Australia"
# Sentinel detects: requires_fresh=true, time_sensitivity="week", date_range="recent"
# Results filtered to last 2 weeks, old articles from 2020/2016 penalized
```

### Microsoft Recognizers Text

Deterministic extraction using [Microsoft.Recognizers.Text](https://github.com/microsoft/Recognizers-Text) to CONFIRM sentinel output:

- **DateTimes**: "last week", "March 15", "past 3 days" → resolved time ranges
- **Numbers**: "$500 million", "50%", "third" → normalized values
- **Sequences**: URLs, phone numbers, emails, IP addresses

```bash
# With locale support (date format varies by region)
doomsummarizer scroll "news about £500 million deal" --locale en-gb
# Recognizers output: dates:[last week], nums:[500 million]
```

Supported locales: `en-us`, `en-gb`, `es-es`, `fr-fr`, `de-de`, `pt-br`, `zh-cn`, `ja-jp`

### Named Entity Recognition (NER)

ONNX-based BERT model extracts entities:

- **PER**: Person names (politicians, executives, etc.)
- **ORG**: Organizations (companies, agencies)
- **LOC**: Locations (countries, cities)
- **MISC**: Miscellaneous (events, products)

```bash
# Entities guide source selection and cached content lookup
doomsummarizer scroll "OpenAI Sam Altman regulation" --entities
# NER detects: OpenAI (ORG), Sam Altman (PER)
# Cached items about these entities injected into results
```

### Freshness Detection

Multi-layer freshness scoring:

1. **Sentinel temporal extraction**: Detects recency keywords
2. **Article publication date**: Parsed from RSS/API responses
3. **Year heuristic**: Detects years in titles ("Cases from 2020" → penalized for recency queries)
4. **Exponential decay**: 48-hour half-life (7 days old = 0.06 score)

```bash
# Debug mode shows freshness scores
doomsummarizer scroll "recent tech news" --debug-pipeline
# Phase 1 table shows Fresh column: 0.99 (today) → 0.00 (years old)
```

### URL Fixer Service

Resolves aggregator URLs to canonical article URLs:

- **Google News**: Base64 decoding of `/rss/articles/CBMi...` URLs
- **Google Redirects**: Extracts target from `google.com/url?q=...`
- **Bing News**: Decodes `bing.com/news/apiclick?url=...`

Results are cached to avoid repeated lookups.

## Long-Form Article Generation

When using blog templates (`-t blog-article`, `-t blog-timeline`, or any YAML template), `scroll` activates a six-phase evidence-grounded pipeline instead of the standard digest synthesis.

```bash
# Generate an 8-section deep-dive on AI safety
doomsummarizer scroll "AI safety landscape 2026" -t deep-dive -o safety.md

# Timeline article — sections ordered chronologically
doomsummarizer scroll "history of large language models" -t blog-timeline -o llm-history.md

# Problem/solution structure
doomsummarizer scroll "technical debt in microservices" -t problem-solution -o tech-debt.md

# Balanced pros/cons analysis
doomsummarizer scroll "Kubernetes vs serverless" -t pros-cons -o k8s-vs-serverless.md
```

### Pipeline Phases

```
Phase 1  Evidence Preparation     Deterministic — segment extraction, ONNX embeddings, salience
Phase 2  Document Planning        Sentinel LLM — JSON outline with theme keywords per section
Phase 3  Evidence Assignment      Deterministic — embedding similarity, no LLM
Phase 4  Section Generation       Main LLM — sequential, with running context + entity tracking
Phase 5  Output Validation        Deterministic — URL/entity/fact grounding checks
Phase 6  Assembly                 Deterministic — stitch + template render
```

**Key properties:**
- Every URL in the output is verified against fetched evidence (hallucinated URLs are removed)
- Each section gets its own curated evidence slice via embedding similarity + salience scoring
- Running summary carries context forward without bloating the context window
- Entity continuity tracker maintains coherent references across sections
- Drift detection re-anchors sections that wander from the document theme
- LLM calls: N+3 total (1 outline + 1 intro + N sections + 1 conclusion)
- Everything else is deterministic (embeddings, assignment, validation)

### Evidence Assignment

Each section's theme keywords are embedded, then segments are scored:

```
score = 0.60 * cosine(section_theme, segment_embedding)
      + 0.25 * segment_salience     (TextRank graph centrality)
      + 0.15 * article_relevance    (RRF score from ranking pipeline)
```

Greedy selection with MMR diversity (max 2 segments per article per section) and dedup (cosine > 0.85 skipped). Unassigned high-salience segments are rescued to best-matching sections.

### Output Validation

After generation, every claim is checked against the evidence corpus:

| Check | Method | Action on failure |
|-------|--------|-------------------|
| URLs | Whitelist against fetched evidence | Remove link, keep text |
| Entities | Fuzzy match (Levenshtein ≤ 2) | Flag as ungrounded |
| Titles | Jaccard similarity to known titles | Replace with closest match |
| Facts | Sentence embedding vs evidence (cosine > 0.6) | Flag if < 0.4 |

Documents with grounding score < 70% are flagged. Auto-fix removes hallucinated URLs and corrects fabricated source titles.

## Templates

### Built-in Templates

| Template | Description | Output |
|----------|-------------|--------|
| `default` | Standard console digest | Markdown |
| `console` | Compact console display | Text |
| `compact` | Minimal bullet list | Markdown |
| `detailed` | Full details with sentiment | Markdown |
| `file` | Clean markdown with YAML frontmatter | Markdown |
| `email` | HTML email with inline styles | HTML |
| `newsletter` | Professional newsletter | HTML |
| `slack` | Slack-formatted message | Slack |
| `json` | Raw JSON for automation | JSON |
| `image` | Single item with featured image | Markdown |
| `blog-article` | Multi-section long-form article | Markdown |
| `blog-timeline` | Chronological article | Markdown |
| `blog-newsletter` | Curated newsletter with editorial picks | Markdown |
| `blog-newsletter-html` | Newsletter as styled HTML | HTML |

### YAML Templates (Structured Articles)

Pre-built article structures with fixed sections, word targets, and evidence strategies:

| Template | Sections | Description |
|----------|----------|-------------|
| `deep-dive` | 5 | Context, Technical Analysis, Key Findings, Expert Perspectives, Implications |
| `problem-solution` | 4 | The Problem, Why It Matters, Proposed Solutions, The Path Forward |
| `pros-cons` | 4 | Background, The Case For, The Case Against, The Verdict |

```bash
# Use a YAML template
doomsummarizer scroll "WebAssembly adoption" -t deep-dive -o wasm.md
```

Custom YAML templates go in `~/.doomsummarizer/templates/`. See `docs/Templates.md` for the schema.

### List All Available Templates

```bash
doomsummarizer scroll --list-templates
```

## Sources

Default sources are free (RSS/HTML), no API keys required.

| Category | Sources | Example |
|----------|---------|---------|
| Tech | Hacker News, Reddit, Lobsters, Slashdot, Dev.to, HackerNoon | `-s hn`, `-s reddit:dotnet` |
| News | BBC, CNN, Reuters, Guardian, Ars, Verge, Wired, TechCrunch | `-s bbc:health`, `-s guardian` |
| Search (free) | Google News, DuckDuckGo | `-s "search:rust programming"` |
| Search (API) | Brave, Serper, Tavily, NewsAPI, NewsData, Jina | `-s brave`, `-s serper` |
| Academic | arXiv | `-s arxiv` |
| Q&A | StackOverflow | `-s so:csharp` |
| Fact Check | Snopes, PolitiFact, FactCheck.org, FullFact | `-s factcheck:snopes` |
| Space | Spaceflight News | `-s spaceflight` |
| Seismic | USGS Earthquakes | `-s earthquake:significant_week` |
| Reference | Wikipedia | `-s wiki:news` |
| Custom | Any URL or RSS feed | `scroll https://example.com/feed.xml` |

Sources are auto-selected via semantic topic routing (e.g., "pharmaceutical news" routes to health feeds).

## Processing Pipeline

```
Query → PromptInterpreter → SourceRouter (YAML) → Parallel Fetchers
  → Cache Check (reuse segments for similar queries)
  → URL/Title Dedup → FTS5 KB Enrichment (keyword pre-filter)
  → Document Keyword Profiling (structural weighting: title 4x, headings 3x, intro 2x)
  → ONNX Embeddings (384-dim all-MiniLM-L6-v2)
  → Phase 1 RRF (BM25F + Freshness + Authority + Quality) → Hard gate (cosine ≥ 0.20) → Discard bottom 25%
  → PRF Centroid Refinement (top-5 embedding average, α=0.7)
  → Phase 2 RRF (+ Query Similarity + Vibe Alignment + Quality) → Hard gate (cosine ≥ 0.20)
  → Source Reliability Weights → LFU Diversity Decay → In-Corpus PageRank
  → One-Hop Link Following → TextRank Sentence Extraction
  → Entity Graph Enrichment (co-occurrence discovery, ≥2 shared entities)
  → LLM Synthesis (evidence-grounded) or Long-Form Pipeline (blog templates)
```

### Ranking Signals (RRF Fusion)

| Signal | Weight | Phase | Description |
|--------|--------|-------|-------------|
| BM25F | 1.0 | 1 | Field-weighted TF-IDF: title (2x), keywords (2.5x), content (1x) |
| Freshness | 0.5 | 1 | Exponential decay (48h half-life) |
| Authority | 0.3 | 1 | Platform score (HN upvotes, etc.) |
| Query Similarity | 0.8 | 2 | Embedding cosine similarity |
| Vibe Alignment | 0.4 | 2 | Embedding cosine to vibe |
| Quality | 0.2 | 1+2 | Embedding-based clickbait vs substantive content scoring |

### `--no-llm` Mode

Without Ollama, the full signal pipeline still runs: ONNX embeddings, BM25, sentiment, topic inference, RRF ranking, NER (with `--entities`). All signals stored to SQLite.

## LLM Providers

### Cloud LLMs

| Provider | Models | Key |
|----------|--------|-----|
| Anthropic | Claude Sonnet 4 (main), Claude 3.5 Haiku (sentinel) | `ANTHROPIC_API_KEY` |
| OpenAI | GPT-4o-mini (main + sentinel) | `OPENAI_API_KEY` |

Budget-controlled with per-service rate limits, retry with backoff, and circuit breakers.

### Local Models (Ollama)

| Role | Default | Purpose |
|------|---------|---------|
| Synthesis | `gemma3:4b` | Digests, articles, evidence-grounded answers |
| Sentinel | `qwen3:0.6b` | Triage, JSON outlines, fast classification |

Use `benchmark` to find optimal models for your hardware. Ollama is always the free fallback when cloud providers are unavailable or over budget.

## Vibes

`-v` accepts a preset name or any custom text.

| Vibe | Tone |
|------|------|
| `doom` | Pessimistic, problem-focused |
| `hopeful` | Optimistic, opportunity-focused |
| `snarky` | Witty, cynical commentary |
| `funny` | Puns, absurd analogies |
| `upbeat` | High energy, celebratory |
| `friendly` | Warm, conversational |
| `neutral` | Objective, balanced (default) |
| *custom* | `-v "excited about space exploration"` |

## Configuration

Config file: `~/.doomsummarizer/config.json` — Local override: `doomsummarizer.json` in working directory.

```json
{
  "sources": {
    "hackerNews": { "enabled": true, "sections": ["top", "best"], "maxStories": 30, "minScore": 50 },
    "reddit": { "enabled": true, "subreddits": ["programming", "csharp", "dotnet"], "minScore": 100 }
  },
  "sourceFilter": {
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
  "linkFollowing": { "enabled": true, "maxLinksPerArticle": 3, "maxTotalLinks": 15 },
  "keys": [
    { "name": "anthropic", "apiKey": "", "enabled": true, "maxRequestsPerDay": 200, "rateLimitMs": 100 },
    { "name": "brave_search", "apiKey": "", "enabled": true, "maxRequestsPerDay": 60, "rateLimitMs": 1100 }
  ],
  "apiBudget": { "globalMaxRequestsPerDay": 500, "globalDailyBudgetUsd": 2.0 }
}
```

### API Keys

Priority: .NET user secrets > environment variables > config JSON.

```bash
# .NET user secrets (recommended)
dotnet user-secrets set "Anthropic" "sk-ant-..."
dotnet user-secrets set "BraveSearch" "BSA..."

# Environment variables
export ANTHROPIC_API_KEY=sk-ant-...
export DOOM_BRAVE_SEARCH=BSA...
```

### Resilience

| Setting | Default | Purpose |
|---------|---------|---------|
| `rateLimitMs` | 200 | Minimum delay between requests |
| `maxRetries` | 2 | Retry on 429/5xx |
| `circuitBreakerThreshold` | 3 | Failures before circuit opens |
| `circuitBreakerResetSeconds` | 60 | Reset time |

### Email Delivery

```json
{
  "email": {
    "provider": "sendgrid",
    "enabled": true,
    "fromAddress": "digest@example.com",
    "fromName": "DoomSummarizer",
    "toAddresses": "team@example.com",
    "subjectTemplate": "Doom Scroll Digest — {{DATE}}",
    "template": "newsletter"
  }
}
```

```bash
dotnet user-secrets set "SendGrid" "SG.xxx"
doomsummarizer scroll "AI news" --email
doomsummarizer scroll "security" --email --email-to "ops@example.com"
```

### Prompt Customization

Override any LLM prompt by placing a file in `~/.doomsummarizer/prompts/`. Uses `{{VARIABLE}}` placeholders and Liquid syntax (`{% if %}`, `{% for %}`). Built-in defaults are embedded in the binary.

```
~/.doomsummarizer/prompts/
├── roundup.txt           # Headline roundup
├── answer.txt            # Q&A answer
├── digest.txt            # Digest format
├── ask-answer.txt        # Interactive Q&A
├── newsletter.txt        # Newsletter format
├── processed-digest.txt  # Digest with confidence scores
├── blog-intro.txt        # Article introduction
├── blog-section.txt      # Article section (standard)
├── blog-conclusion.txt   # Article conclusion
├── blog-outline.txt      # Article outline (sentinel)
├── longform-outline.txt  # Long-form outline (with entities + segment count)
└── longform-section.txt  # Long-form section (running summary + entity tracking + drift)
```

## Storage

- **SQLite** (`~/.doomsummarizer/doom.db`) — Articles, embeddings, query logs, trends, usage
- **DuckDB** (`~/.doomsummarizer/vectors.duckdb`) — HNSW vector index (with `--graph`)
- **Retention:** 30 days (configurable)

## Documentation

- `docs/CLI.md` — All commands, options, and examples
- `docs/Sources.md` — Source syntax (`-s`) and API integrations
- `docs/KnowledgeBase.md` — Storage, crawling, `ask`, entities, graph
- `docs/Templates.md` — Built-in + custom templates (Liquid + YAML)
- `docs/Config.md` — Config file, env vars, API keys, budgets
- `docs/Automation.md` — JSON/file output and scheduling
- `docs/Architecture.md` — Pipeline and storage architecture
- `docs/FunctionalSpec.AdaptiveRetrieval.md` — Cache-vs-live retrieval, gap-filling subqueries (DeepRAG-inspired)
- `docs/MCP.md` — MCP server setup, tools reference, agent workflows
- `docs/Troubleshooting.md` — Common issues

## MCP Server (AI Agent Integration)

DoomSummarizer exposes its knowledge base, search pipeline, and entity graph as an [MCP (Model Context Protocol)](https://modelcontextprotocol.io) server. This lets AI agents like Claude Code, Claude Desktop, or any MCP client query your stored knowledge, ingest URLs, and explore entity relationships.

### Starting the MCP Server

```bash
doomsummarizer --mcp
```

This launches a stdio-based MCP server. The server uses the same SQLite database and ONNX embedding model as the CLI.

### Configuration

**Claude Code** (`~/.claude.json`):
```json
{
  "mcpServers": {
    "doomsummarizer": {
      "command": "doomsummarizer",
      "args": ["--mcp"]
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "doomsummarizer": {
      "command": "/path/to/doomsummarizer",
      "args": ["--mcp"]
    }
  }
}
```

### Available Tools

| Tool | Description |
|------|-------------|
| **search_kb** | Full relevance pipeline search (FTS5 pre-filter → BM25F + embeddings → PRF refinement → RRF) |
| **keyword_search** | Fast FTS5 keyword-only search (no embeddings) |
| **semantic_search** | Pure embedding cosine similarity search |
| **get_item_content** | Retrieve full content, entities, and keyword profile for an item by ID |
| **extract_keywords** | Deterministic keyword extraction from arbitrary text (structural weighting) |
| **compare_items** | Cosine similarity + keyword Jaccard overlap between two items |
| **ingest_url** | Fetch a URL, extract content, embed, profile, and index into the KB |
| **list_collections** | List all KB collections with item counts and stats |
| **get_collection_items** | Browse items in a collection with pagination |
| **list_entities** | Top entities from the knowledge graph (filterable by type/recency) |
| **get_entity_details** | Entity relationships and mentioning articles |
| **get_entity_network** | Subgraph exploration — seed entities + neighbors + co-occurring articles |
| **find_related_by_entities** | Discover documents sharing entities with given items |
| **get_kb_stats** | KB overview: collections, entities, FTS5 index, embedding model info |
| **get_trends** | Topic distribution and sentiment analysis over time |

### Example Agent Workflows

**Research assistant**: Search your crawled documentation, then follow up with entity graph exploration:
```
Agent: search_kb("authentication flow") → get_entity_details("oauth2") → find_related_by_entities(...)
```

**Knowledge ingestion**: Ingest a URL, then verify it was indexed:
```
Agent: ingest_url("https://example.com/article") → get_item_content(id) → extract_keywords(content)
```

**Comparative analysis**: Compare two articles' semantic overlap:
```
Agent: keyword_search("AI safety") → compare_items(id1, id2)
```

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

278 tests covering ranking pipeline, embeddings, templates, long-form generation (32 unit + 5 ONNX integration), entity disambiguation, prompt interpretation, and knowledge graph operations.

## Platforms

Pre-built binaries: Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64.

## License

MIT
