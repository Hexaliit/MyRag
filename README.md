# ***lucid***RAG

<div align="center">

**The Open-Source Agentic RAG Platform**

*Multi-document intelligence with GraphRAG entity extraction, 22-wave image analysis, and enterprise multi-tenancy*

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-The_Unlicense-green.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/scottgal/lucidrag?include_prereleases&label=Release&logo=github)](https://github.com/scottgal/lucidrag/releases)
[![GitHub Downloads](https://img.shields.io/github/downloads/scottgal/lucidrag/total?label=Downloads&logo=github)](https://github.com/scottgal/lucidrag/releases)

> **IN DEVELOPMENT** - This project is under active development and not yet ready for production use. APIs may change
> without notice. Watch this repo for updates.

</div>

---

## DoomSummarizer — Console-First Research Assistant

<div align="center">

[![DoomSummarizer Releases](https://img.shields.io/github/v/release/scottgal/lucidrag?include_prereleases&label=DoomSummarizer&logo=windowsterminal&logoColor=white&color=blue)](https://github.com/scottgal/lucidrag/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey?logo=dotnet)](https://github.com/scottgal/lucidrag/releases)

</div>

**DoomSummarizer** is a distillation of ***lucid***RAG principles — hybrid search, entity extraction, knowledge graph
construction, evidence-grounded synthesis — compressed into a standalone single-binary CLI. It fetches, ranks, and
synthesizes news and research with a built-in local knowledge base, NER entity extraction, and long-form article
generation. No API keys required for default sources.

```bash
# Daily digest from default sources
doomsummarizer scroll

# Topic query with tone
doomsummarizer scroll "AI regulation news" -v snarky

# Build a knowledge base from any website (incremental with ETag caching)
doomsummarizer crawl https://docs.example.com -n mydocs --entities
doomsummarizer ask -s crawl:mydocs "how does authentication work?"

# Generate a long-form article with evidence grounding
doomsummarizer scroll "history of LLMs" -t blog-article -o llm-history.md
```

**Key features**: hybrid BM25 + semantic ranking, ONNX embeddings (offline), HTTP ETag/Last-Modified cache-aware web
crawling, NER entity extraction with knowledge graph, budget-controlled cloud LLM support (Anthropic/OpenAI), 15+ output
templates, email delivery.

> *
*[Full documentation, all CLI options, and examples →](https://github.com/scottgal/lucidrag/blob/main/src/DoomSummarizer/README.md)
**

**Download**: Grab pre-built binaries for Windows, Linux, and macOS from [**Releases
**](https://github.com/scottgal/lucidrag/releases).

---

## Why ***lucid***RAG?

Most RAG systems are basic document-to-vector pipelines. ***lucid***RAG is different:

| Feature          | Basic RAG        | ***lucid***RAG                                   |
|------------------|------------------|--------------------------------------------------|
| Search           | Semantic only    | Hybrid BM25 + Semantic with RRF fusion           |
| Query Processing | Direct embedding | Agentic decomposition (Sentinel)                 |
| Knowledge        | Flat chunks      | GraphRAG with entity extraction & communities    |
| Images           | Not supported    | 22-wave ML pipeline (OCR, faces, motion, scenes) |
| Data Files       | Not supported    | CSV, Excel, Parquet profiling with DuckDB        |
| Video            | Not supported    | Scene detection, transcript extraction           |
| Deployment       | Cloud-dependent  | **Zero API keys** - runs fully local             |
| Multi-tenancy    | Not supported    | Schema-per-tenant with automatic provisioning    |

---

## Getting Started (Development)

> **Note:** ***lucid***RAG is in active development. These instructions are for contributors and early testers.

### Prerequisites

- .NET 10.0 SDK
- PostgreSQL 16+ with pgvector extension
- Node.js 18+ (for CSS build)
- Optional: Ollama for local LLM inference

### Running Locally

```bash
# Clone the repository
git clone https://github.com/scottgal/lucidrag.git
cd lucidrag

# Set up the database connection in user secrets
dotnet user-secrets -p src/LucidRAG/LucidRAG.csproj set "ConnectionStrings:DefaultConnection" "Host=localhost;Database=LucidRAG;Username=postgres;Password=yourpassword"

# Build and run
dotnet run --project src/LucidRAG/LucidRAG.csproj
```

### Standalone Mode (Quick Testing)

For quick testing without PostgreSQL:

```bash
dotnet run --project src/LucidRAG/LucidRAG.csproj -- --standalone
```

Uses SQLite + InMemory vectors. **Note:** Embeddings are not persisted between restarts in standalone mode.

---

## Core Components

***lucid***RAG is built from specialized processing engines, each designed for a specific content type:

```
LucidRAG Platform
       │
       ├── Web Application (ASP.NET Core 10 + Razor + Alpine.js + Tailwind)
       │      ├── Chat Interface with streaming responses
       │      ├── File Explorer with natural language search
       │      ├── Knowledge Graph visualization (D3.js)
       │      └── Multi-tenant admin dashboard
       │
       └── Unified Pipeline Registry
              ├── DocumentPipeline  →  PDF, DOCX, Markdown, HTML, TXT
              ├── ImagePipeline     →  PNG, JPG, GIF, WebP (22-wave analysis)
              ├── DataPipeline      →  CSV, Excel, Parquet, JSON (DuckDB)
              └── VideoPipeline     →  MP4, MKV, MOV (scene detection)
```

### DocumentPipeline (`Mostlylucid.DocSummarizer.Core`)

Handles traditional documents with intelligent chunking and hybrid search:

- **PDF**: Native extraction via PdfPig + table detection
- **DOCX**: OpenXML parsing with structure preservation
- **Markdown/HTML**: AST parsing with code block handling
- **Chunking**: Semantic boundaries with configurable overlap
- **Search**: BM25 lexical + BERT semantic with RRF fusion

### ImagePipeline (`ImageSummarizer.Core`)

A 22-wave modular ML pipeline for comprehensive image understanding:

| Wave Category | Waves                               | Purpose                                      |
|---------------|-------------------------------------|----------------------------------------------|
| **OCR**       | AdvancedOcr, MlOcr, OcrQuality      | Multi-engine text extraction with confidence |
| **Vision AI** | Florence2, VisionLlm, ClipEmbedding | Foundation models for understanding          |
| **Detection** | Face, Scene, TextRegion, QRCode     | Object and pattern detection                 |
| **Analysis**  | Color, Motion, Edge, Composition    | Visual feature extraction                    |
| **Forensics** | Exif, Contradiction, AutoRouting    | Metadata and validation                      |

**Special Capabilities:**

- Animated GIF/WebP: Frame deduplication (SSIM), temporal voting, filmstrip generation
- Faces: Detection with bounding boxes for privacy redaction
- Motion: Optical flow analysis for animation classification

### DataPipeline (`DataSummarizer.Core`)

Structured data profiling powered by DuckDB:

- **Column Profiling**: Type inference, cardinality, null rates
- **Statistical Analysis**: Percentiles, distributions, outliers
- **Constraint Validation**: Unique keys, foreign key relationships
- **Query Generation**: Auto-generated SQL for common questions

### VideoPipeline (`VideoSummarizer.Core`)

Video content extraction and analysis:

- **Scene Detection**: ML-based shot boundary detection
- **Keyframe Extraction**: Representative frames per scene
- **Audio Transcription**: Whisper integration for speech-to-text
- **Frame Sampling**: Configurable intervals for analysis

---

## Intelligent Features

### Agentic Query Decomposition (Sentinel)

The Sentinel service transforms user queries into optimized search plans:

```
User: "Compare the authentication approaches in the 2023 and 2024 security audits"
       │
       └── Sentinel Analysis
              ├── Query Type: Comparison
              ├── Sub-queries:
              │     ├── "authentication approach 2023 security audit"
              │     └── "authentication approach 2024 security audit"
              └── Fusion Strategy: Side-by-side comparison
```

**Features:**

- Query classification (keyword, semantic, comparison, aggregation)
- Automatic sub-query generation
- Clarification requests for ambiguous queries
- 15-minute query plan caching

### GraphRAG Knowledge Graph

Entity extraction with community detection for connected knowledge:

```
Documents → Entity Extraction → Relationship Building → Community Detection
                │                      │                       │
                ├── Person            ├── works_at           ├── Louvain clustering
                ├── Organization      ├── located_in         ├── LLM summarization
                ├── Location          ├── related_to         └── Visual exploration
                └── Concept           └── mentions
```

**Interactive Visualization**: D3.js force-directed graph with:

- Node sizing by connection count
- Edge weights showing relationship strength
- Community coloring for clusters
- Click-through to source documents

### Evidence Artifacts

Structured storage for all extracted intelligence:

| Artifact Type    | Content                                      |
|------------------|----------------------------------------------|
| `ocr_text`       | Extracted text with per-character confidence |
| `ocr_word_boxes` | Bounding box coordinates for each word       |
| `llm_summary`    | AI-generated content summaries               |
| `filmstrip`      | Compressed frame sequences for GIFs/videos   |
| `key_frame`      | Representative frames from videos            |
| `table_csv`      | Extracted tables as CSV                      |
| `table_json`     | Table metadata and structure                 |
| `transcript`     | Audio transcriptions with timestamps         |

---

## File Explorer

The new File Explorer provides a full-width document browser with:

- **Natural Language Search**: Query documents using conversational language
- **Signal Filters**: Filter by hasImages, hasTables, hasCode, dateRange
- **Entity Filters**: Filter by extracted entities (Person, Organization, etc.)
- **Community Filters**: Filter by GraphRAG community clusters
- **Folder Organization**: Virtual folders for document organization
- **Bulk Operations**: Select multiple documents for batch actions

---

## Multi-Tenancy

Enterprise-ready tenant isolation:

```
┌─────────────────────────────────────────────────────┐
│                   LucidRAG Instance                 │
├─────────────────────────────────────────────────────┤
│  tenant_acme (schema)    │  tenant_globex (schema)  │
│  ├── collections         │  ├── collections         │
│  ├── documents           │  ├── documents           │
│  ├── entities            │  ├── entities            │
│  └── qdrant: acme_vecs   │  └── qdrant: globex_vecs │
└─────────────────────────────────────────────────────┘
```

**Features:**

- PostgreSQL schema-per-tenant isolation
- Automatic schema provisioning on first access
- Domain-based routing (subdomain or path)
- Per-tenant Qdrant collections
- Role-based access control per tenant

---

## API Reference

| Endpoint           | Methods           | Description                         |
|--------------------|-------------------|-------------------------------------|
| `/api/chat`        | POST, GET         | Conversational AI with memory       |
| `/api/search`      | POST              | Stateless semantic search           |
| `/api/documents`   | GET, POST, DELETE | Document CRUD                       |
| `/api/explorer`    | GET               | File browser with filters           |
| `/api/collections` | CRUD              | Collection management               |
| `/api/folders`     | CRUD              | Virtual folder organization         |
| `/api/graph`       | GET               | Knowledge graph data                |
| `/api/communities` | GET, POST         | Community detection                 |
| `/api/evidence`    | GET               | Artifact retrieval                  |
| `/api/tenants`     | CRUD              | Multi-tenant management             |
| `/api/ingestion`   | CRUD              | Source management (GitHub, S3, FTP) |
| `/api/crawl`       | POST, GET         | Web crawling                        |

**OpenAPI Documentation**: `/scalar/v1`

---

## Configuration

### Embedding Backend

```json
{
  "DocSummarizer": {
    "EmbeddingBackend": "Onnx",  // Onnx (local), Ollama, OpenAI, Anthropic
    "BertRag": {
      "VectorStore": "Qdrant",   // Qdrant (production), DuckDB
      "CollectionName": "ragdocs"
    }
  }
}
```

### LLM Provider

**Local (Ollama):**

```json
{
  "DocSummarizer": {
    "LlmBackend": "Ollama",
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "qwen2.5:3b"
    }
  }
}
```

**Cloud (Anthropic/OpenAI):**

```json
{
  "DocSummarizer": {
    "LlmBackend": "Anthropic",
    "Anthropic": { "Model": "claude-sonnet-4-20250514" }
  }
}
```

### Unified LLM Providers (Advanced)

For multi-provider setups with named instances and resilience, use the YAML-based configuration:

```yaml
# Config/llm-providers.yaml
backends:
  anthropic:
    type: anthropic
    api_key: ${ANTHROPIC_API_KEY}
    max_retries: 3

providers:
  fast-local:
    model: tinyllama
  general:
    model: claude-sonnet
    fallback: gpt-4o-mini
  smart:
    model: claude-opus
```

**Features:**

- Named providers with tier-based selection (triage, general, synthesis, vision)
- Polly resilience (retry with exponential backoff, circuit breaker)
- OpenTelemetry observability (tracing, metrics)
- Named prompt library with provider-specific overrides

See [docs/UNIFIED_LLM_PROVIDERS.md](docs/UNIFIED_LLM_PROVIDERS.md) for complete documentation.

---

## CLI Tools

### DoomSummarizer CLI (Standalone Research Assistant)

[![DoomSummarizer Releases](https://img.shields.io/github/v/release/scottgal/lucidrag?include_prereleases&label=Download&logo=github)](https://github.com/scottgal/lucidrag/releases)

A distillation of ***lucid***RAG into a single-binary CLI. Console-first research assistant and personal knowledge
base — fetches, ranks, and synthesizes content from 30+ sources with local ONNX embeddings, no API keys required.

```bash
doomsummarizer scroll "AI security news" -v snarky     # Digest with tone
doomsummarizer crawl https://docs.example.com --entities # Build a KB
doomsummarizer ask -s crawl:docs "how does auth work?"   # Query your KB
doomsummarizer scroll "Rust vs Go" -t deep-dive -o comparison.md  # Long-form article
```

**[Full documentation →](https://github.com/scottgal/lucidrag/blob/main/src/DoomSummarizer/README.md)** | *
*[Download →](https://github.com/scottgal/lucidrag/releases)**

### LucidRAG CLI

```bash
# Process files (auto-routes by extension)
lucidrag-cli process document.pdf image.gif data.csv --collection mydata

# Search
lucidrag-cli search "authentication best practices" --collection mydata

# Interactive chat
lucidrag-cli chat --collection mydata

# Run web server
lucidrag-cli serve --port 5080
```

### ImageSummarizer CLI (Standalone)

A powerful image analysis tool with MCP server support:

```bash
# Install globally
dotnet tool install -g Mostlylucid.ImageSummarizer.Cli

# Analyze image
imagesummarizer screenshot.png

# Process animated GIF
imagesummarizer animation.gif --pipeline advancedocr

# Run as MCP server for Claude Desktop
imagesummarizer --mcp
```

**MCP Tools (9 available):** `extract_text_from_image`, `analyze_image_quality`, `list_ocr_pipelines`,
`batch_extract_text`, `summarize_animated_gif`, `generate_caption`, `generate_detailed_description`,
`analyze_with_template`, `list_output_templates`

---

## NuGet Packages

This repo publishes a set of reusable NuGet packages (pipelines, storage, and integrations) that power LucidRAG and
DoomSummarizer.

- Package list + install guide: `docs/NUGET_PACKAGES.md`
- Canonical docs index: `docs/DOCS-INDEX.md`

---

## Development

```bash
# Build solution
dotnet build LucidRAG.sln

# Run with hot reload
dotnet watch run --project src/LucidRAG/LucidRAG.csproj

# Build CSS (Tailwind + DaisyUI)
cd src/LucidRAG && npm install && npm run build:css

# Run tests
dotnet test LucidRAG.sln -c Release --filter "Category!=Browser&Category!=Integration"

# Integration tests (requires PostgreSQL + migrations / test DB)
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj -c Release --filter "Category=Integration"

# Browser tests (requires a running app + downloads Chromium via Puppeteer)
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj -c Release --filter "Category=Browser"
```

---

## Requirements

| Component  | Version | Notes                    |
|------------|---------|--------------------------|
| .NET SDK   | 10.0+   | Required                 |
| PostgreSQL | 16+     | Or SQLite for standalone |
| Node.js    | 18+     | For CSS build only       |

**Optional Services:**

- **Ollama** - Local LLM inference (recommended: qwen2.5:3b)
- **Qdrant** - Production vector storage
- **Docling** - Enhanced PDF/DOCX parsing

---

## Architecture

```
src/
├── LucidRAG/                          # Web application
├── LucidRAG.Cli/                      # Command-line tool
├── LucidRAG.Core/                     # Business logic & entities
├── LucidRAG.Tests/                    # Integration tests
│
├── Mostlylucid.Summarizer.Core/       # Pipeline interfaces
├── Mostlylucid.DocSummarizer.Core/    # Document processing
├── ImageSummarizer.Core/              # Image analysis (22 waves)
├── DataSummarizer.Core/               # Structured data profiling
├── VideoSummarizer.Core/              # Video processing
│
├── Mostlylucid.DocSummarizer.Anthropic/  # Claude integration
├── Mostlylucid.DocSummarizer.OpenAI/     # OpenAI integration
│
├── Mostlylucid.GraphRag/              # Entity extraction & graphs
├── Mostlylucid.RAG/                   # Vector store abstraction
│
└── Mostlylucid.ImageSummarizer.Cli/   # Standalone OCR tool + MCP
```

---

## CI/CD

| Workflow                          | Trigger           | Output                           |
|-----------------------------------|-------------------|----------------------------------|
| `build.yml`                       | PR/Push           | Tests with PostgreSQL containers |
| `release-lucidrag.yml`            | `lucidrag-v*` tag | Docker multi-arch (amd64/arm64)  |
| `release-lucidrag-cli.yml`        | `cli-v*` tag      | CLI binaries                     |
| `release-imagesummarizer.yml`     | `img-v*` tag      | ImageSummarizer releases         |
| `publish-docsummarizer-nuget.yml` | Manual            | NuGet packages                   |

---

## License

The UnLicense - see [LICENSE](LICENSE)

---

## Contributing

***lucid***RAG is in active development and we welcome contributions! Please check
the [Issues](https://github.com/scottgal/lucidrag/issues) for areas where help is needed.

---

<div align="center">

**[GitHub](https://github.com/scottgal/lucidrag)** | **[Releases](https://github.com/scottgal/lucidrag/releases)** | *
*[Issues](https://github.com/scottgal/lucidrag/issues)** | *
*[DoomSummarizer Docs](https://github.com/scottgal/lucidrag/blob/main/src/DoomSummarizer/README.md)**

</div>
