# Functional Spec: Adaptive Retrieval (Cache vs Live) + Gap-Filling Queries

This spec describes a bounded, inspectable retrieval loop for DoomSummarizer that:

1. Reuses cached/KB evidence when it is sufficient.
2. Decides when to look up new information (live retrieval) vs stay local.
3. Tailors subqueries to fill specific evidence gaps.
4. Logs a structured retrieval trace for reuse and tuning.

The design is inspired by DeepRAG’s framing of retrieval as iterative decisions (continue vs stop; retrieve vs proceed). For DoomSummarizer we map this to **continue vs stop** and **use cached/KB vs fetch live**.

## Status

- Target: `ask --auto` first (scroll integration optional follow-up).
- Backward compatible: default `ask` behavior remains unless `--auto` (or config default) is enabled.
- This is a **proposed** feature spec (not implemented yet as of January 28, 2026).

## Glossary

- **KB**: Local knowledge base (SQLite `doom.db`, optional DuckDB vectors).
- **Live retrieval**: Fetch new items from web/API/crawl sources and store into KB.
- **Round**: One iteration of plan → retrieve → evaluate sufficiency.
- **Trace**: Structured log of rounds, queries, decisions, and stop reason.
- **Gap**: A missing facet preventing an evidence-grounded answer.

## Goals

- Prefer local-first: reuse KB and cached retrieval results when “good enough”.
- Reduce noise: retrieve fewer, higher-yield items; avoid redundant fetches.
- Provide deterministic budgets: cap rounds, items, and API spend.
- Make decisions debuggable: emit a trace and store it for later reuse/tuning.

## Non-goals

- Training/fine-tuning a DeepRAG-style model.
- Building a general-purpose autonomous agent loop.
- Guaranteeing “latest” without live retrieval (unless user explicitly forces `--cache only`).

## User Stories

1. As a user, when I ask a question similar to one I asked recently, DoomSummarizer should reuse prior evidence and answer quickly.
2. As a user, when my question asks for “latest” information, DoomSummarizer should detect stale evidence and fetch fresh items (unless I forbid it).
3. As a user, if the initial evidence is missing specifics (numbers, dates, one side of a comparison), DoomSummarizer should run targeted follow-up queries rather than broad re-search.
4. As a user, I can see (and export) why the system stopped and what it retrieved.

## CLI / UX Requirements

### New flags (Ask)

- `doomsummarizer ask "<question>" --auto`
  - Enables adaptive retrieval (cache reuse + gap-fill + optional live retrieval).
- `--cache <MODE>` where `<MODE>` is:
  - `only`: never fetch live; answer from KB or return “insufficient evidence”.
  - `prefer`: use KB/cache unless gap analysis requires live retrieval. *(default for `--auto`)*
  - `bypass`: skip cache reuse; still allowed to use KB, but forces a fresh retrieval plan.
- `--max-rounds <N>` (default: `3`)
- `--max-new-items <N>` (default: `30` total across rounds)
- `--freshness-days <D>`
  - Default: `30`
  - If the question implies recency (e.g., “latest”, “today”, “this week”), effective default becomes `7` unless overridden.
- `--trace` prints a compact round-by-round trace to console.
- `--trace-json <PATH>` writes the full trace JSON to a file.

### Output behavior

- If `--cache only` and evidence is insufficient: return an “insufficient evidence” response that includes:
  - a short gap summary (what’s missing),
  - suggested follow-up queries (the planner output),
  - and (if `--trace`) the stop reason.

## Functional Requirements

## 1) Cache reuse

### Inputs

- `question` string
- `questionEmbedding` (if embeddings available)
- `query_log` recent entries (stored embeddings + item IDs)
- Effective freshness requirement (derived from question + `--freshness-days`)

### Behavior

1. Attempt to find a similar recent query (`FindSimilarQueryAsync`).
2. Apply similarity thresholds:
   - **Hard hit**: similarity ≥ `0.95` → reuse cached evidence as round 0 evidence.
   - **Soft hit**: similarity ≥ `0.90` → reuse as starting evidence, but must re-evaluate sufficiency.
   - **Miss**: similarity < `0.90` → no reuse; proceed to KB retrieval.
3. Apply a freshness gate when recency intent is detected:
   - If newest evidence item is older than `now - freshnessDays`, treat as soft hit at best and prefer gap-fill / live retrieval.

### Notes

- Cache reuse never “auto-terminates” on its own; sufficiency evaluation always runs.

## 2) Retrieval loop (bounded rounds)

### Loop constraints

- Maximum rounds: `--max-rounds`
- Maximum new items fetched live: `--max-new-items`
- Always dedupe and rescore after each round.

### Round structure

Each round consists of:

1. **Retrieve baseline evidence** from KB if evidence set is empty.
2. **Evaluate sufficiency** (terminate vs continue, and gap list if continuing).
3. If continue:
   - If `--cache only`: stop with insufficient evidence.
   - Else: plan subqueries, then decide per subquery whether to use KB-only retrieval or live retrieval.
4. Merge results, dedupe, rescore, and proceed to next round.

## 3) Cached-vs-live decision (per subquery)

This is the “atomic decision” for DoomSummarizer:

- **KB**: run local search against stored content.
- **Live**: fetch new content and store it, then include it in the working evidence set.

### Deterministic default policy

Choose **KB** when all are true:

- The gap is not freshness/time-sensitive, **and**
- KB search for the subquery returns at least `K` candidates above a relevance threshold (suggested: `K=5`, `threshold=0.25`), **and**
- The last KB attempt for the same gap produced meaningful novelty (e.g., ≥2 new high-relevance items not already in the working set).

Choose **Live** when any are true:

- Gap is `FreshnessGap` and newest KB evidence is older than `now - freshnessDays`.
- Gap is `NumericDetailGap` / `ProvenanceGap` and KB results are low-confidence, missing citable URLs, or contradictory.
- Repeated KB attempts for the same gap yield low novelty.

### Budget guards

- Never do live retrieval when `--cache only`.
- Never exceed `--max-new-items`.
- Respect API budget limits (if configured); if exhausted, behave like `--cache only` for remaining rounds.

## 4) Gap analysis (what’s missing?)

### Gap types (v1)

Each gap has a stable `gap_id` and `type`:

- `FreshnessGap`: question implies recency, evidence too old.
- `EntityCoverageGap`: required entity/term from question absent in evidence.
- `NumericDetailGap`: question asks for numbers/dates/amounts and evidence lacks them.
- `ComparisonGap`: question asks to compare A vs B, evidence covers only one side.
- `MissingDefinitionGap`: “what is X” but evidence lacks a definitional snippet.
- `ConflictGap`: evidence contains conflicting claims with no tie-breaker.
- `ProvenanceGap`: claims exist but lack citable/primary sources.

### Detection (v1 heuristics)

- Recency intent: keyword/regex (and optionally sentinel classification).
- Numeric intent: keyword/regex (“how many”, “cost”, “percent”, “when”, “date”, etc.).
- Entity/term extraction: keyword extraction + NER if available; verify presence in titles/snippets/keywords.
- Provenance: count items with citable URLs; detect unresolvable URLs; prefer primary sources when gap requests authority.

## 5) Subquery planning (gap-filling)

The planner generates a small set of **atomic** subqueries, each tied to a single gap.

### Output schema

Each planned subquery includes:

- `query` (string)
- `gap_id` (string)
- `constraints`:
  - `quoted_entities` (string[])
  - `time_window_days` (int?)
  - `preferred_sources` (string[]?)
  - `site_filters` (string[]?)
  - `required_fields` (string[]; e.g., `["number","date","primary_source"]`)

### Rules

- Max 5 subqueries per round.
- Prefer quoting entity names for precise matching.
- For numeric gaps, include units and expected fields.
- For freshness gaps, restrict time window aggressively.

## 6) Evidence handling

### Merge + dedupe

- Dedupe by normalized URL (strip querystring, trim trailing slash).
- Fallback dedupe by normalized title.
- Track provenance: which round and subquery produced each item.

### Rescore + cap

- After each round, rescore via the existing multi-signal pipeline.
- Keep a working set capped at a fixed size (suggested: 40) to avoid context bloat.

### Snippet selection (noise control)

When assembling evidence for answer generation:

- Prefer best snippets per item (e.g., TextRank top sentences) instead of raw content.
- Cap per-item snippet length (suggested: 400–600 chars).

## Data / Persistence

### Trace storage

Option A (recommended first): extend `query_log` with optional columns:

- `trace_json TEXT NULL`
- `stop_reason TEXT NULL`
- `rounds INT NULL`
- `used_live INT NULL` (0/1)

Option B: new table `retrieval_trace` (separate lifecycle and indexing).

### Trace JSON (minimum fields)

- `version`
- `settings` (cache mode, budgets, freshness)
- `rounds[]`:
  - `round_index`
  - `starting_from_cache`
  - `kb_queries[]`
  - `live_queries[]`
  - `new_items_count`
  - `gaps[]`
  - `stop_decision` (if terminated)

## Stopping policy (SufficiencyEvaluator)

### Terminate when ALL are true

- `top_relevance >= 0.45` *(tunable)*
- At least `minEvidenceCount = 5` citable items, and
- No `FreshnessGap`, and
- Either:
  - numeric intent satisfied (a number/date is present in at least one high-ranked snippet), or
  - non-numeric intent has ≥2 corroborating items.

### Continue when ANY are true

- `top_relevance < 0.35`
- `FreshnessGap` present
- required entity/term missing
- unresolved `ConflictGap` / `ProvenanceGap`

### Forced stop (budgets exhausted)

Return best-effort answer + explicit caveats + “next queries to run” (from gaps/planner).

## Telemetry / Tuning Metrics

Track:

- % answered without live retrieval
- average rounds per question
- live items fetched per question
- cache hit rate (hard/soft)
- citation coverage (citable URLs used)
- novelty per round (new high-relevance items)

## Acceptance Criteria

1. `ask --auto` reuses evidence from a similar recent query when similarity ≥ 0.95.
2. `ask --auto` detects stale evidence for recency questions and performs at least one live round (unless `--cache only`).
3. Gap-filling produces targeted subqueries tied to specific gap types and stops when sufficiency criteria are met or budgets are exhausted.
4. `--trace` shows: rounds, queries, cache/liveness decisions, stop reason.
5. `--trace-json` writes a valid JSON trace reflecting the session.
