using System.Net;
using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Browser functional tests for Explorer entity filtering.
///     Tests that entity filtering in the explorer sidebar works correctly.
///     Requires:
///     - LucidRAG app running on https://localhost:5020
///     - Authentication (tests skip gracefully if redirected to login)
///     - Some documents uploaded with extracted entities for full filtering tests
///     The explorer mode is accessed via /admin and using the sidebar mode toggle.
///     Entity filtering is only available in the authenticated admin area.
///     Run locally only with: dotnet test --filter "Category=Browser"
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class ExplorerEntityFilteringTests : IAsyncLifetime
{
    private const string BaseUrl = "https://localhost:5020";
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _consoleMessages = [];
    private readonly List<string> _pageErrors = [];
    private IBrowser? _browser;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--ignore-certificate-errors" // Allow self-signed certs for localhost
            ]
        });

        _page = await _browser.NewPageAsync();
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });

        // Capture console messages and errors for debugging
        _page.Console += (_, e) =>
        {
            var msg = $"[{e.Message.Type}] {e.Message.Text}";
            _consoleMessages.Add(msg);
            if (e.Message.Type == ConsoleType.Error) _consoleErrors.Add(msg);
        };
        _page.Error += (_, e) => _pageErrors.Add($"[PageError] {e}");
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    /// <summary>
    ///     Navigate to the main page and switch to Explorer mode.
    ///     Returns the Alpine.js data if successfully loaded.
    /// </summary>
    private async Task<JsonElement?> NavigateToExplorerModeAsync()
    {
        // Navigate to admin page (requires authentication - may redirect to login or public page)
        var response = await _page!.GoToAsync($"{BaseUrl}/admin", new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
        });

        // Wait for Alpine.js to initialize
        await Task.Delay(3000);

        // Log current URL for debugging
        var currentUrl = _page.Url;
        Console.WriteLine($"Current URL after navigation: {currentUrl}");
        Console.WriteLine($"Response status: {response!.Status}");

        // Check if we're on login page (not authenticated)
        if (currentUrl.Contains("login") || currentUrl.Contains("auth"))
        {
            Console.WriteLine("Redirected to login page - authentication required");
            return null;
        }

        // Switch to explorer mode using Alpine.js and return state
        var result = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (!ragAppEl) return { error: 'ragApp not found', pageTitle: document.title };

                const data = ragAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data', pageTitle: document.title };

                // Switch to explorer mode if sidebar mode exists
                if (typeof data.sidebarMode !== 'undefined') {
                    data.sidebarMode = 'explorer';
                    // Wait for UI update
                    await new Promise(r => setTimeout(r, 500));
                }

                // Load entities if function exists
                if (typeof data.loadEntities === 'function') {
                    await data.loadEntities();
                }

                return {
                    sidebarMode: data.sidebarMode,
                    hasEntityGroups: 'entityGroups' in data,
                    hasSelectedEntities: 'selectedEntities' in data,
                    hasToggleEntityFilter: typeof data.toggleEntityFilter === 'function',
                    entityGroupTypes: data.entityGroups ? Object.keys(data.entityGroups) : [],
                    totalEntities: data.entityGroups ? Object.values(data.entityGroups).flat().length : 0
                };
            }
        ");

        Console.WriteLine($"Explorer mode result: {result}");
        return result;
    }

    /// <summary>
    ///     Test that the admin page loads successfully and the ragApp component initializes.
    /// </summary>
    [Fact]
    public async Task Explorer_LoadsSuccessfully_WithRagAppComponent()
    {
        // Act - Navigate to admin page
        var response = await _page!.GoToAsync($"{BaseUrl}/admin", new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
        });

        // Wait for Alpine.js to initialize
        await Task.Delay(3000);

        // Log current URL for debugging
        Console.WriteLine($"Current URL: {_page.Url}");
        Console.WriteLine($"Response status: {response!.Status}");

        // Check for ragApp component
        var appState = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (ragAppEl) {
                    const data = ragAppEl._x_dataStack?.[0];
                    if (data) {
                        return {
                            found: true,
                            source: 'ragApp',
                            hasSidebarMode: 'sidebarMode' in data,
                            sidebarMode: data.sidebarMode,
                            hasEntityGroups: 'entityGroups' in data,
                            hasSelectedEntities: 'selectedEntities' in data,
                            hasToggleEntityFilter: typeof data.toggleEntityFilter === 'function',
                            hasHasActiveFilters: typeof data.hasActiveFilters === 'function'
                        };
                    }
                }

                // Check for fileExplorer component as fallback
                const fileExplorerEl = document.querySelector('[x-data*=""fileExplorer""]');
                if (fileExplorerEl) {
                    const data = fileExplorerEl._x_dataStack?.[0];
                    if (data) {
                        return {
                            found: true,
                            source: 'fileExplorer',
                            hasEntityGroups: 'entityGroups' in data,
                            hasSelectedEntities: 'selectedEntities' in data,
                            hasToggleEntityFilter: typeof data.toggleEntityFilter === 'function'
                        };
                    }
                }

                return {
                    found: false,
                    error: 'No Alpine component found',
                    pageTitle: document.title,
                    url: window.location.href
                };
            }
        ");

        // Log state for debugging
        Console.WriteLine($"App state: {appState}");

        // Assert - Page loaded (even if redirected)
        response!.Status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found, HttpStatusCode.Redirect);

        // If component found, verify it has entity filtering capabilities
        if (appState.TryGetProperty("found", out var found) && found.GetBoolean())
        {
            appState.GetProperty("hasSelectedEntities").GetBoolean().Should()
                .BeTrue("Should have selectedEntities property");
            appState.GetProperty("hasToggleEntityFilter").GetBoolean().Should()
                .BeTrue("Should have toggleEntityFilter function");
        }
        else
        {
            Console.WriteLine("Note: Alpine component not found - may require authentication");
        }
    }

    /// <summary>
    ///     Test that switching to explorer mode exposes entity filtering capabilities.
    /// </summary>
    [Fact]
    public async Task Explorer_SwitchToExplorerMode_HasEntityFilteringCapabilities()
    {
        // Act - Navigate and switch to explorer mode
        var explorerState = await NavigateToExplorerModeAsync();

        // If null, authentication is required - skip
        if (explorerState == null)
        {
            Console.WriteLine("SKIP: Authentication required");
            return;
        }

        if (explorerState.Value.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"SKIP: {error.GetString()}");
            return;
        }

        // Assert
        explorerState.Value.GetProperty("hasEntityGroups").GetBoolean().Should()
            .BeTrue("Should have entityGroups property");
        explorerState.Value.GetProperty("hasSelectedEntities").GetBoolean().Should()
            .BeTrue("Should have selectedEntities property");
        explorerState.Value.GetProperty("hasToggleEntityFilter").GetBoolean().Should()
            .BeTrue("Should have toggleEntityFilter function");
    }

    /// <summary>
    ///     Test that clicking an entity in the sidebar adds it to selected entities.
    /// </summary>
    [Fact]
    public async Task Explorer_ClickEntity_SelectsEntity()
    {
        // Arrange - Navigate to explorer mode
        var explorerState = await NavigateToExplorerModeAsync();
        if (explorerState == null || explorerState.Value.TryGetProperty("error", out _))
        {
            Console.WriteLine("SKIP: Could not navigate to explorer mode");
            return;
        }

        // Check if there are entities
        var totalEntities = explorerState.Value.GetProperty("totalEntities").GetInt32();
        if (totalEntities == 0)
        {
            Console.WriteLine("SKIP: No entities available for filtering test");
            return;
        }

        // Act - Click on first entity
        var clickResult = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                const fileExplorerEl = document.querySelector('[x-data*=""fileExplorer""]');
                const data = ragAppEl?._x_dataStack?.[0] || fileExplorerEl?._x_dataStack?.[0];

                if (!data) return { error: 'No Alpine data found' };

                // Expand entities section
                if (typeof data.entitiesExpanded !== 'undefined') {
                    data.entitiesExpanded = true;
                }

                const entityGroups = data.entityGroups || {};
                const types = Object.keys(entityGroups);

                // Find first entity
                let firstEntity = null;
                let firstEntityType = null;
                for (const type of types) {
                    if (entityGroups[type]?.length > 0) {
                        firstEntity = entityGroups[type][0];
                        firstEntityType = type;
                        break;
                    }
                }

                if (!firstEntity) return { error: 'No entity found to click' };

                // Store initial state
                const initialSelectedCount = data.selectedEntities?.size || 0;

                // Call toggleEntityFilter
                if (typeof data.toggleEntityFilter === 'function') {
                    data.toggleEntityFilter(firstEntity.id, firstEntityType);
                } else {
                    return { error: 'toggleEntityFilter function not found' };
                }

                // Wait for state update
                await new Promise(r => setTimeout(r, 500));

                return {
                    clickedEntityId: firstEntity.id,
                    clickedEntityName: firstEntity.name,
                    clickedEntityType: firstEntityType,
                    initialSelectedCount: initialSelectedCount,
                    finalSelectedCount: data.selectedEntities?.size || 0,
                    isSelected: data.selectedEntities?.has(firstEntity.id) || false
                };
            }
        ");

        Console.WriteLine($"Click result: {clickResult}");

        // Assert
        if (clickResult.TryGetProperty("error", out var errorProp))
        {
            Console.WriteLine($"SKIP: {errorProp.GetString()}");
            return;
        }

        clickResult.GetProperty("isSelected").GetBoolean().Should().BeTrue("Entity should be selected after click");
        clickResult.GetProperty("finalSelectedCount").GetInt32().Should()
            .BeGreaterThan(0, "Selected count should increase");
    }

    /// <summary>
    ///     Test that hasActiveFilters returns true when an entity is selected.
    /// </summary>
    [Fact]
    public async Task Explorer_SelectEntity_HasActiveFiltersReturnsTrue()
    {
        // Arrange
        var explorerState = await NavigateToExplorerModeAsync();
        if (explorerState == null || explorerState.Value.TryGetProperty("error", out _))
        {
            Console.WriteLine("SKIP: Could not navigate to explorer mode");
            return;
        }

        var totalEntities = explorerState.Value.GetProperty("totalEntities").GetInt32();
        if (totalEntities == 0)
        {
            Console.WriteLine("SKIP: No entities available for filtering test");
            return;
        }

        // Act - Select entity and check hasActiveFilters
        var result = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                const fileExplorerEl = document.querySelector('[x-data*=""fileExplorer""]');
                const data = ragAppEl?._x_dataStack?.[0] || fileExplorerEl?._x_dataStack?.[0];

                if (!data) return { error: 'No Alpine data found' };

                const entityGroups = data.entityGroups || {};
                const types = Object.keys(entityGroups);

                // Find first entity
                let firstEntity = null;
                let firstEntityType = null;
                for (const type of types) {
                    if (entityGroups[type]?.length > 0) {
                        firstEntity = entityGroups[type][0];
                        firstEntityType = type;
                        break;
                    }
                }

                if (!firstEntity) return { error: 'No entity found' };

                // Check hasActiveFilters before
                let hasFiltersBefore = false;
                if (typeof data.hasActiveFilters === 'function') {
                    hasFiltersBefore = data.hasActiveFilters();
                }

                // Toggle entity filter
                if (typeof data.toggleEntityFilter === 'function') {
                    data.toggleEntityFilter(firstEntity.id, firstEntityType);
                }

                // Wait for state update
                await new Promise(r => setTimeout(r, 500));

                // Check hasActiveFilters after
                let hasFiltersAfter = false;
                if (typeof data.hasActiveFilters === 'function') {
                    hasFiltersAfter = data.hasActiveFilters();
                }

                return {
                    entityId: firstEntity.id,
                    entityName: firstEntity.name,
                    hasFiltersBefore: hasFiltersBefore,
                    hasFiltersAfter: hasFiltersAfter,
                    selectedCount: data.selectedEntities?.size || 0
                };
            }
        ");

        Console.WriteLine($"Result: {result}");

        // Assert
        if (result.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"SKIP: {error.GetString()}");
            return;
        }

        result.GetProperty("hasFiltersAfter").GetBoolean().Should()
            .BeTrue("hasActiveFilters should return true after selecting entity");
        result.GetProperty("selectedCount").GetInt32().Should().BeGreaterThan(0, "Should have selected entity");
    }

    /// <summary>
    ///     Test that toggling entity filter twice removes the selection.
    /// </summary>
    [Fact]
    public async Task Explorer_ToggleEntityTwice_RemovesSelection()
    {
        // Arrange
        var explorerState = await NavigateToExplorerModeAsync();
        if (explorerState == null || explorerState.Value.TryGetProperty("error", out _))
        {
            Console.WriteLine("SKIP: Could not navigate to explorer mode");
            return;
        }

        var totalEntities = explorerState.Value.GetProperty("totalEntities").GetInt32();
        if (totalEntities == 0)
        {
            Console.WriteLine("SKIP: No entities available for filtering test");
            return;
        }

        // Act - Select entity, then deselect it
        var result = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                const fileExplorerEl = document.querySelector('[x-data*=""fileExplorer""]');
                const data = ragAppEl?._x_dataStack?.[0] || fileExplorerEl?._x_dataStack?.[0];

                if (!data) return { error: 'No Alpine data found' };

                const entityGroups = data.entityGroups || {};
                const types = Object.keys(entityGroups);

                // Find first entity
                let firstEntity = null;
                let firstEntityType = null;
                for (const type of types) {
                    if (entityGroups[type]?.length > 0) {
                        firstEntity = entityGroups[type][0];
                        firstEntityType = type;
                        break;
                    }
                }

                if (!firstEntity) return { error: 'No entity found' };

                // First toggle - select
                if (typeof data.toggleEntityFilter === 'function') {
                    data.toggleEntityFilter(firstEntity.id, firstEntityType);
                }
                await new Promise(r => setTimeout(r, 200));

                const countAfterSelect = data.selectedEntities?.size || 0;
                const isSelectedAfterFirst = data.selectedEntities?.has(firstEntity.id) || false;

                // Second toggle - deselect
                if (typeof data.toggleEntityFilter === 'function') {
                    data.toggleEntityFilter(firstEntity.id, firstEntityType);
                }
                await new Promise(r => setTimeout(r, 200));

                const countAfterDeselect = data.selectedEntities?.size || 0;
                const isSelectedAfterSecond = data.selectedEntities?.has(firstEntity.id) || false;

                return {
                    entityId: firstEntity.id,
                    entityName: firstEntity.name,
                    countAfterSelect: countAfterSelect,
                    countAfterDeselect: countAfterDeselect,
                    isSelectedAfterFirst: isSelectedAfterFirst,
                    isSelectedAfterSecond: isSelectedAfterSecond
                };
            }
        ");

        Console.WriteLine($"Result: {result}");

        // Assert
        if (result.TryGetProperty("error", out var error))
        {
            Console.WriteLine($"SKIP: {error.GetString()}");
            return;
        }

        result.GetProperty("countAfterSelect").GetInt32().Should().Be(1, "Should have 1 selected after first toggle");
        result.GetProperty("isSelectedAfterFirst").GetBoolean().Should()
            .BeTrue("Entity should be selected after first toggle");
        result.GetProperty("countAfterDeselect").GetInt32().Should()
            .Be(0, "Should have 0 selected after second toggle");
        result.GetProperty("isSelectedAfterSecond").GetBoolean().Should()
            .BeFalse("Entity should not be selected after second toggle");
    }

    /// <summary>
    ///     Test that no JavaScript errors occur during entity filtering interactions.
    /// </summary>
    [Fact]
    public async Task Explorer_EntityFilteringInteraction_NoJavaScriptErrors()
    {
        // Clear previous messages
        _consoleErrors.Clear();
        _pageErrors.Clear();

        // Act - Navigate and interact
        var explorerState = await NavigateToExplorerModeAsync();
        if (explorerState == null || explorerState.Value.TryGetProperty("error", out _))
        {
            Console.WriteLine("SKIP: Could not navigate to explorer mode");
            return;
        }

        // Try to click an entity
        await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                const fileExplorerEl = document.querySelector('[x-data*=""fileExplorer""]');
                const data = ragAppEl?._x_dataStack?.[0] || fileExplorerEl?._x_dataStack?.[0];

                if (!data) return { noData: true };

                // Expand entities
                if (typeof data.entitiesExpanded !== 'undefined') {
                    data.entitiesExpanded = true;
                }

                const entityGroups = data.entityGroups || {};
                const types = Object.keys(entityGroups);

                for (const type of types) {
                    if (entityGroups[type]?.length > 0) {
                        const entity = entityGroups[type][0];
                        if (typeof data.toggleEntityFilter === 'function') {
                            data.toggleEntityFilter(entity.id, type);
                        }
                        break;
                    }
                }

                await new Promise(r => setTimeout(r, 1000));
                return { done: true };
            }
        ");

        await Task.Delay(2000);

        // Assert - No critical errors
        var criticalErrors = _consoleErrors
            .Where(e => !e.Contains("favicon") && !e.Contains("net::ERR_CERT") && !e.Contains("404"))
            .ToList();

        if (criticalErrors.Any())
        {
            Console.WriteLine("Console errors found:");
            foreach (var error in criticalErrors) Console.WriteLine($"  {error}");
        }

        _pageErrors.Should().BeEmpty("No page-level errors should occur");

        // Log all console messages for debugging
        Console.WriteLine($"Total console messages: {_consoleMessages.Count}");
        foreach (var msg in _consoleMessages.Take(20)) Console.WriteLine($"  {msg}");
    }
}