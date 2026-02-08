# lucidRESEARCH

Fullscreen terminal UI and MCP server for managing autonomous research sessions powered by the [UltraResearch](../Mostlylucid.LucidRAG.UltraResearch/README.md) engine.

lucidRESEARCH provides two modes:

- **TUI mode** (default) — interactive terminal dashboard for starting, monitoring, and controlling research sessions with live metrics and convergence tracking.
- **MCP server mode** (`--mcp`) — exposes UltraResearch tools to AI agents via the [Model Context Protocol](https://modelcontextprotocol.io).

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- **Optional**: [Ollama](https://ollama.ai) running locally for LLM-powered sentinel evaluation (falls back to structural-only mode without it)
- **Optional**: `SEMANTIC_SCHOLAR_API_KEY` environment variable for dedicated Semantic Scholar rate limits

## Installation

```bash
# Build from source
dotnet build src/LucidResearch/LucidResearch.csproj

# Run TUI
dotnet run --project src/LucidResearch/LucidResearch.csproj

# Run MCP server
dotnet run --project src/LucidResearch/LucidResearch.csproj -- --mcp
```

## TUI Usage

The TUI provides five views, navigated with function keys:

| Key | View | Description |
|-----|------|-------------|
| `F1` | Dashboard | Live metrics, convergence sparkline, activity log |
| `F2` | New Research | Start a new research session with topic and parameters |
| `F3` | Frontier | Priority-ranked paper candidates queued for fetching |
| `F4` | Checkpoints | Sentinel evaluation history with gap analysis |
| `F5` | Sessions | Active session details, stop/clear controls |
| `q` | — | Quit the application |

### Dashboard

Displays real-time metrics updated every 500ms:

- **Metrics panel**: Status, topic, iteration count, papers fetched/ingested/failed, frontier size, seen IDs, entity count, orphan citations
- **Convergence panel**: NewInfoRatio with progress bar and sparkline chart showing convergence history
- **Activity log**: Recent session events (last 10 entries)

### Starting a Research Session

Press `F2` to open the session form:

| Field | Description | Default |
|-------|-------------|---------|
| Topic | Research topic query (required) | — |
| Max Papers | Maximum papers to fetch before stopping | 200 |
| Batch Size | Papers to fetch per iteration | 10 |
| Max Iterations | Maximum main loop iterations | 50 |
| Seed arXiv IDs | Comma-separated starting papers (e.g., `1706.03762,1810.04805`) | — |
| arXiv Categories | Comma-separated category filters (e.g., `cs.CL,cs.AI`) | All |
| Collection Name | Override auto-generated name | `ultraresearch-{topic}-{date}` |

### Frontier View

Shows the top 50 candidates ordered by composite priority score (0-1):

- **Priority**: Weighted score from citation count (40%), entity overlap (25%), sentinel boost (20%), recency (15%)
- **Type**: `arxiv` or `doi`
- **Source**: How the candidate was discovered (Search, Citation, SemanticScholar, Orphan, Sentinel)
- **Citations**: Citation count from Semantic Scholar

### Session Management

Press `F5` to view session details and controls:

- **Stop Session**: Gracefully stop the running session (finishes current operation)
- **Clear Session**: Reset the dashboard and clear the active session
- Displays elapsed time, success rate, and stop reason

## MCP Server Mode

Run with `--mcp` to start as a Model Context Protocol server over stdio:

```bash
dotnet run --project src/LucidResearch/LucidResearch.csproj -- --mcp
```

### Claude Desktop Configuration

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "lucidresearch": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/LucidResearch/LucidResearch.csproj", "--", "--mcp"],
      "env": {
        "SEMANTIC_SCHOLAR_API_KEY": "your-key-here"
      }
    }
  }
}
```

Or if using a published binary:

```json
{
  "mcpServers": {
    "lucidresearch": {
      "command": "/path/to/lucidresearch",
      "args": ["--mcp"]
    }
  }
}
```

### Available MCP Tools

#### Session Management

| Tool | Description |
|------|-------------|
| `ultraresearch_start` | Start an autonomous research session. Returns a session ID for monitoring. |
| `ultraresearch_stop` | Gracefully stop a running session. |
| `ultraresearch_status` | Get current session status: progress, frontier size, checkpoints. |
| `ultraresearch_list_sessions` | List all sessions started via MCP in this process. |

#### Analysis

| Tool | Description |
|------|-------------|
| `ultraresearch_frontier` | Get frontier candidates ordered by priority with scores and metadata. |
| `ultraresearch_checkpoints` | Get sentinel checkpoint history with convergence metrics and gap analysis. |
| `ultraresearch_stats` | Get aggregate statistics: timing, paper counts, success rates. |

### Example MCP Interaction

```
User: Research "retrieval augmented generation" and find the key papers

Agent: [calls ultraresearch_start with topic="retrieval augmented generation", maxPapers=100]
       Session abc123 started.

Agent: [calls ultraresearch_status with sessionId="abc123"]
       Status: Running, Iteration: 3, Papers fetched: 28, Frontier: 142

Agent: [calls ultraresearch_checkpoints with sessionId="abc123"]
       Checkpoint at iteration 3: NewInfoRatio=0.45, Gaps: ["No papers on multi-hop RAG"]

Agent: [calls ultraresearch_frontier with sessionId="abc123", limit=10]
       Top candidates: 1. "Self-RAG" (priority=0.85), 2. "RAPTOR" (priority=0.78)...
```

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=lucidresearch.db"
  },
  "DocSummarizer": {
    "EmbeddingBackend": "Onnx",
    "BertRag": {
      "VectorStore": "InMemory"
    }
  }
}
```

### Environment Variables

| Variable | Description |
|----------|-------------|
| `SEMANTIC_SCHOLAR_API_KEY` | Dedicated Semantic Scholar rate limit (1 req/sec vs shared pool) |

### Logging

Logs are written to `logs/lucidresearch-{date}.log` via Serilog (stdout is reserved for the TUI). In MCP mode, logs go to stderr.

## Architecture

```
lucidresearch [--mcp]
  │
  ├── TUI Mode (default)
  │   ├── ResearchApp          → XenoAtom Terminal.UI layout
  │   ├── AppState             → Reactive state (State<T> bindings)
  │   ├── StatePoller          → 500ms background polling
  │   └── Views/               → Dashboard, StartResearch, Frontier, Checkpoints, Sessions
  │
  └── MCP Mode (--mcp)
      └── UltraResearchTools   → 7 MCP tools over stdio
          │
          ▼
      UltraResearchOrchestrator (shared engine)
          ├── ResearchPaperFetcher      → arXiv + Semantic Scholar + CrossRef
          ├── ResearchFrontierManager   → Priority queue with composite scoring
          ├── ResearchSentinelEvaluator  → LLM checkpoint with structural fallback
          └── VenueQualityScorer        → Venue tier scoring for RRF
```

### Data Storage

- **SQLite** (`lucidresearch.db`): Session metadata and collection entities
- **Paper files**: `%APPDATA%/lucidrag/ultraresearch/{id}.md` — Markdown with YAML frontmatter
- **Logs**: `logs/lucidresearch-{date}.log`

## Related Documentation

- [UltraResearch Engine](../Mostlylucid.LucidRAG.UltraResearch/README.md) — NuGet package README
- [UltraResearch Architecture](../../docs/ULTRARESEARCH.md) — Detailed architecture guide
- [lucidRAG Project](../../README.md) — Parent project overview

## License

MIT
