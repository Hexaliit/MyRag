// ── LucidSupport Widget SDK — Client-Side Condition Evaluator ──
// Evaluates .support.md condition rules against current page state.
// Runs entirely client-side — no API call needed.

import type { ConditionRule, PageState } from './types';

/** Evaluate a single condition rule against current page state. */
export function evaluateCondition(rule: ConditionRule, state: PageState): boolean {
  const parts = rule.when.split(/\s+AND\s+/i);
  return parts.every(part => evaluatePart(part.trim(), state));
}

/** Evaluate all conditions, returning those that match. */
export function evaluateConditions(rules: ConditionRule[], state: PageState): ConditionRule[] {
  return rules.filter(rule => evaluateCondition(rule, state));
}

function evaluatePart(expr: string, state: PageState): boolean {
  // [#selector].error — field is in error state
  let match = expr.match(/^\[([^\]]+)\]\.error(?:\.(\w+))?$/);
  if (match) {
    const fieldState = state.fieldStates[match[1]];
    if (!fieldState) return false;
    if (!fieldState.hasError) return false;
    // If a specific error type is specified (e.g., .error.expired), check errorText
    if (match[2] && fieldState.errorText) {
      return fieldState.errorText.toLowerCase().includes(match[2].toLowerCase());
    }
    return fieldState.hasError;
  }

  // [#selector].empty — field has no value
  match = expr.match(/^\[([^\]]+)\]\.empty$/);
  if (match) {
    const fieldState = state.fieldStates[match[1]];
    return fieldState ? !fieldState.hasValue : true;
  }

  // [#selector].changed — field value changed since last check
  match = expr.match(/^\[([^\]]+)\]\.changed$/);
  if (match) return state.changedFields.has(match[1]);

  // [#selector].focus — field currently has focus
  match = expr.match(/^\[([^\]]+)\]\.focus$/);
  if (match) {
    const fieldState = state.fieldStates[match[1]];
    return fieldState?.hasFocus ?? false;
  }

  // page.idle > Ns — user hasn't interacted for N seconds
  match = expr.match(/^page\.idle\s*>\s*(\d+)s$/);
  if (match) return state.idleSeconds >= parseInt(match[1], 10);

  // form.incomplete — at least one required field is empty
  if (expr === 'form.incomplete') return state.hasIncompleteRequired;

  // user.attempts > N — number of form submit attempts
  match = expr.match(/^user\.attempts\s*>\s*(\d+)$/);
  if (match) return state.submitAttempts > parseInt(match[1], 10);

  // Unknown expression — don't match
  return false;
}
