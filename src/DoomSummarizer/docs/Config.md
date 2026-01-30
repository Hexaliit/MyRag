# Configuration, API keys, and budgets

DoomSummarizer reads configuration from:
- Global: `$HOME/.doomsummarizer/config.json`
- Local override: `./doomsummarizer.json` (in the current working directory)

Create a default config:

```bash
doomsummarizer config --init
```

Show the effective config (pretty-printed summary):

```bash
doomsummarizer config --show
```

## Config structure (high-level)

Top-level sections in `config.json`:
- `sources`: Hacker News / Reddit defaults, plus optional `websites` entries
- `sourceFilter`: allow/block domains and per-source weights
- `ollama`: model names, temperature, timeouts, context sizes
- `embedding`: local ONNX embedding configuration
- `output`: defaults for formatting
- `storage`: SQLite location + retention
- `linkFollowing`: enrichment settings and safety blocks
- `vibes`: named vibe → prompt mappings
- `keys`: optional API service definitions (search/news + cloud LLM providers)
- `apiBudget`: global budget caps

## Source filtering and weighting

`sourceFilter` supports:
- `allowedDomains`: if non-empty, only these domains are kept (allowlist mode)
- `blockedDomains`: always removed
- `weights`: multipliers applied to ranking (higher = boosted, lower = penalized, `0` = effectively blocked)

Weights keys are typically:
- Source ids like `hn`, `reddit`, `newsapi`, `brave_news`
- Or domain substrings like `reuters.com`

## Vibes

`--vibe` accepts either:
- A vibe name that exists in `config.vibes` (e.g. `snarky`, `upbeat`, `friendly`)
- Any custom text, treated as “apply this tone/perspective”

Add your own vibe:

```json
{
  "vibes": {
    "enterprise": "Be formal, concise, and risk-focused. Prefer primary sources. Use bullet points."
  }
}
```

Then:

```bash
doomsummarizer scroll "security updates" --vibe enterprise
```

## Optional API keys (search/news + cloud LLMs)

> **Cloud LLM providers (OpenAI, Anthropic) are disabled by default.** DoomSummarizer uses local Ollama models exclusively unless you explicitly enable a cloud provider in config with `"enabled": true` and a valid API key. Search/news API keys work when set — they don't require an `enabled` flag.

API keys are loaded in this priority order:
1. .NET user secrets (when running from source — highest priority)
2. Environment variables
3. `config.json` `keys[]` entries

### Environment variables

Search/news providers:
- `DOOM_GOOGLE_SEARCH` and `DOOM_GOOGLE_SEARCH_CX` (Google Custom Search)
- `DOOM_GOOGLE_PLACES` (Google Places; can share Google Search key)
- `DOOM_BRAVE_SEARCH` (Brave Search API)
- `DOOM_SERPER` (Serper.dev)
- `DOOM_TAVILY` (Tavily)
- `DOOM_NEWSAPI` (NewsAPI.org)
- `DOOM_NEWSDATA` (NewsData.io)
- `DOOM_JINA` (Jina AI)

Cloud LLM providers:
- `OPENAI_API_KEY` (+ optional `DOOM_OPENAI_MODELS` as `main|sentinel`)
- `ANTHROPIC_API_KEY` (+ optional `DOOM_ANTHROPIC_MODELS` as `main|sentinel`)

`DOOM_*_MODELS` format:
- `mainModel|sentinelModel` (for example: `gpt-4o-mini|gpt-4o-mini`)

### .NET user secrets (source builds)

When running from source, DoomSummarizer also reads user secrets under the project’s `UserSecretsId`.

Common secret names:
- `GoogleSearch`, `GoogleSearchCx`, `GooglePlaces`
- `BraveSearch`, `Serper`, `Tavily`, `NewsApi`, `NewsData`, `Jina`
- `OpenAi`, `OpenAiModels`
- `Anthropic`, `AnthropicModels`

Example (from this project directory):

```bash
dotnet user-secrets set "Serper" "<your key>" --project DoomSummarizer.csproj
```

## Budgets and safety

Paid/limited services are budgeted via:
- Global caps: `apiBudget.globalMaxRequestsPerDay`, `apiBudget.globalDailyBudgetUsd`
- Per-service caps in `keys[]` entries (daily + lifetime + cost-per-request)

Budget enforcement is checked before each cloud/API call; when denied, DoomSummarizer skips that provider and falls back to another (or to a free option when available).

## About `sources.websites` (experimental)

`sources.websites[]` supports per-site settings like `selector` and `usePlaywright`, but it is not currently wired into `scroll` by default.

For now, to fetch arbitrary sites:
- Use `scroll -s https://example.com` (RSS discovery + scrape fallback)
- Or use `page https://example.com/article` for “single URL into my KB + summary”
- Or use `crawl https://example.com` for a site-wide KB
