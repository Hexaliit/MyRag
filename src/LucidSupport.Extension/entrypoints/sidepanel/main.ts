import type {
  PageExtractionResult,
  ExtractedField,
  NavLink,
  AdminField,
} from '@/lib/types';
import {
  createPage,
  updatePage,
  setBaseUrl,
  getBaseUrl,
  testConnection,
} from '@/lib/api-client';

// ── DOM refs ────────────────────────────────────────────────────────

const serverUrlInput = document.getElementById('server-url') as HTMLInputElement;
const btnTest = document.getElementById('btn-test') as HTMLButtonElement;
const connectionStatus = document.getElementById('connection-status') as HTMLSpanElement;
const btnExtract = document.getElementById('btn-extract') as HTMLButtonElement;
const btnSave = document.getElementById('btn-save') as HTMLButtonElement;
const btnSelectAll = document.getElementById('btn-select-all') as HTMLButtonElement;
const btnSelectNone = document.getElementById('btn-select-none') as HTMLButtonElement;
const statusEl = document.getElementById('status') as HTMLDivElement;
const resultsEl = document.getElementById('results') as HTMLDivElement;
const pageTitleInput = document.getElementById('page-title') as HTMLInputElement;
const pageIdInput = document.getElementById('page-id') as HTMLInputElement;
const pageIdError = document.getElementById('page-id-error') as HTMLDivElement;
const urlPatternInput = document.getElementById('url-pattern') as HTMLInputElement;
const fieldCountEl = document.getElementById('field-count') as HTMLSpanElement;
const fieldListEl = document.getElementById('field-list') as HTMLDivElement;
const navSection = document.getElementById('nav-section') as HTMLElement;
const navListEl = document.getElementById('nav-list') as HTMLDivElement;

// ── State ───────────────────────────────────────────────────────────

let currentExtraction: PageExtractionResult | null = null;
let fieldIncluded: boolean[] = [];
let currentTabId: number | null = null;

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

function validatePageId(value: string): boolean {
  return /^[a-z0-9-]+$/.test(value);
}

// ── Connection testing ──────────────────────────────────────────────

async function checkConnection() {
  connectionStatus.className = 'connection-indicator checking';
  connectionStatus.title = 'Checking...';

  const connected = await testConnection();

  if (connected) {
    connectionStatus.className = 'connection-indicator connected';
    connectionStatus.title = 'Connected';
  } else {
    connectionStatus.className = 'connection-indicator disconnected';
    connectionStatus.title = 'Cannot connect to server';
  }

  return connected;
}

// ── Highlight element on page ───────────────────────────────────────

async function highlightField(selector: string) {
  if (!currentTabId) return;

  try {
    await browser.tabs.sendMessage(currentTabId, {
      type: 'HIGHLIGHT_ELEMENT',
      selector,
    });
  } catch {
    // Tab may have been closed
  }
}

async function clearHighlight() {
  if (!currentTabId) return;

  try {
    await browser.tabs.sendMessage(currentTabId, { type: 'CLEAR_HIGHLIGHT' });
  } catch {
    // Tab may have been closed
  }
}

// ── Render extraction results ───────────────────────────────────────

function renderFields(fields: ExtractedField[]) {
  fieldCountEl.textContent = String(fields.length);
  fieldListEl.innerHTML = '';
  fieldIncluded = fields.map(() => true);

  fields.forEach((field, idx) => {
    const card = document.createElement('div');
    card.className = 'field-card';
    card.dataset.idx = String(idx);

    // Build badges HTML
    const badges: string[] = [];
    badges.push(`<span class="badge badge-type">${escapeHtml(field.type)}</span>`);
    if (field.required) {
      badges.push('<span class="badge badge-required">required</span>');
    }
    if (field.pattern) {
      badges.push(`<span class="badge badge-pattern">${escapeHtml(field.pattern)}</span>`);
    }
    if (field.autocomplete) {
      badges.push(`<span class="badge">${escapeHtml(field.autocomplete)}</span>`);
    }

    const displayLabel = field.label || field.name || field.selector;

    card.innerHTML = `
      <div class="field-card-header">
        <input type="checkbox" class="field-checkbox" data-idx="${idx}" checked title="Include this field" />
        <span class="field-label" data-selector="${escapeHtml(field.selector)}" title="Click to highlight on page">${escapeHtml(displayLabel)}</span>
        <div class="field-actions">
          <button class="field-highlight-btn" data-selector="${escapeHtml(field.selector)}" title="Highlight on page">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <circle cx="12" cy="12" r="10"/>
              <circle cx="12" cy="12" r="3"/>
            </svg>
          </button>
        </div>
      </div>
      <div class="field-selector" data-selector="${escapeHtml(field.selector)}" title="Click to highlight">${escapeHtml(field.selector)}</div>
      <div class="field-badges">${badges.join('')}</div>
      <div class="expanded-content">
        <label for="field-label-${idx}">Label</label>
        <input id="field-label-${idx}" type="text" value="${escapeHtml(field.label)}" data-field="label" data-idx="${idx}" />
        <label for="field-help-${idx}">Help text</label>
        <textarea id="field-help-${idx}" placeholder="Add help text for this field..." data-field="help" data-idx="${idx}"></textarea>
      </div>
    `;

    fieldListEl.appendChild(card);
  });

  // Attach event listeners
  fieldListEl.querySelectorAll('.field-checkbox').forEach((cb) => {
    cb.addEventListener('change', (e) => {
      const target = e.target as HTMLInputElement;
      const idx = Number(target.dataset.idx);
      fieldIncluded[idx] = target.checked;
      const card = target.closest('.field-card');
      if (card) {
        card.classList.toggle('excluded', !target.checked);
      }
      updateFieldCount();
    });
  });

  fieldListEl.querySelectorAll('[data-selector]').forEach((el) => {
    el.addEventListener('click', () => {
      const selector = (el as HTMLElement).dataset.selector;
      if (selector) highlightField(selector);
    });
  });
}

function updateFieldCount() {
  const included = fieldIncluded.filter(Boolean).length;
  const total = fieldIncluded.length;
  fieldCountEl.textContent = included === total ? String(total) : `${included}/${total}`;
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
  btnExtract.innerHTML = `
    <svg class="btn-icon-left" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
      <circle cx="12" cy="12" r="10"/>
      <path d="M12 6v6l4 2"/>
    </svg>
    Extracting...
  `;

  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) {
      showStatus('No active tab found.', 'error');
      return;
    }

    currentTabId = tab.id;

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
    if (msg.includes('Could not establish connection')) {
      showStatus('Content script not loaded. Try refreshing the page.', 'error');
    } else {
      showStatus(`Extraction failed: ${msg}`, 'error');
    }
  } finally {
    btnExtract.disabled = false;
    btnExtract.innerHTML = `
      <svg class="btn-icon-left" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <circle cx="11" cy="11" r="8"/>
        <line x1="21" y1="21" x2="16.65" y2="16.65"/>
      </svg>
      Learn This Page
    `;
  }
}

// ── Save handler ────────────────────────────────────────────────────

async function handleSave() {
  if (!currentExtraction) return;

  // Validate page ID
  const pageId = pageIdInput.value.trim();
  if (!validatePageId(pageId)) {
    pageIdError.hidden = false;
    pageIdInput.focus();
    return;
  }
  pageIdError.hidden = true;

  const title = pageTitleInput.value.trim();
  if (!title) {
    showStatus('Page title is required.', 'warning');
    pageTitleInput.focus();
    return;
  }

  hideStatus();
  btnSave.disabled = true;
  btnSave.innerHTML = `
    <svg class="btn-icon-left" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
      <circle cx="12" cy="12" r="10"/>
      <path d="M12 6v6l4 2"/>
    </svg>
    Saving...
  `;

  try {
    setBaseUrl(serverUrlInput.value);

    // Collect edited labels and help text, filtering excluded fields
    const fields: AdminField[] = currentExtraction.fields
      .filter((_, idx) => fieldIncluded[idx])
      .map((f, originalIdx) => {
        // Find actual index accounting for filtering
        let actualIdx = -1;
        let count = 0;
        for (let i = 0; i < currentExtraction!.fields.length; i++) {
          if (fieldIncluded[i]) {
            if (count === originalIdx) {
              actualIdx = i;
              break;
            }
            count++;
          }
        }
        // Use the original index to get form values
        const idx = currentExtraction!.fields.indexOf(f);

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

    const result = await createPage({
      pageId,
      title,
      urlPattern: urlPatternInput.value,
      site: new URL(currentExtraction.url).hostname,
      fields,
    });

    if (result.ok) {
      showStatus(
        `Saved "${title}" (${fields.length} fields). Open admin editor to refine.`,
        'success',
      );
    } else if (result.status === 409) {
      showStatus('Page exists. Updating...', 'info');
      const updateResult = await updatePage(pageId, {
        title,
        urlPattern: urlPatternInput.value,
        fields,
      });
      if (updateResult.ok) {
        showStatus(`Updated "${title}" (${fields.length} fields).`, 'success');
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
    btnSave.innerHTML = `
      <svg class="btn-icon-left" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/>
        <polyline points="17 21 17 13 7 13 7 21"/>
        <polyline points="7 3 7 8 15 8"/>
      </svg>
      Save to LucidSupport
    `;
  }
}

// ── Select all/none ─────────────────────────────────────────────────

function selectAllFields(selected: boolean) {
  fieldIncluded = fieldIncluded.map(() => selected);
  fieldListEl.querySelectorAll<HTMLInputElement>('.field-checkbox').forEach((cb) => {
    cb.checked = selected;
    const card = cb.closest('.field-card');
    if (card) {
      card.classList.toggle('excluded', !selected);
    }
  });
  updateFieldCount();
}

// ── Keyboard shortcuts ──────────────────────────────────────────────

function setupKeyboardShortcuts() {
  document.addEventListener('keydown', (e) => {
    // Ctrl+E to extract
    if (e.ctrlKey && e.key === 'e') {
      e.preventDefault();
      if (!btnExtract.disabled) {
        handleExtract();
      }
    }
    // Ctrl+S to save
    if (e.ctrlKey && e.key === 's') {
      e.preventDefault();
      if (!btnSave.disabled && !resultsEl.hidden) {
        handleSave();
      }
    }
    // Escape to clear highlight
    if (e.key === 'Escape') {
      clearHighlight();
    }
  });
}

// ── Validation ──────────────────────────────────────────────────────

function setupValidation() {
  pageIdInput.addEventListener('input', () => {
    const valid = validatePageId(pageIdInput.value);
    pageIdError.hidden = valid || pageIdInput.value === '';
    pageIdInput.setCustomValidity(valid ? '' : 'Invalid page ID');
  });

  // Auto-convert to lowercase
  pageIdInput.addEventListener('blur', () => {
    pageIdInput.value = pageIdInput.value.toLowerCase();
  });
}

// ── Init ────────────────────────────────────────────────────────────

async function init() {
  // Restore saved server URL
  try {
    const stored = await browser.storage.local.get('serverUrl');
    if (stored.serverUrl) {
      serverUrlInput.value = stored.serverUrl as string;
      setBaseUrl(stored.serverUrl as string);
    }
  } catch {
    // storage may not be available yet
  }

  // Initial connection check
  checkConnection();

  // Server URL change handler
  serverUrlInput.addEventListener('change', async () => {
    const url = serverUrlInput.value.trim();
    setBaseUrl(url);
    await browser.storage.local.set({ serverUrl: url }).catch(() => {});
    checkConnection();
  });

  // Test connection button
  btnTest.addEventListener('click', async () => {
    const connected = await checkConnection();
    if (connected) {
      showStatus('Connected to server.', 'success');
    } else {
      showStatus('Cannot connect to server. Check the URL.', 'error');
    }
  });

  // Event listeners
  btnExtract.addEventListener('click', handleExtract);
  btnSave.addEventListener('click', handleSave);
  btnSelectAll.addEventListener('click', () => selectAllFields(true));
  btnSelectNone.addEventListener('click', () => selectAllFields(false));

  // Setup keyboard shortcuts
  setupKeyboardShortcuts();

  // Setup validation
  setupValidation();

  // Clear highlight when panel loses focus
  window.addEventListener('blur', clearHighlight);
}

init();
