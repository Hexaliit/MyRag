# StorageService Partial Class Refactoring

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Split the 1400-line `StorageService.cs` into 7 focused partial class files without changing any public API.

**Architecture:** Use C# `partial class` to split `StorageService` by responsibility domain. Each file contains methods that operate on related database tables. All files share the same `_connection` field via the partial class mechanism. No callers change.

**Tech Stack:** C# partial classes, SQLite (Microsoft.Data.Sqlite)

---

### Task 1: Create `StorageService.EntityGraph.cs`

**Files:**
- Create: `Services/StorageService.EntityGraph.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move (lines 378–631):**
- `UpsertEntityAsync`
- `UpsertEntityMentionAsync`
- `UpsertRelationshipAsync`
- `GetTopEntitiesAsync`
- `GetRelationshipsAsync`
- `GetArticlesForEntityAsync`
- `GetEntitiesForItemsAsync`
- `GetGraphStatsAsync`
- `FindRelatedByEntitiesAsync` (lines 1108–1154)

---

### Task 2: Create `StorageService.QueryFeedback.cs`

**Files:**
- Create: `Services/StorageService.QueryFeedback.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move (lines 638–773):**
- `LogQueryAsync`
- `FindSimilarQueryAsync`
- `GetItemsByIdsAsync`
- `GetItemUsageAsync`

---

### Task 3: Create `StorageService.Cache.cs`

**Files:**
- Create: `Services/StorageService.Cache.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move (lines 781–938):**
- `GetCachedFeatureEmbeddingAsync`
- `UpsertFeatureCacheAsync`
- `GetUrlCacheAsync`
- `IsUrlFreshAsync`
- `UpdateUrlCacheAsync`
- `IsContentUnchangedAsync`
- `GetAllUrlCacheEntriesAsync`
- `NormalizeCacheUrl` (private static helper)

---

### Task 4: Create `StorageService.Fts.cs`

**Files:**
- Create: `Services/StorageService.Fts.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move (lines 946–1194):**
- `IndexDocumentFtsAsync`
- `FtsPreFilterAsync`
- `BuildFtsQuery` (private static)
- `EscapeFtsToken` (private static)
- `UpdateKeywordCorpusAsync`
- `GetKeywordCorpusAsync`
- `GetKeywordCorpusSizeAsync`
- `LoadItemsByIdsAsync`
- `GetAllItemsAsync`
- `IsFtsIndexEmptyAsync`

---

### Task 5: Create `StorageService.Analytics.cs`

**Files:**
- Create: `Services/StorageService.Analytics.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move:**
- `GetTrendAnalysisAsync` (lines 292–357)
- `SaveSummaryAsync` (lines 359–371)
- `GetCollectionsAsync` (lines 1310–1341)
- `GetItemsBySourceAsync` (lines 1346–1361)
- `ClearAllAsync` (lines 1200–1232)
- `CleanupOldDataAsync` (lines 1234–1271)

---

### Task 6: Create `StorageService.Items.cs`

**Files:**
- Create: `Services/StorageService.Items.cs`
- Modify: `Services/StorageService.cs` (remove moved methods)

**Methods to move:**
- `GetRecentItemsAsync` (lines 233–252)
- `FindSimilarAsync` (lines 254–290)

---

### Task 7: Verify

- `dotnet build -c Release` — 0 errors, 0 warnings
- Verify no orphan methods remain in the wrong file
- Core file retains: class declaration, fields, constructor, `InitializeAsync`, `ExistsAsync`, `ExistsRecentlyAsync`, `SaveItemAsync`, `ReadStoredItem`, `DisposeAsync`, record types

## Verification

```bash
dotnet build -c Release
```
Expected: 0 errors, 0 warnings. No behavioral changes.
