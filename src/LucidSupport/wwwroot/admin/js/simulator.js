/**
 * LucidSupport Admin — Simulator logic (scenario step-through).
 */

const SimulatorScenarios = {
  /**
   * Generate a step-through scenario for a page.
   * Returns an array of state snapshots that can be applied in sequence.
   */
  generateHappyPath(page) {
    const steps = [];
    const state = {};

    // Initialize all fields empty
    for (const f of page.fields) {
      state[f.selector] = { hasValue: false, hasError: false, errorText: null, hasFocus: false };
    }

    // Step through each field
    for (const f of page.fields) {
      // Focus this field
      const focusState = JSON.parse(JSON.stringify(state));
      for (const key of Object.keys(focusState)) {
        focusState[key].hasFocus = (key === f.selector);
      }
      steps.push({ label: `Focus: ${f.label}`, state: focusState });

      // Fill this field
      state[f.selector].hasValue = true;
      state[f.selector].hasFocus = false;
      const fillState = JSON.parse(JSON.stringify(state));
      steps.push({ label: `Fill: ${f.label}`, state: fillState });
    }

    return steps;
  },

  /**
   * Generate an error scenario — trigger validation errors on required fields.
   */
  generateErrorPath(page) {
    const steps = [];
    const state = {};

    for (const f of page.fields) {
      state[f.selector] = { hasValue: false, hasError: false, errorText: null, hasFocus: false };
    }

    // Submit empty → errors on required fields
    for (const f of page.fields) {
      if (f.required) {
        state[f.selector].hasError = true;
        const firstErr = Object.values(f.errors || {})[0];
        if (firstErr) state[f.selector].errorText = firstErr;
      }
    }
    steps.push({ label: 'Submit empty (errors)', state: JSON.parse(JSON.stringify(state)) });

    // Fix fields one by one
    for (const f of page.fields) {
      if (f.required) {
        state[f.selector].hasValue = true;
        state[f.selector].hasError = false;
        state[f.selector].errorText = null;
        steps.push({ label: `Fix: ${f.label}`, state: JSON.parse(JSON.stringify(state)) });
      }
    }

    return steps;
  }
};
