using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

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

    public SignalPanel()
    {
        InitializeComponent();
    }
}
