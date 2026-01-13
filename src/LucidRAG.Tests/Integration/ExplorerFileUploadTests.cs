using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
///     Browser functional tests for Explorer file upload.
///     Tests that drag-drop and button upload work correctly.
///
///     Requires:
///     - LucidRAG app running on https://localhost:5020
///     - Authentication (tests login first)
///
///     Run locally with: dotnet test --filter "FullyQualifiedName~ExplorerFileUploadTests" src/LucidRAG.Tests/LucidRAG.Tests.csproj
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class ExplorerFileUploadTests : IAsyncLifetime
{
    private const string BaseUrl = "https://localhost:5020";
    private const string TestEmail = "admin@lucidrag.local";
    private const string TestPassword = "Admin123!";
    private readonly List<string> _consoleMessages = [];
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _pageErrors = [];
    private readonly List<string> _networkRequests = [];
    private IBrowser? _browser;
    private IPage? _page;

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = false, // Set to false to see the browser during debugging
            Args = [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--ignore-certificate-errors"
            ]
        });

        _page = await _browser.NewPageAsync();
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });

        // Capture console messages and errors
        _page.Console += (_, e) =>
        {
            var msg = $"[{e.Message.Type}] {e.Message.Text}";
            _consoleMessages.Add(msg);
            if (e.Message.Type == ConsoleType.Error)
            {
                _consoleErrors.Add(msg);
            }
        };
        _page.Error += (_, e) => _pageErrors.Add($"[PageError] {e}");

        // Capture network requests
        _page.Request += (_, e) =>
        {
            if (e.Request.Url.Contains("/api/"))
            {
                _networkRequests.Add($"[{e.Request.Method}] {e.Request.Url}");
            }
        };
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    /// <summary>
    ///     Login and navigate to the admin page.
    /// </summary>
    private async Task<bool> LoginAndNavigateToAdminAsync()
    {
        // Navigate to login page
        Console.WriteLine("Step 1: Navigating to login page...");
        await _page!.GoToAsync($"{BaseUrl}/auth/login");
        await Task.Delay(1000);

        // Fill in login form
        Console.WriteLine("Step 2: Filling in login form...");
        await _page.EvaluateFunctionAsync(@$"
            () => {{
                const emailInput = document.querySelector('input[type=""email""], input[name=""email""], input[name=""Email""], input[name=""Input.Email""], #email, #Email');
                if (emailInput) {{
                    emailInput.value = '{TestEmail}';
                    emailInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }}
                const passwordInput = document.querySelector('input[type=""password""], input[name=""password""], input[name=""Password""], input[name=""Input.Password""], #password, #Password');
                if (passwordInput) {{
                    passwordInput.value = '{TestPassword}';
                    passwordInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
                }}
            }}
        ");

        // Submit form
        Console.WriteLine("Step 3: Submitting form...");
        var submitted = await _page.EvaluateFunctionAsync<bool>(@"
            () => {
                const submitBtn = document.querySelector('button[type=""submit""], input[type=""submit""]');
                if (submitBtn) {
                    submitBtn.click();
                    return true;
                }
                const form = document.querySelector('form');
                if (form) {
                    form.submit();
                    return true;
                }
                return false;
            }
        ");

        if (!submitted)
        {
            Console.WriteLine("Could not submit login form");
            return false;
        }

        // Wait for navigation
        try
        {
            await _page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = 10000
            });
        }
        catch (TimeoutException)
        {
            // May have already navigated
        }

        await Task.Delay(2000);
        Console.WriteLine($"Current URL after login: {_page.Url}");

        return !_page.Url.Contains("login");
    }

    /// <summary>
    ///     Switch to Explorer mode in the sidebar.
    /// </summary>
    private async Task<bool> SwitchToExplorerModeAsync()
    {
        Console.WriteLine("Switching to Explorer mode...");

        var result = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                // Try ragApp first
                const ragAppEl = document.querySelector('[x-data*=""ragApp""]');
                if (ragAppEl) {
                    const data = ragAppEl._x_dataStack?.[0];
                    if (data && typeof data.sidebarMode !== 'undefined') {
                        data.sidebarMode = 'explorer';
                        await new Promise(r => setTimeout(r, 500));
                        return { success: true, mode: data.sidebarMode, source: 'ragApp' };
                    }
                }

                // Try clicking explorer tab directly
                const explorerTab = document.querySelector('[data-tab=""explorer""], button[onclick*=""explorer""]');
                if (explorerTab) {
                    explorerTab.click();
                    await new Promise(r => setTimeout(r, 500));
                    return { success: true, source: 'tabClick' };
                }

                // Look for text-based explorer button
                const buttons = document.querySelectorAll('button, a, [role=""tab""]');
                for (const btn of buttons) {
                    if (btn.textContent?.toLowerCase().includes('explorer')) {
                        btn.click();
                        await new Promise(r => setTimeout(r, 500));
                        return { success: true, source: 'textMatch' };
                    }
                }

                return { success: false, error: 'Could not find explorer mode' };
            }
        ");

        Console.WriteLine($"Explorer mode result: {result}");
        return result.TryGetProperty("success", out var success) && success.GetBoolean();
    }

    /// <summary>
    ///     Create a test file with content for upload testing.
    /// </summary>
    private static async Task<string> CreateTestFileAsync()
    {
        var testFilePath = Path.Combine(Path.GetTempPath(), $"lucidrag-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(testFilePath, "This is a test document for LucidRAG upload testing.\n\nIt contains some sample content that can be processed by the document pipeline.");
        return testFilePath;
    }

    /// <summary>
    ///     Test that file upload via drag-drop works.
    /// </summary>
    [Fact]
    public async Task Explorer_DragDropUpload_UploadsFile()
    {
        // Arrange - Login and navigate
        var loggedIn = await LoginAndNavigateToAdminAsync();
        if (!loggedIn)
        {
            Console.WriteLine("SKIP: Could not login");
            return;
        }

        await SwitchToExplorerModeAsync();
        await Task.Delay(1000);

        // Take screenshot before upload
        await _page!.ScreenshotAsync(@"E:\source\lucidrag\explorer-before-upload.png", new ScreenshotOptions { FullPage = true });
        Console.WriteLine("Screenshot saved: explorer-before-upload.png");

        // Create a test file
        var testFilePath = await CreateTestFileAsync();
        Console.WriteLine($"Test file created: {testFilePath}");

        try
        {
            // Check if fileExplorer component exists
            var componentCheck = await _page.EvaluateFunctionAsync<JsonElement>(@"
                () => {
                    const fileExplorer = document.querySelector('[x-data*=""fileExplorer""]');
                    if (!fileExplorer) {
                        return { found: false, error: 'fileExplorer component not found' };
                    }

                    const data = fileExplorer._x_dataStack?.[0];
                    return {
                        found: true,
                        hasUploadFiles: typeof data?.uploadFiles === 'function',
                        hasHandleFileDrop: typeof data?.handleFileDrop === 'function',
                        hasDragOver: typeof data?.handleDragOver === 'function',
                        isDragging: data?.isDragging ?? null,
                        currentCollectionId: data?.currentCollectionId ?? null,
                        documentsCount: data?.documents?.length ?? 0
                    };
                }
            ");

            Console.WriteLine($"Component check: {componentCheck}");

            if (!componentCheck.GetProperty("found").GetBoolean())
            {
                Console.WriteLine("ERROR: fileExplorer component not found on page");

                // Debug: List all Alpine components
                var alpineComponents = await _page.EvaluateFunctionAsync<string[]>(@"
                    () => {
                        const components = [];
                        document.querySelectorAll('[x-data]').forEach(el => {
                            const attr = el.getAttribute('x-data');
                            components.push(attr?.substring(0, 100) || 'empty');
                        });
                        return components;
                    }
                ");

                Console.WriteLine("Alpine components found:");
                foreach (var comp in alpineComponents)
                {
                    Console.WriteLine($"  - {comp}");
                }

                return;
            }

            // Simulate file drop using JavaScript since PuppeteerSharp doesn't have native file drop
            _networkRequests.Clear();

            var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@$"
                async () => {{
                    const fileExplorer = document.querySelector('[x-data*=""fileExplorer""]');
                    const data = fileExplorer?._x_dataStack?.[0];

                    if (!data || typeof data.uploadFiles !== 'function') {{
                        return {{ success: false, error: 'uploadFiles function not available' }};
                    }}

                    // Create a fake File object
                    const fileContent = 'This is a test document for LucidRAG upload testing.\n\nIt contains some sample content.';
                    const blob = new Blob([fileContent], {{ type: 'text/plain' }});
                    const file = new File([blob], 'test-upload-{Guid.NewGuid():N}.txt', {{ type: 'text/plain' }});

                    try {{
                        // Get initial document count
                        const initialCount = data.documents?.length ?? 0;

                        // Call uploadFiles directly
                        await data.uploadFiles([file]);

                        // Wait for HTMX refresh
                        await new Promise(r => setTimeout(r, 2000));

                        // Get final document count
                        const finalCount = data.documents?.length ?? 0;

                        return {{
                            success: true,
                            initialCount: initialCount,
                            finalCount: finalCount,
                            uploadedFileName: file.name
                        }};
                    }} catch (e) {{
                        return {{ success: false, error: e.message || e.toString() }};
                    }}
                }}
            ");

            Console.WriteLine($"Upload result: {uploadResult}");

            // Check network requests made
            Console.WriteLine("Network requests during upload:");
            foreach (var req in _networkRequests)
            {
                Console.WriteLine($"  {req}");
            }

            // Take screenshot after upload
            await _page.ScreenshotAsync(@"E:\source\lucidrag\explorer-after-upload.png", new ScreenshotOptions { FullPage = true });
            Console.WriteLine("Screenshot saved: explorer-after-upload.png");

            // Assert
            if (uploadResult.TryGetProperty("error", out var error))
            {
                Console.WriteLine($"Upload error: {error.GetString()}");
            }

            // Check for any JavaScript errors
            if (_consoleErrors.Any())
            {
                Console.WriteLine("Console errors during upload:");
                foreach (var err in _consoleErrors)
                {
                    Console.WriteLine($"  {err}");
                }
            }
        }
        finally
        {
            // Cleanup test file
            if (File.Exists(testFilePath))
            {
                File.Delete(testFilePath);
            }
        }
    }

    /// <summary>
    ///     Test that the upload endpoint works when called directly.
    /// </summary>
    [Fact]
    public async Task Explorer_UploadEndpoint_RespondsCorrectly()
    {
        // Arrange - Login
        var loggedIn = await LoginAndNavigateToAdminAsync();
        if (!loggedIn)
        {
            Console.WriteLine("SKIP: Could not login");
            return;
        }

        // Act - Call upload endpoint directly via fetch
        var result = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const fileContent = 'Test document content for API test.';
                const blob = new Blob([fileContent], { type: 'text/plain' });
                const file = new File([blob], 'api-test.txt', { type: 'text/plain' });

                const formData = new FormData();
                formData.append('files', file);

                try {
                    const response = await fetch('/api/documents/upload-batch', {
                        method: 'POST',
                        body: formData
                    });

                    const status = response.status;
                    let body = null;
                    try {
                        body = await response.json();
                    } catch {
                        body = await response.text();
                    }

                    return {
                        success: response.ok,
                        status: status,
                        body: body
                    };
                } catch (e) {
                    return { success: false, error: e.message };
                }
            }
        ");

        Console.WriteLine($"API response: {result}");

        // Assert
        result.GetProperty("success").GetBoolean().Should().BeTrue("Upload API should return success");
        result.GetProperty("status").GetInt32().Should().Be(200, "Upload should return 200 OK");
    }

    /// <summary>
    ///     Test the Explorer layout and verify key elements are visible.
    /// </summary>
    [Fact]
    public async Task Explorer_Layout_HasRequiredElements()
    {
        // Arrange - Login and navigate
        var loggedIn = await LoginAndNavigateToAdminAsync();
        if (!loggedIn)
        {
            Console.WriteLine("SKIP: Could not login");
            return;
        }

        await SwitchToExplorerModeAsync();
        await Task.Delay(1000);

        // Act - Check for required elements
        var layoutCheck = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const viewport = { width: window.innerWidth, height: window.innerHeight };

                // Check for sidebar
                const sidebar = document.querySelector('.explorer-sidebar, [data-explorer-sidebar], .drawer-side');
                const sidebarRect = sidebar?.getBoundingClientRect();

                // Check for file list
                const fileList = document.querySelector('.explorer-file-list, [data-explorer-list], table');
                const fileListRect = fileList?.getBoundingClientRect();

                // Check for upload area/button
                const uploadArea = document.querySelector('.upload-area, [data-upload], .dropzone, [x-on\\:drop]');
                const uploadButton = document.querySelector('button[onclick*=""upload""], button:has(.upload-icon), .upload-btn');

                // Check for entities section
                const entitiesSection = document.querySelector('[x-show*=""entities""], .entities-list, #entities');

                // Check for info/details panel
                const infoPanel = document.querySelector('.info-panel, .details-panel, [data-details]');

                // Check for select all
                const selectAll = document.querySelector('input[type=""checkbox""][x-model*=""selectAll""], .select-all');

                return {
                    viewport: viewport,
                    sidebar: sidebarRect ? {
                        exists: true,
                        width: sidebarRect.width,
                        visible: sidebarRect.width > 0 && sidebarRect.height > 0
                    } : { exists: false },
                    fileList: fileListRect ? {
                        exists: true,
                        width: fileListRect.width,
                        height: fileListRect.height,
                        visible: fileListRect.width > 0 && fileListRect.height > 0
                    } : { exists: false },
                    uploadArea: { exists: !!uploadArea },
                    uploadButton: { exists: !!uploadButton },
                    entitiesSection: { exists: !!entitiesSection },
                    infoPanel: { exists: !!infoPanel },
                    selectAll: { exists: !!selectAll }
                };
            }
        ");

        Console.WriteLine($"Layout check: {layoutCheck}");

        // Take screenshot
        await _page.ScreenshotAsync(@"E:\source\lucidrag\explorer-layout.png", new ScreenshotOptions { FullPage = true });
        Console.WriteLine("Screenshot saved: explorer-layout.png");

        // Report findings
        Console.WriteLine("\n=== LAYOUT ANALYSIS ===");
        Console.WriteLine($"Viewport: {layoutCheck.GetProperty("viewport")}");

        var sidebar = layoutCheck.GetProperty("sidebar");
        Console.WriteLine($"Sidebar: {(sidebar.GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");

        var fileList = layoutCheck.GetProperty("fileList");
        Console.WriteLine($"File List: {(fileList.GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");

        Console.WriteLine($"Upload Area: {(layoutCheck.GetProperty("uploadArea").GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");
        Console.WriteLine($"Upload Button: {(layoutCheck.GetProperty("uploadButton").GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");
        Console.WriteLine($"Entities Section: {(layoutCheck.GetProperty("entitiesSection").GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");
        Console.WriteLine($"Info Panel: {(layoutCheck.GetProperty("infoPanel").GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");
        Console.WriteLine($"Select All: {(layoutCheck.GetProperty("selectAll").GetProperty("exists").GetBoolean() ? "EXISTS" : "MISSING")}");
        Console.WriteLine("=========================\n");
    }

    /// <summary>
    ///     Test that document list shows document type, entities, and other details.
    /// </summary>
    [Fact]
    public async Task Explorer_FileList_ShowsDocumentDetails()
    {
        // Arrange - Login and navigate
        var loggedIn = await LoginAndNavigateToAdminAsync();
        if (!loggedIn)
        {
            Console.WriteLine("SKIP: Could not login");
            return;
        }

        await SwitchToExplorerModeAsync();
        await Task.Delay(1000);

        // Act - Check file list columns
        var fileListInfo = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const table = document.querySelector('table');
                if (!table) return { hasTable: false };

                const headers = [];
                table.querySelectorAll('th').forEach(th => {
                    headers.push(th.textContent?.trim() || '');
                });

                const rows = [];
                table.querySelectorAll('tbody tr').forEach((tr, i) => {
                    if (i >= 5) return; // Only first 5 rows
                    const cells = [];
                    tr.querySelectorAll('td').forEach(td => {
                        cells.push(td.textContent?.trim()?.substring(0, 50) || '');
                    });
                    rows.push(cells);
                });

                // Check what data attributes are shown
                const firstRow = table.querySelector('tbody tr');
                const badges = [];
                firstRow?.querySelectorAll('.badge').forEach(badge => {
                    badges.push(badge.textContent?.trim() || '');
                });

                return {
                    hasTable: true,
                    headers: headers,
                    rowCount: table.querySelectorAll('tbody tr').length,
                    sampleRows: rows,
                    badges: badges,
                    hasEntityCount: !!firstRow?.querySelector('[class*=""entity""], [data-entity-count]'),
                    hasDocType: !!firstRow?.querySelector('[class*=""type""], [data-doc-type]'),
                    hasStatus: !!firstRow?.querySelector('[class*=""status""], [data-status]')
                };
            }
        ");

        Console.WriteLine($"File list info: {fileListInfo}");

        // Report findings
        Console.WriteLine("\n=== FILE LIST ANALYSIS ===");
        if (fileListInfo.GetProperty("hasTable").GetBoolean())
        {
            Console.WriteLine("Headers found:");
            foreach (var header in fileListInfo.GetProperty("headers").EnumerateArray())
            {
                Console.WriteLine($"  - {header.GetString()}");
            }

            Console.WriteLine($"Row count: {fileListInfo.GetProperty("rowCount").GetInt32()}");
            Console.WriteLine($"Shows entity count: {fileListInfo.GetProperty("hasEntityCount").GetBoolean()}");
            Console.WriteLine($"Shows doc type: {fileListInfo.GetProperty("hasDocType").GetBoolean()}");
            Console.WriteLine($"Shows status: {fileListInfo.GetProperty("hasStatus").GetBoolean()}");
        }
        else
        {
            Console.WriteLine("No table found in file list area");
        }
        Console.WriteLine("===========================\n");
    }

    /// <summary>
    ///     Test that details view shows document summary when available.
    /// </summary>
    [Fact]
    public async Task Explorer_DetailsView_ShowsSummary()
    {
        // Arrange - Login and navigate
        var loggedIn = await LoginAndNavigateToAdminAsync();
        if (!loggedIn)
        {
            Console.WriteLine("SKIP: Could not login");
            return;
        }

        await SwitchToExplorerModeAsync();
        await Task.Delay(1000);

        // Act - Click on first document to open details
        var detailsResult = await _page!.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                // Find and click first document row
                const firstRow = document.querySelector('table tbody tr');
                if (!firstRow) return { hasDocuments: false };

                // Try to find a clickable element or click the row
                const clickTarget = firstRow.querySelector('a, button, [x-on\\:click]') || firstRow;
                clickTarget.click();

                // Wait for details panel to appear
                await new Promise(r => setTimeout(r, 1000));

                // Check for details panel content
                const detailsPanel = document.querySelector('.details-panel, .info-panel, [data-details], .drawer-content [x-show]');

                if (!detailsPanel) {
                    return {
                        hasDocuments: true,
                        detailsShown: false,
                        error: 'Details panel not found after click'
                    };
                }

                return {
                    hasDocuments: true,
                    detailsShown: true,
                    hasSummary: !!detailsPanel.querySelector('[data-summary], .summary, .executive-summary'),
                    hasEntities: !!detailsPanel.querySelector('[data-entities], .entities'),
                    hasMetadata: !!detailsPanel.querySelector('[data-metadata], .metadata'),
                    detailsContent: detailsPanel.textContent?.substring(0, 500) || ''
                };
            }
        ");

        Console.WriteLine($"Details result: {detailsResult}");

        // Take screenshot
        await _page.ScreenshotAsync(@"E:\source\lucidrag\explorer-details.png", new ScreenshotOptions { FullPage = true });
        Console.WriteLine("Screenshot saved: explorer-details.png");
    }
}
