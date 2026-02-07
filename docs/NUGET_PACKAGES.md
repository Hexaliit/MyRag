# NuGet Packages

This repo publishes a set of reusable NuGet packages that power the LucidRAG platform and CLI tools.

If you’re new, start with:

- `Mostlylucid.LucidRAG.Summarizer.Core` (shared pipeline contracts)
- `Mostlylucid.LucidRAG.DocSummarizer` (documents / chunking / retrieval)
- `Mostlylucid.LucidRAG.Storage.Core` (vector storage backends)

## Core building blocks

### `Mostlylucid.LucidRAG.Summarizer.Core`

Shared pipeline abstractions used across all summarizers and the LucidRAG registry.

```bash
dotnet add package Mostlylucid.LucidRAG.Summarizer.Core
```

### `Mostlylucid.LucidRAG.Storage.Core`

Unified vector storage (`IVectorStore`) with InMemory / DuckDB / Qdrant backends.

```bash
dotnet add package Mostlylucid.LucidRAG.Storage.Core
```

Project docs: `src/Mostlylucid.Storage.Core/README.md`

## Pipelines (content-type engines)

### Documents

#### `Mostlylucid.LucidRAG.DocSummarizer`

Local-first document processing + retrieval (Markdown / PDF / DOCX / HTML / URLs) with optional LLM synthesis.

```bash
dotnet add package Mostlylucid.LucidRAG.DocSummarizer
```

Project docs: `src/Mostlylucid.DocSummarizer.Core/README.md`

#### Provider integrations

- `Mostlylucid.LucidRAG.DocSummarizer.OpenAI`
- `Mostlylucid.LucidRAG.DocSummarizer.Anthropic`
- `Mostlylucid.LucidRAG.DocSummarizer.LLamaSharp`

### Images

#### `Mostlylucid.LucidRAG.ImageSummarizer`

Signal-based image analysis (OCR + vision) and unified pipeline integration.

```bash
dotnet add package Mostlylucid.LucidRAG.ImageSummarizer
```

Project docs: `src/ImageSummarizer.Core/README.md`

### Data

#### `Mostlylucid.LucidRAG.DataSummarizer`

Tabular/data-file profiling (CSV / Excel / Parquet / JSON) for semantic search and reporting.

```bash
dotnet add package Mostlylucid.LucidRAG.DataSummarizer
```

### Video

#### `Mostlylucid.LucidRAG.VideoSummarizer`

Video processing with scene segmentation + OCR tracking + transcription.

```bash
dotnet add package Mostlylucid.LucidRAG.VideoSummarizer
```

### Audio

#### `Mostlylucid.LucidRAG.AudioSummarizer`

Forensic audio characterization with speech-to-text and speaker analysis.

```bash
dotnet add package Mostlylucid.LucidRAG.AudioSummarizer
```

Project docs: `docs/audiosummarizer.md`

### Domain specialists

#### `Mostlylucid.LucidRAG.DomainClassifier.Core`

Plugin registry and enrichment contracts for domain-aware ingestion.

```bash
dotnet add package Mostlylucid.LucidRAG.DomainClassifier.Core
```

Built-in specialist plugin projects in this repo:

- `src/DomainClassifier.Financial`
- `src/DomainClassifier.Technical`
- `src/DomainClassifier.Narrative`

## DoomSummarizer ecosystem

### Core + complete bundle

- `Mostlylucid.LucidRAG.DoomSummarizer.Core` (shared engine)
- `Mostlylucid.LucidRAG.DoomSummarizer` (console app package)
- `Mostlylucid.LucidRAG.DoomSummarizer.Complete` (batteries-included bundle)

### Plugins

These are optional analysis plugins (image/data/audio/video/subtitles/books) that plug into DoomSummarizer’s pipeline registry.

Project docs: `src/DoomSummarizer/docs/` (especially `Sources.md` and `Config.md`)

### Sources

These packages add source connectors (web/search, YouTube, Google, Reddit, UK Gov, etc.) for DoomSummarizer.

Project docs: `src/DoomSummarizer/docs/Sources.md`

## Unified LLM providers

If you need named multi-provider configs (fallbacks / retries / resilience), see:

- `docs/UNIFIED_LLM_PROVIDERS.md`
- `Mostlylucid.LucidRAG.LLM`
