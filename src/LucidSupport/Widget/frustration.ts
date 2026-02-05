// ── LucidSupport Widget SDK — Frustration Tracker ──
// Rolling 2-minute window with weighted frustration signals.
// Triggers "struggling" mode when score reaches threshold.

export type FrustrationSignal =
  | 'rage_click'
  | 'exit_intent'
  | 'validation_error'
  | 'same_field_error'
  | 'slow_dwell'
  | 'field_cycling'
  | 'repeated_correction'
  | 'dead_click';

interface SignalEntry {
  signal: FrustrationSignal;
  weight: number;
  ts: number;
}

const SIGNAL_WEIGHTS: Record<FrustrationSignal, number> = {
  rage_click: 3.0,
  exit_intent: 2.5,
  validation_error: 2.0,
  same_field_error: 2.5,
  slow_dwell: 1.5,
  field_cycling: 1.5,
  repeated_correction: 1.0,
  dead_click: 1.0,
};

const WINDOW_MS = 2 * 60 * 1000; // 2 minute rolling window

export interface FrustrationTracker {
  recordSignal(signal: FrustrationSignal): void;
  recordValidationError(selector: string): void;
  getScore(): number;
  reset(): void;
  onDismiss(): void;
  destroy(): void;
}

export interface FrustrationConfig {
  /** Score threshold to trigger struggling mode. Starts at 5.0, rises after dismissals. */
  threshold: number;
  /** Called when frustration score crosses the threshold. */
  onFrustrated: () => void;
}

export function createFrustrationTracker(config: FrustrationConfig): FrustrationTracker {
  const entries: SignalEntry[] = [];

  // Cooldown after dismiss: 60s → 120s → 240s
  let dismissCount = 0;
  let lastDismissTime = 0;
  let threshold = config.threshold;

  // Detection state
  const clickTimestamps = new Map<string, number[]>(); // element key → click timestamps
  let lastFocusSelector: string | null = null;
  let focusStartTime = 0;
  let focusChangeTimestamps: number[] = [];
  let lastInputTimestamps = new Map<string, number[]>(); // field → input clear timestamps
  let erroredFields = new Set<string>(); // fields that have been errored before

  // Listeners cleanup
  const cleanupFns: (() => void)[] = [];

  // ── Signal Recording ──

  function recordSignal(signal: FrustrationSignal) {
    const now = Date.now();
    entries.push({ signal, weight: SIGNAL_WEIGHTS[signal], ts: now });
    pruneOldEntries(now);

    const score = getScore();
    if (score >= threshold && canTrigger(now)) {
      config.onFrustrated();
    }
  }

  function getScore(): number {
    const now = Date.now();
    pruneOldEntries(now);
    return entries.reduce((sum, e) => sum + e.weight, 0);
  }

  function reset() {
    entries.length = 0;
  }

  // ── Cooldown Logic ──

  function canTrigger(now: number): boolean {
    if (lastDismissTime === 0) return true;

    // Escalating cooldown: 60s, 120s, 240s
    const cooldowns = [60_000, 120_000, 240_000];
    const cooldown = cooldowns[Math.min(dismissCount - 1, cooldowns.length - 1)] ?? 60_000;
    return (now - lastDismissTime) >= cooldown;
  }

  /** Call when user dismisses the struggling prompt. */
  function onDismiss() {
    dismissCount++;
    lastDismissTime = Date.now();
    // After 3 dismissals, raise threshold
    if (dismissCount >= 3) {
      threshold = 7.5;
    }
    reset();
  }

  // ── Pruning ──

  function pruneOldEntries(now: number) {
    const cutoff = now - WINDOW_MS;
    while (entries.length > 0 && entries[0].ts < cutoff) {
      entries.shift();
    }
  }

  // ── DOM Event Detectors ──

  function setupDetectors() {
    // Rage click: 3+ clicks on same element in 1s
    function onDocClick(e: MouseEvent) {
      const target = e.target;
      if (!(target instanceof Element)) return;

      const key = target.id || target.className.toString().slice(0, 50);
      if (!key) return;

      const now = Date.now();
      const timestamps = clickTimestamps.get(key) ?? [];
      timestamps.push(now);

      // Keep only last 1s
      const recent = timestamps.filter(t => now - t < 1000);
      clickTimestamps.set(key, recent);

      if (recent.length >= 3) {
        recordSignal('rage_click');
        clickTimestamps.set(key, []); // reset to avoid repeated firing
      }

      // Dead click: click on something that produces no DOM change in 2s
      // Simplified: if click target is not interactive, count as dead click
      if (!(target instanceof HTMLInputElement || target instanceof HTMLButtonElement ||
            target instanceof HTMLAnchorElement || target instanceof HTMLSelectElement ||
            target instanceof HTMLTextAreaElement || target.getAttribute('role') === 'button')) {
        setTimeout(() => {
          // If no DOM change occurred, record dead click
          // (simplified: we just record it — a real implementation would diff DOM)
          recordSignal('dead_click');
        }, 2000);
      }
    }

    // Exit intent: mouse leaves viewport upward
    function onMouseLeave(e: MouseEvent) {
      if (e.clientY <= 0) {
        recordSignal('exit_intent');
      }
    }

    // Focus tracking: slow dwell (>30s on one field) + field cycling (5+ changes in 10s)
    function onFocusIn(e: FocusEvent) {
      const target = e.target;
      if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement ||
            target instanceof HTMLSelectElement)) return;

      const selector = target.id ? `#${target.id}` : target.name || '';
      const now = Date.now();

      // Check slow dwell on previous field
      if (lastFocusSelector && (now - focusStartTime) > 30_000) {
        recordSignal('slow_dwell');
      }

      lastFocusSelector = selector;
      focusStartTime = now;

      // Track focus change frequency
      focusChangeTimestamps.push(now);
      focusChangeTimestamps = focusChangeTimestamps.filter(t => now - t < 10_000);

      if (focusChangeTimestamps.length >= 5) {
        // Check if there was any input during these changes
        // If not, it's likely field cycling
        recordSignal('field_cycling');
        focusChangeTimestamps = []; // reset
      }
    }

    // Input clear detection (repeated correction): 3+ clear-retype cycles
    function onInput(e: Event) {
      const target = e.target;
      if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return;

      const selector = target.id ? `#${target.id}` : target.name || '';
      if (!selector) return;

      const now = Date.now();

      // Detect when field is cleared (length goes to 0)
      if (target.value.length === 0) {
        const timestamps = lastInputTimestamps.get(selector) ?? [];
        timestamps.push(now);
        const recent = timestamps.filter(t => now - t < 30_000);
        lastInputTimestamps.set(selector, recent);

        if (recent.length >= 3) {
          recordSignal('repeated_correction');
          lastInputTimestamps.set(selector, []);
        }
      }
    }

    document.addEventListener('click', onDocClick, { passive: true });
    document.documentElement.addEventListener('mouseleave', onMouseLeave, { passive: true });
    document.addEventListener('focusin', onFocusIn, { passive: true });
    document.addEventListener('input', onInput, { passive: true });

    cleanupFns.push(
      () => document.removeEventListener('click', onDocClick),
      () => document.documentElement.removeEventListener('mouseleave', onMouseLeave),
      () => document.removeEventListener('focusin', onFocusIn),
      () => document.removeEventListener('input', onInput),
    );
  }

  setupDetectors();

  // ── Public API ──

  return {
    recordSignal,
    getScore,
    reset,

    /** Record a validation error. Tracks repeated errors on the same field. */
    recordValidationError(selector: string) {
      if (erroredFields.has(selector)) {
        recordSignal('same_field_error');
      } else {
        erroredFields.add(selector);
        recordSignal('validation_error');
      }
    },

    /** Call when user dismisses struggling prompt. */
    onDismiss,

    destroy() {
      for (const fn of cleanupFns) fn();
      cleanupFns.length = 0;
      entries.length = 0;
    },
  };
}
