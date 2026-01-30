# Cloud LLM Providers (OpenAI & Anthropic)

> **Cloud providers are optional and disabled by default.** DoomSummarizer runs fully on local Ollama models (`gemma3:4b` + `qwen3:0.6b`). Cloud LLMs are only used when explicitly enabled in config with a valid API key.

Cloud LLM providers can deliver improved results for complex queries. They offer larger context windows, stronger reasoning, and higher-quality synthesis. When enabled, they serve as **fallback** providers — Ollama remains the primary LLM.

## Quick Setup

### OpenAI

```bash
# Set environment variable
export OPENAI_API_KEY="sk-..."

# Optional: specify models (main|sentinel)
export DOOM_OPENAI_MODELS="gpt-4o|gpt-4o-mini"
```

### Anthropic (Claude)

```bash
# Set environment variable
export ANTHROPIC_API_KEY="sk-ant-..."

# Optional: specify models (main|sentinel)
export DOOM_ANTHROPIC_MODELS="claude-sonnet-4-5-20251124|claude-haiku-4-5-20251124"
```

Then enable the provider in your config (`~/.doomsummarizer/config.json`):
```json
{ "name": "anthropic", "apiKey": "sk-ant-...", "enabled": true }
```

Cloud providers must be explicitly enabled. Setting an API key alone is not sufficient.

## Model Selection

DoomSummarizer uses **two model roles**:

| Role | Purpose | Choose |
|------|---------|--------|
| **Main** | Synthesis, summaries, article generation | Larger model with strong reasoning |
| **Sentinel** | Query classification, intent detection, Lucene query generation | Cheaper/faster model |

Format: `DOOM_*_MODELS="main_model|sentinel_model"`

### Choosing the Right Models

**For Main role (synthesis):**
- Choose the most capable model within your budget
- Larger models produce more coherent summaries, better temporal understanding, and higher-quality articles
- Look for models with large context windows (128K+) to reason over more evidence
- Frontier/flagship models excel at long-form article generation

**For Sentinel role (classification):**
- Smaller, faster models work well here
- The sentinel classifies query intent and generates Lucene queries — it doesn't need frontier-level reasoning
- Prioritize speed and cost over raw capability
- "Mini" or "Haiku" tier models are ideal

### OpenAI Model Tiers

| Tier | Examples | Best For |
|------|----------|----------|
| **Flagship** | GPT-5, GPT-4o | Main role: synthesis, complex reasoning |
| **Balanced** | GPT-4.1, GPT-4-turbo | Main role with budget constraints |
| **Fast/Mini** | GPT-4o-mini, GPT-4.1-mini | Sentinel role, classification |

Check [OpenAI's model docs](https://platform.openai.com/docs/models) for current models and pricing.

**Example setups:**
```bash
# Quality + budget balance (recommended)
export DOOM_OPENAI_MODELS="gpt-4o|gpt-4o-mini"

# Premium quality
export DOOM_OPENAI_MODELS="gpt-5|gpt-4o-mini"

# All budget
export DOOM_OPENAI_MODELS="gpt-4o-mini|gpt-4o-mini"
```

### Anthropic Model Tiers

| Tier | Examples | Best For |
|------|----------|----------|
| **Opus** | claude-opus-* | Main role: highest quality, research, agent chains |
| **Sonnet** | claude-sonnet-* | Main role: best balance of quality and cost |
| **Haiku** | claude-haiku-* | Sentinel role: fast, cheap, classification |

Check [Anthropic's model docs](https://docs.anthropic.com/en/docs/about-claude/models) for current models and pricing.

**Example setups:**
```bash
# Best balance (recommended)
export DOOM_ANTHROPIC_MODELS="claude-sonnet-4-5-20251124|claude-haiku-4-5-20251124"

# Premium quality
export DOOM_ANTHROPIC_MODELS="claude-opus-4-5-20251124|claude-haiku-4-5-20251124"

# All budget
export DOOM_ANTHROPIC_MODELS="claude-haiku-4-5-20251124|claude-haiku-4-5-20251124"
```

### Model Naming Conventions

**OpenAI:** `gpt-{version}` or `gpt-{version}-mini`
- Version numbers increase over time (4, 4o, 4.1, 5, 5.2, etc.)
- `-mini` suffix indicates smaller, faster, cheaper variant

**Anthropic:** `claude-{tier}-{version}-{date}`
- Tiers: `opus` (best), `sonnet` (balanced), `haiku` (fast)
- Date format: YYYYMMDD (e.g., 20251124)
- Use `-latest` suffix for auto-updating to newest version of a tier

## Config File Setup

Instead of environment variables, add cloud providers to `~/.doomsummarizer/config.json`:

```json
{
  "keys": [
    {
      "service": "openai",
      "apiKey": "sk-...",
      "searchEngineId": "gpt-4o|gpt-4o-mini",
      "dailyLimit": 100,
      "costPerRequest": 0.02
    },
    {
      "service": "anthropic",
      "apiKey": "sk-ant-...",
      "searchEngineId": "claude-sonnet-4-5-20251124|claude-haiku-4-5-20251124",
      "dailyLimit": 100,
      "costPerRequest": 0.02
    }
  ]
}
```

The `searchEngineId` field stores model configuration as `main_model|sentinel_model`.

## Provider Priority

DoomSummarizer routes LLM calls with intelligent fallback:

1. **Ollama (primary)** — Local, free, no API costs
2. **Anthropic** — If configured and budget allows
3. **OpenAI** — If configured and budget allows

If Ollama fails or is unavailable, cloud providers are tried in order. Budget limits are checked before each cloud API call.

## Budget Control

Cloud providers are automatically budget-controlled:

```json
{
  "apiBudget": {
    "globalMaxRequestsPerDay": 500,
    "globalDailyBudgetUsd": 5.00
  },
  "keys": [
    {
      "service": "anthropic",
      "apiKey": "...",
      "searchEngineId": "claude-sonnet-4-5-20251124|claude-haiku-4-5-20251124",
      "dailyLimit": 100,
      "lifetimeLimit": 10000,
      "costPerRequest": 0.02
    }
  ]
}
```

When budget is exhausted, DoomSummarizer falls back to Ollama or skips that provider.

## Examples

### High-quality long-form article

```bash
# Cloud LLM for synthesis, local ONNX for embeddings
export ANTHROPIC_API_KEY="sk-ant-..."

doomsummarizer scroll "history of LLMs" -t blog-article -o llm-history.md
```

### Knowledge base Q&A

```bash
export OPENAI_API_KEY="sk-..."

# Build KB from docs site
doomsummarizer crawl https://docs.example.com --name docs --entities

# Ask questions using cloud LLM for reasoning
doomsummarizer ask --source crawl:docs "how does authentication work?"
```

### Budget-conscious daily digest

```bash
# Use Haiku-tier for everything
export DOOM_ANTHROPIC_MODELS="claude-haiku-4-5-20251124|claude-haiku-4-5-20251124"
export ANTHROPIC_API_KEY="sk-ant-..."

doomsummarizer scroll "AI news" -v snarky
```

### Temporal queries

```bash
export ANTHROPIC_API_KEY="sk-ant-..."

# Cloud LLM understands "since last week" naturally
doomsummarizer scroll "AI security news since last week" --debug
```

## Checking Provider Status

```bash
# Show which providers are configured and available
doomsummarizer config --show
```

## Troubleshooting

### "anthropic: budget exceeded — trying next"

Budget limits reached. Either:
- Wait until the next day (daily limits reset)
- Increase `dailyLimit` in config
- Fall back to Ollama (automatic)

### Cloud provider not detected

1. Check environment variable is set: `echo $ANTHROPIC_API_KEY`
2. Verify key is valid (try a simple API call)
3. Check `config --show` for provider status

### Slow responses

Cloud providers have network latency. For faster iteration:
- Use `--no-llm` flag for ranking-only mode (no synthesis)
- Use smaller models for sentinel role
- Ensure Ollama is running for hybrid mode

## Why Cloud LLMs Help

Cloud LLMs provide:

1. **Larger context windows** (128K-200K+ tokens) — can reason over more evidence
2. **Better instruction following** — more accurate query classification
3. **Higher quality synthesis** — better summaries, more coherent articles
4. **Temporal understanding** — "since last week", "recent", "breaking" parsed naturally
5. **Entity awareness** — better at identifying people, organizations, topics

Combined with DoomSummarizer's local ONNX embeddings, BM25 ranking, and knowledge graph:
- Embeddings and retrieval are **free** (local ONNX)
- Ranking and scoring are **free** (deterministic algorithms)
- Only synthesis uses cloud LLMs (controllable cost)

This hybrid approach gives you fast, free retrieval with intelligent, high-quality synthesis.
