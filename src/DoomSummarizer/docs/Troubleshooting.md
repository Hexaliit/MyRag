# Troubleshooting

## “Ollama not available”

Symptoms:
- Warnings like “Ollama not available”
- Summaries are limited or evidence-only

Fix:
- Start Ollama: `ollama serve`
- Pull configured models (defaults live in `config.json`): `doomsummarizer setup` shows what’s missing

Cloud fallback:
- Set `OPENAI_API_KEY` and/or `ANTHROPIC_API_KEY` to enable cloud LLMs (budgeted); see `docs/Config.md`.

## First run is downloading models

Expected behavior:
- ONNX embedding model downloads on first run unless already present.

If downloads fail:
- Verify network access
- Re-run `doomsummarizer setup` to see progress output
- Delete the partial model folder under `$HOME/.doomsummarizer/models/…` and retry

## `--entities` doesn’t do anything

`--entities` requires the NER model:
- Run `doomsummarizer setup --ner`

## `--graph` errors (DuckDB VSS)

`--graph` uses DuckDB’s VSS extension for HNSW.

If initialization fails:
- Ensure DuckDB can install/load extensions (network may be needed the first time)
- Try re-running with `--debug` to see where it failed

## `--images` doesn’t render

Inline image rendering depends on your terminal emulator.

Try:
- Windows Terminal (latest)
- Ensure the terminal supports the underlying image protocol used by your console image renderer

## Reddit / RSS sources occasionally fail

Some sources are rate-limited or temporarily unavailable.

Tips:
- Use `--force` sparingly (it bypasses caches)
- Increase crawl delay for fragile sites (`crawl --delay …`)
- Add alternate sources (`-s gnews:…`, `-s search:…`) to keep coverage broad

