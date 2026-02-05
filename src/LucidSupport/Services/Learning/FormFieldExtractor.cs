using System.Text.Json;
using System.Text.Json.Serialization;
using LucidSupport.Models;
using LucidSupport.Services.Ingestion;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Extracts form fields from a page using Playwright.
///     Reads structure and attributes only - never reads field values.
/// </summary>
internal static class FormFieldExtractor
{
    /// <summary>
    ///     JavaScript injected into the page to extract field metadata.
    ///     Returns an array of raw field objects with structural attributes only.
    /// </summary>
    private const string ExtractionScript = """
        () => {
            function buildSelector(el) {
                if (el.id) return '#' + el.id;
                if (el.name) {
                    const tag = el.tagName.toLowerCase();
                    const byName = document.querySelectorAll(tag + '[name="' + el.name + '"]');
                    if (byName.length === 1) return tag + '[name="' + el.name + '"]';
                }
                // Fallback: build a path
                const parts = [];
                let current = el;
                while (current && current !== document.body) {
                    let sel = current.tagName.toLowerCase();
                    if (current.id) { parts.unshift('#' + current.id); break; }
                    const parent = current.parentElement;
                    if (parent) {
                        const siblings = Array.from(parent.children).filter(c => c.tagName === current.tagName);
                        if (siblings.length > 1) {
                            sel += ':nth-of-type(' + (siblings.indexOf(current) + 1) + ')';
                        }
                    }
                    parts.unshift(sel);
                    current = current.parentElement;
                }
                return parts.join(' > ');
            }

            function findLabel(el) {
                // 1. Explicit <label for="">
                if (el.id) {
                    const label = document.querySelector('label[for="' + el.id + '"]');
                    if (label) return label.textContent.trim();
                }
                // 2. aria-label
                const ariaLabel = el.getAttribute('aria-label');
                if (ariaLabel) return ariaLabel;
                // 3. aria-labelledby
                const labelledBy = el.getAttribute('aria-labelledby');
                if (labelledBy) {
                    const ref = document.getElementById(labelledBy);
                    if (ref) return ref.textContent.trim();
                }
                // 4. Closest ancestor label
                const parentLabel = el.closest('label');
                if (parentLabel) {
                    // Get text excluding the input's own text
                    const clone = parentLabel.cloneNode(true);
                    const inputs = clone.querySelectorAll('input,select,textarea');
                    inputs.forEach(i => i.remove());
                    const text = clone.textContent.trim();
                    if (text) return text;
                }
                // 5. Placeholder as last resort
                if (el.placeholder) return el.placeholder;
                return el.name || '';
            }

            const fields = [];
            const selector = 'input, select, textarea, [role="textbox"], [role="combobox"], [role="spinbutton"], [contenteditable="true"]';
            document.querySelectorAll(selector).forEach(el => {
                // Skip hidden/submit/button inputs
                const type = (el.type || el.tagName.toLowerCase()).toLowerCase();
                if (type === 'hidden' || type === 'submit' || type === 'button' || type === 'reset') return;

                const computed = window.getComputedStyle(el);
                if (computed.display === 'none' || computed.visibility === 'hidden') return;

                fields.push({
                    selector: buildSelector(el),
                    type: type,
                    name: el.name || null,
                    label: findLabel(el),
                    placeholder: el.placeholder || null,
                    required: el.required || el.getAttribute('aria-required') === 'true',
                    pattern: el.pattern || null,
                    minLength: el.minLength > 0 ? el.minLength : null,
                    maxLength: el.maxLength > 0 && el.maxLength < 524288 ? el.maxLength : null,
                    min: el.min || null,
                    max: el.max || null,
                    autocomplete: el.autocomplete || null,
                    ariaDescribedBy: el.getAttribute('aria-describedby') || null,
                    ariaErrorMessage: el.getAttribute('aria-errormessage') || null,
                });
            });
            return JSON.stringify({ fields: fields, title: document.title });
        }
        """;

    /// <summary>
    ///     Extract all interactive fields from the current page.
    /// </summary>
    public static async Task<(List<FieldDefinition> Fields, string PageTitle)> ExtractAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(ExtractionScript);
        var raw = JsonSerializer.Deserialize<RawPageData>(json, JsonOpts);
        if (raw is null)
            return ([], "");

        var fields = new List<FieldDefinition>();
        foreach (var f in raw.Fields)
        {
            var attrs = new FieldAttributes
            {
                Type = f.Type,
                Name = f.Name,
                Autocomplete = NormalizeAutocomplete(f.Autocomplete),
                Placeholder = f.Placeholder,
                AriaLabel = f.Label
            };

            var clientValidation = new List<string>();
            if (f.Required) clientValidation.Add("required");
            if (!string.IsNullOrEmpty(f.Pattern)) clientValidation.Add($"pattern `{f.Pattern}`");
            if (f.MinLength.HasValue) clientValidation.Add($"minlength {f.MinLength}");
            if (f.MaxLength.HasValue) clientValidation.Add($"maxlength {f.MaxLength}");
            if (!string.IsNullOrEmpty(f.Min)) clientValidation.Add($"min {f.Min}");
            if (!string.IsNullOrEmpty(f.Max)) clientValidation.Add($"max {f.Max}");

            var pattern = PatternRegistry.Detect(attrs);

            fields.Add(new FieldDefinition
            {
                Selector = f.Selector,
                Label = f.Label ?? f.Name ?? f.Selector,
                Type = f.Type,
                DisplayLabel = f.Label,
                Placeholder = f.Placeholder,
                Pattern = pattern,
                ClientValidation = clientValidation,
                Help = null, // Populated by human editing
                Autocomplete = NormalizeAutocomplete(f.Autocomplete),
                Required = f.Required,
                MinLength = f.MinLength,
                MaxLength = f.MaxLength
            });
        }

        return (fields, raw.Title ?? "");
    }

    private static string? NormalizeAutocomplete(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "on" || value == "off")
            return null;
        return value;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RawPageData
    {
        [JsonPropertyName("fields")] public List<RawField> Fields { get; init; } = [];
        [JsonPropertyName("title")] public string? Title { get; init; }
    }

    private sealed record RawField
    {
        [JsonPropertyName("selector")] public string Selector { get; init; } = "";
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("label")] public string? Label { get; init; }
        [JsonPropertyName("placeholder")] public string? Placeholder { get; init; }
        [JsonPropertyName("required")] public bool Required { get; init; }
        [JsonPropertyName("pattern")] public string? Pattern { get; init; }
        [JsonPropertyName("minLength")] public int? MinLength { get; init; }
        [JsonPropertyName("maxLength")] public int? MaxLength { get; init; }
        [JsonPropertyName("min")] public string? Min { get; init; }
        [JsonPropertyName("max")] public string? Max { get; init; }
        [JsonPropertyName("autocomplete")] public string? Autocomplete { get; init; }
        [JsonPropertyName("ariaDescribedBy")] public string? AriaDescribedBy { get; init; }
        [JsonPropertyName("ariaErrorMessage")] public string? AriaErrorMessage { get; init; }
    }
}
