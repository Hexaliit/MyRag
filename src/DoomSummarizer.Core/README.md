# Mostlylucid.DoomSummarizer.Core

Core signal extraction pipeline for news and content analysis.

## Features

- **Signal Extraction**: Wave-based analysis pipeline (entities, topics, knowledge graphs)
- **LLM Routing**: Budget-enforced, circuit-breaking multi-provider routing (Ollama primary, cloud providers disabled by default)
- **Relevance Scoring**: 6-signal RRF fusion with query-type adaptive ranking
- **Content Extraction**: SmartReader + Markdown analysis for web content
- **Knowledge Graphs**: Entity extraction and relationship mapping
- **Resilience**: Circuit breakers, API budget tracking, adaptive rate limiting
- **Local-First**: ONNX embeddings, DuckDB vector search, Lucene full-text search

## Dependencies

- [Mostlylucid.DocSummarizer](https://www.nuget.org/packages/Mostlylucid.DocSummarizer) - Shared NER, content extraction, RRF scoring, wave/signal types
- [Mostlylucid.Summarizer.Core](https://www.nuget.org/packages/Mostlylucid.Summarizer.Core) - Foundation pipeline interfaces

## License

MIT
