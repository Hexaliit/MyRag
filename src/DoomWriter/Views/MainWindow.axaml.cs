using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DoomWriter.Controls;
using DoomWriter.Services;
using DoomWriter.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var container = this.FindControl<Border>("EditorContainer");
        var placeholder = this.FindControl<TextBlock>("EditorPlaceholder");
        if (container == null) return;

        var bridge = App.Services.GetRequiredService<EditorBridge>();

        _webViewHost = new WebView2Host();

        // Wire JS → C# messages (connect immediately so no messages are lost)
        _webViewHost.WebMessageReceived += (s, json) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => bridge.HandleWebMessage(json));
        };

        // When WebView2 core is ready, wire C# → JS and navigate to editor
        _webViewHost.WebViewReady += async (s, _) =>
        {
            // Wire C# → JS script invocation
            bridge.SetInvokeScript(async script =>
            {
                return await _webViewHost.ExecuteScriptAsync(script);
            });

            // Extract editor.html to app data and navigate
            var editorPath = await ExtractEditorHtmlAsync();
            _webViewHost.Navigate($"file:///{editorPath.Replace('\\', '/')}");
        };

        // When navigation completes, Vditor starts loading from CDN.
        // After a short delay, push any pending content (handles startup file open).
        _webViewHost.NavigationCompleted += (s, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (placeholder != null)
                    placeholder.IsVisible = false;

                // Wait for Vditor to initialize, then push pending content
                await PushPendingContentAsync(bridge);
            });
        };

        // Handle WebView2 init failure (runtime not installed)
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

        // Replace placeholder with WebView2 host
        container.Child = _webViewHost;
    }

    /// <summary>
    /// Waits for Vditor to initialize then pushes any content that was loaded before the editor was ready.
    /// </summary>
    private static async Task PushPendingContentAsync(EditorBridge bridge)
    {
        // Vditor needs time to load from CDN and initialize.
        // Poll until the editor is ready, then push content.
        for (var i = 0; i < 30; i++) // Max 6 seconds
        {
            await Task.Delay(200);
            try
            {
                // Try calling vditor.getValue() — if it works, Vditor is ready
                var val = await bridge.GetContentAsync();
                if (val != null)
                {
                    // Editor is ready — push any pending content from the ViewModel
                    var vm = App.Services.GetRequiredService<MainWindowViewModel>();
                    if (!string.IsNullOrEmpty(vm.Editor.Content))
                    {
                        await bridge.SetContentAsync(vm.Editor.Content);
                        vm.Editor.MarkClean(); // Content was set from file, not user edit
                    }
                    return;
                }
            }
            catch
            {
                // Vditor not ready yet, keep waiting
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

        // Always extract fresh copy (so updates are picked up)
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
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null && IsMarkdownFile(path))
            {
                await vm.OpenFileAsync(path);
                break; // Only open the first file
            }
        }
    }

#pragma warning restore CS0618

    private static bool IsMarkdownFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" or ".mdx" or ".txt";
    }
}
