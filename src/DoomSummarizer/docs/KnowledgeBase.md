# DoomSummarizer as a local knowledge base

DoomSummarizer is “secretly” a console-first knowledge base system:
- It *collects* content from the web (`scroll`, `page`, `crawl`)
- It *stores* the raw content, summaries, embeddings, entities, and usage data locally (SQLite)
- It *retrieves* relevant evidence later (`ask`, `scroll --local`)
- It can optionally *materialize structure* (NER + a small knowledge graph)

## What gets stored (SQLite)

By default, runs write into `$HOME/.doomsummarizer/doom.db` (configurable).

Stored data includes (high-level):
- Items: title/url/source/content/summary/sentiment/topic/embedding
- URL cache: ETags/content hashes/last-modified to avoid re-fetching unchanged pages
- Query log: embeddings of queries for segment reuse (“similar query” shortcuts)
- Trends tables: per-day sentiment/topic statistics
- Entity tables: extracted entities + mentions + co-occurrence edges
- Feature cache: embeddings of disambiguation features used by `ask`

Retention:
- `scroll` runs `CleanupOldDataAsync(retentionDays)` based on `storage.retentionDays`

## Building your KB

### `scroll` stores what it fetches

Run `scroll` regularly to accumulate an evidence corpus:

```bash
doomsummarizer scroll "security news"
doomsummarizer scroll -s hn -s reddit:netsec
```

### `page` stores a single page

Useful for “put this URL into my KB and summarize it”:

```bash
doomsummarizer page https://example.com/incident-report --template detailed
```

### `crawl` stores a site as `crawl:<name>`

Use this for docs/wiki/intranet knowledge bases:

```bash
doomsummarizer crawl https://docs.example.com --name docs --depth 4 --max-pages 800

# Only index blog posts, extract named entities
doomsummarizer crawl https://blog.example.com -g "/blog/*" --entities

# Force full re-crawl (ignoring cache)
doomsummarizer crawl https://docs.example.com --force
```

The stored `source` value for crawled pages is `crawl:docs` (or whatever name you used).

Re-crawling is **incremental by default**: the crawler sends HTTP conditional request headers (`If-None-Match` / `If-Modified-Since`) using stored ETags and Last-Modified dates. Pages that return `304 Not Modified` skip downloading entirely. For servers without ETag support, a SHA256 content hash fallback detects unchanged pages after download.

## Querying your KB

### `scroll --local`

`--local` disables fetching and instead searches your stored items using embeddings (plus metadata filters like `--source`).

```bash
doomsummarizer scroll --local "oauth token exchange"
doomsummarizer scroll --local -s crawl:docs "rate limits"
```

### `ask`

`ask` is the interactive “retrieve + answer” interface:

```bash
doomsummarizer ask --source crawl:docs "how does authentication work?"
```

How it behaves:
- Computes an embedding for your question
- Retrieves top evidence from SQLite (and can reuse near-identical cached queries)
- If an LLM is available, it writes an evidence-grounded answer; otherwise it can still show the evidence
- If your query is ambiguous (multiple entities), it can prompt you to pick which cluster you meant

## Entities and graphs

### NER (`--entities`)

When enabled:
- DoomSummarizer runs a local BERT NER model (downloaded via `setup --ner`)
- Extracted entities are stored in SQLite for later analysis and disambiguation

Examples:

```bash
doomsummarizer scroll "ai regulation" --entities
doomsummarizer crawl https://docs.example.com --entities
```

### Knowledge graph (`--graph`)

When enabled on a `scroll` run, DoomSummarizer also maintains a DuckDB-based graph at:
- `$HOME/.doomsummarizer/vectors.duckdb`

This graph stores:
- Item embeddings (HNSW) for similarity search
- Entity nodes + mentions + co-occurrence relationships

Example:

```bash
doomsummarizer scroll "ai regulation" --entities --graph
```

## Caching and "why it feels fast"

Several caches work together:
- **URL cache (HTTP-aware)**: stores ETags, Last-Modified headers, and SHA256 content hashes per URL. On re-crawl or re-fetch, the crawler sends conditional HTTP headers (`If-None-Match` / `If-Modified-Since`) — if the server returns `304 Not Modified`, the page body is never transferred. For servers that don't support conditional requests, the content hash detects unchanged pages after download.
- **Segment reuse**: if a new query is very similar to a recent query, DoomSummarizer can reuse stored evidence instead of refetching
- **Feature cache**: speeds up entity disambiguation in `ask`

If you want to force a full refetch/re-process, use:

```bash
doomsummarizer scroll "your query" --force      # Ignore scroll cache
doomsummarizer crawl https://example.com --force # Re-process all crawled pages
```

