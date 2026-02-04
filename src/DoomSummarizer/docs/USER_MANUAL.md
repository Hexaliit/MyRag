# DoomSummarizer User Manual

> Structured reference for AI help systems. Each section is self-contained and can be retrieved independently.

---

## SECTION: Overview

### What is DoomSummarizer?

DoomSummarizer is a command-line tool that aggregates content from multiple sources (Hacker News, Reddit, RSS feeds, web
pages, local files), processes it through an AI pipeline (embeddings, NER, sentiment analysis, topic detection), and
generates ranked, summarized digests. It also builds persistent knowledge bases for Q&A.

### Build Variants

Two binaries are produced from the same codebase. Both have the same commands — the difference is the dependency chain.

| Binary                               | Description                                                                              | Size    | Includes                                                                                                                                                                       |
|--------------------------------------|------------------------------------------------------------------------------------------|---------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **`doomsummarizer`** (slim, default) | The "it just works" version — minimal web-oriented deep research and knowledge base tool | ~76 MB  | Web crawling, KB Q&A, ONNX embeddings, local LLM (LLamaSharp), DuckDB vector search, BM25 full-text, NER, LLM routing, MCP server                                              |
| **`lucidrag`** (complete)            | All the bells and whistles                                                               | ~112 MB | Everything above + all document formats (DOCX, HTML, PPTX), image analysis, YouTube transcription (Whisper), audio analysis, subtitle processing (SRT/VTT/ASS), email delivery |

Build slim: `dotnet publish src/DoomSummarizer/DoomSummarizer.csproj -c Release` (produces `doomsummarizer`)
Build complete: `dotnet publish src/DoomSummarizer/DoomSummarizer.csproj -c Release -p:CompleteBuild=true` (produces
`lucidrag`)

The binary name tells you which variant you're running. All examples in this manual use `doomsummarizer` — substitute
`lucidrag` if using the complete variant.

### Smart Routing

If you run DoomSummarizer with no recognized command, it routes intelligently:

- No arguments: shows help
- A URL: routes to `page` command
- A local file/directory path: routes to `scroll` with `-s` (auto-ingest)
- Anything else: treated as a natural language prompt for `scroll`

---

## SECTION: First-Time Setup

### Command: `setup`

```
doomsummarizer setup [--playwright] [--ner]
```

**What it does:**

1. Creates configuration directory (`~/.doomsummarizer/`)
2. Downloads ONNX embedding model (`all-MiniLM-L6-v2`) for local semantic search
3. Initializes SQLite database
4. Checks Ollama availability and model status
5. Creates templates directory

**Optional flags:**

- `--playwright` — Install Chromium browser for JavaScript-heavy site crawling
- `--ner` — Download BERT-NER model (~430 MB) for named entity extraction
- `--local-llm` — Download local GGUF models for LLamaSharp inference (~2.7 GB)
- `--skip-local-llm` — Skip local LLM model download

**Recommended first run:**

```
doomsummarizer setup --playwright --ner
lucidrag setup --playwright --ner
```

### Prerequisites

- **.NET 10 runtime** (or use self-contained binary from Releases)

### LLM Provider Defaults

The two variants default to different LLM providers:

|                    | `doomsummarizer`                                        | `lucidrag`                                                  |
|--------------------|---------------------------------------------------------|-------------------------------------------------------------|
| **Default**        | LLamaSharp (local GGUF — zero-config, no server needed) | Ollama (local server at localhost:11434)                    |
| **Setup**          | Auto-downloads GGUF models (~2.7 GB)                    | Does not download GGUF models (use `--local-llm` to opt in) |
| **Fallback chain** | LLamaSharp → Ollama → Cloud                             | LLamaSharp → Ollama → Cloud                                 |

**`doomsummarizer`** works out of the box after `setup` — no Ollama or API keys needed. To use Ollama instead, install
it and start it; it will be detected automatically.

**`lucidrag`** expects Ollama to be running. Install from https://ollama.com, then pull models:

```
ollama serve
ollama pull gemma3:4b
ollama pull qwen3:0.6b
```

To use local GGUF models with `lucidrag` instead: `lucidrag setup --local-llm`

---

## SECTION: Configuration

### Command: `config`

```
doomsummarizer config [--init] [--full] [--show] [--reference]
```

| Flag            | Effect                                                   |
|-----------------|----------------------------------------------------------|
| `--init`        | Create starter config at `~/.doomsummarizer/config.json` |
| `--init --full` | Create complete config with every setting                |
| `--show`        | Display current effective config (default)               |
| `--reference`   | Print full YAML reference with all available options     |

### Config File Locations (highest priority wins)

1. `./doomsummarizer.json` — Project-specific (current directory)
2. `~/.doomsummarizer/config.json` — User config (home directory)
3. Embedded defaults — Ships with the binary

Deep merge: object properties merge recursively; scalars and arrays replace entirely.

### Key Configuration Sections

#### Ollama (Local LLM)

```json
{
  "ollama": {
    "baseUrl": "http://localhost:11434",
    "model": "gemma3:4b",
    "sentinelModel": "qwen3:0.6b",
    "temperature": 0.4,
    "timeoutSeconds": 300,
    "contextSize": 8192
  }
}
```

- `model` — Primary synthesis model (generates summaries)
- `sentinelModel` — Fast triage model (planning, filtering, quality checks)

#### Embedding

```json
{
  "embedding": {
    "backend": "onnx",
    "model": "all-MiniLM-L6-v2",
    "similarityThreshold": 0.95
  }
}
```

Backends: `onnx` (local, no API key), `ollama`

#### Storage

```json
{
  "storage": {
    "dbPath": "~/.doomsummarizer/doom.db",
    "retentionDays": 30
  }
}
```

#### Sources

```json
{
  "sources": {
    "hacker_news": { "enabled": true, "sections": ["top", "best", "new"], "maxStories": 30, "minScore": 50 },
    "reddit": { "enabled": true, "subreddits": ["programming", "csharp"], "sort": "hot", "maxPosts": 25 }
  }
}
```

#### Source Filtering and Weighting

```json
{
  "sourceFilter": {
    "allowedDomains": [],
    "blockedDomains": ["example.com"],
    "weights": { "reuters": 1.4, "bbc": 1.3, "arxiv": 1.3, "reddit": 0.9 }
  }
}
```

Weights multiply RRF scores: >1.0 boosts, <1.0 penalizes, 1.0 neutral.

#### API Keys (Cloud LLM providers)

```json
{
  "keys": [
    {
      "name": "anthropic",
      "apiKey": "${DOOM_ANTHROPIC}",
      "enabled": true,
      "maxRequestsPerDay": 100,
      "dailyBudgetUsd": 2.0
    }
  ]
}
```

Supported providers: `anthropic`, `openai`, `google_search`, `brave_search`, `newsapi`, `tavily`, `jina`, `serper`,
`duckduckgo`, `newsdata`, `currents`, `google_places`.

**Best practice:** Set API keys via environment variables (`DOOM_ANTHROPIC`, `DOOM_OPENAI`, etc.), not in config files.

#### Global API Budget

```json
{
  "apiBudget": {
    "globalMaxRequestsPerDay": 500,
    "globalDailyBudgetUsd": 2.0
  }
}
```

#### Vibes (Synthesis Tone)

Built-in vibes: `neutral`, `doom`, `hopeful`, `snarky`, `funny`, `upbeat`, `friendly`, `toon`

Custom vibes can be added in config:

```json
{
  "vibes": {
    "academic": "Write in formal academic style with citations"
  }
}
```

#### Email (Newsletter Delivery)

```json
{
  "email": {
    "provider": "smtp",
    "enabled": false,
    "fromAddress": "",
    "toAddresses": "",
    "subjectTemplate": "Doom Scroll Digest — {{DATE}}",
    "smtp": { "host": "smtp.gmail.com", "port": 587, "useSsl": true }
  }
}
```

#### Link Following

```json
{
  "linkFollowing": {
    "enabled": true,
    "maxLinksPerArticle": 3,
    "maxTotalLinks": 15,
    "blockedDomains": ["facebook.com", "twitter.com", "youtube.com"]
  }
}
```

---

## SECTION: scroll Command

### Usage

```
doomsummarizer scroll [prompt] [options]
```

The primary command. Fetches content from configured sources, ranks it, and generates a digest summary.

### Arguments

| Argument   | Description                                                                     |
|------------|---------------------------------------------------------------------------------|
| `[prompt]` | Natural language prompt (e.g., "summarize bbc and hacker news about AI") or URL |

### Options

| Flag                        | Description                                                                                  | Default     |
|-----------------------------|----------------------------------------------------------------------------------------------|-------------|
| `-s\|--source`              | Sources: `hn`, `reddit`, `search:query`, URL, or local file path (repeatable)                | All enabled |
| `-l\|--limit`               | Maximum items to fetch                                                                       | 30          |
| `-v\|--vibe`                | Output tone: neutral, doom, hopeful, snarky, funny, upbeat, friendly, toon, or custom text   | neutral     |
| `-o\|--output`              | Write output to file (.md, .txt, .html, .json)                                               | stdout      |
| `-t\|--template`            | Output template name                                                                         | default     |
| `-q\|--quiet`               | Minimal console output                                                                       | false       |
| `--json`                    | Output as structured JSON (for LLM tool consumption)                                         | false       |
| `--graph`                   | Enable knowledge graph build and display                                                     | false       |
| `--images`                  | Display inline ASCII art images for important items                                          | false       |
| `--local`                   | Query ONLY local knowledge base — no fetching                                                | false       |
| `--no-llm\|--nollm`         | Skip LLM summarization (still runs embeddings, sentiment, topic inference)                   | false       |
| `--no-links`                | Skip one-hop link following                                                                  | false       |
| `--no-entities`             | Disable NER entity extraction                                                                | false       |
| `--raw`                     | Show raw extracted content before LLM processing                                             | false       |
| `-f\|--force`               | Ignore cache and re-process all content                                                      | false       |
| `--full`                    | Show full diagnostic output: startup panel, status lines, NER, decomposer, evidence briefing | false       |
| `--briefing`                | Show evidence briefing panel with themes, entities, and coverage metrics                     | false       |
| `--debug\|--debug-pipeline` | Show detailed pipeline diagnostics: RRF scores, discards, salience                           | false       |
| `--model`                   | Override LLM model for generation (e.g., qwen3:8b)                                           | from config |
| `--sentinel-model`          | Override sentinel LLM model                                                                  | from config |
| `--parallel`                | Enable parallel section generation for long-form articles                                    | true        |
| `--locale`                  | Locale for date/number parsing (e.g., en-gb, de-de)                                          | en-us       |
| `--email`                   | Send digest via email                                                                        | false       |
| `--email-to`                | Override email recipients                                                                    | from config |
| `--list-templates`          | List available output templates                                                              | false       |
| `--clear-storage`           | Delete all cached data and exit                                                              | false       |
| `--ee\|--easter-egg`        | Play the DoomSummarizer animation                                                            | false       |
| `-n\|--name`                | Named knowledge base collection                                                              | none        |

### Examples

```bash
# Quick tech news digest
doomsummarizer scroll "tech news today"

# Specific sources with doom vibe
doomsummarizer scroll -s hn -s reddit --vibe doom

# Save to file
doomsummarizer scroll "AI developments" -o digest.md

# JSON output for tooling
doomsummarizer scroll "security news" --json --limit 10

# Local-only query (no network)
doomsummarizer scroll "what did I read about rust" --local

# No LLM (just fetch, rank, extract)
doomsummarizer scroll -s hn --no-llm --limit 5

# Full diagnostic output
doomsummarizer scroll "tech" --full --debug

# With inline images
doomsummarizer scroll "tech news" --images

# Custom source URL
doomsummarizer scroll -s "https://news.ycombinator.com/best"

# Local file ingestion (directory)
doomsummarizer scroll -s ./my-articles/

# Local file ingestion (single file — auto-detected via smart routing)
doomsummarizer "C:\Users\me\invoice.pdf"
doomsummarizer "/home/me/thesis.docx"
```

### Local File Ingestion via scroll

When you pass a local file or directory path — either as the argument or via `-s` — scroll auto-ingests the content into
a named knowledge base collection before running the retrieval pipeline.

**How it works:**

1. **Smart routing** — Running `doomsummarizer "/path/to/file.pdf"` auto-detects the path is a file and routes to
   `scroll -s "/path/to/file.pdf"`
2. **Auto-naming** — The collection name is derived from the filename or directory (e.g., `invoice.pdf` → collection
   `invoice-pdf`). Override with `-n/--name`.
3. **Document processing** — Files are processed through the full pipeline: format extraction → document type
   detection → adaptive chunking → batch embedding → indexing → NER
4. **Retrieval** — After ingestion, scroll runs the standard retrieval pipeline against the newly-created collection and
   generates an LLM summary
5. **Persistence** — The ingested content is stored in SQLite with source tag `file:<name>`, so subsequent `ask` queries
   can access it

**Supported formats:**

| Format                                   | Slim (`doomsummarizer`) | Complete (`lucidrag`) |
|------------------------------------------|:-----------------------:|:---------------------:|
| Markdown (`.md`)                         |           Yes           |          Yes          |
| Plain text (`.txt`)                      |           Yes           |          Yes          |
| PDF (`.pdf`)                             |           Yes           |          Yes          |
| Word (`.docx`)                           |           Yes           |          Yes          |
| HTML (`.html`)                           |           Yes           |          Yes          |
| PowerPoint (`.pptx`)                     |            -            |          Yes          |
| Images (`.jpg`, `.png`, `.gif`, `.webp`) |            -            |          Yes          |
| Plugin formats (`.srt`, `.vtt`, etc.)    |            -            |          Yes          |

**Examples:**

```bash
# Ingest and summarize a single PDF
doomsummarizer "C:\Users\scott\invoice.pdf"

# Ingest a directory of research papers
doomsummarizer -s ~/papers/ -n research

# Ingest files, then ask follow-up questions
doomsummarizer crawl ~/project-docs --ask

# Query previously ingested files
doomsummarizer ask -s file:invoice-pdf "what are the line items?"
doomsummarizer ask -s file:research "summarize the findings"
```

**Difference between `scroll -s file` and `crawl file`:**

|                     | `scroll -s /path` or `doomsummarizer /path` | `crawl /path`                               |
|---------------------|---------------------------------------------|---------------------------------------------|
| **Primary purpose** | Ingest + immediate summary                  | Ingest into persistent KB                   |
| **After ingestion** | Runs retrieval pipeline → LLM synthesis     | Shows stats, optionally enters `--ask` loop |
| **Best for**        | Quick "what's in this file?" answers        | Building a corpus for repeated Q&A          |
| **Re-run behavior** | Re-ingests (unless already cached)          | Incremental (skips unchanged files)         |

### Extending with Plugins

The `plugin` command lets you install additional format support and data sources from NuGet at runtime — no rebuild
needed.

```bash
# List known plugin shorthands
doomsummarizer plugin shorthands

# Install a plugin (by shorthand or NuGet package ID)
doomsummarizer plugin install plugin-image
doomsummarizer plugin install Acme.CustomSource --version 2.1.0

# List installed plugins
doomsummarizer plugin list

# Disable/enable without uninstalling
doomsummarizer plugin disable plugin-image
doomsummarizer plugin enable plugin-image

# Remove completely
doomsummarizer plugin uninstall plugin-image
```

**Available plugin shorthands:**

| Shorthand          | Package                                  | Adds                            |
|--------------------|------------------------------------------|---------------------------------|
| `plugin-image`     | `Mostlylucid.LucidRAG.Plugins.Image`     | Image analysis (ML vision, OCR) |
| `plugin-audio`     | `Mostlylucid.LucidRAG.Plugins.Audio`     | Audio transcription & analysis  |
| `plugin-video`     | `Mostlylucid.LucidRAG.Plugins.Video`     | Video processing                |
| `plugin-books`     | `Mostlylucid.LucidRAG.Plugins.Books`     | Long-form book processing       |
| `plugin-data`      | `Mostlylucid.LucidRAG.Plugins.Data`      | CSV, Excel, Parquet profiling   |
| `plugins-complete` | `Mostlylucid.LucidRAG.Plugins.Complete`  | All plugins in one package      |
| `source-imap`      | `Mostlylucid.DoomSummarizer.Source.Imap` | Email inbox as data source      |

**How plugins work:**

1. NuGet package is downloaded and extracted to `~/.doomsummarizer/plugins/`
2. On startup, plugin DLLs are loaded and scanned for `ISourcePlugin`, `IProcessorPlugin`, or `ICliPlugin`
   implementations
3. Source plugins add new `-s <key>` data sources
4. Processor plugins add document format support (extensions registered automatically)
5. CLI plugins contribute additional commands to the CLI

**Writing your own plugin:**

A processor plugin is a class implementing `IProcessorPlugin` in a NuGet-packaged .NET library:

```csharp
public sealed class MyPlugin : IProcessorPlugin
{
    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "my-plugin",
        DisplayName = "My Document Processor",
        SupportedExtensions = [".xyz"],
        DocumentTypes = ["custom"]
    };

    public bool CanProcess(string markdown, ProcessingContext context)
        => context.FileName != null &&
           Metadata.SupportedExtensions.Contains(
               Path.GetExtension(context.FileName).ToLowerInvariant());

    public Task<ProcessorResult> ProcessAsync(
        string markdown, ProcessorOptions options, CancellationToken ct)
    {
        // Your processing logic
        return Task.FromResult(new ProcessorResult { ... });
    }
    // ... remaining interface members
}
```

Reference `DoomSummarizer.Core` (or the NuGet `Mostlylucid.LucidRAG.DoomSummarizer.Core`) for the plugin interfaces.
Publish to NuGet, then install with `doomsummarizer plugin install Your.Package.Id`.

### Pipeline Stages

1. **Source Fetching** — Parallel fetch from configured sources
2. **Content Extraction** — SmartReader/Mozilla Readability HTML → text
3. **Link Following** — One-hop enrichment of top articles
4. **Embedding** — ONNX all-MiniLM-L6-v2 vector generation
5. **Deduplication** — Near-duplicate detection via cosine similarity
6. **Sentiment Analysis** — Per-item positive/neutral/negative scoring
7. **Topic Detection** — Category inference from content
8. **RRF Ranking** — Reciprocal Rank Fusion combining: Lucene FTS, semantic similarity, freshness, authority, quality
9. **NER** — Named entity extraction (person, organization, location)
10. **LLM Synthesis** — Sentinel planning → parallel section generation → assembly

### Output Modes

- **Console Panel** (default) — Word-wrapped markdown in a bordered panel
- **JSON** (`--json`) — Structured data with metadata, facts, themes, sentiment, sources
- **Evidence Briefing** (`--full` or `--briefing`) — Color-coded themes, entity tables, coverage metrics
- **Email** (`--email`) — HTML newsletter delivery via SMTP or SendGrid
- **File** (`-o path`) — Markdown, text, HTML, or JSON to disk

---

## SECTION: crawl Command

### Usage

```
doomsummarizer crawl <source> [options]
```

Crawls a website to build a persistent, searchable knowledge base with incremental updates.

### Arguments

| Argument   | Description                           |
|------------|---------------------------------------|
| `<source>` | Seed URL or local file/directory path |

### Options

| Flag              | Description                                               | Default       |
|-------------------|-----------------------------------------------------------|---------------|
| `-d\|--depth`     | Maximum crawl depth from seed URL                         | 3             |
| `-m\|--max-pages` | Maximum pages to crawl                                    | 200           |
| `--delay`         | Minimum delay between requests in ms (adaptive)           | 1000          |
| `--concurrency`   | Maximum concurrent requests (hard cap: 5)                 | 3             |
| `-g\|--glob`      | URL path filter (e.g., `/blog/*`, `/docs/*`)              | all paths     |
| `--no-entities`   | Disable NER entity extraction                             | false         |
| `-f\|--force`     | Re-process all pages regardless of cache                  | false         |
| `--ask`           | Drop to interactive Q&A mode while crawling in background | false         |
| `-r\|--recurse`   | Recurse into subdirectories (local paths only)            | false         |
| `-q\|--quiet`     | Minimal console output                                    | false         |
| `-n\|--name`      | Named knowledge base collection                           | auto from URL |

### Examples

```bash
# Crawl documentation site
doomsummarizer crawl https://docs.example.com --depth 5

# Crawl only blog posts
doomsummarizer crawl https://example.com -g "/blog/*" --max-pages 100

# Crawl and immediately start asking questions
doomsummarizer crawl https://docs.example.com --ask

# Force re-crawl (ignore cache)
doomsummarizer crawl https://example.com -f

# Crawl local markdown files
doomsummarizer crawl ./documentation/ -r

# YouTube URL (Complete build only)
doomsummarizer crawl https://www.youtube.com/watch?v=VIDEO_ID
```

### Incremental Updates

Crawl uses ETag and content-hash caching. Re-running `crawl` on the same URL only processes changed pages. Use `-f` to
force full re-crawl.

### YouTube Support (`lucidrag` Only)

In the `lucidrag` (complete) build, `crawl` detects YouTube URLs and extracts:

- Video metadata (title, author, channel, duration)
- Subtitles/transcript
- Audio transcription via Whisper (if subtitles unavailable)

The `doomsummarizer` (slim) build does not support YouTube URLs.

---

## SECTION: ask Command

### Usage

```
doomsummarizer ask [question] [options]
```

Interactive Q&A over your stored knowledge base. Answers are grounded in evidence from indexed content.

### Options

| Flag           | Description                                      | Default                   |
|----------------|--------------------------------------------------|---------------------------|
| `[question]`   | Initial question (enters interactive mode after) | none                      |
| `-s\|--source` | Filter to source(s) (repeatable)                 | all                       |
| `--days`       | How far back to search                           | 30 (general), 365 (crawl) |
| `--top`        | Number of evidence items to use                  | 10                        |
| `--once`       | Answer once and exit (no interactive loop)       | false                     |
| `-q\|--quiet`  | Minimal output                                   | false                     |
| `-n\|--name`   | Named KB collection                              | none                      |

### Examples

```bash
# Start interactive Q&A
doomsummarizer ask

# Ask a single question
doomsummarizer ask "What were the main AI developments this week?" --once

# Filter to crawled docs
doomsummarizer ask -s crawl:docs "How does authentication work?"

# Limit evidence to recent articles
doomsummarizer ask --days 7 "trending topics"
```

---

## SECTION: page Command

### Usage

```
doomsummarizer page <url> [options]
```

Download and summarize a single web page.

### Options

| Flag             | Description                  | Default  |
|------------------|------------------------------|----------|
| `<url>`          | URL of the page to summarize | required |
| `-v\|--vibe`     | Output tone                  | neutral  |
| `-o\|--output`   | Write to file                | stdout   |
| `-t\|--template` | Output template              | default  |
| `-q\|--quiet`    | Minimal output               | false    |
| `--no-llm`       | Skip LLM summarization       | false    |
| `--raw`          | Show raw extracted content   | false    |

### Examples

```bash
# Summarize a page
doomsummarizer page https://example.com/article

# Save summary to file
doomsummarizer page https://example.com/article -o summary.md

# Snarky summary
doomsummarizer page https://example.com/article --vibe snarky
```

---

## SECTION: man Command

### Usage

```
doomsummarizer man [question] [options]
```

Built-in manual: Q&A about DoomSummarizer itself. Auto-downloads documentation from GitHub and indexes it.

### Options

| Flag            | Description                           | Default |
|-----------------|---------------------------------------|---------|
| `[question]`    | Question about DoomSummarizer         | none    |
| `--refresh`     | Re-download and re-index the manual   | false   |
| `--load-manual` | Load manual without asking a question | false   |
| `--top`         | Number of evidence items              | 8       |
| `--once`        | Answer once and exit                  | false   |
| `-q\|--quiet`   | Minimal output                        | false   |

### Examples

```bash
# Ask about a feature
doomsummarizer man "how do I configure email?"

# Refresh the manual index
doomsummarizer man --refresh

# Interactive manual Q&A
doomsummarizer man
```

---

## SECTION: Other Commands

### show — Collection Inspector

```
doomsummarizer show [collection] [--limit N] [--full]
```

Lists all knowledge base collections with stats, or inspects a specific collection's documents.

### list — Content Browser

```
doomsummarizer list docs [--query Q] [--source S] [--entity E] [--limit N]
doomsummarizer list segments [--topic T] [--limit N]
doomsummarizer list entities [--type TYPE] [--limit N]
```

Browse documents, segments, and entities in the knowledge base.

### sources — Source Registry

```
doomsummarizer sources
```

Displays available sources (built-in plugins + crawled collections) with examples.

### trends — Analytics

```
doomsummarizer trends [--days N]
```

Sentiment and topic trend analysis over configurable time period (default: 7 days).

### benchmark — Model Testing

```
doomsummarizer benchmark [models...] [--role synthesis|sentinel|both] [--rounds N] [--pull]
```

Benchmarks Ollama models for speed (tokens/second) and quality. Use `--pull` to auto-download models.

### plugin — Extension Manager

```
doomsummarizer plugin install <package> [--version V]
doomsummarizer plugin uninstall <package>
doomsummarizer plugin list
doomsummarizer plugin enable|disable <key>
doomsummarizer plugin shorthands
```

Manage source and output plugins. Plugins are NuGet packages loaded from `~/.doomsummarizer/plugins/`.

---

## SECTION: MCP Server Mode

### Usage

```
doomsummarizer --mcp
```

Launches DoomSummarizer as an MCP (Model Context Protocol) server over StdIO. This exposes the knowledge base to AI
agents and tools.

### Available Tools (15)

| Tool                       | Category     | Description                                                |
|----------------------------|--------------|------------------------------------------------------------|
| `search_kb`                | Search       | Full relevance pipeline (Lucene + embeddings + RRF fusion) |
| `keyword_search`           | Search       | Fast Lucene-only search with stemming and fuzzy matching   |
| `semantic_search`          | Search       | Embedding cosine similarity search                         |
| `get_item_content`         | Content      | Full text + summary + keywords + metadata by item ID       |
| `extract_keywords`         | Content      | Structure-weighted keyword extraction (no LLM)             |
| `compare_items`            | Content      | Cosine similarity + Jaccard index between two items        |
| `ingest_url`               | Ingestion    | Fetch URL → extract → embed → store in KB                  |
| `list_collections`         | Collections  | Summary of all source collections                          |
| `get_collection_items`     | Collections  | Items from specific source                                 |
| `list_entities`            | Entity Graph | Top entities with mention counts and freshness             |
| `get_entity_details`       | Entity Graph | Entity relationships and mentioning articles               |
| `get_entity_network`       | Entity Graph | Multi-hop BFS traversal (up to depth 3)                    |
| `find_related_by_entities` | Entity Graph | Discover documents sharing entities                        |
| `get_kb_stats`             | Analytics    | Knowledge base overview and diagnostics                    |
| `get_trends`               | Analytics    | Topic distribution and sentiment trends                    |

### Integration Example (Claude Desktop)

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

---

## SECTION: Troubleshooting

### Problem: "No items found" or empty output

**Causes:**

- No sources configured or all disabled
- Network connectivity issues
- Source APIs returning empty results (e.g., HN min_score too high)

**Fixes:**

- Run `doomsummarizer sources` to see what's available
- Try with explicit source: `doomsummarizer scroll -s hn`
- Lower limits: `--limit 5`
- Check config: `doomsummarizer config --show`

### Problem: No LLM summary generated

**Causes:**

- Ollama not running
- Model not pulled
- API keys not configured

**Fixes:**

- Check Ollama: `curl http://localhost:11434/api/tags`
- Pull model: `ollama pull gemma3:4b`
- Run setup: `doomsummarizer setup`
- Use `--no-llm` flag to skip summarization and still get indexed content

### Problem: "ONNX model not found"

**Cause:** Embedding model not downloaded during setup.

**Fix:** Run `doomsummarizer setup` — it downloads `all-MiniLM-L6-v2` automatically.

### Problem: JavaScript-heavy sites return empty content

**Cause:** Default HTTP fetcher can't execute JavaScript.

**Fix:** Install Playwright: `doomsummarizer setup --playwright`. Crawl will auto-detect JS-heavy pages and use
Playwright.

### Problem: crawl keeps re-processing unchanged pages

**Cause:** ETag/hash caching may be stale or site doesn't support ETags.

**Fix:** This is expected for some sites. Use `--delay` to be respectful. Use `-g` glob patterns to limit scope.

### Problem: YouTube URL not working

**Cause:** The `doomsummarizer` (slim) build doesn't include YouTube support.

**Fix:** Use the `lucidrag` binary instead, which includes YouTube transcription and audio analysis.

### Problem: Entity extraction not working

**Cause:** NER model not downloaded.

**Fix:** Run `doomsummarizer setup --ner` to download the BERT-NER model.

### Problem: High memory usage

**Causes:**

- Large knowledge base with many embeddings
- Multiple concurrent crawl operations
- TorchSharp/ML.NET loaded (`lucidrag` / complete build)

**Fixes:**

- Reduce `retentionDays` in config
- Use `doomsummarizer` (slim) if YouTube/audio not needed
- Limit crawl concurrency: `--concurrency 1`

### Problem: API rate limits or budget exceeded

**Causes:**

- Too many requests to paid APIs
- Daily budget cap reached

**Fixes:**

- Check budget: review `apiBudget` config section
- Set per-API limits in `keys` config
- Use local Ollama to avoid cloud API costs
- Circuit breaker will auto-pause after consecutive failures

### Problem: Config not loading

**Fix:** Run `doomsummarizer config --show` to see which config files are loaded and their priority order. The output
shows exactly which file each setting comes from.

---

## SECTION: Source Reference

### Built-in Sources

| Key            | Description                          | Auth Required  |
|----------------|--------------------------------------|----------------|
| `hn`           | Hacker News (top, best, new stories) | No             |
| `reddit`       | Reddit (configured subreddits)       | No             |
| `bbc`          | BBC News RSS feeds                   | No             |
| `gnews`        | Google News RSS                      | No             |
| `search:QUERY` | Web search via configured search API | Depends on API |
| `URL`          | Any web URL                          | No             |
| Local path     | File or directory on disk            | No             |

### Crawl Collections

After crawling a site, it appears as a source:

```bash
doomsummarizer ask -s crawl:docs.example.com "how does X work?"
doomsummarizer scroll -s crawl:my-collection --local
```

### Plugin Sources

Additional sources installable via `doomsummarizer plugin install`:

- Academic paper search
- Science journal feeds
- Reference/encyclopedia
- UK Government publications
- YouTube (`lucidrag` only)
- Google Custom Search
- News API aggregators

---

## SECTION: Output Templates

Templates control the format and structure of generated summaries. Located in `~/.doomsummarizer/templates/`.

### Listing Templates

```
doomsummarizer scroll --list-templates
```

### Using Templates

```
doomsummarizer scroll -t email "tech news"
doomsummarizer scroll -t briefing "security updates"
```

### Custom Templates

Place custom template files in `~/.doomsummarizer/templates/`. Templates support variable substitution: `{{DATE}}`,
`{{QUERY}}`, `{{CONTENT}}`.

---

## SECTION: Data Storage

### Database

SQLite database at `~/.doomsummarizer/doom.db` (configurable via `storage.dbPath`).

Contains:

- Crawled page content and metadata
- Content segments with embeddings
- Entity graph (nodes + relationships)
- Query history and circuit breaker state
- Lucene FTS index

### Vector Store

DuckDB with VSS extension for persistent vector embeddings. Enables semantic search across all indexed content.

### Image Cache

Temporary image cache at `%TEMP%/doomsummarizer/images/`. Auto-cleaned after 1 hour.

### Clearing Data

```
doomsummarizer scroll --clear-storage
```

Deletes all cached segments, queries, and entities.

---

## SECTION: Environment Variables

| Variable             | Purpose                      |
|----------------------|------------------------------|
| `DOOM_ANTHROPIC`     | Anthropic API key            |
| `DOOM_OPENAI`        | OpenAI API key               |
| `DOOM_GOOGLE_SEARCH` | Google Custom Search API key |
| `DOOM_BRAVE_SEARCH`  | Brave Search API key         |
| `DOOM_NEWSAPI`       | NewsAPI.org key              |
| `DOOM_TAVILY`        | Tavily AI Search key         |
| `DOOM_JINA`          | Jina API key                 |
| `DOOM_SENDGRID`      | SendGrid API key (email)     |
| `DOOM_SERPER`        | Serper API key               |
| `DOOM_NEWSDATA`      | NewsData.io key              |
| `DOOM_CURRENTS`      | Currents API key             |

---

## SECTION: Glossary

| Term                    | Definition                                                                                                                             |
|-------------------------|----------------------------------------------------------------------------------------------------------------------------------------|
| **RRF**                 | Reciprocal Rank Fusion — combines multiple ranking signals (text match, semantic similarity, freshness, authority) into a single score |
| **NER**                 | Named Entity Recognition — extracts people, organizations, and locations from text                                                     |
| **Sentinel Model**      | A smaller, faster LLM used for planning and quality checks before the main synthesis model runs                                        |
| **Vibe**                | A personality/tone preset that controls how summaries are written                                                                      |
| **Knowledge Base (KB)** | The persistent local store of all indexed content, embeddings, and entity relationships                                                |
| **ONNX**                | Open Neural Network Exchange — format used for the local embedding model (no API key needed)                                           |
| **MCP**                 | Model Context Protocol — standard for exposing tools to AI agents                                                                      |
| **Decomposer**          | Component that breaks complex queries into sub-questions for better evidence retrieval                                                 |
| **Circuit Breaker**     | Fault tolerance pattern that pauses API calls after consecutive failures                                                               |
| **Slim Build**          | Default binary without YouTube/audio/subtitle processing (~163 MB)                                                                     |
| **Complete Build**      | Full binary with all features including YouTube/Whisper/subtitles (~248 MB)                                                            |
