# LucidRAG Web App

LucidRAG is the ASP.NET Core web application in this repository (`src/LucidRAG/LucidRAG.csproj`).
It provides multimodal RAG with deterministic ingestion pipelines, GraphRAG, domain specialists, and routed LLM synthesis.

## Core Capabilities

- Hybrid retrieval (BM25 + semantic + RRF)
- Agentic query decomposition (Sentinel)
- GraphRAG entity/relationship extraction and community exploration
- Unified ingestion pipeline registry:
  - Documents: PDF, DOCX, Markdown, HTML, TXT
  - Images: OCR + vision analysis
  - Data files: CSV, Excel, Parquet, JSON
  - Video: scene + transcript extraction
  - Audio: transcription + signal extraction
- Domain specialists during ingestion:
  - `financial`
  - `technical` (academic/docs)
  - `narrative`
- Multi-tenant support (schema-per-tenant)
- Local-first operation with optional cloud model fallback

## Quick Start (Local Dev)

### Prerequisites

- .NET 10 SDK
- PostgreSQL 16+ (recommended) or standalone mode for quick testing
- Node.js 18+ (for CSS build)
- Optional: Ollama at `http://localhost:11434`

### Run the web app

```bash
dotnet run --project src/LucidRAG/LucidRAG.csproj
```

### Run in standalone mode

```bash
dotnet run --project src/LucidRAG/LucidRAG.csproj -- --standalone
```

Standalone mode uses SQLite for portability.

## Configuration Highlights

Primary configuration lives in:

- `src/LucidRAG/appsettings.json`
- `src/LucidRAG/Config/llm-providers.yaml`
- `src/LucidRAG/Config/prompts.yaml`

### Default LLM settings (appsettings)

- `DocSummarizer:LlmBackend = Ollama`
- `DocSummarizer:Ollama:Model = qwen2.5:3b`
- Sentinel defaults:
  - `Sentinel:TinyModel = gemma3:1b`
  - `Sentinel:EscalationModel = qwen3:8b`

### Unified provider routing (YAML)

Provider tiers are routed by task:

- `triage` -> `fast-local`
- `general` -> `general`
- `synthesis` -> `smart`
- `vision` -> `vision`

See `../../docs/UNIFIED_LLM_PROVIDERS.md` for full examples.

## Docker

This project includes compose files under `src/LucidRAG/`:

- `docker-compose.yml` for local deployment shape
- `docker-compose.production.yml` for production-style setup

Build image:

```bash
docker build -f src/LucidRAG/Dockerfile -t lucidrag:local .
```

Run compose from `src/LucidRAG`:

```bash
docker compose up -d
```

## Development Commands

```bash
# Build solution
dotnet build LucidRAG.sln

# Run tests (excluding browser/integration categories)
dotnet test LucidRAG.sln -c Release --filter "Category!=Browser&Category!=Integration"

# CSS build for web UI
cd src/LucidRAG
npm install
npm run build:css
```

## Related Docs

- Root overview: `../../README.md`
- Docs index: `../../docs/DOCS-INDEX.md`
- LLM providers: `../../docs/UNIFIED_LLM_PROVIDERS.md`
- Prompt variables: `../../docs/PROMPT_TEMPLATE_VARIABLES.md`
- DoomSummarizer CLI: `../DoomSummarizer/README.md`
