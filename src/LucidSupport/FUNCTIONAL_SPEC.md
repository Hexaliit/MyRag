# LucidSupport - Functional Specification

## 1. What LucidSupport Is

LucidSupport is a **page-aware support assistant** that uses Playwright to deeply learn web pages — their structure, visual design, validation behavior, and interactive patterns — and outputs human-editable `.support.md` files that become the source of truth for contextual help. Combined with DoomSummarizer's RAG pipeline for knowledge base integration, a tiny LLM (or no LLM at all) can deliver precise, context-aware help because all the hard work happens at learn-time, not query-time.

### Core Principle: Smart Le];arning, Dumb Serving

The learner is the intelligent part. It:
- **Sees** the page (screenshots, CSS computed styles, visual grouping)
- **Probes** the page (fills fields, triggers validation, detects dependencies)
- **Understands** the page (layout semantics, reading order, error patterns)
- **Documents** the page (generates rich `.support.md` with all findings)

The runtime is deliberately simple: pattern-match URL + field states → return pre-indexed help. An LLM adds conversational polish but is optional.

---

## 2. The Smart Learner Pipeline

When `lucidsupport learn https://example.com/checkout` runs, six analysis phases execute in sequence:

### Phase 1: Structural Extraction (`FormFieldExtractor`)

JavaScript injected via `page.EvaluateAsync()` extracts every interactive element:

| Extracted | Source |
|-----------|--------|
| Selector | `#id`, `[name="..."]`, or computed CSS path |
| Type | `input.type`, `tagName` |
| Label | `<label for>` → `aria-label` → `aria-labelledby` → closest `<label>` → `placeholder` |
| Validation attrs | `required`, `pattern`, `minlength`, `maxlength`, `min`, `max` |
| Autocomplete | `autocomplete` attribute (normalized; `on`/`off` stripped) |
| ARIA refs | `aria-describedby`, `aria-errormessage` for connected elements |

**Never reads**: `.value` — privacy by architecture.

The `PatternRegistry` then classifies each field by its attributes into named patterns (email, credit-card, phone, cvv, postal-code, etc.) using a priority-ordered rule table checking autocomplete → type → name.

### Phase 2: Visual Analysis (`VisualAnalyzer`)

A second JavaScript injection performs deep CSS inspection of every field:

**Per-field visual data:**
- Bounding box (page coordinates)
- Computed CSS: background, border (color, width, radius), font, padding, shadow, outline
- Visual prominence score (area × contrast)
- Error state detection:
  - Red/error-colored borders
  - `aria-invalid` attribute
  - CSS classes matching `/error|invalid|danger/`
  - Adjacent error message elements (with position: above/below/right/inline)
  - Error icons (SVG/img siblings with error-class names)
- Visual group membership (detected via `<fieldset>`, `role="group"`, or styled containers with headings)

**Page-level visual data:**
- Theme detection (light/dark from body background luminance)
- Primary brand color (most common non-neutral button/link color)
- Multi-column layout detection
- Reading order (all fields sorted by Y then X position)
- Visual groups with bounding boxes and boundary detection

**Screenshots** are captured at each phase:
- Full-page screenshot (full scroll height)
- Per-field screenshots (field bounding box + 20px padding for context)

### Phase 3: Layout Analysis (`LayoutAnalyzer`)

Tests four viewport widths: 375px (mobile), 768px (tablet), 1024px (desktop), 1440px (wide).

At each breakpoint:
- Detect column count via CSS grid template, flex children, or side-by-side element detection
- Record min/max viewport ranges
- Deduplicate adjacent breakpoints with identical column counts

### Phase 4: Active Interaction Probing (`InteractionProber`)

This is what makes the learner *smart* — it doesn't just read the page, it **uses** it:

**Focus probing** (per field):
- Focus the field → capture CSS diff (border, outline, shadow changes)
- Detect floating labels (Material Design pattern) via label position diff
- Detect focus-triggered help text (`aria-describedby` pointing to help elements, tooltips)

**Validation probing** (per field):
- Enter pattern-specific test data:
  - Email fields: `""`, `"test@"`, `"not-an-email"`
  - Credit card fields: `""`, `"4242"`, `"abcd"`
  - Phone fields: `""`, `"555"`, `"abc"`
  - Password fields: `""`, `"a"` (short)
  - Generic: `""` (empty for required check)
- After each fill + blur, capture:
  - `el.validity` state and `el.validationMessage`
  - Custom error messages from `aria-errormessage` refs
  - Adjacent elements matching `/error|invalid|validation/`
  - Whether `aria-invalid` was set

**Feature detection** (per field):
- Character counter (`"0/100"` pattern in siblings)
- Password strength meter (`[class*="strength"]`, `[role="progressbar"]`)
- Autocomplete suggestions (`<datalist>`, `[role="listbox"]`)
- Input masking (`type="password"`)
- Show/hide toggle (buttons with show/hide/eye labels)

**Field dependency probing**:
- For each `<select>`, `<radio>`, `<checkbox>`:
  - Try each option / toggle state
  - Wait 500ms for DOM to update
  - Diff field visibility/disabled states
  - Record dependencies: `select[name="country"] changed → show #state-field`

### Phase 5: Validation Error Extraction (`ValidationExtractor`)

Calls `form.checkValidity()` to trigger HTML5 constraint validation and captures:
- Per-field validity state breakdown (valueMissing, typeMismatch, patternMismatch, tooShort, etc.)
- Browser-generated validation messages
- Custom error elements via ARIA relationships
- Sibling error elements with validation-related class names

### Phase 6: Synthesis

All six data sources merge into the final `PageModel`:
- Field validation errors from phases 4 and 5 are combined (probed errors + HTML5 constraint errors)
- Help text discovered during focus probing fills in empty `help` properties
- Conditions are auto-generated from: field errors with help text, field dependencies, idle timeout for multi-field forms
- Page description is synthesized from: field count, visual groups, layout type, theme

---

## 3. The `.support.md` Format

The output of learning. Human-editable, version-controllable, round-trip parseable.

### Structure

```
---
page_id: checkout-payment        # unique ID, derived from URL path
url_pattern: /checkout/payment*  # glob pattern for URL matching
title: Payment Page              # from document.title
learned: 2026-02-04T12:00:00Z   # when learning ran
site: myapp.example.com          # host
layouts:                         # responsive breakpoints (compact YAML)
  mobile: { max: 767, columns: 1 }
  desktop: { min: 1024, columns: 2, note: "form left, order summary right" }
nav:                             # navigation links
  back: { label: "Back to Shipping", selector: "#back-link" }
  next: { label: "Pay Now", selector: "#pay-btn" }
flow: checkout                   # multi-page flow name (--follow-nav)
step: 2                          # position in flow
prev: checkout-shipping          # previous page_id
---

# Payment Page

Auto-learned from https://myapp.example.com/checkout/payment
Contains 4 interactive fields. Sections: Payment Details, Billing Address.
Uses multi-column layout.

## Fields

### [#card-number] Card Number
- type: text
- label: Card number
- placeholder: 1234 5678 9012 3456
- pattern: credit-card
- autocomplete: cc-number
- required: true
- validation:
  - client: required, pattern `[0-9\s]{13,19}`
  - server: Luhn algorithm, BIN lookup
- errors:
  - required: "Card number is required"
  - pattern: "Please enter a valid card number"
  - empty: "Constraints not satisfied"
  - partial: "Please match the requested format."
- help: Enter your 16-digit card number. We accept Visa, Mastercard, and Amex.

## Conditions

> when: [#card-number].error
> suggest: Enter your 16-digit card number. We accept Visa, Mastercard, and Amex.
> highlight: #card-number

> when: page.idle > 30s AND form.incomplete
> suggest: Need help completing this form? I can walk you through each field.

## Topics
- "What payment methods do you accept?" → accepted-payment-methods
- "Is my payment secure?" → security-and-encryption
```

### Parser/Writer Round-Trip

`SupportMarkdownParser.Parse(markdown)` → `PageModel` → `SupportMarkdownWriter.Write(model)` → identical markdown.

The parser handles:
- YAML frontmatter with `YamlDotNet` (underscore naming convention)
- `## Fields` with `### [#selector] Label` headings and nested property lists
- `## Conditions` with blockquoted `when`/`suggest`/`highlight` rules
- `## Topics` with `"question" → article-id` mappings
- Free text description between frontmatter and first `## heading`

---

## 4. Knowledge Base Integration (Phase 2)

LucidSupport connects to existing knowledge base articles through DoomSummarizer's RAG pipeline.

### Ingestion Path

```
.support.md → SupportIngestorService → ContentItem[] → StorageService
                                                        (SQLite + FTS5 + embeddings)
```

Each `.support.md` produces multiple `ContentItem`s:
1. **Page-level item**: `Source="support:{site}"`, `ParentDocumentId=page_id`, content = page description + field list
2. **Per-field items**: One per field, tagged with `[field_selector]`, content = label + help + error messages
3. **Condition items**: One per condition rule
4. **Topic link items**: Mapped to KB article IDs

All items get ONNX embeddings (all-MiniLM-L6-v2) for semantic search.

### KB Auto-Connection

The `## Topics` section in `.support.md` creates links to existing KB articles. During ingestion:
1. Topic question text is embedded
2. Embedding is searched against existing DoomSummarizer corpus
3. Top matches are suggested as `article-id` values
4. Human editor confirms/edits the mappings

At runtime, the `ScopedRetrievalService` searches both:
- Support items (field help, conditions) filtered by page_id + field selector
- KB articles matching topic mappings + semantic similarity to user question

---

## 5. Runtime Help Flow (Phase 3)

### Widget → API → Response

The JS widget sends `PageContext` (URL, visible fields, field states — never values):

```json
{
  "url": "/checkout/payment",
  "visibleFieldIds": ["#card-number", "#expiry"],
  "fieldStates": {
    "#card-number": { "hasValue": true, "hasError": true, "errorText": "Invalid" }
  },
  "viewportWidth": 375,
  "question": "Why is my card not working?"
}
```

The API processes this in stages:
1. **URL pattern matching**: `/checkout/payment` → page_id `checkout-payment`
2. **PageModel lookup**: Load cached PageModel (parsed from `.support.md`)
3. **Condition evaluation**: `#card-number` has error → triggers condition → gets suggestion + highlight target
4. **Scoped retrieval**: Search DoomSummarizer corpus filtered to `source="support:{site}"` AND tags ∈ `{page_id, #card-number}`
5. **Micro-context assembly**: 2-3 matched help chunks + field label + error text + triggered conditions
6. **Response composition**:
   - **With LLM**: Send micro-context (~200 tokens) to phi-3-mini/Qwen2 → get 1-2 sentence natural response
   - **Without LLM**: Template engine stitches help text + error context → structured response

Response includes: text, highlight targets (selector + style), suggestion chips, topic links.

### Widget Architecture

- Shadow DOM isolation (no CSS leakage)
- <15KB gzipped (vanilla TypeScript, no framework)
- Hybrid toast model: proactive toasts on conditions (errors, idle), chat on click
- Field observation via MutationObserver (validation errors) + IntersectionObserver (scroll)
- State machine: IDLE → TOAST → CHAT_OPEN → CHAT_ASKING → CHAT_SHOWING → IDLE

---

## 6. What's Built (Phase 1 — Current State)

All code compiles and builds. Project structure:

```
src/LucidSupport/
├── LucidSupport.csproj              ✅ .NET 10, refs DoomSummarizer.Core + Playwright
├── Program.cs                       ✅ Spectre.Console CLI with learn/ingest commands
├── Models/
│   ├── PageModel.cs                 ✅ Central model (frontmatter + fields + conditions + topics)
│   ├── FieldDefinition.cs           ✅ Field with selector, type, validation, errors, help
│   ├── ConditionRule.cs             ✅ When/suggest/highlight rules
│   ├── LayoutBreakpoint.cs          ✅ Responsive layout info
│   ├── NavInfo.cs                   ✅ Navigation link (label + selector)
│   ├── TopicMapping.cs              ✅ FAQ → KB article mapping
│   ├── PageContext.cs               ✅ Runtime widget context (no PII)
│   ├── HelpResponse.cs              ✅ API response (text, highlights, suggestions, topics)
│   └── VisualFieldInfo.cs           ✅ Visual analysis models (style, error state, groups)
├── Services/
│   ├── Learning/
│   │   ├── PageLearnerService.cs    ✅ Orchestrator: 6-phase deep learning pipeline
│   │   ├── FormFieldExtractor.cs    ✅ DOM field extraction (JS injection)
│   │   ├── ValidationExtractor.cs   ✅ HTML5 constraint validation capture
│   │   ├── LayoutAnalyzer.cs        ✅ Multi-breakpoint responsive analysis
│   │   ├── NavigationExtractor.cs   ✅ Back/next/cancel link detection
│   │   ├── VisualAnalyzer.cs        ✅ CSS style analysis, grouping, screenshots
│   │   ├── InteractionProber.cs     ✅ Active probing (focus, validation, dependencies)
│   │   └── SupportMarkdownWriter.cs ✅ PageModel → .support.md serializer
│   └── Ingestion/
│       ├── SupportMarkdownParser.cs ✅ .support.md → PageModel parser
│       └── PatternRegistry.cs       ✅ Named data pattern detection (13 patterns)
├── Commands/
│   ├── LearnCommand.cs              ✅ CLI: lucidsupport learn <url> [--follow-nav] [-o dir]
│   └── IngestCommand.cs             ✅ CLI: lucidsupport ingest <file> (stub for Phase 2)
```

### What the Learner Captures Today

| Category | Data Captured |
|----------|--------------|
| **Structure** | Every input/select/textarea with selector, type, name, label, placeholder, validation attributes, ARIA refs |
| **Patterns** | 13 named patterns (email, phone, credit-card, cvv, date-partial, postal-code, password, url, currency, name, address, username, search) |
| **Visual** | Bounding boxes, computed CSS styles (bg, border, font, padding, shadow), visual prominence, dark/light theme, brand color, multi-column detection |
| **Grouping** | Visual field groups from fieldset/section/role="group" with heading detection and boundary detection |
| **Reading order** | Fields sorted by visual position (top-to-bottom, left-to-right) |
| **Error visuals** | Error borders (red detection), error icons, error message elements with position (above/below/right/inline), aria-invalid |
| **Focus effects** | Style changes on focus, floating labels, focus-triggered help text, tooltips |
| **Validation** | Pattern-specific test inputs → captured error messages per test case. HTML5 constraint validation messages |
| **Field features** | Char counters, password strength meters, autocomplete suggestions, input masking, show/hide toggles |
| **Dependencies** | Select/checkbox → show/hide/enable/disable other fields |
| **Navigation** | Back/next/cancel/skip/save links with selectors |
| **Layout** | Column counts at 4 breakpoints (375/768/1024/1440px), deduplicated |
| **Flow** | Multi-page flow discovery via --follow-nav with step linking |
| **Screenshots** | Full page + per-field (with padding) for visual reference |

---

## 7. What's Planned (Phases 2-4)

### Phase 2: Ingestion + Scoped Retrieval
- `SupportIngestorService`: Parse `.support.md` → create ContentItems → store in StorageService with ONNX embeddings
- `PageContextMatcher`: URL pattern matching with glob support
- `ScopedRetrievalService`: Wraps DoomSummarizer's RetrievalPipeline with page/field/source filtering
- `ConditionEvaluator`: Runtime evaluation of when/suggest rules against live field states
- `MicroContextBuilder`: Build tiny prompts (~200 tokens) from matched help + context
- `TemplateResponseEngine`: No-LLM fallback that stitches help text directly
- KB auto-linking: Embed topic questions → find nearest KB articles in existing corpus

### Phase 3: API + Widget
- `/api/help/contextual` (POST): Main help endpoint — PageContext in, HelpResponse out
- `/api/learn/page` (POST): Trigger learning from API
- `/widget/sdk.js`: Shadow DOM widget (TypeScript, <15KB)
- Field observer: MutationObserver for validation, IntersectionObserver for scroll
- Toast system: Proactive toasts on conditions, expand to chat on click
- SSE streaming for longer responses

### Phase 4: Polish + Production
- Responsive widget (desktop panel → mobile bottom sheet)
- Analytics: track help requests per page/field, identify pain points
- Feedback loop: index support conversations back into corpus
- Vision model integration: screenshot → LLM description for visual documentation
- Accessibility audit: ARIA compliance checking during learning

---

## 8. Architecture Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Playwright for learning | Not Puppeteer/Selenium | Same library as DoomSummarizer's WebsiteFetcher; .NET native; built-in page.Evaluate |
| `.support.md` as source of truth | Not database | Human-editable, git-diffable, version-controllable. Humans improve what the learner generates |
| YAML frontmatter | Not JSON | More readable for metadata; consistent with DoomSummarizer YAML config |
| Named patterns over regex | `credit-card` not `/\d{16}/` | Never store actual validation regex (security); pattern names are semantic |
| Visual analysis via CSS injection | Not image ML (yet) | CSS computed styles give precise, structured data. Screenshots captured for future ML/vision model use |
| Active probing | Not passive-only | Passive extraction misses: focus effects, validation messages, conditional fields, dependencies |
| Per-field screenshots | Not just full page | Enables future vision model analysis of individual field appearance |
| Shadow DOM widget | Not iframe | Better integration, lighter weight, still fully isolated |
| Optional LLM | Not LLM-required | System works with template responses from `.support.md`; LLM is conversational polish |
| DoomSummarizer.Core reference | Not standalone | Proven StorageService, embedding, retrieval, and scoring infrastructure |

---

## 9. Privacy Model

**Guarantee: No PII ever reaches the AI.**

| Phase | Data | PII? |
|-------|------|------|
| Learning | DOM structure, CSS styles, validation attributes, label text, error messages | No — never reads `.value` |
| Screenshots | Visual capture of the page structure (empty fields or with placeholder text only) | No — fields are empty/placeholder during learning |
| Ingestion | Field metadata, help text, conditions, topic mappings | No — authored content only |
| Runtime widget | URL, field IDs, field states (hasValue/hasError/errorText), viewport width | No — explicitly excludes `.value`; only boolean states |
| LLM prompt | Page title, field label, error message, 2-3 help chunks, user question | No — structural context only |

---

## 10. How to Run

```bash
# Build
dotnet build src/LucidSupport/LucidSupport.csproj -c Release

# Learn a single page
dotnet run --project src/LucidSupport -- learn https://example.com/checkout/payment -o ./output

# Learn a multi-step flow
dotnet run --project src/LucidSupport -- learn https://example.com/checkout/cart --follow-nav -o ./output

# Output: ./output/checkout-payment.support.md + ./output/screenshots/
```
