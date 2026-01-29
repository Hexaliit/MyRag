using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DoomWriter.ViewModels;

namespace DoomWriter.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
