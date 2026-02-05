// ── LucidSupport Widget SDK — Field Observer ──
// Passive field monitoring using event delegation, MutationObserver, and IntersectionObserver.
// Zero layout shift, <1ms per frame.

import type { FieldState } from './types';

/** Callback when a field's state changes. */
export type FieldChangeCallback = (selector: string, state: FieldState) => void;

/** Callback when a form submission attempt is detected. */
export type SubmitCallback = () => void;

export interface ObserverOptions {
  /** Known field selectors from the page model. Only these are tracked. */
  trackedSelectors: string[];
  /** Milliseconds of idle on a focused field before triggering field_idle. */
  fieldIdleMs: number;
  /** Called when any tracked field changes state. */
  onFieldChange: FieldChangeCallback;
  /** Called when a field has been focused longer than fieldIdleMs. */
  onFieldIdle: (selector: string) => void;
  /** Called when a form submit is detected. */
  onSubmit: SubmitCallback;
}

/** Manages all field observation. Call destroy() to clean up. */
export function createFieldObserver(options: ObserverOptions) {
  const { trackedSelectors, fieldIdleMs, onFieldChange, onFieldIdle, onSubmit } = options;

  // Set of tracked selectors for fast lookup
  const tracked = new Set(trackedSelectors);
  // Last known field states
  const fieldStates = new Map<string, FieldState>();
  // Visible fields (IntersectionObserver)
  const visibleFields = new Map<string, boolean>();
  // Currently focused field
  let focusedSelector: string | null = null;
  let fieldIdleTimer: ReturnType<typeof setTimeout> | null = null;
  // Mutation processing flag
  let pendingProcess = false;

  // ── Event Delegation ──
  function onFocusIn(e: FocusEvent) {
    const target = e.target;
    if (!(target instanceof Element)) return;
    const selector = findTrackedSelector(target);
    if (!selector) return;

    focusedSelector = selector;
    clearFieldIdleTimer();
    fieldIdleTimer = setTimeout(() => onFieldIdle(selector), fieldIdleMs);

    updateFieldState(selector, target);
  }

  function onFocusOut(e: FocusEvent) {
    const target = e.target;
    if (!(target instanceof Element)) return;
    const selector = findTrackedSelector(target);
    if (!selector) return;

    // Debounce: allow validation to fire before reading state
    setTimeout(() => {
      if (focusedSelector === selector) focusedSelector = null;
      clearFieldIdleTimer();
      updateFieldState(selector, target);
    }, 300);
  }

  function onFormSubmit(e: Event) {
    const form = e.target;
    if (form instanceof HTMLFormElement) {
      onSubmit();
    }
  }

  // ── MutationObserver ──
  const mutationObserver = new MutationObserver((mutations) => {
    if (pendingProcess) return;
    pendingProcess = true;
    scheduleIdle(() => {
      processMutationBatch(mutations);
      pendingProcess = false;
    });
  });

  function processMutationBatch(mutations: MutationRecord[]) {
    const affectedSelectors = new Set<string>();

    for (const mutation of mutations) {
      // Attribute changes on tracked elements
      if (mutation.type === 'attributes' && mutation.target instanceof Element) {
        const selector = findTrackedSelector(mutation.target);
        if (selector) affectedSelectors.add(selector);
      }

      // New child elements that might be error messages
      if (mutation.type === 'childList') {
        for (const node of mutation.addedNodes) {
          if (node instanceof Element) {
            checkNearbyFields(node, affectedSelectors);
          }
        }
        for (const node of mutation.removedNodes) {
          if (node instanceof Element) {
            checkNearbyFields(node, affectedSelectors);
          }
        }
      }
    }

    // Re-check state for all affected fields
    for (const selector of affectedSelectors) {
      const el = document.querySelector(selector);
      if (el) updateFieldState(selector, el);
    }
  }

  // ── IntersectionObserver ──
  const intersectionObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        const selector = elementToSelector.get(entry.target);
        if (selector) visibleFields.set(selector, entry.isIntersecting);
      }
    },
    { threshold: 0.1 }
  );

  // Reverse map: Element → selector (since we don't modify host DOM)
  const elementToSelector = new Map<Element, string>();

  // ── State Management ──
  function updateFieldState(selector: string, el: Element) {
    const newState: FieldState = {
      hasValue: hasFieldValue(el),
      hasError: isFieldInError(el),
      errorText: getErrorText(el),
      hasFocus: focusedSelector === selector,
    };

    const prev = fieldStates.get(selector);
    if (!prev || stateChanged(prev, newState)) {
      fieldStates.set(selector, newState);
      onFieldChange(selector, newState);
    }
  }

  function stateChanged(a: FieldState, b: FieldState): boolean {
    return a.hasValue !== b.hasValue
      || a.hasError !== b.hasError
      || a.errorText !== b.errorText
      || a.hasFocus !== b.hasFocus;
  }

  // ── Field Analysis (Framework-Agnostic) ──
  function hasFieldValue(el: Element): boolean {
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
      return el.value.length > 0;
    }
    if (el instanceof HTMLSelectElement) {
      return el.selectedIndex > 0; // First option is usually a placeholder
    }
    return false;
  }

  function isFieldInError(el: Element): boolean {
    // HTML5 Constraint API
    if (el instanceof HTMLInputElement && !el.validity.valid) return true;
    if (el instanceof HTMLTextAreaElement && !el.validity.valid) return true;
    if (el instanceof HTMLSelectElement && !el.validity.valid) return true;

    // ARIA
    if (el.getAttribute('aria-invalid') === 'true') return true;

    // CSS classes (Bootstrap, Tailwind, Material, custom)
    const cls = el.className.toString().toLowerCase();
    if (/\b(error|invalid|danger|has-error|is-invalid|field-error|ng-invalid)\b/.test(cls)) return true;

    // Parent wrapper classes (React Hook Form, Formik patterns)
    const parent = el.parentElement;
    if (parent) {
      const pcls = parent.className.toString().toLowerCase();
      if (/\b(error|invalid|has-error|field-error)\b/.test(pcls)) return true;
    }

    return false;
  }

  function getErrorText(el: Element): string | null {
    // aria-errormessage reference
    const errId = el.getAttribute('aria-errormessage');
    if (errId) {
      const errEl = document.getElementById(errId);
      if (errEl && isVisible(errEl)) return errEl.textContent?.trim() || null;
    }

    // aria-describedby pointing to error element
    const descBy = el.getAttribute('aria-describedby');
    if (descBy) {
      for (const id of descBy.split(/\s+/)) {
        const ref = document.getElementById(id);
        if (ref && isVisible(ref) && /error|invalid|help/.test(ref.className.toLowerCase())) {
          return ref.textContent?.trim() || null;
        }
      }
    }

    // Adjacent sibling with error class
    const sib = el.nextElementSibling;
    if (sib && isVisible(sib as HTMLElement) && /error|invalid|validation|field-error/.test(sib.className.toLowerCase())) {
      return sib.textContent?.trim() || null;
    }

    // Parent's child with error role
    const parent = el.parentElement;
    if (parent) {
      const errChild = parent.querySelector('[role="alert"], .error-message, .field-error, .validation-message');
      if (errChild && isVisible(errChild as HTMLElement)) {
        return errChild.textContent?.trim() || null;
      }
    }

    return null;
  }

  // ── Helpers ──
  function findTrackedSelector(el: Element): string | null {
    // Direct match by id
    if (el.id && tracked.has(`#${el.id}`)) return `#${el.id}`;

    // Check if element matches any tracked selector
    for (const sel of tracked) {
      try {
        if (el.matches(sel)) return sel;
      } catch {
        // Invalid selector — skip
      }
    }
    return null;
  }

  function checkNearbyFields(node: Element, affectedSelectors: Set<string>) {
    // Check if the added/removed element is near a tracked field
    const parent = node.parentElement;
    if (!parent) return;

    for (const sel of tracked) {
      try {
        if (parent.querySelector(sel)) affectedSelectors.add(sel);
      } catch {
        // Invalid selector — skip
      }
    }
  }

  function isVisible(el: Element): boolean {
    if (el instanceof HTMLElement) {
      return el.offsetParent !== null && !el.hidden;
    }
    return true;
  }

  function clearFieldIdleTimer() {
    if (fieldIdleTimer !== null) {
      clearTimeout(fieldIdleTimer);
      fieldIdleTimer = null;
    }
  }

  function scheduleIdle(fn: () => void) {
    if ('requestIdleCallback' in window) {
      requestIdleCallback(fn, { timeout: 100 });
    } else {
      setTimeout(fn, 16);
    }
  }

  // ── Lifecycle ──
  function start() {
    // Event delegation
    document.addEventListener('focusin', onFocusIn, { passive: true });
    document.addEventListener('focusout', onFocusOut, { passive: true });
    document.addEventListener('submit', onFormSubmit, { passive: true, capture: true });

    // Mutation observer
    mutationObserver.observe(document.body, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: [
        'class',
        'aria-invalid',
        'aria-errormessage',
        'aria-describedby',
        'disabled',
        'hidden',
        'aria-hidden',
      ],
      characterData: false,
    });

    // Intersection observer — track all known fields
    for (const sel of tracked) {
      try {
        const el = document.querySelector(sel);
        if (el) {
          elementToSelector.set(el, sel);
          intersectionObserver.observe(el);
          visibleFields.set(sel, true); // Assume visible initially
        }
      } catch {
        // Invalid selector — skip
      }
    }

    // Initial state scan
    for (const sel of tracked) {
      const el = document.querySelector(sel);
      if (el) updateFieldState(sel, el);
    }
  }

  function destroy() {
    document.removeEventListener('focusin', onFocusIn);
    document.removeEventListener('focusout', onFocusOut);
    document.removeEventListener('submit', onFormSubmit, { capture: true } as EventListenerOptions);
    mutationObserver.disconnect();
    intersectionObserver.disconnect();
    clearFieldIdleTimer();
    elementToSelector.clear();
    fieldStates.clear();
    visibleFields.clear();
  }

  /** Get all current field states (for building PageContext). */
  function getFieldStates(): Record<string, FieldState> {
    const result: Record<string, FieldState> = {};
    for (const [sel, state] of fieldStates) {
      result[sel] = state;
    }
    return result;
  }

  /** Get visible field IDs. */
  function getVisibleFieldIds(): string[] {
    return Array.from(visibleFields.entries())
      .filter(([, visible]) => visible)
      .map(([sel]) => sel);
  }

  return { start, destroy, getFieldStates, getVisibleFieldIds };
}

export type FieldObserver = ReturnType<typeof createFieldObserver>;
