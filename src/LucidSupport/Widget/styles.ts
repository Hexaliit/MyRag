// ── LucidSupport Widget SDK — Styles ──
// CSS as a string constant, injected into Shadow DOM via style.textContent (CSP-safe).

export const STYLES = /* css */ `
/* ── Reset & Host ── */
:host {
  all: initial;
  position: fixed;
  z-index: 2147483647;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
  font-size: 14px;
  line-height: 1.5;
  color-scheme: light dark;
}

*, *::before, *::after {
  box-sizing: border-box;
}

/* ── Theme Variables ── */
.ls-root[data-theme="light"] {
  --ls-bg: #ffffff;
  --ls-bg-secondary: #f8f9fa;
  --ls-text: #1a1a2e;
  --ls-text-secondary: #6b7280;
  --ls-border: #e5e7eb;
  --ls-primary: #3b82f6;
  --ls-primary-hover: #2563eb;
  --ls-primary-text: #ffffff;
  --ls-error: #ef4444;
  --ls-success: #22c55e;
  --ls-shadow: 0 4px 24px rgba(0, 0, 0, 0.12);
  --ls-shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.08);
  --ls-toast-bg: #1a1a2e;
  --ls-toast-text: #f8f9fa;
}

.ls-root[data-theme="dark"] {
  --ls-bg: #1e1e2e;
  --ls-bg-secondary: #2a2a3e;
  --ls-text: #e5e7eb;
  --ls-text-secondary: #9ca3af;
  --ls-border: #374151;
  --ls-primary: #60a5fa;
  --ls-primary-hover: #93bbfd;
  --ls-primary-text: #1a1a2e;
  --ls-error: #f87171;
  --ls-success: #4ade80;
  --ls-shadow: 0 4px 24px rgba(0, 0, 0, 0.4);
  --ls-shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.3);
  --ls-toast-bg: #f8f9fa;
  --ls-toast-text: #1a1a2e;
}

/* ── FAB (Floating Action Button) ── */
.ls-fab {
  position: fixed;
  width: 48px;
  height: 48px;
  border-radius: 50%;
  border: none;
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: var(--ls-shadow);
  transition: transform 0.2s cubic-bezier(0.4, 0, 0.2, 1),
              box-shadow 0.2s cubic-bezier(0.4, 0, 0.2, 1);
  z-index: 1;
}

.ls-fab:hover {
  transform: scale(1.08);
  box-shadow: var(--ls-shadow), 0 0 0 4px rgba(59, 130, 246, 0.2);
}

.ls-fab:active {
  transform: scale(0.95);
}

.ls-fab svg {
  width: 22px;
  height: 22px;
  fill: currentColor;
}

.ls-fab-hidden {
  transform: scale(0);
  pointer-events: none;
}

/* Position variants */
.ls-root[data-position="bottom-right"] .ls-fab {
  bottom: 20px;
  right: 20px;
}

.ls-root[data-position="bottom-left"] .ls-fab {
  bottom: 20px;
  left: 20px;
}

/* ── Toast ── */
.ls-toast-container {
  position: fixed;
  display: flex;
  flex-direction: column;
  gap: 8px;
  z-index: 2;
  max-width: 360px;
}

.ls-root[data-position="bottom-right"] .ls-toast-container {
  bottom: 80px;
  right: 20px;
}

.ls-root[data-position="bottom-left"] .ls-toast-container {
  bottom: 80px;
  left: 20px;
}

.ls-toast {
  background: var(--ls-toast-bg);
  color: var(--ls-toast-text);
  padding: 12px 16px;
  border-radius: 12px;
  box-shadow: var(--ls-shadow);
  transform: translateX(120%);
  transition: transform 0.25s cubic-bezier(0.4, 0, 0.2, 1),
              opacity 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  opacity: 0;
  cursor: pointer;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  max-width: 100%;
}

.ls-root[data-position="bottom-left"] .ls-toast {
  transform: translateX(-120%);
}

.ls-toast-show {
  transform: translateX(0);
  opacity: 1;
}

.ls-toast-text {
  flex: 1;
  font-size: 13px;
  line-height: 1.4;
}

.ls-toast-dismiss {
  flex-shrink: 0;
  background: none;
  border: none;
  color: inherit;
  opacity: 0.6;
  cursor: pointer;
  padding: 0;
  font-size: 16px;
  line-height: 1;
}

.ls-toast-dismiss:hover {
  opacity: 1;
}

/* ── Panel ── */
.ls-panel {
  position: fixed;
  background: var(--ls-bg);
  box-shadow: var(--ls-shadow);
  display: flex;
  flex-direction: column;
  transform: translateX(100%);
  transition: transform 0.3s cubic-bezier(0.4, 0, 0.2, 1);
  z-index: 3;
  overflow: hidden;
}

.ls-root[data-position="bottom-right"] .ls-panel {
  top: 0;
  right: 0;
  width: 380px;
  max-width: 100vw;
  height: 100vh;
  height: 100dvh;
  border-left: 1px solid var(--ls-border);
}

.ls-root[data-position="bottom-left"] .ls-panel {
  top: 0;
  left: 0;
  width: 380px;
  max-width: 100vw;
  height: 100vh;
  height: 100dvh;
  border-right: 1px solid var(--ls-border);
  transform: translateX(-100%);
}

.ls-panel-open {
  transform: translateX(0);
}

.ls-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  border-bottom: 1px solid var(--ls-border);
  background: var(--ls-bg);
  flex-shrink: 0;
}

.ls-panel-title {
  font-weight: 600;
  font-size: 15px;
  color: var(--ls-text);
}

.ls-panel-close {
  background: none;
  border: none;
  cursor: pointer;
  color: var(--ls-text-secondary);
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
}

.ls-panel-close:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

.ls-panel-close svg {
  width: 18px;
  height: 18px;
  fill: currentColor;
}

.ls-panel-body {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.ls-panel-body::-webkit-scrollbar {
  width: 4px;
}

.ls-panel-body::-webkit-scrollbar-thumb {
  background: var(--ls-border);
  border-radius: 2px;
}

/* ── Response Bubble ── */
.ls-response {
  background: var(--ls-bg-secondary);
  border-radius: 12px;
  padding: 12px 14px;
  font-size: 13px;
  color: var(--ls-text);
  line-height: 1.5;
}

.ls-response:empty {
  display: none;
}

/* ── Welcome/Context ── */
.ls-context {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.4;
}

/* ── Suggestions ── */
.ls-suggestions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.ls-suggestions:empty {
  display: none;
}

.ls-chip {
  background: var(--ls-bg-secondary);
  border: 1px solid var(--ls-border);
  border-radius: 16px;
  padding: 5px 12px;
  font-size: 12px;
  color: var(--ls-primary);
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
  white-space: nowrap;
}

.ls-chip:hover {
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  border-color: var(--ls-primary);
}

/* ── Topic Links ── */
.ls-topics {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ls-topics:empty {
  display: none;
}

.ls-topic-link {
  font-size: 13px;
  color: var(--ls-primary);
  text-decoration: none;
  padding: 4px 0;
  cursor: pointer;
}

.ls-topic-link:hover {
  text-decoration: underline;
}

/* ── Input Area ── */
.ls-panel-input {
  display: flex;
  gap: 8px;
  padding: 12px 16px;
  border-top: 1px solid var(--ls-border);
  background: var(--ls-bg);
  flex-shrink: 0;
}

.ls-input {
  flex: 1;
  border: 1px solid var(--ls-border);
  border-radius: 20px;
  padding: 8px 14px;
  font-size: 13px;
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
  outline: none;
  transition: border-color 0.15s;
}

.ls-input::placeholder {
  color: var(--ls-text-secondary);
}

.ls-input:focus {
  border-color: var(--ls-primary);
}

.ls-send {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  border: none;
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
  flex-shrink: 0;
}

.ls-send:hover {
  background: var(--ls-primary-hover);
}

.ls-send:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.ls-send svg {
  width: 16px;
  height: 16px;
  fill: currentColor;
}

/* ── Loading Spinner ── */
.ls-spinner {
  display: inline-flex;
  gap: 4px;
  padding: 8px 0;
}

.ls-spinner-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ls-primary);
  animation: ls-bounce 1.2s infinite ease-in-out;
}

.ls-spinner-dot:nth-child(2) { animation-delay: 0.16s; }
.ls-spinner-dot:nth-child(3) { animation-delay: 0.32s; }

@keyframes ls-bounce {
  0%, 80%, 100% { transform: scale(0.6); opacity: 0.4; }
  40% { transform: scale(1); opacity: 1; }
}

/* ── Highlights (overlay on host page elements) ── */
.ls-highlight {
  position: fixed;
  border-radius: 4px;
  pointer-events: none;
  transition: opacity 0.3s, box-shadow 0.3s;
  opacity: 0;
  z-index: 2147483646;
}

.ls-highlight-active {
  opacity: 1;
}

.ls-highlight-error {
  box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.5), 0 0 12px rgba(239, 68, 68, 0.2);
}

.ls-highlight-info {
  box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.5), 0 0 12px rgba(59, 130, 246, 0.2);
}

.ls-highlight-success {
  box-shadow: 0 0 0 3px rgba(34, 197, 94, 0.5), 0 0 12px rgba(34, 197, 94, 0.2);
}

.ls-highlight-fade {
  opacity: 0;
}

/* Pulsing animation for active highlights */
.ls-highlight-active.ls-highlight-error {
  animation: ls-pulse-error 2s ease-in-out infinite;
}

.ls-highlight-active.ls-highlight-info {
  animation: ls-pulse-info 2s ease-in-out infinite;
}

@keyframes ls-pulse-error {
  0%, 100% { box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.5), 0 0 12px rgba(239, 68, 68, 0.2); }
  50% { box-shadow: 0 0 0 5px rgba(239, 68, 68, 0.3), 0 0 20px rgba(239, 68, 68, 0.15); }
}

@keyframes ls-pulse-info {
  0%, 100% { box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.5), 0 0 12px rgba(59, 130, 246, 0.2); }
  50% { box-shadow: 0 0 0 5px rgba(59, 130, 246, 0.3), 0 0 20px rgba(59, 130, 246, 0.15); }
}

/* ── Mobile ── */
@media (max-width: 767px) {
  .ls-panel {
    top: auto;
    bottom: 0;
    left: 0;
    right: 0;
    width: 100%;
    max-height: 70vh;
    max-height: 70dvh;
    border-radius: 16px 16px 0 0;
    border-left: none;
    border-right: none;
    border-top: 1px solid var(--ls-border);
    transform: translateY(100%);
  }

  .ls-root[data-position="bottom-left"] .ls-panel {
    transform: translateY(100%);
  }

  .ls-panel-open {
    transform: translateY(0);
  }

  .ls-toast-container {
    left: 12px;
    right: 12px;
    max-width: none;
  }

  .ls-toast {
    transform: translateY(120%);
  }

  .ls-toast-show {
    transform: translateY(0);
  }
}

/* ── Struggling Mode Toast ── */
.ls-toast-struggling {
  flex-direction: column;
  gap: 8px;
}

.ls-struggling-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.ls-struggling-guide {
  background: var(--ls-primary);
  color: var(--ls-primary-text);
  border: none;
  border-radius: 16px;
  padding: 6px 14px;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.15s;
  white-space: nowrap;
}

.ls-struggling-guide:hover {
  background: var(--ls-primary-hover);
}

/* Pulsing FAB when struggling */
.ls-fab-pulse {
  animation: ls-fab-pulse 2s ease-in-out infinite;
}

@keyframes ls-fab-pulse {
  0%, 100% { box-shadow: var(--ls-shadow); }
  50% { box-shadow: var(--ls-shadow), 0 0 0 8px rgba(59, 130, 246, 0.2); }
}

/* ── Active/Guide Mode ── */
.ls-guide-progress {
  padding: 8px 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ls-progress-bar {
  height: 4px;
  background: var(--ls-primary);
  border-radius: 2px;
  transition: width 0.3s ease-out;
}

.ls-guide-progress::before {
  content: '';
  display: block;
  height: 4px;
  background: var(--ls-border);
  border-radius: 2px;
  position: absolute;
  left: 0;
  right: 0;
}

.ls-guide-progress {
  position: relative;
}

.ls-progress-text {
  font-size: 11px;
  color: var(--ls-text-secondary);
  text-align: center;
}

.ls-guide-current {
  background: var(--ls-bg-secondary);
  border-radius: 8px;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  border-left: 3px solid var(--ls-primary);
}

.ls-guide-field-label {
  font-weight: 600;
  font-size: 14px;
  color: var(--ls-text);
}

.ls-guide-help {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.4;
}

.ls-guide-format {
  font-size: 12px;
  color: var(--ls-primary);
  font-family: 'SF Mono', 'Fira Code', 'Consolas', monospace;
}

.ls-guide-next {
  font-size: 12px;
  color: var(--ls-text-secondary);
  padding: 4px 0;
  font-style: italic;
}

.ls-guide-exit {
  margin-top: auto;
  padding: 8px 16px;
  background: transparent;
  border: 1px solid var(--ls-border);
  border-radius: var(--ls-radius, 6px);
  color: var(--ls-text-secondary);
  font-size: 12px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
  align-self: center;
}

.ls-guide-exit:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

/* ── Focus visible outlines ── */
.ls-fab:focus-visible,
.ls-panel-close:focus-visible,
.ls-chip:focus-visible,
.ls-send:focus-visible,
.ls-input:focus-visible {
  outline: 2px solid var(--ls-primary);
  outline-offset: 2px;
}

/* ── Reduced motion ── */
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}

/* ── Field Help Inline (persistent until cleared) ── */
.ls-field-help {
  position: fixed;
  background: var(--ls-bg);
  border: 1px solid var(--ls-border);
  border-radius: 12px;
  padding: 12px 16px;
  box-shadow: var(--ls-shadow);
  max-width: 320px;
  z-index: 2147483645;
  opacity: 0;
  transform: translateY(8px);
  transition: opacity 0.2s ease-out, transform 0.2s ease-out;
  pointer-events: none;
}

.ls-field-help-visible {
  opacity: 1;
  transform: translateY(0);
  pointer-events: auto;
}

.ls-field-help-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.ls-field-help-label {
  font-weight: 600;
  font-size: 13px;
  color: var(--ls-text);
}

.ls-field-help-close {
  background: none;
  border: none;
  cursor: pointer;
  color: var(--ls-text-secondary);
  padding: 2px;
  border-radius: 4px;
  display: flex;
  font-size: 14px;
  line-height: 1;
}

.ls-field-help-close:hover {
  background: var(--ls-bg-secondary);
  color: var(--ls-text);
}

.ls-field-help-text {
  font-size: 13px;
  color: var(--ls-text-secondary);
  line-height: 1.5;
  margin-bottom: 8px;
}

.ls-field-help-format {
  font-size: 11px;
  color: var(--ls-primary);
  font-family: 'SF Mono', 'Fira Code', 'Consolas', monospace;
  background: var(--ls-bg-secondary);
  padding: 4px 8px;
  border-radius: 4px;
}

.ls-field-help-questions {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--ls-border);
}

.ls-field-help-question {
  font-size: 12px;
  color: var(--ls-primary);
  cursor: pointer;
  padding: 4px 0;
  text-decoration: none;
  background: none;
  border: none;
  text-align: left;
}

.ls-field-help-question:hover {
  text-decoration: underline;
}

/* ── Success Tick Animation (SweetAlert style) ── */
.ls-success-tick {
  position: fixed;
  width: 60px;
  height: 60px;
  z-index: 2147483646;
  pointer-events: none;
  display: flex;
  align-items: center;
  justify-content: center;
  opacity: 0;
  transform: scale(0);
}

.ls-success-tick-visible {
  animation: ls-tick-pop 0.6s cubic-bezier(0.175, 0.885, 0.32, 1.275) forwards;
}

@keyframes ls-tick-pop {
  0% { opacity: 0; transform: scale(0); }
  50% { opacity: 1; transform: scale(1.2); }
  70% { transform: scale(0.9); }
  100% { opacity: 1; transform: scale(1); }
}

.ls-success-tick-circle {
  width: 56px;
  height: 56px;
  border-radius: 50%;
  background: var(--ls-success);
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: 0 4px 16px rgba(34, 197, 94, 0.4);
}

.ls-success-tick svg {
  width: 32px;
  height: 32px;
  stroke: white;
  stroke-width: 3;
  stroke-linecap: round;
  stroke-linejoin: round;
  fill: none;
}

.ls-success-tick-path {
  stroke-dasharray: 50;
  stroke-dashoffset: 50;
  animation: ls-tick-draw 0.4s ease-out 0.2s forwards;
}

@keyframes ls-tick-draw {
  to { stroke-dashoffset: 0; }
}

.ls-success-tick-fade {
  animation: ls-tick-fade 0.3s ease-out 0.8s forwards;
}

@keyframes ls-tick-fade {
  to { opacity: 0; transform: scale(0.8); }
}

/* ── Cached Response Indicator ── */
.ls-response-cached {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 10px;
  color: var(--ls-text-secondary);
  margin-top: 8px;
}

.ls-response-cached svg {
  width: 12px;
  height: 12px;
  fill: currentColor;
}
`;
