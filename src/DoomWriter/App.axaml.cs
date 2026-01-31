using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using DoomWriter.Services;
using Mostlylucid.DocSummarizer.Services;
using DoomWriter.ViewModels;
using DoomWriter.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DoomWriter;

public class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Open file from command-line if provided
            if (Program.Args.Length > 0)
                _ = vm.OpenFileAsync(Program.Args[0]);

            // Initialize corpus in background, then wire Lucene to CrawlService
            _ = Task.Run(async () =>
            {
                var corpus = Services.GetRequiredService<CorpusService>();
                await corpus.InitializeAsync();
                corpus.StartWatchingAll();

                // Share Lucene FTS index with CrawlService so crawled content is searchable
                var crawl = Services.GetRequiredService<CrawlService>();
                if (corpus.Lucene != null)
                    crawl.SetLucene(corpus.Lucene);
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Settings (must be first — others depend on config)
        services.AddSingleton<WriterSettingsService>();

        // DoomSummarizer.Core services
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            return EmbeddingFactory.CreateAsync().GetAwaiter().GetResult();
        });
        services.AddSingleton<NerService>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<WriterSettingsService>();
            var dbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DoomWriter", "data");
            Directory.CreateDirectory(dbDir);
            return new StorageService(Path.Combine(dbDir, "corpus.db"));
        });
        services.AddSingleton(sp =>
        {
            var dbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DoomWriter", "data");
            Directory.CreateDirectory(dbDir);
            return new DuckDbVectorStore(Path.Combine(dbDir, "vectors.duckdb"));
        });
        services.AddSingleton<IEntityGraphStore>(sp =>
        {
            var dbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DoomWriter", "data");
            Directory.CreateDirectory(dbDir);
            return new DuckDbEntityGraphStore(Path.Combine(dbDir, "vectors.duckdb"));
        });
        services.AddSingleton(sp =>
        {
            var embedding = sp.GetRequiredService<IEmbeddingService>();
            var entityStore = sp.GetRequiredService<IEntityGraphStore>();
            return new EntityProfileService(embedding, entityStore);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<WriterSettingsService>();
            var config = new OllamaConfig
            {
                BaseUrl = settings.Config.OllamaBaseUrl,
                Model = settings.Config.MainModel,
                SentinelModel = settings.Config.SentinelModel,
                ContextSize = settings.Config.ContextWindow
            };
            return new DoomSummarizer.Services.OllamaService(config);
        });

        // DoomWriter services
        services.AddSingleton<SpellCheckService>();
        services.AddSingleton<CorpusService>();
        services.AddSingleton<SuggestionService>();
        services.AddSingleton<WritingAssistantService>();
        services.AddSingleton<AutocompleteService>();
        services.AddSingleton<GraphBridge>();
        services.AddSingleton<EntityGraphService>();
        services.AddSingleton<CrawlService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton<SignalPanelViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<CorpusViewModel>();

        // Editor bridge
        services.AddSingleton<EditorBridge>();
        services.AddSingleton<DocumentAnalysisService>();
    }
}
