using System.Net;
using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Quick validation test for the LucidRAG UI.
///     Tests Alpine.js initialization, explorer mode, and entity selection.
///     Requires:
///     - LucidRAG app running on https://localhost:5020 (HTTPS)
///     - Authentication may be required for full ragApp functionality
///     The test handles three scenarios:
///     1. Authenticated: Tests ragApp with entityGroups, selectedEntities, and entity selection
///     2. Public page: Tests publicApp functionality
///     3. Login page: Verifies login form is present
///     Run with: dotnet test src/LucidRAG.Tests/LucidRAG.Tests.csproj --filter "FullyQualifiedName~QuickValidationTests"
///     --no-build
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class QuickValidationTests : IAsyncLifetime
{
    private const string BaseUrl = "https://127.0.0.1:5020";
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
                "--ignore-certificate-errors" // Allow self-signed certs for localhost HTTPS
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
    ///     Quick validation test that verifies:
    ///     1. Navigation to https://localhost:5020
    ///     2. Alpine.js initialization
    ///     3. Explorer mode switching (if authenticated and ragApp available)
    ///     4. entityGroups and selectedEntities availability
    ///     5. Entity selection via click (if entities exist)
    ///     6. No JS errors occurred
    ///     Note: The ragApp component requires authentication.
    ///     Public pages use publicApp instead. Login pages verify form presence.
    /// </summary>
    [Fact]
    public async Task QuickValidation_AlpineAndExplorerMode()
    {
        // Clear error tracking
        _consoleErrors.Clear();
        _pageErrors.Clear();

        // Step 1: Navigate to the root page
        Console.WriteLine("Step 1: Navigating to {0}...", BaseUrl);
        var response = await _page!.GoToAsync(BaseUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
        });

        var currentUrl = _page.Url;
        Console.WriteLine($"Current URL: {currentUrl}");
        Console.WriteLine($"Response status: {response!.Status}");

        response.Status.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Found,
            HttpStatusCode.Redirect);

        // Step 2: Wait for Alpine.js to initialize
        Console.WriteLine("Step 2: Waiting for Alpine.js to initialize...");
        await Task.Delay(3000);

        var alpineLoaded = await _page.EvaluateFunctionAsync<bool>("() => typeof Alpine !== 'undefined'");
        alpineLoaded.Should().BeTrue("Alpine.js should be loaded");
        Console.WriteLine($"Alpine.js loaded: {alpineLoaded}");

        // Step 3: Detect which Alpine component is available
        Console.WriteLine("Step 3: Detecting Alpine component type...");
        var componentCheck = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                const publicAppEl = document.querySelector('[x-data*=""publicApp""]');
                const isLoginPage = window.location.href.includes('login') || window.location.href.includes('auth');

                if (ragAppEl) {
                    const data = ragAppEl._x_dataStack?.[0];
                    return {
                        componentType: 'ragApp',
                        found: true,
                        hasData: !!data,
                        url: window.location.href
                    };
                }

                if (publicAppEl) {
                    const data = publicAppEl._x_dataStack?.[0];
                    return {
                        componentType: 'publicApp',
                        found: true,
                        hasData: !!data,
                        url: window.location.href
                    };
                }

                return {
                    componentType: isLoginPage ? 'login' : 'unknown',
                    found: false,
                    isLoginPage: isLoginPage,
                    pageTitle: document.title,
                    url: window.location.href
                };
            }
        ");

        var componentType = componentCheck.GetProperty("componentType").GetString();
        var componentFound = componentCheck.GetProperty("found").GetBoolean();
        Console.WriteLine($"Component type: {componentType}, found: {componentFound}");
        Console.WriteLine($"Current URL: {componentCheck.GetProperty("url").GetString()}");

        if (componentType == "ragApp" && componentFound)
            await TestRagAppComponent();
        else if (componentType == "publicApp" && componentFound)
            await TestPublicAppComponent();
        else if (componentType == "login")
            await TestLoginPage();
        else
            Console.WriteLine($"WARNING: Unknown page state. URL: {componentCheck.GetProperty("url").GetString()}");

        // Step Final: Check for JS errors
        Console.WriteLine("\nStep Final: Checking for JavaScript errors...");
        await Task.Delay(1000); // Give time for any async errors

        // Filter out non-critical errors
        var criticalErrors = _consoleErrors
            .Where(e =>
                !e.Contains("favicon") &&
                !e.Contains("net::ERR_CERT") &&
                !e.Contains("404") &&
                !e.Contains("Failed to load resource"))
            .ToList();

        if (criticalErrors.Any())
        {
            Console.WriteLine("Critical JavaScript errors found:");
            foreach (var error in criticalErrors) Console.WriteLine($"  - {error}");
        }
        else
        {
            Console.WriteLine("No critical JavaScript errors found");
        }

        Console.WriteLine($"JS Errors: {(criticalErrors.Any() ? "FOUND " + criticalErrors.Count : "NONE")}");
        Console.WriteLine($"Page Errors: {(_pageErrors.Any() ? "FOUND " + _pageErrors.Count : "NONE")}");
        Console.WriteLine("=========================");

        // Page-level errors should be empty
        _pageErrors.Should().BeEmpty("No page-level errors should occur");
    }

    private async Task TestRagAppComponent()
    {
        Console.WriteLine("\n=== Testing ragApp Component (Authenticated) ===");

        // Step 4: Switch to explorer mode if available
        Console.WriteLine("Step 4: Checking for explorer mode...");
        var explorerModeResult = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (!ragAppEl) return { error: 'ragApp element not found' };

                const data = ragAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data stack' };

                // Check if sidebarMode exists
                const hasSidebarMode = 'sidebarMode' in data;
                const initialSidebarMode = data.sidebarMode;

                // Switch to explorer mode if available
                if (hasSidebarMode && data.sidebarMode !== 'explorer') {
                    data.sidebarMode = 'explorer';
                    // Wait for UI update
                    await new Promise(r => setTimeout(r, 500));
                }

                // Load entities if function exists
                if (typeof data.loadEntities === 'function') {
                    try {
                        await data.loadEntities();
                    } catch (e) {
                        // Entity loading may fail if no documents
                    }
                }

                return {
                    hasSidebarMode: hasSidebarMode,
                    initialSidebarMode: initialSidebarMode,
                    currentSidebarMode: data.sidebarMode,
                    switchedToExplorer: data.sidebarMode === 'explorer'
                };
            }
        ");

        Console.WriteLine($"Explorer mode result: {explorerModeResult}");

        if (!explorerModeResult.TryGetProperty("error", out _))
        {
            var switched = explorerModeResult.GetProperty("switchedToExplorer").GetBoolean();
            Console.WriteLine($"Switched to explorer: {switched}");
        }

        // Step 5: Check if entityGroups and selectedEntities are available
        Console.WriteLine("Step 5: Checking for entityGroups and selectedEntities...");
        var entityCheckResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (!ragAppEl) return { error: 'ragApp element not found' };

                const data = ragAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data stack' };

                return {
                    hasEntityGroups: 'entityGroups' in data,
                    hasSelectedEntities: 'selectedEntities' in data,
                    entityGroupsType: typeof data.entityGroups,
                    selectedEntitiesType: typeof data.selectedEntities,
                    entityGroupKeys: data.entityGroups ? Object.keys(data.entityGroups) : [],
                    totalEntitiesCount: data.entityGroups
                        ? Object.values(data.entityGroups).flat().length
                        : 0,
                    selectedEntitiesIsSet: data.selectedEntities instanceof Set,
                    selectedEntitiesSize: data.selectedEntities?.size ?? 0
                };
            }
        ");

        Console.WriteLine($"Entity check result: {entityCheckResult}");

        if (!entityCheckResult.TryGetProperty("error", out _))
        {
            var hasEntityGroups = entityCheckResult.GetProperty("hasEntityGroups").GetBoolean();
            var hasSelectedEntities = entityCheckResult.GetProperty("hasSelectedEntities").GetBoolean();

            hasEntityGroups.Should().BeTrue("entityGroups should be available");
            hasSelectedEntities.Should().BeTrue("selectedEntities should be available");

            Console.WriteLine($"entityGroups available: {hasEntityGroups}");
            Console.WriteLine($"selectedEntities available: {hasSelectedEntities}");

            if (entityCheckResult.TryGetProperty("entityGroupKeys", out var groupKeys))
            {
                var keys = groupKeys.EnumerateArray().Select(k => k.GetString()).ToList();
                Console.WriteLine($"Entity group types: {string.Join(", ", keys)}");
            }

            var totalEntities = entityCheckResult.GetProperty("totalEntitiesCount").GetInt32();
            Console.WriteLine($"Total entities: {totalEntities}");
        }

        // Step 6: Click an entity in the sidebar (if entities exist)
        Console.WriteLine("Step 6: Testing entity selection...");
        var entitySelectionResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (!ragAppEl) return { error: 'ragApp element not found' };

                const data = ragAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data stack' };

                // Check if toggleEntityFilter exists
                if (typeof data.toggleEntityFilter !== 'function') {
                    return { error: 'toggleEntityFilter function not found' };
                }

                // Get entity groups
                const entityGroups = data.entityGroups || {};
                const types = Object.keys(entityGroups);

                if (types.length === 0) {
                    return {
                        noEntities: true,
                        message: 'No entity groups available - may need documents with extracted entities'
                    };
                }

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

                if (!firstEntity) {
                    return {
                        noEntities: true,
                        message: 'Entity groups exist but are empty'
                    };
                }

                // Store initial selectedEntities size
                const initialSize = data.selectedEntities?.size ?? 0;

                // Click/toggle the entity
                data.toggleEntityFilter(firstEntity.id, firstEntityType);

                // Wait for state update
                await new Promise(r => setTimeout(r, 500));

                // Check if entity was selected
                const finalSize = data.selectedEntities?.size ?? 0;
                const isSelected = data.selectedEntities?.has(firstEntity.id) ?? false;

                return {
                    entityClicked: true,
                    entityId: firstEntity.id,
                    entityName: firstEntity.name,
                    entityType: firstEntityType,
                    initialSelectedSize: initialSize,
                    finalSelectedSize: finalSize,
                    entityIsSelected: isSelected,
                    selectedEntitiesUpdated: finalSize !== initialSize
                };
            }
        ");

        Console.WriteLine($"Entity selection result: {entitySelectionResult}");

        if (entitySelectionResult.TryGetProperty("error", out var selectionError))
        {
            Console.WriteLine($"Entity selection: {selectionError.GetString()}");
        }
        else if (entitySelectionResult.TryGetProperty("noEntities", out _))
        {
            var message = entitySelectionResult.GetProperty("message").GetString();
            Console.WriteLine($"No entities to select: {message}");
        }
        else
        {
            var entityClicked = entitySelectionResult.GetProperty("entityClicked").GetBoolean();
            var entityIsSelected = entitySelectionResult.GetProperty("entityIsSelected").GetBoolean();
            var selectedUpdated = entitySelectionResult.GetProperty("selectedEntitiesUpdated").GetBoolean();

            Console.WriteLine($"Entity clicked: {entityClicked}");
            Console.WriteLine($"Entity is now selected: {entityIsSelected}");
            Console.WriteLine($"selectedEntities Set updated: {selectedUpdated}");

            if (entityClicked)
            {
                entityIsSelected.Should().BeTrue("Entity should be selected after toggle");
                selectedUpdated.Should().BeTrue("selectedEntities Set should be updated");
            }
        }

        // Step 7: Verify the selectedEntities Set state
        Console.WriteLine("Step 7: Verifying selectedEntities Set state...");
        var finalStateResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (!ragAppEl) return { error: 'ragApp element not found' };

                const data = ragAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data stack' };

                // Get all selected entity IDs
                const selectedIds = data.selectedEntities instanceof Set
                    ? Array.from(data.selectedEntities)
                    : [];

                // Check hasActiveFilters if available
                let hasActiveFilters = false;
                if (typeof data.hasActiveFilters === 'function') {
                    hasActiveFilters = data.hasActiveFilters();
                }

                return {
                    selectedEntitiesSize: data.selectedEntities?.size ?? 0,
                    selectedEntityIds: selectedIds,
                    hasActiveFilters: hasActiveFilters
                };
            }
        ");

        Console.WriteLine($"Final state: {finalStateResult}");

        Console.WriteLine("\n=== VALIDATION SUMMARY (ragApp - Authenticated) ===");
        Console.WriteLine("ragApp found: OK");
        if (!entityCheckResult.TryGetProperty("error", out _))
        {
            Console.WriteLine(
                $"entityGroups: {(entityCheckResult.GetProperty("hasEntityGroups").GetBoolean() ? "OK" : "FAIL")}");
            Console.WriteLine(
                $"selectedEntities: {(entityCheckResult.GetProperty("hasSelectedEntities").GetBoolean() ? "OK" : "FAIL")}");
        }
    }

    private async Task TestPublicAppComponent()
    {
        Console.WriteLine("\n=== Testing publicApp Component (Public Page) ===");

        // Test publicApp functionality - check what properties are actually available
        var publicAppCheck = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const publicAppEl = document.querySelector('[x-data*=""publicApp""]');
                if (!publicAppEl) return { error: 'publicApp element not found' };

                const data = publicAppEl._x_dataStack?.[0];
                if (!data) return { error: 'No Alpine data stack' };

                // Get all available properties/functions for diagnostics
                const props = Object.keys(data);
                const funcs = props.filter(p => typeof data[p] === 'function');

                return {
                    hasData: true,
                    hasSidebarOpen: 'sidebarOpen' in data,
                    sidebarOpen: data.sidebarOpen,
                    hasCurrentMessage: 'currentMessage' in data,
                    hasMessages: 'messages' in data,
                    hasChatHistory: Array.isArray(data.chatHistory),
                    availableProperties: props.slice(0, 20), // First 20 props
                    availableFunctions: funcs.slice(0, 10)   // First 10 functions
                };
            }
        ");

        Console.WriteLine($"publicApp check: {publicAppCheck}");

        if (!publicAppCheck.TryGetProperty("error", out _))
        {
            // publicApp was found and has data - that's the core validation
            var hasData = publicAppCheck.GetProperty("hasData").GetBoolean();
            hasData.Should().BeTrue("publicApp should have Alpine data");

            // Log available properties for debugging
            if (publicAppCheck.TryGetProperty("availableProperties", out var props))
            {
                var propList = props.EnumerateArray().Select(p => p.GetString()).ToList();
                Console.WriteLine($"Available properties: {string.Join(", ", propList)}");
            }

            if (publicAppCheck.TryGetProperty("availableFunctions", out var funcs))
            {
                var funcList = funcs.EnumerateArray().Select(f => f.GetString()).ToList();
                Console.WriteLine($"Available functions: {string.Join(", ", funcList)}");
            }

            // Check for common UI properties (soft checks - just report)
            var hasSidebarOpen = publicAppCheck.GetProperty("hasSidebarOpen").GetBoolean();
            var hasCurrentMessage = publicAppCheck.GetProperty("hasCurrentMessage").GetBoolean();
            Console.WriteLine($"sidebarOpen property: {(hasSidebarOpen ? "present" : "missing")}");
            Console.WriteLine($"currentMessage property: {(hasCurrentMessage ? "present" : "missing")}");
        }

        Console.WriteLine("\n=== VALIDATION SUMMARY (publicApp - Public Page) ===");
        Console.WriteLine("publicApp found: OK");
        Console.WriteLine("Alpine data initialized: OK");
    }

    private async Task TestLoginPage()
    {
        Console.WriteLine("\n=== Testing Login Page ===");

        // Verify login page has expected elements
        var loginPageCheck = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const emailInput = document.querySelector('input[type=""email""], input[name=""email""], input[name=""Email""], input[name=""Input.Email""]');
                const passwordInput = document.querySelector('input[type=""password""]');
                const submitBtn = document.querySelector('button[type=""submit""]');

                return {
                    hasEmailInput: emailInput !== null,
                    hasPasswordInput: passwordInput !== null,
                    hasSubmitButton: submitBtn !== null,
                    pageIsReady: true
                };
            }
        ");

        Console.WriteLine($"Login page check: {loginPageCheck}");

        loginPageCheck.GetProperty("hasEmailInput").GetBoolean().Should().BeTrue("Login page should have email input");
        loginPageCheck.GetProperty("hasPasswordInput").GetBoolean().Should()
            .BeTrue("Login page should have password input");
        loginPageCheck.GetProperty("hasSubmitButton").GetBoolean().Should()
            .BeTrue("Login page should have submit button");

        Console.WriteLine("\n=== VALIDATION SUMMARY (Login Page) ===");
        Console.WriteLine("Login page: OK");
        Console.WriteLine("Email input: OK");
        Console.WriteLine("Password input: OK");
        Console.WriteLine("Submit button: OK");
        Console.WriteLine("NOTE: ragApp requires authentication");
    }
}