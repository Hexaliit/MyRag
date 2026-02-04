# MCP (Model Context Protocol) Server

DoomSummarizer can run as an MCP server so AI agents can query your local knowledge base (KB), explore the entity graph,
and ingest new URLs using structured tools.

## Starting the server

Run:

```bash
doomsummarizer --mcp
```

This starts a **stdio-based** MCP server (the MCP client launches the process and communicates over stdin/stdout). It
uses the same local SQLite database and ONNX embedding model as the CLI.

## Configuration

### Claude Code (`~/.claude.json`)

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

### Claude Desktop (`claude_desktop_config.json`)

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

Notes:

- On Windows, `command` can be `doomsummarizer.exe` (or the full path to it).
- The MCP process will use your normal DoomSummarizer config and data directory (where `doom.db` lives).

## Available tools

All tools return JSON.

### Search

- `search_kb` — Full relevance pipeline: **Lucene pre-filter (LLM-generated query) → BM25F + global IDF → embeddings →
  PRF refinement → RRF**; returns ranked item IDs + metadata.
- `keyword_search` — Fast keyword-only Lucene search (no embeddings).
- `semantic_search` — Pure embedding similarity search (cosine).

### Content

- `get_item_content` — Full content + metadata for an item ID.
- `extract_keywords` — Deterministic keyword extraction from arbitrary text.
- `compare_items` — Compare two items (cosine similarity + keyword overlap).

### Ingestion

- `ingest_url` — Fetch a URL, extract readable content, embed, profile, and index into the KB.

### Collections

- `list_collections` — List collections (sources) and basic stats.
- `get_collection_items` — Paginated browse of a collection.

### Entities / Graph

- `list_entities` — List top entities (filterable by type/recency).
- `get_entity_details` — Entity details + relationships + mentions.
- `get_entity_network` — Multi-hop neighborhood expansion around seed entities.
- `find_related_by_entities` — Find documents related via shared entities.

### Analytics

- `get_kb_stats` — KB overview stats and index status.
- `get_trends` — Topic distribution and sentiment trends over time.

## Example workflows

### Research a topic in your crawled docs

1. `search_kb` with `source="crawl:docs"`
2. `get_item_content` for the most relevant IDs
3. `list_entities` / `get_entity_details` to explore key concepts

### Ingest a page, then verify it was indexed

1. `ingest_url("https://example.com/article")`
2. `search_kb("key phrase from the article")`
3. `get_item_content(<id>)`

## Operational notes

- If embeddings are not set up, some tools will fall back to keyword-only behavior.
- Treat MCP ingestion as “trusted input”: avoid ingesting internal URLs into a shared KB unless you intend to share the
  database.

