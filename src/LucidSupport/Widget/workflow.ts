// ── LucidSupport Widget SDK — Workflow Event System ──
// Evaluates workflow rules against field state and emits CustomEvents.
// The host page is responsible for handling visibility changes.

import type { WorkflowRule, WorkflowEvent, SupportSection } from './types';

export interface WorkflowState {
  fieldValues: Record<string, string>;
  checkedFields: Set<string>;
}

/**
 * Evaluate workflow rules against current field state.
 * Returns events to dispatch on the host document.
 */
export function evaluateWorkflowRules(
  rules: WorkflowRule[],
  sections: SupportSection[],
  state: WorkflowState,
  pageId: string
): WorkflowEvent[] {
  const events: WorkflowEvent[] = [];

  // Sort by priority (lower first)
  const sorted = [...rules].sort((a, b) => a.priority - b.priority);

  for (const rule of sorted) {
    if (!evaluateWorkflowCondition(rule.when, state)) continue;

    const isSectionTarget = sections.some(s => `[${s.id}]` === rule.target || s.id === rule.target);
    const prefix = isSectionTarget ? 'ls:section' : 'ls:field';
    const eventType = `${prefix}:${rule.action}` as WorkflowEvent['type'];

    events.push({
      type: eventType,
      target: rule.target,
      rule: rule.when,
      pageId,
    });
  }

  return events;
}

/**
 * Dispatch workflow events on the host document.
 */
export function dispatchWorkflowEvents(events: WorkflowEvent[]): void {
  for (const event of events) {
    document.dispatchEvent(new CustomEvent('ls:workflow', {
      detail: event,
      bubbles: true,
      composed: true,
    }));
  }
}

/**
 * Evaluate a single workflow condition expression.
 * Supports: [#selector].checked, [#selector].value == "x",
 * [#selector].empty, [#selector].hasValue, AND/OR combinations.
 */
function evaluateWorkflowCondition(expression: string, state: WorkflowState): boolean {
  // Handle AND
  if (expression.includes(' AND ')) {
    return expression.split(' AND ').every(p => evaluateWorkflowCondition(p.trim(), state));
  }

  // Handle OR
  if (expression.includes(' OR ')) {
    return expression.split(' OR ').some(p => evaluateWorkflowCondition(p.trim(), state));
  }

  const expr = expression.trim();

  // [#selector].checked
  const checkedMatch = expr.match(/^\[([^\]]+)\]\.checked$/i);
  if (checkedMatch) {
    return state.checkedFields.has(checkedMatch[1]);
  }

  // [#selector].value == "value"
  const valueMatch = expr.match(/^\[([^\]]+)\]\.value\s*==\s*"([^"]*)"$/i);
  if (valueMatch) {
    const val = state.fieldValues[valueMatch[1]] ?? '';
    return val.toLowerCase() === valueMatch[2].toLowerCase();
  }

  // [#selector].empty
  const emptyMatch = expr.match(/^\[([^\]]+)\]\.empty$/i);
  if (emptyMatch) {
    const val = state.fieldValues[emptyMatch[1]] ?? '';
    return val === '';
  }

  // [#selector].hasValue
  const hasValueMatch = expr.match(/^\[([^\]]+)\]\.hasValue$/i);
  if (hasValueMatch) {
    const val = state.fieldValues[hasValueMatch[1]] ?? '';
    return val !== '';
  }

  return false;
}

/**
 * Create a workflow evaluator that observes fields and emits events.
 */
export function createWorkflowEvaluator(
  rules: WorkflowRule[],
  sections: SupportSection[],
  pageId: string
) {
  let prevEvents: string[] = [];

  function evaluate(): void {
    const state = collectWorkflowState(sections);
    const events = evaluateWorkflowRules(rules, sections, state, pageId);

    // Only dispatch if events changed (avoid spamming)
    const eventKeys = events.map(e => `${e.type}:${e.target}`);
    const changed = eventKeys.length !== prevEvents.length ||
      eventKeys.some((k, i) => k !== prevEvents[i]);

    if (changed) {
      prevEvents = eventKeys;
      dispatchWorkflowEvents(events);
    }
  }

  function collectWorkflowState(sections: SupportSection[]): WorkflowState {
    const fieldValues: Record<string, string> = {};
    const checkedFields = new Set<string>();

    // Collect all field selectors from sections
    const allSelectors = new Set<string>();
    for (const section of sections) {
      for (const sel of section.fields) {
        allSelectors.add(sel);
      }
    }

    // Also check rules for selectors
    for (const rule of rules) {
      const matches = rule.when.matchAll(/\[([^\]]+)\]/g);
      for (const m of matches) {
        allSelectors.add(m[1]);
      }
    }

    for (const selector of allSelectors) {
      const el = document.querySelector(selector) as HTMLInputElement | null;
      if (!el) continue;

      if (el.type === 'checkbox' || el.type === 'radio') {
        if (el.checked) checkedFields.add(selector);
      }
      fieldValues[selector] = el.value ?? '';
    }

    return { fieldValues, checkedFields };
  }

  return {
    evaluate,
    destroy() {
      prevEvents = [];
    },
  };
}
