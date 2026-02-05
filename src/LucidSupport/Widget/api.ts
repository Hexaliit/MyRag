// ── LucidSupport Widget SDK — API Client ──
// fetch wrapper for help requests, page model loading, and analytics.

import type { PageContext, HelpResponse, SupportPageModel } from './types';

/** Create an API client bound to a base URL. */
export function createApiClient(apiBase: string) {
  /** Fetch the page model for the current URL. Returns null for unknown pages. */
  async function loadPageModel(url: string): Promise<SupportPageModel | null> {
    try {
      const response = await fetch(
        `${apiBase}/api/support/page?url=${encodeURIComponent(url)}`,
        { credentials: 'omit' }
      );
      if (!response.ok) return null;
      return response.json();
    } catch {
      return null;
    }
  }

  /** Send a contextual help request. */
  async function askForHelp(context: PageContext): Promise<HelpResponse> {
    const response = await fetch(`${apiBase}/api/help/contextual`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(context),
      keepalive: true,
      credentials: 'omit',
    });

    if (!response.ok) {
      throw new Error(`Help request failed: ${response.status}`);
    }

    return response.json();
  }

  /** Send a streaming help request (SSE). Calls onChunk for each text fragment. */
  async function askStreaming(
    context: PageContext,
    onChunk: (text: string) => void,
    onDone: () => void
  ): Promise<void> {
    const response = await fetch(`${apiBase}/api/help/contextual?stream=true`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(context),
      credentials: 'omit',
    });

    if (!response.ok) {
      throw new Error(`Stream request failed: ${response.status}`);
    }

    if (!response.body) {
      // Fallback: no ReadableStream support — read as text
      const text = await response.text();
      onChunk(text);
      onDone();
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        onChunk(decoder.decode(value, { stream: true }));
      }
    } finally {
      reader.releaseLock();
      onDone();
    }
  }

  /** Fire-and-forget analytics event via sendBeacon. */
  function trackEvent(event: string, data: Record<string, unknown> = {}): void {
    if (!navigator.sendBeacon) return;
    const payload = JSON.stringify({ event, ...data, ts: Date.now() });
    navigator.sendBeacon(`${apiBase}/api/analytics`, payload);
  }

  return { loadPageModel, askForHelp, askStreaming, trackEvent };
}

export type ApiClient = ReturnType<typeof createApiClient>;
