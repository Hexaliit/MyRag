# UltraResearch — Autonomous Research Corpus Builder

> **Package**: `Mostlylucid.LucidRAG.UltraResearch`
> **Project**: [`src/Mostlylucid.LucidRAG.UltraResearch/`](../src/Mostlylucid.LucidRAG.UltraResearch/)
> **Tests**: [`src/Mostlylucid.LucidRAG.UltraResearch.Tests/`](../src/Mostlylucid.LucidRAG.UltraResearch.Tests/) (39 tests)

UltraResearch is a long-running autonomous agent that builds a fully-indexed research corpus. It continuously discovers, fetches, and indexes academic papers using citation graph topology, entity clusters, and LLM sentinel checkpoints to decide what to fetch next. The output is a live LucidRAG collection that users can chat with.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [The Agentic Loop](#the-agentic-loop)
- [Components](#components)
  - [UltraResearchOrchestrator](#ultraresearchorchestrator)
  - [ResearchPaperFetcher](#researchpaperfetcher)
  - [ResearchFrontierManager](#researchfrontiermanager)
  - [ResearchSentinelEvaluator](#researchsentinelevaluator)
  - [SemanticScholarClient](#semanticscholarclient)
  - [IDocumentIngester](#idocumentingester)
- [CLI Command](#cli-command)
- [Programmatic API](#programmatic-api)
- [Venue Quality Scoring](#venue-quality-scoring)
- [Priority Scoring](#priority-scoring)
- [Convergence Detection](#convergence-detection)
- [Sentinel Evaluation](#sentinel-evaluation)
- [State Persistence & Crash Recovery](#state-persistence--crash-recovery)
- [Data Flow](#data-flow)
- [Models Reference](#models-reference)
- [Configuration Reference](#configuration-reference)
- [Semantic Scholar API](#semantic-scholar-api)
- [Testing](#testing)
- [Reused Infrastructure](#reused-infrastructure)

## Overview

The existing `follow-papers` command does BFS citation following but only fetches metadata — it doesn't ingest papers into the index. The existing `research:` source plugin fetches N papers and stops.

**UltraResearch** is fundamentally different:

| Aspect | follow-papers / research: | UltraResearch |
|--------|---------------------------|---------------|
| Duration | Seconds–minutes | Hours |
| Steering | Static BFS | Adaptive (citation graph + LLM sentinel) |
| Ingestion | Metadata only | Full RAG pipeline (chunk, embed, entity, graph) |
| Discovery | Forward citations only | Forward + reverse citations + search + sentinel queries |
| Convergence | Fixed count | Autonomous (declining new info, frontier exhaustion, graph saturation) |
| Output | Metadata records | Live chatworthy collection |

## Architecture

```
User: "ultraresearch: attention mechanisms in transformers"
  │
  ├─ CLI: lucidrag ultraresearch "topic" --max-papers 200 --max-hours 4
  └─ Web: POST /api/ultraresearch/start { topic, maxPapers, seedPapers }
       │
       ▼
  UltraResearchOrchestrator (agentic loop)
       │
       ├── 1. SEARCH ──► ArxivFetcher + SemanticScholarClient
       │                  (topic search, seed papers, sentinel-suggested queries)
       │
       ├── 2. FETCH ───► ResearchPaperFetcher
       │                  (ar5iv full text, CrossRef metadata, S2 enrichment)
       │                  Saves as .md files with YAML frontmatter
       │
       ├── 3. INGEST ──► IDocumentIngester → existing pipeline
       │                  chunk → embed → entity extract → graph → link extract → Qdrant
       │
       ├── 4. ANALYZE ─► ResearchFrontierManager
       │                  CitationGraphQueries.FindOrphanCitationsAsync()
       │                  CitationGraphQueries.FindFoundationalReferencesAsync()
       │                  Entity cluster density, temporal gaps, citation topology
       │
       ├── 5. SENTINEL ► ResearchSentinelEvaluator (every N papers)
       │                  LLM sees corpus shape → identifies conceptual gaps
       │                  → suggests new search queries → convergence assessment
       │
       └── 6. STEER ──► Update frontier priorities, add sentinel queries
                         Loop back to step 1 until converged/budget exhausted
       │
       ▼
  Live Collection → Chat via existing ConversationService + RAG pipeline
```

### Project Layout

```
src/Mostlylucid.LucidRAG.UltraResearch/
├── Mostlylucid.LucidRAG.UltraResearch.csproj   # NuGet-packaged plugin
├── UltraResearchModels.cs                        # Config, state, candidate, checkpoint, progress
├── UltraResearchOrchestrator.cs                  # Main agentic loop + session lifecycle
├── ResearchPaperFetcher.cs                       # Multi-source paper fetch + save as .md
├── ResearchFrontierManager.cs                    # Priority queue + index-driven steering
├── ResearchSentinelEvaluator.cs                  # LLM checkpoint with structural fallback
├── VenueQualityScorer.cs                         # Venue quality scoring + tier dictionary
├── IDocumentIngester.cs                          # Abstraction for CLI/web ingestion
├── Extensions/
│   └── ServiceCollectionExtensions.cs            # DI registration + UltraResearchOptions
├── README.md                                     # NuGet package README
└── release-notes.txt

src/Mostlylucid.LucidRAG.UltraResearch.Tests/
├── Mostlylucid.LucidRAG.UltraResearch.Tests.csproj
├── GlobalUsings.cs
├── UltraResearchModelsTests.cs                   # 7 tests: defaults, serialization, enums
├── ResearchPaperFetcherTests.cs                  # 8 tests: normalization, S2 ID formatting
├── SemanticScholarResponseTests.cs               # 5 tests: JSON deserialization of S2 models
└── VenueQualityScorerTests.cs                    # 13 tests: scoring, tier matching, deserialization

src/DoomSummarizer.Core/Services/
└── SemanticScholarClient.cs                      # Semantic Scholar API client (shared)

src/DoomSummarizer.Core/Resources/prompts/
└── ultraresearch-sentinel.txt                    # LLM sentinel prompt template

src/LucidRAG.Cli/Commands/
└── UltraResearchCommand.cs                       # CLI entry point + CliDocumentIngester adapter
```

## The Agentic Loop

The orchestrator runs the following loop:

### 1. INITIALIZE

- Create or find a `CollectionEntity` (name from config or auto-generated `ultraresearch-{topic-slug}-{date}`)
- Initialize `UltraResearchState` (or restore from `Collection.Settings` on resume)
- Seed frontier: config seed arXiv IDs/DOIs get priority 1.0
- Run initial search: `ArxivFetcher.SearchAsync(topic)` + `SemanticScholarClient.SearchAsync(topic)`
- Add results as `FetchCandidate` entries to frontier

### 2. MAIN LOOP (while not converged and within budget)

```
iteration++

a. SELECT — frontierManager.GetNextBatch(state, batchSize)
   If frontier empty: try sentinel queries, then topic variations.
   If still empty → STOP (frontier exhausted).

b. FETCH — For each candidate (rate-limited):
   ResearchPaperFetcher.FetchAndPrepareAsync(candidate, dataDir)
   Extract citations from fetched content → add as new FetchCandidates
   Semantic Scholar: get reverse citations → add as candidates

c. INGEST — IDocumentIngester.IngestAsync(filePath, collectionId)
   Full pipeline: chunk → embed → entity extract → graph → link extract

d. ANALYZE — frontierManager.RefreshFrontierAsync()
   Recompute orphan citations, foundational refs, rescore all candidates

e. SENTINEL (every sentinelInterval papers) — sentinel.EvaluateAsync()
   LLM or structural analysis → gaps + suggested queries
   New queries → run searches → add results to frontier
   Update convergence tracking (consecutiveLowInfo counter)

f. PERSIST — Serialize state to Collection.Settings JSON

g. CHECK TERMINATION
   - papers >= maxPapers → budget exhausted
   - elapsed >= maxDuration → time limit
   - iterations > maxIterations → iteration limit
   - newInfoRatio < threshold for 3 checkpoints → converged
   - frontier empty AND no new queries → frontier exhausted
   - CancellationToken → user stopped
```

### 3. FINALIZE

- Set status = Completed / Stopped / Failed
- Persist final state
- Collection is now a live corpus ready for chat

## Components

### UltraResearchOrchestrator

**File**: [`UltraResearchOrchestrator.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/UltraResearchOrchestrator.cs)

The main entry point and session lifecycle manager. Registered as a **singleton** (manages active sessions across requests).

**Key methods**:

| Method | Description |
|--------|-------------|
| `StartAsync(config, ingester)` | Start a new session. Returns `Guid` sessionId immediately; loop runs via `Task.Run` |
| `ResumeAsync(collectionId, ingester)` | Reload state from `CollectionEntity.Settings` JSON, continue loop |
| `Stop(sessionId)` | Graceful cancellation via `CancellationTokenSource` |
| `GetStatus(sessionId)` | Returns current `UltraResearchState` |
| `StreamProgress(sessionId)` | Returns `IAsyncEnumerable<UltraResearchProgress>` via `Channel<T>` |

Active sessions tracked in `ConcurrentDictionary<Guid, ActiveSession>` — same pattern as `IngestionService._activeJobs` in the web app.

### ResearchPaperFetcher

**File**: [`ResearchPaperFetcher.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/ResearchPaperFetcher.cs)

Multi-source paper acquisition. Wraps `ArxivFetcher`, `ICitationResolver`, and `SemanticScholarClient`.

**Key methods**:

| Method | Description |
|--------|-------------|
| `SearchAsync(query, config, seenIds)` | Search arXiv (per category) + Semantic Scholar, return new candidates |
| `FetchAndPrepareAsync(candidate, dataDir)` | Fetch full text/metadata, compute venue quality via S2 enrichment, save as .md with YAML frontmatter, extract citation IDs |
| `GetReverseCitationsAsync(candidate, seenIds)` | Get reverse citations from Semantic Scholar |

**Content strategy**:
- **arXiv papers**: ar5iv HTML → plain text (primary), abstract-only (fallback)
- **DOI papers**: CrossRef metadata + abstract via `CitationResolver`
- **Deduplication**: `seenIds` HashSet with normalized keys (`arxiv:2301.12345`, `doi:10.xxxx/yyyy`)

**Static helpers** (public, used across the codebase):
- `NormalizeSeenKey(type, id)` — produces consistent dedup keys, strips arXiv versions
- `NormalizePaperId(S2Paper)` — extracts `(id, type)` from Semantic Scholar paper, prefers arXiv over DOI

### ResearchFrontierManager

**File**: [`ResearchFrontierManager.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/ResearchFrontierManager.cs)

Priority queue for research paper candidates. Registered as **scoped** (matches DbContext lifetime).

**Key methods**:

| Method | Description |
|--------|-------------|
| `RefreshFrontierAsync(state, collectionId)` | Query citation graph for orphans, add high-priority candidates, rescore all |
| `AddDiscoveredCandidates(candidates, state)` | Add new candidates (dedup + merge), trigger rescore |
| `GetNextBatch(state, batchSize)` | Return top-N by priority, remove from frontier |

**Index-driven gap signals** (no LLM needed):
- Orphan citations cited by 2+ ingested docs → high priority fetch
- Entity type coverage proxied by discovery source
- Temporal gaps via arXiv ID year extraction
- Sentinel keyword overlap with candidate titles

### ResearchSentinelEvaluator

**File**: [`ResearchSentinelEvaluator.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/ResearchSentinelEvaluator.cs)

LLM checkpoint that evaluates the "shape of the data." Registered as **scoped**.

**Two modes**:

1. **LLM mode** (when `OllamaService` is available): Builds a structured prompt with entity clusters, citation topology, year distribution, and search history. Calls `OllamaService.SentinelGenerateJsonAsync()` for structured JSON output.

2. **Structural-only mode** (graceful degradation): Runs when no LLM is configured or reachable. Uses purely index-derived signals: orphan citation counts, temporal gap detection, and arithmetic convergence ratios.

**Sentinel inputs** (structured summary, NOT full paper text):
1. Topic entity clusters — top 20 entities grouped by type
2. Citation graph topology — orphan count, foundational refs
3. Year distribution histogram
4. Search query history
5. Convergence metrics from last 3 checkpoints

**Sentinel prompt**: [`ultraresearch-sentinel.txt`](../src/DoomSummarizer.Core/Resources/prompts/ultraresearch-sentinel.txt)

### SemanticScholarClient

**File**: [`src/DoomSummarizer.Core/Services/SemanticScholarClient.cs`](../src/DoomSummarizer.Core/Services/SemanticScholarClient.cs)

Lives in `DoomSummarizer.Core` alongside `ArxivFetcher` and `CitationResolver` because it's a general-purpose academic API client reusable beyond UltraResearch.

**Key methods**:

| Method | Description |
|--------|-------------|
| `SearchAsync(query, limit, yearFrom, yearTo, minCitations, fieldsOfStudy)` | Bulk keyword search with filters |
| `GetCitationsAsync(paperId, limit)` | Papers that **cite** the given paper (reverse citations) |
| `GetReferencesAsync(paperId, limit)` | Papers **cited by** the given paper (forward references) |
| `GetPaperAsync(paperId)` | Single paper lookup by ID |
| `SetApiKey(apiKey)` | Configure API key for dedicated rate limit |

**Paper ID formats**: `ARXIV:2301.12345`, `DOI:10.xxxx/yyyy`, or raw S2 paper ID.

**Rate limiting**: `SemaphoreSlim` + timestamp enforcement (1 req/sec). Same pattern as `CitationResolver`.

**Response models** (public): `S2Paper`, `S2Author`, `S2Tldr`, `S2ExternalIds`, `S2Citation`

### IDocumentIngester

**File**: [`IDocumentIngester.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/IDocumentIngester.cs)

Abstraction that decouples the orchestrator from environment-specific ingestion:

```csharp
public interface IDocumentIngester
{
    Task<DocumentIngestResult> IngestAsync(string filePath, Guid collectionId, CancellationToken ct = default);
}

public record DocumentIngestResult(bool Success, string Message, Guid? DocumentId = null, int SegmentCount = 0);
```

**Implementations**:
- **CLI**: `CliDocumentIngester` (in `UltraResearchCommand.cs`) wraps `CliDocumentProcessor.IndexFileAsync()`
- **Web**: Would wrap `DocumentProcessingQueue.EnqueueAsync()` (future PR)

## CLI Command

**File**: [`src/LucidRAG.Cli/Commands/UltraResearchCommand.cs`](../src/LucidRAG.Cli/Commands/UltraResearchCommand.cs)

Registered in `Program.cs` as `rootCommand.Subcommands.Add(UltraResearchCommand.Create())`.

**Features**:
- Spectre.Console live table showing: iteration, papers fetched/ingested, frontier size, sentinel metrics
- FigletText banner on startup
- Automatic seed paper classification (arXiv ID regex, DOI regex, URL extraction)
- Comma-separated category parsing
- Environment variable support for `SEMANTIC_SCHOLAR_API_KEY`
- Summary table on completion with all session metrics

The CLI command runs the loop **synchronously** (not via `Task.Run`) using `RunInteractiveLoopAsync` for direct control and Spectre.Console live display compatibility.

## Programmatic API

### Service Registration

```csharp
// In your DI setup:
services.AddUltraResearch(options =>
{
    options.SemanticScholarApiKey = Environment.GetEnvironmentVariable("SEMANTIC_SCHOLAR_API_KEY");
});
```

This registers:
- `SemanticScholarClient` — singleton with its own `HttpClient`
- `ResearchPaperFetcher` — scoped (matches DbContext)
- `ResearchFrontierManager` — scoped
- `ResearchSentinelEvaluator` — scoped
- `UltraResearchOrchestrator` — singleton (manages sessions)

### Starting and Monitoring

```csharp
var orchestrator = sp.GetRequiredService<UltraResearchOrchestrator>();

// Start
var sessionId = await orchestrator.StartAsync(config, ingester);

// Monitor progress
await foreach (var progress in orchestrator.StreamProgress(sessionId)!)
{
    logger.LogInformation("[{Stage}] {Message}", progress.Stage, progress.Message);
}

// Or poll status
var state = orchestrator.GetStatus(sessionId);

// Stop gracefully
orchestrator.Stop(sessionId);
```

### Resume After Crash

```csharp
// Find collections with persisted UltraResearch state
var collections = await db.Collections
    .Where(c => c.Settings != null && c.Settings.Contains("\"Status\":\"Running\""))
    .ToListAsync();

foreach (var collection in collections)
{
    var sessionId = await orchestrator.ResumeAsync(collection.Id, ingester);
    if (sessionId.HasValue)
        logger.LogInformation("Resumed session {SessionId} for collection {CollectionId}",
            sessionId, collection.Id);
}
```

## Venue Quality Scoring

**File**: [`VenueQualityScorer.cs`](../src/Mostlylucid.LucidRAG.UltraResearch/VenueQualityScorer.cs)

Papers from reputable journals and conferences should rank higher during retrieval than unpublished preprints, all else being equal. The `VenueQualityScorer` computes a composite venue quality score (0-1) that flows through the entire pipeline:

```
Fetch (S2 API) → Compute Score → YAML Frontmatter → Ingestion → DocumentEntity.Metadata → RRF Signal
```

### Data Sources

| Source | Field | Status |
|--------|-------|--------|
| Semantic Scholar | `publicationVenue` (name, type, ISSN) | Requested via API |
| Semantic Scholar | `influentialCitationCount` | Already fetched |
| Semantic Scholar | `citationCount` | Already fetched |
| CrossRef | `container-title` → `CitationMetadata.Venue` | Used as fallback |

### Composite Formula

```
venueQuality = 0.35 * citationSignal
             + 0.25 * influentialCitationSignal
             + 0.25 * venueTypeSignal
             + 0.15 * publicationSignal
```

- **citationSignal**: `Min(1.0, Log(1 + citations) / Log(1 + 1000))` — log-scaled, caps at ~1000
- **influentialCitationSignal**: `Min(1.0, Log(1 + influential) / Log(1 + 100))` — log-scaled for influential citations
- **venueTypeSignal**: `journal = 0.7, conference = 0.6, unknown = 0.4` (from S2 `publicationVenue.type`)
- **publicationSignal**: `1.0` if published (has DOI + venue), `0.6` if DOI only, `0.3` if preprint-only

### Venue Tier Dictionary

A built-in dictionary of ~30 well-known venues overrides the generic `venueTypeSignal` when matched:

| Tier | Score | Examples |
|------|-------|----------|
| Top-tier journals | 1.0 | Nature, Science |
| High-impact journals | 0.90-0.95 | Cell, Lancet, NEJM, PNAS |
| Top CS conferences | 0.85-0.90 | NeurIPS, ICML, ICLR, CVPR, ACL |
| Strong conferences | 0.80 | NAACL, IJCAI, ECCV, SIGIR, WWW |
| Good journals | 0.65-0.85 | JMLR, Nature Comms, Scientific Reports, PLOS ONE |
| Preprint servers | 0.30 | arXiv, bioRxiv, medRxiv, SSRN |

Matching is fuzzy: case-insensitive, with common prefixes stripped ("Proceedings of the", "International Conference on", etc.).

### RRF Integration

The venue quality score is stored in `DocumentEntity.Metadata` as JSON and used as a 6th signal in `AgenticSearchService.ApplyBm25RrfAsync()`:

| Signal | Hybrid Weight | Keyword Weight |
|--------|--------------|----------------|
| Dense embedding | 1.0 | 0.3 |
| BM25 sparse | 1.0 | 1.5 |
| Salience | 0.3 | 0.2 |
| Freshness | 0.2 | 0.1 |
| Domain relevance | 1.5 | 0.5 |
| **Venue quality** | **0.8** | **0.3** |

The venue weight is higher in hybrid mode because academic paper quality is a strong signal when multiple segments are semantically similar.

### Score Examples

| Paper Type | Citations | Influential | Venue | Score |
|-----------|-----------|-------------|-------|-------|
| Nature (journal, 500 cit, 50 inf) | 500 | 50 | Nature | ~0.95 |
| NeurIPS (conference, 100 cit, 20 inf) | 100 | 20 | NeurIPS | ~0.82 |
| Good journal paper (50 cit, 10 inf) | 50 | 10 | JMLR | ~0.65 |
| arXiv preprint (5 cit, 0 inf) | 5 | 0 | arXiv | ~0.25 |
| Unknown paper (no S2 data) | 0 | 0 | — | ~0.15 |

## Priority Scoring

The frontier manager assigns a weighted composite score (0.0 to 1.0) to each candidate:

```
Priority = (citedByScore * 0.40) + (entityScore * 0.25) + (sentinelScore * 0.20) + (recencyScore * 0.15)
```

### Cited-By Score (weight: 0.40)

Normalized against the maximum citation count in the current frontier. A paper cited by 1000 others when the max is 1000 scores 1.0.

### Entity Overlap Score (weight: 0.25)

Approximated by candidate source type:

| Source | Score | Rationale |
|--------|-------|-----------|
| Orphan (cited by ingested docs) | 0.8 | High topical relevance |
| Sentinel-suggested | 0.7 | LLM identified gap |
| Direct citation | 0.6 | Connected in citation graph |
| Semantic Scholar | 0.4 | Keyword match only |
| Search | 0.3 | Broad match |

### Sentinel Boost Score (weight: 0.20)

Computed as keyword overlap between the candidate's title and the sentinel's most recent `SuggestedQueries` + `IdentifiedGaps`. Words from sentinel output are tokenized and matched against title words.

### Recency Score (weight: 0.15)

Extracted from arXiv ID format (`YYMM.XXXXX`):

| Age | Score |
|-----|-------|
| 0-1 years | 1.0 |
| 2-3 years | 0.8 |
| 4-5 years | 0.6 |
| 6-10 years | 0.4 |
| 10+ years | 0.2 |
| DOI (unknown age) | 0.5 |

## Convergence Detection

Three independent convergence signals:

### 1. New Information Declining

The sentinel tracks `NewInfoRatio` = (new entities this batch) / (total entities). When this ratio falls below `ConvergenceThreshold` (default: 0.15) for **3 consecutive** sentinel checkpoints, the session stops.

This means: each batch of papers is contributing fewer than 15% new entities to the corpus.

### 2. Frontier Exhausted

When the frontier is empty:
1. Try executing sentinel-suggested queries
2. Try topic variations ("survey", "review", partial topic)
3. If still empty after all attempts → stop

### 3. Budget Exhausted

- `PapersFetched >= MaxPapers` → paper budget
- `elapsed >= MaxDuration` → time budget
- `Iteration > MaxIterations` → iteration budget

## Sentinel Evaluation

### LLM Mode

Uses `OllamaService.SentinelGenerateJsonAsync()` with the [`ultraresearch-sentinel.txt`](../src/DoomSummarizer.Core/Resources/prompts/ultraresearch-sentinel.txt) system prompt.

The prompt provides:
1. Topic entity clusters (top 20 by type)
2. Citation graph stats (orphan count, foundational references)
3. Year distribution histogram
4. Previously-executed search queries
5. Convergence metrics from recent checkpoints

The LLM responds with structured JSON containing gap analysis, query suggestions, and a continue/stop recommendation.

### Structural-Only Mode (Fallback)

When no LLM is available:

- **Gap detection**: Orphan citations cited by 2+ ingested documents flagged as gaps; temporal gaps (year histogram jumps > 2 years) flagged
- **Query suggestions**: Titles of top orphan citations used as search queries
- **Continue logic**: `ShouldContinue = (newInfoRatio >= threshold) OR (orphanRatio > 0.10)`

## State Persistence & Crash Recovery

`UltraResearchState` is serialized to `CollectionEntity.Settings` as JSON after every iteration of the main loop.

### What's Persisted

| Field | Type | Purpose |
|-------|------|---------|
| `SessionId` | `Guid` | Unique session identifier |
| `CollectionId` | `Guid` | Target collection for ingested papers |
| `Status` | `enum` | Running, Paused, Completed, Stopped, Failed |
| `Topic` | `string` | Original research topic |
| `Iteration` | `int` | Current loop iteration |
| `PapersFetched/Ingested/Skipped/Failed` | `int` | Counters |
| `SeenIds` | `HashSet<string>` | All paper IDs ever encountered (prevents re-fetch) |
| `Frontier` | `List<FetchCandidate>` | Pending candidates with priority scores |
| `SearchQueriesUsed` | `HashSet<string>` | Queries already executed (prevents repeats) |
| `Checkpoints` | `List<SentinelCheckpoint>` | Full sentinel evaluation history |
| `StartedAt` / `CompletedAt` | `DateTimeOffset` | Timing |
| `StopReason` | `string?` | Human-readable stop reason |

### Resume Flow

1. Load `CollectionEntity` by ID
2. Deserialize `Settings` JSON → `UltraResearchState`
3. Verify `Status == Running` (only running sessions can resume)
4. Reconstruct `UltraResearchConfig` from persisted topic
5. Continue main loop from current iteration

### Size Considerations

At 10,000 seen IDs with 500 frontier candidates and 20 checkpoints, the state JSON is approximately 200KB — well within SQLite and PostgreSQL limits.

## Data Flow

### Paper Storage

Papers are saved as Markdown with YAML frontmatter to `{dataDir}/ultraresearch/{sanitized_id}.md`:

```markdown
---
title: "Attention Is All You Need"
authors: "Ashish Vaswani, Noam Shazeer, Niki Parmar, ..."
year: 2017
doi: "10.48550/arXiv.1706.03762"
arxiv_id: "1706.03762"
source_url: "https://arxiv.org/abs/1706.03762"
venue: "NeurIPS"
venue_quality: 0.95
citation_count: 95000
influential_citations: 12000
fetched_at: "2026-02-07T14:30:00.0000000+00:00"
---

# Attention Is All You Need

[Full text content or abstract]
```

The `venue_quality` score is computed by `VenueQualityScorer` and propagated through to `DocumentEntity.Metadata` during ingestion, where it serves as a 6th RRF signal during retrieval (see [Venue Quality Scoring](#venue-quality-scoring)).

### Content Resolution Strategy

1. **arXiv papers**: Try ar5iv HTML extraction first (`AcademicPatterns.FetchAr5ivTextAsync`), fall back to abstract from arXiv API
2. **DOI papers**: Resolve via `CitationResolver.ResolveDoiAsync`, use abstract
3. **Citation extraction**: `AcademicPatterns.ExtractCitationIds(content, arxivId)` finds referenced arXiv IDs and DOIs within the text

### ID Normalization

All paper IDs are normalized to prevent duplicates:

| Input | Normalized Key |
|-------|---------------|
| `2301.12345v1` | `arxiv:2301.12345` |
| `2301.12345v2` | `arxiv:2301.12345` |
| `2301.12345` | `arxiv:2301.12345` |
| `10.1234/test` | `doi:10.1234/test` |

Version suffixes are stripped from arXiv IDs so v1 and v2 of the same paper are treated as identical.

## Models Reference

### UltraResearchConfig

Session configuration. See [Configuration Reference](#configuration-reference).

### UltraResearchState

Full mutable state of a running session. Serialized to `CollectionEntity.Settings` for crash recovery.

### FetchCandidate

A paper discovered during research, queued for fetching:

```csharp
public class FetchCandidate
{
    public required string Id { get; set; }       // arXiv ID or DOI
    public required string Type { get; set; }      // "arxiv" or "doi"
    public CandidateSource Source { get; set; }    // How discovered
    public double Priority { get; set; }           // 0-1, higher = fetch sooner
    public int CitedByCount { get; set; }          // Citation count
    public string? Title { get; set; }             // Paper title if known
    public string? DiscoveredFrom { get; set; }    // Parent paper ID
}
```

### CandidateSource (enum)

| Value | Description |
|-------|-------------|
| `Search` | Found via keyword search |
| `Citation` | Extracted from an ingested paper's references |
| `SemanticScholar` | Found via Semantic Scholar reverse citations |
| `Orphan` | Orphan citation in the graph (cited but not ingested) |
| `Sentinel` | Suggested by sentinel evaluation |

### SentinelCheckpoint

Snapshot from a sentinel evaluation:

```csharp
public class SentinelCheckpoint
{
    public int Iteration { get; set; }
    public int TotalPapers { get; set; }
    public int TotalEntities { get; set; }
    public int OrphanCitations { get; set; }
    public double NewInfoRatio { get; set; }         // 0-1, fraction of new entities
    public List<string> IdentifiedGaps { get; set; }
    public List<string> SuggestedQueries { get; set; }
    public string? SentinelAnalysis { get; set; }    // Full analysis text
    public bool ShouldContinue { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
```

### UltraResearchProgress (record)

Progress update emitted during the loop:

```csharp
public record UltraResearchProgress(
    ResearchStage Stage,        // Searching, Fetching, Ingesting, Analyzing, Sentinel, Finalizing
    string Message,
    int Iteration,
    int PapersFetched,
    int PapersIngested,
    int FrontierSize,
    double? NewInfoRatio);
```

### FetchedPaper (record)

Result of fetching and preparing a paper:

```csharp
public record FetchedPaper(
    string FilePath,
    string Title,
    List<string> Authors,
    int? Year,
    string? Doi,
    string? ArxivId,
    string? SourceUrl,
    List<(string type, string id)> CitationIds,
    string? VenueName = null,
    double VenueQuality = 0.0,
    int CitationCount = 0,
    int InfluentialCitations = 0);
```

## Configuration Reference

### UltraResearchConfig

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Topic` | `string` | (required) | Research topic query |
| `MaxPapers` | `int` | 200 | Maximum papers to fetch |
| `BatchSize` | `int` | 10 | Papers per iteration |
| `MaxIterations` | `int` | 50 | Maximum loop iterations |
| `MaxDuration` | `TimeSpan` | 8h | Maximum wall-clock time |
| `SentinelInterval` | `int` | 5 | Papers between sentinel evaluations |
| `ConvergenceThreshold` | `double` | 0.15 | New-info ratio below this for 3 checkpoints = converged |
| `SeedArxivIds` | `List<string>` | `[]` | Starting arXiv papers |
| `SeedDois` | `List<string>` | `[]` | Starting DOI papers |
| `ArxivCategories` | `List<string>` | `[]` | arXiv category filters |
| `IncludeSemanticScholar` | `bool` | `true` | Enable reverse citation discovery |
| `CollectionName` | `string?` | auto | Override collection name |
| `DataDirectory` | `string?` | `%APPDATA%/lucidrag` | Paper file storage directory |
| `DryRun` | `bool` | `false` | Discover without ingesting |

### UltraResearchOptions (DI)

| Property | Type | Description |
|----------|------|-------------|
| `SemanticScholarApiKey` | `string?` | S2 API key for dedicated rate limit |

## Semantic Scholar API

The [Semantic Scholar Academic Graph API](https://api.semanticscholar.org/api-docs/graph) provides the key capability that arXiv alone cannot: **reverse citations** (which papers cite paper X?).

### Authentication

| Mode | Rate Limit | Setup |
|------|-----------|-------|
| Unauthenticated | 5,000 req/5min shared pool | No setup needed |
| Authenticated | 1 req/sec dedicated | Set `SEMANTIC_SCHOLAR_API_KEY` env var or `UltraResearchOptions.SemanticScholarApiKey` |

### Paper ID Formats

- `ARXIV:2301.12345` — arXiv paper
- `DOI:10.xxxx/yyyy` — DOI paper
- Raw S2 paper ID — Semantic Scholar internal ID

Helper methods: `SemanticScholarClient.ArxivToS2Id(arxivId)`, `SemanticScholarClient.DoiToS2Id(doi)`

### Response Models

| Model | Fields |
|-------|--------|
| `S2Paper` | paperId, title, authors, year, abstract, citationCount, influentialCitationCount, fieldsOfStudy, tldr, externalIds, venue, publicationVenue |
| `S2PublicationVenue` | name, type ("Journal"/"Conference"), issn |
| `S2Author` | authorId, name |
| `S2Tldr` | model, text |
| `S2ExternalIds` | ArXiv, DOI, CorpusId |
| `S2Citation` | Paper (`S2Paper`), IsInfluential (`bool`) |

## Testing

39 tests across 4 test files:

### UltraResearchModelsTests (7 tests)

- Config defaults are reasonable (200 papers, 8h, batch 10, etc.)
- State initializes correctly (Running status, empty collections)
- State round-trips through JSON serialization
- All `CandidateSource` enum values exist
- `SentinelCheckpoint` defaults to `ShouldContinue = true`
- `UltraResearchProgress` record constructs correctly
- `UltraResearchStatus` enum serializes as string (not integer)

### ResearchPaperFetcherTests (8 tests)

- `NormalizeSeenKey` produces consistent keys across arXiv versions
- `NormalizePaperId` prefers arXiv over DOI when both present
- `NormalizePaperId` falls back to DOI when no arXiv
- `NormalizePaperId` returns null for papers with no external IDs
- `ArxivToS2Id` formats correctly and strips versions
- `DoiToS2Id` formats correctly
- `S2Paper.ExternalIds` extracts ArxivId and Doi correctly

### SemanticScholarResponseTests (5 tests)

- `S2Paper` deserializes from full API JSON (all fields, nested authors, TLDR, external IDs)
- `S2Paper` deserializes with minimal fields (null handling)
- `S2SearchResponse` deserializes (total count + data array)
- `S2CitationResponse` deserializes (citing papers + influence flags)
- `S2ReferenceResponse` deserializes (cited papers + DOI extraction)

### VenueQualityScorerTests (13 tests)

- Nature paper (journal, 500 citations, 50 influential) scores >= 0.85
- NeurIPS paper (conference, 100 citations, 20 influential) scores >= 0.70
- arXiv preprint (no venue, 5 citations) scores 0.10-0.40
- Unknown paper (no S2 data) scores very low (<= 0.20)
- CrossRef venue used as fallback when S2 has none
- Journal venue type signal higher than conference
- Unknown venue type gets default 0.4
- Direct tier matching (Nature = 1.0)
- Case-insensitive tier matching (neurips = 0.90)
- Prefix stripping ("Proceedings of the NeurIPS" matches)
- No-match returns null
- Venue name normalization strips common prefixes
- S2Paper deserializes new venue/publicationVenue fields
- Extreme values stay in [0, 1] range

### Running Tests

```bash
# UltraResearch tests only
dotnet test src/Mostlylucid.LucidRAG.UltraResearch.Tests/ --verbosity normal

# Full solution (excludes Browser and Integration tests)
dotnet test LucidRAG.sln -c Release --filter "Category!=Browser&Category!=Integration"
```

## Reused Infrastructure

UltraResearch builds on existing LucidRAG infrastructure rather than reimplementing:

| Component | Source | Used For |
|-----------|--------|----------|
| `AcademicPatterns` | DoomSummarizer.Core | Regex for arXiv/DOI IDs, NormalizeDoi, ExtractCitationIds, FetchAr5ivTextAsync, StripArxivVersion |
| `ArxivFetcher` | DoomSummarizer.Core | arXiv paper search and metadata |
| `ICitationResolver` | DoomSummarizer.Core | DOI/arXiv metadata resolution with caching |
| `CitationGraphQueries` | LucidRAG.Core | Orphan citation detection, foundational reference discovery (with new collection-scoped overloads) |
| `OllamaService` | DoomSummarizer.Core | `SentinelGenerateJsonAsync` for structured LLM output |
| `CliDocumentProcessor` | LucidRAG.Cli | Full ingestion pipeline (CLI mode) |
| `CollectionEntity` | LucidRAG.Core | Collection grouping + Settings JSON for state persistence |
| `RagDocumentsDbContext` | LucidRAG.Core | Entity queries for sentinel evaluation |

### Modified Files

| File | Change |
|------|--------|
| `CitationGraphQueries.cs` | Added `FindOrphanCitationsAsync(Guid collectionId, ...)` and `FindFoundationalReferencesAsync(Guid collectionId, ...)` overloads |
| `LucidRAG.Cli/Program.cs` | Registered `UltraResearchCommand.Create()` |
| `LucidRAG.Cli.csproj` | Added project reference to UltraResearch |
| `DoomSummarizer.Core.csproj` | Added `InternalsVisibleTo` for test assembly |
