# Semantic Query Classifier v2.5

Deterministic, embedding-based query classification that runs before the sentinel LLM.
Classifies user queries into topic, type, vibe, and escalation signals using cosine
similarity against a library of pre-embedded exemplar questions — no LLM round-trip needed.

## Why This Exists

The sentinel LLM (TinyLlama/Phi-3/Ollama) is effective but slow (~200-800ms) and non-deterministic.
For the majority of queries — "latest tech news", "AI developments", "how do I set up Docker?" —
the topic and intent are unambiguous. The classifier resolves these in under 2ms with ONNX
cosine similarity, skipping the sentinel entirely. The sentinel is reserved for what it does
best: decomposing composite queries and generating search terms.

**Decision flow:**

```
Query ──→ Embedding Classifier (< 2ms, deterministic)
               │
               ├─ Strong match (≥ 0.55) + not composite/complex
               │     → Use embedding result directly, skip sentinel
               │
               └─ Weak match OR composite OR complex
                     → Call sentinel LLM for decomposition
                           → Embedding categories still used (sentinel provides search terms)
```

## Architecture

### Components

| File | Role |
|------|------|
| `QueryClassifier.cs` | Core classifier — init, scoring, weighted voting |
| `QueryFeatures.cs` | Structural feature extraction (regex-based, sub-0.02ms) |
| `QueryExemplar.cs` | Data models — exemplar, classification result |
| `DoomConfig.cs` | `ClassifierConfig` record — all thresholds |
| `PromptInterpreter.cs` | Integration point — classifier → sentinel → routing |
| `CommandBootstrap.cs` | Wiring — loads config, configures shared classifier |
| `ExemplarsCommand.cs` | CLI management — list, init, rebuild, validate |
| `default-exemplars.yaml` | 444 embedded exemplar questions |

### Initialization

At startup, `CommandBootstrap.CreateAsync` does:

1. Loads `ClassifierConfig` from YAML config
2. Calls `PromptInterpreter.ConfigureClassifier(config.Classifier)` — creates a `QueryClassifier` with custom thresholds
3. On first query, `PromptInterpreter.GetRouterAsync()` calls `QueryClassifier.InitializeAsync(embedding)`:
   - Loads all exemplars (embedded defaults + user YAML overrides)
   - Batch-embeds all 444 questions in a single ONNX call (~150ms on GPU)
   - Computes IDF weights per topic and type label
4. Subsequent queries score against the pre-embedded exemplars (< 2ms each)

### Per-Query Classification Flow

```
ClassifyAsync(query):

  1. Extract structural features     ← QueryFeatures.Extract()  (< 0.02ms)
     - question words, intent markers, composite conjunctions
     - imperative verbs, comparison markers, search-only patterns

  2. Embed query                     ← IEmbeddingService.EmbedAsync()  (~1ms ONNX)

  3. Score ALL exemplars              ← single pass, SIMD cosine similarity
     Simultaneously track:
     - Candidate set (sim ≥ 0.35)
     - Best overall match
     - Best vibe match (raw similarity)
     - Composite top-2 scores (consensus check)
     - Complex flag (any complex exemplar above threshold)

  4. IDF-weighted voting              ← per topic, per type
     score(label) = max_sim + CountBoost × log₂(count) × idf(label)

  5. Feature-based adjustments        ← short queries only (≤ 4 words)
     - Howto/comparison/QA intent markers boost respective type scores
     - Search-only pattern → force search_only type
     - No intent markers → boost roundup (short queries default to news browse)

  6. Vibe detection                   ← raw similarity (not weighted vote)
     Best vibe exemplar match > 0.70 → detected vibe

  7. Composite detection              ← consensus of top 2 composite matches
     Both above threshold (0.75) → flag IsComposite
     Feature-based conjunction → relax threshold by 15%

  8. Return QueryClassification
```

## The Scoring Algorithm

### IDF-Weighted Multi-Match Voting

The classifier uses a voting scheme that combines three statistical principles:

**1. Max anchoring** — the best individual exemplar match dominates the score.
A single 0.92 match outweighs many mediocre matches.

**2. Logarithmic count boost** — additional matches contribute diminishing returns.
`log₂(count)` means 2 matches add 1.0, 4 add 2.0, 8 add 3.0.

**3. IDF weighting** — rare labels get stronger count boosts.
"howto" (5 exemplars) gets a higher IDF weight than "roundup" (40 exemplars),
so 5 howto matches can out-score 40 roundup matches when quality is similar.

```
score(label) = max_sim(label) + CountBoost × log₂(count(label)) × idf(label)

where:
  max_sim(label)  = highest cosine similarity among candidates with this label
  count(label)    = number of candidates with this label
  CountBoost      = 0.05 (configurable)
  idf(label)      = log₂(1 + total_exemplars / exemplars_with_label)
```

**Concrete example with IDF:**

```
Given 444 total exemplars:
  roundup:  80 exemplars → idf = log₂(1 + 444/80)  ≈ 2.76
  howto:    15 exemplars → idf = log₂(1 + 444/15)  ≈ 4.93
  composite: 12 exemplars → idf = log₂(1 + 444/12) ≈ 5.25

Query: "Docker help"
  roundup: max=0.52, count=30 → 0.52 + 0.05 × 4.9 × 2.76 = 1.20
  howto:   max=0.58, count=4  → 0.58 + 0.05 × 2.0 × 4.93 = 1.07

  → roundup wins (more exemplars matched, higher total)

Query: "How do I configure Docker networking?"
  roundup: max=0.45, count=25 → 0.45 + 0.05 × 4.6 × 2.76 = 1.09
  howto:   max=0.82, count=6  → 0.82 + 0.05 × 2.6 × 4.93 = 1.46

  → howto wins (strong individual match + IDF boost for rare type)
```

### Why Not Just Max Similarity?

Max-per-group (Phase 1 approach) was fragile: a single high-scoring exemplar in "roundup"
could beat consistent "howto" matches. Multi-match voting uses the collective signal.
When 6 different howto exemplars all match at 0.6-0.8, that's more reliable than 1 roundup
exemplar matching at 0.85 (which might be vocabulary overlap rather than true semantic match).

### Vibe and Composite: Raw Similarity, Not Voting

Vibe and composite use **raw max similarity**, not the IDF-weighted vote. This is deliberate:

- **Vibe**: vocabulary overlap causes false positives. "latest tech news" partially matches
  "Roast the latest tech announcements" (snarky vibe) via shared "latest tech". IDF voting
  would amplify this. Raw similarity requires a genuine semantic match (> 0.70).

- **Composite**: requires **consensus** — top 2 composite exemplar matches must both be strong
  (> 0.75). This prevents "latest tech news" from matching "Summarize tech news and also politics"
  just because they share "tech news".

## Short-Query Feature Decomposition

Embeddings produce noisy similarity scores on short queries (1-4 words) because there's
insufficient context for semantic differentiation. "Docker help" matches both howto and roundup
exemplars at similar scores. Structural features provide discriminative signals that embeddings miss.

### How Features Work

`QueryFeatures.Extract(query)` runs 7 pre-compiled source-generated regexes in under 0.02ms:

| Feature | Pattern | What it detects |
|---------|---------|-----------------|
| `HasQuestionWord` | `^(how\|what\|why\|when\|who\|where\|which)` | Question intent |
| `HasHowtoMarker` | `how (do\|can\|to)\|set up\|configure\|tutorial` | Howto/tutorial intent |
| `HasComparisonMarker` | `compare\|vs\|versus\|difference between` | Comparison intent |
| `HasSearchOnlyMarker` | `convert\|define\|population of\|what time` | Factual lookup intent |
| `HasQaMarker` | `^(what is\|who is\|where is)` | Direct Q&A intent |
| `HasCompositeConjunction` | `and also\|as well as\|in addition to` | Multi-part query |
| `HasImperativeVerb` | `^(show\|get\|find\|tell\|list\|summarize)` | Action/fetch intent |

### Feature-Based Type Adjustments

Applied **only when the query has 4 or fewer words** (configurable via `short_query_max_words`):

- Howto marker detected → boost "howto" type score by +0.12
- Comparison marker detected → boost "comparison" type score by +0.12
- QA marker detected (and not search-only) → boost "qa" type score by +0.10
- No intent markers at all → boost "roundup" by +0.08 (short queries without intent are news browsing)

### Search-Only Fast Path

`HasSearchOnlyMarker` applies to **all query lengths** (not just short queries). Patterns like
"convert X to Y", "define Z", "what time in Tokyo" are unambiguously factual lookups regardless
of how the embedding scores them. When detected, the type is forced to `search_only` unless
the embedding strongly disagrees (best match > 0.85 for a non-search-only type).

### Synonym Expansion

Disabled by default (`synonym_expansion_enabled: false`). When enabled, abbreviations in short
queries are expanded before embedding: "AI news" → "artificial intelligence news". Testing showed
MiniLM-L6-v2 already handles common abbreviations well without expansion.

## Exemplar System

### Structure

Each exemplar is a representative question with classification metadata:

```yaml
exemplars:
  - question: "How do I set up Docker?"
    topic: technology
    type: howto

  - question: "Give me the most depressing news you can find"
    topic: default
    type: roundup
    vibe: doom

  - question: "What are the second-order effects of rising interest rates?"
    topic: business
    type: deep_dive
    complexity: complex

  - question: "Tell me about AI news and also what's going on in politics"
    topic: ai
    type: composite
```

### Fields

| Field | Required | Values | Purpose |
|-------|----------|--------|---------|
| `question` | Yes | Free text | The representative query (gets embedded) |
| `topic` | Yes | technology, ai, programming, science, health, business, finance, politics, world, entertainment, sports, space, security, gaming, environment, crime, flooding, pharma, satire, food, transport, uk, education, default | Routing category |
| `type` | Yes | roundup, qa, howto, deep_dive, comparison, composite, search_only, trend, news | Query intent type |
| `sources` | No | List of source hints: hn, reddit, bbc, reuters, etc. | Preferred sources for this exemplar |
| `vibe` | No | doom, hopeful, snarky, funny, upbeat, friendly, toon, neutral, concise | Detected tone from query phrasing |
| `complexity` | No | simple, complex | Whether the query needs nuanced sentinel analysis |

### Coverage (v2.5)

- **444 exemplars** across 24 topics and 9 types
- **~20 topics** with 10+ exemplars each (enough for meaningful multi-match voting)
- **12+ composite** exemplars for multi-part query detection
- **5+ complex** exemplars for sentinel escalation
- **7 vibes** covered: doom, hopeful, snarky, funny, upbeat, friendly, toon
- **10+ search_only** exemplars for factual lookup detection

### Exemplar Design Principles

1. **Pattern-based, not entity-specific** — "Latest news from [region]" not "What happened to Boris Johnson?"
   NER already handles entity extraction; exemplars capture query *patterns*.

2. **Sufficient per-topic density** — at least 5-10 exemplars per topic so multi-match voting
   gets meaningful count signals, not just single-match luck.

3. **Type diversity within topics** — each topic should have roundup + at least one other type
   (howto, qa, deep_dive, comparison) to allow type disambiguation.

4. **Vibe exemplars use distinctive phrasing** — "doom-scroll the worst headlines" not just
   "bad news". The embedding needs to distinguish phrasing, not just topic words.

### User Exemplars

Users can extend or override defaults by adding YAML files to `~/.doomsummarizer/exemplars/`:

```bash
# Create the directory with a template
doomsummarizer exemplars --init

# Edit ~/.doomsummarizer/exemplars/my-exemplars.yaml
# Then rebuild embeddings:
doomsummarizer exemplars --rebuild
```

User exemplars with the same question text as a default override the default's metadata.
New questions are added to the exemplar set.

## Integration with PromptInterpreter

`PromptInterpreter.InterpretAsync()` is the main entry point for query interpretation.
The classifier integrates as the first stage:

### Strong Match Path (no sentinel)

When `BestMatchScore ≥ 0.55` and the query is neither composite nor complex:

1. Classifier returns `QueryClassification` with categories, type, vibe
2. `BuildIntentFromClassification()` converts to a `SentinelIntent`
3. `SentinelSourceMapper.ToInterpretedPrompt()` maps to sources via YAML routing
4. Result returned directly — sentinel LLM never called

### Sentinel Path (weak/composite/complex)

When the match is weak, composite, or complex:

1. Classifier still runs (categories computed)
2. Sentinel LLM called for decomposition (search queries, subqueries, filter keywords)
3. **Categories come from embedding** (not sentinel) — sentinel provides decomposition only
4. If sentinel fails, keyword-based fallback uses embedding categories for routing

### Vibe Enrichment

In the fallback path (`FallbackInterpretAsync`), when keyword-based vibe detection returns
"neutral" but embedding detected a vibe with confidence > 0.50, the embedding vibe is used.
This catches vibes from phrasing ("give me a sarcastic take") that keywords miss.

### Composite Detection

Composite queries trigger sentinel escalation for decomposition:

```
isComposite = embedding classifier IsComposite flag
              OR prompt contains "; " (semicolons always signal multi-part)
```

The classifier's `IsComposite` already incorporates both embedding similarity (consensus of
top-2 composite exemplar matches) and structural feature detection (`HasCompositeConjunction`
from regex: "and also", "as well as", "in addition to", "along with").

## Configuration

All thresholds are in the `classifier:` section of `~/.doomsummarizer/config.json`:

```yaml
classifier:
  # ── Core Thresholds ──
  min_candidate_threshold: 0.35    # Cosine sim floor to enter candidate set
  min_topic_threshold: 0.35        # Weighted vote floor for topic inclusion
  min_type_threshold: 0.30         # Weighted vote floor for type detection
  count_boost: 0.05                # IDF count boost multiplier

  # ── Detection Thresholds ──
  complex_threshold: 0.50          # Raw sim for complex exemplar flagging
  vibe_threshold: 0.70             # Raw sim for vibe detection
  composite_raw_threshold: 0.75    # Raw sim consensus for composite detection

  # ── Short-Query Features ──
  short_query_max_words: 4         # Word count threshold for "short" query
  howto_feature_boost: 0.12        # Type boost for howto intent markers
  comparison_feature_boost: 0.12   # Type boost for comparison intent markers
  default_roundup_boost: 0.08      # Type boost for unmarked short queries
  qa_feature_boost: 0.10           # Type boost for QA intent markers
  search_only_feature_threshold: 0.60  # Min confidence for search_only override
  synonym_expansion_enabled: false # Expand abbreviations before embedding
  short_query_confidence_scale: 0.85   # Scale confidence for short queries
```

### Tuning Guidelines

**More aggressive sentinel skipping** (trust embedding more):
- Lower `min_candidate_threshold` to 0.30
- Lower `vibe_threshold` to 0.60

**More sentinel usage** (better decomposition, higher latency):
- Raise `composite_raw_threshold` to 0.80 (fewer composite detections)
- Set `short_query_confidence_scale` to 0.70 (more uncertainty on short queries)

**Device profiles:**
- Desktop with GPU: defaults are fine
- Raspberry Pi: consider raising `min_candidate_threshold` to 0.40 (fewer candidates to vote on)

## CLI Commands

```bash
# Show exemplar summary (topic/type counts, user dir status)
doomsummarizer exemplars

# List all exemplars in a table
doomsummarizer exemplars --list

# Create user exemplar directory with template
doomsummarizer exemplars --init

# Re-embed all exemplars and show sample classifications
doomsummarizer exemplars --rebuild

# Validate YAML files for errors
doomsummarizer exemplars --validate

# Quiet rebuild (no sample output)
doomsummarizer exemplars --rebuild --quiet

# Use specific GPU for rebuild
doomsummarizer exemplars --rebuild --gpu 1
```

### Debug Output

With `scroll "query" --debug`, the classifier output appears as:

```
Embedding: technology=0.93, ai=0.54 | type=roundup (0.93) | vibe=doom (0.72) | composite | complex
  Top matches: "What's the latest tech news?" (0.93), "Show me programming articles" (0.71), ...
Final: technology=0.93 | intent=roundup | vibe=doom
```

The `--rebuild` command shows a classification table with flags:
- **C** = composite detected
- **X** = complex detected
- **F** = structural features contributed (short query)

## Classification Result

`QueryClassification` contains all extracted signals:

| Property | Type | Description |
|----------|------|-------------|
| `Categories` | `Dictionary<string, double>` | Topic → weighted vote score |
| `QueryType` | `string` | Best detected type (roundup, howto, qa, etc.) |
| `QueryTypeConfidence` | `double` | Confidence score for the detected type |
| `Vibe` | `string?` | Detected vibe (doom, snarky, etc.) or null |
| `VibeConfidence` | `double` | Confidence of vibe detection |
| `IsComposite` | `bool` | Multi-part query needing decomposition |
| `IsComplex` | `bool` | Complex query benefiting from sentinel nuance |
| `SourceHints` | `List<string>?` | Preferred sources from best-matching exemplar |
| `BestMatch` | `string?` | Best-matching exemplar question (debug) |
| `BestMatchScore` | `double` | Similarity of the best match |
| `TopMatches` | `List<ScoredExemplar>` | Top 5 matches (debug) |
| `Features` | `object?` | Structural features for short queries |

## Performance

| Operation | Time | Notes |
|-----------|------|-------|
| Initialization (batch embed 444 exemplars) | ~150ms | Single ONNX call, once at startup |
| Feature extraction | < 0.02ms | Pre-compiled source-generated regexes |
| Query embedding | ~1ms | Single ONNX inference |
| Score all 444 exemplars | < 1ms | SIMD cosine similarity, single pass |
| Weighted voting (2 dimensions) | < 0.1ms | Dictionary accumulation |
| **Total per-query** | **< 2ms** | Compared to 200-800ms for sentinel LLM |

Memory: ~444 × 384 × 4 bytes = ~680KB for exemplar embeddings (all-MiniLM-L6-v2, 384 dimensions).

## Test Coverage

875 tests pass, including:

- **Unit tests**: Exemplar loading, count, types, vibes, complexity, no duplicates, topic coverage
- **Integration tests** (ONNX): Topic detection (15 queries × expected topic), type detection (5 types),
  vibe detection (4 vibes), composite detection (3 queries), complex detection (2 queries),
  search-only detection (4 queries), strong match rate > 70%, determinism (3 runs identical),
  niche topic coverage (flooding, crime, pharma, satire, gaming, food, transport)

Run tests:
```bash
# Unit tests (no ONNX needed)
dotnet test src/DoomSummarizer.Tests/ --filter "Category!=Browser&Category!=Integration"

# Integration tests (requires ONNX model files)
dotnet test src/DoomSummarizer.Tests/ --filter "Category=Integration"
```

## Evolution History

| Phase | What Changed |
|-------|-------------|
| 1.0 | Max-per-group scoring, 76 exemplars, topic + type only |
| 2.0 | Multi-match IDF-weighted voting, 444 exemplars, vibe/composite/complex detection |
| 2.5 | Short-query feature decomposition, single-pass scoring, centroid filter removed, all thresholds YAML-configurable |
