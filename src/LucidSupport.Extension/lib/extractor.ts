import type { ExtractedField, NavLink, PageExtractionResult } from './types';

/**
 * Build a CSS selector for an element.
 * Prefers #id, then tag[name="..."], then a structural path fallback.
 * Ported from FormFieldExtractor.cs lines 21-45.
 */
function buildSelector(el: Element): string {
  if (el.id) return '#' + el.id;

  const htmlEl = el as HTMLInputElement;
  if (htmlEl.name) {
    const tag = el.tagName.toLowerCase();
    const byName = document.querySelectorAll(`${tag}[name="${htmlEl.name}"]`);
    if (byName.length === 1) return `${tag}[name="${htmlEl.name}"]`;
  }

  // Fallback: build a structural path
  const parts: string[] = [];
  let current: Element | null = el;
  while (current && current !== document.body) {
    let sel = current.tagName.toLowerCase();
    if (current.id) {
      parts.unshift('#' + current.id);
      break;
    }
    const parent = current.parentElement;
    if (parent) {
      const siblings = Array.from(parent.children).filter(
        (c) => c.tagName === current!.tagName,
      );
      if (siblings.length > 1) {
        sel += ':nth-of-type(' + (siblings.indexOf(current) + 1) + ')';
      }
    }
    parts.unshift(sel);
    current = current.parentElement;
  }
  return parts.join(' > ');
}

/**
 * Find the label text for a form element using multiple strategies.
 * Ported from FormFieldExtractor.cs lines 47-75.
 */
function findLabel(el: Element): string {
  const htmlEl = el as HTMLInputElement;

  // 1. Explicit <label for="">
  if (el.id) {
    const label = document.querySelector(`label[for="${el.id}"]`);
    if (label) return label.textContent?.trim() || '';
  }

  // 2. aria-label
  const ariaLabel = el.getAttribute('aria-label');
  if (ariaLabel) return ariaLabel;

  // 3. aria-labelledby
  const labelledBy = el.getAttribute('aria-labelledby');
  if (labelledBy) {
    const ref = document.getElementById(labelledBy);
    if (ref) return ref.textContent?.trim() || '';
  }

  // 4. Closest ancestor <label>
  const parentLabel = el.closest('label');
  if (parentLabel) {
    const clone = parentLabel.cloneNode(true) as HTMLElement;
    clone.querySelectorAll('input,select,textarea').forEach((i) => i.remove());
    const text = clone.textContent?.trim();
    if (text) return text;
  }

  // 5. Placeholder as last resort
  if (htmlEl.placeholder) return htmlEl.placeholder;
  return htmlEl.name || '';
}

/**
 * Extract all interactive form fields from the current page.
 * Ported from FormFieldExtractor.cs lines 78-104.
 */
export function extractFields(): ExtractedField[] {
  const fields: ExtractedField[] = [];
  const selector =
    'input, select, textarea, [role="textbox"], [role="combobox"], [role="spinbutton"], [contenteditable="true"]';

  document.querySelectorAll(selector).forEach((el) => {
    const htmlEl = el as HTMLInputElement;

    // Skip hidden/submit/button inputs
    const type = (htmlEl.type || el.tagName.toLowerCase()).toLowerCase();
    if (
      type === 'hidden' ||
      type === 'submit' ||
      type === 'button' ||
      type === 'reset'
    )
      return;

    const computed = window.getComputedStyle(el);
    if (computed.display === 'none' || computed.visibility === 'hidden') return;

    const autocomplete = htmlEl.autocomplete;
    const normalizedAutocomplete =
      autocomplete && autocomplete !== 'on' && autocomplete !== 'off'
        ? autocomplete
        : null;

    fields.push({
      selector: buildSelector(el),
      type,
      name: htmlEl.name || null,
      label: findLabel(el),
      placeholder: htmlEl.placeholder || null,
      required:
        htmlEl.required || el.getAttribute('aria-required') === 'true',
      pattern: htmlEl.pattern || null,
      minLength:
        htmlEl.minLength > 0 ? htmlEl.minLength : null,
      maxLength:
        htmlEl.maxLength > 0 && htmlEl.maxLength < 524288
          ? htmlEl.maxLength
          : null,
      autocomplete: normalizedAutocomplete,
    });
  });

  return fields;
}

/**
 * Classify a button/link by its visible text.
 * Ported from NavigationExtractor.cs lines 18-26.
 */
function classifyNav(text: string): string | null {
  const t = text.toLowerCase().trim();
  if (/^(back|previous|prev|go back|return)/.test(t)) return 'back';
  if (
    /^(next|continue|proceed|forward|submit|pay|place order|confirm|finish|complete)/.test(
      t,
    )
  )
    return 'next';
  if (/^(cancel|close|exit|discard)/.test(t)) return 'cancel';
  if (/^(skip)/.test(t)) return 'skip';
  if (/^(save|draft)/.test(t)) return 'save';
  return null;
}

/**
 * Build a selector for a navigation element.
 * Ported from NavigationExtractor.cs lines 28-37.
 */
function buildNavSelector(el: Element): string | null {
  if (el.id) return '#' + el.id;
  const htmlEl = el as HTMLInputElement;
  if (htmlEl.name)
    return `${el.tagName.toLowerCase()}[name="${htmlEl.name}"]`;
  const text = (el.textContent?.trim() || '').substring(0, 30);
  if (el.tagName === 'BUTTON' || el.tagName === 'A') {
    return `${el.tagName.toLowerCase()}:has-text("${text}")`;
  }
  return null;
}

/**
 * Extract navigation links (back, next, cancel, skip, save) from the page.
 * Ported from NavigationExtractor.cs lines 40-63.
 */
export function extractNavigation(): Record<string, NavLink> {
  const nav: Record<string, NavLink> = {};

  const candidates = document.querySelectorAll(
    'a, button, [role="button"], input[type="submit"]',
  );

  candidates.forEach((el) => {
    const computed = window.getComputedStyle(el);
    if (computed.display === 'none' || computed.visibility === 'hidden') return;

    const htmlEl = el as HTMLInputElement;
    const text =
      el.textContent?.trim() ||
      el.getAttribute('aria-label') ||
      htmlEl.value ||
      '';
    if (!text) return;

    const role = classifyNav(text);
    if (!role) return;

    const selector = buildNavSelector(el);
    if (!selector) return;

    // Don't overwrite — keep first match per role
    if (nav[role]) return;

    nav[role] = {
      role,
      label: text.substring(0, 100),
      selector,
    };
  });

  return nav;
}

/**
 * Full page extraction: combines fields + navigation + page metadata.
 */
export function extractPage(): PageExtractionResult {
  return {
    url: window.location.href,
    title: document.title,
    fields: extractFields(),
    nav: extractNavigation(),
  };
}
