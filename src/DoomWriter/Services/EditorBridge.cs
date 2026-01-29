using System.Text.Json;
using DoomWriter.Models;
using DoomWriter.ViewModels;

namespace DoomWriter.Services;

/// <summary>
/// Bridge between C# (Avalonia) and the Vditor JavaScript editor.
/// Handles bidirectional communication via WebView message protocol.
/// </summary>
public class EditorBridge
{
    private readonly EditorViewModel _editor;

    // Delegate for invoking JavaScript in the WebView
    private Func<string, Task<string?>>? _invokeScript;

    public EditorBridge(EditorViewModel editor)
    {
        _editor = editor;
    }

    /// <summary>
    /// Set the JavaScript invocation function from the WebView control.
    /// Called during view initialization.
    /// </summary>
    public void SetInvokeScript(Func<string, Task<string?>> invokeScript)
    {
        _invokeScript = invokeScript;
    }

    // --- C# → JS commands ---

    /// <summary>Get the current editor content.</summary>
    public async Task<string> GetContentAsync()
    {
        if (_invokeScript == null) return "";
        return await _invokeScript("vditor.getValue()") ?? "";
    }

    /// <summary>Set the full editor content (for file open).</summary>
    public async Task SetContentAsync(string markdown)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(markdown);
        await _invokeScript($"vditor.setValue({escaped})");
    }

    /// <summary>Insert text at the current cursor position.</summary>
    public async Task InsertAtCursorAsync(string text)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(text);
        await _invokeScript($"vditor.insertValue({escaped})");
    }

    /// <summary>Get the current cursor position (character offset).</summary>
    public async Task<int> GetCursorPositionAsync()
    {
        if (_invokeScript == null) return 0;
        var result = await _invokeScript("getCursorPosition()");
        return int.TryParse(result, out var pos) ? pos : 0;
    }

    /// <summary>Set the editor theme.</summary>
    public async Task SetThemeAsync(string theme, string contentTheme, string codeTheme)
    {
        if (_invokeScript == null) return;
        var t = JsonSerializer.Serialize(theme);
        var ct = JsonSerializer.Serialize(contentTheme);
        var cdt = JsonSerializer.Serialize(codeTheme);
        await _invokeScript($"vditor.setTheme({t}, {ct}, {cdt})");
    }

    /// <summary>Focus the editor.</summary>
    public async Task FocusAsync()
    {
        if (_invokeScript == null) return;
        await _invokeScript("vditor.focus()");
    }

    /// <summary>Set the editor mode (ir, wysiwyg, sv).</summary>
    public async Task SetModeAsync(string mode)
    {
        if (_invokeScript == null) return;
        var m = JsonSerializer.Serialize(mode);
        await _invokeScript($"setEditorMode({m})");
    }

    // --- C# → JS: Navigation ---

    /// <summary>Scroll to a heading by text.</summary>
    public async Task ScrollToHeadingAsync(string headingText)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(headingText);
        await _invokeScript($"scrollToHeading({escaped})");
    }

    /// <summary>Scroll to a text fragment in the editor.</summary>
    public async Task ScrollToTextAsync(string text)
    {
        if (_invokeScript == null) return;
        var escaped = JsonSerializer.Serialize(text);
        await _invokeScript($"scrollToText({escaped})");
    }

    // --- C# → JS: Autocomplete ---

    /// <summary>Show autocomplete suggestion in the editor (ghost text or popup).</summary>
    public async Task ShowAutocompleteSuggestionAsync(AutocompleteResult result)
    {
        if (_invokeScript == null) return;
        var json = JsonSerializer.Serialize(new
        {
            kind = result.Kind.ToString().ToLowerInvariant(),
            suggestions = result.Suggestions.Select(s => new
            {
                text = s.Text,
                displayText = s.DisplayText,
                kind = s.Kind.ToString().ToLowerInvariant()
            })
        });
        await _invokeScript($"showAutocompleteSuggestion({json})");
    }

    /// <summary>Show AI generation indicator.</summary>
    public async Task ShowAiGeneratingAsync(string message)
    {
        if (_invokeScript == null) return;
        var m = JsonSerializer.Serialize(message);
        await _invokeScript($"showAiGenerating({m})");
    }

    /// <summary>Hide AI generation indicator.</summary>
    public async Task HideAiGeneratingAsync()
    {
        if (_invokeScript == null) return;
        await _invokeScript("hideAiGenerating()");
    }

    // --- C# → JS: Diagnostics ---

    /// <summary>Show diagnostics (spelling, grammar, entity links) in the editor.</summary>
    public async Task ShowDiagnosticsAsync(List<DiagnosticItem> items)
    {
        if (_invokeScript == null) return;
        var json = JsonSerializer.Serialize(items.Select(d => new
        {
            type = d.Type.ToString().ToLowerInvariant(),
            word = d.Word,
            line = d.Line,
            col = d.Column,
            length = d.Length,
            message = d.Message,
            suggestions = d.Suggestions,
            quickFix = d.QuickFixText
        }));
        await _invokeScript($"showDiagnostics({json})");
    }

    /// <summary>Clear all diagnostics from the editor.</summary>
    public async Task ClearDiagnosticsAsync()
    {
        if (_invokeScript == null) return;
        await _invokeScript("clearDiagnostics()");
    }

    // --- JS → C# message handling ---

    /// <summary>
    /// Raised when autocomplete is requested from the editor.
    /// </summary>
    public event EventHandler<string>? AutocompleteRequested;

    /// <summary>
    /// Raised when a quick action is requested from the editor (Ctrl+. or lightbulb click).
    /// </summary>
    public event EventHandler<QuickActionRequest>? QuickActionRequested;

    /// <summary>
    /// Raised when spell check is requested for a paragraph (on typing pause).
    /// </summary>
    public event EventHandler<string>? SpellCheckRequested;

    /// <summary>
    /// Handle a message received from the WebView (Vditor → C#).
    /// Call this from the WebView's WebMessageReceived event.
    /// </summary>
    public void HandleWebMessage(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "contentChanged":
                    var content = root.GetProperty("content").GetString() ?? "";
                    var cursor = root.GetProperty("cursor").GetInt32();
                    _editor.OnContentChanged(content, cursor);
                    break;

                case "toolbarAction":
                    var action = root.GetProperty("action").GetString() ?? "";
                    _editor.OnToolbarAction(action);
                    break;

                case "selection":
                    var text = root.GetProperty("text").GetString() ?? "";
                    var from = root.GetProperty("from").GetInt32();
                    var to = root.GetProperty("to").GetInt32();
                    _editor.OnTextSelected(text, from, to);
                    break;

                case "ready":
                    _editor.OnEditorReady();
                    break;

                case "modeChanged":
                    var mode = root.GetProperty("mode").GetString() ?? "";
                    _editor.OnModeChanged(mode);
                    break;

                case "autocompleteRequest":
                    var textBefore = root.GetProperty("textBeforeCursor").GetString() ?? "";
                    AutocompleteRequested?.Invoke(this, textBefore);
                    break;

                case "quickAction":
                    var qaLine = root.GetProperty("line").GetInt32();
                    var qaCol = root.GetProperty("col").GetInt32();
                    var qaWord = root.GetProperty("word").GetString() ?? "";
                    QuickActionRequested?.Invoke(this, new QuickActionRequest
                    {
                        Line = qaLine,
                        Column = qaCol,
                        Word = qaWord
                    });
                    break;

                case "spellCheck":
                    var paragraph = root.GetProperty("text").GetString() ?? "";
                    SpellCheckRequested?.Invoke(this, paragraph);
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }
}
