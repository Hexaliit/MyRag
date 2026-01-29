using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DoomWriter.ViewModels;

/// <summary>
/// ViewModel for the Vditor markdown editor panel.
/// Manages content state, cursor position, and editor mode.
/// </summary>
public partial class EditorViewModel : ObservableObject
{
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileName = "Untitled";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isEditorReady;
    [ObservableProperty] private int _cursorPosition;
    [ObservableProperty] private string _editorMode = "ir";
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty] private int _selectionStart;
    [ObservableProperty] private int _selectionEnd;

    /// <summary>
    /// Raised when content changes (debounced from Vditor).
    /// </summary>
    public event EventHandler<string>? ContentChanged;

    /// <summary>
    /// Raised when a toolbar action is triggered from the editor.
    /// </summary>
    public event EventHandler<string>? ToolbarAction;

    /// <summary>
    /// Raised when text is selected in the editor.
    /// </summary>
    public event EventHandler<(string Text, int From, int To)>? TextSelected;

    public void OnContentChanged(string newContent, int cursor)
    {
        Content = newContent;
        CursorPosition = cursor;
        IsDirty = true;
        ContentChanged?.Invoke(this, newContent);
    }

    public void OnToolbarAction(string action)
    {
        ToolbarAction?.Invoke(this, action);
    }

    public void OnTextSelected(string text, int from, int to)
    {
        SelectedText = text;
        SelectionStart = from;
        SelectionEnd = to;
        TextSelected?.Invoke(this, (text, from, to));
    }

    public void OnEditorReady()
    {
        IsEditorReady = true;
    }

    public void OnModeChanged(string mode)
    {
        EditorMode = mode;
    }

    public void MarkClean()
    {
        IsDirty = false;
    }

    public void SetFile(string path, string content)
    {
        FilePath = path;
        FileName = Path.GetFileName(path);
        Content = content;
        IsDirty = false;
    }

    public void NewDocument()
    {
        FilePath = "";
        FileName = "Untitled";
        Content = "";
        IsDirty = false;
    }
}
