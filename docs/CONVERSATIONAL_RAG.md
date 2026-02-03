# Conversational RAG: Multi-Turn Chat System

The `ask` command supports multi-turn conversational RAG that combines search salience
with traditional LLM conversation. This document describes the architecture and how
the caching layers work together.

## Problem

In a naive RAG chat loop, follow-up questions like "Tell me more about that" or
"How does it compare?" fail because:

- Retrieval uses the **raw question** only, which lacks context
- "Tell me more about that" retrieves unrelated results
- Conversation history is truncated and only injected into the synthesis prompt
- Cached segments from earlier turns are lost

## Architecture

```
User Question
    |
    v
[1] ConversationSentinel
    - Detects follow-ups (pronouns, continuation markers, semantic similarity)
    - Rewrites query into standalone form for retrieval
    - Rule-based first (<5ms), LLM fallback (~200ms) when needed
    |
    v
[2] RetrievalPipeline.SearchAsync(resolvedQuery)
    - Searches knowledge base with the resolved (standalone) query
    |
    v
[3] SalientSegmentCache + PromptSalienceIndex
    - Merges fresh results with salient cached segments
    - PromptSalienceIndex: O(1) lookup for similar past queries
    - SalientSegmentCache: LFU eviction keeps relevant context alive
    |
    v
[4] ContextCompressor + ask-answer.txt
    - Compresses conversation history to fit model context window
    - Three tiers: passthrough / extractive / abstractive
    |
    v
[5] ChatCorpusService (async, non-blocking)
    - Indexes Q+A turn into SQLite + FTS5
    - Enables cross-session memory
```

## Components

### ConversationSentinel

**File:** `src/DoomSummarizer.Core/Services/ConversationSentinel.cs`

Runs before retrieval to detect whether a question is a follow-up and, if so,
rewrite it into a standalone query.

**Tier 1 - Rule-based (no LLM, <5ms):**

| Signal | Example | Action |
|--------|---------|--------|
| Continuation markers | "tell me more", "what about", "go on" | Rewrite with previous topic |
| Pronouns | "it", "that", "they" | Replace with extracted topic |
| Semantic similarity > 0.7 | Same topic rephrased | Append context from previous turn |
| Semantic similarity 0.4-0.7 | Related topic | Treat as new query, cache still helps |
| Semantic similarity < 0.4 | Topic change | Fresh query, new topic |

**Tier 2 - LLM-assisted:**

Only fires when Tier 1 detects a follow-up. Uses `SentinelGenerateJsonAsync`
(fast model, 0.1 temperature, JSON format) to rewrite the query into a fully
standalone form with all pronouns and references resolved.

**Example:**

```
Turn 1: "What are knowledge graphs used for in AI?"
Turn 2: "Tell me more about Med-Graph specifically"
  -> Resolved: "What is Med-Graph and how is it used in knowledge graph AI?"
Turn 3: "How does it compare to traditional approaches?"
  -> Resolved: "How does Med-Graph compare to traditional knowledge graph approaches in AI?"
```

### SalientSegmentCache

**File:** `src/DoomSummarizer.Core/Services/SalientSegmentCache.cs`

In-memory per-session LFU cache that keeps frequently-relevant segments alive
across conversation turns.

**Capacity:** 50 segments (default) = ~25KB of context

**Operations:**

- `AddRange(items, turn)` - Cache fresh retrieval results
- `GetSalient(queryEmbedding, turn)` - Get segments relevant to current query
- `Evict(turn)` - Remove stale (>5 turns old) and low-frequency segments

**Eviction policy:**

1. **Staleness first:** Segments not accessed in 5 turns are evicted
2. **LFU second:** When over capacity, lowest `AccessCount` segments are evicted
3. **Prompt index pruning:** After eviction, prompt index entries pointing to
   evicted segments are cleaned up

**Merge strategy:**

```
freshResults  = retrieval.SearchAsync(resolvedQuery)     // 10-20 items
cachedSalient = cache.GetSalient(queryEmbedding, turn)   // 0-10 items

combined = freshResults
    .Concat(cachedSalient.Where(not in fresh))
    .Take(topK)
```

Fresh results always take priority. Cached segments fill remaining slots.

### PromptSalienceIndex

**File:** `src/DoomSummarizer.Core/Services/PromptSalienceIndex.cs`

A prompt-level cache that sits in front of `SalientSegmentCache.GetSalient()`.
Maps prompt embeddings to their previously-resolved salient segment sets.

**Why it matters:**

Without the index, `GetSalient()` computes cosine similarity between the query
embedding and every cached segment (N comparisons per query). With the index,
a similar past query's result is returned in O(K) where K = number of cached
prompts (typically K << N, and early-exit on first match above threshold).

**How it works:**

```
Query arrives -> embed -> check PromptSalienceIndex
                              |
                  cos(query, cached_prompt) >= 0.85?
                      |                 |
                     YES               NO
                      |                 |
              Return cached          Full cosine scan
              segment IDs           against all segments
                                         |
                                  Record result in index
```

**Configuration:**

| Parameter | Default | Description |
|-----------|---------|-------------|
| `capacity` | 30 | Max cached prompt entries (LRU eviction) |
| `hitThreshold` | 0.85 | Cosine similarity threshold for cache hit |

**Diagnostics (via `/cache` or `/stats`):**

```
Prompt Salience Index (12 entries)
  Hits: 8, Misses: 15, Hit rate: 35%
  (Each hit saves 47 cosine similarity computations)
```

**Consistency:**

When `SalientSegmentCache.Evict()` removes segments, `PromptIndex.Prune()` is
called automatically to remove or trim entries that reference evicted segments.
This prevents stale cache hits returning segment IDs that no longer exist.

### ContextCompressor

**File:** `src/DoomSummarizer.Core/Services/ContextCompressor.cs`

Compresses conversation history to fit within the target model's context window.

**Tiered strategy:**

| History size | Strategy | Method |
|-------------|----------|--------|
| < 7000 chars (~2000 tokens) | Passthrough | Return as-is |
| 7000-21000 chars | Extractive | Keep sentences most similar to current query |
| > 21000 chars | Abstractive | LLM summarizes chunks, then extractive on summaries |

**Extractive compression:**

1. Split history into sentences with turn metadata
2. Embed all sentences + current query
3. Rank sentences by cosine similarity to query
4. Take top sentences until token budget filled
5. Re-sort by chronological order (preserves coherence)

**Abstractive compression:**

1. Chunk history into ~7000-char windows
2. Sentinel model summarizes each chunk (2-3 sentences)
3. If summaries still too large, apply extractive step on them

The compressed context is injected into the `ask-answer.txt` template via
`{{CONVERSATION_CONTEXT}}`, replacing the old naive approach (last 3 turns,
200-char truncation per answer).

### ChatCorpusService

**File:** `src/DoomSummarizer.Core/Services/ChatCorpusService.cs`

Indexes conversation turns as searchable ContentItems using existing storage
infrastructure. Runs fire-and-forget (non-blocking) after each answer is displayed.

**What gets indexed per turn:**

- ContentItem with `Id = "chat:{sessionId}:{turnNumber:D3}"`
- Source: `"chat:{sessionId}"`
- Full Q+A content as searchable text
- Keywords extracted via `DocumentProfileService.ExtractProfile`
- 384-dim embedding via ONNX
- FTS5 index entry for keyword search

**Implicit feedback:**

When the ConversationSentinel detects a follow-up (user continued the same topic),
the previous turn gets a positive signal via the `item_usage` table (increments
`access_count`). This means frequently-discussed topics accumulate higher
usage scores over time.

## Slash Commands

Available during interactive ask mode:

| Command | Description |
|---------|-------------|
| `/docs` | Show documents from last query |
| `/segments` | Show all source segments across conversation with frequency |
| `/cache` | Show cached segment IDs + prompt index stats |
| `/resolve <query>` | Preview how a query would be resolved (rule-based only) |
| `/stats` | Session statistics (turns, sources, cache, saved computations) |
| `/help` | List available slash commands |

## Data Flow Example

```
Turn 1: "What are knowledge graphs used for in AI?"
  Sentinel: First turn, no rewrite needed
  Retrieval: 15 fresh results from KB
  Cache: 15 segments added (cache: 15/50)
  Prompt Index: Records embedding -> [15 segment IDs]
  Answer: "Knowledge graphs impose logic on generative AI..."
  ChatCorpus: Indexes turn as chat:abc123:001

Turn 2: "Tell me more about Med-Graph specifically"
  Sentinel: Continuation marker detected -> "More details about knowledge graphs"
  Sentinel (LLM): "What is Med-Graph and how is it used in knowledge graph AI?"
  Retrieval: 12 fresh results using resolved query
  Cache: GetSalient returns 5 relevant cached segments from Turn 1
    -> Prompt Index: miss (new topic focus), full scan
  Merge: 12 fresh + 3 cached (deduplicated) = 15 combined
  Cache: Now 27/50 segments, prompt index records new mapping
  Answer: Uses ContextCompressor (extractive) on Turn 1 history
  ChatCorpus: Indexes turn, records positive feedback on Turn 1

Turn 3: "What about in healthcare specifically?"
  Sentinel: "What about" + semantic sim 0.75 -> follow-up
  Sentinel (LLM): "How are knowledge graphs and Med-Graph used in healthcare AI?"
  Retrieval: 10 fresh results
  Cache: GetSalient -> Prompt Index hit! (similar to Turn 2)
    -> Returns cached segment IDs in O(1), skips 27 cosine computations
  Merge: 10 fresh + 4 cached = 14 combined
  Answer: Uses ContextCompressor on Turns 1-2 history
```

## Configuration

The cache sizes are tunable at construction:

```csharp
// In InteractiveAskLoop.RunAsync()
var segmentCache = new SalientSegmentCache(
    capacity: 50,               // Max cached segments
    promptIndexCapacity: 30,    // Max cached prompt embeddings
    promptHitThreshold: 0.85f   // Cosine sim for prompt cache hit
);
```

Increasing `promptHitThreshold` (e.g., 0.90) makes the prompt cache more
conservative (fewer hits, but higher precision). Decreasing it (e.g., 0.80)
gives more hits but risks returning stale segment sets for queries that are
similar but not identical in intent.
