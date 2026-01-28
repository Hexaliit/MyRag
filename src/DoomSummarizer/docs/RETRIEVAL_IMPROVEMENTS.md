# DoomSummarizer Retrieval Improvements Spec

## Current State

The pipeline uses RRF (Reciprocal Rank Fusion) to combine signals:
- **BM25F** - keyword matching with field boosting (title 3x, keywords 2.5x, content 1x)
- **Embedding similarity** - semantic matching via all-MiniLM-L6-v2 (384-dim)
- **Freshness** - exponential decay with 48h half-life
- **Authority** - domain-based scoring
- **Vibe alignment** - tone matching
- **Entity profile similarity** - TF×IDF×confidence weighted entity embeddings (NEW)

Each signal provides evidence of salience; none is the final arbiter.

## Implemented (This Session)

- [x] **GPU auto-detection** - DirectML for Windows GPUs (AMD/Intel/NVIDIA), CUDA fallback
- [x] **Improved Lucene queries** - title boosting, phrase detection, technical term handling
- [x] **Sentinel filter extraction** - `corrected_query`, `filter_keywords`, `lucene_query` fields
- [x] **Shared stopword list** - uses dotnet-stop-words package via StopwordLists.cs
- [x] **Performance optimizations** - AggressiveInlining on hot paths, removed redundant ToLowerInvariant()
- [x] **Entity Profile HNSW** - Semantic entity similarity replacing naive shared-count (see below)
- [x] **Semantic outlier detection** - Uses query embedding for outlier filtering (catches synonyms)

---

## Entity-Based Semantic Retrieval (NEW)

### What
Each document gets a 384-dim **entity profile embedding** that encodes its "entity fingerprint":
- Which entities appear (via their text embeddings)
- How distinctive each entity is (Entity IDF - rare entities matter more)
- How frequently each entity appears (saturating TF to prevent boilerplate dominance)
- How confident the NER was (confidence weighting with floor)
- Entity type weighting (ORG 1.2×, PER 1.1×, LOC 1.0×, MISC 0.9×)

### Why
Previous approach counted shared entities: `shared_count >= 2`. This fails when:
- "Apple Inc" + "California" matches BOTH tech articles AND fruit farming articles
- O(N²) SQL joins for every graph enrichment query

New approach uses HNSW on entity profiles:
- O(log N) retrieval via cosine similarity
- Tech articles cluster together (similar entity embeddings)
- Semantic entity matching: "OpenAI" and "Anthropic" are close in embedding space

### Formula

```
TF(e, d) = 1 + log(mentions_in_doc(e, d))     // Saturating to prevent boilerplate dominance
IDF(e) = log((N+1)/(df(e)+1)) + 1              // Smoothed for stable behavior at extremes
confidence' = clamp(confidence, 0.2, 1.0)      // Floor prevents vanishing low-confidence entities
type_weight = {ORG: 1.2, PER: 1.1, LOC: 1.0, MISC: 0.9}
weight = TF × IDF × confidence' × type_weight

doc_entity_profile = L2_normalize(Σ entity_embedding(e) × weight)
```

### Files Changed
- `Services/EntityProfileService.cs` - NEW: Compute TF×IDF×confidence weighted entity profiles
- `Services/DuckDbVectorStore.cs` - Added entity_profile column + HNSW index
- `Services/KnowledgeGraphService.cs` - Computes & stores entity profiles during ingestion
- `Commands/ScrollCommand.cs` - Uses entity profile HNSW for graph enrichment
- `Services/RelevanceScorer.cs` - Passes query embedding to outlier detection

### Debug Output
With `--debug` flag, users see entity profile HNSW results:
```
[grey]Entity profile HNSW: 3 candidates from 2 query entities[/]
  ⤷ Microsoft's Role in AI Governance: 0.852
  ⤷ OpenAI Regulatory Challenges: 0.789
  ⤷ EU AI Act Explained: 0.721
```

### Backfill Command
For existing KB items with entity mentions but no entity profiles:
```bash
doomsummarizer scroll --backfill-entity-profiles
```

### How It Works
1. **Ingestion**: When `--graph` is used with `--entities`, entity profiles are computed and stored
2. **Query**: Sentinel extracts query entities (e.g., "OpenAI", "regulation")
3. **Search**: Three candidate sources are fused:
   - Lucene (keyword matching with boosting)
   - Embedding (semantic similarity)
   - Entity Profile HNSW (entity-to-entity semantic matching)
4. **RRF**: Candidates are scored and ranked using existing 6-signal RRF

---

## Phase 1: Cross-Encoder Reranking

**What**: A second-pass neural model that scores query-document pairs together (vs bi-encoder which embeds separately).

**Why**: Cross-encoders are 10-20% more accurate than bi-encoders for relevance scoring. They catch semantic nuances that embedding similarity misses.

**Implementation**:
1. Add `ms-marco-MiniLM-L-6-v2` cross-encoder model (~80MB ONNX)
2. After RRF produces top-50, rerank to top-10 with cross-encoder
3. Cache cross-encoder scores by (query_hash, doc_id) for repeated queries

**Files**:
- `Services/CrossEncoderService.cs` - NEW: ONNX cross-encoder inference
- `Services/RelevanceScorer.cs` - Add `RerankedTop` method after `ScoreFull`

**Effort**: Medium (2-3 hours)

---

## Phase 2: Query Expansion

**What**: Automatically add synonyms and related terms to improve recall.

**Why**: User query "ML frameworks" should also find docs mentioning "machine learning libraries", "PyTorch", "TensorFlow".

**Implementation**:
1. Use embedding similarity to find related terms from indexed vocabulary
2. Expand acronyms (ML → "machine learning", LLM → "large language model")
3. Add top-3 synonym terms to Lucene query with lower boost (0.5x)

**Files**:
- `Services/QueryExpansionService.cs` - NEW: synonym lookup, acronym expansion
- `Services/SentinelSourceMapper.cs` - Add `expanded_terms` field to SentinelIntent

**Effort**: Medium (2-3 hours)

---

## Phase 3: Entity Linking

**What**: Connect extracted entities to knowledge bases (Wikipedia, Wikidata) for disambiguation and enrichment.

**Why**: "Apple" in a tech context → Apple Inc. "Apple" in food context → fruit. Linking provides context.

**Implementation**:
1. Use existing NER entities from QueryPreprocessor
2. Query Wikidata API for entity disambiguation
3. Add entity metadata (description, type, aliases) to search context
4. Use linked entities for graph enrichment queries

**Files**:
- `Services/EntityLinkingService.cs` - NEW: Wikidata API integration
- `Services/NerService.cs` - Add `LinkedEntity` record with Wikidata ID

**Effort**: Medium-High (3-4 hours)

---

## Phase 4: Adaptive Chunking

**What**: Different chunk sizes for different content types.

**Why**: Code needs smaller chunks (function-level). Prose needs larger chunks (paragraph-level). Current fixed 512-token chunks are suboptimal.

**Implementation**:
1. Detect content type (code, prose, list, table)
2. Apply type-specific chunking: code=256, prose=512, list=128
3. Overlap chunks by 10% for context continuity

**Files**:
- `Services/ArticleProcessor.cs` - Add content-type detection and adaptive chunking
- `Models/ContentSegment.cs` - Add `ChunkType` enum

**Effort**: Medium (2-3 hours)

---

## Phase 5: Late Interaction (ColBERT-style)

**What**: Token-level matching instead of single embedding per document.

**Why**: Captures fine-grained matches that single-vector embeddings miss. "Python web framework" matches "Flask is a Python microframework for web" better.

**Implementation**:
1. Store per-token embeddings (first 64 tokens per doc)
2. MaxSim scoring: for each query token, find max similarity across doc tokens
3. Sum MaxSim scores for final relevance

**Files**:
- `Services/ColBertService.cs` - NEW: token-level embedding and MaxSim
- `Services/DuckDbVectorStore.cs` - Add token embedding table

**Effort**: High (4-6 hours, requires schema changes)

---

## Phase 6: Learned Fusion Weights

**What**: Learn optimal RRF weights from user feedback instead of hand-tuned constants.

**Why**: Current weights (BM25=1.0, freshness=0.6, etc.) are guesses. Learn from click-through data.

**Implementation**:
1. Log (query, clicked_item, shown_items) tuples
2. Train logistic regression on signal values → click probability
3. Use learned coefficients as RRF weights

**Files**:
- `Services/FeedbackService.cs` - NEW: log user interactions
- `Services/RelevanceScorer.cs` - Load weights from config/model

**Effort**: High (needs feedback collection infrastructure)

---

## Priority Order

1. **Cross-Encoder Reranking** - Biggest accuracy win with moderate effort
2. **Query Expansion** - Good recall improvement
3. **Adaptive Chunking** - Better for mixed-content KBs
4. **Entity Linking** - Valuable for named entity heavy queries
5. **Late Interaction** - Diminishing returns unless precision is critical
6. **Learned Fusion** - Needs usage data, defer until user base grows

---

## CLI UX Improvements (Requested)

- [ ] **Multiple panels** - Use Spectre.Console Layout for side-by-side results
- [ ] **Markdown rendering** - Render markdown content with Spectre markup
- [ ] **Clickable URLs** - Use ANSI OSC 8 hyperlinks for terminal support
- [ ] **Progress panels** - Show fetch/analyze/generate in parallel progress bars
