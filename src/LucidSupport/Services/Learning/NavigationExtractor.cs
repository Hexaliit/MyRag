using System.Text.Json;
using System.Text.Json.Serialization;
using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Extracts navigation links from a page (back, next, cancel, etc.).
/// </summary>
internal static class NavigationExtractor
{
    private const string NavExtractionScript = """
        () => {
            const navLinks = [];

            // Helper: classify a link/button by its text content
            function classify(text) {
                const t = text.toLowerCase().trim();
                if (t.match(/^(back|previous|prev|go back|return)/)) return 'back';
                if (t.match(/^(next|continue|proceed|forward|submit|pay|place order|confirm|finish|complete)/)) return 'next';
                if (t.match(/^(cancel|close|exit|discard)/)) return 'cancel';
                if (t.match(/^(skip)/)) return 'skip';
                if (t.match(/^(save|draft)/)) return 'save';
                return null;
            }

            function buildSelector(el) {
                if (el.id) return '#' + el.id;
                if (el.name) return el.tagName.toLowerCase() + '[name="' + el.name + '"]';
                // Use text content for buttons
                const text = el.textContent.trim().substring(0, 30);
                if (el.tagName === 'BUTTON' || el.tagName === 'A') {
                    return el.tagName.toLowerCase() + ':has-text("' + text + '")';
                }
                return null;
            }

            // Check buttons and links
            const candidates = document.querySelectorAll('a, button, [role="button"], input[type="submit"]');
            candidates.forEach(el => {
                const computed = window.getComputedStyle(el);
                if (computed.display === 'none' || computed.visibility === 'hidden') return;

                const text = el.textContent.trim() || el.getAttribute('aria-label') || el.value || '';
                if (!text) return;

                const role = classify(text);
                if (!role) return;

                const selector = buildSelector(el);
                if (!selector) return;

                // Don't add duplicates for same role
                if (navLinks.some(n => n.role === role)) return;

                navLinks.push({
                    role: role,
                    label: text.substring(0, 100),
                    selector: selector,
                    href: el.href || null
                });
            });

            return JSON.stringify(navLinks);
        }
        """;

    /// <summary>
    ///     Extract navigation links from the current page.
    /// </summary>
    public static async Task<Dictionary<string, NavInfo>> ExtractAsync(IPage page)
    {
        var result = new Dictionary<string, NavInfo>();

        try
        {
            var json = await page.EvaluateAsync<string>(NavExtractionScript);
            var entries = JsonSerializer.Deserialize<List<RawNavLink>>(json, JsonOpts);
            if (entries is null) return result;

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Role) || string.IsNullOrEmpty(entry.Selector))
                    continue;

                result[entry.Role] = new NavInfo
                {
                    Label = entry.Label ?? entry.Role,
                    Selector = entry.Selector
                };
            }
        }
        catch
        {
            // Navigation extraction is best-effort
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record RawNavLink
    {
        [JsonPropertyName("role")] public string? Role { get; init; }
        [JsonPropertyName("label")] public string? Label { get; init; }
        [JsonPropertyName("selector")] public string? Selector { get; init; }
        [JsonPropertyName("href")] public string? Href { get; init; }
    }
}
