// ── LucidSupport Widget SDK — Core Widget (State Machine + Lifecycle) ──
// Orchestrates observer, UI, API, conditions, and frustration tracking.

import type {
  WidgetConfig, WidgetState, WidgetEvent, PageContext,
  HelpResponse, ConditionRule, SupportPageModel, FieldState, PageState,
  CachedTopicResponse
} from './types';
import { createApiClient } from './api';
import { createFieldObserver, type FieldObserver } from './observer';
import { evaluateConditions } from './conditions';
import { createFrustrationTracker, type FrustrationTracker } from './frustration';
import { createWorkflowEvaluator } from './workflow';
import {
  createUI, showToast, clearAllToasts, openPanel, closePanel,
  showResponse, showLoading, showError, showWelcome, showHighlight,
  clearHighlights, detectTheme, showStrugglingToast, showActiveGuide,
  updateActiveGuide, showFieldHelp, hideFieldHelp, isFieldHelpVisible,
  showSuccessTick, showCachedIndicator, type UICallbacks
} from './ui';

// ── State Machine ──

interface TransitionDef {
  from: WidgetState;
  event: WidgetEvent;
  to: WidgetState;
}

const TRANSITIONS: TransitionDef[] = [
  // Original transitions
  { from: 'idle',       event: 'condition_trigger',     to: 'toast' },
  { from: 'idle',       event: 'fab_click',             to: 'panel' },
  { from: 'idle',       event: 'field_idle',            to: 'toast' },
  { from: 'toast',      event: 'toast_click',           to: 'panel' },
  { from: 'toast',      event: 'toast_dismiss',         to: 'idle' },
  { from: 'toast',      event: 'toast_timeout',         to: 'idle' },
  { from: 'panel',      event: 'ask_question',          to: 'asking' },
  { from: 'panel',      event: 'panel_close',           to: 'idle' },
  { from: 'asking',     event: 'response',              to: 'showing' },
  { from: 'asking',     event: 'error',                 to: 'panel' },
  { from: 'showing',    event: 'panel_close',           to: 'idle' },
  { from: 'showing',    event: 'ask_question',          to: 'asking' },

  // Struggling mode transitions
  { from: 'idle',       event: 'frustration_threshold', to: 'struggling' },
  { from: 'struggling', event: 'accept_guide',          to: 'active' },
  { from: 'struggling', event: 'dismiss_struggling',    to: 'idle' },
  { from: 'struggling', event: 'fab_click',             to: 'panel' },

  // Active/guide mode transitions
  { from: 'active',     event: 'exit_guide',            to: 'idle' },
  { from: 'active',     event: 'ask_question',          to: 'asking' },

  // Allow opening panel from showing in active mode
  { from: 'showing',    event: 'exit_guide',            to: 'idle' },
];

function findTransition(current: WidgetState, event: WidgetEvent): WidgetState | null {
  const t = TRANSITIONS.find(t => t.from === current && t.event === event);
  return t ? t.to : null;
}

// ── Widget Core ──

export function initWidget(shadowRoot: ShadowRoot, config: WidgetConfig) {
  let state: WidgetState = 'idle';
  let pageModel: SupportPageModel | null = null;
  let observer: FieldObserver | null = null;
  let lastConditionTrigger: ConditionRule | null = null;
  let frustrationTracker: FrustrationTracker | null = null;
  let workflowEvaluator: ReturnType<typeof createWorkflowEvaluator> | null = null;

  // Page state tracking
  const pageState: PageState = {
    fieldStates: {},
    changedFields: new Set(),
    idleSeconds: 0,
    hasIncompleteRequired: false,
    submitAttempts: 0,
  };

  // Active mode tracking
  let activeFieldIndex = -1;
  let completedFieldSelectors = new Set<string>();

  // Idle tracking
  let idleTimer: ReturnType<typeof setInterval> | null = null;
  let lastActivity = Date.now();

  // Condition evaluation cooldown (don't spam toasts)
  const triggeredConditions = new Set<string>();

  // Topic response cache (pre-computed answers)
  const topicCache = new Map<string, CachedTopicResponse>();
  const CACHE_TTL_MS = 30 * 60 * 1000; // 30 minutes

  // Track currently focused field
  let focusedFieldSelector: string | null = null;
  let fieldHelpShown = false;

  // API client
  const api = createApiClient(config.api);

  // Resolve theme
  const theme = config.theme === 'auto' ? detectTheme() : config.theme;

  // UI callbacks
  const uiCallbacks: UICallbacks = {
    onFabClick: () => transition('fab_click'),
    onToastClick: (rule) => {
      lastConditionTrigger = rule;
      transition('toast_click');
    },
    onPanelClose: () => {
      if (state === 'active') {
        transition('exit_guide');
      } else {
        transition('panel_close');
      }
    },
    onSendMessage: (text) => handleQuestion(text),
    onChipClick: (text) => handleQuestion(text),
    onTopicClick: (topicId) => handleTopicClick(topicId),
    onAcceptGuide: () => transition('accept_guide'),
    onDismissStruggling: () => {
      frustrationTracker?.onDismiss();
      transition('dismiss_struggling');
    },
    onExitGuide: () => transition('exit_guide'),
  };

  // Create UI
  const ui = createUI(shadowRoot, config.position, theme, uiCallbacks);

  // ── State Transition Engine ──

  function transition(event: WidgetEvent) {
    const nextState = findTransition(state, event);
    if (nextState === null) return; // Invalid transition — ignore

    const prevState = state;
    state = nextState;
    onStateChange(prevState, nextState, event);
  }

  function onStateChange(from: WidgetState, to: WidgetState, event: WidgetEvent) {
    switch (to) {
      case 'idle':
        closePanel(ui.panel, ui.fab);
        clearAllToasts(ui.toastContainer);
        clearHighlights();
        completedFieldSelectors.clear();
        activeFieldIndex = -1;
        break;

      case 'toast':
        if (lastConditionTrigger) {
          showToast(ui.toastContainer, lastConditionTrigger, (rule) => {
            lastConditionTrigger = rule;
            transition('toast_click');
          });
        }
        break;

      case 'panel':
        clearAllToasts(ui.toastContainer);
        openPanel(ui.panel, ui.fab);
        if (pageModel) {
          showWelcome(ui, pageModel.title);
          renderTopics();
        }
        // If opened from a condition toast, show context
        if (from === 'toast' && lastConditionTrigger) {
          ui.responseArea.textContent = lastConditionTrigger.suggest;
          if (lastConditionTrigger.highlight) {
            showHighlight(shadowRoot, lastConditionTrigger.highlight, 'info');
          }
        }
        ui.input.focus();
        break;

      case 'asking':
        showLoading(ui);
        break;

      case 'showing':
        // Response is rendered by the async handler
        break;

      case 'struggling':
        // Show proactive struggling toast with "Need help?" + "Guide me"
        showStrugglingToast(ui.toastContainer, uiCallbacks);
        break;

      case 'active':
        // Enter guided mode
        clearAllToasts(ui.toastContainer);
        openPanel(ui.panel, ui.fab);
        completedFieldSelectors.clear();
        activeFieldIndex = 0;
        if (pageModel) {
          showActiveGuide(ui, pageModel, activeFieldIndex, completedFieldSelectors, () => transition('exit_guide'));
          // Highlight first field
          if (pageModel.fields.length > 0) {
            showHighlight(shadowRoot, pageModel.fields[0].selector, 'info');
          }
        }
        break;
    }

    // Analytics
    api.trackEvent('state_change', { from, to, event });
  }

  // ── Question Handling ──

  async function handleQuestion(text: string, useCacheOnly = false) {
    // Check cache first
    const cached = topicCache.get(text);
    if (cached && Date.now() - cached.cachedAt < CACHE_TTL_MS) {
      // Use cached response instantly
      renderResponse(cached.response, true);
      transition('ask_question');
      transition('response');
      return;
    }

    // If cache-only mode and no cache, skip
    if (useCacheOnly) return;

    transition('ask_question');

    const context = collectPageContext(text);

    try {
      const response = await api.askForHelp(context);

      // Cache the response for future use
      topicCache.set(text, {
        question: text,
        response,
        cachedAt: Date.now(),
      });

      renderResponse(response, false);
      transition('response');
    } catch (err) {
      showError(ui, 'Sorry, help is temporarily unavailable. Please try again.');
      transition('error');
    }
  }

  function handleTopicClick(topicId: string) {
    const topic = pageModel?.topics.find(t => t.articleId === topicId);
    if (topic) {
      handleQuestion(topic.question);
    }
  }

  // Pre-warm cache for all topics
  async function prewarmTopicCache() {
    if (!pageModel?.topics.length) return;

    for (const topic of pageModel.topics) {
      // Skip if already cached
      if (topicCache.has(topic.question)) continue;

      try {
        const context = collectPageContext(topic.question);
        const response = await api.askForHelp(context);
        topicCache.set(topic.question, {
          question: topic.question,
          response,
          cachedAt: Date.now(),
        });
      } catch {
        // Silently fail pre-warming
      }
    }
  }

  function renderResponse(response: HelpResponse, fromCache = false) {
    showResponse(ui, response, uiCallbacks);

    // Show cached indicator if from cache
    if (fromCache) {
      showCachedIndicator(ui);
    }

    // Show highlights
    for (const hl of response.highlights) {
      showHighlight(shadowRoot, hl.selector, hl.style);
    }
  }

  function renderTopics() {
    if (!pageModel?.topics.length) return;

    ui.suggestionsArea.textContent = '';
    for (const topic of pageModel.topics) {
      const chip = document.createElement('button');
      chip.className = 'ls-chip';
      chip.textContent = topic.question;
      chip.addEventListener('click', () => handleQuestion(topic.question));
      ui.suggestionsArea.appendChild(chip);
    }
  }

  // ── Page Context Collection ──

  function collectPageContext(question?: string): PageContext {
    const fieldStates = observer?.getFieldStates() ?? {};
    const visibleFieldIds = observer?.getVisibleFieldIds() ?? [];

    return {
      url: location.pathname,
      visibleFieldIds,
      fieldStates,
      viewportWidth: window.innerWidth,
      question,
    };
  }

  // ── Condition Evaluation ──

  function checkConditions() {
    if (!pageModel?.conditions.length) return;
    if (state !== 'idle') return; // Only trigger from idle state

    const matched = evaluateConditions(pageModel.conditions, pageState);
    for (const rule of matched) {
      if (triggeredConditions.has(rule.when)) continue;
      triggeredConditions.add(rule.when);

      lastConditionTrigger = rule;
      transition('condition_trigger');
      break; // One toast at a time
    }
  }

  // ── Active Mode: Field Tracking ──

  function updateActiveMode(selector: string, fieldState: FieldState) {
    if (state !== 'active' || !pageModel) return;

    // Track completed fields (has value, no error)
    if (fieldState.hasValue && !fieldState.hasError) {
      completedFieldSelectors.add(selector);
      showHighlight(shadowRoot, selector, 'success');
    } else {
      completedFieldSelectors.delete(selector);
    }

    // Find which field is focused and update the guide
    if (fieldState.hasFocus) {
      const idx = pageModel.fields.findIndex(f => f.selector === selector);
      if (idx >= 0 && idx !== activeFieldIndex) {
        activeFieldIndex = idx;
        clearHighlights();
        showHighlight(shadowRoot, selector, 'info');
        // Re-highlight completed fields
        for (const sel of completedFieldSelectors) {
          showHighlight(shadowRoot, sel, 'success');
        }
      }
    }

    // Update the guide panel
    updateActiveGuide(ui, pageModel, activeFieldIndex, completedFieldSelectors);
  }

  // ── Idle Tracking ──

  function onActivity() {
    lastActivity = Date.now();
    pageState.idleSeconds = 0;
  }

  function startIdleTracking() {
    document.addEventListener('mousemove', onActivity, { passive: true });
    document.addEventListener('keydown', onActivity, { passive: true });
    document.addEventListener('scroll', onActivity, { passive: true });
    document.addEventListener('touchstart', onActivity, { passive: true });

    idleTimer = setInterval(() => {
      pageState.idleSeconds = Math.floor((Date.now() - lastActivity) / 1000);
      checkConditions();
    }, 5_000);
  }

  function stopIdleTracking() {
    document.removeEventListener('mousemove', onActivity);
    document.removeEventListener('keydown', onActivity);
    document.removeEventListener('scroll', onActivity);
    document.removeEventListener('touchstart', onActivity);
    if (idleTimer) clearInterval(idleTimer);
  }

  // ── Observer Integration ──

  function onFieldChange(selector: string, fieldState: FieldState) {
    const prev = pageState.fieldStates[selector];
    pageState.fieldStates[selector] = fieldState;

    if (prev && (prev.hasValue !== fieldState.hasValue || prev.hasError !== fieldState.hasError)) {
      pageState.changedFields.add(selector);
    }

    // Track validation errors for frustration
    if (fieldState.hasError && (!prev || !prev.hasError)) {
      frustrationTracker?.recordValidationError(selector);
    }

    // ── Field Help: Show on focus, hide on blur ──
    if (fieldState.hasFocus && focusedFieldSelector !== selector) {
      // New field focused - show help
      focusedFieldSelector = selector;
      showFieldHelpForSelector(selector);
    } else if (!fieldState.hasFocus && focusedFieldSelector === selector) {
      // Field lost focus
      const hadValue = prev?.hasValue ?? false;
      const nowHasValue = fieldState.hasValue;

      // If field was completed (gained value), show success tick
      if (!hadValue && nowHasValue && !fieldState.hasError) {
        hideFieldHelp();
        showSuccessTick(shadowRoot, selector);
      } else {
        // Just hide help (no tick)
        hideFieldHelp();
      }

      focusedFieldSelector = null;
      fieldHelpShown = false;
    }

    // ── Show success tick when field becomes valid (value + no error) ──
    if (prev && !prev.hasValue && fieldState.hasValue && !fieldState.hasError && state !== 'active') {
      // Only show tick if not in active guide mode (which has its own completion UI)
      if (!fieldState.hasFocus) {
        showSuccessTick(shadowRoot, selector);
      }
    }

    // Check if any required fields are incomplete
    if (pageModel?.fields) {
      pageState.hasIncompleteRequired = pageModel.fields
        .filter(f => f.type === 'required' || f.help?.includes('required'))
        .some(f => {
          const s = pageState.fieldStates[f.selector];
          return !s || !s.hasValue;
        });
    }

    // Update active mode guide
    updateActiveMode(selector, fieldState);

    // Re-evaluate workflow rules on field changes
    workflowEvaluator?.evaluate();

    checkConditions();
  }

  // Show contextual help for a focused field
  function showFieldHelpForSelector(selector: string) {
    if (state === 'active') return; // Don't show inline help in guided mode

    const field = pageModel?.fields.find(f => f.selector === selector);
    if (!field) return;

    // Only show if field has help or pattern
    if (!field.help && !field.pattern) return;

    // Find related questions from topics
    const relatedQuestions = pageModel?.topics
      .filter(t => t.question.toLowerCase().includes(field.label.toLowerCase()) ||
                   field.label.toLowerCase().includes(t.question.toLowerCase().split(' ')[0]))
      .map(t => t.question)
      .slice(0, 3) ?? [];

    showFieldHelp(shadowRoot, selector, {
      label: field.label,
      help: field.help,
      pattern: field.pattern,
      questions: relatedQuestions,
      onQuestionClick: (q) => {
        hideFieldHelp();
        handleQuestion(q);
      },
      onClose: () => {
        fieldHelpShown = false;
      },
    });

    fieldHelpShown = true;
  }

  function onFieldIdle(selector: string) {
    if (state !== 'idle') return;

    // Don't show toast if inline field help is already visible
    if (fieldHelpShown && focusedFieldSelector === selector) return;

    const field = pageModel?.fields.find(f => f.selector === selector);
    if (field?.help) {
      lastConditionTrigger = {
        when: `[${selector}].focus`,
        suggest: field.help,
        highlight: selector,
      };
      transition('field_idle');
    }
  }

  function onFormSubmit() {
    pageState.submitAttempts++;
    pageState.changedFields.clear();
    checkConditions();
  }

  // ── Initialization ──

  async function init() {
    // Load page model
    pageModel = await api.loadPageModel(location.pathname);

    if (pageModel) {
      // Start observing known fields
      observer = createFieldObserver({
        trackedSelectors: pageModel.fields.map(f => f.selector),
        fieldIdleMs: 5_000,
        onFieldChange,
        onFieldIdle,
        onSubmit: onFormSubmit,
      });
      observer.start();
    }

    // Initialize workflow evaluator if the model has workflow rules
    if (pageModel?.workflowRules?.length) {
      workflowEvaluator = createWorkflowEvaluator(
        pageModel.workflowRules,
        pageModel.sections ?? [],
        pageModel.pageId
      );
      // Initial evaluation
      workflowEvaluator.evaluate();
    }

    // Pre-warm topic cache in background (after a short delay)
    if (pageModel?.topics?.length) {
      setTimeout(() => prewarmTopicCache(), 2000);
    }

    // Initialize frustration tracker
    frustrationTracker = createFrustrationTracker({
      threshold: 5.0,
      onFrustrated: () => {
        if (state === 'idle') {
          transition('frustration_threshold');
        }
      },
    });

    // Start idle tracking
    startIdleTracking();

    // Listen for page visibility changes
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) {
        stopIdleTracking();
      } else {
        startIdleTracking();
      }
    });

    api.trackEvent('widget_loaded', {
      pageId: pageModel?.pageId ?? 'unknown',
      hasModel: !!pageModel,
    });
  }

  // Start
  init();

  // Return cleanup handle
  return {
    destroy() {
      observer?.destroy();
      frustrationTracker?.destroy();
      workflowEvaluator?.destroy();
      stopIdleTracking();
      clearHighlights();
      hideFieldHelp();
      topicCache.clear();
    },
  };
}
