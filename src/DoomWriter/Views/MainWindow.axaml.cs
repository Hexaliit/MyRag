using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DoomWriter.Controls;
using DoomWriter.Services;
using DoomWriter.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DoomWriter.Views;

public partial class MainWindow : Window
{
    private WebView2Host? _webViewHost;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Loaded += OnLoaded;

        // Wire keyboard shortcuts that need StorageProvider (can't use compiled bindings for this)
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Wire toolbar buttons directly (avoids broken compiled binding for StorageProvider)
        WireToolbarButtons();

        // Restore signal panel width from config
        RestoreSignalPanelWidth();

        // Initialize the WebView2 editor
        InitializeEditor();
    }

    private void WireToolbarButtons()
    {
        var btnNew = this.FindControl<Button>("BtnNew");
        var btnOpen = this.FindControl<Button>("BtnOpen");
        var btnSave = this.FindControl<Button>("BtnSave");
        var btnSaveAs = this.FindControl<Button>("BtnSaveAs");
        var btnCorpus = this.FindControl<Button>("BtnCorpus");
        var btnSettings = this.FindControl<Button>("BtnSettings");

        if (btnNew != null) btnNew.Click += (_, _) => Vm?.NewDocumentCommand.Execute(null);
        if (btnOpen != null) btnOpen.Click += async (_, _) => await OnOpenFileAsync();
        if (btnSave != null) btnSave.Click += (_, _) => Vm?.SaveFileCommand.Execute(null);
        if (btnSaveAs != null) btnSaveAs.Click += async (_, _) => await OnSaveFileAsAsync();
        if (btnCorpus != null) btnCorpus.Click += (_, _) => Vm?.OpenCorpusDialogCommand.Execute(null);
        if (btnSettings != null) btnSettings.Click += (_, _) => Vm?.OpenSettingsDialogCommand.Execute(null);
    }

    private async Task OnOpenFileAsync()
    {
        if (Vm == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.mdx"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null)
                await Vm.OpenFileAsync(path);
        }
    }

    private async Task OnSaveFileAsAsync()
    {
        if (Vm == null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Markdown File",
            DefaultExtension = "md",
            SuggestedFileName = Vm.Editor.FileName == "Untitled" ? "document.md" : Vm.Editor.FileName,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] }
            ]
        });

        if (file != null)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
            {
                await File.WriteAllTextAsync(path, Vm.Editor.Content);
                Vm.Editor.SetFile(path, Vm.Editor.Content);
                Vm.Config.AddRecentFile(path);
                Vm.UpdateTitlePublic();
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    _ = OnOpenFileAsync();
                    e.Handled = true;
                    break;
            }
        }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            switch (e.Key)
            {
                case Key.S:
                    _ = OnSaveFileAsAsync();
                    e.Handled = true;
                    break;
            }
        }
    }

    private void InitializeEditor()
    {
        var container = this.FindControl<Border>("EditorContainer");
        var placeholder = this.FindControl<TextBlock>("EditorPlaceholder");
        if (container == null) return;

        var bridge = App.Services.GetRequiredService<EditorBridge>();

        _webViewHost = new WebView2Host();

        // Wire JS → C# messages
        _webViewHost.WebMessageReceived += (s, json) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => bridge.HandleWebMessage(json));
        };

        // When WebView2 core is ready, wire C# → JS and navigate to editor
        _webViewHost.WebViewReady += async (s, _) =>
        {
            bridge.SetInvokeScript(async script => await _webViewHost.ExecuteScriptAsync(script));
            var editorPath = await ExtractEditorHtmlAsync();
            _webViewHost.Navigate($"file:///{editorPath.Replace('\\', '/')}");
        };

        // When navigation completes, push any pending content
        _webViewHost.NavigationCompleted += (s, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (placeholder != null)
                    placeholder.IsVisible = false;
                await PushPendingContentAsync(bridge);
            });
        };

        // Handle WebView2 init failure
        _webViewHost.InitializationFailed += (s, msg) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (placeholder != null)
                {
                    placeholder.Text = msg;
                    placeholder.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                    placeholder.Foreground = Avalonia.Media.Brushes.IndianRed;
                }
            });
        };

        container.Child = _webViewHost;
    }

    private static async Task PushPendingContentAsync(EditorBridge bridge)
    {
        for (var i = 0; i < 30; i++) // Max 6 seconds
        {
            await Task.Delay(200);
            try
            {
                var val = await bridge.GetContentAsync();
                if (val != null)
                {
                    var vm = App.Services.GetRequiredService<MainWindowViewModel>();
                    if (!string.IsNullOrEmpty(vm.Editor.Content))
                    {
                        await bridge.SetContentAsync(vm.Editor.Content);
                        vm.Editor.MarkClean();
                    }
                    return;
                }
            }
            catch
            {
                // Vditor not ready yet
            }
        }
    }

    private static async Task<string> ExtractEditorHtmlAsync()
    {
        var editorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoomWriter", "editor");
        Directory.CreateDirectory(editorDir);
        var editorPath = Path.Combine(editorDir, "editor.html");

        await using var stream = Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://DoomWriter/Resources/editor.html"));
        await using var fileStream = File.Create(editorPath);
        await stream.CopyToAsync(fileStream);

        return editorPath;
    }

#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null && IsMarkdownFile(path))
            {
                await Vm.OpenFileAsync(path);
                break;
            }
        }
    }
#pragma warning restore CS0618

    private void RestoreSignalPanelWidth()
    {
        var grid = this.FindControl<Grid>("ContentGrid");
        if (grid == null || Vm == null || grid.ColumnDefinitions.Count == 0) return;

        var col = grid.ColumnDefinitions[0];
        var saved = Vm.Config.SignalPanelWidth;
        if (saved is > 200 and < 500)
            col.Width = new GridLength(saved);

        // Save width back when the splitter is dragged
        col.PropertyChanged += (_, args) =>
        {
            if (args.Property == ColumnDefinition.WidthProperty && Vm != null)
            {
                var w = col.Width.Value;
                if (col.Width.GridUnitType == GridUnitType.Pixel && w is > 200 and < 500)
                {
                    Vm.Config.SignalPanelWidth = w;
                    Vm.SignalPanelWidth = w;
                }
            }
        };
    }

    private static bool IsMarkdownFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" or ".mdx" or ".txt";
    }
}
