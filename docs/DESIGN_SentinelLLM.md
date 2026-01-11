# Sentinel LLM: Signal-Aware Query Routing

## Overview

The Sentinel LLM is a lightweight routing layer that uses a tiny LLM (TinyLlama/Phi-3-mini) combined with pgvector similarity search to dynamically determine which signals, filters, and retrieval strategies to use for a given query.

## Problem Statement

Current RAG queries use static filter configurations. Users must manually specify:
- File type filters
- Collection filters
- Date ranges
- Entity/community filters
- Signal thresholds

The Sentinel LLM automates this by learning from query patterns and available signals.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         User Query                              │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Sentinel LLM Router                          │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  1. Embed query → pgvector similarity search               │ │
│  │  2. Find similar historical queries with their signals     │ │
│  │  3. Fetch available signals for matched content            │ │
│  │  4. Pass to TinyLLM for routing decision                   │ │
│  └─────────────────────────────────────────────────────────────┘ │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                   TinyLLM Decision                              │
│  Input: Query + Available Signals + Similar Queries             │
│  Output: FilterParams JSON                                      │
│  {                                                              │
│    "file_types": ["pdf", "docx"],                              │
│    "collections": ["technical-docs"],                          │
│    "date_range": { "after": "2024-01-01" },                    │
│    "signals": ["transcription.confidence > 0.8"],              │
│    "communities": ["database-systems"],                         │
│    "retrieval_strategy": "hybrid_rrf",                         │
│    "expansion_depth": 1                                        │
│  }                                                              │
└─────────────────────────┬───────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                  Filtered RAG Retrieval                         │
│  Uses generated filters for vector + BM25 + graph search        │
└─────────────────────────────────────────────────────────────────┘
```

## Signal Embedding Store

### Table: `signal_embeddings`

```sql
CREATE TABLE signal_embeddings (
    id UUID PRIMARY KEY,
    signal_name VARCHAR(128) NOT NULL,        -- e.g., "transcription.confidence"
    signal_description TEXT,                   -- Human-readable description
    signal_type VARCHAR(32),                   -- metadata, acoustic, speaker, etc.
    value_type VARCHAR(32),                    -- numeric, string, boolean, json
    typical_values JSONB,                      -- Example values for context
    embedding VECTOR(384),                     -- ONNX embedding of description
    usage_count INT DEFAULT 0,                 -- How often this signal is used
    avg_correlation FLOAT,                     -- Correlation with query success
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_signal_embeddings_vector ON signal_embeddings
    USING ivfflat (embedding vector_cosine_ops) WITH (lists = 50);
```

### Table: `query_signal_history`

Tracks which signals were useful for past queries (for learning).

```sql
CREATE TABLE query_signal_history (
    id UUID PRIMARY KEY,
    query_text TEXT NOT NULL,
    query_embedding VECTOR(384),
    filters_used JSONB,                        -- Filters applied to this query
    signals_matched JSONB,                     -- Signals that contributed to results
    result_count INT,                          -- Number of results returned
    user_satisfaction FLOAT,                   -- Optional: user feedback score
    latency_ms INT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_query_history_embedding ON query_signal_history
    USING ivfflat (query_embedding vector_cosine_ops) WITH (lists = 100);
```

## Signal Catalog

Available signals the Sentinel can route to:

### Document Signals
| Signal | Type | Description |
|--------|------|-------------|
| `doc.type` | string | File type (pdf, docx, md, etc.) |
| `doc.page_count` | int | Number of pages |
| `doc.has_tables` | bool | Contains extracted tables |
| `doc.language` | string | Detected language |
| `doc.reading_level` | string | Complexity (simple/moderate/advanced) |

### Audio Signals
| Signal | Type | Description |
|--------|------|-------------|
| `transcription.confidence` | float | Transcription confidence (0-1) |
| `transcription.language` | string | Detected spoken language |
| `speaker.count` | int | Number of speakers |
| `speaker.classification` | string | single/two/multi speaker |
| `audio.content_type` | string | speech/music/mixed/silence |
| `music.bpm` | float | Beats per minute |
| `music.key` | string | Musical key |

### Image Signals
| Signal | Type | Description |
|--------|------|-------------|
| `image.has_text` | bool | Contains OCR text |
| `image.ocr_confidence` | float | OCR confidence |
| `image.caption` | string | AI-generated caption |
| `image.scene_type` | string | Scene classification |

### Graph Signals
| Signal | Type | Description |
|--------|------|-------------|
| `entity.types[]` | string[] | Entity types in content |
| `entity.count` | int | Number of entities |
| `community.name` | string | Graph community membership |
| `community.importance` | float | Community centrality score |

### Date Signals
| Signal | Type | Description |
|--------|------|-------------|
| `date.extracted[]` | date[] | Dates found in content |
| `date.precision` | string | day/month/quarter/year |
| `date.is_relative` | bool | Contains relative dates |

## TinyLLM Prompt Template

```markdown
You are a RAG query router. Given a user query and available signals, output a JSON
filter configuration to optimize retrieval.

## Available Signals
{signal_list}

## Similar Historical Queries
{similar_queries_and_their_filters}

## User Query
{user_query}

## Instructions
1. Analyze the query intent (factual, temporal, multi-modal, entity-focused)
2. Select relevant signals based on query type
3. Set appropriate filter thresholds
4. Choose retrieval strategy

Output ONLY valid JSON:
```

## Query Intent Classification

The Sentinel classifies queries into intent categories:

| Intent | Description | Signals to Prioritize |
|--------|-------------|----------------------|
| `factual` | Direct information lookup | entity signals, doc.type |
| `temporal` | Date-based queries | date.*, doc.created_at |
| `multi_modal` | Cross-content type | audio.*, image.*, doc.* |
| `entity_focused` | About specific entities | entity.types, community.name |
| `technical` | Code/technical docs | doc.type=code, entity.types=technology |
| `conversational` | Audio transcripts | transcription.*, speaker.* |

## Feedback Loop

User feedback (clicks, dwell time, explicit ratings) feeds back to improve routing:

1. **Implicit Feedback**: Track which retrieved results users engage with
2. **Signal Correlation**: Update `avg_correlation` for signals that lead to engagement
3. **Query Pattern Learning**: Similar queries get similar successful filters

## Implementation Phases

### Phase 1: Signal Catalog
- [ ] Create signal_embeddings table
- [ ] Populate with all known signals
- [ ] Generate embeddings for signal descriptions
- [ ] Build signal discovery API

### Phase 2: TinyLLM Integration
- [ ] Integrate Phi-3-mini or TinyLlama via Ollama
- [ ] Create prompt templates for routing
- [ ] Build filter parameter parser
- [ ] Add fallback to default filters

### Phase 3: Learning Loop
- [ ] Implement query_signal_history tracking
- [ ] Build feedback collection (implicit + explicit)
- [ ] Create correlation update job
- [ ] Add A/B testing for filter strategies

### Phase 4: Production Hardening
- [ ] Add caching for repeated query patterns
- [ ] Implement rate limiting for LLM calls
- [ ] Build monitoring dashboard
- [ ] Create manual override capability

## Configuration

```yaml
sentinel:
  enabled: true
  model: "phi3:mini"  # or "tinyllama"

  # Embedding model for signal similarity
  embedding_model: "nomic-embed-text"

  # How many similar queries to consider
  similar_query_limit: 5

  # How many signals to include in context
  max_signals_in_context: 20

  # Fallback filters if LLM fails
  fallback_filters:
    retrieval_strategy: "hybrid_rrf"
    expansion_depth: 1

  # Minimum confidence to use LLM routing
  min_confidence: 0.6

  # Cache TTL for similar query patterns
  cache_ttl_minutes: 60
```

## API Design

### Route Query Endpoint

```http
POST /api/sentinel/route
Content-Type: application/json

{
  "query": "What tables show revenue data from 2024?",
  "collection_ids": ["uuid1", "uuid2"],  // Optional: limit to collections
  "force_signals": ["doc.has_tables"],   // Optional: force certain signals
  "explain": true                        // Return routing explanation
}
```

Response:
```json
{
  "filters": {
    "file_types": ["pdf", "xlsx"],
    "date_range": { "year": 2024 },
    "signals": {
      "doc.has_tables": true,
      "table.column_contains": "revenue"
    },
    "retrieval_strategy": "hybrid_rrf",
    "expansion_depth": 1
  },
  "explanation": {
    "intent": "temporal_tabular",
    "signals_considered": ["doc.has_tables", "date.extracted", "table.columns"],
    "similar_queries": [
      { "query": "revenue tables from last quarter", "success_rate": 0.92 }
    ],
    "confidence": 0.87
  }
}
```

## Performance Considerations

1. **Latency Budget**: Sentinel routing should add < 100ms to query time
2. **Caching**: Frequently used query patterns should be cached
3. **Fallback**: If LLM times out, use rule-based routing
4. **Batching**: Batch embed multiple signals in single API call

## Metrics to Track

- `sentinel.routing_latency_ms` - Time spent in routing
- `sentinel.llm_calls_total` - Number of LLM routing calls
- `sentinel.cache_hit_rate` - Cache efficiency
- `sentinel.filter_changes` - How often LLM changes default filters
- `sentinel.retrieval_improvement` - Result quality vs default routing

## Security Considerations

- Sanitize user queries before passing to LLM
- Rate limit per user/tenant
- Audit log all routing decisions
- Allow admin override of routing decisions
