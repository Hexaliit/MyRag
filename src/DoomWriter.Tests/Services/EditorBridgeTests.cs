using System.Text.Json;
using DoomWriter.Services;
using DoomWriter.ViewModels;

namespace DoomWriter.Tests.Services;

public class EditorBridgeTests
{
    private readonly EditorViewModel _editor;
    private readonly EditorBridge _sut;

    public EditorBridgeTests()
    {
        _editor = new EditorViewModel();
        _sut = new EditorBridge(_editor);
    }

    [Fact]
    public void HandleWebMessage_ContentChanged_UpdatesEditor()
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "contentChanged",
            content = "# Hello\n\nWorld",
            cursor = 15
        });

        _sut.HandleWebMessage(message);

        _editor.Content.Should().Be("# Hello\n\nWorld");
        _editor.CursorPosition.Should().Be(15);
        _editor.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void HandleWebMessage_ToolbarAction_RaisesEvent()
    {
        string? receivedAction = null;
        _editor.ToolbarAction += (_, action) => receivedAction = action;

        var message = JsonSerializer.Serialize(new
        {
            type = "toolbarAction",
            action = "nextParagraph"
        });

        _sut.HandleWebMessage(message);

        receivedAction.Should().Be("nextParagraph");
    }

    [Fact]
    public void HandleWebMessage_Selection_UpdatesEditorState()
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "selection",
            text = "selected text",
            from = 10,
            to = 23
        });

        _sut.HandleWebMessage(message);

        _editor.SelectedText.Should().Be("selected text");
        _editor.SelectionStart.Should().Be(10);
        _editor.SelectionEnd.Should().Be(23);
    }

    [Fact]
    public void HandleWebMessage_Ready_SetsEditorReady()
    {
        _editor.IsEditorReady.Should().BeFalse();

        var message = JsonSerializer.Serialize(new { type = "ready" });
        _sut.HandleWebMessage(message);

        _editor.IsEditorReady.Should().BeTrue();
    }

    [Fact]
    public void HandleWebMessage_ModeChanged_UpdatesEditorMode()
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "modeChanged",
            mode = "wysiwyg"
        });

        _sut.HandleWebMessage(message);

        _editor.EditorMode.Should().Be("wysiwyg");
    }

    [Fact]
    public void HandleWebMessage_AutocompleteRequest_RaisesEvent()
    {
        string? received = null;
        _sut.AutocompleteRequested += (_, text) => received = text;

        var message = JsonSerializer.Serialize(new
        {
            type = "autocompleteRequest",
            textBeforeCursor = "The quick brown "
        });

        _sut.HandleWebMessage(message);

        received.Should().Be("The quick brown ");
    }

    [Fact]
    public void HandleWebMessage_MalformedJson_DoesNotThrow()
    {
        var act = () => _sut.HandleWebMessage("not valid json at all");
        act.Should().NotThrow();
    }

    [Fact]
    public void HandleWebMessage_UnknownType_DoesNotThrow()
    {
        var message = JsonSerializer.Serialize(new { type = "unknownType", data = "whatever" });
        var act = () => _sut.HandleWebMessage(message);
        act.Should().NotThrow();
    }

    [Fact]
    public void HandleWebMessage_EmptyString_DoesNotThrow()
    {
        var act = () => _sut.HandleWebMessage("");
        act.Should().NotThrow();
    }

    [Fact]
    public void HandleWebMessage_ContentChanged_RaisesContentChangedEvent()
    {
        string? receivedContent = null;
        _editor.ContentChanged += (_, content) => receivedContent = content;

        var message = JsonSerializer.Serialize(new
        {
            type = "contentChanged",
            content = "test content",
            cursor = 5
        });

        _sut.HandleWebMessage(message);

        receivedContent.Should().Be("test content");
    }

    // --- C# → JS commands (with null script delegate) ---

    [Fact]
    public async Task GetContentAsync_ReturnsEmpty_WhenNoScriptDelegate()
    {
        // No SetInvokeScript called — should return empty gracefully
        var result = await _sut.GetContentAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task InsertAtCursorAsync_NoOp_WhenNoScriptDelegate()
    {
        // Should not throw
        await _sut.InsertAtCursorAsync("some text");
    }

    [Fact]
    public async Task GetCursorPositionAsync_ReturnsZero_WhenNoScriptDelegate()
    {
        var pos = await _sut.GetCursorPositionAsync();
        pos.Should().Be(0);
    }

    [Fact]
    public async Task SetContentAsync_CallsScript_WhenDelegateSet()
    {
        string? capturedScript = null;
        _sut.SetInvokeScript(script =>
        {
            capturedScript = script;
            return Task.FromResult<string?>(null);
        });

        await _sut.SetContentAsync("# Hello");

        capturedScript.Should().NotBeNull();
        capturedScript.Should().Contain("vditor.setValue");
        capturedScript.Should().Contain("Hello");
    }

    [Fact]
    public async Task GetContentAsync_ReturnsValue_FromScriptDelegate()
    {
        _sut.SetInvokeScript(_ => Task.FromResult<string?>("returned content"));

        var result = await _sut.GetContentAsync();
        result.Should().Be("returned content");
    }
}
