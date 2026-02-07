# LucidSupport

> **COMING IN V2.0** — LucidSupport is under active development and will be available in lucidRAG v2.0.

Page-aware support assistant that uses Playwright to deeply learn web pages - their structure, visual design, validation behavior, and interactive patterns - and outputs human-editable `.support.md` files that become the source of truth for contextual help.

Combined with LucidRAG's RAG pipeline for knowledge base integration, a tiny LLM (or no LLM at all) can deliver precise, context-aware help because all the hard work happens at learn-time, not query-time.

## Key Concepts

- **Smart Learning, Dumb Serving**: The learner is intelligent (sees, probes, understands, documents pages). The runtime is deliberately simple: pattern-match URL + field states to return pre-indexed help.
- **Embeddable Widget**: A lightweight JavaScript widget (`lucid-support`) that can be embedded in any web page to provide contextual assistance.

See `FUNCTIONAL_SPEC.md` and `Widget/WIDGET_SPEC.md` for full details.
