# LucidSupport Training Extension

A Chrome extension that lets you "train" LucidSupport by extracting form field metadata from any web page - including authenticated pages that Playwright can't access.

## Overview

The LucidSupport system provides contextual help for web forms. Before it can help users, it needs to "learn" the forms on your site. The training extension solves the authentication problem: instead of using headless browsers that can't log in, the extension runs in YOUR browser session where you're already authenticated.

```
┌─────────────────────────────────────────────────────────────────────┐
│                         TRAINING FLOW                                │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│   ┌──────────────┐    message    ┌──────────────┐                   │
│   │ Side Panel   │ ────────────► │ Content      │                   │
│   │ (UI)         │               │ Script       │                   │
│   │              │ ◄──────────── │ (DOM access) │                   │
│   └──────┬───────┘   extraction  └──────────────┘                   │
│          │                                                           │
│          │ POST /api/admin/pages                                     │
│          ▼                                                           │
│   ┌──────────────┐    write     ┌──────────────┐                    │
│   │ LucidSupport │ ────────────►│ .support.md  │                    │
│   │ Server       │              │ files        │                    │
│   └──────────────┘              └──────────────┘                    │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

## Architecture

### Components

| Component | Location | Purpose |
|-----------|----------|---------|
| **Side Panel** | `entrypoints/sidepanel/` | UI for reviewing and editing extracted data |
| **Content Script** | `entrypoints/content.ts` | Runs on every page, extracts DOM metadata |
| **Background Script** | `entrypoints/background.ts` | Opens side panel on icon click |
| **Extraction Library** | `lib/extractor.ts` | Pure functions for field/nav detection |
| **API Client** | `lib/api-client.ts` | Fetches to LucidSupport server |

### Data Flow

1. **User navigates** to a form page (e.g., checkout, settings)
2. **User clicks extension icon** → Side panel opens
3. **User clicks "Learn This Page"** → Side panel sends `EXTRACT_PAGE` message to content script
4. **Content script** queries the DOM for all form fields and navigation buttons
5. **Extraction result** sent back to side panel with fields, nav links, page title/URL
6. **User reviews/edits** labels and help text in the side panel
7. **User clicks "Save"** → Side panel POSTs to `/api/admin/pages`
8. **Server writes** a `.support.md` file and adds to in-memory store
9. **Widget can now** provide contextual help for that page

## Extraction Details

### Field Detection

The content script finds all interactive form elements:

```javascript
const selector = 'input, select, textarea, [role="textbox"], [role="combobox"], [role="spinbutton"], [contenteditable="true"]';
```

For each element, it extracts:

| Attribute | Source | Example |
|-----------|--------|---------|
| `selector` | Built from ID, name, or structural path | `#email`, `input[name="phone"]` |
| `type` | `el.type` or tag name | `text`, `email`, `tel`, `select` |
| `label` | Multiple strategies (see below) | `"Email Address"` |
| `required` | `el.required` or `aria-required` | `true` |
| `pattern` | `el.pattern` | `[0-9]{5}` |
| `minLength/maxLength` | Element attributes | `5`, `100` |
| `autocomplete` | Normalized (excludes "on"/"off") | `email`, `tel` |

### Label Detection Strategies

The extractor tries 5 strategies in order:

1. **Explicit label**: `<label for="email">` pointing to the element
2. **aria-label**: `aria-label="Email Address"` on the element
3. **aria-labelledby**: References another element's text
4. **Parent label**: Element nested inside a `<label>`
5. **Placeholder**: Falls back to placeholder text
6. **Name**: Last resort, uses the `name` attribute

### Selector Building

Selectors are built with stability in mind:

1. **ID** (most stable): `#email-input`
2. **Name** (if unique): `input[name="email"]`
3. **Structural path** (fallback): `#form > div:nth-of-type(2) > input`

### Navigation Detection

Buttons and links are classified by their text:

| Pattern | Role |
|---------|------|
| `back`, `previous`, `return` | `back` |
| `next`, `continue`, `submit`, `pay`, `confirm` | `next` |
| `cancel`, `close`, `exit` | `cancel` |
| `skip` | `skip` |
| `save`, `draft` | `save` |

## Side Panel Features

### Connection Status

- **Green dot**: Server is reachable
- **Red dot**: Cannot connect
- **Pulsing**: Checking connection

Auto-checks on load and when URL changes.

### Field Cards

Each extracted field shows:
- **Checkbox**: Include/exclude from save
- **Label**: Editable, click to highlight on page
- **Selector**: CSS selector (click to highlight)
- **Badges**: Type, required, pattern, autocomplete
- **Help text**: Optional guidance for users

### Field Highlighting

Click any field label or selector to highlight it on the page:
- Blue pulsing border
- Scrolls into view
- Press `Esc` to clear

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+E` | Extract page |
| `Ctrl+S` | Save to server |
| `Esc` | Clear highlight |

### Validation

- **Page ID**: Must be lowercase letters, numbers, and hyphens
- **Title**: Required
- Auto-converts to lowercase on blur

## Server API

### POST /api/admin/pages

Creates a new page model from extension data.

**Request:**
```json
{
  "pageId": "checkout-payment",
  "title": "Payment Details",
  "urlPattern": "/checkout/payment*",
  "site": "shop.example.com",
  "fields": [
    {
      "selector": "#card-number",
      "label": "Card Number",
      "type": "text",
      "required": true,
      "help": "Enter your 16-digit card number"
    }
  ]
}
```

**Response:** `201 Created` with full `PageDetailDto`

**Errors:**
- `409 Conflict`: Page ID already exists (extension auto-updates instead)

### File Output

The server writes a `.support.md` file:

```markdown
---
page_id: checkout-payment
url_pattern: /checkout/payment*
title: Payment Details
learned: 2026-02-05T10:30:00+00:00
site: shop.example.com
---

# Payment Details

## Fields

### [#card-number] Card Number
- type: text
- required: true
- help: Enter your 16-digit card number
```

## Installation

### Build the Extension

```bash
cd src/LucidSupport.Extension
npm install
npm run build
```

### Load in Chrome

1. Open `chrome://extensions`
2. Enable "Developer mode"
3. Click "Load unpacked"
4. Select `src/LucidSupport.Extension/.output/chrome-mv3/`

### Start the Server

```bash
dotnet run --project src/LucidSupport -- serve --port 5050
```

## Usage Workflow

### Training a New Page

1. **Start the server** on your machine
2. **Navigate** to the page you want to train
3. **Click the extension icon** to open the side panel
4. **Check connection** (green dot = connected)
5. **Click "Learn This Page"**
6. **Review extracted fields**:
   - Click field labels to highlight on page
   - Edit labels if auto-detection was wrong
   - Add help text for complex fields
   - Uncheck fields you don't want to include
7. **Edit page info**:
   - Title: User-friendly name
   - Page ID: URL-friendly identifier
   - URL Pattern: Glob for matching (auto-filled)
8. **Click "Save to LucidSupport"**
9. **Refine in admin editor** (optional): `http://localhost:5050/admin/editor.html?page=your-page-id`

### Training Multi-Page Flows

For wizards/checkout flows with multiple steps:

1. Train each page separately
2. Use the admin editor to set:
   - `flow`: Common flow name (e.g., "checkout")
   - `step`: Position in flow (1, 2, 3...)
   - `prev`/`next`: Link to adjacent page IDs

### Training Authenticated Pages

This is the extension's main advantage:

1. Log into the site normally in Chrome
2. Navigate to the protected page
3. Use the extension to extract
4. The extension runs in your authenticated session

No need to configure cookies, tokens, or SSO in a headless browser.

## Troubleshooting

### "Content script not loaded"

The page was loaded before the extension. **Solution**: Refresh the page.

### "Cannot connect to server"

- Check the server is running (`dotnet run --project src/LucidSupport -- serve`)
- Check the URL in the extension matches the server port
- Check for CORS issues in browser console

### Fields not detected

Some frameworks render fields in shadow DOM or use custom elements. The current extractor doesn't pierce shadow DOM. **Solution**: Add help text manually in the admin editor.

### Wrong labels detected

The label detection heuristics aren't perfect. **Solution**: Edit the label in the side panel before saving.

## Dark Mode

The extension respects your system's color scheme preference:
- Light mode: White cards, blue accents
- Dark mode: Dark gray cards, lighter blue accents

No manual toggle needed.

## Security Notes

- The extension only reads DOM structure, never field **values**
- Extraction runs in a content script sandbox
- Data is only sent to the server URL you configure
- No data is sent to any third party
