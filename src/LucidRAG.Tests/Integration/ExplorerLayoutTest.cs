using System.Text.Json;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Test to verify the Explorer mode layout - checking if the main content fills the screen.
/// Run with: dotnet test --filter "FullyQualifiedName~ExplorerLayoutTest" src/LucidRAG.Tests/LucidRAG.Tests.csproj
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class ExplorerLayoutTest : IAsyncLifetime
{
    private IBrowser? _browser;
    private IPage? _page;
    private const string BaseUrl = "http://127.0.0.1:5080";

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = false, // Set to false to see the browser
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--start-maximized"]
        });

        _page = await _browser.NewPageAsync();
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    [Fact]
    public async Task Explorer_MainContent_FillsScreen()
    {
        // Step 1: Navigate to login page
        Console.WriteLine("Step 1: Navigating to login page...");
        await _page!.GoToAsync($"{BaseUrl}/auth/login");
        await Task.Delay(1000);

        // Step 2: Fill in login form
        Console.WriteLine("Step 2: Filling in login form...");

        // Find and fill email field using JavaScript
        await _page.EvaluateFunctionAsync(@"
            () => {
                const emailInput = document.querySelector('input[type=""email""], input[name=""email""], input[name=""Email""], input[name=""Input.Email""], #email, #Email');
                if (emailInput) {
                    emailInput.value = 'admin@lucidrag.local';
                    emailInput.dispatchEvent(new Event('input', { bubbles: true }));
                }
            }
        ");

        // Find and fill password field using JavaScript
        await _page.EvaluateFunctionAsync(@"
            () => {
                const passwordInput = document.querySelector('input[type=""password""], input[name=""password""], input[name=""Password""], input[name=""Input.Password""], #password, #Password');
                if (passwordInput) {
                    passwordInput.value = 'Admin123!';
                    passwordInput.dispatchEvent(new Event('input', { bubbles: true }));
                }
            }
        ");

        // Step 3: Submit form and wait for redirect
        Console.WriteLine("Step 3: Submitting form...");

        // Find and click submit button using JavaScript
        var submitted = await _page.EvaluateFunctionAsync<bool>(@"
            () => {
                // Try various selectors for submit button
                const submitBtn = document.querySelector('button[type=""submit""], input[type=""submit""]');
                if (submitBtn) {
                    submitBtn.click();
                    return true;
                }

                // Look for button with login text
                const buttons = document.querySelectorAll('button');
                for (const btn of buttons) {
                    const text = btn.textContent?.toLowerCase() || '';
                    if (text.includes('login') || text.includes('sign in') || text.includes('log in')) {
                        btn.click();
                        return true;
                    }
                }

                // Submit the form directly
                const form = document.querySelector('form');
                if (form) {
                    form.submit();
                    return true;
                }

                return false;
            }
        ");

        Console.WriteLine($"Form submitted: {submitted}");

        // Wait for navigation to /admin
        Console.WriteLine("Waiting for redirect to /admin...");
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

        var currentUrl = _page.Url;
        Console.WriteLine($"Current URL after login: {currentUrl}");

        // Step 4: Take screenshot of admin page
        Console.WriteLine("Step 4: Taking screenshot of admin page...");
        await _page.ScreenshotAsync(@"E:\source\lucidrag\admin-page.png", new ScreenshotOptions { FullPage = true });
        Console.WriteLine("Screenshot saved to: E:\\source\\lucidrag\\admin-page.png");

        // Step 5: Look for Explorer button/tab and click it
        Console.WriteLine("Step 5: Looking for Explorer button/tab...");

        // First, try standard CSS selectors
        IElementHandle? explorerButton = null;
        var validSelectors = new[]
        {
            "[data-tab='explorer']",
            "#explorer-tab",
            "button[onclick*='explorer']",
            "a[href*='explorer']"
        };

        foreach (var selector in validSelectors)
        {
            try
            {
                explorerButton = await _page.QuerySelectorAsync(selector);
                if (explorerButton != null)
                {
                    Console.WriteLine($"Found Explorer with selector: {selector}");
                    break;
                }
            }
            catch
            {
                // Try next selector
            }
        }

        // If not found, try JavaScript to find by text content
        if (explorerButton == null)
        {
            Console.WriteLine("Trying JavaScript to find Explorer element...");
            var found = await _page.EvaluateFunctionAsync<bool>(@"
                () => {
                    const elements = document.querySelectorAll('button, a, [role=""tab""], .tab, .btn');
                    for (const el of elements) {
                        const text = el.textContent?.toLowerCase() || '';
                        if (text.includes('explorer')) {
                            console.log('Found Explorer element:', el);
                            el.click();
                            return true;
                        }
                    }
                    return false;
                }
            ");

            if (found)
            {
                Console.WriteLine("Clicked Explorer via JavaScript");
            }
            else
            {
                // Log all available buttons/tabs for debugging
                var buttons = await _page.EvaluateFunctionAsync<string[]>(@"
                    () => {
                        const items = [];
                        document.querySelectorAll('button, a, [role=""tab""], .tab, .btn, nav a, .menu a, .menu button').forEach(el => {
                            const text = el.textContent?.trim()?.substring(0, 50) || '';
                            if (text) {
                                items.push(`${el.tagName}: ${text}`);
                            }
                        });
                        return items;
                    }
                ");

                Console.WriteLine("Available clickable elements:");
                foreach (var btn in buttons)
                {
                    Console.WriteLine($"  - {btn}");
                }
            }
        }
        else
        {
            // Step 6: Click the Explorer button
            Console.WriteLine("Step 6: Clicking Explorer...");
            await explorerButton.ClickAsync();
        }

        // Step 7: Wait 2 seconds for explorer mode to load
        Console.WriteLine("Step 7: Waiting 2 seconds for explorer to load...");
        await Task.Delay(2000);

        // Step 8: Take full page screenshot
        Console.WriteLine("Step 8: Taking full page screenshot of explorer...");
        await _page.ScreenshotAsync(@"E:\source\lucidrag\explorer-expanded.png", new ScreenshotOptions { FullPage = true });
        Console.WriteLine("Screenshot saved to: E:\\source\\lucidrag\\explorer-expanded.png");

        // Step 9: Analyze the explorer layout
        Console.WriteLine("\n=== LAYOUT ANALYSIS ===");

        var layoutInfo = await _page.EvaluateFunctionAsync<JsonElement>(@"
            () => {
                const viewport = {
                    width: window.innerWidth,
                    height: window.innerHeight
                };

                // Find main content area
                const mainContent = document.querySelector('main, .main-content, [role=""main""], .drawer-content > div');
                const sidebar = document.querySelector('.drawer-side, aside, .sidebar, [role=""navigation""]');

                const mainRect = mainContent?.getBoundingClientRect();
                const sidebarRect = sidebar?.getBoundingClientRect();

                // Check for explorer-specific elements
                const explorerContainer = document.querySelector('.explorer-container, #explorer, [data-explorer]');
                const explorerRect = explorerContainer?.getBoundingClientRect();

                return {
                    viewport: viewport,
                    mainContent: mainRect ? {
                        width: mainRect.width,
                        height: mainRect.height,
                        left: mainRect.left,
                        top: mainRect.top
                    } : null,
                    sidebar: sidebarRect ? {
                        width: sidebarRect.width,
                        height: sidebarRect.height,
                        visible: sidebarRect.width > 0
                    } : null,
                    explorerContainer: explorerRect ? {
                        width: explorerRect.width,
                        height: explorerRect.height,
                        left: explorerRect.left
                    } : null,
                    bodyClasses: document.body.className,
                    currentUrl: window.location.href
                };
            }
        ");

        Console.WriteLine($"Layout Info: {layoutInfo}");

        // Parse the results
        var viewport = layoutInfo.GetProperty("viewport");
        var viewportWidth = viewport.GetProperty("width").GetDouble();
        var viewportHeight = viewport.GetProperty("height").GetDouble();
        Console.WriteLine($"Viewport: {viewportWidth}x{viewportHeight}");

        if (layoutInfo.TryGetProperty("mainContent", out var mainContent) && mainContent.ValueKind != JsonValueKind.Null)
        {
            var mainWidth = mainContent.GetProperty("width").GetDouble();
            var mainHeight = mainContent.GetProperty("height").GetDouble();
            var mainLeft = mainContent.GetProperty("left").GetDouble();
            Console.WriteLine($"Main Content: {mainWidth}x{mainHeight} at left={mainLeft}");

            var widthRatio = mainWidth / viewportWidth;
            Console.WriteLine($"\nMain content width ratio: {widthRatio:P1} of viewport");

            if (widthRatio > 0.8)
            {
                Console.WriteLine("SUCCESS: Explorer main content fills most of the screen!");
            }
            else if (widthRatio > 0.5)
            {
                Console.WriteLine("PARTIAL: Explorer has significant width but may be constrained by sidebar");
            }
            else
            {
                Console.WriteLine("ISSUE: Explorer main content appears squished (less than 50% of viewport width)");
            }
        }
        else
        {
            Console.WriteLine("Could not find main content element");
        }

        if (layoutInfo.TryGetProperty("sidebar", out var sidebar) && sidebar.ValueKind != JsonValueKind.Null)
        {
            var sidebarWidth = sidebar.GetProperty("width").GetDouble();
            Console.WriteLine($"Sidebar width: {sidebarWidth}px");
        }

        if (layoutInfo.TryGetProperty("explorerContainer", out var explorer) && explorer.ValueKind != JsonValueKind.Null)
        {
            var explorerWidth = explorer.GetProperty("width").GetDouble();
            Console.WriteLine($"Explorer container width: {explorerWidth}px");
        }

        Console.WriteLine($"Body classes: {layoutInfo.GetProperty("bodyClasses").GetString()}");
        Console.WriteLine($"Current URL: {layoutInfo.GetProperty("currentUrl").GetString()}");

        Console.WriteLine("\n=== END ANALYSIS ===");
    }
}
