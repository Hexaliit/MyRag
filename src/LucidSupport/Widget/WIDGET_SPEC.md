# LucidSupport Widget SDK — Technical Specification

## 1. Design Constraints

| Constraint | Target | Rationale |
|-----------|--------|-----------|
| Bundle size | <12KB gzipped | Must not noticeably affect page load. Intercom's loader is ~8KB; full widget loads lazily |
| Framework | Vanilla TypeScript | Zero dependencies. No React, no Vue, no virtual DOM. Template literals + direct DOM |
| CSS isolation | Shadow DOM | Widget styles can't leak out; host page styles can't break widget |
| Page impact | Zero layout shift, <1ms per frame | Never block main thread. All observation is passive/async |
| PII | Never reads `.value` | Privacy by architecture. Only reads: field IDs, CSS classes, `aria-*` attrs, `validity` state |
| CSP compatible | No `eval`, no `unsafe-inline` styles | Works under strict Content-Security-Policy headers |
| Browser support | Chrome 90+, Firefox 90+, Safari 15+, Edge 90+ | Shadow DOM v1, MutationObserver, IntersectionObserver all stable |

---

## 2. Loading Strategy: Two-Phase Load

The widget loads in two phases to minimize page impact:

### Phase 1: Loader (~1.5KB)

```html
<script src="https://support.example.com/widget/loader.js"
        data-site="myapp"
        data-api="https://support.example.com/api"
        data-position="bottom-right"
        data-theme="auto"
        async defer></script>
```

The loader is tiny. It does exactly four things:

1. **Reads `data-*` attributes** from its own `<script>` tag (using `document.currentScript`)
2. **Creates the host element**: `<lucid-support>` appended to `document.body`
3. **Attaches Shadow DOM**: `attachShadow({ mode: 'closed' })` — closed mode prevents host page JS from reaching inside
4. **Waits for idle, then lazy-loads Phase 2**: `requestIdleCallback(() => import('./widget.js'))`

If `requestIdleCallback` isn't available (Safari), falls back to `setTimeout(fn, 1)` after `load` event.

### Phase 2: Widget Core (~10KB)

Loaded via dynamic `import()` only after the page is idle. Contains:
- State machine
- Field observer
- Toast/panel UI
- API client
- CSS (as a JS string constant, injected into shadow root via `<style>` element — CSP-safe because it's not inline on a host element)

### Why Not an Iframe?

Iframes create a separate browsing context. The widget needs to:
- Observe field states on the host page (MutationObserver — can't cross iframe boundary)
- Highlight host page elements (inject CSS classes — can't from iframe)
- Read field `aria-*` attributes (same-origin only from host page)

Shadow DOM gives isolation without losing host page access.

---

## 3. CSS Isolation Architecture

### Shadow DOM Structure

```
<lucid-support>                        ← host element (on the page)
  #shadow-root (closed)                ← shadow boundary — nothing crosses this
    <style>                            ← widget CSS lives here (injected from JS constant)
      .ls-fab { ... }
      .ls-toast { ... }
      .ls-panel { ... }
    </style>
    <div class="ls-root" data-theme="light">
      <button class="ls-fab">?</button>        ← help button (FAB)
      <div class="ls-toast-container"></div>    ← toast region
      <div class="ls-panel"></div>              ← slide-in panel
    </div>
```

### Why Closed Shadow Root?

- **`mode: 'closed'`**: Host page JS cannot call `el.shadowRoot` to reach inside. This prevents accidental or intentional style/DOM manipulation from the host page.
- Intercom uses closed mode. Zendesk uses open mode (but wraps with an iframe anyway).
- We keep a private reference to the shadow root inside our module closure.

### CSS Strategy Inside Shadow DOM

All CSS is a string constant in the JS bundle:

```typescript
const STYLES = `
  :host { all: initial; position: fixed; z-index: 2147483647; }
  .ls-fab { position: fixed; bottom: 20px; right: 20px; ... }
  ...
`;
```

Injected as:
```typescript
const style = document.createElement('style');
style.textContent = STYLES;
shadowRoot.appendChild(style);
```

This is CSP-safe — `style.textContent` is not inline styles (`style="..."` on elements) and doesn't require `unsafe-inline`. Only `<style>` elements created via DOM API and text content assignment.

### Theme Detection

```typescript
function detectTheme(): 'light' | 'dark' {
  // 1. Check data-theme attribute on script tag
  // 2. Check prefers-color-scheme media query
  // 3. Check host page body background luminance
  const bg = getComputedStyle(document.body).backgroundColor;
  return luminance(parseRgb(bg)) < 0.5 ? 'dark' : 'light';
}
```

The widget ships two theme variants (CSS custom properties), toggled by `data-theme` attribute on `.ls-root`.

---

## 4. Field Observation — Zero-Cost Passive Monitoring

The widget needs to know field states without adding per-field event listeners or polling. Three complementary techniques:

### 4a. Event Delegation (Bubbling)

A single listener on `document` captures all field interactions:

```typescript
document.addEventListener('focusin', onFieldFocus, { passive: true, capture: false });
document.addEventListener('focusout', onFieldBlur, { passive: true, capture: false });
```

**Why `focusin`/`focusout` not `focus`/`blur`?** — `focus`/`blur` don't bubble. `focusin`/`focusout` do. One listener catches all fields.

**What we do on focus:**
1. Check if `event.target` matches a known selector from the `.support.md` page model
2. If yes, note which field has focus (for contextual help)
3. Start an idle timer (if user sits on field > 5s without typing, consider showing help)

**What we do on blur:**
1. After a 300ms debounce (allow validation to fire), read the field's state:
   - `el.validity.valid` (HTML5 constraint API — no value access)
   - `el.getAttribute('aria-invalid')`
   - `el.classList` (check for error classes)
   - Adjacent error elements (checked via selector from `.support.md`)
2. Compare with previous state. If changed → evaluate conditions.

**Why passive listeners?** — `{ passive: true }` tells the browser this listener will never call `preventDefault()`, so it can optimize scrolling and input handling. Zero impact on input latency.

### 4b. MutationObserver (DOM Change Detection)

This is the key to framework-agnostic validation detection. React, Vue, Angular — they all eventually mutate the DOM.

```typescript
const observer = new MutationObserver(onMutations);
observer.observe(document.body, {
  subtree: true,
  childList: true,       // new elements added (error messages)
  attributes: true,       // class/aria changes on fields
  attributeFilter: [       // only watch relevant attributes
    'class',
    'aria-invalid',
    'aria-errormessage',
    'aria-describedby',
    'disabled',
    'hidden',
    'aria-hidden'
  ],
  characterData: false     // don't watch text changes (too noisy)
});
```

**What the callback does:**

```typescript
function onMutations(mutations: MutationRecord[]) {
  // Batch — don't process one mutation at a time
  // Schedule processing on next idle callback
  if (!pendingProcess) {
    pendingProcess = true;
    scheduleIdle(() => {
      processMutationBatch(mutations);
      pendingProcess = false;
    });
  }
}
```

The processor checks:
1. **Attribute changes on known fields**: Did `aria-invalid` become `"true"`? Did a class matching `/error|invalid|danger/` get added?
2. **New child elements near known fields**: Did a new element with an error-class appear as a sibling or child of a field's container?
3. **Element visibility changes**: Did a `hidden`/`aria-hidden` attribute change on a field? (conditional field appeared)

### 4c. IntersectionObserver (Visibility & Scroll)

Used for two things:

1. **Which fields are visible**: Only collect state for fields currently in the viewport. Don't waste cycles on off-screen fields.
2. **Scroll position awareness**: If user scrolls past a section, dismiss related toasts.

```typescript
const visibilityObserver = new IntersectionObserver(
  (entries) => {
    for (const entry of entries) {
      const sel = entry.target.getAttribute('data-ls-tracked');
      if (sel) visibleFields.set(sel, entry.isIntersecting);
    }
  },
  { threshold: 0.1 }  // 10% visible = "in view"
);
```

We don't add `data-ls-tracked` to host elements (that would mutate host DOM). Instead, we maintain an internal `Map<Element, string>` mapping tracked elements to selectors.

### Performance Budget

| Technique | CPU cost | When it runs |
|-----------|----------|-------------|
| Event delegation (2 listeners) | ~0.01ms per event | On focus/blur only (not every keystroke) |
| MutationObserver | ~0.1ms per batch | On DOM changes (batched, processed on idle) |
| IntersectionObserver | ~0.05ms per intersection change | On scroll (browser-optimized, off main thread) |
| **Total per-frame** | **<0.2ms** | **Well under 16ms frame budget** |

---

## 5. State Machine

```
                    ┌──────────────────────────────────────────┐
                    │                  IDLE                     │
                    │  (FAB visible, no toasts, panel closed)   │
                    └────┬──────────┬──────────┬───────────────┘
                         │          │          │
              condition  │   FAB    │   field  │
              triggers   │  click   │  focus   │
                         │          │          │  >5s
                         ▼          ▼          ▼
                    ┌─────────┐ ┌────────┐ ┌──────────┐
                    │  TOAST  │ │ PANEL  │ │  TOAST   │
                    │ showing │ │  OPEN  │ │ (field   │
                    │         │ │        │ │  help)   │
                    └──┬──┬───┘ └──┬─────┘ └───┬──────┘
                       │  │        │           │
                 click │  │dismiss │ type Q    │ click
                       │  │/10s    │           │
                       ▼  ▼        ▼           ▼
                  PANEL  IDLE    ASKING    PANEL OPEN
                   OPEN          │              │
                                 ▼              │
                              SHOWING ──────────┘
                                 │
                               close
                                 │
                                 ▼
                               IDLE
```

### State Definitions

| State | FAB | Toast | Panel | What's happening |
|-------|-----|-------|-------|-----------------|
| `IDLE` | Visible | Hidden | Hidden | Waiting for triggers |
| `TOAST` | Visible | Visible (slide in from right) | Hidden | Condition triggered; showing 1-line suggestion |
| `PANEL_OPEN` | Hidden | Hidden | Visible (slide in from right) | Chat panel open, showing context or waiting for question |
| `ASKING` | Hidden | Hidden | Visible + spinner | Sent question to API, awaiting response |
| `SHOWING` | Hidden | Hidden | Visible + response | Displaying response with highlights/suggestions |

### Transitions

```typescript
type State = 'idle' | 'toast' | 'panel' | 'asking' | 'showing';

interface Transition {
  from: State;
  event: string;
  to: State;
  action?: () => void;
}

const transitions: Transition[] = [
  { from: 'idle',    event: 'condition_trigger', to: 'toast',   action: showToast },
  { from: 'idle',    event: 'fab_click',         to: 'panel',   action: openPanel },
  { from: 'idle',    event: 'field_idle',        to: 'toast',   action: showFieldToast },
  { from: 'toast',   event: 'toast_click',       to: 'panel',   action: expandToast },
  { from: 'toast',   event: 'toast_dismiss',     to: 'idle',    action: hideToast },
  { from: 'toast',   event: 'toast_timeout',     to: 'idle',    action: hideToast },
  { from: 'panel',   event: 'ask_question',      to: 'asking',  action: sendQuestion },
  { from: 'panel',   event: 'panel_close',       to: 'idle',    action: closePanel },
  { from: 'asking',  event: 'response',          to: 'showing', action: showResponse },
  { from: 'asking',  event: 'error',             to: 'panel',   action: showError },
  { from: 'showing', event: 'panel_close',       to: 'idle',    action: closePanel },
  { from: 'showing', event: 'ask_question',      to: 'asking',  action: sendQuestion },
];
```

---

## 6. Page State Collection (The Privacy-Safe Scanner)

When the widget needs to send state to the API, it collects a `PageContext` object:

```typescript
function collectPageContext(question?: string): PageContext {
  const visibleFieldIds: string[] = [];
  const fieldStates: Record<string, FieldState> = {};

  for (const [selector, isVisible] of visibleFields) {
    if (!isVisible) continue;
    const el = document.querySelector(selector);
    if (!el) continue;

    visibleFieldIds.push(selector);

    // Privacy-safe state collection — NEVER reads .value
    const input = el as HTMLInputElement;
    fieldStates[selector] = {
      hasValue: input.value?.length > 0,     // boolean only, not the value itself
      hasError: isFieldInError(el),
      errorText: getErrorText(el),            // from error message element, not from value
      hasFocus: document.activeElement === el,
    };
  }

  return {
    url: location.pathname,
    visibleFieldIds,
    fieldStates,
    viewportWidth: window.innerWidth,
    question: question ?? undefined,
  };
}
```

### Error Detection (Framework-Agnostic)

```typescript
function isFieldInError(el: Element): boolean {
  // 1. HTML5 Constraint API
  if (el instanceof HTMLInputElement && !el.validity.valid) return true;

  // 2. ARIA
  if (el.getAttribute('aria-invalid') === 'true') return true;

  // 3. CSS classes (covers Bootstrap, Tailwind, Material, custom)
  const cls = el.className.toString().toLowerCase();
  if (/\b(error|invalid|danger|has-error|is-invalid|field-error)\b/.test(cls)) return true;

  // 4. Parent wrapper classes (React Hook Form, Formik patterns)
  const parent = el.parentElement;
  if (parent) {
    const pcls = parent.className.toString().toLowerCase();
    if (/\b(error|invalid|has-error|field-error)\b/.test(pcls)) return true;
  }

  return false;
}

function getErrorText(el: Element): string | null {
  // 1. aria-errormessage reference
  const errId = el.getAttribute('aria-errormessage');
  if (errId) {
    const errEl = document.getElementById(errId);
    if (errEl && isElementVisible(errEl)) return errEl.textContent?.trim() || null;
  }

  // 2. aria-describedby pointing to error element
  const descBy = el.getAttribute('aria-describedby');
  if (descBy) {
    for (const id of descBy.split(/\s+/)) {
      const ref = document.getElementById(id);
      if (ref && isElementVisible(ref) && /error|invalid|help/.test(ref.className.toLowerCase())) {
        return ref.textContent?.trim() || null;
      }
    }
  }

  // 3. Adjacent sibling with error class
  const sib = el.nextElementSibling;
  if (sib && isElementVisible(sib as HTMLElement) && /error|invalid|validation|field-error/.test(sib.className.toLowerCase())) {
    return sib.textContent?.trim() || null;
  }

  return null;
}
```

This approach detects validation errors from:
- **React Hook Form**: Adds `aria-invalid` + renders error `<p>` with describedby
- **Formik**: Adds error class on wrapper + renders error `<div>`
- **Vue Vuelidate**: Adds `.invalid` class
- **Angular Reactive Forms**: Adds `.ng-invalid` class
- **Bootstrap**: Adds `.is-invalid` class
- **Tailwind**: Adds `border-red-*` classes (detected via parent class scan)
- **Native HTML5**: `el.validity.valid` covers all constraint validation

---

## 7. Highlighting Host Page Elements

When the API returns `highlights: [{ selector: "#card-number", style: "error" }]`, the widget needs to visually mark elements on the host page — outside the shadow DOM.

### Technique: Overlay Positioning

We **do not** modify host page element styles (that could break their layout or conflict with their CSS). Instead, we create absolutely-positioned overlay elements inside our shadow root that visually appear over the target elements.

```typescript
function showHighlight(selector: string, style: 'error' | 'info' | 'success') {
  const target = document.querySelector(selector);
  if (!target) return;

  const rect = target.getBoundingClientRect();
  const overlay = document.createElement('div');
  overlay.className = `ls-highlight ls-highlight-${style}`;
  overlay.style.cssText = `
    position: fixed;
    top: ${rect.top - 3}px;
    left: ${rect.left - 3}px;
    width: ${rect.width + 6}px;
    height: ${rect.height + 6}px;
    pointer-events: none;
  `;

  shadowRoot.querySelector('.ls-root')!.appendChild(overlay);

  // Animate in
  requestAnimationFrame(() => overlay.classList.add('ls-highlight-active'));

  // Auto-remove after 5s
  setTimeout(() => {
    overlay.classList.add('ls-highlight-fade');
    overlay.addEventListener('transitionend', () => overlay.remove());
  }, 5000);
}
```

**CSS for highlights** (inside shadow root):
```css
.ls-highlight {
  border-radius: 4px;
  transition: opacity 0.3s, box-shadow 0.3s;
  opacity: 0;
  z-index: 2147483646;
}
.ls-highlight-active { opacity: 1; }
.ls-highlight-error { box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.5), 0 0 12px rgba(239, 68, 68, 0.2); }
.ls-highlight-info { box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.5), 0 0 12px rgba(59, 130, 246, 0.2); }
.ls-highlight-success { box-shadow: 0 0 0 3px rgba(34, 197, 94, 0.5), 0 0 12px rgba(34, 197, 94, 0.2); }
.ls-highlight-fade { opacity: 0; }
```

**Repositioning on scroll/resize**: A single `requestAnimationFrame` loop repositions all active highlights:

```typescript
function updateHighlightPositions() {
  if (activeHighlights.size === 0) return;
  for (const [overlay, selector] of activeHighlights) {
    const target = document.querySelector(selector);
    if (!target) { overlay.remove(); activeHighlights.delete(overlay); continue; }
    const rect = target.getBoundingClientRect();
    overlay.style.top = `${rect.top - 3}px`;
    overlay.style.left = `${rect.left - 3}px`;
  }
  if (activeHighlights.size > 0) requestAnimationFrame(updateHighlightPositions);
}
```

---

## 8. API Communication

### Help Requests: `fetch` with `keepalive`

```typescript
async function askForHelp(context: PageContext): Promise<HelpResponse> {
  const response = await fetch(`${apiBase}/api/help/contextual`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(context),
    keepalive: true,  // survives page navigation
  });
  return response.json();
}
```

Why `fetch` not WebSocket:
- Help requests are request/response, not streaming
- No persistent connection to maintain
- `keepalive` ensures in-flight requests survive page navigation
- Works behind proxies that don't support WebSocket
- Simpler, more reliable

### Streaming Responses (Optional SSE)

For longer LLM-generated responses, SSE provides token-by-token streaming:

```typescript
async function askStreaming(context: PageContext, onChunk: (text: string) => void): Promise<void> {
  const response = await fetch(`${apiBase}/api/help/contextual?stream=true`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(context),
  });

  const reader = response.body!.getReader();
  const decoder = new TextDecoder();

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    onChunk(decoder.decode(value, { stream: true }));
  }
}
```

Falls back to regular fetch if ReadableStream isn't available.

### Analytics: `sendBeacon`

```typescript
function trackEvent(event: string, data: Record<string, unknown>) {
  const payload = JSON.stringify({ event, ...data, ts: Date.now() });
  navigator.sendBeacon(`${apiBase}/api/analytics`, payload);
}
```

`sendBeacon` is fire-and-forget, non-blocking, and guaranteed to be sent even during page unload. Perfect for telemetry.

---

## 9. UI Layout

### Responsive Positioning

```
Desktop (>768px):                    Mobile (<768px):
┌──────────────────────────┐         ┌──────────────────────┐
│                          │         │                      │
│    Host page content     │         │   Host page content  │
│                          │         │                      │
│                     ┌────┤         ├──────────────────────┤
│                     │ 🗨 ││         │                      │
│                     │ Pan│         │    Panel (bottom     │
│                     │ el │         │    sheet, full width) │
│                     └────┤         │                      │
│                 [FAB] ●  │         ├──────────────────────┤
└──────────────────────────┘         │              [FAB] ● │
                                     └──────────────────────┘
```

### Panel Structure

```html
<div class="ls-panel">
  <div class="ls-panel-header">
    <span class="ls-panel-title">Help</span>
    <button class="ls-panel-close" aria-label="Close help panel">×</button>
  </div>
  <div class="ls-panel-body">
    <!-- Response content renders here -->
    <div class="ls-response"></div>

    <!-- Suggestion chips -->
    <div class="ls-suggestions">
      <button class="ls-chip">Chip text</button>
    </div>

    <!-- Topic links -->
    <div class="ls-topics">
      <a class="ls-topic-link" href="#">Topic</a>
    </div>
  </div>
  <div class="ls-panel-input">
    <input type="text" class="ls-input" placeholder="Ask a question..."
           aria-label="Ask a help question">
    <button class="ls-send" aria-label="Send">→</button>
  </div>
</div>
```

### Animations (CSS only, no JS animation libraries)

```css
.ls-panel {
  transform: translateX(100%);
  transition: transform 0.3s cubic-bezier(0.4, 0, 0.2, 1);
}
.ls-panel-open {
  transform: translateX(0);
}

.ls-toast {
  transform: translateX(120%);
  transition: transform 0.25s cubic-bezier(0.4, 0, 0.2, 1);
}
.ls-toast-show {
  transform: translateX(0);
}

/* Mobile: bottom sheet */
@media (max-width: 767px) {
  .ls-panel {
    transform: translateY(100%);
    bottom: 0; left: 0; right: 0;
    max-height: 70vh;
    border-radius: 16px 16px 0 0;
  }
  .ls-panel-open {
    transform: translateY(0);
  }
}
```

All animations use `transform` (GPU-composited, no layout thrashing) and `cubic-bezier` for Material Design motion.

---

## 10. Page Model Loading

On initialization, the widget fetches the page model for the current URL:

```typescript
async function loadPageModel(): Promise<SupportPageModel | null> {
  try {
    const response = await fetch(`${apiBase}/api/support/page?url=${encodeURIComponent(location.pathname)}`);
    if (!response.ok) return null;
    return response.json();
  } catch {
    return null;
  }
}
```

The page model tells the widget:
- Which field selectors to observe (only track known fields, not every input)
- What conditions to evaluate
- What topic links to offer
- What help text is available per field

Without a page model (unknown page), the widget shows only the generic FAB + chat. With a page model, it becomes proactive: toasts on conditions, field-specific help.

---

## 11. Condition Evaluation (Client-Side)

Conditions from `.support.md` are evaluated locally in the widget:

```typescript
interface ConditionRule {
  when: string;   // "[#card-number].error AND user.attempts > 2"
  suggest: string;
  highlight?: string;
}

function evaluateCondition(rule: ConditionRule, state: PageState): boolean {
  // Simple expression parser — not a full language
  const parts = rule.when.split(/\s+AND\s+/i);
  return parts.every(part => evaluatePart(part.trim(), state));
}

function evaluatePart(expr: string, state: PageState): boolean {
  // [#selector].error — field is in error state
  const fieldError = expr.match(/^\[([^\]]+)\]\.error$/);
  if (fieldError) return state.fieldStates[fieldError[1]]?.hasError ?? false;

  // [#selector].changed — field value changed since last check
  const fieldChanged = expr.match(/^\[([^\]]+)\]\.changed$/);
  if (fieldChanged) return state.changedFields.has(fieldChanged[1]);

  // page.idle > 30s — user hasn't interacted for N seconds
  const idle = expr.match(/^page\.idle\s*>\s*(\d+)s$/);
  if (idle) return state.idleSeconds >= parseInt(idle[1]);

  // form.incomplete — at least one required field is empty
  if (expr === 'form.incomplete') return state.hasIncompleteRequired;

  // user.attempts > N — number of form submit attempts
  const attempts = expr.match(/^user\.attempts\s*>\s*(\d+)$/);
  if (attempts) return state.submitAttempts > parseInt(attempts[1]);

  return false;
}
```

This runs entirely client-side — no API call needed to check conditions. Only when a condition triggers does the widget optionally call the API for richer context.

---

## 12. Module Structure

```
Widget/
├── loader.ts          ~1.5KB  Entry point: creates host, attaches shadow, lazy-loads core
├── widget.ts          ~3KB    State machine, lifecycle, initialization
├── observer.ts        ~2KB    Event delegation, MutationObserver, IntersectionObserver
├── ui.ts              ~2KB    Toast, panel, highlight rendering (template literals)
├── api.ts             ~1KB    fetch wrapper, sendBeacon analytics
├── conditions.ts      ~0.5KB  Client-side condition evaluator
├── styles.ts          ~1.5KB  CSS string constant (both themes)
└── types.ts           ~0.5KB  TypeScript interfaces (PageContext, HelpResponse, etc.)
                      -------
                      ~12KB    → ~8KB gzipped (estimate)
```

### Build

```bash
# TypeScript → single JS bundle (esbuild for speed, <100ms)
esbuild Widget/loader.ts --bundle --minify --format=esm --outfile=wwwroot/widget/loader.js
esbuild Widget/widget.ts --bundle --minify --format=esm --outfile=wwwroot/widget/widget.js
```

Two output files: `loader.js` (inline in page) and `widget.js` (lazy-loaded).

---

## 13. What the Widget Does NOT Do

| Will not | Why |
|----------|-----|
| Read `.value` from any field | Privacy guarantee |
| Modify host page DOM elements | Could break host page; use overlay highlights instead |
| Add CSS classes to host elements | Could conflict with host styles |
| Use `document.cookie` | Not our cookie jar |
| Create global variables | Module scope only |
| Use `eval()` or `new Function()` | CSP incompatible |
| Use `innerHTML` | XSS risk; use `textContent` + DOM API |
| Block the main thread | All processing is async/idle-scheduled |
| Phone home without user interaction | Only communicates on FAB click, toast click, or condition trigger |
| Load external resources (fonts, icons) | Everything inline in the JS bundle (SVG icons as template literals) |

---

## 14. Graceful Degradation

| Scenario | Behavior |
|----------|----------|
| JavaScript disabled | Widget doesn't load. No harm. |
| Shadow DOM unsupported | Fallback to a basic `<div>` with scoped CSS (prefix all selectors with `[data-ls]`) |
| API unreachable | Widget shows FAB but toasts show cached help text from page model. Chat shows "Help is currently unavailable." |
| Unknown page (no model) | Generic FAB + chat only. No proactive toasts. |
| Strict CSP blocks fetch | Widget detects this and disables API calls, shows only cached page model help |
| Slow network | Loading spinner in panel, toast still works from cached model |
| Multiple instances | `loader.js` checks if `<lucid-support>` already exists, skips if so |
