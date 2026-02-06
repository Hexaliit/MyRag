using System.Text.Json;
using System.Text.Json.Serialization;
using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Deep visual analysis of a page using Playwright CSS inspection and screenshots.
///     Understands how fields look, their visual grouping, prominence, and reading order.
///     Goes far beyond DOM scraping - understands the visual design of the page.
/// </summary>
internal static class VisualAnalyzer
{
    /// <summary>
    ///     JavaScript that performs deep visual analysis of every interactive element.
    ///     Extracts computed styles, bounding boxes, visual grouping, and error indicators.
    /// </summary>
    private static readonly string VisualExtractionScript = $$"""
        () => {
            {{DomScriptSnippets.BuildStableSelectorFunction}}
            {{DomScriptSnippets.ErrorDetectionHelpers}}
            // ── Color analysis helpers ──────────────────
            function parseColor(str) {
                if (!str || str === 'transparent' || str === 'rgba(0, 0, 0, 0)') return null;
                const m = str.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
                if (!m) return null;
                return { r: +m[1], g: +m[2], b: +m[3] };
            }

            function isRedish(color) {
                if (!color) return false;
                return color.r > 180 && color.g < 100 && color.b < 100;
            }

            function luminance(color) {
                if (!color) return 0;
                return (0.299 * color.r + 0.587 * color.g + 0.114 * color.b) / 255;
            }

            function isDark(bgColor) {
                return luminance(bgColor) < 0.5;
            }

            // ── Group detection helpers ─────────────────
            function findGroupContainer(el) {
                let current = el.parentElement;
                while (current && current !== document.body) {
                    const tag = current.tagName.toLowerCase();
                    // Fieldset, section, div with role="group", or div with a heading child
                    if (tag === 'fieldset') {
                        const legend = current.querySelector('legend');
                        return { name: legend ? legend.textContent.trim() : 'Group', selector: buildSel(current) };
                    }
                    if (current.getAttribute('role') === 'group') {
                        const label = current.getAttribute('aria-label') || '';
                        return { name: label || 'Group', selector: buildSel(current) };
                    }
                    if (tag === 'section' || tag === 'div') {
                        // Check for heading child
                        const heading = current.querySelector('h1, h2, h3, h4, h5, h6, [role="heading"]');
                        if (heading) {
                            const cs = window.getComputedStyle(current);
                            // Must have some visual boundary (padding, border, bg)
                            const hasBorder = parseFloat(cs.borderWidth) > 0;
                            const hasBg = cs.backgroundColor !== 'transparent' && cs.backgroundColor !== 'rgba(0, 0, 0, 0)';
                            const hasPad = parseFloat(cs.paddingTop) > 8 || parseFloat(cs.paddingBottom) > 8;
                            if (hasBorder || hasBg || hasPad) {
                                return { name: heading.textContent.trim(), selector: buildSel(current), hasBoundary: true };
                            }
                        }
                    }
                    current = current.parentElement;
                }
                return null;
            }

            function buildSel(el) {
                return buildStableSelector(el);
            }

            // ── Error state detection ───────────────────
            function detectErrorState(el) {
                const result = { isError: false, hasErrorBorder: false, hasErrorIcon: false, hasErrorMessage: false, errorMessageText: null, errorMessageSelector: null, errorMessagePosition: null };

                result.isError = isFieldInErrorState(el);

                // Check border color for red-ish
                const cs = window.getComputedStyle(el);
                const borderColor = parseColor(cs.borderColor);
                if (isRedish(borderColor)) {
                    result.isError = true;
                    result.hasErrorBorder = true;
                }

                const errorInfo = findFieldErrorMessage(el);
                if (errorInfo) {
                    result.hasErrorMessage = true;
                    result.errorMessageText = errorInfo.text;
                    result.errorMessageSelector = errorInfo.selector;
                    result.errorMessagePosition = getRelativePosition(el, errorInfo.element);
                }

                // 4. Check for error icon (svg, img, or ::before/::after with icon font)
                const prevSib = el.previousElementSibling;
                const nextSib = el.nextElementSibling;
                [prevSib, nextSib].forEach(sib => {
                    if (!sib) return;
                    if (sib.tagName === 'SVG' || sib.tagName === 'IMG' || sib.querySelector('svg, img')) {
                        const sibClasses = (sib.className || '').toString().toLowerCase();
                        if (sibClasses.match(/error|warn|alert|exclamation|x-circle/)) {
                            result.hasErrorIcon = true;
                        }
                    }
                });

                return result;
            }

            function getRelativePosition(field, msg) {
                const fRect = field.getBoundingClientRect();
                const mRect = msg.getBoundingClientRect();
                if (mRect.top < fRect.top - 5) return 'above';
                if (mRect.top > fRect.bottom + 5) return 'below';
                if (mRect.left > fRect.right) return 'right';
                return 'inline';
            }

            // ── Main extraction ─────────────────────────
            const selector = 'input, select, textarea, [role="textbox"], [role="combobox"], [contenteditable="true"]';
            const elements = document.querySelectorAll(selector);
            const fields = [];
            const groupMap = {};

            // Detect page theme from body background
            const bodyStyle = window.getComputedStyle(document.body);
            const bodyBg = parseColor(bodyStyle.backgroundColor);
            const theme = isDark(bodyBg) ? 'dark' : 'light';

            // Find primary brand color (most common non-black/white color on buttons and links)
            const colorCounts = {};
            document.querySelectorAll('button, a, [role="button"], .btn').forEach(el => {
                const cs = window.getComputedStyle(el);
                const bg = cs.backgroundColor;
                if (bg && bg !== 'transparent' && bg !== 'rgba(0, 0, 0, 0)') {
                    const c = parseColor(bg);
                    if (c && !isRedish(c) && luminance(c) > 0.1 && luminance(c) < 0.9) {
                        colorCounts[bg] = (colorCounts[bg] || 0) + 1;
                    }
                }
            });
            const primaryColor = Object.entries(colorCounts).sort((a, b) => b[1] - a[1])[0]?.[0] || null;

            elements.forEach(el => {
                const type = (el.type || el.tagName.toLowerCase()).toLowerCase();
                if (type === 'hidden' || type === 'submit' || type === 'button' || type === 'reset') return;

                const cs = window.getComputedStyle(el);
                if (cs.display === 'none' || cs.visibility === 'hidden') return;

                const rect = el.getBoundingClientRect();
                if (rect.width < 10 || rect.height < 10) return;

                const elSelector = buildStableSelector(el);
                if (!elSelector) return;

                // Computed styles
                const borderColor = parseColor(cs.borderColor);
                const bgColor = parseColor(cs.backgroundColor);
                const shadowStr = cs.boxShadow || '';
                const outlineStr = cs.outline || '';

                const style = {
                    backgroundColor: cs.backgroundColor,
                    borderColor: cs.borderColor,
                    borderWidth: parseFloat(cs.borderWidth) || 0,
                    borderRadius: parseFloat(cs.borderRadius) || 0,
                    fontSize: parseFloat(cs.fontSize) || 0,
                    fontFamily: cs.fontFamily,
                    color: cs.color,
                    hasBorder: (parseFloat(cs.borderWidth) || 0) > 0,
                    hasShadow: shadowStr !== 'none' && shadowStr !== '',
                    hasOutline: outlineStr !== 'none' && outlineStr !== '' && !outlineStr.startsWith('0'),
                    padding: [parseFloat(cs.paddingTop)||0, parseFloat(cs.paddingRight)||0, parseFloat(cs.paddingBottom)||0, parseFloat(cs.paddingLeft)||0],
                    theme: theme
                };

                // Visual prominence: bigger + higher contrast = more prominent
                const area = rect.width * rect.height;
                const maxArea = window.innerWidth * 60; // approximate max single field area
                const areaScore = Math.min(area / maxArea, 1);
                const contrastScore = Math.abs(luminance(parseColor(cs.color)) - luminance(bgColor || bodyBg));
                const prominence = (areaScore * 0.4 + contrastScore * 0.6);

                // Error state
                const errorState = detectErrorState(el);

                // Group detection
                const group = findGroupContainer(el);

                if (group) {
                    if (!groupMap[group.name]) {
                        groupMap[group.name] = { name: group.name, selector: group.selector, hasBoundary: !!group.hasBoundary, fields: [] };
                    }
                    groupMap[group.name].fields.push(elSelector);
                }

                fields.push({
                    selector: elSelector,
                    bounds: { x: rect.x + window.scrollX, y: rect.y + window.scrollY, width: rect.width, height: rect.height },
                    groupName: group ? group.name : null,
                    groupIndex: group ? (groupMap[group.name].fields.length - 1) : 0,
                    style: style,
                    errorState: errorState,
                    prominence: prominence
                });
            });

            // Reading order: sort by Y then X
            const readingOrder = [...fields].sort((a, b) => {
                const dy = a.bounds.y - b.bounds.y;
                if (Math.abs(dy) > 20) return dy;
                return a.bounds.x - b.bounds.x;
            }).map(f => f.selector);

            // Multi-column detection
            const isMultiColumn = fields.length > 1 && fields.some((f, i) => {
                if (i === 0) return false;
                const prev = fields[i-1];
                return Math.abs(f.bounds.y - prev.bounds.y) < 20 && Math.abs(f.bounds.x - prev.bounds.x) > 100;
            });

            return JSON.stringify({
                theme: theme,
                primaryColor: primaryColor,
                isMultiColumn: isMultiColumn,
                readingOrder: readingOrder,
                fields: fields,
                groups: Object.values(groupMap)
            });
        }
        """;

    /// <summary>
    ///     Perform deep visual analysis of the page.
    ///     Captures computed styles, visual grouping, error states, and reading order.
    /// </summary>
    public static async Task<PageVisualAnalysis> AnalyzeAsync(IPage page, string? screenshotDir = null)
    {
        // Take full-page screenshot
        string? fullScreenshotPath = null;
        if (screenshotDir is not null)
        {
            Directory.CreateDirectory(screenshotDir);
            fullScreenshotPath = Path.Combine(screenshotDir, "full-page.png");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = fullScreenshotPath,
                FullPage = true,
                Type = ScreenshotType.Png
            });
        }

        // Run visual extraction script
        var json = await page.EvaluateAsync<string>(VisualExtractionScript);
        var raw = JsonSerializer.Deserialize<RawVisualData>(json, JsonOpts);

        if (raw is null)
            return new PageVisualAnalysis { FullScreenshotPath = fullScreenshotPath };

        // Convert raw data to models
        var fields = new List<VisualFieldInfo>();
        foreach (var f in raw.Fields)
        {
            string? fieldScreenshotPath = null;

            // Capture per-field screenshot if output dir is set and field has bounds
            if (screenshotDir is not null && f.Bounds is { Width: > 10, Height: > 10 })
            {
                try
                {
                    var safeName = f.Selector.Replace("#", "").Replace("[", "").Replace("]", "").Replace("\"", "");
                    fieldScreenshotPath = Path.Combine(screenshotDir, $"field-{safeName}.png");

                    // Clip with some padding for context
                    const int pad = 20;
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = fieldScreenshotPath,
                        Type = ScreenshotType.Png,
                        Clip = new Clip
                        {
                            X = (float)Math.Max(0, f.Bounds.X - pad),
                            Y = (float)Math.Max(0, f.Bounds.Y - pad),
                            Width = (float)(f.Bounds.Width + pad * 2),
                            Height = (float)(f.Bounds.Height + pad * 2)
                        }
                    });
                }
                catch
                {
                    fieldScreenshotPath = null;
                }
            }

            fields.Add(new VisualFieldInfo
            {
                Selector = f.Selector,
                Bounds = new Models.BoundingBox
                {
                    X = f.Bounds.X, Y = f.Bounds.Y,
                    Width = f.Bounds.Width, Height = f.Bounds.Height
                },
                GroupName = f.GroupName,
                GroupIndex = f.GroupIndex,
                Style = new FieldVisualStyle
                {
                    BackgroundColor = f.Style.BackgroundColor,
                    BorderColor = f.Style.BorderColor,
                    BorderWidth = f.Style.BorderWidth,
                    BorderRadius = f.Style.BorderRadius,
                    FontSize = f.Style.FontSize,
                    FontFamily = f.Style.FontFamily,
                    Color = f.Style.Color,
                    HasBorder = f.Style.HasBorder,
                    HasShadow = f.Style.HasShadow,
                    HasOutline = f.Style.HasOutline,
                    Padding = f.Style.Padding,
                    Theme = f.Style.Theme ?? "light"
                },
                ErrorState = new ErrorVisualState
                {
                    IsError = f.ErrorState.IsError,
                    HasErrorBorder = f.ErrorState.HasErrorBorder,
                    HasErrorIcon = f.ErrorState.HasErrorIcon,
                    HasErrorMessage = f.ErrorState.HasErrorMessage,
                    ErrorMessageText = f.ErrorState.ErrorMessageText,
                    ErrorMessageSelector = f.ErrorState.ErrorMessageSelector,
                    ErrorMessagePosition = f.ErrorState.ErrorMessagePosition
                },
                Prominence = f.Prominence,
                ScreenshotPath = fieldScreenshotPath
            });
        }

        var groups = raw.Groups.Select(g => new VisualGroup
        {
            Name = g.Name ?? "Unnamed",
            Selector = g.Selector,
            FieldSelectors = g.Fields,
            HasVisualBoundary = g.HasBoundary
        }).ToList();

        return new PageVisualAnalysis
        {
            FullScreenshotPath = fullScreenshotPath,
            Groups = groups,
            Fields = fields,
            Theme = raw.Theme ?? "light",
            IsMultiColumn = raw.IsMultiColumn,
            PrimaryColor = raw.PrimaryColor,
            ReadingOrder = raw.ReadingOrder
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Raw JSON DTOs ───────────────────────────────────

    private sealed record RawVisualData
    {
        [JsonPropertyName("theme")] public string? Theme { get; init; }
        [JsonPropertyName("primaryColor")] public string? PrimaryColor { get; init; }
        [JsonPropertyName("isMultiColumn")] public bool IsMultiColumn { get; init; }
        [JsonPropertyName("readingOrder")] public List<string> ReadingOrder { get; init; } = [];
        [JsonPropertyName("fields")] public List<RawVisualField> Fields { get; init; } = [];
        [JsonPropertyName("groups")] public List<RawVisualGroup> Groups { get; init; } = [];
    }

    private sealed record RawVisualField
    {
        [JsonPropertyName("selector")] public string Selector { get; init; } = "";
        [JsonPropertyName("bounds")] public RawBounds Bounds { get; init; } = new();
        [JsonPropertyName("groupName")] public string? GroupName { get; init; }
        [JsonPropertyName("groupIndex")] public int GroupIndex { get; init; }
        [JsonPropertyName("style")] public RawStyle Style { get; init; } = new();
        [JsonPropertyName("errorState")] public RawErrorState ErrorState { get; init; } = new();
        [JsonPropertyName("prominence")] public double Prominence { get; init; }
    }

    private sealed record RawBounds
    {
        [JsonPropertyName("x")] public double X { get; init; }
        [JsonPropertyName("y")] public double Y { get; init; }
        [JsonPropertyName("width")] public double Width { get; init; }
        [JsonPropertyName("height")] public double Height { get; init; }
    }

    private sealed record RawStyle
    {
        [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; init; }
        [JsonPropertyName("borderColor")] public string? BorderColor { get; init; }
        [JsonPropertyName("borderWidth")] public double BorderWidth { get; init; }
        [JsonPropertyName("borderRadius")] public double BorderRadius { get; init; }
        [JsonPropertyName("fontSize")] public double FontSize { get; init; }
        [JsonPropertyName("fontFamily")] public string? FontFamily { get; init; }
        [JsonPropertyName("color")] public string? Color { get; init; }
        [JsonPropertyName("hasBorder")] public bool HasBorder { get; init; }
        [JsonPropertyName("hasShadow")] public bool HasShadow { get; init; }
        [JsonPropertyName("hasOutline")] public bool HasOutline { get; init; }
        [JsonPropertyName("padding")] public double[] Padding { get; init; } = [];
        [JsonPropertyName("theme")] public string? Theme { get; init; }
    }

    private sealed record RawErrorState
    {
        [JsonPropertyName("isError")] public bool IsError { get; init; }
        [JsonPropertyName("hasErrorBorder")] public bool HasErrorBorder { get; init; }
        [JsonPropertyName("hasErrorIcon")] public bool HasErrorIcon { get; init; }
        [JsonPropertyName("hasErrorMessage")] public bool HasErrorMessage { get; init; }
        [JsonPropertyName("errorMessageText")] public string? ErrorMessageText { get; init; }
        [JsonPropertyName("errorMessageSelector")] public string? ErrorMessageSelector { get; init; }
        [JsonPropertyName("errorMessagePosition")] public string? ErrorMessagePosition { get; init; }
    }

    private sealed record RawVisualGroup
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("selector")] public string? Selector { get; init; }
        [JsonPropertyName("hasBoundary")] public bool HasBoundary { get; init; }
        [JsonPropertyName("fields")] public List<string> Fields { get; init; } = [];
    }
}
