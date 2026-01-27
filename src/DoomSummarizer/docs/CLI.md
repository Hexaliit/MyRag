# DoomSummarizer CLI

Run `doomsummarizer --help` for the command list, and `doomsummarizer <command> --help` for full option help.

## Commands

### `scroll` — fetch, rank, and synthesize

Generates a digest from a mix of:
- Explicit `-s/--source` values you pass
- Sources inferred from your natural-language prompt
- Search queries inferred from the prompt

Common examples:

```bash
doomsummarizer scroll
doomsummarizer scroll "AI security news" --vibe snarky
doomsummarizer scroll -s hn -s reddit:dotnet -s bbc --limit 40
doomsummarizer scroll --local -s crawl:docs "how does auth work?"
doomsummarizer scroll --list-templates
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

`ask` searches your locally stored items using embeddings, then answers using an LLM when available (Ollama or cloud). It can also disambiguate ambiguous entity queries by clustering evidence.

Examples:

```bash
doomsummarizer ask "What happened with the SSH vulnerability?"
doomsummarizer ask --source crawl:docs "how does authentication work?"
doomsummarizer ask --once "latest AI news"
```

Options:
- `-s, --source <SRC>`: filter evidence to one source (e.g. `crawl:docs`, `hn`, `reddit`)
- `--days <N>`: search window (default: 30 days; 365 for `crawl:*` sources)
- `--top <N>`: number of evidence items to use (default `10`)
- `--once`: answer once and exit
- `-q, --quiet`: hide evidence panels and show only the answer

Interactive mode meta-commands:
- `sources`: show evidence used for previous answers
- `history`: show prior Q&A turns
- `clear`: reset conversation memory
- `quit` / `exit`: leave

### `crawl` — build a named knowledge base from a website

Crawls same-domain links from a seed URL, extracts readable content, embeds it, and stores it in SQLite under a `crawl:<name>` source.

Examples:

```bash
doomsummarizer crawl https://docs.example.com --name docs
doomsummarizer crawl https://wiki.local -n wiki --depth 5 --max-pages 500
doomsummarizer crawl https://intranet.company.com --entities
```

Options:
- `-n, --name <NAME>`: knowledge base name; defaults to a derived domain label
- `-d, --depth <N>`: max link depth (default `3`)
- `-m, --max-pages <N>`: max pages to crawl (default `200`)
- `--delay <MS>`: politeness delay between requests (default `500`)
- `--concurrency <N>`: max concurrent requests (default `3`)
- `--entities`: run NER over crawled pages and store entity text into summaries
- `-q, --quiet`: minimal output

Query a crawl KB:
- `doomsummarizer scroll --local -s crawl:docs "your query"`
- `doomsummarizer ask --source crawl:docs "your question"`

### `page` — summarize one URL

Downloads a page, extracts readable content, runs the article pipeline, and optionally produces long-form output (blog/newsletter templates). The page is saved into SQLite as source `page`.

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

Prints a quick reference table for the most common `-s/--source` forms. For the full list (including API-backed providers), see `docs/Sources.md`.

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
- `--playwright` installs Chromium for Playwright-based fetching; at the moment this is only used when a website is fetched with `UsePlaywright=true` (there is no direct CLI flag for this yet).

### `benchmark` — compare Ollama models

Examples:

```bash
doomsummarizer benchmark
doomsummarizer benchmark qwen3:4b,gemma3:4b --rounds 3
doomsummarizer benchmark --role sentinel
doomsummarizer benchmark qwen3:4b --pull
```

### `trends` — sentiment over time (from your stored DB)

Examples:

```bash
doomsummarizer trends
doomsummarizer trends --days 14
```
