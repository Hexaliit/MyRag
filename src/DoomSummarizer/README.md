# DoomSummarizer

> **⚠️ PREVIEW / ALPHA** — This project is in active development. APIs, commands, and features may change without notice until v1.0. Use at your own risk and expect rough edges.

[![GitHub release](https://img.shields.io/github/v/release/scottgal/lucidrag?include_prereleases&label=Release&logo=github)](https://github.com/scottgal/lucidrag/releases)
[![GitHub Downloads](https://img.shields.io/github/downloads/scottgal/lucidrag/total?label=Downloads&logo=github)](https://github.com/scottgal/lucidrag/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)](https://github.com/scottgal/lucidrag/releases)

A distillation of [***lucid*RAG**](https://github.com/scottgal/lucidrag) principles — hybrid search, entity extraction, knowledge graph construction, evidence-grounded synthesis — into a console-first, local-first research assistant and personal knowledge base.

- **Scroll** — Fetch + rank news/search results into a digest, article, or newsletter
- **Ask** — Interactive Q&A over your stored knowledge base
- **Crawl** — Index any website or local files/directories for semantic search (PDF, DOCX, Markdown, HTML, TXT, PPTX)
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

# Build a knowledge base from a website, then query it
doomsummarizer crawl https://docs.example.com -n mydocs
doomsummarizer ask -s crawl:mydocs "how does authentication work?"

# Ingest local files and ask questions interactively
doomsummarizer crawl C:\docs\project-specs --ask
doomsummarizer crawl /home/user/research --recurse --ask

# Crawl a website with background crawl + live Q&A
doomsummarizer crawl https://docs.example.com --ask
```

### Requirements

- **.NET 10** SDK
- **Ollama** (recommended): `ollama serve` + pull models — see `doomsummarizer setup`
- **First run**: downloads ONNX embedding model ([all-MiniLM-L6-v2](https://huggingface.co/Xenova/all-MiniLM-L6-v2), 384-dim, ~23 MB quantized) to `~/.doomsummarizer/models/`
- **No API keys required** — default sources are free RSS/HTML. Optional search APIs (Brave, Serper, Tavily, NewsAPI) and cloud LLMs (Anthropic, OpenAI) are disabled by default

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

Chat-style interface over stored evidence. Uses the same synthesis pipeline as `scroll` (smart evidence budgeting, TextRank compression, semantic re-ranking). Multi-turn with conversation context.

```bash
doomsummarizer ask "What's new in .NET 10?"
doomsummarizer ask -s crawl:mydocs "how does auth work?"
doomsummarizer ask -s file:my-project "what's the architecture?"
doomsummarizer ask --once "latest AI news"           # Single answer, exit
doomsummarizer ask --days 7 "this week's highlights"
```

Inside the loop: `sources`, `history`, `clear`, `quit`.

| Option | Short | Description |
|--------|-------|-------------|
| `--source NAME` | `-s` | Filter to source (e.g. `crawl:mysite`, `file:specs`, `hn`) |
| `--days N` | | How far back to search (default: 30) |
| `--top N` | | Evidence items to use (default: 10) |
| `--once` | | Answer once, no interactive loop |
| `--quiet` | `-q` | Hide evidence, show answer only |

### `crawl` — Build a knowledge base

Accepts a URL (web crawl) or local file/directory path (document ingestion). Web crawls are incremental by default — uses HTTP ETags and content hashing to skip unchanged pages.

```bash
# Web crawl
doomsummarizer crawl https://docs.example.com
doomsummarizer crawl https://wiki.local -n wiki -d 5 -m 500
doomsummarizer crawl https://blog.example.com -g "/blog/*" --entities
doomsummarizer crawl https://docs.example.com --force  # Bypass cache

# Local file/directory ingestion
doomsummarizer crawl C:\docs\project-specs
doomsummarizer crawl /home/user/papers --recurse
doomsummarizer crawl C:\Blog\posts --ask               # Ingest + Q&A

# Web crawl + interactive Q&A (background crawl)
doomsummarizer crawl https://docs.example.com --ask
```

Query crawled sites: `doomsummarizer ask -s crawl:wiki "search query"`
Query local files: `doomsummarizer ask -s file:project-specs "search query"`
Browse contents: `doomsummarizer show wiki`

| Option | Short | Description |
|--------|-------|-------------|
| `--name NAME` | `-n` | Knowledge base name |
| `--depth N` | `-d` | Max crawl depth (default: 3) |
| `--max-pages N` | `-m` | Max pages (default: 200) |
| `--glob PATTERN` | `-g` | URL path filter (e.g., `/blog/*`, `/docs/**`) |
| `--force` | `-f` | Re-process all pages/files, ignore cache |
| `--delay MS` | | Request delay in ms (default: 1000) |
| `--concurrency N` | | Concurrent requests (default: 3) |
| `--entities` | | NER entity extraction + knowledge graph |
| `--ask` | | Interactive Q&A mode (local: after ingest; URL: during crawl) |
| `--recurse` | `-r` | Recurse subdirectories for local paths (default: top-level) |
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

ONNX-based BERT model ([protectai/bert-base-NER-onnx](https://huggingface.co/protectai/bert-base-NER-onnx), ~430 MB) extracts entities. See [Models & ML Pipeline](#models--ml-pipeline) for full details.

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

## v0.6.1: Advanced Search & Retrieval

### Composite Query Decomposition

DoomSummarizer now intelligently handles multi-part questions joined by "and", "also", or implicit conjunctions:

```bash
# Multi-question query
doomsummarizer scroll "What's new in AI safety and what are the latest regulations?"

# Sentinel decomposition:
# - Subquery 1: "What's new in AI safety?"
# - Subquery 2: "What are the latest AI regulations?"
```

**How it works:**
1. **Sentinel Detection** — The sentinel LLM identifies composite queries and extracts subqueries
2. **Multi-Query Embedding** — Each subquery gets its own embedding vector
3. **Max Similarity Scoring** — Items are scored against the BEST matching subquery (not averaged)
4. **Structured Responses** — LLM explicitly addresses each sub-question in the output

Debug mode shows decomposition:
```bash
doomsummarizer scroll "AI safety and regulations" --debug
# [cyan]Composite query detected: 2 subqueries[/]
# [grey]  1. What's new in AI safety?[/]
# [grey]  2. What are the latest AI regulations?[/]
```

### Lucene.NET Hybrid Search

The retrieval pipeline uses [Lucene.NET](https://lucenenet.apache.org/) (Apache Lucene for .NET) instead of SQLite FTS5 as the primary full-text search engine. Per-collection indexes are stored at `~/.doomsummarizer/lucene/<collection>/`.

| Feature | Lucene.NET | SQLite FTS5 |
|---------|-----------|-------------|
| **BM25F field weighting** | Title (2x), keywords (2.5x), content (1x) | Equal weight |
| **Porter stemming** | "running" matches "run" | No stemming |
| **Fuzzy matching** | `languge~` finds "language" | No fuzzy |
| **Phrase boosting** | `"machine learning"^3` | No phrase boost |
| **Query syntax** | Full boolean + proximity + wildcards | Simple AND/OR |

Lucene and embedding HNSW searches run in parallel (`Task.WhenAll`) and are fused via RRF.

```bash
# Fuzzy + boosted search
doomsummarizer scroll "langauge models transformer" --debug
# [grey]Lucene: 15 keyword matches (fuzzy: langauge~, boosted: transformer^2)[/]
```

### Entity Profile HNSW (Preview)

For knowledge bases with `--entities`, documents get **entity profile embeddings** for semantic graph retrieval:

```
Document → NER entities → Entity embeddings → TF×IDF×confidence weighting → L2-normalized profile
```

Query-time: Find related documents via HNSW similarity on entity profiles (O(log N) retrieval).

```bash
# Build with entity profiles
doomsummarizer crawl https://docs.example.com --entities

# Entity-enhanced retrieval
doomsummarizer scroll "OpenAI regulation" --debug
# [green]Entity profile HNSW: +3 related (0.85, 0.72 similarity)[/]
```

### Reliability Improvements

- **SQLite Thread Safety** — Semaphore-protected database operations prevent concurrent access issues
- **Improved Error Handling** — Better circuit breaker state management for flaky APIs
- **Budget Service Stability** — Fixed race conditions in usage tracking

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
Query → PromptInterpreter (+ composite query decomposition) → SourceRouter (YAML) → Parallel Fetchers
  → Cache Check (reuse segments for similar queries)
  → URL/Title Dedup → FTS5 KB Enrichment (keyword pre-filter)
  → Document Keyword Profiling (structural weighting: title 4x, headings 3x, intro 2x)
  → ONNX Embeddings (384-dim all-MiniLM-L6-v2)
  → Phase 1 RRF (Lucene BM25F + Freshness + Authority + Quality) → Hard gate (cosine ≥ 0.20) → Discard bottom 25%
  → PRF Centroid Refinement (top-5 embedding average, α=0.7)
  → Phase 2 RRF (+ Query Similarity [max across subqueries] + Vibe Alignment + Quality) → Hard gate (cosine ≥ 0.20)
  → Source Reliability Weights → LFU Diversity Decay → In-Corpus PageRank
  → One-Hop Link Following → TextRank Sentence Extraction
  → Entity Graph Enrichment (HNSW profile similarity or co-occurrence discovery)
  → LLM Synthesis (evidence-grounded, structured for composite queries) or Long-Form Pipeline (blog templates)
```

### Ranking Signals (RRF Fusion)

| Signal | Weight | Phase | Description |
|--------|--------|-------|-------------|
| Lucene BM25F | 1.0 | 1 | Apache Lucene with field weighting: title (2×), keywords (2.5×), content (1×), fuzzy matching |
| Freshness | 0.5 | 1 | Exponential decay (48h half-life) |
| Authority | 0.3 | 1 | Platform score (HN upvotes, etc.) |
| Query Similarity | 0.8 | 2 | Max embedding cosine similarity (across all subqueries for composite queries) |
| Vibe Alignment | 0.4 | 2 | Embedding cosine to vibe |
| Quality | 0.2 | 1+2 | Embedding-based clickbait vs substantive content scoring |
| Entity Profile | 0.3 | 2 | HNSW similarity on entity profiles (when `--entities` enabled) |

### `--no-llm` Mode

Without Ollama, the full signal pipeline still runs: ONNX embeddings, BM25, sentiment, topic inference, RRF ranking, NER (with `--entities`). All signals stored to SQLite.

## LLM Providers

### Local Models — Ollama (Default)

DoomSummarizer runs entirely on local Ollama models by default. No cloud API keys required.

| Role | Default Model | Purpose |
|------|---------------|---------|
| **Main** (synthesis) | `gemma3:4b` | Digests, articles, evidence-grounded answers |
| **Sentinel** (triage) | `qwen3:0.6b` | Query classification, JSON outlines, fast decisions |

Use `benchmark` to find optimal models for your hardware:
```bash
doomsummarizer benchmark "qwen3:4b,gemma3:4b" --pull
```

### Cloud LLMs (Optional — Disabled by Default)

Cloud providers are **disabled by default**. To enable, set an API key **and** set `"enabled": true` in config or user secrets.

| Provider | Models | Key | Docs |
|----------|--------|-----|------|
| Anthropic | Claude Sonnet 4 / Haiku 3.5 | `ANTHROPIC_API_KEY` | [CloudLLM.md](docs/CloudLLM.md) |
| OpenAI | GPT-4o / GPT-4o-mini | `OPENAI_API_KEY` | [CloudLLM.md](docs/CloudLLM.md) |

To enable cloud fallback:
```json
{ "name": "anthropic", "apiKey": "sk-ant-...", "enabled": true }
```

Cloud LLMs offer larger context windows (200K tokens) and higher-quality synthesis. When enabled, they are used as **fallback** when Ollama fails — Ollama remains the primary provider. Budget-controlled with per-service rate limits, retry with backoff, and circuit breakers. See [docs/CloudLLM.md](docs/CloudLLM.md).

## Models & ML Pipeline

DoomSummarizer runs a full ML inference stack locally — no cloud APIs needed for embeddings, NER, or ranking. All ONNX models are quantized (int8) by default for fast CPU inference with minimal accuracy loss.

### Embedding Models (ONNX)

Embeddings power semantic search, similarity scoring, entity profiles, and RRF fusion. Downloaded automatically on first run to `~/.doomsummarizer/models/embeddings/`.

| Model | Dimensions | Max Tokens | Size (quantized) | Notes |
|-------|-----------|------------|-------------------|-------|
| **all-MiniLM-L6-v2** (default) | 384 | 256 | 23 MB | Fast general-purpose. Source: [Xenova/all-MiniLM-L6-v2](https://huggingface.co/Xenova/all-MiniLM-L6-v2) |
| bge-small-en-v1.5 | 384 | 512 | 34 MB | Best quality-for-size. Requires instruction prefix. Source: [Xenova/bge-small-en-v1.5](https://huggingface.co/Xenova/bge-small-en-v1.5) |
| gte-small | 384 | 512 | 34 MB | Good all-around. Source: [Xenova/gte-small](https://huggingface.co/Xenova/gte-small) |
| multi-qa-MiniLM-L6-cos-v1 | 384 | 512 | 23 MB | QA-optimized. Source: [Xenova/multi-qa-MiniLM-L6-cos-v1](https://huggingface.co/Xenova/multi-qa-MiniLM-L6-cos-v1) |
| paraphrase-MiniLM-L3-v2 | 384 | 128 | 17 MB | Smallest & fastest. Source: [Xenova/paraphrase-MiniLM-L3-v2](https://huggingface.co/Xenova/paraphrase-MiniLM-L3-v2) |

Each model downloads three files: `model_quantized.onnx`, `tokenizer.json` (HuggingFace universal format), and `vocab.txt` (fallback). All use BERT-style tokenization with `[CLS]`, `[SEP]`, `[PAD]` special tokens.

Configure in `config.json`:
```json
{ "embedding": { "backend": "onnx", "model": "all-MiniLM-L6-v2" } }
```

**Where embeddings are used:**
- Semantic similarity scoring in RRF fusion (Phase 2)
- Entity profile HNSW search (TF x IDF x confidence weighted entity embeddings)
- PRF centroid refinement (top-5 embedding average, alpha=0.7)
- Evidence assignment in long-form articles (cosine similarity to section themes)
- Deduplication (cosine threshold 0.90 ingestion, 0.90 retrieval)
- Personal corpus gap-filling (semantic match on personal facts)

### Named Entity Recognition (NER) — ONNX

BERT-based NER extracts structured entities from text for the knowledge graph, entity profiles, and gap-filling. Optional — downloaded on demand via `doomsummarizer setup --ner`.

| Component | Details |
|-----------|---------|
| **Model** | [protectai/bert-base-NER-onnx](https://huggingface.co/protectai/bert-base-NER-onnx) (BERT base, cased) |
| **Size** | ~430 MB |
| **Max sequence** | 512 tokens |
| **Tokenizer** | BERT WordPiece (cased), vocab size 28,996 |
| **Confidence threshold** | 0.5 minimum |
| **Storage** | `~/.doomsummarizer/models/ner/` |

**Entity types** (CoNLL-2003 BIO scheme):

| Tag | Type | Examples |
|-----|------|----------|
| `PER` | Person | Politicians, executives, researchers |
| `ORG` | Organization | Companies, agencies, universities |
| `LOC` | Location | Countries, cities, regions |
| `MISC` | Miscellaneous | Events, products, technologies |

**Post-processing pipeline:**
1. WordPiece token merging (handles `##` subword prefixes)
2. BIO tag consolidation (B-ORG + I-ORG → single entity span)
3. Entity reclassification (tech product/company detection)
4. Confidence-based deduplication (keeps highest-scoring mention)
5. Knowledge graph ingestion: entity nodes, co-occurrence edges, TF x IDF x confidence profiles

### Lucene.NET (Full-Text Search)

Not an ML model, but a core ranking component. Per-collection indexes stored at `~/.doomsummarizer/lucene/<collection>/`.

| Feature | Implementation |
|---------|---------------|
| **Scoring** | BM25F with field boosting: title (2x), keywords (2.5x), content (1x) |
| **Stemming** | Porter stemmer ("running" matches "run") |
| **Fuzzy** | Levenshtein distance (~1-2 edits) |
| **Phrase** | Proximity boosting ("machine learning"^3) |
| **Indexing** | Incremental — new items indexed at search time |

### TextRank & Document Profiling

Deterministic text analysis (no ONNX model, pure algorithm):

| Component | Purpose | Method |
|-----------|---------|--------|
| **TextRank** | Sentence extraction / summarization | Graph centrality over sentence similarity |
| **TF-IDF** | Keyword extraction | Structural weighting: title 4x, headings 3x, intro 2x, body 1x |
| **BM25** | Term relevance scoring | Okapi BM25 with k1=1.2, b=0.75 |
| **Freshness** | Temporal decay scoring | Exponential: `exp(-h * ln(2) / halfLife)`, query-type-adaptive half-lives |

### ONNX Runtime Configuration

All ONNX models use Microsoft.ML.OnnxRuntime. Execution provider is configurable:

| Provider | Platform | Notes |
|----------|----------|-------|
| **CPU** (default) | All | Always works, stable |
| **CUDA** | NVIDIA GPU | Requires CUDA runtime installed |
| **DirectML** | Windows GPU | AMD/Intel/NVIDIA, may be unstable on some drivers |
| **Auto** | All | Tries DirectML → CUDA → CPU fallback chain |

### Complete Build — Additional Models

The `doomsummarizer_complete` build includes additional ML models for media processing:

| Model | Purpose | Size | Source |
|-------|---------|------|--------|
| **Whisper** (GGML) | Audio transcription | 75 MB–3 GB (size-dependent) | [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) |
| **ECAPA-TDNN** | Speaker identification | ~100 MB | [Wespeaker/ecapa-tdnn512](https://huggingface.co/Wespeaker/wespeaker-ecapa-tdnn512-LM) |
| **HTDemucs** | Audio source separation | 220 MB | [gentij/htdemucs-ort](https://huggingface.co/gentij/htdemucs-ort) |

These are downloaded on first use and stored in `~/.doomsummarizer/models/`.

### Model Storage Summary

```
~/.doomsummarizer/
├── models/
│   ├── embeddings/
│   │   └── all-MiniLM-L6-v2/        # 23 MB (auto-downloaded on first run)
│   │       ├── model_quantized.onnx
│   │       ├── tokenizer.json
│   │       └── vocab.txt
│   └── ner/                          # 430 MB (downloaded via setup --ner)
│       ├── model.onnx
│       ├── vocab.txt
│       └── config.json
├── lucene/                           # Per-collection Lucene indexes
│   └── <collection>/
├── doom.db                           # SQLite: items, embeddings, metadata
└── vectors.duckdb                    # DuckDB HNSW: entity profile vectors
```

**Minimal setup** (embedding only): ~23 MB model download
**Recommended setup** (embedding + NER): ~453 MB
**Complete build** (all models): ~2.5–5 GB depending on Whisper model size

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
    { "name": "anthropic", "apiKey": "", "enabled": false, "maxRequestsPerDay": 200, "rateLimitMs": 100 },
    { "name": "openai", "apiKey": "", "enabled": false, "maxRequestsPerDay": 200, "rateLimitMs": 100 },
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

770 tests covering ranking pipeline, embeddings, templates, long-form generation, entity disambiguation, prompt interpretation, knowledge graph operations, personal corpus (self-disclosure detection, named corpuses, gap-filling), and retrieval pipeline scoring.

## Platforms

Pre-built binaries: Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64.

## License

MIT
