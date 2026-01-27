# Output templates (Liquid + YAML)

DoomSummarizer renders output via named templates. Templates power:
- Console formatting
- File export (Markdown/HTML/JSON)
- “Future automation” outputs (email/newsletter/slack-style formatting)

List built-in templates:

```bash
doomsummarizer scroll --list-templates
```

Use a template:

```bash
doomsummarizer scroll "ai security news" -t newsletter -o digest.html
doomsummarizer scroll "ai security news" -t json -o digest.json
doomsummarizer page https://example.com/article -t blog-article -o article.md
```

## Built-in template names

`scroll` commonly uses:
- `default`, `console`, `compact`, `detailed`, `file`
- `email`, `newsletter`, `slack`
- `json`
- `image`
- `blog-article`, `blog-timeline`, `blog-newsletter`, `blog-newsletter-html`

`page` supports a smaller default subset (`default`, `blog-article`, `blog-timeline`, `detailed`, `file`, `json`) but can use any custom templates you add.

Note: some templates are designed for multi-item digests (like `newsletter` / `blog-newsletter`) and won’t be meaningful when summarizing a single page.

## Custom templates directory

Put custom templates here:
- `$HOME/.doomsummarizer/templates/`

Supported file types:
- `*.liquid`: Liquid templates for rendering
- `*.yaml` / `*.yml`: YAML “template definitions” (LLM structure + optional inline Liquid rendering template)

## Liquid templates (`.liquid`)

Create `$HOME/.doomsummarizer/templates/my-template.liquid`, then:

```bash
doomsummarizer scroll "topic" -t my-template
```

### Available data (Liquid variables)

Templates render a `DigestData` model with these fields:
- `date`
- `vibe`
- `query`
- `sources` (list)
- `overview`
- `what_to_watch`
- `items` (list of `DigestItem`)
- `item_count`
- `featured_image`

Grouped iteration helper:
- `items_by_topic` (each has `name` and `items`)

Blog-mode fields (populated for blog templates):
- `article_title`
- `introduction`
- `sections` (each: `heading`, `content`, `source_urls`)
- `conclusion`
- `source_urls`

Newsletter-mode fields (populated for newsletter templates):
- `top_picks` (each: `title`, `url`, `commentary`, `source`)
- `quick_hits` (each: `title`, `url`, `one_liner`)
- `sign_off`

Item fields (`items[*]`):
- `title`, `url`, `summary`, `topic`, `sentiment`, `score`, `source`, `image_url`

### Custom filters

Liquid filters registered by DoomSummarizer:
- `truncate_words`
- `sentiment_emoji`
- `sentiment_label`
- `title_case`
- `strip_html`

## YAML template definitions (`.yaml`)

YAML definitions are for “structured synthesis” templates: they tell DoomSummarizer how to generate multi-section outputs (especially for `blog-*` style templates), and can optionally provide a custom Liquid rendering template.

Minimal example:

```yaml
name: my-deep-dive
description: "Long-form deep dive with background and implications"
base_template: blog-article

introduction:
  prompt: "Start with a hook and explain why this matters."
  target_words: 150

sections:
  - heading: "Background"
    prompt: "Give the necessary context and prior events."
    target_words: 300

  - heading: "What Changed"
    prompt: "Explain the new development and the key evidence."
    target_words: 350

conclusion:
  prompt: "Summarize takeaways and what to watch next."
  target_words: 120
```

Then:

```bash
doomsummarizer scroll "your topic" -t my-deep-dive
```

Notes:
- If `template:` is present in the YAML, it’s compiled as an inline Liquid template under the same `name`.
- If no Liquid `template:` is provided, DoomSummarizer uses the `base_template` renderer and fills blog/newsletter fields based on the definition.

### Built-in YAML templates

These YAML templates ship with the binary (no need to copy files):

| Template | Sections | Description |
|----------|----------|-------------|
| `deep-dive` | 5 | Context, Technical Analysis, Key Findings, Expert Perspectives, Implications |
| `problem-solution` | 4 | The Problem, Why It Matters, Proposed Solutions, The Path Forward |
| `pros-cons` | 4 | Background, The Case For, The Case Against, The Verdict |

```bash
doomsummarizer scroll "WebAssembly adoption" -t deep-dive -o wasm.md
doomsummarizer scroll "technical debt" -t problem-solution -o debt.md
doomsummarizer scroll "Kubernetes vs serverless" -t pros-cons -o comparison.md
```

Source YAML definitions are in `Resources/templates/`. To create custom ones, place `.yaml` files in `$HOME/.doomsummarizer/templates/`.

## Long-form generation pipeline

Blog templates (`blog-article`, `blog-timeline`, and all YAML templates) activate a six-phase evidence-grounded pipeline in `scroll`:

1. **Evidence Preparation** — ArticleProcessor extracts segments with ONNX embeddings and TextRank salience
2. **Document Planning** — Sentinel LLM generates JSON outline with theme keywords per section
3. **Evidence Assignment** — Deterministic: each section gets evidence via embedding similarity + salience + relevance scoring (no LLM)
4. **Section Generation** — Main LLM generates sections sequentially with running summary, entity continuity tracking, and drift detection
5. **Output Validation** — Deterministic: URLs checked against fetched evidence whitelist, entities fuzzy-matched, facts grounded via sentence embeddings
6. **Assembly** — Sections stitched into `BlogArticleResult`, rendered by the template

All embeddings (segments, themes, validation) use the same ONNX `all-MiniLM-L6-v2` model. LLM calls total N+3 (1 outline + 1 intro + N sections + 1 conclusion). Everything else is deterministic.
