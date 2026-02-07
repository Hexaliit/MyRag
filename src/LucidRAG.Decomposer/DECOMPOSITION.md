# LucidRAG.Decomposer - Agentic Query Decomposition Engine

## Why Decomposition?

Most RAG systems treat every query the same way: embed it, search, return top-K, summarize. This works for simple
single-topic queries but breaks down when users ask complex questions:

> "Compare recent LLM advancements with classical NLP approaches and tell me about https://arxiv.org/abs/2210.02406"

This single query contains:

- **Two distinct topics** (LLMs vs. classical NLP) that need separate retrieval
- **A comparison intent** requiring evidence from both sides
- **A URL reference** that should be fetched directly, not searched
- **An implicit temporal constraint** ("recent" advancements)

A single-pass pipeline either misses half the question or returns a muddled mix that doesn't serve either topic well.

### The Core Problem

| Issue                | Single-pass                                    | With decomposition                           |
|----------------------|------------------------------------------------|----------------------------------------------|
| Multi-topic queries  | Conflates topics, retrieves middle-ground docs | Splits, retrieves independently, interleaves |
| URL references       | Treated as search terms                        | Fetched directly                             |
| Comparisons          | Returns a blend                                | Fetches both sides, presents side-by-side    |
| Knowledge gaps       | Unknown terms get bad results                  | Pre-fetches "What is X?" before main query   |
| Repeated sub-queries | Re-fetched every time                          | Semantic cache across sessions               |
| Existing KB content  | Always fetches fresh                           | Probes KB first, skips/reduces fetch         |

## Design Philosophy

> **Deterministic decomposition is CORE. LLM input refines it to make it precise.**

The decomposer uses existing NLP tools (NER, text recognizers, embeddings, query type detection) as the deterministic
foundation. The LLM sentinel **enhances but never replaces** what deterministic analysis produces. The system works
fully without an LLM available.

**No hardcoded word lists.** Recognizers, NER, and embedding similarity replace regex patterns wherever possible.
Conjunctions are checked via embedding similarity to detect actual topic boundaries, not assumed to always split.

## Architecture: Three Phases

```
User Query
    │
    ▼
┌─────────────────────────────────────────┐
│  FAST PATH GATE                         │
│  ComplexityClassifier                   │
│  → Simple: skip decomposition           │
│  → Moderate/Complex: full pipeline      │
└──────────────┬──────────────────────────┘
               │
    ┌──────────┼──────────┐
    ▼          ▼          ▼
 Phase 1    Phase 2    Phase 3
 Analyze    Refine     Orchestrate
```

### Phase 1: Deterministic Analysis (no LLM)

Six analyzers run in sequence, sharing an embedding cache to avoid redundant computations:

1. **ReferenceExtractor**  - Extracts URLs, file paths, DOIs, GitHub references. Each becomes a `ContentReference` node
   fetched directly.

2. **StructuralAnalyzer**  - Splits on sentence boundaries, then checks conjunctions. For "and"/"also"/"plus", embeds
   the clause before and after. If cosine similarity < 0.40, different topics → split. If >= 0.40, same topic → keep
   together.

   ```
   "LLM transformers and recent gold prices"
   → Clause A embedding vs Clause B embedding = 0.11 → SPLIT

   "LLM transformers and attention mechanisms"
   → Clause A embedding vs Clause B embedding = 0.82 → KEEP
   ```

3. **EntityRelationAnalyzer**  - Uses NER entities to detect multi-entity queries. When 2+ entities of the same type
   appear, checks comparison archetype similarity. Score >= 0.50 → create ComparisonNode with per-entity sub-queries.

4. **TemporalAnalyzer**  - Detects temporal comparisons ("changed since", "over time") and freshness requirements ("
   latest", "today"). Temporal comparisons become two time-bounded sub-queries.

5. **SemanticClusterAnalyzer**  - For queries without clear structural boundaries, splits into overlapping n-gram
   windows, embeds each, clusters by cosine similarity. 2+ distinct clusters → multi-topic decomposition.

6. **ConceptClassifier**  - Classifies the query concept type using embedding archetype matching. Replaces regex
   detection with semantic similarity to pre-embedded archetypes for each concept type (Definition, Procedure,
   Comparison, Troubleshooting, etc.).

### Phase 2: LLM Refinement

The sentinel LLM has already run (existing `PromptInterpreter`). Phase 2 merges its output with Phase 1:

| Deterministic      | Sentinel        | Result                                                |
|--------------------|-----------------|-------------------------------------------------------|
| Both split         | Both split      | Deterministic structure, sentinel keywords            |
| Only deterministic | No split        | Keep deterministic (structural is more reliable)      |
| Only sentinel      | Sentinel splits | Accept sentinel (LLM caught semantic ambiguity)       |
| Neither splits     | Single query    | Single node with sentinel spell-correction + keywords |

**Deterministic always wins on structure.** Sentinel enhances wording, keywords, and spell-correction.

### Phase 3: Agentic Orchestration

The orchestrator executes the plan without running queries itself - it delegates to `ISubQueryExecutor` (implemented by
DoomSummarizer.Core):

1. **Prerequisites** (KnowledgeGap nodes)  -  "What is RLHF?" runs first, results stored in KB
2. **Content references**  - URLs/DOIs fetched directly
3. **Parallel nodes**  - Independent sub-queries run concurrently via `Task.WhenAll`
4. **Dependent nodes**  - Comparison sides wait for both to complete

Each node goes through:

- **Cache check**  - Semantic similarity >= 0.92 against previous results
- **KB probe**  - Top-3 score >= 0.70 = skip fetch; 0.40-0.70 = reduced fetch
- **Execute** - Delegate to sub-query executor
- **Store** - Cache result for future queries

## Fast Path: Simple Query Detection

The `ComplexityClassifier` runs **before** any expensive analysis. It classifies queries as:

- **Simple** - Single topic, no references, no comparisons. Goes straight to single-pass fetch+score. This is the common
  case for "What's the latest AI news?" or "Tell me about Rust's type system."
- **Moderate** - Has URLs or mild decomposition needs. Light analysis.
- **Complex** - Multi-topic, comparison, temporal splits, knowledge gaps. Full pipeline.

Classification signals (all deterministic, no LLM):

- Clause count (sentence boundaries + conjunction detection)
- URL/file path presence
- NER entity count and type diversity
- Embedding similarity to comparison/temporal archetypes

## Concepts as First-Class Citizens

> "Concepts are first-class: they select the retrieval plan and the response contract."

Every query is classified into a `ConceptType` that drives the entire pipeline:

| Concept             | Retrieval Strategy                                  | Response Shape                        |
|---------------------|-----------------------------------------------------|---------------------------------------|
| **Definition**      | Corpus first, canonical sources, 5 items            | Definition card + aliases + examples  |
| **Procedure**       | Official docs, version-matched, 10 items            | Steps + prerequisites + failure modes |
| **Comparison**      | Both sides independently, 20 items                  | Side-by-side table + recommendation   |
| **Troubleshooting** | Issues/PRs/runbooks, error sigs, 15 items           | Diagnosis tree + fixes + logs         |
| **Incident**        | Status pages + changelogs, strong recency, 20 items | Timeline + blast radius + status      |
| **Policy**          | High-trust only, require citations, 10 items        | Rules + exceptions + citations        |
| **Roundup**         | Multi-source breadth, strong recency, 30 items      | Headlines + analysis + trends         |

The `ConceptRegistry` is the programmable substrate:

- Each concept has a `ConceptPolicy` defining retrieval strategy, source boosts, evidence requirements, response format,
  and active lenses
- Plugins can register custom policies or add lenses (PII redaction, security mode, ops mode)
- The same question in different environments yields different rigor and output

### Concept-Driven Retrieval

Instead of treating all queries identically:

```
BEFORE: query → retrieve → summarize

AFTER:  query → classify concept → select policy
          → choose sources + ranking + evidence level
          → retrieve with concept-aware strategy
          → synthesize with concept-specific format
          → validate evidence requirements
          → present with concept renderer
```

## The Embedding Budget Problem

Each analyzer may need to embed text. Naive implementation could embed the same text multiple times across analyzers.
The solution:

- All analyzers share a `Dictionary<string, float[]> EmbeddingCache` on the `QuerySignals` object
- The query itself is embedded once and reused
- Archetype embeddings are computed at startup (not per-query)
- `EmbedBatchAsync` is used wherever multiple texts need embedding

## Glossary: Startup Knowledge Seeding

The glossary system seeds the KB with foundational terms at startup, so the system "knows" core concepts before any user
query runs:

```yaml
# glossary.yaml
corpus: ai-fundamentals
terms:
  - term: RLHF
    query: "What is Reinforcement Learning from Human Feedback?"
    sources: [arxiv, search]
    ttl_days: 30
    concept_type: Definition
```

Plugin packs ship their own glossaries. The `KnowledgeGapDetector` extends this dynamically - terms discovered at query
time get stored and become part of the corpus for future queries.

## Risk Mitigations

| Risk                   | Mitigation                                                                                 |
|------------------------|--------------------------------------------------------------------------------------------|
| **Embedding latency**  | Shared embedding cache per session, batch embedding calls, archetype embeddings at startup |
| **Over-decomposition** | 0.40 cosine threshold for split detection, max 8 leaf budget, max depth 3                  |
| **LLM-free operation** | Phase 2 is enhancement only. System works fully with Phase 1 + DeterministicRefiner        |
| **Archetype drift**    | Archetypes stored as readable text, re-embedded at startup. Validate periodically          |
| **Cache staleness**    | TTL tiers by content type, temporal scope matching, semantic similarity threshold 0.92     |
| **Cycle detection**    | RecursionGuard checks embedding similarity >= 0.90 against ancestors                       |

## Tool-Use Routing: Sources Are Tools

> **"Sources are tools really"**

The decomposer doesn't just split queries into search sub-queries - it routes to *tools*. A query like:

> "Go to C:/test, index all the markdown files with 'summarizer' in the name, build a knowledge base called 'myfiles',
> then tell me how long they are"

decomposes into a **tool action chain**:

| Step | Tool         | Intent              | Parameters                           | Depends On |
|------|--------------|---------------------|--------------------------------------|------------|
| 1    | `FileSystem` | Find markdown files | `path=C:/test, pattern=*summarizer*` | -          |
| 2    | `Index`      | Build KB "myfiles"  | `collection=myfiles`                 | Step 1     |
| 3    | `Analyze`    | Calculate lengths   | `metric=length`                      | Step 2     |

### ToolKind Enum

| Tool           | What It Does                  | Example Trigger                           |
|----------------|-------------------------------|-------------------------------------------|
| **Search**     | Web search via source plugins | "What is X?" (default)                    |
| **Fetch**      | Direct URL/file retrieval     | "Tell me about https://..."               |
| **FileSystem** | Find/list/read local files    | "Go to C:/test and find files"            |
| **Index**      | Ingest content into KB        | "Build a knowledge base called 'myfiles'" |
| **KbQuery**    | Query existing KB             | "Search my knowledge base for..."         |
| **Analyze**    | Run analysis/statistics       | "Tell me how long they are"               |
| **Crawl**      | Multi-page web crawl          | "Crawl https://example.com"               |
| **Transform**  | Convert/export data           | "Export as CSV"                           |

### Detection: Deterministic + Embedding Archetypes

The `ToolUseAnalyzer` uses a two-tier approach:

1. **Deterministic extraction** - file paths, collection names, crawl URLs detected via regex. When a file path + file
   verb ("find", "go to", "list") appears, route to `FileSystem`. When "index"/"build KB" appears, route to `Index`.

2. **Embedding archetype matching** - each tool kind has 5 archetype phrases (e.g., `FileSystem`: "Go to folder and find
   files", "List all files in directory"). Query clauses are embedded and matched against archetypes. Score >= 0.50 =
   tool match.

### Execution Model

Tool action nodes execute in the orchestrator's **Phase C** (between reference fetches and parallel search nodes). Tool
chains with dependencies execute sequentially. Independent tool nodes can run in parallel.

```csharp
// Orchestrator execution order:
// Phase A: Prerequisites (KnowledgeGap - "What is X?")
// Phase B: Content references (URL/DOI fetch)
// Phase C: Tool actions (FileSystem → Index → Analyze chain)
// Phase D: Parallel search nodes
// Phase E: Dependent nodes (comparison sides wait for both)
```

### ISubQueryExecutor - Tool Support

Executors declare which tools they support:

```csharp
public interface ISubQueryExecutor
{
    Task<SubQueryResult> ExecuteAsync(QueryNode node, CancellationToken ct);
    Task<SubQueryResult> FetchReferenceAsync(ContentReference reference, CancellationToken ct);
    Task<SubQueryResult> ExecuteToolAsync(QueryNode node, ToolAction action, CancellationToken ct);
    Task<bool> SupportsToolAsync(ToolKind tool, CancellationToken ct);
}
```

Unsupported tools fail gracefully - the orchestrator logs a warning and marks the node as failed, allowing the rest of
the plan to continue.

## File Structure

```
src/LucidRAG.Decomposer/
├── Models/
│   ├── QueryNode.cs             # Execution unit with concept + temporal + entity + tool metadata
│   ├── QuerySignals.cs          # Phase 1 aggregated output (includes DetectedTools)
│   ├── DecompositionResult.cs   # Root result with execution plan (includes ToolActionNodes)
│   ├── ToolAction.cs            # Tool invocation model (ToolKind + Intent + Parameters)
│   ├── ConceptType.cs           # Concept classification enum (12 types)
│   ├── ConceptPolicy.cs         # Retrieval strategy + response contract per concept
│   └── ConceptRegistry.cs       # Programmable substrate: built-in + plugin policies
├── Analysis/
│   ├── IQueryAnalyzer.cs        # Analyzer interface
│   ├── ComplexityClassifier.cs  # Fast-path gate (Simple/Moderate/Complex)
│   ├── ConceptClassifier.cs     # Embedding archetype matching for concept type
│   ├── StructuralAnalyzer.cs    # Conjunction + sentence boundary splitting
│   ├── ReferenceExtractor.cs    # URL, DOI, file path, GitHub ref extraction
│   ├── EntityRelationAnalyzer.cs # NER-driven comparison + entity scoping
│   ├── TemporalAnalyzer.cs      # Time constraint + temporal comparison detection
│   ├── SemanticClusterAnalyzer.cs # N-gram window embedding clustering
│   └── ToolUseAnalyzer.cs       # Tool-use detection (FileSystem, Index, Crawl, Analyze, etc.)
├── Refinement/
│   ├── IDecompositionRefiner.cs # Refiner interface
│   ├── SentinelRefiner.cs       # Merges deterministic + LLM sentinel output
│   └── DeterministicRefiner.cs  # Fallback (no LLM)
├── Orchestration/
│   ├── DecompositionPipeline.cs # Main entry: Classify → Analyze → Refine
│   ├── DecompositionOrchestrator.cs # Executes plan: parallel, cache, KB, tool actions
│   ├── ISubQueryExecutor.cs     # Execution abstraction + tool execution + tool support check
│   └── RecursionGuard.cs        # Depth + cycle + budget limits
├── Caching/
│   ├── IDecompositionCache.cs   # Semantic cache interface
│   └── InMemoryDecompositionCache.cs # LRU with embedding similarity keys
├── KnowledgeBase/
│   └── IKnowledgeBaseProbe.cs   # KB probe interface (skip/reduce fetch)
├── Glossary/
│   ├── GlossaryService.cs       # Load + seed glossary at startup
│   ├── GlossaryConfig.cs        # YAML model
│   └── glossary.yaml            # Core AI/RAG/NLP terms
└── Integration/
    ├── DoomSummarizerAdapter.cs # Type mapping between decomposer ↔ DoomSummarizer
    └── ServiceCollectionExtensions.cs # services.AddDecomposer()
```

## Integration Point

```csharp
// In DoomSummarizer.Core (ScrollCommand / AskCommand):
var decomposer = services.GetRequiredService<DecompositionPipeline>();

// After QueryPreprocessor and PromptInterpreter run:
var sentinelInput = DoomSummarizerAdapter.ToRefinementInput(
    sentinelIntent.IsComposite,
    sentinelIntent.Subqueries,
    sentinelIntent.CorrectedQuery,
    sentinelIntent.FilterKeywords,
    sentinelIntent.SearchQueries,
    sentinelIntent.Entities,
    sentinelIntent.TimeSensitivity,
    sentinelIntent.RequiresFresh,
    sentinelIntent.Intent,
    sentinelIntent.Categories);

var result = await decomposer.DecomposeAsync(
    query,
    nerContext.Entities,
    nerContext.RecognizerSignals?.Urls.Count > 0,
    nerContext.HasTemporalSignals,
    sentinelInput,
    ct);

if (result.IsFastPath)
{
    // Single-pass execution (existing pipeline, concept-enriched)
    var policy = registry.GetPolicy(result.Concept);
    // Apply policy.FetchBudget, policy.PreferredSources, etc.
}
else
{
    // Orchestrated parallel execution
    var orchestrator = services.GetRequiredService<DecompositionOrchestrator>();
    var aggregated = await orchestrator.ExecuteAsync(result, executor, ct);
}
```
