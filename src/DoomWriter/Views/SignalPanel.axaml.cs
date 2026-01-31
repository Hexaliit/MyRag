using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using DoomWriter.ViewModels;

namespace DoomWriter.Views;

public partial class SignalPanel : UserControl
{
    /// <summary>
    /// Converter: heading level (1-6) → left padding for indent.
    /// </summary>
    public static readonly FuncValueConverter<int, Thickness> LevelToPadding = new(level =>
        new Thickness((level - 1) * 12, 2, 4, 2));

    /// <summary>
    /// Converter: heading level → font weight (H1-H2 bold, rest normal).
    /// </summary>
    public static readonly FuncValueConverter<int, FontWeight> LevelToWeight = new(level =>
        level <= 2 ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>
    /// Converter: SearchMode enum ↔ string for ComboBox binding.
    /// </summary>
    public static readonly SearchModeStringConverter SearchModeConverter = new();

    public SignalPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
        {
            searchBox.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter && DataContext is SignalPanelViewModel vm)
                {
                    vm.SubmitSearchCommand.Execute(null);
                    args.Handled = true;
                }
            };
        }
    }
}

/// <summary>
/// Two-way converter between SearchMode enum and display string.
/// </summary>
public class SearchModeStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SearchMode mode ? mode.ToString() : "Corpus";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Enum.TryParse<SearchMode>(s, out var mode) ? mode : SearchMode.Corpus;
}
