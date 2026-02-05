using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Analyzes page layout at multiple viewport breakpoints.
/// </summary>
internal static class LayoutAnalyzer
{
    /// <summary>
    ///     Standard breakpoints to test: mobile, tablet, desktop, wide.
    /// </summary>
    private static readonly (string Name, int Width)[] Breakpoints =
    [
        ("mobile", 375),
        ("tablet", 768),
        ("desktop", 1024),
        ("wide", 1440)
    ];

    /// <summary>
    ///     JavaScript to detect the number of visual columns in the main content area.
    /// </summary>
    private const string ColumnDetectionScript = """
        () => {
            // Find the main content area
            const main = document.querySelector('main, [role="main"], .main-content, #content, #main')
                        || document.body;

            // Check for CSS grid or flexbox children
            const style = window.getComputedStyle(main);
            const display = style.display;

            if (display === 'grid') {
                const cols = style.gridTemplateColumns;
                // Count non-zero-width column tracks
                return cols.split(/\s+/).filter(c => c !== '0px' && c !== '0').length;
            }

            if (display === 'flex' && style.flexDirection === 'row') {
                // Count visible flex children
                return Array.from(main.children).filter(c => {
                    const cs = window.getComputedStyle(c);
                    return cs.display !== 'none' && cs.visibility !== 'hidden' && c.offsetWidth > 50;
                }).length;
            }

            // Check direct children for side-by-side layout
            const children = Array.from(main.children).filter(c => {
                const cs = window.getComputedStyle(c);
                return cs.display !== 'none' && cs.visibility !== 'hidden'
                    && cs.position !== 'absolute' && cs.position !== 'fixed'
                    && c.offsetWidth > 50 && c.offsetHeight > 20;
            });

            if (children.length >= 2) {
                // Check if first two children are side by side (tops within 50px)
                const top0 = children[0].getBoundingClientRect().top;
                const top1 = children[1].getBoundingClientRect().top;
                if (Math.abs(top0 - top1) < 50) return 2;
            }

            return 1;
        }
        """;

    /// <summary>
    ///     Analyze the page layout at multiple breakpoints, returning layout info.
    /// </summary>
    public static async Task<List<LayoutBreakpoint>> AnalyzeAsync(IPage page)
    {
        var results = new List<LayoutBreakpoint>();

        // Save original viewport
        var originalSize = page.ViewportSize;

        try
        {
            for (int i = 0; i < Breakpoints.Length; i++)
            {
                var (name, width) = Breakpoints[i];

                await page.SetViewportSizeAsync(width, 900);
                // Small wait for layout recalc
                await page.WaitForTimeoutAsync(200);

                var columns = await page.EvaluateAsync<int>(ColumnDetectionScript);

                int? minWidth = i > 0 ? width : null;
                int? maxWidth = i < Breakpoints.Length - 1 ? Breakpoints[i + 1].Width - 1 : null;

                results.Add(new LayoutBreakpoint
                {
                    Name = name,
                    MinWidth = minWidth,
                    MaxWidth = maxWidth,
                    Columns = Math.Max(1, columns)
                });
            }
        }
        finally
        {
            // Restore original viewport
            if (originalSize is not null)
                await page.SetViewportSizeAsync(originalSize.Width, originalSize.Height);
        }

        // Deduplicate identical adjacent breakpoints (same column count)
        return DeduplicateBreakpoints(results);
    }

    private static List<LayoutBreakpoint> DeduplicateBreakpoints(List<LayoutBreakpoint> breakpoints)
    {
        if (breakpoints.Count <= 1) return breakpoints;

        var result = new List<LayoutBreakpoint> { breakpoints[0] };
        for (int i = 1; i < breakpoints.Count; i++)
        {
            var prev = result[^1];
            var curr = breakpoints[i];

            if (prev.Columns == curr.Columns && prev.Note == curr.Note)
            {
                // Merge: extend previous breakpoint's max to current's max
                result[^1] = prev with { MaxWidth = curr.MaxWidth };
            }
            else
            {
                result.Add(curr);
            }
        }

        return result;
    }
}
