import { extractPage } from '@/lib/extractor';

// Track the currently highlighted element
let highlightedEl: Element | null = null;
let highlightOverlay: HTMLDivElement | null = null;

/**
 * Create or get the highlight overlay element.
 */
function getOverlay(): HTMLDivElement {
  if (highlightOverlay) return highlightOverlay;

  highlightOverlay = document.createElement('div');
  highlightOverlay.id = 'ls-highlight-overlay';
  highlightOverlay.style.cssText = `
    position: fixed;
    pointer-events: none;
    border: 3px solid #4361ee;
    border-radius: 4px;
    background: rgba(67, 97, 238, 0.1);
    z-index: 2147483647;
    transition: all 0.2s ease-out;
    box-shadow: 0 0 0 4px rgba(67, 97, 238, 0.3);
  `;
  document.body.appendChild(highlightOverlay);
  return highlightOverlay;
}

/**
 * Highlight an element on the page by selector.
 */
function highlightElement(selector: string): boolean {
  try {
    // Handle :has-text() pseudo-selector (not native CSS)
    let el: Element | null = null;
    const hasTextMatch = selector.match(/^(\w+):has-text\("(.+)"\)$/);
    if (hasTextMatch) {
      const [, tag, text] = hasTextMatch;
      const candidates = document.querySelectorAll(tag);
      for (const candidate of candidates) {
        if (candidate.textContent?.trim().startsWith(text)) {
          el = candidate;
          break;
        }
      }
    } else {
      el = document.querySelector(selector);
    }

    if (!el) return false;

    const rect = el.getBoundingClientRect();
    const overlay = getOverlay();

    overlay.style.top = `${rect.top - 4}px`;
    overlay.style.left = `${rect.left - 4}px`;
    overlay.style.width = `${rect.width + 8}px`;
    overlay.style.height = `${rect.height + 8}px`;
    overlay.style.display = 'block';

    // Pulse animation
    overlay.animate(
      [
        { boxShadow: '0 0 0 4px rgba(67, 97, 238, 0.3)' },
        { boxShadow: '0 0 0 8px rgba(67, 97, 238, 0.1)' },
        { boxShadow: '0 0 0 4px rgba(67, 97, 238, 0.3)' },
      ],
      { duration: 1000, iterations: 3 },
    );

    // Scroll into view if needed
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });

    highlightedEl = el;
    return true;
  } catch {
    return false;
  }
}

/**
 * Remove the highlight.
 */
function clearHighlight() {
  if (highlightOverlay) {
    highlightOverlay.style.display = 'none';
  }
  highlightedEl = null;
}

export default defineContentScript({
  matches: ['<all_urls>'],
  main() {
    browser.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      if (msg?.type === 'EXTRACT_PAGE') {
        const result = extractPage();
        sendResponse(result);
      } else if (msg?.type === 'HIGHLIGHT_ELEMENT') {
        const success = highlightElement(msg.selector);
        sendResponse({ success });
      } else if (msg?.type === 'CLEAR_HIGHLIGHT') {
        clearHighlight();
        sendResponse({ success: true });
      }
      return true;
    });
  },
});
