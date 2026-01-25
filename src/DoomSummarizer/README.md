# DoomSummarizer

An AI-powered CLI tool that doom-scrolls HN, Reddit, StackOverflow, BBC News, and more — so you don't have to.

## Features

- **Multiple Sources**: Hacker News, Reddit, StackOverflow, BBC, Guardian, Ars Technica, The Verge, and more
- **Natural Language Prompts**: Just ask "what does BBC say about AI today"
- **4 Vibes**: doom, hopeful, snarky, neutral
- **Signal-Based Summarization**: Uses DocSummarizer's segment extraction for evidence-backed summaries
- **Local LLM**: Powered by Ollama with qwen2.5:3b (fast, quality)
- **Entity Extraction**: Named entities (people, orgs, locations)
- **Trend Tracking**: Track sentiment changes over time
- **Inline Images**: Display thumbnails for visual content

## Installation

```bash
# Build
dotnet build src/DoomSummarizer/DoomSummarizer.csproj

# Run setup to download ONNX models
dotnet run --project src/DoomSummarizer -- setup

# Install Ollama and pull model (optional but recommended)
# https://ollama.com
ollama pull qwen2.5:3b
```

## Quick Start

```bash
# Basic HN scroll
doomsummarizer scroll -s hn

# Multiple sources with vibe
doomsummarizer scroll -s hn -s bbc --vibe doom

# Natural language
doomsummarizer scroll "what does bbc say about AI today"
```

## Sources

| Source | Description | Example |
|--------|-------------|---------|
| `hn` | Hacker News | `-s hn` |
| `reddit` | Reddit (default subs) | `-s reddit` |
| `reddit:sub` | Specific subreddit | `-s reddit:dotnet` |
| `so` | StackOverflow hot | `-s so` |
| `so:tag` | By tag | `-s so:csharp` |
| `bbc` | BBC News Tech | `-s bbc` |
| `guardian` | The Guardian Tech | `-s guardian` |
| `ars` | Ars Technica | `-s ars` |
| `verge` | The Verge | `-s verge` |
| `search:q` | DuckDuckGo search | `-s "search:rust programming"` |

Run `doomsummarizer sources` for full list.

## Vibes

- **doom**: Focus on concerning trends, vulnerabilities, layoffs
- **hopeful**: Highlight innovations, opportunities, positive developments
- **snarky**: Dry wit, hype vs reality, entertaining but informative
- **neutral**: Objective, balanced, just the facts

## Examples

### Hacker News + BBC with Snarky Vibe

```bash
doomsummarizer scroll -s hn -s bbc --vibe snarky --force
```

```
# Doom-Scroll Digest - January 25, 2026

## CLOUD
### ANN v3: Millions of Vectors at Your Fingertips
* [Turbopuffer](https://turbopuffer.com/blog/ann-v3)
A new AI algorithm promises blazingly fast query latencies over billions of
vectors, but the article suggests skepticism about whether such performance
can be achieved in practice.

## TOOLS
### macOS App Promoting Better Posture
* [GitHub](https://github.com/tldev/posturr)
A novel macOS application that automatically blurs the screen when you
slouch, encouraging better posture.

**What to Watch:**
With Google's high-friction sideloading flow coming to Android and the
announcement of Bonsplit's tabs functionality, users will be watching
how these affect both security and user experience.
```

### BBC News About AI (Natural Language)

```bash
doomsummarizer scroll "what does bbc say about AI today" --vibe doom
```

```
Detected: sources=[bbc:AI], vibe=doom

# Doom-Scroll Digest - January 25, 2026

## AI
- [Is China quietly winning the AI race?](https://www.bbc.com/news/articles/c86v52gv726o)
- [ChatGPT to carry adverts for some users.](https://www.bbc.com/news/articles/cvgjn012k3do)
- [Mother of Elon Musk's child sues xAI over Grok deepfakes.](https://www.bbc.com/news/articles/cp37erw0zwwo)

## SECURITY
- [Ofcom investigates Elon Musk's X over Grok AI sexual deepfakes.](https://www.bbc.com/news/articles/cwy875j28k0o)
- [Elon Musk's Grok AI appears to have made child sexual imagery, says charity.](https://www.bbc.com/news/articles/cvg1mzlryxeo)
```

### StackOverflow C# Questions

```bash
doomsummarizer scroll -s so:csharp --vibe neutral --force
```

Signal-based processing extracts key segments from each question:

```
# Doom-Scroll Digest for January 25, 2026

## LANGUAGE
### What is the difference between String and string in C#?
In C#, 'string' and 'String' are both used for string literals but 'string'
is a keyword while 'String' is a reference type alias. It's recommended to
use 'string' as it aligns with best practices.

### How can I enumerate an enum in C#?
To enumerate an enum in C#, iterate using foreach and 'Enum' keyword...
```

### Reddit Programming with Snarky Vibe

```bash
doomsummarizer scroll -s reddit:programming --vibe snarky --force
```

```
# Doom-Scroll Digest - January 25, 2026

## TOOLS
- [cURL Gets Rid of Its Bug Bounty Program Over AI Slop Overrun]
  (https://itsfoss.com/news/curl-closes-bug-bounty-program/)
- [Your Agent is Building Things You'll Never Use]
  (https://mahdiyusuf.com/your-agent-is-building-things-youll-never-use/)

## CLOUD
- [Why Developing for Microsoft SharePoint is a Horrible Experience]
  (https://medium.com/@jordansrowles/...)

### What to Watch
With cURL abandoning its bug bounty program over AI vulnerabilities and
developers pointing out that AI-driven tools often produce features
users won't use, it's clear the tech world is grappling with new challenges.
```

## Flags

| Flag | Description |
|------|-------------|
| `--vibe` | Set mood: doom, hopeful, snarky, neutral |
| `--limit N` | Limit items per source |
| `--force` | Re-analyze even if previously seen |
| `--entities` | Extract named entities |
| `--images` | Display inline thumbnails |
| `--json` | Output as JSON |
| `--raw` | Show raw fetched content |
| `--no-llm` | Skip LLM (uses salience-based segments) |
| `-q, --quiet` | Minimal output |

## Configuration

Config file: `~/.doomsummarizer/config.json`

```json
{
  "ollama": {
    "baseUrl": "http://localhost:11434",
    "model": "qwen2.5:3b",
    "temperature": 0.4
  },
  "vibes": {
    "doom": "Focus on concerning trends, vulnerabilities, layoffs...",
    "hopeful": "Highlight innovations, opportunities...",
    "snarky": "Add dry wit and mild cynicism...",
    "neutral": "Objective, balanced summary..."
  }
}
```

## Model Selection

Tested on local Ollama (January 2026):

| Model | Speed | Quality | Recommendation |
|-------|-------|---------|----------------|
| qwen2.5:1.5b | ~3s | Good | Fastest option |
| qwen2.5:3b | ~5s | Better | **Default (balanced)** |
| gemma3:4b | ~8s | Good | Alternative |
| llama3.2:3b | ~12s | Good | Slower |

## Architecture

DoomSummarizer uses signal-based processing by default for best-quality summaries:

1. **Fetch**: Parallel content retrieval from multiple sources
2. **Dedupe**: URL-based deduplication (same article from multiple sources)
3. **Extract**: Segment extraction with ONNX embeddings (all-MiniLM-L6-v2)
4. **Score**: Salience scoring with MMR (Maximal Marginal Relevance)
5. **Analyze**: Per-article analysis using top segments by salience
6. **Synthesize**: LLM generates summary from high-salience segments

Processing strategy adapts to content length:
- **Short (<500 chars)**: Direct LLM summary
- **Medium (<2000)**: Basic segment extraction
- **Long (<5000)**: Full signal extraction with MMR
- **Very Long**: BertRAG-style retrieval

This ensures summaries are grounded in actual content with evidence references.

## Commands

```bash
doomsummarizer scroll     # Main summarization command
doomsummarizer setup      # Download models, setup Playwright
doomsummarizer trends     # Show trends over time
doomsummarizer config     # Show/edit configuration
doomsummarizer sources    # List available sources
```

## License

MIT
