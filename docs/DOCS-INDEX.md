# LucidRAG Documentation Index

This index links to the canonical documentation in this repo. (All paths below are relative to `docs/`.)

## Start Here

- **Project overview / dev setup**: [../README.md](../README.md)
- **NuGet packages (what to install)**: [NUGET_PACKAGES.md](NUGET_PACKAGES.md)
- **DoomSummarizer CLI**: [../src/DoomSummarizer/README.md](../src/DoomSummarizer/README.md)
- **LucidRAG web app**: [../src/LucidRAG/README.md](../src/LucidRAG/README.md)
- **ImageSummarizer CLI (OCR + MCP)**: [../src/Mostlylucid.ImageSummarizer.Cli/README.md](../src/Mostlylucid.ImageSummarizer.Cli/README.md)
- **ImageSummarizer command reference**: [../src/Mostlylucid.ImageSummarizer.Cli/COMMAND-REFERENCE.md](../src/Mostlylucid.ImageSummarizer.Cli/COMMAND-REFERENCE.md)
- **Claude / agent notes**: [../CLAUDE.md](../CLAUDE.md)

## LucidRAG (Web App / RAG Platform)

- **Conversational RAG notes**: [CONVERSATIONAL_RAG.md](CONVERSATIONAL_RAG.md)
- **Adding ingestion sources**: [ADDING_SOURCES.md](ADDING_SOURCES.md)
- **Unified LLM providers**: [UNIFIED_LLM_PROVIDERS.md](UNIFIED_LLM_PROVIDERS.md)
- **Prompt template variables**: [PROMPT_TEMPLATE_VARIABLES.md](PROMPT_TEMPLATE_VARIABLES.md)
- **Deduplication strategy**: [DEDUPLICATION_STRATEGY.md](DEDUPLICATION_STRATEGY.md)
- **Recent improvements**: [RECENT-IMPROVEMENTS.md](RECENT-IMPROVEMENTS.md)

## DoomSummarizer (Console-First Research Assistant)

- **CLI docs**: [../src/DoomSummarizer/docs/CLI.md](../src/DoomSummarizer/docs/CLI.md)
- **Config**: [../src/DoomSummarizer/docs/Config.md](../src/DoomSummarizer/docs/Config.md)
- **Sources**: [../src/DoomSummarizer/docs/Sources.md](../src/DoomSummarizer/docs/Sources.md)
- **MCP server**: [../src/DoomSummarizer/docs/MCP.md](../src/DoomSummarizer/docs/MCP.md)

## ImageSummarizer (OCR + MCP)

- **Core docs**: [../src/ImageSummarizer.Core/README.md](../src/ImageSummarizer.Core/README.md)
- **Signals / pipelines**: [../src/ImageSummarizer.Core/SIGNALS.md](../src/ImageSummarizer.Core/SIGNALS.md)
- **CLI usage**: [../src/Mostlylucid.ImageSummarizer.Cli/README.md](../src/Mostlylucid.ImageSummarizer.Cli/README.md)
- **MCP summaries (historical)**: [completed/MCP-IMPLEMENTATION-SUMMARY.md](completed/MCP-IMPLEMENTATION-SUMMARY.md), [completed/MCP-ENHANCEMENTS-SUMMARY.md](completed/MCP-ENHANCEMENTS-SUMMARY.md)

## Design & Historical Notes

- **Design docs**: [design/](design/)
- **Completed session summaries**: [completed/](completed/)
- **Research / results**: [summaries/](summaries/)

## CI / Workflows

- **Build & test**: [../.github/workflows/build.yml](../.github/workflows/build.yml)
- **Releases**: [../.github/workflows/](../.github/workflows/)

## Local Commands (Verified)

```bash
dotnet build LucidRAG.sln -c Release
dotnet test LucidRAG.sln -c Release --filter "Category!=Browser&Category!=Integration"
```

## License

This repository is released under **The Unlicense**: [../LICENSE](../LICENSE)

---

**Last Updated**: 2026-02-04
