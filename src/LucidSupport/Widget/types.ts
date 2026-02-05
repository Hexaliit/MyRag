// ── LucidSupport Widget SDK — Type Definitions ──

/** Configuration read from data-* attributes on the script tag. */
export interface WidgetConfig {
  site: string;
  api: string;
  position: 'bottom-right' | 'bottom-left';
  theme: 'light' | 'dark' | 'auto';
}

/** Runtime context sent to the API. Contains page state but NEVER field values. */
export interface PageContext {
  url: string;
  visibleFieldIds: string[];
  fieldStates: Record<string, FieldState>;
  viewportWidth: number;
  question?: string;
}

/** State of a single field. Privacy-safe: no actual values. */
export interface FieldState {
  hasValue: boolean;
  hasError: boolean;
  errorText: string | null;
  hasFocus: boolean;
}

/** Response from the help API. */
export interface HelpResponse {
  text: string;
  highlights: HighlightTarget[];
  suggestions: string[];
  topics: TopicLink[];
  source: string | null;
}

/** Element to highlight on the page. */
export interface HighlightTarget {
  selector: string;
  style: 'error' | 'info' | 'success';
}

/** Link to a help topic or KB article. */
export interface TopicLink {
  id: string;
  label: string;
}

/** Page model returned by the API for known pages. */
export interface SupportPageModel {
  pageId: string;
  urlPattern: string;
  title: string;
  fields: SupportField[];
  conditions: ConditionRule[];
  topics: TopicMapping[];
  sections?: SupportSection[];
  workflowRules?: WorkflowRule[];
}

/** Field definition from the page model. */
export interface SupportField {
  selector: string;
  label: string;
  type: string;
  pattern: string | null;
  help: string | null;
}

/** Condition rule from .support.md. */
export interface ConditionRule {
  when: string;
  suggest: string;
  highlight: string | null;
}

/** Topic → KB article mapping. */
export interface TopicMapping {
  question: string;
  articleId: string;
}

/** Section grouping from ## Sections in .support.md. */
export interface SupportSection {
  id: string;
  label: string;
  fields: string[];
  order: number;
}

/** Workflow visibility rule from ## Workflow in .support.md. */
export interface WorkflowRule {
  when: string;
  action: 'show' | 'hide';
  target: string;
  priority: number;
}

/** Workflow event dispatched on the host document. */
export interface WorkflowEvent {
  type: 'ls:field:show' | 'ls:field:hide' | 'ls:section:show' | 'ls:section:hide';
  target: string;
  rule: string;
  pageId: string;
}

/** Escalation event dispatched when user requests human help. */
export interface EscalationEvent {
  type: 'ls:escalation:requested';
  pageId: string;
  frustrationScore: number;
  message?: string;
}

/** Internal page state tracked by the widget. */
export interface PageState {
  fieldStates: Record<string, FieldState>;
  changedFields: Set<string>;
  idleSeconds: number;
  hasIncompleteRequired: boolean;
  submitAttempts: number;
}

/** Widget state machine states. */
export type WidgetState = 'idle' | 'toast' | 'panel' | 'asking' | 'showing' | 'struggling' | 'active';

/** Events that trigger state transitions. */
export type WidgetEvent =
  | 'condition_trigger'
  | 'fab_click'
  | 'field_idle'
  | 'toast_click'
  | 'toast_dismiss'
  | 'toast_timeout'
  | 'ask_question'
  | 'panel_close'
  | 'response'
  | 'error'
  | 'frustration_threshold'
  | 'accept_guide'
  | 'dismiss_struggling'
  | 'exit_guide';

/** State transition definition. */
export interface Transition {
  from: WidgetState;
  event: WidgetEvent;
  to: WidgetState;
}
