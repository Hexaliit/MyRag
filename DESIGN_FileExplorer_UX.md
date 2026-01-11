# File Explorer UX Design

## Overview

The File Explorer is a powerful document discovery and management interface that combines:
- **Natural Language Search** - "Find documents about authentication from last month"
- **Smart Filtering** - Signals, features, communities, entities, dates
- **Collections** - Virtual folders (one file can be in many)
- **Chat Integration** - Select files → collapse explorer → chat with selection

---

## Core Principles

1. **Search-First** - Natural language query is the primary way to find content
2. **Progressive Disclosure** - Start simple, reveal complexity on demand
3. **Keyboard-First** - Power users can navigate without mouse
4. **Context Preservation** - Remember selections, filters, view preferences
5. **Seamless Transitions** - Smooth animation when collapsing to chat mode

---

## Layout Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  [☰] LucidRAG            [🔍 Ask anything... (⌘K)]         [Theme ▼] [User] │
├──────────────────┬──────────────────────────────────────────────────────────┤
│                  │  [Active Filters: Type: PDF ✕ | Community: Auth ✕ | ⊗ ]  │
│  EXPLORER        │                                                          │
│  ─────────────   │  ┌─ Sort: Relevance ▼ ─┐  ┌─ View: ▣ List │ ⊞ Grid ─┐   │
│  📁 Collections  │                                                          │
│    ├─ Research   │  ┌────────────────────────────────────────────────────┐  │
│    ├─ Projects   │  │ ☐ [PDF] Authentication Guide v2.pdf                │  │
│    └─ + New      │  │    "OAuth 2.0 implementation with JWT tokens..."   │  │
│                  │  │    [Auth Team] [Security]  Score: 0.92  Jan 10     │  │
│  🌐 Communities  │  ├────────────────────────────────────────────────────┤  │
│    ├─ Auth (8)   │  │ ☐ [MD] api-security-notes.md                       │  │
│    ├─ API (15)   │  │    "Rate limiting and API key rotation..."         │  │
│    └─ More...    │  │    [API] [Best Practices]  Score: 0.87  Jan 8      │  │
│                  │  ├────────────────────────────────────────────────────┤  │
│  🏷️ Entities     │  │ ☐ [DOCX] Security Audit Report Q4.docx             │  │
│    └─ [Expand]   │  │    "Penetration testing results for..."            │  │
│                  │  │    [Security] [Audit]  Score: 0.84  Dec 15         │  │
│  📊 Signals      │  └────────────────────────────────────────────────────┘  │
│    └─ [Expand]   │                                                          │
│                  │  ┌──────────────────────────────────────────────────────┐│
│  🕒 Recent       │  │  [3 selected]  [💬 Chat with Selection] [📁 Add to] ││
│  ⭐ Favorites    │  └──────────────────────────────────────────────────────┘│
│                  │                                                          │
│  ─────────────   │                                                          │
│  [📤 Upload]     │                                                          │
└──────────────────┴──────────────────────────────────────────────────────────┘
```

---

## Component Details

### 1. Command Search Bar (⌘K)

**Behavior:**
- Always visible in header
- Focus with `⌘K` / `Ctrl+K`
- Accepts natural language: "find auth docs from last month"
- Shows instant suggestions while typing
- Streams Sentinel LLM results in real-time

**Auto-Suggestions:**
```
┌─────────────────────────────────────────────────┐
│ 🔍 "auth"                                       │
├─────────────────────────────────────────────────┤
│ 📄 RECENT DOCUMENTS                             │
│   Authentication Guide v2.pdf                   │
│   api-auth-patterns.md                          │
├─────────────────────────────────────────────────┤
│ 🌐 COMMUNITIES                                  │
│   Auth & Security (8 docs)                      │
├─────────────────────────────────────────────────┤
│ 🏷️ ENTITIES                                     │
│   OAuth 2.0 (5 mentions)                        │
│   JWT (12 mentions)                             │
├─────────────────────────────────────────────────┤
│ 💡 Try: "find authentication docs from 2025"   │
└─────────────────────────────────────────────────┘
```

**Sentinel LLM Flow:**
1. User types: "find documents about rate limiting with code examples"
2. Sentinel interprets query → extracts:
   - Topic: "rate limiting"
   - Feature: "has code"
   - Intent: discovery
3. Results stream in with semantic highlighting
4. Auto-generates filter chips: `[rate limiting] [has: code]`

---

### 2. Explorer Sidebar (Collapsible)

**Width:** 280px expanded, 48px collapsed
**Toggle:** Hamburger menu or `⌘\`

#### Sections:

**📁 Collections (Virtual Folders)**
```
📁 Collections                              [+]
  ├─ 📂 Research Papers            (23)
  │    └─ ML Papers                (12)
  ├─ 📂 Project Docs               (45)
  ├─ 📂 Meeting Notes              (8)
  └─ 📂 Personal                   (15)
```

- Drag-and-drop files to collections
- Files can be in multiple collections
- Create nested folders
- Smart collections (auto-populate by rules)

**🌐 Communities (GraphRAG)**
```
🌐 Communities                              [⋯]
  ├─ Auth & Security               (8)  ▸
  │   "Authentication, JWT, OAuth..."
  ├─ API Documentation            (15)  ▸
  │   "REST endpoints, GraphQL..."
  ├─ Database Design               (6)  ▸
  └─ [Show 12 more...]
```

- Auto-generated by GraphRAG clustering
- Shows community name + summary preview
- Click to filter files by community
- Expand arrow (▸) shows member entities

**🏷️ Entities (Auto-Extracted)**
```
🏷️ Entities                                 [⋯]
  ├─ 👤 People                      [→]
  │    Scott (15) • John (8) • ...
  ├─ 🏢 Organizations               [→]
  │    Anthropic (12) • OpenAI (8)
  ├─ 📍 Locations                   [→]
  ├─ 💡 Concepts                    [→]
  │    RAG (25) • Embeddings (18)
  └─ 🔧 Code Elements               [→]
       IEmbeddingService (5) • ...
```

- Grouped by entity type
- Click entity to filter
- Multi-select with Ctrl/Cmd

**📊 Signals (Advanced Filters)**
```
📊 Signals                                  [⋯]
  ├─ 📄 Document Type
  │    ☑ PDF  ☑ Markdown  ☐ DOCX
  ├─ 📅 Date Range
  │    [Last 30 days ▼]
  ├─ ⭐ Quality Score
  │    [━━━━━●━━━━━] > 0.7
  ├─ 🖼️ Has Images
  │    ○ Yes  ○ No  ● Any
  ├─ 📊 Has Tables
  │    ○ Yes  ○ No  ● Any
  └─ [+ More Signals...]
```

- Content-type specific signals
- Date range with presets
- Quality/confidence slider
- Boolean toggles for features

---

### 3. Active Filter Chips

**Location:** Above results, below search bar

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Active Filters:                                                         │
│ [Type: PDF ✕] [Community: Auth ✕] [Entity: OAuth ✕] [Since: 30d ✕] [⊗] │
└─────────────────────────────────────────────────────────────────────────┘
```

**Behavior:**
- Click ✕ to remove individual filter
- Click ⊗ "Clear all" to reset
- Color-coded by filter type:
  - Blue = Type filters
  - Green = Community filters
  - Purple = Entity filters
  - Orange = Signal filters
- Chips are generated from:
  - Sidebar selections
  - Sentinel LLM query interpretation
  - URL parameters (shareable filtered views)

---

### 4. Results Area

**View Toggles:** List | Grid | Table

#### List View (Default)
```
┌────────────────────────────────────────────────────────────────────────┐
│ ☐ [PDF] Authentication Guide v2.pdf                          ⭐ ⋯    │
│    "OAuth 2.0 implementation with **JWT tokens** requires careful..."  │
│    [Auth Team] [Security] [API]                                        │
│    Score: 0.92 • 45 pages • Updated: Jan 10, 2026                      │
├────────────────────────────────────────────────────────────────────────┤
│ ☐ [MD] api-security-notes.md                                 ⭐ ⋯    │
│    "**Rate limiting** and API key rotation best practices..."          │
│    [API] [Best Practices]                                              │
│    Score: 0.87 • 2.3 KB • Updated: Jan 8, 2026                         │
└────────────────────────────────────────────────────────────────────────┘
```

- Checkbox for multi-select
- File type icon + name
- Semantic snippet with **bold** matching concepts
- Tag chips
- Metadata row: score, size/pages, date
- Hover actions: ⭐ Favorite, ⋯ Menu

#### Grid/Gallery View
```
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│   [PDF]      │ │   [MD]       │ │   [IMG]      │
│  ┌────────┐  │ │  ┌────────┐  │ │  ┌────────┐  │
│  │ thumb  │  │ │  │ thumb  │  │ │  │ thumb  │  │
│  └────────┘  │ │  └────────┘  │ │  └────────┘  │
│ Auth Guide   │ │ API Notes    │ │ Diagram.png  │
│ Jan 10       │ │ Jan 8        │ │ Jan 5        │
│ [Auth]       │ │ [API]        │ │ [Arch]       │
└──────────────┘ └──────────────┘ └──────────────┘
```

- Visual thumbnails (PDF first page, image preview)
- Compact metadata
- Hover for full title

#### Table View (Power Users)
```
│ ☐ │ Name                    │ Type │ Community      │ Score │ Updated  │
├───┼─────────────────────────┼──────┼────────────────┼───────┼──────────┤
│ ☐ │ Authentication Guide    │ PDF  │ Auth & Sec     │ 0.92  │ Jan 10   │
│ ☐ │ api-security-notes      │ MD   │ API Docs       │ 0.87  │ Jan 8    │
│ ☐ │ Security Audit Q4       │ DOCX │ Auth & Sec     │ 0.84  │ Dec 15   │
```

- Sortable columns
- Resizable
- Column visibility toggle

---

### 5. Selection & Chat Mode

**Selection Bar (appears when files selected):**
```
┌────────────────────────────────────────────────────────────────────────┐
│  ☑ 3 files selected                                                    │
│  [💬 Chat with Selection] [📁 Add to Collection] [🏷️ Tag] [⋯ More]   │
└────────────────────────────────────────────────────────────────────────┘
```

**Chat Mode Transition:**
1. User clicks "Chat with Selection"
2. Explorer sidebar collapses to 48px (icons only)
3. Results area becomes chat interface
4. Selected files shown as context chips above chat

```
┌──────────────────────────────────────────────────────────────────────┐
│ [◀ Back to Explorer]                                                 │
│                                                                      │
│ Chatting with: [Auth Guide.pdf ✕] [api-notes.md ✕] [Audit.docx ✕]   │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│                    [Chat messages area]                              │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│ [Ask about these documents...]                              [Send]   │
└──────────────────────────────────────────────────────────────────────┘
```

**Features:**
- Remove individual files from context
- Add more files without leaving chat
- Collapsed sidebar shows file count badge
- "Back to Explorer" preserves chat history

---

### 6. Upload Interface

**Location:** Bottom of sidebar + drag-and-drop zone

**Sidebar Upload Button:**
```
┌─────────────────────────┐
│  [📤 Upload Files]      │
│  Drag files anywhere    │
└─────────────────────────┘
```

**Upload Modal:**
```
┌────────────────────────────────────────────────────────────────────┐
│  Upload Documents                                           [✕]    │
├────────────────────────────────────────────────────────────────────┤
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                                                              │  │
│  │         📄 Drop files here or click to browse                │  │
│  │                                                              │  │
│  │         Supported: PDF, DOCX, MD, HTML, TXT, Images          │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  Add to Collection: [Research Papers ▼]                            │
│                                                                    │
│  Tags: [+ Add tags...]                                             │
│                                                                    │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ auth-guide.pdf                    ━━━━━━━━●━━ 78%  [✕]     │    │
│  │ api-notes.md                      ━━━━━━━━━━━━ ✓ Done      │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                    │
│                                          [Cancel]  [Upload All]    │
└────────────────────────────────────────────────────────────────────┘
```

**Processing Status:**
- Real-time progress bar
- Status indicators: Uploading → Processing → Indexing → Done
- Error handling with retry option

---

### 7. Smart Collections (Rule-Based)

**Create Smart Collection Modal:**
```
┌────────────────────────────────────────────────────────────────────┐
│  Create Smart Collection                                    [✕]    │
├────────────────────────────────────────────────────────────────────┤
│  Name: [Security Documentation                              ]      │
│  Icon: [🔒 ▼]                                                      │
│                                                                    │
│  Match: (● All rules) (○ Any rule)                                 │
│                                                                    │
│  Rules:                                                            │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ [Community ▼] [is        ▼] [Auth & Security      ▼] [✕]  │    │
│  │ [Entity    ▼] [contains  ▼] [security             ▼] [✕]  │    │
│  │ [Type      ▼] [is        ▼] [PDF, Markdown        ▼] [✕]  │    │
│  │ [Updated   ▼] [after     ▼] [2025-01-01           ▼] [✕]  │    │
│  └────────────────────────────────────────────────────────────┘    │
│  [+ Add Rule]                                                      │
│                                                                    │
│  Preview: 12 documents match                      [Refresh]        │
│                                                                    │
│                                          [Cancel]  [Create]        │
└────────────────────────────────────────────────────────────────────┘
```

**Available Rule Types:**
- Community: is, is not
- Entity: contains, does not contain
- Type: is (multi-select)
- Date: before, after, between
- Signal: has images, has tables, has code
- Quality: above, below threshold
- Search: matches query (semantic)

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `⌘K` / `Ctrl+K` | Focus search bar |
| `⌘\` / `Ctrl+\` | Toggle sidebar |
| `↑` / `↓` | Navigate results |
| `Space` | Toggle selection |
| `⌘A` | Select all (in view) |
| `Enter` | Open selected file |
| `⌘Enter` | Chat with selection |
| `⌘⇧N` | New collection |
| `⌘⇧U` | Upload files |
| `Esc` | Clear search / Close modal |
| `F` | Open filter panel |
| `1` / `2` / `3` | Switch views (List/Grid/Table) |

---

## State Persistence

**LocalStorage:**
- Selected view mode
- Sidebar expanded/collapsed
- Column widths (table view)
- Recent searches
- Favorite filters

**URL Parameters (Shareable):**
- `?q=authentication` - Search query
- `?community=auth-security` - Community filter
- `?type=pdf,md` - Type filter
- `?entities=oauth,jwt` - Entity filter
- `?view=grid` - View mode
- `?files=id1,id2,id3` - Pre-selected files (for chat links)

---

## API Endpoints Required

```
GET  /api/explorer/search?q={query}&filters={json}
GET  /api/explorer/communities
GET  /api/explorer/entities?type={type}
GET  /api/explorer/signals
GET  /api/explorer/collections
POST /api/explorer/collections  (create)
PUT  /api/explorer/collections/{id}/files  (add files)
DELETE /api/explorer/collections/{id}/files/{fileId}
POST /api/explorer/smart-collections  (create with rules)
GET  /api/explorer/files/{id}/preview
POST /api/explorer/chat  (chat with selected files)
```

---

## Implementation Phases

### Phase 1: Core Explorer
- [ ] Search bar with Sentinel LLM integration
- [ ] Results list view with selection
- [ ] Basic sidebar with communities and entities
- [ ] Filter chips display

### Phase 2: Collections
- [ ] Manual collections (CRUD)
- [ ] Drag-and-drop file organization
- [ ] Multi-collection file support

### Phase 3: Advanced Filtering
- [ ] Signal-based filters
- [ ] Date range filters
- [ ] Quality score filter
- [ ] Smart collections with rules

### Phase 4: Chat Integration
- [ ] Selection → Chat mode transition
- [ ] Collapsible sidebar animation
- [ ] File context management in chat

### Phase 5: Polish
- [ ] Grid and table views
- [ ] Keyboard navigation
- [ ] URL state persistence
- [ ] Upload interface improvements

---

## Design Tokens

```css
/* Explorer-specific */
--explorer-sidebar-width: 280px;
--explorer-sidebar-collapsed: 48px;
--explorer-transition-speed: 200ms;

/* Result cards */
--result-card-padding: 1rem;
--result-snippet-lines: 2;
--result-hover-bg: var(--base-200);

/* Filter chips */
--chip-type-bg: oklch(0.85 0.1 240);      /* Blue */
--chip-community-bg: oklch(0.85 0.1 150); /* Green */
--chip-entity-bg: oklch(0.85 0.1 300);    /* Purple */
--chip-signal-bg: oklch(0.85 0.1 30);     /* Orange */
```

---

## Component Hierarchy

```
FileExplorer/
├── ExplorerHeader/
│   ├── SearchBar (command palette)
│   └── ViewControls
├── ExplorerSidebar/
│   ├── CollectionsTree
│   ├── CommunitiesList
│   ├── EntitiesAccordion
│   ├── SignalsFilters
│   └── UploadButton
├── FilterChipsBar/
├── ResultsArea/
│   ├── ListView
│   ├── GridView
│   └── TableView
├── SelectionBar/
└── ChatMode/
    ├── FileContextChips
    └── ChatInterface
```
