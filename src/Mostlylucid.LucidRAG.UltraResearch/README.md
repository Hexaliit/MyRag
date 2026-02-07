# Mostlylucid.LucidRAG.UltraResearch

NuGet: `Mostlylucid.LucidRAG.UltraResearch`

Autonomous research corpus builder for [LucidRAG](https://github.com/scottgal/lucidrag). A long-running agentic loop that discovers, fetches, and indexes academic papers using citation graph topology, entity clusters, and LLM sentinel checkpoints to steer exploration.

Unlike simple paper fetchers that retrieve N results and stop, UltraResearch uses its own **growing index** to decide what to fetch next. It continuously analyzes what it has already ingested — orphan citations, foundational references, entity coverage gaps, temporal holes — and steers itself toward the most informative papers. The result is a live, chat-ready research corpus.

## How It Works

```
User: "ultraresearch: attention mechanisms in transformers"
  │
  ▼
UltraResearchOrchestrator (agentic loop)
  │
  ├── 1. SEARCH ──► arXiv + Semantic Scholar keyword search
  │
  ├── 2. FETCH ───► ar5iv full text, CrossRef metadata, S2 enrichment
  │                  → saved as .md files with YAML frontmatter
  │
  ├── 3. INGEST ──► Full RAG pipeline (chunk → embed → entity extract → graph → link extract)
  │
  ├── 4. ANALYZE ─► Citation graph orphans, foundational refs, entity coverage, temporal gaps
  │
  ├── 5. SENTINEL ► LLM evaluates corpus "shape" → identifies conceptual gaps → suggests queries
  │
  └── 6. STEER ──► Update frontier priorities, loop back to step 1
  │
  ▼
Live Collection → Chat via existing LucidRAG conversation pipeline
```

## Installation

```bash
dotnet add package Mostlylucid.LucidRAG.UltraResearch
```

Requires the following peer dependencies (from the LucidRAG ecosystem):

- `Mostlylucid.LucidRAG.DoomSummarizer.Core` — ArxivFetcher, CitationResolver, AcademicPatterns, OllamaService, SemanticScholarClient
- `LucidRAG.Core` — RagDocumentsDbContext, CitationGraphQueries, CollectionEntity

## CLI Usage

```bash
lucidrag ultraresearch "attention mechanisms in transformers" \
    --max-papers 200 \
    --max-hours 4 \
    --collection "transformer-attention" \
    --seed-paper 1706.03762 \
    --categories cs.CL,cs.AI \
    --verbose
```

### Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `<topic>` | | Research topic query (required positional argument) | |
| `--max-papers` | `-m` | Maximum papers to fetch before stopping | 200 |
| `--max-hours` | | Maximum wall-clock duration in hours | 4 |
| `--collection` | `-c` | Collection name (auto-generated if omitted) | `ultraresearch-{topic}-{date}` |
| `--seed-paper` | `-s` | Seed arXiv IDs or DOIs (repeatable) | None |
| `--categories` | | arXiv categories to filter (comma-separated) | All |
| `--no-s2` | | Disable Semantic Scholar (arXiv-only mode) | false |
| `--dry-run` | | Search + discover candidates without ingesting | false |
| `--verbose` | `-v` | Detailed output | false |

### Examples

```bash
# Basic: explore a topic with defaults
lucidrag ultraresearch "graph neural networks"

# Seeded: start from a known foundational paper
lucidrag ultraresearch "transformers" --seed-paper 1706.03762 --seed-paper 1810.04805

# Scoped: restrict to specific arXiv categories
lucidrag ultraresearch "reinforcement learning" --categories cs.LG,cs.AI --max-papers 100

# Quick scout: dry-run to see what's discoverable
lucidrag ultraresearch "federated learning" --dry-run --max-papers 50

# Focused: small corpus, short time budget
lucidrag ultraresearch "sparse attention" --max-papers 30 --max-hours 1 --collection sparse-attn
```

## Programmatic Usage

### Service Registration

```csharp
// Register UltraResearch services in your DI container
services.AddUltraResearch(options =>
{
    // Optional: set Semantic Scholar API key for dedicated rate limit
    options.SemanticScholarApiKey = config["SemanticScholarApiKey"];
});
```

### Starting a Session (Orchestrator)

```csharp
var orchestrator = services.GetRequiredService<UltraResearchOrchestrator>();

var config = new UltraResearchConfig
{
    Topic = "attention mechanisms in transformers",
    MaxPapers = 200,
    MaxDuration = TimeSpan.FromHours(4),
    SeedArxivIds = ["1706.03762"],
    ArxivCategories = ["cs.CL", "cs.AI"],
    IncludeSemanticScholar = true
};

// IDocumentIngester bridges to your ingestion pipeline
var sessionId = await orchestrator.StartAsync(config, ingester);

// Stream progress
await foreach (var progress in orchestrator.StreamProgress(sessionId)!)
{
    Console.WriteLine($"[{progress.Stage}] {progress.Message} " +
                      $"(fetched: {progress.PapersFetched}, frontier: {progress.FrontierSize})");
}

// Check final status
var state = orchestrator.GetStatus(sessionId);
Console.WriteLine($"Done: {state?.PapersIngested} papers, reason: {state?.StopReason}");
```

### Implementing IDocumentIngester

The `IDocumentIngester` interface decouples UltraResearch from the specific ingestion environment:

```csharp
public class MyIngester : IDocumentIngester
{
    public async Task<DocumentIngestResult> IngestAsync(
        string filePath, Guid collectionId, CancellationToken ct)
    {
        // Your ingestion logic here (chunk, embed, entity extract, etc.)
        return new DocumentIngestResult(Success: true, Message: "OK", DocumentId: docId, SegmentCount: 42);
    }
}
```

The CLI provides `CliDocumentIngester` (wraps `CliDocumentProcessor`). The web app would wrap `DocumentProcessingQueue`.

## Architecture

### Component Overview

| Component | Responsibility |
|-----------|----------------|
| `UltraResearchOrchestrator` | Main agentic loop, session lifecycle, state persistence, convergence |
| `ResearchPaperFetcher` | Multi-source paper acquisition (arXiv, S2, CrossRef), .md file creation |
| `ResearchFrontierManager` | Priority queue with weighted scoring from citation graph signals |
| `ResearchSentinelEvaluator` | LLM checkpoint for gap analysis with structural-only fallback |
| `SemanticScholarClient` | Semantic Scholar Academic Graph API client (in DoomSummarizer.Core) |
| `IDocumentIngester` | Abstraction for environment-specific ingestion (CLI / web) |

### Priority Scoring

The frontier manager assigns a composite priority score (0-1) to each candidate paper:

| Signal | Weight | Description |
|--------|--------|-------------|
| Cited-by count | 0.40 | Normalized citation count from Semantic Scholar or citation graph |
| Entity overlap | 0.25 | Source-based proxy: orphan citations (0.8), direct citations (0.6), S2 (0.4), sentinel (0.7) |
| Sentinel boost | 0.20 | Title keyword overlap with sentinel-suggested topics and gaps |
| Recency | 0.15 | Year extracted from arXiv ID (2024+ = 1.0, 2022-24 = 0.8, down to 0.2 for pre-2014) |

### Convergence Detection

Three independent signals trigger session completion:

1. **New information declining**: `newInfoRatio < convergenceThreshold` for 3 consecutive sentinel checkpoints (default threshold: 0.15)
2. **Frontier exhausted**: No candidates remain AND sentinel suggests no new queries AND topic variations yield nothing
3. **Budget exhausted**: Paper count, wall-clock time, or iteration limit reached

### State Persistence & Crash Recovery

`UltraResearchState` is serialized to `CollectionEntity.Settings` as JSON after every iteration:

- `SeenIds` (`HashSet<string>`) prevents re-fetching on resume
- `Frontier` (`List<FetchCandidate>`) preserves prioritized candidates
- `Checkpoints` (`List<SentinelCheckpoint>`) preserves convergence history
- At 10K+ seen IDs, state JSON is ~200KB — acceptable for SQLite/PostgreSQL

Resume a crashed or stopped session:

```csharp
var sessionId = await orchestrator.ResumeAsync(collectionId, ingester);
```

### Sentinel Evaluator

The sentinel runs every N papers (default: 5) and evaluates the corpus "shape":

**Inputs** (structured summary, NOT full paper text):
- Top 20 entities by type (algorithm, dataset, framework, etc.)
- Citation graph topology: orphan count, foundational refs
- Year histogram of ingested papers
- Search query history (avoids repeats)
- Convergence metrics from last 3 checkpoints

**Output** (JSON):
```json
{
  "newInfoRatio": 0.12,
  "identifiedGaps": ["No papers on federated learning applications"],
  "suggestedQueries": ["federated learning transformer attention"],
  "shouldContinue": true,
  "reasoning": "Corpus covers core attention mechanisms but lacks applied variants"
}
```

**Graceful degradation**: When no LLM is available (`OllamaService` is null or unreachable), the sentinel runs in **structural-only mode** using index signals: orphan citation counts, temporal gap detection, and convergence ratio arithmetic. No LLM calls are made.

## Semantic Scholar API

The `SemanticScholarClient` provides citation traversal that arXiv alone cannot offer — specifically **reverse citations** (who cites paper X?).

- **Unauthenticated**: Shared pool of 5,000 requests per 5 minutes across all users. Works for small/medium corpora.
- **Authenticated**: Dedicated rate limit of 1 request/second. Set via `SEMANTIC_SCHOLAR_API_KEY` environment variable or `UltraResearchOptions.SemanticScholarApiKey`.
- **Rate limiting**: Built-in `SemaphoreSlim` + timestamp enforcement (1 req/sec) matching the `CitationResolver` pattern.
- **Caching**: `ConcurrentDictionary` caches paper lookups to avoid redundant API calls.

Disable Semantic Scholar entirely with `--no-s2` (CLI) or `config.IncludeSemanticScholar = false`.

## Paper Storage Format

Fetched papers are saved as Markdown files with YAML frontmatter:

```markdown
---
title: "Attention Is All You Need"
authors: "Ashish Vaswani, Noam Shazeer, Niki Parmar"
year: 2017
doi: "10.48550/arXiv.1706.03762"
arxiv_id: "1706.03762"
source_url: "https://arxiv.org/abs/1706.03762"
fetched_at: "2026-02-07T14:30:00.0000000+00:00"
---

# Attention Is All You Need

[Full text from ar5iv, or abstract as fallback]
```

Files are stored in `{dataDir}/ultraresearch/{sanitized_id}.md`.

## Configuration Reference

### UltraResearchConfig

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Topic` | `string` | (required) | Research topic query |
| `MaxPapers` | `int` | 200 | Maximum papers to fetch |
| `BatchSize` | `int` | 10 | Papers per iteration before analyzing |
| `MaxIterations` | `int` | 50 | Maximum main loop iterations |
| `MaxDuration` | `TimeSpan` | 8 hours | Maximum wall-clock duration |
| `SentinelInterval` | `int` | 5 | Run sentinel every N papers |
| `ConvergenceThreshold` | `double` | 0.15 | New-info ratio below this for 3 checkpoints = converged |
| `SeedArxivIds` | `List<string>` | `[]` | Seed arXiv IDs to start from |
| `SeedDois` | `List<string>` | `[]` | Seed DOIs to start from |
| `ArxivCategories` | `List<string>` | `[]` | arXiv categories to filter |
| `IncludeSemanticScholar` | `bool` | `true` | Enable Semantic Scholar reverse citations |
| `CollectionName` | `string?` | auto | Collection name override |
| `DataDirectory` | `string?` | `%APPDATA%/lucidrag` | Directory for saved paper files |
| `DryRun` | `bool` | `false` | Search + discover without ingesting |

### UltraResearchOptions (DI)

| Property | Type | Description |
|----------|------|-------------|
| `SemanticScholarApiKey` | `string?` | Semantic Scholar API key for dedicated rate limit |

## Dependencies

- `DoomSummarizer.Core` — ArxivFetcher, CitationResolver, AcademicPatterns, OllamaService, SemanticScholarClient
- `LucidRAG.Core` — RagDocumentsDbContext, CitationGraphQueries, CollectionEntity
- `Microsoft.Extensions.Logging` — Structured logging
- `Microsoft.Extensions.DependencyInjection` — Service registration
- `Microsoft.Extensions.Http` — HttpClient factory

## License

MIT
