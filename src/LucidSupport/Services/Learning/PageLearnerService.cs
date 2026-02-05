using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Learns a web page by navigating with Playwright, performing deep visual analysis,
///     and actively probing interactive behavior. Outputs a PageModel that can be written
///     to .support.md, plus a <see cref="LearnResult"/> with visual analysis data.
///
///     The learning pipeline:
///     1. Navigate and wait for page to settle
///     2. Extract form fields (DOM attributes, labels, ARIA)
///     3. Extract navigation links
///     4. Analyze responsive layouts at multiple breakpoints
///     5. Deep visual analysis (CSS styles, grouping, reading order, theme detection)
///     6. Active interaction probing (focus effects, validation, field dependencies)
///     7. Validation error extraction
///     8. Synthesize everything into PageModel + LearnResult
/// </summary>
internal sealed class PageLearnerService : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initialized;

    /// <summary>
    ///     Full learning result including the PageModel and visual/interaction analysis.
    /// </summary>
    public sealed record LearnResult
    {
        public required PageModel Model { get; init; }
        public PageVisualAnalysis? VisualAnalysis { get; init; }
        public InteractionProber.ProbeResult? ProbeResult { get; init; }
    }

    /// <summary>
    ///     Learn a single page with full deep analysis.
    /// </summary>
    public async Task<LearnResult> LearnPageDeepAsync(string url, string? screenshotDir = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            // Phase 1: Structural extraction (parallel where possible)
            var fieldsTask = FormFieldExtractor.ExtractAsync(page);
            var navTask = NavigationExtractor.ExtractAsync(page);
            await Task.WhenAll(fieldsTask, navTask);

            var (fields, pageTitle) = fieldsTask.Result;
            var nav = navTask.Result;

            // Phase 2: Visual analysis (CSS styles, grouping, screenshots)
            var visualAnalysis = await VisualAnalyzer.AnalyzeAsync(page, screenshotDir);

            // Phase 3: Layout analysis at multiple breakpoints
            var layouts = await LayoutAnalyzer.AnalyzeAsync(page);

            // Phase 4: Active interaction probing (focus, validation, dependencies)
            var probeResult = await InteractionProber.ProbeAsync(page, fields);

            // Phase 5: Validation error extraction (triggers form submission)
            var validationErrors = await ValidationExtractor.ExtractAsync(page);

            // Phase 6: Synthesis — merge all data sources into enriched fields
            var enrichedFields = EnrichFields(fields, validationErrors, visualAnalysis, probeResult);

            // Generate conditions from visual analysis and probe results
            var conditions = GenerateConditions(enrichedFields, probeResult, visualAnalysis);

            var uri = new Uri(url);
            var model = new PageModel
            {
                PageId = BuildPageId(uri),
                UrlPattern = BuildUrlPattern(uri),
                Title = pageTitle,
                Learned = DateTimeOffset.UtcNow,
                Site = uri.Host,
                Layouts = layouts,
                Nav = nav,
                Description = BuildDescription(url, pageTitle, fields, layouts, visualAnalysis),
                Fields = enrichedFields,
                Conditions = conditions
            };

            return new LearnResult
            {
                Model = model,
                VisualAnalysis = visualAnalysis,
                ProbeResult = probeResult
            };
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    ///     Learn a single page (standard mode - no visual analysis or screenshots).
    /// </summary>
    public async Task<PageModel> LearnPageAsync(string url, CancellationToken ct = default)
    {
        var result = await LearnPageDeepAsync(url, screenshotDir: null, ct);
        return result.Model;
    }

    /// <summary>
    ///     Learn a page and follow navigation links to discover a multi-step flow.
    /// </summary>
    public async Task<List<LearnResult>> LearnFlowAsync(string startUrl, string? screenshotDir = null,
        CancellationToken ct = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LearnResult>();
        var flowName = BuildFlowName(new Uri(startUrl));
        int step = 1;

        var currentUrl = startUrl;
        while (!string.IsNullOrEmpty(currentUrl) && !visited.Contains(currentUrl))
        {
            visited.Add(currentUrl);

            var stepScreenshotDir = screenshotDir is not null
                ? Path.Combine(screenshotDir, $"step-{step}")
                : null;

            var result = await LearnPageDeepAsync(currentUrl, stepScreenshotDir, ct);

            // Set flow metadata
            var model = result.Model with
            {
                Flow = flowName,
                Step = step,
                Prev = results.Count > 0 ? results[^1].Model.PageId : null
            };

            // Link previous page to this one
            if (results.Count > 0)
            {
                var prevResult = results[^1];
                var prevModel = prevResult.Model with { Next = model.PageId };
                results[^1] = prevResult with { Model = prevModel };
            }

            results.Add(result with { Model = model });
            step++;

            // Follow next nav link
            if (model.Nav.TryGetValue("next", out var nextNav))
                currentUrl = await ResolveNavUrlAsync(currentUrl, nextNav, ct);
            else
                break;

            if (step > 20) break; // Safety limit
        }

        return results;
    }

    /// <summary>
    ///     Learn a page and write the result to a .support.md file.
    /// </summary>
    public async Task<string> LearnAndWriteAsync(string url, string? outputDir = null,
        CancellationToken ct = default)
    {
        outputDir ??= Directory.GetCurrentDirectory();
        var screenshotDir = Path.Combine(outputDir, "screenshots");

        var result = await LearnPageDeepAsync(url, screenshotDir, ct);

        var fileName = $"{result.Model.PageId}.support.md";
        var filePath = Path.Combine(outputDir, fileName);
        SupportMarkdownWriter.WriteFile(result.Model, filePath);
        return filePath;
    }

    /// <summary>
    ///     Learn a flow and write all pages to .support.md files.
    /// </summary>
    public async Task<List<string>> LearnFlowAndWriteAsync(string startUrl, string? outputDir = null,
        CancellationToken ct = default)
    {
        outputDir ??= Directory.GetCurrentDirectory();
        var screenshotDir = Path.Combine(outputDir, "screenshots");

        var results = await LearnFlowAsync(startUrl, screenshotDir, ct);

        var paths = new List<string>();
        foreach (var result in results)
        {
            var fileName = $"{result.Model.PageId}.support.md";
            var filePath = Path.Combine(outputDir, fileName);
            SupportMarkdownWriter.WriteFile(result.Model, filePath);
            paths.Add(filePath);
        }

        return paths;
    }

    // ── Enrichment: merge all analysis sources into field definitions ──

    private static List<FieldDefinition> EnrichFields(
        List<FieldDefinition> fields,
        Dictionary<string, Dictionary<string, string>> validationErrors,
        PageVisualAnalysis visualAnalysis,
        InteractionProber.ProbeResult probeResult)
    {
        return fields.Select(f =>
        {
            // Merge validation errors
            var errors = new Dictionary<string, string>(f.Errors);
            if (validationErrors.TryGetValue(f.Selector, out var valErrors))
            {
                foreach (var (key, msg) in valErrors)
                    errors.TryAdd(key, msg);
            }

            // Merge probe validation results
            var probeField = probeResult.Fields.FirstOrDefault(p => p.Selector == f.Selector);
            if (probeField is not null)
            {
                foreach (var probe in probeField.ValidationProbes.Where(p => p.IsInvalid && p.ErrorMessage is not null))
                    errors.TryAdd(probe.TestCase, probe.ErrorMessage!);
            }

            // Enrich help text from visual analysis and probing
            var help = f.Help;
            if (string.IsNullOrEmpty(help) && probeField?.FocusEffect.HelpText is not null)
                help = probeField.FocusEffect.HelpText;

            // Find visual info for this field
            var visual = visualAnalysis.Fields.FirstOrDefault(v => v.Selector == f.Selector);

            // Build server validation from probe results
            var serverValidation = new List<string>(f.ServerValidation);

            return f with
            {
                Errors = errors,
                Help = help,
                ServerValidation = serverValidation
            };
        }).ToList();
    }

    // ── Condition generation from analysis data ──

    private static List<ConditionRule> GenerateConditions(
        List<FieldDefinition> fields,
        InteractionProber.ProbeResult probeResult,
        PageVisualAnalysis visualAnalysis)
    {
        var conditions = new List<ConditionRule>();

        // Generate conditions for fields with validation errors
        foreach (var field in fields.Where(f => f.Errors.Count > 0 && !string.IsNullOrEmpty(f.Help)))
        {
            conditions.Add(new ConditionRule
            {
                When = $"[{field.Selector}].error",
                Suggest = field.Help!,
                Highlight = field.Selector
            });
        }

        // Generate conditions for field dependencies
        foreach (var dep in probeResult.Dependencies)
        {
            if (dep.Effect == "show")
            {
                conditions.Add(new ConditionRule
                {
                    When = $"[{dep.TriggerSelector}].changed",
                    Suggest = $"New field appeared: check {dep.AffectedSelector}",
                    Highlight = dep.AffectedSelector
                });
            }
        }

        // Idle condition for pages with many fields
        if (fields.Count >= 3)
        {
            conditions.Add(new ConditionRule
            {
                When = "page.idle > 30s AND form.incomplete",
                Suggest = "Need help completing this form? I can walk you through each field."
            });
        }

        return conditions;
    }

    // ── Description generation ──

    private static string BuildDescription(string url, string pageTitle,
        List<FieldDefinition> fields, List<LayoutBreakpoint> layouts,
        PageVisualAnalysis visualAnalysis)
    {
        var parts = new List<string>();

        parts.Add($"Auto-learned from {url}");

        if (fields.Count > 0)
            parts.Add($"Contains {fields.Count} interactive field{(fields.Count == 1 ? "" : "s")}.");

        if (visualAnalysis.Groups.Count > 0)
        {
            var groupNames = visualAnalysis.Groups.Select(g => g.Name).Where(n => n != "Unnamed");
            var named = groupNames.ToList();
            if (named.Count > 0)
                parts.Add($"Sections: {string.Join(", ", named)}.");
        }

        if (visualAnalysis.IsMultiColumn)
            parts.Add("Uses multi-column layout.");

        if (visualAnalysis.Theme == "dark")
            parts.Add("Dark theme detected.");

        return string.Join(" ", parts);
    }

    // ── Helpers ──

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        _initialized = true;
    }

    private static string BuildPageId(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path)) path = "home";
        return path
            .Replace('/', '-')
            .Replace('_', '-')
            .Replace('.', '-')
            .ToLowerInvariant()
            .Trim('-');
    }

    private static string BuildUrlPattern(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (!path.EndsWith('*'))
            path += "*";
        return path;
    }

    private static string BuildFlowName(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length > 0 ? segments[0].ToLowerInvariant() : "flow";
    }

    private async Task<string?> ResolveNavUrlAsync(string currentUrl, NavInfo navInfo, CancellationToken ct)
    {
        await EnsureInitializedAsync();

        var page = await _browser!.NewPageAsync();
        try
        {
            await page.GotoAsync(currentUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            return await page.EvaluateAsync<string?>($$"""
                () => {
                    const el = document.querySelector('{{navInfo.Selector.Replace("'", "\\'")}}');
                    if (!el) return null;
                    return el.href || null;
                }
                """);
        }
        catch
        {
            return null;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
