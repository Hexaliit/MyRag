using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomWriter.Services;

namespace DoomWriter.ViewModels;

/// <summary>
/// ViewModel for the Corpus management dialog.
/// Allows adding/removing directories and re-indexing.
/// </summary>
public partial class CorpusViewModel : ObservableObject
{
    private readonly CorpusService _corpus;
    private readonly WriterSettingsService _settings;
    private Window? _window;
    private CancellationTokenSource? _indexCts;

    public ObservableCollection<string> Directories { get; } = [];

    [ObservableProperty] private string? _selectedDirectory;
    [ObservableProperty] private int _totalDocuments;
    [ObservableProperty] private int _totalSegments;
    [ObservableProperty] private string _indexingStatus = "";
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private float _indexProgress;
    [ObservableProperty] private bool _autoIndexOnChange;

    public CorpusViewModel(CorpusService corpus, WriterSettingsService settings)
    {
        _corpus = corpus;
        _settings = settings;

        foreach (var dir in settings.Config.CorpusDirectories)
            Directories.Add(dir);

        AutoIndexOnChange = settings.Config.AutoIndexOnChange;
        TotalDocuments = corpus.TotalDocuments;
        TotalSegments = corpus.TotalSegments;

        _corpus.IndexProgress += OnIndexProgress;
        _corpus.IndexCompleted += OnIndexCompleted;
    }

    public void SetWindow(Window window) => _window = window;

    [RelayCommand]
    private async Task AddDirectory()
    {
        if (_window == null) return;

        var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Corpus Directory",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (path != null && !Directories.Contains(path))
            {
                Directories.Add(path);
                SaveDirectories();

                // Start indexing the new directory
                await IndexDirectoryAsync(path);
            }
        }
    }

    [RelayCommand]
    private void RemoveDirectory()
    {
        if (SelectedDirectory == null) return;

        var dir = SelectedDirectory;
        Directories.Remove(dir);
        _corpus.StopWatching(dir);
        SaveDirectories();
    }

    [RelayCommand]
    private async Task Reindex()
    {
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();

        IsIndexing = true;
        IndexProgress = 0;
        IndexingStatus = "Starting full re-index...";

        foreach (var dir in Directories)
        {
            if (_indexCts.Token.IsCancellationRequested) break;
            await IndexDirectoryAsync(dir);
        }

        IsIndexing = false;
    }

    private async Task IndexDirectoryAsync(string path)
    {
        IsIndexing = true;
        IndexingStatus = $"Indexing {Path.GetFileName(path)}...";

        try
        {
            await _corpus.IngestDirectoryAsync(path, _indexCts?.Token ?? CancellationToken.None);
            _corpus.StartWatching(path);
        }
        catch (OperationCanceledException)
        {
            IndexingStatus = "Indexing cancelled.";
        }
        catch (Exception ex)
        {
            IndexingStatus = $"Error: {ex.Message}";
        }
    }

    private void OnIndexProgress(object? sender, CorpusIndexProgress progress)
    {
        IndexProgress = progress.ProgressPercent;
        IndexingStatus = $"Indexing: {progress.CurrentFile} ({progress.ProcessedFiles}/{progress.TotalFiles})";
    }

    private void OnIndexCompleted(object? sender, EventArgs e)
    {
        IsIndexing = false;
        TotalDocuments = _corpus.TotalDocuments;
        TotalSegments = _corpus.TotalSegments;
        IndexingStatus = $"Complete. {TotalDocuments} documents, {TotalSegments} segments.";
    }

    private void SaveDirectories()
    {
        _settings.Config.CorpusDirectories = [.. Directories];
        _settings.Config.AutoIndexOnChange = AutoIndexOnChange;
        _settings.Save();
    }

    [RelayCommand]
    private void Close()
    {
        SaveDirectories();
        _window?.Close();
    }
}
