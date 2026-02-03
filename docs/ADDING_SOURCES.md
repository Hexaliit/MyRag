# Adding a New Source Plugin

This guide covers how to add a new data source to DoomSummarizer, from YAML definition through to NuGet packaging.

## Architecture Overview

Sources follow a layered architecture:

```
sources.yaml           → Defines available sources, routing rules, topic keywords
Fetcher service        → Calls the external API, parses responses into ContentItems
Plugin adapter         → Implements ISourcePlugin, routes sub-params to fetcher
BuiltinPlugins.cs      → Registers adapter at startup
Standalone project     → Optional NuGet package for external distribution
```

All source data flows through `List<ContentItem>` — the universal return type.

## Quick Reference

| File | Purpose |
|------|---------|
| `src/DoomSummarizer.Core/Resources/sources.yaml` | Source definitions, routing, topic keywords |
| `src/DoomSummarizer.Core/Services/{Name}Fetcher.cs` | API integration logic |
| `src/DoomSummarizer.Core/Plugins/Adapters/{Name}Plugin.cs` | ISourcePlugin adapter |
| `src/DoomSummarizer.Core/Plugins/BuiltinPlugins.cs` | Plugin registration |
| `src/DoomSummarizer.Core/Plugins/ISourcePlugin.cs` | Plugin interface |
| `src/DoomSummarizer.Core/Plugins/SourcePluginMetadata.cs` | Metadata + capabilities |
| `src/DoomSummarizer.Core/Plugins/SourceFetchContext.cs` | Input context |
| `src/DoomSummarizer.Core/Models/ContentItem.cs` | Output model |
| `src/LucidRAG.LLM/Services/ApiRateLimiter.cs` | Rate limiting defaults |

---

## Step 1: Define the Source in YAML

Edit `src/DoomSummarizer.Core/Resources/sources.yaml` (and sync to `src/DoomSummarizer/Resources/sources.yaml`).

### Source Definition

Add to the `sources:` section:

```yaml
sources:
  # -- Your category comment --
  mysource:
    type: api                    # "api" for JSON APIs, "rss" for RSS/Atom feeds
    description: "Short description of what this source provides"
    search: true                 # Optional: supports query search
    scope: [technology, science] # Optional: topic categories it covers
    region: global               # Optional: UK, US, global
    # Sub-params: list available sub-parameters
    # Examples: -s mysource, -s mysource:subsection
```

For RSS sources, also define feeds:

```yaml
  mysource:
    type: rss
    description: "My RSS Source"
    feeds:
      default:
        - https://example.com/rss
      technology:
        - https://example.com/tech/rss
```

### Routing Rules

Add routing entries so topic detection can find your source:

```yaml
routing:
  my_topic:
    sources: [mysource, google_news, bbc]  # Order = priority
    bbc_category: technology               # Optional: BBC feed category
    google_news_topic: TECHNOLOGY          # Optional: Google News topic
```

**Specialist-first principle**: Put specialist sources before general ones in the routing list. This reduces noise — the system prefers sources earlier in the list.

### Topic Keywords

Add detection keywords so user queries route to your source:

```yaml
topic_keywords:
  my_topic: [keyword1, keyword2, specific phrase, another term]
```

---

## Step 2: Create the Fetcher Service

Create `src/DoomSummarizer.Core/Services/{Name}Fetcher.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches data from {API Name} — free, no auth.
/// {Brief description of what data it provides}.
/// {Link to API documentation}
/// </summary>
public class MySourceFetcher(HttpClient httpClient)
{
    private const string BaseUrl = "https://api.example.com/v1";
    private const string UserAgent = "DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)";

    /// <summary>
    /// Fetch items. Supports optional query search and sub-section routing.
    /// </summary>
    public async Task<List<ContentItem>> FetchAsync(
        int limit = 20, string? query = null, string? section = null)
    {
        var items = new List<ContentItem>();

        try
        {
            var url = $"{BaseUrl}/items?limit={Math.Min(limit, 50)}";
            if (!string.IsNullOrEmpty(query))
                url += $"&search={Uri.EscapeDataString(query)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ApiResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Items == null) return items;

            foreach (var item in result.Items.Take(limit))
            {
                if (string.IsNullOrEmpty(item.Title)) continue;

                items.Add(new ContentItem
                {
                    Id = $"mysource_{item.Id ?? GenerateId(item.Title)}",
                    Source = "mysource",        // Must match YAML key
                    Title = item.Title,
                    Url = item.Url ?? BaseUrl,
                    Content = item.Description ?? "",
                    Author = "Source Name",
                    CreatedAt = item.Date ?? DateTimeOffset.UtcNow,
                    Tags = ["tag1", "tag2"]
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: MySource API failed: {ex.Message}");
        }

        return items;
    }

    private static string GenerateId(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input))[..8]).ToLowerInvariant();

    // ── API response models (private records) ─────────────────────────
    private record ApiResponse(List<ApiItem>? Items);
    private record ApiItem(string? Id, string? Title, string? Url,
        string? Description, DateTimeOffset? Date);
}
```

### Fetcher Best Practices

- **User-Agent**: Always set `DoomSummarizer/1.0 (https://github.com/scottgal/lucidrag)`
- **Error handling**: Catch exceptions, log with `Debug.WriteLine`, return empty list
- **Limit enforcement**: Respect the `limit` parameter; don't fetch more than needed
- **Content size**: Truncate Content to ~2000 chars max
- **ID generation**: Use `SHA256.HashData` for deterministic, collision-resistant IDs
- **Response models**: Use private records for JSON deserialization
- **API models**: Use `[property: JsonPropertyName("snake_case")]` for JSON mapping

---

## Step 3: Create the Plugin Adapter

Create `src/DoomSummarizer.Core/Plugins/Adapters/{Name}Plugin.cs`:

```csharp
using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Plugins.Adapters;

/// <summary>
/// Adapts <see cref="MySourceFetcher"/> to the <see cref="ISourcePlugin"/> contract.
/// </summary>
public sealed class MySourcePlugin : ISourcePlugin
{
    private HttpClient _httpClient = null!;

    public SourcePluginMetadata Metadata { get; } = new()
    {
        PrimaryKey = "mysource",
        Keys = ["mysource", "alias1"],           // All keys this plugin responds to
        DisplayName = "My Source",
        Description = "Short description.",
        Capabilities = SourceCapabilities.Search | SourceCapabilities.NoAuth,
        Examples = ["-s mysource", "-s mysource:subsection"]
    };

    public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
    {
        _httpClient = services.HttpClient;
        return Task.CompletedTask;
    }

    public async Task<List<ContentItem>> FetchAsync(
        SourceFetchContext context, CancellationToken ct = default)
    {
        var fetcher = new MySourceFetcher(_httpClient);
        var section = context.SubParams.Count > 0 ? context.SubParams[0] : null;
        return await fetcher.FetchAsync(context.Limit, context.Query, section);
    }
}
```

### Metadata Fields

| Field | Required | Description |
|-------|----------|-------------|
| `PrimaryKey` | Yes | Main lookup key (e.g. `"hn"`, `"parliament"`) |
| `Keys` | Yes | All keys including aliases |
| `DisplayName` | Yes | Human-readable name |
| `Description` | Yes | Short description |
| `Capabilities` | No | Flags: `Search`, `Feed`, `NoAuth`, `SubSource`, etc. |
| `RequiredApiKeys` | No | API key names needed (empty = no auth) |
| `PackageId` | No | NuGet package ID for standalone distribution |
| `Scopes` | No | Sub-source scopes |
| `Examples` | No | CLI usage examples |

### Capability Flags

```csharp
[Flags]
public enum SourceCapabilities
{
    Search = 1,        // Supports keyword search
    TopicBrowse = 2,   // Supports browsing by category
    Feed = 4,          // RSS/Atom feeds
    NoAuth = 8,        // No API key required
    RequiresAuth = 16, // Needs API key
    SubSource = 32,    // Supports sub-params (e.g. subreddit)
    LocalFiles = 64,   // Reads local files
    NewsOnly = 128     // Primarily news content
}
```

---

## Step 4: Register the Plugin

Edit `src/DoomSummarizer.Core/Plugins/BuiltinPlugins.cs`:

```csharp
public static void RegisterAllSources(SourcePluginRegistry registry)
{
    // ... existing plugins ...
    registry.Register(new MySourcePlugin());
}
```

---

## Step 5: Add Rate Limiting

Edit `src/LucidRAG.LLM/Services/ApiRateLimiter.cs`, add to `DefaultDelayMs`:

```csharp
["mysource"] = 500,  // Comment describing the API's rate limit policy
```

### Rate Limit Guidelines

| API Type | Suggested Delay | Notes |
|----------|----------------|-------|
| Government / open data | 500ms | Be polite, no hard limits |
| Community APIs | 200-500ms | Respect the community |
| APIs with documented limits | Match their limit | e.g. arXiv = 3000ms |
| Paid APIs | 100-200ms | Typically generous limits |

---

## Step 6: Build and Test

```bash
# Build the solution
dotnet build LucidRAG.sln

# Run tests
dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj

# Test the source manually via CLI
dotnet run --project src/DoomSummarizer -- -s mysource -q "test query" --limit 5
```

---

## Optional: Standalone NuGet Package

For distributing the source as a separate NuGet package:

### Create Project

```
src/Mostlylucid.DoomSummarizer.Source.{Name}/
├── Mostlylucid.DoomSummarizer.Source.{Name}.csproj
└── {Name}SourcePlugin.cs
```

### .csproj Template

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <RootNamespace>DoomSummarizer.Sources.{Name}</RootNamespace>

        <IsPackable>true</IsPackable>
        <PackageId>Mostlylucid.LucidRAG.Sources.{Name}</PackageId>
        <Authors>Scott Galloway</Authors>
        <Company>Mostlylucid</Company>
        <Description>Description here.</Description>
        <PackageTags>doomsummarizer;plugin;source;{name}</PackageTags>
        <RepositoryUrl>https://github.com/scottgal/lucidrag</RepositoryUrl>
        <PackageLicenseExpression>MIT</PackageLicenseExpression>

        <IncludeSymbols>true</IncludeSymbols>
        <SymbolPackageFormat>snupkg</SymbolPackageFormat>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
        <NoWarn>$(NoWarn);1591</NoWarn>
        <Deterministic>true</Deterministic>
        <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    </PropertyGroup>

    <PropertyGroup>
        <MinVerTagPrefix>src{shortname}v</MinVerTagPrefix>
        <MinVerSkip Condition="'$(Configuration)' == 'Debug'">true</MinVerSkip>
    </PropertyGroup>

    <PropertyGroup Condition="'$(Configuration)' == 'Release'">
        <DebugSymbols>false</DebugSymbols>
        <DebugType>none</DebugType>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="MinVer" Version="8.0.0-alpha.1">
            <PrivateAssets>all</PrivateAssets>
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup Condition="'$(UseNuGetDependency)' != 'true'">
        <ProjectReference Include="..\DoomSummarizer.Core\DoomSummarizer.Core.csproj" />
    </ItemGroup>
    <ItemGroup Condition="'$(UseNuGetDependency)' == 'true'">
        <PackageReference Include="Mostlylucid.LucidRAG.DoomSummarizer.Core" Version="$(CorePackageVersion)" />
    </ItemGroup>
</Project>
```

### Add to Solution

```bash
dotnet sln LucidRAG.sln add src/Mostlylucid.DoomSummarizer.Source.{Name}/Mostlylucid.DoomSummarizer.Source.{Name}.csproj
```

### Plugin Discovery

Standalone plugins are discovered automatically at runtime via `PluginDiscovery.DiscoverSourcePlugins()` which scans loaded assemblies for `ISourcePlugin` implementations. The `PluginLoader` class can also download and load plugins from NuGet packages.

---

## SourceFetchContext Reference

The `SourceFetchContext` record is passed to every `FetchAsync` call:

| Property | Type | Description |
|----------|------|-------------|
| `RawSource` | `string` | Original source string (e.g. `"reddit:csharp"`) |
| `SourceKey` | `string` | Lowercase key for routing (e.g. `"reddit"`) |
| `SubParams` | `IReadOnlyList<string>` | Colon-separated params after key |
| `Query` | `string?` | Search query |
| `RawPrompt` | `string?` | User prompt text |
| `Limit` | `int` | Max items to fetch (default 20) |
| `Vibe` | `string` | Tone: `"doom"`, `"hopeful"`, `"neutral"` |
| `Progress` | `Action<string>?` | Progress callback |
| `Config` | `DoomConfig?` | Full configuration |

### Parse Examples

```
"hn"                    → SourceKey="hn",         SubParams=[]
"reddit:csharp"         → SourceKey="reddit",     SubParams=["csharp"]
"ukpolice:crime:51,-0"  → SourceKey="ukpolice",   SubParams=["crime", "51,-0"]
"parliament:divisions"  → SourceKey="parliament",  SubParams=["divisions"]
```

---

## ContentItem Reference

Every fetcher returns `List<ContentItem>`. Key fields to populate:

| Field | Required | Description |
|-------|----------|-------------|
| `Id` | Yes | Unique ID, format: `{source}_{hash}` |
| `Source` | Yes | Must match YAML key |
| `Title` | Yes | Display title |
| `Url` | No | Link to original content |
| `Content` | No | Full text or excerpt (max ~2000 chars) |
| `Author` | No | Content author |
| `Score` | No | Engagement metric (upvotes, severity, etc.) |
| `CreatedAt` | No | Publication/event time |
| `Tags` | No | Classification tags |
| `Metadata` | No | Structured key-value data |
| `ImageUrl` | No | Thumbnail/feature image |
| `CommentCount` | No | Discussion count |

---

## Existing Sources

| Key | API | Auth | Rate Limit |
|-----|-----|------|------------|
| `hn` | Hacker News (Firebase) | None | 200ms |
| `reddit` | Reddit | None | 1000ms |
| `wikipedia` / `wiki` | MediaWiki + REST | None | 200ms |
| `arxiv` | arXiv OAI | None | 3000ms |
| `spaceflight` | SNAPI v4 | None | 500ms |
| `earthquake` | USGS GeoJSON | None | 500ms |
| `parliament` | Hansard API | None | 500ms |
| `ukpolice` | data.police.uk | None | 500ms |
| `ukflood` | Environment Agency | None | 500ms |
| `factcheck` | Snopes/PolitiFact RSS | None | 500ms |
| `google_news` | Google News RSS | None | 200ms |
| `google_search` | Google Custom Search | API Key | 200ms |
| `stackoverflow` | SO API v2.3 | None | 1000ms |
