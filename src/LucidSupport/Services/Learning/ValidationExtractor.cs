using System.Text.Json;
using System.Text.Json.Serialization;
using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Extracts client-side and server-side validation rules by interacting with the page.
///     Submits empty/invalid data to trigger validation messages.
/// </summary>
internal static class ValidationExtractor
{
    /// <summary>
    ///     JavaScript to trigger HTML5 constraint validation and capture error messages.
    /// </summary>
    private static readonly string TriggerValidationScript = $$"""
        () => {
            {{DomScriptSnippets.BuildStableSelectorFunction}}
            {{DomScriptSnippets.ErrorDetectionHelpers}}
            const results = [];
            const forms = document.querySelectorAll('form');

            forms.forEach(form => {
                // Trigger validation without actually submitting
                const isValid = form.checkValidity();

                form.querySelectorAll('input, select, textarea').forEach(el => {
                    if (el.type === 'hidden' || el.type === 'submit') return;

                    const validity = el.validity;
                    const messages = [];

                    if (validity.valueMissing) messages.push({ key: 'required', text: el.validationMessage });
                    if (validity.typeMismatch) messages.push({ key: 'type', text: el.validationMessage });
                    if (validity.patternMismatch) messages.push({ key: 'pattern', text: el.validationMessage });
                    if (validity.tooShort) messages.push({ key: 'minlength', text: el.validationMessage });
                    if (validity.tooLong) messages.push({ key: 'maxlength', text: el.validationMessage });
                    if (validity.rangeUnderflow) messages.push({ key: 'min', text: el.validationMessage });
                    if (validity.rangeOverflow) messages.push({ key: 'max', text: el.validationMessage });

                    // Check for visible error messages via aria-errormessage or aria-describedby
                    const errorInfo = findFieldErrorMessage(el);
                    const customError = errorInfo ? errorInfo.text : null;

                    const selector = buildStableSelector(el);
                    if (selector) {
                        results.push({
                            selector: selector,
                            messages: messages,
                            customError: customError,
                            isInvalid: isFieldInErrorState(el)
                        });
                    }
                });
            });
            return JSON.stringify(results);
        }
        """;

    /// <summary>
    ///     Extract validation error messages for fields on the page.
    ///     Triggers HTML5 form validation to capture constraint messages.
    /// </summary>
    /// <returns>Dictionary mapping field selector to error key/message pairs.</returns>
    public static async Task<Dictionary<string, Dictionary<string, string>>> ExtractAsync(IPage page)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();

        try
        {
            var json = await page.EvaluateAsync<string>(TriggerValidationScript);
            var entries = JsonSerializer.Deserialize<List<ValidationEntry>>(json, JsonOpts);
            if (entries is null) return result;

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Selector)) continue;

                var errors = new Dictionary<string, string>();

                foreach (var msg in entry.Messages)
                {
                    if (!string.IsNullOrWhiteSpace(msg.Text))
                        errors[msg.Key] = msg.Text;
                }

                if (!string.IsNullOrWhiteSpace(entry.CustomError) && errors.Count == 0)
                    errors["custom"] = entry.CustomError;

                if (errors.Count > 0)
                    result[entry.Selector] = errors;
            }
        }
        catch
        {
            // Validation extraction is best-effort; don't fail the whole learning process
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record ValidationEntry
    {
        [JsonPropertyName("selector")] public string? Selector { get; init; }
        [JsonPropertyName("messages")] public List<ValidationMessage> Messages { get; init; } = [];
        [JsonPropertyName("customError")] public string? CustomError { get; init; }
        [JsonPropertyName("isInvalid")] public bool IsInvalid { get; init; }
    }

    private sealed record ValidationMessage
    {
        [JsonPropertyName("key")] public string Key { get; init; } = "";
        [JsonPropertyName("text")] public string? Text { get; init; }
    }
}
