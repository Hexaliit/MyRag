using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoomSummarizer.Models;
using DoomWriter.Models;
using DoomWriter.Services;

namespace DoomWriter.ViewModels;

public partial class CorpusViewModel : ObservableObject
{
    private readonly CorpusService _corpus;
    private readonly CrawlService _crawlService;
    private readonly WriterSettingsService _settings;
    private Window? _window;
    private CancellationTokenSource? _indexCts;

    public ObservableCollection<string> Directories { get; } = [];
    public ObservableCollection<CrawlSourceItem> CrawlSources { get; } = [];
    public ObservableCollection<CorpusCheckItem> CorpusCheckItems { get; } = [];

    [ObservableProperty] private string? _selectedDirectory;
    [ObservableProperty] private CrawlSourceItem? _selectedCrawlSource;
    [ObservableProperty] private int _totalDocuments;
    [ObservableProperty] private int _totalSegments;
    [ObservableProperty] private string _indexingStatus = "";
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private float _indexProgress;
    [ObservableProperty] private bool _autoIndexOnChange;

    // Crawl state
    [ObservableProperty] private bool _isCrawling;
    [ObservableProperty] private string _crawlStatus = "";
    [ObservableProperty] private float _crawlProgress;
    [ObservableProperty] private bool _hasCrawlSourceSelected;

    public CorpusViewModel(CorpusService corpus, CrawlService crawlService, WriterSettingsService settings)
    {
        _corpus = corpus;
        _crawlService = crawlService;
        _settings = settings;

        foreach (var dir in settings.Config.CorpusDirectories)
            Directories.Add(dir);

        foreach (var src in settings.Config.CrawlSources)
        {
            CrawlSources.Add(new CrawlSourceItem(src));
        }

        AutoIndexOnChange = settings.Config.AutoIndexOnChange;
        TotalDocuments = corpus.TotalDocuments;
        TotalSegments = corpus.TotalSegments;

        _corpus.IndexProgress += OnIndexProgress;
        _corpus.IndexCompleted += OnIndexCompleted;
        _crawlService.CrawlProgress += OnCrawlProgress;
        _crawlService.CrawlCompleted += OnCrawlCompleted;

        RebuildCorpusCheckItems();
    }

    partial void OnSelectedCrawlSourceChanged(CrawlSourceItem? value)
    {
        HasCrawlSourceSelected = value != null;
        if (value != null)
            RebuildCorpusCheckItems();
    }

    private void RebuildCorpusCheckItems()
    {
        CorpusCheckItems.Clear();
        var selectedCorpora = SelectedCrawlSource?.Source.TargetCorpora ?? [];
        foreach (var dir in Directories)
        {
            var name = Path.GetFileName(dir) ?? dir;
            CorpusCheckItems.Add(new CorpusCheckItem
            {
                Name = name,
                Path = dir,
                IsSelected = selectedCorpora.Contains(name)
            });
        }
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
                RebuildCorpusCheckItems();

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
        RebuildCorpusCheckItems();
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

    [RelayCommand]
    private void AddCrawlSource()
    {
        var source = new CrawlSource
        {
            Url = "https://",
            Name = "",
            MaxDepth = 2,
            MaxPages = 50,
            Recurse = true
        };
        var item = new CrawlSourceItem(source);
        CrawlSources.Add(item);
        SelectedCrawlSource = item;
        _settings.Config.CrawlSources.Add(source);
        _settings.Save();
    }

    [RelayCommand]
    private void RemoveCrawlSource(CrawlSourceItem? item)
    {
        if (item == null) return;
        CrawlSources.Remove(item);
        _settings.Config.CrawlSources.Remove(item.Source);
        _settings.Save();
        if (SelectedCrawlSource == item)
            SelectedCrawlSource = null;
    }

    [RelayCommand]
    private async Task CrawlSource(CrawlSourceItem? item)
    {
        if (item == null || _crawlService.IsCrawling) return;

        // Sync selected corpora back to the source model
        item.Source.TargetCorpora = CorpusCheckItems
            .Where(c => c.IsSelected)
            .Select(c => c.Name)
            .ToList();
        _settings.Save();

        IsCrawling = true;
        CrawlStatus = $"Starting crawl: {item.Url}";
        CrawlProgress = 0;

        await _crawlService.StartCrawlAsync(item.Source);
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

    private void OnCrawlProgress(object? sender, CrawlProgressUpdate progress)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CrawlStatus = progress.Message;
            if (progress.Total > 0)
                CrawlProgress = (float)progress.Current / progress.Total * 100;
            if (progress.IsComplete)
                IsCrawling = false;
        });
    }

    private void OnCrawlCompleted(object? sender, CrawlSessionResult result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsCrawling = false;
            CrawlStatus = $"Crawl complete: {result.NewChanged} pages indexed from {result.KbName}";
            if (SelectedCrawlSource != null)
                SelectedCrawlSource.StatusText = $"Last crawled: {DateTimeOffset.Now:g}";
        });
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

public partial class CrawlSourceItem : ObservableObject
{
    public CrawlSource Source { get; }

    public string Url => Source.Url;

    [ObservableProperty] private string? _pathFilter;
    [ObservableProperty] private int _maxDepth;
    [ObservableProperty] private int _maxPages;
    [ObservableProperty] private bool _recurse;
    [ObservableProperty] private string _statusText = "";

    public CrawlSourceItem(CrawlSource source)
    {
        Source = source;
        _pathFilter = source.PathFilter;
        _maxDepth = source.MaxDepth;
        _maxPages = source.MaxPages;
        _recurse = source.Recurse;
        _statusText = source.LastCrawled.HasValue
            ? $"Last crawled: {source.LastCrawled.Value:g}"
            : "Not yet crawled";
    }

    partial void OnPathFilterChanged(string? value) => Source.PathFilter = value;
    partial void OnMaxDepthChanged(int value) => Source.MaxDepth = value;
    partial void OnMaxPagesChanged(int value) => Source.MaxPages = value;
    partial void OnRecurseChanged(bool value) => Source.Recurse = value;
}

public partial class CorpusCheckItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    [ObservableProperty] private bool _isSelected;
}
