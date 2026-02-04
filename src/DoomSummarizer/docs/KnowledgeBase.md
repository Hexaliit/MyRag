# DoomSummarizer as a Local Knowledge Base

DoomSummarizer is "secretly" a console-first knowledge base system:

- It *collects* content from the web (`scroll`, `page`, `crawl`)
- It *stores* the raw content, summaries, embeddings, entities, and usage data locally (SQLite)
- It *retrieves* relevant evidence later (`ask`, `scroll --local`)
- It can optionally *materialize structure* (NER + a small knowledge graph)

## Storage Architecture

### SQLite Database

By default, data is stored in `$HOME/.doomsummarizer/doom.db` (configurable via `storage.dbPath`).

**Core tables:**

| Table                  | Purpose                                                                     |
|------------------------|-----------------------------------------------------------------------------|
| `items`                | Articles: title, URL, content, summary, sentiment, topic, 384-dim embedding |
| `items_fts`            | SQLite FTS5 index (legacy, lightweight backup for KB enrichment)            |
| `keyword_corpus`       | Global term frequencies for proper IDF computation                          |
| `entities`             | NER-extracted entities (people, organizations, locations)                   |
| `entity_mentions`      | Entity-to-article provenance links                                          |
| `entity_relationships` | Co-occurrence edges between entities                                        |
| `url_cache`            | HTTP ETags, Last-Modified, content hashes for conditional fetching          |
| `query_log`            | Query embeddings for segment reuse                                          |
| `feature_cache`        | Disambiguation feature embeddings                                           |
| `daily_stats`          | Per-day sentiment/topic statistics                                          |

### DuckDB Vector Store

When `--graph` is enabled, a separate DuckDB database at `$HOME/.doomsummarizer/vectors.duckdb` stores:

- Item embeddings with HNSW index for fast similarity search
- Entity nodes and co-occurrence relationships
- In-corpus PageRank scores for authority ranking

### Lucene Index

A Lucene index is maintained at `$HOME/.doomsummarizer/lucene/<collection>/` for full-text search:

- Complex boolean queries with fuzzy matching
- Porter stemming for term expansion ("running" matches "run")
- Field-weighted search (title boosted)
- LLM-generated query optimization (converts natural language to Lucene syntax)

Lucene is the primary search engine for KB queries (`--local`, `--name`), providing sophisticated text search with
Porter stemming, fuzzy matching, and field-weighted search. The sentinel LLM generates optimized Lucene queries from
natural language. Results are fused with embedding similarity for hybrid retrieval.

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

### `crawl` stores a site or local files

Use this for docs/wiki/intranet knowledge bases, or for ingesting local files:

```bash
# Web crawl
doomsummarizer crawl https://docs.example.com --name docs --depth 4 --max-pages 800

# Only index blog posts, extract named entities
doomsummarizer crawl https://blog.example.com -g "/blog/*" --entities

# Force full re-crawl (ignoring cache)
doomsummarizer crawl https://docs.example.com --force

# Local file/directory ingestion
doomsummarizer crawl C:\docs\project-specs
doomsummarizer crawl /home/user/research --recurse
doomsummarizer crawl C:\Blog\posts --ask   # Ingest + interactive Q&A
```

**Source tags**:

- Web crawls: `crawl:<name>` (e.g., `crawl:docs`)
- Local ingestion: `file:<name>` (e.g., `file:project-specs`)

**Supported local formats**: PDF, DOCX, Markdown, HTML, TXT, PPTX, plus any registered processor plugins.

**Document type detection**: The ingestion pipeline classifies documents as Fiction, NonFiction, Academic, Technical, or
Unknown using heuristic scoring (chapter markers, dialogue patterns, abstract/keywords sections, code blocks, ISBN
markers). This affects chunk sizing — books use 5000-char chunks for narrative continuity while technical documents use
2000-char chunks.

**Batch processing**: Local ingestion is optimized with batch ONNX embedding (single forward pass for all chunks) and
batch SQLite indexing (single transaction for all items).

Re-crawling (web) is **incremental by default**: the crawler sends HTTP conditional request headers (`If-None-Match` /
`If-Modified-Since`) using stored ETags and Last-Modified dates. Pages that return `304 Not Modified` skip downloading
entirely. For servers without ETag support, a SHA256 content hash fallback detects unchanged pages after download. Local
ingestion checks if the collection already has items and skips re-ingestion unless `--force` is used.

## Querying Your KB

### `scroll --local`

`--local` disables fetching and searches only your stored items:

```bash
doomsummarizer scroll --local "oauth token exchange"
doomsummarizer scroll --local -s crawl:docs "rate limits"
doomsummarizer scroll --local "API security" --debug  # Show scoring details
```

### `scroll --name <source>`

Query a specific named collection (crawl or other source):

```bash
doomsummarizer scroll "authentication" --name docs
doomsummarizer scroll "rate limiting" --name wiki --debug
```

### `ask`

Interactive "retrieve + answer" interface with conversation context:

```bash
doomsummarizer ask --source crawl:docs "how does authentication work?"
doomsummarizer ask --source file:my-project "what's the architecture?"
doomsummarizer ask --days 7 "what happened this week?"
doomsummarizer ask --once "latest security news"  # Single answer, no loop
```

How it behaves:

- Computes an embedding for your question
- Runs three-layer retrieval in parallel: Lucene FTS + embedding HNSW + entity profiles (via `Task.WhenAll`)
- Re-ranks evidence using batch cosine similarity against your query
- Synthesizes an answer using `SynthesizeSummaryAsync` — the same pipeline as `scroll`:
    - Smart evidence budgeting (short items donate surplus to long ones)
    - TextRank key-sentence extraction for compressing long evidence
    - Full content snippets (not truncated summaries)
    - Conversation history folded into the query for multi-turn context
- If no LLM is available, shows the evidence items directly
- If your query is ambiguous (multiple entities), it can prompt you to pick which cluster you meant

### `crawl --ask`

Combines ingestion with interactive Q&A:

```bash
# Local: ingest files first, then ask loop
doomsummarizer crawl C:\docs\specs --ask

# Web: background crawl + ask loop (ask while crawling)
doomsummarizer crawl https://docs.example.com --ask
```

## Retrieval Pipeline

When you query the KB, DoomSummarizer uses a **three-layer retrieval pipeline**. Layers 1 and 2 execute in parallel via
`Task.WhenAll` for lower latency.

### Layer 1: Lucene.NET Full-Text Search

DoomSummarizer uses [Lucene.NET](https://lucenenet.apache.org/) (Apache Lucene ported to .NET) instead of SQLite FTS5 as
the primary search engine. Why Lucene over FTS5?

| Feature         | Lucene.NET                                 | SQLite FTS5              |
|-----------------|--------------------------------------------|--------------------------|
| Field weighting | BM25F: title 2x, keywords 2.5x, content 1x | Equal weight all columns |
| Stemming        | Porter stemmer ("running" → "run")         | No stemming              |
| Fuzzy matching  | Edit distance (`languge~` → "language")    | No fuzzy                 |
| Phrase boosting | `"machine learning"^3`                     | No phrase boost          |
| Query syntax    | Full boolean + proximity + wildcards       | Simple prefix/AND/OR     |

The sentinel LLM converts natural language queries into optimized Lucene syntax:

```
Query: "history of LLMs"
        │
        ▼
   Sentinel LLM generates Lucene query:
   "title:history OR title:LLM OR content:language content:model"
        │
        ▼
   Lucene search on:
   - Document title (boosted 2x)
   - Extracted keywords (boosted 2.5x)
   - Content (1x)
        │
        ▼
   30 docs → ~10-15 candidates
```

Each collection gets its own Lucene index at `$HOME/.doomsummarizer/lucene/<collection>/`. SQLite FTS5 is retained as a
lightweight backup for KB enrichment pre-filtering.

### Layer 2: Embedding HNSW Similarity

384-dim all-MiniLM-L6-v2 embeddings with cosine similarity. For composite queries, items are scored against the
best-matching subquery (max-sim). Runs concurrently with Layer 1.

### Layer 3: Entity Profile HNSW (optional)

With `--entities`, documents get entity profile embeddings (TF-IDF-confidence weighted). HNSW search finds related
documents in O(log N).

### RRF Fusion

Reciprocal Rank Fusion combines signals from all layers:

| Signal               | Weight | Purpose                                                 |
|----------------------|--------|---------------------------------------------------------|
| BM25F                | 1.0    | Keyword relevance (title 2x, keywords 2.5x, content 1x) |
| Embedding similarity | 0.8    | Semantic matching ("pharmaceutical" ↔ "drug")           |
| Freshness            | 0.5    | Recency boost (configurable decay)                      |
| Authority            | 0.3    | In-corpus PageRank, source quality                      |
| Quality              | 0.2    | Content vs clickbait (anchor-based)                     |
| Vibe alignment       | 0.4    | Tone matching (doom, hopeful, etc.)                     |
| Entity profile       | 0.3    | Entity fingerprint similarity (when `--entities`)       |

### Graph Enrichment (optional)

With `--entities --graph`, entity co-occurrence discovers related documents:

```bash
doomsummarizer scroll "OpenAI regulation" --entities --graph --debug
```

This finds documents sharing entities with top results (e.g., "Sam Altman" appears in both AI policy and business
articles).

## Keyword Profiling

Each document is automatically profiled with structurally-weighted keywords:

| Zone             | Weight | Why                                            |
|------------------|--------|------------------------------------------------|
| Title            | 4.0x   | Author's chosen label — strongest topic signal |
| Headings (H1-H6) | 3.0x   | Section topics summarize document themes       |
| Intro paragraphs | 2.0x   | Opening sets context and thesis                |
| Body text        | 1.0x   | Baseline — lots of supporting detail           |

Keywords are stored in the `items.keywords` column and indexed in Lucene for fast pre-filtering.

## Global IDF

Term importance is computed from the **full corpus**, not per-query batches:

```sql
-- keyword_corpus table
SELECT keyword, document_count FROM keyword_corpus ORDER BY document_count DESC;
```

This ensures rare terms (like "CVE-2024-1234") get appropriately high weights even in small query batches.

## Entities and Knowledge Graph

### NER Entity Extraction (`--entities`)

DoomSummarizer extracts named entities using a local BERT-based ONNX model:

```bash
# First-time setup downloads the NER model (~400MB)
doomsummarizer setup --ner

# Extract entities during scroll
doomsummarizer scroll "ai regulation" --entities --debug

# Extract entities while crawling
doomsummarizer crawl https://docs.example.com --entities
```

**Entity types:**

| Type   | Examples                  | Uses                                 |
|--------|---------------------------|--------------------------------------|
| `PER`  | "Sam Altman", "Elon Musk" | Person tracking, biography queries   |
| `ORG`  | "OpenAI", "Microsoft"     | Company news, organization queries   |
| `LOC`  | "San Francisco", "EU"     | Regional filtering, location queries |
| `MISC` | "GPT-4", "GDPR"           | Product/concept tracking             |

Entities are stored in the `entities` table with mention provenance in `entity_mentions`.

### Knowledge Graph (`--graph`)

Builds a DuckDB-based graph for advanced retrieval:

```bash
doomsummarizer scroll "ai regulation" --entities --graph
```

**Graph components:**

1. **Entity nodes** — Unique entities with embeddings
2. **Co-occurrence edges** — Entities that appear together in articles
3. **Item embeddings** — HNSW-indexed for fast similarity search
4. **PageRank scores** — Authority ranking within the corpus

**Graph-enhanced queries:**

```bash
# Find articles connected by shared entities
doomsummarizer scroll "OpenAI" --graph --debug

# Story connections show entity overlap between articles
doomsummarizer scroll "AI regulation" --entities --graph --briefing
```

### Entity Disambiguation

When your query is ambiguous (e.g., "Apple" could mean the company or fruit), `ask` can prompt you to clarify:

```bash
doomsummarizer ask "what's happening with Apple?"
# → Detected multiple entity clusters:
#   1. Apple Inc (technology, iPhone, Mac)
#   2. Apple (fruit, agriculture, recipes)
# Select cluster: _
```

This uses embedding similarity between your query and stored entity contexts.

## Temporal Queries

DoomSummarizer understands temporal expressions in queries and filters results accordingly:

```bash
# These queries filter to recent content
doomsummarizer scroll "AI news today"
doomsummarizer scroll "what happened this week in tech?"
doomsummarizer scroll "breaking security news"
doomsummarizer scroll "updates since yesterday"

# Cloud LLMs are especially good at temporal understanding
export ANTHROPIC_API_KEY="..."
doomsummarizer scroll "LLM news since last week" --debug
```

**Temporal detection uses:**

1. **Sentinel LLM extraction** — The classification LLM parses natural language dates
2. **Microsoft.Recognizers.Text** — Deterministic confirmation of date/time expressions
3. **Year extraction heuristic** — Detects years in article titles (e.g., "Study from 2020")

**Temporal signals in SentinelIntent:**

| Field              | Values                               | Effect                |
|--------------------|--------------------------------------|-----------------------|
| `time_sensitivity` | `today`, `breaking`, `week`, `any`   | Freshness boost       |
| `requires_fresh`   | `true`                               | Skip cache, fetch new |
| `date_range`       | `{ start: "2024-01-15", end: null }` | Filter to date range  |

Use `--debug` to see temporal extraction in action:

```bash
doomsummarizer scroll "recent court cases in Australia" --debug
# Temporal: requires_fresh=false, time_sensitivity=week, date_range=null
# → Freshness multiplier applied to items older than 7 days
```

## Caching and Performance

### URL Cache (HTTP-aware)

Stores ETags, Last-Modified headers, and SHA256 content hashes:

```bash
# First crawl: downloads all pages
doomsummarizer crawl https://docs.example.com --name docs

# Re-crawl: uses conditional requests (304 Not Modified skips download)
doomsummarizer crawl https://docs.example.com --name docs

# Force full re-crawl (ignores cache)
doomsummarizer crawl https://docs.example.com --name docs --force
```

### Query Segment Reuse

If a new query is very similar to a recent query, DoomSummarizer reuses stored evidence:

```bash
# First query: fetches and processes
doomsummarizer scroll "AI security news"

# Similar query: reuses segments (faster)
doomsummarizer scroll "artificial intelligence security"
```

### Feature Cache

Speeds up entity disambiguation in `ask` by caching embedding computations.

### Forcing Fresh Data

```bash
doomsummarizer scroll "your query" --force      # Ignore scroll cache
doomsummarizer crawl https://example.com --force # Re-process all crawled pages
```

## Debug Mode

Use `--debug` to see the full retrieval pipeline in action:

```bash
doomsummarizer scroll "machine learning security" --debug
```

**Debug output shows:**

- Sentinel intent classification (categories, tone, temporal)
- Recognizer signals (dates, numbers, URLs detected)
- NER entities extracted from query
- Lucene pre-filter candidates
- Phase 1 scoring (BM25, freshness, authority, similarity)
- RRF fusion weights
- Phase 2 full scoring
- Final ranked results

This is invaluable for understanding why certain results rank higher than others.

