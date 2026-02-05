// ── LucidSupport Widget SDK — UI Renderer ──
// Renders FAB, toasts, panel, highlights. All DOM via template literals + direct API.
// No innerHTML (XSS-safe). No external resources.

import type { HelpResponse, ConditionRule, SupportPageModel } from './types';
import { STYLES } from './styles';

// ── SVG Icons (inline, no external resources) ──

const ICON_HELP = `<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 12.9 13 13.5 13 15h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z"/></svg>`;
const ICON_CLOSE = `<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>`;
const ICON_SEND = `<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>`;

export interface UIElements {
  root: HTMLElement;
  fab: HTMLButtonElement;
  toastContainer: HTMLElement;
  panel: HTMLElement;
  panelBody: HTMLElement;
  responseArea: HTMLElement;
  suggestionsArea: HTMLElement;
  topicsArea: HTMLElement;
  input: HTMLInputElement;
  sendBtn: HTMLButtonElement;
}

export interface UICallbacks {
  onFabClick: () => void;
  onToastClick: (rule: ConditionRule) => void;
  onPanelClose: () => void;
  onSendMessage: (text: string) => void;
  onChipClick: (text: string) => void;
  onTopicClick: (topicId: string) => void;
  onAcceptGuide: () => void;
  onDismissStruggling: () => void;
  onExitGuide: () => void;
}

/** Create the widget DOM structure inside the shadow root. Returns control handles. */
export function createUI(
  shadowRoot: ShadowRoot,
  position: 'bottom-right' | 'bottom-left',
  theme: 'light' | 'dark',
  callbacks: UICallbacks
): UIElements {
  // Inject styles
  const style = document.createElement('style');
  style.textContent = STYLES;
  shadowRoot.appendChild(style);

  // Root container
  const root = document.createElement('div');
  root.className = 'ls-root';
  root.setAttribute('data-theme', theme);
  root.setAttribute('data-position', position);

  // FAB
  const fab = document.createElement('button');
  fab.className = 'ls-fab';
  fab.setAttribute('aria-label', 'Open help');
  fab.innerHTML = ICON_HELP;
  fab.addEventListener('click', callbacks.onFabClick);

  // Toast container
  const toastContainer = document.createElement('div');
  toastContainer.className = 'ls-toast-container';
  toastContainer.setAttribute('role', 'status');
  toastContainer.setAttribute('aria-live', 'polite');

  // Panel
  const panel = document.createElement('div');
  panel.className = 'ls-panel';
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-label', 'Help panel');

  // Panel header
  const panelHeader = document.createElement('div');
  panelHeader.className = 'ls-panel-header';

  const panelTitle = document.createElement('span');
  panelTitle.className = 'ls-panel-title';
  panelTitle.textContent = 'Help';

  const panelClose = document.createElement('button');
  panelClose.className = 'ls-panel-close';
  panelClose.setAttribute('aria-label', 'Close help panel');
  panelClose.innerHTML = ICON_CLOSE;
  panelClose.addEventListener('click', callbacks.onPanelClose);

  panelHeader.appendChild(panelTitle);
  panelHeader.appendChild(panelClose);

  // Panel body
  const panelBody = document.createElement('div');
  panelBody.className = 'ls-panel-body';

  const responseArea = document.createElement('div');
  responseArea.className = 'ls-response';

  const suggestionsArea = document.createElement('div');
  suggestionsArea.className = 'ls-suggestions';

  const topicsArea = document.createElement('div');
  topicsArea.className = 'ls-topics';

  panelBody.appendChild(responseArea);
  panelBody.appendChild(suggestionsArea);
  panelBody.appendChild(topicsArea);

  // Panel input
  const panelInput = document.createElement('div');
  panelInput.className = 'ls-panel-input';

  const input = document.createElement('input');
  input.type = 'text';
  input.className = 'ls-input';
  input.placeholder = 'Ask a question...';
  input.setAttribute('aria-label', 'Ask a help question');

  const sendBtn = document.createElement('button');
  sendBtn.className = 'ls-send';
  sendBtn.setAttribute('aria-label', 'Send');
  sendBtn.innerHTML = ICON_SEND;

  function handleSend() {
    const text = input.value.trim();
    if (text) {
      callbacks.onSendMessage(text);
      input.value = '';
    }
  }

  sendBtn.addEventListener('click', handleSend);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  });

  panelInput.appendChild(input);
  panelInput.appendChild(sendBtn);

  // Assemble panel
  panel.appendChild(panelHeader);
  panel.appendChild(panelBody);
  panel.appendChild(panelInput);

  // Assemble root
  root.appendChild(fab);
  root.appendChild(toastContainer);
  root.appendChild(panel);
  shadowRoot.appendChild(root);

  return { root, fab, toastContainer, panel, panelBody, responseArea, suggestionsArea, topicsArea, input, sendBtn };
}

// ── Toast Management ──

const activeToasts = new Map<HTMLElement, ReturnType<typeof setTimeout>>();

export function showToast(
  container: HTMLElement,
  rule: ConditionRule,
  onToastClick: (rule: ConditionRule) => void
): HTMLElement {
  const toast = document.createElement('div');
  toast.className = 'ls-toast';
  toast.setAttribute('role', 'status');

  const text = document.createElement('span');
  text.className = 'ls-toast-text';
  text.textContent = rule.suggest;

  const dismiss = document.createElement('button');
  dismiss.className = 'ls-toast-dismiss';
  dismiss.setAttribute('aria-label', 'Dismiss');
  dismiss.textContent = '\u00d7';

  toast.appendChild(text);
  toast.appendChild(dismiss);

  // Click toast body → open panel with this context
  text.addEventListener('click', () => {
    removeToast(toast);
    onToastClick(rule);
  });

  // Dismiss button
  dismiss.addEventListener('click', (e) => {
    e.stopPropagation();
    removeToast(toast);
  });

  container.appendChild(toast);

  // Animate in
  requestAnimationFrame(() => {
    requestAnimationFrame(() => toast.classList.add('ls-toast-show'));
  });

  // Auto-dismiss after 10s
  const timer = setTimeout(() => removeToast(toast), 10_000);
  activeToasts.set(toast, timer);

  return toast;
}

function removeToast(toast: HTMLElement) {
  const timer = activeToasts.get(toast);
  if (timer) clearTimeout(timer);
  activeToasts.delete(toast);

  toast.classList.remove('ls-toast-show');
  toast.addEventListener('transitionend', () => toast.remove(), { once: true });
  // Fallback removal if transition doesn't fire
  setTimeout(() => toast.remove(), 400);
}

export function clearAllToasts(_container: HTMLElement) {
  for (const [toast, timer] of activeToasts) {
    clearTimeout(timer);
    toast.remove();
  }
  activeToasts.clear();
}

// ── Panel State ──

export function openPanel(panel: HTMLElement, fab: HTMLElement) {
  panel.classList.add('ls-panel-open');
  fab.classList.add('ls-fab-hidden');
}

export function closePanel(panel: HTMLElement, fab: HTMLElement) {
  panel.classList.remove('ls-panel-open');
  fab.classList.remove('ls-fab-hidden');
}

// ── Response Rendering ──

export function showResponse(elements: UIElements, response: HelpResponse, callbacks: UICallbacks) {
  // Response text
  elements.responseArea.textContent = response.text;

  // Suggestions
  elements.suggestionsArea.textContent = '';
  for (const suggestion of response.suggestions) {
    const chip = document.createElement('button');
    chip.className = 'ls-chip';
    chip.textContent = suggestion;
    chip.addEventListener('click', () => callbacks.onChipClick(suggestion));
    elements.suggestionsArea.appendChild(chip);
  }

  // Topics
  elements.topicsArea.textContent = '';
  for (const topic of response.topics) {
    const link = document.createElement('a');
    link.className = 'ls-topic-link';
    link.textContent = topic.label;
    link.href = '#';
    link.addEventListener('click', (e) => {
      e.preventDefault();
      callbacks.onTopicClick(topic.id);
    });
    elements.topicsArea.appendChild(link);
  }
}

export function showLoading(elements: UIElements) {
  elements.responseArea.textContent = '';
  const spinner = document.createElement('div');
  spinner.className = 'ls-spinner';
  for (let i = 0; i < 3; i++) {
    const dot = document.createElement('div');
    dot.className = 'ls-spinner-dot';
    spinner.appendChild(dot);
  }
  elements.responseArea.appendChild(spinner);
}

export function showError(elements: UIElements, message: string) {
  elements.responseArea.textContent = message;
}

export function showWelcome(elements: UIElements, title: string) {
  const ctx = document.createElement('div');
  ctx.className = 'ls-context';
  ctx.textContent = `I can help you with the ${title} page. Ask a question or click a suggestion below.`;
  elements.panelBody.insertBefore(ctx, elements.responseArea);
}

// ── Highlight Overlays ──

const activeHighlights = new Map<HTMLElement, string>();
let highlightRafId: number | null = null;

export function showHighlight(
  shadowRoot: ShadowRoot,
  selector: string,
  style: 'error' | 'info' | 'success'
) {
  const target = document.querySelector(selector);
  if (!target) return;

  const rect = target.getBoundingClientRect();
  const overlay = document.createElement('div');
  overlay.className = `ls-highlight ls-highlight-${style}`;
  overlay.style.position = 'fixed';
  overlay.style.top = `${rect.top - 3}px`;
  overlay.style.left = `${rect.left - 3}px`;
  overlay.style.width = `${rect.width + 6}px`;
  overlay.style.height = `${rect.height + 6}px`;
  overlay.style.pointerEvents = 'none';

  const root = shadowRoot.querySelector('.ls-root');
  if (root) root.appendChild(overlay);

  // Animate in
  requestAnimationFrame(() => overlay.classList.add('ls-highlight-active'));

  activeHighlights.set(overlay, selector);

  // Start position tracking if first highlight
  if (activeHighlights.size === 1) {
    startHighlightTracking();
  }

  // Auto-remove after 5s
  setTimeout(() => {
    overlay.classList.remove('ls-highlight-active');
    overlay.classList.add('ls-highlight-fade');
    overlay.addEventListener('transitionend', () => {
      overlay.remove();
      activeHighlights.delete(overlay);
      if (activeHighlights.size === 0) stopHighlightTracking();
    }, { once: true });
    // Fallback
    setTimeout(() => {
      overlay.remove();
      activeHighlights.delete(overlay);
    }, 400);
  }, 5_000);
}

export function clearHighlights() {
  for (const [overlay] of activeHighlights) {
    overlay.remove();
  }
  activeHighlights.clear();
  stopHighlightTracking();
}

function startHighlightTracking() {
  function update() {
    for (const [overlay, selector] of activeHighlights) {
      const target = document.querySelector(selector);
      if (!target) {
        overlay.remove();
        activeHighlights.delete(overlay);
        continue;
      }
      const rect = target.getBoundingClientRect();
      overlay.style.top = `${rect.top - 3}px`;
      overlay.style.left = `${rect.left - 3}px`;
      overlay.style.width = `${rect.width + 6}px`;
      overlay.style.height = `${rect.height + 6}px`;
    }
    if (activeHighlights.size > 0) {
      highlightRafId = requestAnimationFrame(update);
    }
  }
  highlightRafId = requestAnimationFrame(update);
}

function stopHighlightTracking() {
  if (highlightRafId !== null) {
    cancelAnimationFrame(highlightRafId);
    highlightRafId = null;
  }
}

// ── Struggling Mode Toast ──

export function showStrugglingToast(
  container: HTMLElement,
  callbacks: UICallbacks
): HTMLElement {
  const toast = document.createElement('div');
  toast.className = 'ls-toast ls-toast-struggling';
  toast.setAttribute('role', 'status');

  const text = document.createElement('div');
  text.className = 'ls-toast-text';
  text.textContent = 'Need help with this form?';

  const actions = document.createElement('div');
  actions.className = 'ls-struggling-actions';

  const guideBtn = document.createElement('button');
  guideBtn.className = 'ls-struggling-guide';
  guideBtn.textContent = 'Guide me through it';
  guideBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    removeToastElement(toast);
    callbacks.onAcceptGuide();
  });

  const dismissBtn = document.createElement('button');
  dismissBtn.className = 'ls-toast-dismiss';
  dismissBtn.setAttribute('aria-label', 'Dismiss');
  dismissBtn.textContent = '\u00d7';
  dismissBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    removeToastElement(toast);
    callbacks.onDismissStruggling();
  });

  actions.appendChild(guideBtn);
  actions.appendChild(dismissBtn);

  toast.appendChild(text);
  toast.appendChild(actions);

  container.appendChild(toast);

  // Animate in
  requestAnimationFrame(() => {
    requestAnimationFrame(() => toast.classList.add('ls-toast-show'));
  });

  // Auto-dismiss after 15s (longer than normal toast)
  const timer = setTimeout(() => {
    removeToastElement(toast);
    callbacks.onDismissStruggling();
  }, 15_000);
  activeToasts.set(toast, timer);

  return toast;
}

function removeToastElement(toast: HTMLElement) {
  const timer = activeToasts.get(toast);
  if (timer) clearTimeout(timer);
  activeToasts.delete(toast);

  toast.classList.remove('ls-toast-show');
  toast.addEventListener('transitionend', () => toast.remove(), { once: true });
  setTimeout(() => toast.remove(), 400);
}

// ── Active/Guide Mode ──

// Store exit callback for guide mode
let _guideExitCallback: (() => void) | null = null;

export function showActiveGuide(
  ui: UIElements,
  model: SupportPageModel,
  activeFieldIndex: number,
  completedFields: Set<string>,
  onExit?: () => void
) {
  if (onExit) _guideExitCallback = onExit;

  // Clear existing panel body content
  ui.panelBody.textContent = '';

  // Progress bar
  const progress = document.createElement('div');
  progress.className = 'ls-guide-progress';

  const progressBar = document.createElement('div');
  progressBar.className = 'ls-progress-bar';
  const pct = model.fields.length > 0
    ? Math.round((completedFields.size / model.fields.length) * 100)
    : 0;
  progressBar.style.width = `${pct}%`;

  const progressText = document.createElement('span');
  progressText.className = 'ls-progress-text';
  progressText.textContent = `${completedFields.size} of ${model.fields.length} fields complete`;

  progress.appendChild(progressBar);
  progress.appendChild(progressText);
  ui.panelBody.appendChild(progress);

  // Current field guide
  if (activeFieldIndex >= 0 && activeFieldIndex < model.fields.length) {
    const field = model.fields[activeFieldIndex];

    const current = document.createElement('div');
    current.className = 'ls-guide-current';
    current.setAttribute('aria-live', 'polite');

    const label = document.createElement('div');
    label.className = 'ls-guide-field-label';
    label.textContent = field.label;

    current.appendChild(label);

    if (field.help) {
      const help = document.createElement('div');
      help.className = 'ls-guide-help';
      help.textContent = field.help;
      current.appendChild(help);
    }

    if (field.pattern) {
      const format = document.createElement('div');
      format.className = 'ls-guide-format';
      format.textContent = `Format: ${field.pattern}`;
      current.appendChild(format);
    }

    ui.panelBody.appendChild(current);

    // Next field preview
    if (activeFieldIndex + 1 < model.fields.length) {
      const nextField = model.fields[activeFieldIndex + 1];
      const next = document.createElement('div');
      next.className = 'ls-guide-next';
      next.textContent = `Next: ${nextField.label}`;
      ui.panelBody.appendChild(next);
    }
  }

  // Re-add response and suggestions areas
  ui.panelBody.appendChild(ui.responseArea);
  ui.panelBody.appendChild(ui.suggestionsArea);
  ui.panelBody.appendChild(ui.topicsArea);

  // Exit guide button
  const exitBtn = document.createElement('button');
  exitBtn.className = 'ls-guide-exit';
  exitBtn.textContent = 'Exit guided mode';
  exitBtn.addEventListener('click', () => {
    _guideExitCallback?.();
  });
  ui.panelBody.appendChild(exitBtn);
}

export function updateActiveGuide(
  ui: UIElements,
  model: SupportPageModel,
  activeFieldIndex: number,
  completedFields: Set<string>
) {
  // Update progress bar
  const progressBar = ui.panelBody.querySelector('.ls-progress-bar') as HTMLElement | null;
  const progressText = ui.panelBody.querySelector('.ls-progress-text');

  if (progressBar && progressText) {
    const pct = model.fields.length > 0
      ? Math.round((completedFields.size / model.fields.length) * 100)
      : 0;
    progressBar.style.width = `${pct}%`;
    progressText.textContent = `${completedFields.size} of ${model.fields.length} fields complete`;
  }

  // Update current field
  const currentEl = ui.panelBody.querySelector('.ls-guide-current');
  if (currentEl && activeFieldIndex >= 0 && activeFieldIndex < model.fields.length) {
    const field = model.fields[activeFieldIndex];

    const label = currentEl.querySelector('.ls-guide-field-label');
    if (label) label.textContent = field.label;

    const help = currentEl.querySelector('.ls-guide-help');
    if (help) help.textContent = field.help ?? '';

    const format = currentEl.querySelector('.ls-guide-format');
    if (format) format.textContent = field.pattern ? `Format: ${field.pattern}` : '';
  }

  // Update next field preview
  const nextEl = ui.panelBody.querySelector('.ls-guide-next');
  if (nextEl) {
    if (activeFieldIndex + 1 < model.fields.length) {
      nextEl.textContent = `Next: ${model.fields[activeFieldIndex + 1].label}`;
    } else {
      nextEl.textContent = 'All fields covered!';
    }
  }
}

// ── Theme Detection ──

export function detectTheme(): 'light' | 'dark' {
  // Check prefers-color-scheme
  if (window.matchMedia('(prefers-color-scheme: dark)').matches) return 'dark';

  // Check host page body background luminance
  const bg = getComputedStyle(document.body).backgroundColor;
  const rgb = parseRgb(bg);
  if (rgb && luminance(rgb) < 0.5) return 'dark';

  return 'light';
}

function parseRgb(color: string): [number, number, number] | null {
  const match = color.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/);
  if (!match) return null;
  return [parseInt(match[1]), parseInt(match[2]), parseInt(match[3])];
}

function luminance([r, g, b]: [number, number, number]): number {
  // Relative luminance (ITU-R BT.709)
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}

// ── Field Help Popup (Persistent Until Cleared) ──

let activeFieldHelp: HTMLElement | null = null;
let fieldHelpCloseCallback: (() => void) | null = null;

export interface FieldHelpOptions {
  label: string;
  help: string | null;
  pattern: string | null;
  questions?: string[];
  onQuestionClick?: (question: string) => void;
  onClose?: () => void;
}

export function showFieldHelp(
  shadowRoot: ShadowRoot,
  selector: string,
  options: FieldHelpOptions
) {
  // Remove any existing field help
  hideFieldHelp();

  const target = document.querySelector(selector);
  if (!target) return;

  const root = shadowRoot.querySelector('.ls-root');
  if (!root) return;

  const rect = target.getBoundingClientRect();

  // Create field help popup
  const popup = document.createElement('div');
  popup.className = 'ls-field-help';

  // Position below the field (or above if not enough space)
  const spaceBelow = window.innerHeight - rect.bottom;
  const putAbove = spaceBelow < 180 && rect.top > 180;

  if (putAbove) {
    popup.style.bottom = `${window.innerHeight - rect.top + 8}px`;
  } else {
    popup.style.top = `${rect.bottom + 8}px`;
  }
  popup.style.left = `${Math.max(12, Math.min(rect.left, window.innerWidth - 340))}px`;

  // Header
  const header = document.createElement('div');
  header.className = 'ls-field-help-header';

  const label = document.createElement('span');
  label.className = 'ls-field-help-label';
  label.textContent = options.label;

  const closeBtn = document.createElement('button');
  closeBtn.className = 'ls-field-help-close';
  closeBtn.textContent = '\u00d7';
  closeBtn.setAttribute('aria-label', 'Close help');
  closeBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    hideFieldHelp();
  });

  header.appendChild(label);
  header.appendChild(closeBtn);
  popup.appendChild(header);

  // Help text
  if (options.help) {
    const helpText = document.createElement('div');
    helpText.className = 'ls-field-help-text';
    helpText.textContent = options.help;
    popup.appendChild(helpText);
  }

  // Pattern/format hint
  if (options.pattern) {
    const format = document.createElement('div');
    format.className = 'ls-field-help-format';
    format.textContent = `Format: ${options.pattern}`;
    popup.appendChild(format);
  }

  // Related questions
  if (options.questions && options.questions.length > 0) {
    const questionsDiv = document.createElement('div');
    questionsDiv.className = 'ls-field-help-questions';

    for (const q of options.questions) {
      const qBtn = document.createElement('button');
      qBtn.className = 'ls-field-help-question';
      qBtn.textContent = q;
      qBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        options.onQuestionClick?.(q);
      });
      questionsDiv.appendChild(qBtn);
    }
    popup.appendChild(questionsDiv);
  }

  root.appendChild(popup);
  activeFieldHelp = popup;
  fieldHelpCloseCallback = options.onClose || null;

  // Animate in
  requestAnimationFrame(() => {
    requestAnimationFrame(() => popup.classList.add('ls-field-help-visible'));
  });
}

export function hideFieldHelp() {
  if (activeFieldHelp) {
    activeFieldHelp.classList.remove('ls-field-help-visible');
    const popup = activeFieldHelp;
    activeFieldHelp = null;

    // Call close callback
    fieldHelpCloseCallback?.();
    fieldHelpCloseCallback = null;

    // Remove after transition
    popup.addEventListener('transitionend', () => popup.remove(), { once: true });
    setTimeout(() => popup.remove(), 300);
  }
}

export function isFieldHelpVisible(): boolean {
  return activeFieldHelp !== null;
}

// ── Success Tick Animation (SweetAlert Style) ──

export function showSuccessTick(
  shadowRoot: ShadowRoot,
  selector: string,
  duration = 1200
): Promise<void> {
  return new Promise((resolve) => {
    const target = document.querySelector(selector);
    if (!target) {
      resolve();
      return;
    }

    const root = shadowRoot.querySelector('.ls-root');
    if (!root) {
      resolve();
      return;
    }

    const rect = target.getBoundingClientRect();

    // Create tick container
    const tick = document.createElement('div');
    tick.className = 'ls-success-tick';
    tick.style.top = `${rect.top + rect.height / 2 - 30}px`;
    tick.style.left = `${rect.left + rect.width / 2 - 30}px`;

    // Circle background
    const circle = document.createElement('div');
    circle.className = 'ls-success-tick-circle';

    // Checkmark SVG
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');

    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', 'M5 13l4 4L19 7');
    path.classList.add('ls-success-tick-path');

    svg.appendChild(path);
    circle.appendChild(svg);
    tick.appendChild(circle);
    root.appendChild(tick);

    // Trigger animation
    requestAnimationFrame(() => {
      tick.classList.add('ls-success-tick-visible');

      // Start fade after delay
      setTimeout(() => {
        tick.classList.add('ls-success-tick-fade');
      }, duration - 400);

      // Remove after full animation
      setTimeout(() => {
        tick.remove();
        resolve();
      }, duration);
    });
  });
}

// ── Cached Response Indicator ──

export function showCachedIndicator(elements: UIElements) {
  // Check if already shown
  if (elements.responseArea.querySelector('.ls-response-cached')) return;

  const indicator = document.createElement('div');
  indicator.className = 'ls-response-cached';
  indicator.innerHTML = `
    <svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
      <path d="M13 3a9 9 0 0 0-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0 0 13 21a9 9 0 0 0 0-18zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z"/>
    </svg>
    <span>Instant answer</span>
  `;
  elements.responseArea.appendChild(indicator);
}
