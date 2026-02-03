# Adding Biographical Memory to a Reduced RAG

A RAG system that can't remember its user is a stranger every session. When someone
asks "What's the weather here?" and the system doesn't know where "here" is, the
question routes to generic results. This document describes how we added persistent
user memory to DoomSummarizer using the entity infrastructure that already existed
— no new databases, no new models, no new indexing pipeline.

## Problem

Consider a typical RAG conversation:

```
Session 1:
  User: I live in London and work at Anthropic on AI safety
  System: [answers question, forgets everything]

Session 2:
  User: What's the weather here?
  System: [has no idea what "here" means]

  User: My company's latest research
  System: [doesn't know the user works at Anthropic]
```

The system treats every implicit reference as an unsolvable gap. Users end up
repeating context every session.

## Core Insight: Personal Facts Are Just Entities

DoomSummarizer already has a full entity and retrieval pipeline:

1. **NER** (ONNX-based Named Entity Recognition) — extracts entities from text
2. **KnowledgeGraphService** — ingests entities, builds co-occurrence graphs
3. **Entity profiles** — TF*IDF*confidence scoring, HNSW vector search
4. **Lucene.NET** — primary keyword search with BM25 scoring, field boosting
   (title 3x, keywords 2.5x, content 1x), fuzzy matching, phrase proximity
5. **5-signal RRF retrieval** — QuerySim + TextRelevance + Freshness + Authority +
   Quality, with query-type-adaptive half-lives for freshness decay
6. **StorageService** — SQLite metadata with source-filtered queries

A personal fact like "User lives in London and works at Anthropic" is just another
piece of text with entities (London=LOC, Anthropic=ORG). The only difference is its
source tag: `personal:default` instead of `hn` or `crawl:docs`.

Rather than building a separate user profile store, we treat personal information
as a **first-class corpus** that flows through the existing pipeline.

## Architecture

```
User: "I live in London and work at Anthropic on AI safety"
                    |
                    v
[1] Self-Disclosure Detection
    Sentinel LLM (Ollama) or rule-based fallback
    -> Classifies: contains personal facts?
    -> Extracts: "User lives in London; works at Anthropic on AI safety"
                    |
                    v
[2] Index into personal:{name} corpus
    -> Embed (ONNX all-MiniLM-L6-v2)
    -> Keywords extraction + SQLite persistence
    -> NER: London (LOC), Anthropic (ORG), AI safety (MISC)
    -> KnowledgeGraph: entity nodes, co-occurrence edges, profiles
                    |
                    v
[3] Stored as: items with source="personal:default"
    Entities in knowledge graph linked to personal: items
    Entity profiles in HNSW index
    Lucene.NET incrementally indexes on next search

--- Later session ---

User: "What's the weather here?"
                    |
                    v
[4] Gap-filling in ConversationSentinel
    -> Detects: "here" = location gap
    -> Queries personal: entities for type=LOC
    -> Finds "London" (highest freshness score)
    -> Resolves: "What's the weather in London?"
                    |
                    v
[5] Normal retrieval with resolved query
    USER_CONTEXT injected: "User is in London"
```

## Self-Disclosure Detection

The first challenge is recognizing when a user is sharing a personal fact vs.
asking a question. "I live in London" is self-disclosure; "What's London like?"
is not.

**Two-tier approach:**

1. **Sentinel LLM** (when Ollama is available): A focused prompt asks the LLM to
   classify the message and extract any personal facts as clean statements.

2. **Rule-based fallback** (offline/no-LLM mode): Pattern matching on explicit
   first-person constructions:

```
"I live in X"        -> "User lives in X"
"I'm based in X"     -> "User lives in X"
"I work at X"        -> "User works at X"
"I'm a X"            -> "User is a X"
"I use X"            -> "User uses X"
"I prefer X"         -> "User prefers X"
```

The rule-based fallback only catches very explicit patterns. It deliberately
avoids false positives — missing a self-disclosure is acceptable, incorrectly
indexing "What's the weather?" as a personal fact is not.

### Quick-exit optimization

Before running either detection path, a `ContainsFirstPerson` check scans for
first-person pronouns ("I", "my", "I'm", "I am"). Most questions don't contain
these, so the fast path rejects them immediately.

## Indexing: Dual-Layer Search Integration

Once a personal fact is detected, it flows through the same pipeline as any
document content. DoomSummarizer uses a dual-layer search architecture:

- **Lucene.NET** — primary keyword search with BM25 scoring, field boosting,
  fuzzy matching, and phrase proximity. Indexes are file-based, persistent,
  and incrementally updated at search time.
- **SQLite** — metadata store with source-filtered queries for item retrieval.

Personal facts plug into both layers:

```csharp
// Step 1: Embed (ONNX all-MiniLM-L6-v2)
item = item with { Embedding = await _embedding.EmbedAsync(statement, ct) };

// Step 2: Keywords + SQLite persistence
var profile = DocumentProfileService.ExtractProfile(item.Title, statement);
await _storage.SaveItemAsync(item);
await _storage.IndexDocumentFtsAsync(item.Id, item.Title, profile.KeywordsText, statement);

// Step 3: NER -> entity graph -> co-occurrence -> entity profiles
var entities = await _ner.ExtractEntitiesAsync(statement, ct);
await _knowledgeGraph.IngestEntitiesAsync([(item, entities)], ct);
```

At retrieval time, `RetrievalPipeline.SearchAsync()` handles the Lucene side:
it opens the collection's Lucene index, incrementally indexes any items not yet
in Lucene (including newly-added personal facts), then runs a multi-field query
with field boosting (title 3x, keywords 2.5x, content 1x). The results feed
into the 5-signal RRF fusion alongside embedding similarity, entity profiles,
freshness, and quality signals.

The item gets a source tag like `personal:default` (or `personal:scott` for
named corpuses). This source tag drives the filter logic described below.

## Gap-Filling: Resolving Implicit References

The ConversationSentinel, which already handles pronoun resolution and follow-up
detection for conversational RAG, gains a new resolution step:
`ResolveFromPersonalAsync`.

**Gap detection** uses simple keyword checks (not regex extraction):

| Gap type | Trigger words | Resolution |
|----------|---------------|------------|
| Location | "here", "locally", "nearby", "my area", "my city" | Query personal LOC entities |
| Organization | "my company", "my org", "at work", "my team", "my employer" | Query personal ORG entities |
| Tech stack | "my stack", "my project", "my codebase", "my setup" | Query personal MISC entities |

When a gap is detected and a matching personal entity exists, the query is
rewritten:

```
"What's the weather here?" -> "What's the weather in London?"
"My company's policies"    -> "Anthropic policies"
```

The personal context string (e.g., "User is in London; Works at Anthropic") is
also passed to the synthesis prompt via the `{{USER_CONTEXT}}` template variable,
allowing the LLM to personalize its response.

## Overwrite Semantics: Freshness Wins

No special overwrite logic is needed. The existing freshness infrastructure
handles it naturally through two complementary scoring layers:

**Entity freshness** (used for gap-filling, in `ContentItem.FreshnessScore`):
```
MentionCount * exp(-ageHours / 72)    // 72h half-life
```

**Retrieval freshness** (used during 5-signal RRF scoring, in `RelevanceScorer`):
```
exp(-ageHours * ln(2) / halfLifeHours)    // query-type-adaptive half-life
```

The RRF freshness signal adapts its half-life per query type — timeline queries
weight recency highest, explainer queries weight it lowest. Personal entity
ranking uses the simpler 72-hour decay since personal facts don't vary by
query type.

When the user says "I moved to Berlin":
- A new personal item is created with Berlin (LOC)
- Berlin gets a fresh `last_seen` timestamp
- London's freshness decays over time
- When resolving "here", Berlin ranks higher than London

For hard replacement (e.g., employer change), the `/forget` command removes
old items directly.

## Source Filter Mechanics

Personal items are **excluded from evidence** but their **entities remain in
the knowledge graph**.

```
AppendFilterClauses logic:

1. chat: items   -> excluded by default (in-session only)
2. personal: items -> excluded by default (context injected separately)
3. All entities    -> always available in knowledge graph
```

This means "User lives in London" never appears as a search result row, but
the London entity from that item participates in entity-profile-based retrieval
and gap-filling. The user's context is injected as a separate preamble in the
synthesis prompt, not mixed into evidence.

## Named Personal Corpuses

When a user identifies themselves ("My name is Scott"), the system:

1. Detects the name introduction
2. Migrates any existing `personal:default` facts to `personal:scott`
3. Switches the active corpus

Name detection uses pattern matching with validation:
- "My name is X", "Call me X" — direct introduction
- "I'm X", "I am X" — only when X looks like a proper name (capitalized,
  1-3 words, not a common role/adjective)

A blocklist prevents false positives: "I'm a developer", "I'm happy", "I'm
based in London" are all correctly rejected.

Multiple corpuses can coexist (`/whois` lists them, `/whois scott` switches).
All slash commands (`/me`, `/remember`, `/forget`, `/personal`) operate on the
active corpus.

## User Commands

| Command | Action |
|---------|--------|
| `/me` | Show all personal facts for the active corpus |
| `/remember <statement>` | Manually add a fact: `/remember I prefer dark mode` |
| `/forget [type]` | Remove facts: `location`, `org`, `tech`, `all` |
| `/personal` | Show personal entity graph (entities + relationships) |
| `/whois [name]` | List/switch personal corpuses |

## Template Integration

The `ask-answer.txt` prompt template includes:

```
{{USER_CONTEXT}}
{{CONVERSATION_CONTEXT}}
QUESTION: {{QUESTION}} | DATE: {{TODAY}}
```

When personal context exists:
```
USER CONTEXT: User is in London; Works at Anthropic; Uses .NET, C#
```

When it doesn't exist, `{{USER_CONTEXT}}` evaluates to empty — invisible.

## What We Didn't Build

The entire feature reuses existing infrastructure:

- **No new database tables** — personal items use the existing `items` table
  with a `personal:` source prefix
- **No new embedding model** — same ONNX all-MiniLM-L6-v2
- **No new NER model** — same ONNX NER model extracts entities
- **No new entity store** — entities flow into the same knowledge graph
- **No new vector index** — entity profiles use the same HNSW index
- **No new search index** — Lucene.NET incrementally picks up personal items
  at retrieval time; the 5-signal RRF pipeline scores them alongside everything
  else
- **No new scoring** — personal facts participate in the same QuerySim +
  TextRelevance + Freshness + Authority + Quality fusion

The only new code is the thin orchestrator (`PersonalCorpusService`), the
gap-filling logic in `ConversationSentinel`, and the template variable wiring.

## Test Coverage

67 tests covering:

- **Self-disclosure detection** (12 Theory cases): Both positive patterns
  ("I live in X", "I work at X") and negative patterns ("What's the weather?")
- **Fact extraction**: Verb normalization, clean statement extraction
- **Indexing**: SQLite storage, embedding generation, keyword extraction, named
  corpus source tags
- **Forget**: By keyword, by entity type, forget-all, empty corpus
- **Source filter exclusion**: Personal items excluded from default retrieval
  but included when explicitly requested
- **End-to-end**: Detect -> index -> retrieve cycle
- **Gap detection**: Location, organization, and tech stack gap recognition
  via reflection on private static methods
- **Named corpuses**: Name detection (5 positive, 6 negative cases), migration
  from default to named, isolation between corpuses, active corpus listing

All tests run against real SQLite databases (temp files, cleaned up in
DisposeAsync) with real ONNX embeddings — no mocks.
