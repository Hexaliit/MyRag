# DoomSummarizer CLI

Run `doomsummarizer --help` for the command list, and `doomsummarizer <command> --help` for full option help.

## MCP server mode

Run `doomsummarizer --mcp` to start a stdio-based MCP (Model Context Protocol) server for AI agents.

See `docs/MCP.md` for setup and the available tools.

## Commands

### `scroll` — fetch, rank, and synthesize

Generates a digest from a mix of:

- Explicit `-s/--source` values you pass
- Sources inferred from your natural-language prompt
- Search queries inferred from the prompt

Common examples:

```bash
doomsummarizer scroll
doomsummarizer scroll "AI security news" -v snarky
doomsummarizer scroll -s hn -s reddit:dotnet -s bbc -l 40
doomsummarizer scroll --local -s crawl:docs "how does auth work?"
doomsummarizer scroll --list-templates

# Local file ingestion (auto-detected via smart routing)
doomsummarizer "C:\Users\me\invoice.pdf"
doomsummarizer ~/research/paper.docx
doomsummarizer scroll -s ./my-articles/ -n research
doomsummarizer scroll -s report.pdf "what are the key findings?"

# Long-form articles (activates 6-phase evidence-grounded pipeline)
doomsummarizer scroll "AI safety" -t blog-article -o ai-safety.md
doomsummarizer scroll "history of LLMs" -t blog-timeline -o llm-history.md
doomsummarizer scroll "Rust vs Go" -t deep-dive -s hn -s reddit -o comparison.md
doomsummarizer scroll "tech debt" -t problem-solution -o debt.md
doomsummarizer scroll "Kubernetes vs serverless" -t pros-cons -o k8s.md
```

Options (from `doomsummarizer scroll --help`):

- `-v, --vibe <TEXT>`: vibe name from config (e.g. `snarky`) or any custom vibe text
- `-s, --source <SRC>`: repeatable; see `docs/Sources.md`
- `-l, --limit <N>`: max items to fetch (default `30`)
- `-f, --force`: ignore fetch cache + segment reuse
- `--local`: local-only query (no fetching; searches your stored SQLite knowledge base)
- `--no-llm` (alias: `--nollm`): skip LLM calls (still ranks with embeddings/signals)
- `--no-links`: skip one-hop link following enrichment
- `--entities`: enable NER entity extraction
- `--graph`: build/show knowledge graph (DuckDB + HNSW) during the run
- `--json`: emit JSON-formatted digest (for automation)
- `-t, --template <NAME>`: output template name
- `-o, --output <FILE>`: write to file (`.md`, `.txt`, `.html`, `.json`)
- `--raw`: show raw fetched content before processing
- `--images`: render inline images for important items (terminal-dependent)
- `--debug-pipeline` (alias: `--debug`): show scoring + pipeline diagnostics
- `-q, --quiet`: minimal output
- `--list-templates`: print available template names

### `ask` — interactive Q&A over your stored KB

`ask` searches your locally stored items using a three-layer retrieval pipeline (Lucene FTS + embedding HNSW + entity
profiles), then synthesizes answers using the same evidence-grounded pipeline as `scroll`. Multi-turn with conversation
context.

The synthesis pipeline uses smart evidence budgeting (short items donate budget to long ones), TextRank key-sentence
extraction for compression, semantic re-ranking against your query, and full content snippets — not truncated summaries.

Examples:

```bash
doomsummarizer ask "What happened with the SSH vulnerability?"
doomsummarizer ask --source crawl:docs "how does authentication work?"
doomsummarizer ask --source file:my-project "what's the architecture?"
doomsummarizer ask --once "latest AI news"
```

Options:

- `-s, --source <SRC>`: filter evidence to one source (e.g. `crawl:docs`, `file:specs`, `hn`)
- `--days <N>`: search window (default: 30 days; 365 for `crawl:*`/`file:*` sources)
- `--top <N>`: number of evidence items to use (default `10`)
- `--once`: answer once and exit
- `-q, --quiet`: hide evidence panels and show only the answer

Interactive mode meta-commands:

- `sources`: show evidence used for previous answers
- `history`: show prior Q&A turns
- `clear`: reset conversation memory
- `quit` / `exit`: leave

### `crawl` — build a named knowledge base from a URL or local files

Accepts either a **seed URL** (web crawl) or a **local file/directory path** (document ingestion). Web crawls follow
same-domain links, extract readable content, embed it, and store it in SQLite. Local paths ingest PDF, DOCX, Markdown,
HTML, TXT, and PPTX files using the document processing pipeline with adaptive chunking.

Re-crawls are **incremental by default** — the crawler uses HTTP conditional requests (ETag / Last-Modified) and content
hashing to skip unchanged pages.

Examples:

```bash
# Web crawl — auto-names the KB from the domain
doomsummarizer crawl https://docs.example.com

# Named KB with deeper crawl
doomsummarizer crawl https://wiki.local -n wiki --depth 5 --max-pages 500

# Only index pages under /blog/*, with NER entity extraction
doomsummarizer crawl https://blog.example.com -g "/blog/*" --entities

# Force re-process all pages, ignoring cache
doomsummarizer crawl https://docs.example.com --force

# Gentle crawl for external sites
doomsummarizer crawl https://intranet.company.com --delay 1000 --concurrency 1

# Local directory — ingest all supported documents (top-level only)
doomsummarizer crawl C:\docs\project-specs

# Local directory with subdirectories
doomsummarizer crawl /home/user/research --recurse

# Local path + interactive Q&A (ingest first, then ask loop)
doomsummarizer crawl C:\Blog\posts --ask

# Web crawl + interactive Q&A (background crawl + ask loop)
doomsummarizer crawl https://docs.example.com --ask
```

Options:

- `-n, --name <NAME>`: knowledge base name; defaults to a derived domain or directory label
- `-d, --depth <N>`: max link depth for web crawls (default `3`)
- `-m, --max-pages <N>`: max pages to crawl for web crawls (default `200`)
- `-g, --glob <PATTERN>`: URL path filter — only pages matching this pattern are indexed (e.g., `/blog/*`, `/docs/**`).
  Pages outside the filter are still crawled for link discovery but not stored.
- `-f, --force`: re-process all pages/files regardless of cache (default: skip unchanged)
- `--delay <MS>`: politeness delay between requests (default `1000`)
- `--concurrency <N>`: max concurrent requests (default `3`)
- `--entities`: run NER over crawled/ingested content and persist entities to the knowledge graph
- `--ask`: drop into interactive Q&A mode after ingestion (local paths) or during crawl (URLs)
- `-r, --recurse`: recurse into subdirectories when source is a local path (default: top-level only)
- `-q, --quiet`: minimal output

#### Local file ingestion

When the source is a local file or directory, `crawl` runs the document ingestion pipeline:

1. **File discovery** — scans for supported extensions (`.pdf`, `.docx`, `.md`, `.txt`, `.html`, `.pptx`, plus any
   registered processor plugins)
2. **Document type detection** — heuristic classification as Fiction, NonFiction, Academic, Technical, or Unknown (
   affects chunk sizing)
3. **Adaptive chunking** — books use 5000-char chunks for narrative continuity; technical/academic docs use 2000-char
   chunks. PDFs are chunked by page markers; text by headings/paragraphs.
4. **Batch embedding** — all chunks are embedded in a single ONNX batch call (not sequential)
5. **Batch indexing** — all chunks are stored in a single SQLite transaction + Lucene index commit
6. **NER extraction** — optional entity extraction from all chunks (`--entities`)

Source tags for local ingestion use the `file:<name>` prefix (vs `crawl:<name>` for web crawls).

#### `--ask` mode

The `--ask` flag combines ingestion/crawling with interactive Q&A:

- **Local paths**: Ingests files first (fast), then enters the ask loop against the newly-created collection. No
  background task needed.
- **Web URLs**: Starts a background crawl while immediately entering the ask loop. You can ask questions while pages are
  still being crawled. The crawl progress is shown in the ask prompt.

#### Incremental caching (web crawls)

Re-crawls use a two-tier cache to avoid redundant work:

1. **HTTP conditional requests** (fastest): Sends `If-None-Match` / `If-Modified-Since` headers using stored ETags and
   Last-Modified dates. If the server returns `304 Not Modified`, the page body is never transferred.
2. **Content hash fallback**: For servers that don't support ETags, the crawler compares a SHA256 hash of the page
   content against the stored hash. Identical content is skipped.

The summary table after a crawl shows both cache tiers:

```
HTTP 304 (not modified)  │ 42
Content hash match       │ 8
Total cached             │ 50
```

Use `--force` to bypass all caching and re-process every page.

#### Querying crawled/ingested KBs

```bash
# Semantic search over your KB
doomsummarizer scroll --local -s crawl:docs "your query"

# Interactive Q&A
doomsummarizer ask --source crawl:docs "your question"

# For locally ingested files (note: file: prefix)
doomsummarizer ask --source file:project-specs "your question"

# Browse contents
doomsummarizer show docs
doomsummarizer show docs --full
```

### `show` — browse knowledge base collections

Lists all stored collections or inspects a specific one.

```bash
doomsummarizer show                    # List all collections with stats
doomsummarizer show docs               # Items in 'docs' collection
doomsummarizer show docs --full        # With content preview
doomsummarizer show docs -l 100        # Show up to 100 items
```

Options:

- `[name]`: collection name to inspect (omit to list all)
- `-l, --limit <N>`: max items to show (default `50`)
- `--full`: show content preview for each item

### `page` — summarize one URL

Downloads a page, extracts readable content, runs the article pipeline, and optionally produces long-form output (
blog/newsletter templates). The page is saved into SQLite as source `page`.

Examples:

```bash
doomsummarizer page https://example.com/article
doomsummarizer page https://example.com/article --template blog-article -o article.md
doomsummarizer page https://example.com/article --no-llm --raw
```

Options:

- `-v, --vibe <TEXT>`
- `-t, --template <NAME>`: `default`, `blog-article`, `blog-timeline`, `detailed`, `file`, `json` (+ any custom)
- `-o, --output <FILE>`: `.md`, `.txt`, `.html`
- `--raw`: show extracted content before summarization
- `--no-llm` (alias: `--nollm`): skip LLM calls
- `-q, --quiet`

### `sources` — show common source syntax

Prints a quick reference table for the most common `-s/--source` forms. For the full list (including API-backed
providers), see `docs/Sources.md`.

### `config` — view/init config

Examples:

```bash
doomsummarizer config --init
doomsummarizer config --show
```

Config locations:

- Global: `$HOME/.doomsummarizer/config.json`
- Local override: `./doomsummarizer.json`

### `setup` — install models and (optionally) Playwright

Examples:

```bash
doomsummarizer setup
doomsummarizer setup --ner
doomsummarizer setup --playwright
```

Notes:

- `--ner` is required for `--entities` extraction.
- `--playwright` installs Chromium for Playwright-based fetching; at the moment this is only used when a website is
  fetched with `UsePlaywright=true` (there is no direct CLI flag for this yet).

### `benchmark` — compare Ollama models

Examples:

```bash
doomsummarizer benchmark
doomsummarizer benchmark qwen3:4b,gemma3:4b --rounds 3
doomsummarizer benchmark --role sentinel
doomsummarizer benchmark qwen3:4b --pull
```

### `plugin` — manage runtime plugins

Install, enable, disable, and uninstall NuGet-based plugins at runtime.

```bash
# List known shorthands → NuGet package mappings
doomsummarizer plugin shorthands

# Install a plugin (shorthand or full NuGet package ID)
doomsummarizer plugin install plugin-image
doomsummarizer plugin install Mostlylucid.LucidRAG.Plugins.Image --version 1.0.0

# List installed plugins with status
doomsummarizer plugin list

# Enable/disable without uninstalling
doomsummarizer plugin disable plugin-image
doomsummarizer plugin enable plugin-image

# Remove completely
doomsummarizer plugin uninstall plugin-image
```

Plugins are stored in `~/.doomsummarizer/plugins/` and loaded automatically on startup.

### `trends` — sentiment over time (from your stored DB)

Examples:

```bash
doomsummarizer trends
doomsummarizer trends -d 14
```
