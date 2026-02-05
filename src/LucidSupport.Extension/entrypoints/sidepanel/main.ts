import type {
  PageExtractionResult,
  ExtractedField,
  NavLink,
  AdminField,
} from '@/lib/types';
import { createPage, updatePage, setBaseUrl, getBaseUrl } from '@/lib/api-client';

// ── DOM refs ────────────────────────────────────────────────────────

const serverUrlInput = document.getElementById('server-url') as HTMLInputElement;
const btnExtract = document.getElementById('btn-extract') as HTMLButtonElement;
const btnSave = document.getElementById('btn-save') as HTMLButtonElement;
const statusEl = document.getElementById('status') as HTMLDivElement;
const resultsEl = document.getElementById('results') as HTMLDivElement;
const pageTitleInput = document.getElementById('page-title') as HTMLInputElement;
const pageIdInput = document.getElementById('page-id') as HTMLInputElement;
const urlPatternInput = document.getElementById('url-pattern') as HTMLInputElement;
const fieldCountEl = document.getElementById('field-count') as HTMLSpanElement;
const fieldListEl = document.getElementById('field-list') as HTMLDivElement;
const navSection = document.getElementById('nav-section') as HTMLElement;
const navListEl = document.getElementById('nav-list') as HTMLDivElement;

// ── State ───────────────────────────────────────────────────────────

let currentExtraction: PageExtractionResult | null = null;

// ── Helpers ─────────────────────────────────────────────────────────

function showStatus(text: string, type: 'info' | 'success' | 'error' | 'warning') {
  statusEl.textContent = text;
  statusEl.className = `status ${type}`;
  statusEl.hidden = false;
}

function hideStatus() {
  statusEl.hidden = true;
}

function pageIdFromUrl(url: string): string {
  try {
    const u = new URL(url);
    return u.pathname
      .replace(/^\/+|\/+$/g, '')
      .replace(/[\/\.]/g, '-')
      .replace(/[^a-z0-9-]/gi, '')
      .toLowerCase() || 'index';
  } catch {
    return 'unknown-page';
  }
}

function urlPatternFromUrl(url: string): string {
  try {
    const u = new URL(url);
    return u.pathname + '*';
  } catch {
    return url;
  }
}

function escapeHtml(s: string): string {
  const el = document.createElement('span');
  el.textContent = s;
  return el.innerHTML;
}

// ── Render extraction results ───────────────────────────────────────

function renderFields(fields: ExtractedField[]) {
  fieldCountEl.textContent = String(fields.length);
  fieldListEl.innerHTML = '';

  fields.forEach((field, idx) => {
    const card = document.createElement('div');
    card.className = 'field-card';
    card.dataset.idx = String(idx);

    const badges: string[] = [];
    badges.push(`<span class="badge badge-type">${escapeHtml(field.type)}</span>`);
    if (field.required)
      badges.push('<span class="badge badge-required">required</span>');
    if (field.pattern)
      badges.push(`<span class="badge">${escapeHtml(field.pattern)}</span>`);

    card.innerHTML = `
      <div class="field-card-header">
        <span class="field-label">${escapeHtml(field.label || field.name || field.selector)}</span>
        ${badges.join(' ')}
      </div>
      <div class="field-selector">${escapeHtml(field.selector)}</div>
      <label for="field-label-${idx}">Label</label>
      <input id="field-label-${idx}" type="text" value="${escapeHtml(field.label)}" data-field="label" data-idx="${idx}" />
      <label for="field-help-${idx}">Help text</label>
      <textarea id="field-help-${idx}" placeholder="Add help text for this field..." data-field="help" data-idx="${idx}"></textarea>
    `;

    fieldListEl.appendChild(card);
  });
}

function renderNav(nav: Record<string, NavLink>) {
  const entries = Object.values(nav);
  if (entries.length === 0) {
    navSection.hidden = true;
    return;
  }

  navSection.hidden = false;
  navListEl.innerHTML = '';

  for (const link of entries) {
    const item = document.createElement('div');
    item.className = 'nav-item';
    item.innerHTML = `
      <span class="nav-role">${escapeHtml(link.role)}</span>
      <span>${escapeHtml(link.label)}</span>
    `;
    navListEl.appendChild(item);
  }
}

// ── Extract handler ─────────────────────────────────────────────────

async function handleExtract() {
  hideStatus();
  btnExtract.disabled = true;
  btnExtract.textContent = 'Extracting...';

  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) {
      showStatus('No active tab found.', 'error');
      return;
    }

    const result: PageExtractionResult = await browser.tabs.sendMessage(tab.id, {
      type: 'EXTRACT_PAGE',
    });

    if (!result || !result.fields) {
      showStatus('No extraction result. Is the page loaded?', 'warning');
      return;
    }

    currentExtraction = result;

    // Populate page info
    pageTitleInput.value = result.title;
    pageIdInput.value = pageIdFromUrl(result.url);
    urlPatternInput.value = urlPatternFromUrl(result.url);

    // Render fields + nav
    renderFields(result.fields);
    renderNav(result.nav);

    resultsEl.hidden = false;
    showStatus(
      `Extracted ${result.fields.length} field(s) from page.`,
      'success',
    );
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    showStatus(`Extraction failed: ${msg}`, 'error');
  } finally {
    btnExtract.disabled = false;
    btnExtract.textContent = 'Learn This Page';
  }
}

// ── Save handler ────────────────────────────────────────────────────

async function handleSave() {
  if (!currentExtraction) return;

  hideStatus();
  btnSave.disabled = true;
  btnSave.textContent = 'Saving...';

  try {
    setBaseUrl(serverUrlInput.value);

    // Collect edited labels and help text from the UI
    const fields: AdminField[] = currentExtraction.fields.map((f, idx) => {
      const labelInput = document.querySelector<HTMLInputElement>(
        `[data-field="label"][data-idx="${idx}"]`,
      );
      const helpInput = document.querySelector<HTMLTextAreaElement>(
        `[data-field="help"][data-idx="${idx}"]`,
      );

      return {
        selector: f.selector,
        label: labelInput?.value || f.label,
        type: f.type,
        placeholder: f.placeholder ?? undefined,
        pattern: f.pattern ?? undefined,
        required: f.required,
        minLength: f.minLength ?? undefined,
        maxLength: f.maxLength ?? undefined,
        autocomplete: f.autocomplete ?? undefined,
        help: helpInput?.value || undefined,
      };
    });

    const pageId = pageIdInput.value.trim();
    const title = pageTitleInput.value.trim();

    if (!pageId || !title) {
      showStatus('Page ID and title are required.', 'warning');
      return;
    }

    const result = await createPage({
      pageId,
      title,
      urlPattern: urlPatternInput.value,
      site: new URL(currentExtraction.url).hostname,
      fields,
    });

    if (result.ok) {
      showStatus(
        `Saved "${title}" (${fields.length} fields). Open the admin editor at ${getBaseUrl()}/admin/editor.html?page=${encodeURIComponent(pageId)} to refine.`,
        'success',
      );
    } else if (result.status === 409) {
      // Already exists — offer to update
      showStatus('Page already exists. Updating...', 'warning');
      const updateResult = await updatePage(pageId, {
        title,
        urlPattern: urlPatternInput.value,
        fields,
      });
      if (updateResult.ok) {
        showStatus(
          `Updated "${title}" (${fields.length} fields).`,
          'success',
        );
      } else {
        showStatus(`Update failed: ${updateResult.error}`, 'error');
      }
    } else {
      showStatus(`Save failed: ${result.error}`, 'error');
    }
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    showStatus(`Save failed: ${msg}`, 'error');
  } finally {
    btnSave.disabled = false;
    btnSave.textContent = 'Save to LucidSupport';
  }
}

// ── Init ────────────────────────────────────────────────────────────

async function init() {
  // Restore saved server URL
  try {
    const stored = await browser.storage.local.get('serverUrl');
    if (stored.serverUrl) {
      serverUrlInput.value = stored.serverUrl as string;
    }
  } catch {
    // storage may not be available yet
  }

  serverUrlInput.addEventListener('change', () => {
    const url = serverUrlInput.value.trim();
    setBaseUrl(url);
    browser.storage.local.set({ serverUrl: url }).catch(() => {});
  });

  btnExtract.addEventListener('click', handleExtract);
  btnSave.addEventListener('click', handleSave);
}

init();
