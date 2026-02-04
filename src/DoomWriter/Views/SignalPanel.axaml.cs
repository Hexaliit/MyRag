using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DoomWriter.Controls;
using DoomWriter.Services;
using DoomWriter.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DoomWriter.Views;

public partial class SignalPanel : UserControl
{
    /// <summary>
    ///     Converter: heading level (1-6) → left padding for indent.
    /// </summary>
    public static readonly FuncValueConverter<int, Thickness> LevelToPadding = new(level =>
        new Thickness((level - 1) * 12, 2, 4, 2));

    /// <summary>
    ///     Converter: heading level → font weight (H1-H2 bold, rest normal).
    /// </summary>
    public static readonly FuncValueConverter<int, FontWeight> LevelToWeight = new(level =>
        level <= 2 ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>
    ///     Converter: SearchMode enum ↔ string for ComboBox binding.
    /// </summary>
    public static readonly SearchModeStringConverter SearchModeConverter = new();

    private bool _graphInitialized;

    private WebView2Host? _graphWebView;

    public SignalPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
            searchBox.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter && DataContext is SignalPanelViewModel vm)
                {
                    vm.SubmitSearchCommand.Execute(null);
                    args.Handled = true;
                }
            };

        InitializeGraphWebView();
    }

    private void InitializeGraphWebView()
    {
        if (_graphInitialized) return;
        _graphInitialized = true;

        var container = this.FindControl<Border>("GraphContainer");
        if (container == null) return;

        var bridge = App.Services.GetRequiredService<GraphBridge>();

        _graphWebView = new WebView2Host();

        _graphWebView.WebMessageReceived += (_, json) =>
        {
            Dispatcher.UIThread.Post(() => bridge.HandleWebMessage(json));
        };

        _graphWebView.WebViewReady += async (_, _) =>
        {
            bridge.SetInvokeScript(async script => await _graphWebView.ExecuteScriptAsync(script));
            var graphPath = await ExtractGraphHtmlAsync();
            _graphWebView.Navigate($"file:///{graphPath.Replace('\\', '/')}");
        };

        _graphWebView.NavigationCompleted += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Set theme to match app
                if (DataContext is SignalPanelViewModel vm)
                    vm.OnGraphWebViewReady();
            });
        };

        container.Child = _graphWebView;
    }

    private static async Task<string> ExtractGraphHtmlAsync()
    {
        var editorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoomWriter", "editor");
        Directory.CreateDirectory(editorDir);
        var graphPath = Path.Combine(editorDir, "graph.html");

        await using var stream = AssetLoader.Open(
            new Uri("avares://DoomWriter/Resources/graph.html"));
        await using var fileStream = File.Create(graphPath);
        await stream.CopyToAsync(fileStream);

        return graphPath;
    }
}

/// <summary>
///     Two-way converter between SearchMode enum and display string.
/// </summary>
public class SearchModeStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is SearchMode mode ? mode.ToString() : "Corpus";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && Enum.TryParse<SearchMode>(s, out var mode) ? mode : SearchMode.Corpus;
    }
}