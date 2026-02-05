// ── LucidSupport Widget SDK — Loader (~1.5KB) ──
// Phase 1: Reads config, creates host element, attaches shadow DOM, lazy-loads core.
// This file is the one embedded in the page via <script> tag.

(function () {
  // Prevent double-initialization
  if (document.querySelector('lucid-support')) return;

  // Read config from script tag's data-* attributes
  const scriptEl = document.currentScript as HTMLScriptElement | null;
  if (!scriptEl) return;

  // Capture script src before it goes out of scope in async contexts
  const scriptSrc = scriptEl.src;

  const config = {
    site: scriptEl.getAttribute('data-site') || '',
    api: scriptEl.getAttribute('data-api') || '',
    position: (scriptEl.getAttribute('data-position') || 'bottom-right') as 'bottom-right' | 'bottom-left',
    theme: (scriptEl.getAttribute('data-theme') || 'auto') as 'light' | 'dark' | 'auto',
  };

  if (!config.site || !config.api) {
    console.warn('[LucidSupport] Missing data-site or data-api attribute on script tag.');
    return;
  }

  // Create host element
  const host = document.createElement('lucid-support');
  // Attach closed shadow DOM (host page JS can't reach inside)
  const shadow = host.attachShadow({ mode: 'closed' });

  function doLoad() {
    // Resolve the widget.js URL relative to the loader script's URL
    const baseUrl = new URL(scriptSrc);
    const widgetUrl = new URL('widget.js', baseUrl).href;

    import(/* webpackIgnore: true */ widgetUrl).then((module) => {
      module.initWidget(shadow, config);
    }).catch((err) => {
      console.warn('[LucidSupport] Failed to load widget core:', err);
    });
  }

  // Lazy-load core after page is idle
  function loadCore() {
    const ric = (window as unknown as Record<string, unknown>).requestIdleCallback as
      ((cb: () => void, opts?: { timeout: number }) => void) | undefined;

    if (typeof ric === 'function') {
      ric(doLoad, { timeout: 3000 });
    } else if (document.readyState === 'complete') {
      setTimeout(doLoad, 1);
    } else {
      window.addEventListener('load', () => setTimeout(doLoad, 1), { once: true });
    }
  }

  // Append to body, then lazy-load
  function mount() {
    document.body.appendChild(host);
    loadCore();
  }

  if (document.body) {
    mount();
  } else {
    document.addEventListener('DOMContentLoaded', mount, { once: true });
  }
})();
