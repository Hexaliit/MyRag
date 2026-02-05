/**
 * LucidSupport Admin — Shared Alpine.js components + HTMX config.
 */

// HTMX configuration
document.addEventListener('htmx:configRequest', (e) => {
  e.detail.headers['Content-Type'] = 'application/json';
});

// Alpine.js global store for admin state
document.addEventListener('alpine:init', () => {
  Alpine.store('admin', {
    dirty: false,
    currentPage: null,
    saving: false,

    markDirty() {
      this.dirty = true;
    },
    markClean() {
      this.dirty = false;
    },
    async loadPages() {
      const res = await fetch('/api/admin/pages');
      return res.ok ? await res.json() : [];
    },
    async loadPage(pageId) {
      const res = await fetch(`/api/admin/pages/${encodeURIComponent(pageId)}`);
      if (!res.ok) return null;
      const data = await res.json();
      this.currentPage = data;
      return data;
    },
    async savePage(pageId, data) {
      this.saving = true;
      try {
        const res = await fetch(`/api/admin/pages/${encodeURIComponent(pageId)}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        });
        if (res.ok) {
          this.dirty = false;
          this.currentPage = await res.json();
          return true;
        }
        return false;
      } finally {
        this.saving = false;
      }
    },
    async deletePage(pageId) {
      const res = await fetch(`/api/admin/pages/${encodeURIComponent(pageId)}`, {
        method: 'DELETE'
      });
      return res.ok;
    },
    async simulate(pageId, state) {
      const res = await fetch(`/api/admin/pages/${encodeURIComponent(pageId)}/simulate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state)
      });
      return res.ok ? await res.json() : null;
    },
    async evaluateWorkflow(pageId, state) {
      const res = await fetch(`/api/admin/pages/${encodeURIComponent(pageId)}/workflow/evaluate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(state)
      });
      return res.ok ? await res.json() : null;
    },
    exportUrl(pageId) {
      return `/api/admin/pages/${encodeURIComponent(pageId)}/export`;
    }
  });
});

/**
 * Get condition type for color coding.
 */
function getConditionType(when) {
  if (when.includes('[#') || when.includes('[.')) return 'field';
  if (when.includes('page.') || when.includes('form.')) return 'page';
  if (when.includes('user.')) return 'user';
  return 'page';
}

/**
 * Format a condition 'when' expression for display.
 */
function formatCondition(when) {
  return when
    .replace(/\[#([^\]]+)\]/g, '<span class="badge badge-sm badge-primary">$1</span>')
    .replace(/AND/g, '<span class="badge badge-sm badge-ghost">AND</span>')
    .replace(/OR/g, '<span class="badge badge-sm badge-ghost">OR</span>');
}
