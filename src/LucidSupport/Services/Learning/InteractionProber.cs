using System.Text.Json;
using System.Text.Json.Serialization;
using LucidSupport.Models;
using Microsoft.Playwright;

namespace LucidSupport.Services.Learning;

/// <summary>
///     Actively probes a page by interacting with fields to discover behavior:
///     - Fill fields with test data to trigger validation
///     - Focus/blur to detect focus-dependent UI changes
///     - Detect conditional fields (show/hide on other field changes)
///     - Capture before/after DOM diffs for state changes
///     - Detect form dependencies between fields
/// </summary>
internal static class InteractionProber
{
    /// <summary>
    ///     Probe results for the entire page.
    /// </summary>
    public sealed record ProbeResult
    {
        /// <summary>Per-field probe results.</summary>
        public List<FieldProbeResult> Fields { get; init; } = [];

        /// <summary>Detected field dependencies (field A changes → field B appears/changes).</summary>
        public List<FieldDependency> Dependencies { get; init; } = [];

        /// <summary>Conditional fields that are hidden by default and appear on interaction.</summary>
        public List<string> ConditionalFields { get; init; } = [];
    }

    /// <summary>
    ///     Results of probing a single field.
    /// </summary>
    public sealed record FieldProbeResult
    {
        public required string Selector { get; init; }

        /// <summary>What happens when the field receives focus.</summary>
        public FocusEffect FocusEffect { get; init; } = new();

        /// <summary>Validation messages triggered by different test inputs.</summary>
        public List<ValidationProbe> ValidationProbes { get; init; } = [];

        /// <summary>Whether the field has a character counter that updates on input.</summary>
        public bool HasCharCounter { get; init; }

        /// <summary>Whether the field has a strength indicator (e.g., password strength).</summary>
        public bool HasStrengthIndicator { get; init; }

        /// <summary>Whether the field has autocomplete/suggestions dropdown.</summary>
        public bool HasAutocompleteSuggestions { get; init; }

        /// <summary>Whether the field masks input (like password fields).</summary>
        public bool MasksInput { get; init; }

        /// <summary>Whether the field has a show/hide toggle (for password fields).</summary>
        public bool HasVisibilityToggle { get; init; }
    }

    /// <summary>
    ///     What happens when a field receives focus.
    /// </summary>
    public sealed record FocusEffect
    {
        /// <summary>Border/outline changes on focus.</summary>
        public bool StyleChanges { get; init; }

        /// <summary>A tooltip or help text appears on focus.</summary>
        public bool ShowsHelp { get; init; }

        /// <summary>Help text content that appears.</summary>
        public string? HelpText { get; init; }

        /// <summary>Label floats/animates on focus (Material Design pattern).</summary>
        public bool HasFloatingLabel { get; init; }
    }

    /// <summary>
    ///     Result of entering test data into a field.
    /// </summary>
    public sealed record ValidationProbe
    {
        /// <summary>What was tested (e.g., "empty", "partial", "invalid-format").</summary>
        public required string TestCase { get; init; }

        /// <summary>Error message that appeared (null if none).</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Whether the field was marked invalid.</summary>
        public bool IsInvalid { get; init; }

        /// <summary>Whether new DOM elements appeared (error messages, icons).</summary>
        public bool NewElementsAppeared { get; init; }
    }

    /// <summary>
    ///     A dependency between two fields.
    /// </summary>
    public sealed record FieldDependency
    {
        /// <summary>The field that triggers the change.</summary>
        public required string TriggerSelector { get; init; }

        /// <summary>The field that is affected.</summary>
        public required string AffectedSelector { get; init; }

        /// <summary>What kind of effect (show, hide, enable, disable, change-options).</summary>
        public required string Effect { get; init; }
    }

    /// <summary>
    ///     Probe all fields on the page for interactive behavior.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(IPage page, List<FieldDefinition> fields)
    {
        var fieldResults = new List<FieldProbeResult>();
        var dependencies = new List<FieldDependency>();
        var conditionalFields = new List<string>();

        // Snapshot initial DOM state
        var initialState = await CaptureFieldStatesAsync(page);

        foreach (var field in fields)
        {
            try
            {
                var result = await ProbeFieldAsync(page, field, initialState);
                fieldResults.Add(result);
            }
            catch
            {
                // Individual field probe failure shouldn't stop the whole process
                fieldResults.Add(new FieldProbeResult { Selector = field.Selector });
            }
        }

        // Detect field dependencies: change each select/radio and see what appears
        var selectFields = fields.Where(f =>
            f.Type is "select" or "radio" or "checkbox").ToList();

        foreach (var trigger in selectFields)
        {
            try
            {
                var deps = await ProbeFieldDependenciesAsync(page, trigger, fields, initialState);
                dependencies.AddRange(deps);
            }
            catch { /* best-effort */ }
        }

        // Detect conditional fields (hidden in initial state, appeared during probing)
        var finalState = await CaptureFieldStatesAsync(page);
        foreach (var sel in finalState.Keys)
        {
            if (!initialState.ContainsKey(sel) && finalState[sel].IsVisible)
                conditionalFields.Add(sel);
        }

        return new ProbeResult
        {
            Fields = fieldResults,
            Dependencies = dependencies,
            ConditionalFields = conditionalFields
        };
    }

    private static async Task<FieldProbeResult> ProbeFieldAsync(
        IPage page, FieldDefinition field, Dictionary<string, RawFieldState> initialState)
    {
        var locator = page.Locator(field.Selector);
        if (await locator.CountAsync() == 0)
            return new FieldProbeResult { Selector = field.Selector };

        // 1. Test focus effects
        var focusEffect = await ProbeFocusAsync(page, field.Selector);

        // 2. Test validation with various inputs
        var validationProbes = await ProbeValidationAsync(page, field);

        // 3. Detect UI features
        var features = await DetectFieldFeaturesAsync(page, field.Selector);

        return new FieldProbeResult
        {
            Selector = field.Selector,
            FocusEffect = focusEffect,
            ValidationProbes = validationProbes,
            HasCharCounter = features.HasCharCounter,
            HasStrengthIndicator = features.HasStrengthIndicator,
            HasAutocompleteSuggestions = features.HasAutocompleteSuggestions,
            MasksInput = features.MasksInput,
            HasVisibilityToggle = features.HasVisibilityToggle
        };
    }

    private static async Task<FocusEffect> ProbeFocusAsync(IPage page, string selector)
    {
        var script = $$"""
            (sel) => {
                const el = document.querySelector(sel);
                if (!el) return { styleChanges: false, showsHelp: false, helpText: null, hasFloatingLabel: false };

                // Capture before-focus state
                const cs = window.getComputedStyle(el);
                const beforeBorder = cs.borderColor;
                const beforeOutline = cs.outline;
                const beforeShadow = cs.boxShadow;

                // Check for associated label
                const labelEl = el.id ? document.querySelector('label[for="' + el.id + '"]') : el.closest('label');
                const labelBefore = labelEl ? window.getComputedStyle(labelEl).transform : '';
                const labelPosBefore = labelEl ? labelEl.getBoundingClientRect().top : 0;

                // Focus the element
                el.focus();

                // Capture after-focus state
                const csAfter = window.getComputedStyle(el);
                const styleChanges = csAfter.borderColor !== beforeBorder
                    || csAfter.outline !== beforeOutline
                    || csAfter.boxShadow !== beforeShadow;

                // Check label movement (floating label)
                const labelPosAfter = labelEl ? labelEl.getBoundingClientRect().top : 0;
                const hasFloatingLabel = labelEl && Math.abs(labelPosAfter - labelPosBefore) > 5;

                // Check for help text appearing
                let helpText = null;
                let showsHelp = false;
                const descId = el.getAttribute('aria-describedby');
                if (descId) {
                    const helpEl = document.getElementById(descId);
                    if (helpEl && helpEl.offsetParent !== null) {
                        const text = helpEl.textContent.trim();
                        const classes = helpEl.className.toString().toLowerCase();
                        if (text && classes.match(/help|hint|description|info/)) {
                            showsHelp = true;
                            helpText = text;
                        }
                    }
                }

                // Check for tooltip appearing near the field
                if (!showsHelp) {
                    const tooltips = document.querySelectorAll('[role="tooltip"], .tooltip, [data-tooltip]');
                    tooltips.forEach(t => {
                        if (t.offsetParent !== null && t.textContent.trim()) {
                            showsHelp = true;
                            helpText = t.textContent.trim();
                        }
                    });
                }

                el.blur();

                return { styleChanges, showsHelp, helpText, hasFloatingLabel };
            }
            """;

        try
        {
            var json = await page.EvaluateAsync<string>($"(sel) => JSON.stringify(({script})(sel))", selector);
            var result = JsonSerializer.Deserialize<RawFocusEffect>(json, JsonOpts);
            return new FocusEffect
            {
                StyleChanges = result?.StyleChanges ?? false,
                ShowsHelp = result?.ShowsHelp ?? false,
                HelpText = result?.HelpText,
                HasFloatingLabel = result?.HasFloatingLabel ?? false
            };
        }
        catch
        {
            return new FocusEffect();
        }
    }

    private static async Task<List<ValidationProbe>> ProbeValidationAsync(IPage page, FieldDefinition field)
    {
        var probes = new List<ValidationProbe>();
        var locator = page.Locator(field.Selector);

        // Test cases based on field type
        var testCases = GetTestCases(field);

        foreach (var (testName, testValue) in testCases)
        {
            try
            {
                // Clear and fill
                await locator.ClearAsync();
                if (!string.IsNullOrEmpty(testValue))
                    await locator.FillAsync(testValue);

                // Trigger blur to invoke validation
                await locator.BlurAsync();
                await page.WaitForTimeoutAsync(300); // Allow time for validation UI

                // Check for validation state
                var validationState = await page.EvaluateAsync<string>($$"""
                    (sel) => {
                        {{DomScriptSnippets.BuildStableSelectorFunction}}
                        {{DomScriptSnippets.ErrorDetectionHelpers}}
                        const el = document.querySelector(sel);
                        if (!el) return JSON.stringify({ isInvalid: false, errorMessage: null, newElements: false });

                        const isInvalid = isFieldInErrorState(el);
                        let errorMessage = el.validationMessage || null;
                        const errorInfo = findFieldErrorMessage(el);
                        if (errorInfo && errorInfo.text) errorMessage = errorInfo.text;

                        return JSON.stringify({ isInvalid, errorMessage, newElements: !!errorMessage });
                    }
                    """, field.Selector);

                var state = JsonSerializer.Deserialize<RawValidationState>(validationState, JsonOpts);
                probes.Add(new ValidationProbe
                {
                    TestCase = testName,
                    ErrorMessage = state?.ErrorMessage,
                    IsInvalid = state?.IsInvalid ?? false,
                    NewElementsAppeared = state?.NewElements ?? false
                });
            }
            catch
            {
                probes.Add(new ValidationProbe
                {
                    TestCase = testName,
                    IsInvalid = false
                });
            }
        }

        // Clean up: clear the field
        try { await locator.ClearAsync(); } catch { /* ignore */ }

        return probes;
    }

    private static List<(string Name, string Value)> GetTestCases(FieldDefinition field)
    {
        var cases = new List<(string, string)>
        {
            ("empty", "")
        };

        switch (field.Pattern)
        {
            case "email":
                cases.Add(("partial", "test@"));
                cases.Add(("invalid-format", "not-an-email"));
                break;
            case "phone":
                cases.Add(("partial", "555"));
                cases.Add(("letters", "abc"));
                break;
            case "credit-card":
                cases.Add(("partial", "4242"));
                cases.Add(("letters", "abcd"));
                break;
            case "cvv":
                cases.Add(("short", "1"));
                cases.Add(("letters", "ab"));
                break;
            case "postal-code":
                cases.Add(("partial", "1"));
                break;
            case "url":
                cases.Add(("partial", "http://"));
                cases.Add(("invalid", "not-a-url"));
                break;
            case "password":
                cases.Add(("short", "a"));
                break;
            default:
                // For unknown patterns, test with a single character
                if (field.Type is "email")
                    cases.Add(("invalid-format", "not-an-email"));
                else if (field.Type is "number")
                    cases.Add(("letters", "abc"));
                break;
        }

        return cases;
    }

    private static async Task<(bool HasCharCounter, bool HasStrengthIndicator,
        bool HasAutocompleteSuggestions, bool MasksInput, bool HasVisibilityToggle)>
        DetectFieldFeaturesAsync(IPage page, string selector)
    {
        var script = $$"""
            (sel) => {
                const el = document.querySelector(sel);
                if (!el) return JSON.stringify({ charCounter: false, strength: false, autocomplete: false, masks: false, toggle: false });

                const masksInput = el.type === 'password';

                // Check for toggle button (show/hide password)
                const parent = el.parentElement;
                let hasToggle = false;
                if (parent) {
                    const buttons = parent.querySelectorAll('button, [role="button"]');
                    buttons.forEach(btn => {
                        const text = (btn.textContent || btn.getAttribute('aria-label') || '').toLowerCase();
                        if (text.match(/show|hide|eye|toggle|reveal/)) hasToggle = true;
                    });
                }

                // Check for char counter (usually a small text below/beside showing "0/100" pattern)
                let hasCharCounter = false;
                if (parent) {
                    const texts = parent.querySelectorAll('span, div, p');
                    texts.forEach(t => {
                        if (t.textContent.trim().match(/^\d+\s*\/\s*\d+$/)) hasCharCounter = true;
                    });
                }

                // Check for strength indicator
                let hasStrength = false;
                if (parent) {
                    const indicators = parent.querySelectorAll('[class*="strength"], [class*="meter"], [role="progressbar"]');
                    if (indicators.length > 0) hasStrength = true;
                }

                // Autocomplete suggestions (datalist or custom dropdown)
                const hasAutocomplete = !!el.getAttribute('list') || !!parent?.querySelector('[role="listbox"], .autocomplete-list, .suggestions');

                return JSON.stringify({ charCounter: hasCharCounter, strength: hasStrength, autocomplete: hasAutocomplete, masks: masksInput, toggle: hasToggle });
            }
            """;

        try
        {
            var json = await page.EvaluateAsync<string>($"(sel) => ({script})(sel)", selector);
            var result = JsonSerializer.Deserialize<RawFieldFeatures>(json, JsonOpts);
            return (
                result?.CharCounter ?? false,
                result?.Strength ?? false,
                result?.Autocomplete ?? false,
                result?.Masks ?? false,
                result?.Toggle ?? false
            );
        }
        catch
        {
            return (false, false, false, false, false);
        }
    }

    private static async Task<List<FieldDependency>> ProbeFieldDependenciesAsync(
        IPage page, FieldDefinition trigger, List<FieldDefinition> allFields,
        Dictionary<string, RawFieldState> initialState)
    {
        var deps = new List<FieldDependency>();
        var locator = page.Locator(trigger.Selector);
        if (await locator.CountAsync() == 0) return deps;

        // For selects: try each option
        if (trigger.Type == "select")
        {
            var options = await locator.Locator("option").AllTextContentsAsync();
            foreach (var opt in options.Take(5)) // Limit to first 5 options
            {
                try
                {
                    await locator.SelectOptionAsync(new SelectOptionValue { Label = opt });
                    await page.WaitForTimeoutAsync(500);

                    var newState = await CaptureFieldStatesAsync(page);
                    DetectChanges(trigger.Selector, initialState, newState, deps);
                }
                catch { /* ignore */ }
            }
        }
        // For checkboxes: toggle
        else if (trigger.Type == "checkbox")
        {
            try
            {
                await locator.CheckAsync();
                await page.WaitForTimeoutAsync(500);

                var newState = await CaptureFieldStatesAsync(page);
                DetectChanges(trigger.Selector, initialState, newState, deps);

                await locator.UncheckAsync();
            }
            catch { /* ignore */ }
        }

        return deps;
    }

    private static void DetectChanges(string triggerSelector,
        Dictionary<string, RawFieldState> before,
        Dictionary<string, RawFieldState> after,
        List<FieldDependency> deps)
    {
        // Fields that appeared
        foreach (var (sel, state) in after)
        {
            if (!before.ContainsKey(sel) && state.IsVisible)
            {
                deps.Add(new FieldDependency
                {
                    TriggerSelector = triggerSelector,
                    AffectedSelector = sel,
                    Effect = "show"
                });
            }
        }

        // Fields that disappeared
        foreach (var (sel, state) in before)
        {
            if (state.IsVisible && (!after.ContainsKey(sel) || !after[sel].IsVisible))
            {
                deps.Add(new FieldDependency
                {
                    TriggerSelector = triggerSelector,
                    AffectedSelector = sel,
                    Effect = "hide"
                });
            }
        }

        // Fields that changed enabled/disabled state
        foreach (var (sel, state) in after)
        {
            if (before.TryGetValue(sel, out var prevState))
            {
                if (prevState.IsDisabled && !state.IsDisabled)
                    deps.Add(new FieldDependency { TriggerSelector = triggerSelector, AffectedSelector = sel, Effect = "enable" });
                else if (!prevState.IsDisabled && state.IsDisabled)
                    deps.Add(new FieldDependency { TriggerSelector = triggerSelector, AffectedSelector = sel, Effect = "disable" });
            }
        }
    }

    private static async Task<Dictionary<string, RawFieldState>> CaptureFieldStatesAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>($$"""
            () => {
                {{DomScriptSnippets.BuildStableSelectorFunction}}
                const result = {};
                document.querySelectorAll('input, select, textarea, [role="textbox"]').forEach(el => {
                    const sel = buildStableSelector(el);
                    if (!sel) return;
                    const cs = window.getComputedStyle(el);
                    result[sel] = {
                        isVisible: cs.display !== 'none' && cs.visibility !== 'hidden' && el.offsetParent !== null,
                        isDisabled: el.disabled || el.getAttribute('aria-disabled') === 'true'
                    };
                });
                return JSON.stringify(result);
            }
            """);

        return JsonSerializer.Deserialize<Dictionary<string, RawFieldState>>(json, JsonOpts)
               ?? new Dictionary<string, RawFieldState>();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RawFieldState
    {
        [JsonPropertyName("isVisible")] public bool IsVisible { get; init; }
        [JsonPropertyName("isDisabled")] public bool IsDisabled { get; init; }
    }

    private sealed record RawFocusEffect
    {
        [JsonPropertyName("styleChanges")] public bool StyleChanges { get; init; }
        [JsonPropertyName("showsHelp")] public bool ShowsHelp { get; init; }
        [JsonPropertyName("helpText")] public string? HelpText { get; init; }
        [JsonPropertyName("hasFloatingLabel")] public bool HasFloatingLabel { get; init; }
    }

    private sealed record RawValidationState
    {
        [JsonPropertyName("isInvalid")] public bool IsInvalid { get; init; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
        [JsonPropertyName("newElements")] public bool NewElements { get; init; }
    }

    private sealed record RawFieldFeatures
    {
        [JsonPropertyName("charCounter")] public bool CharCounter { get; init; }
        [JsonPropertyName("strength")] public bool Strength { get; init; }
        [JsonPropertyName("autocomplete")] public bool Autocomplete { get; init; }
        [JsonPropertyName("masks")] public bool Masks { get; init; }
        [JsonPropertyName("toggle")] public bool Toggle { get; init; }
    }
}
