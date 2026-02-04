using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;
using OllamaService = DoomSummarizer.Services.OllamaService;
#if FEATURE_LLAMASHARP
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.LLamaSharp.Services;
#endif

namespace DoomSummarizer.Commands;

/// <summary>
///     Shared bootstrap for CLI commands. Creates the common service stack
///     (config, storage, embedding) and provides opt-in methods for LLM,
///     entity stores, and circuit breaker initialization.
/// </summary>
public sealed class CommandBootstrap : IAsyncDisposable
{
    public DoomConfig Config { get; }
    public string DbPath { get; }
    public StorageService Storage { get; }
    public IEmbeddingService Embedding { get; }

    // Vibe resolver (lens-aware, loaded eagerly)
    public VibeResolver VibeResolver { get; }

    // Opt-in services (initialized via methods below)
    public OllamaService? Ollama { get; private set; }
    public ApiKeyService? ApiKeys { get; private set; }
    public ApiBudgetService? ApiBudget { get; private set; }
    public LlmRouter? LlmRouter { get; private set; }
    public CircuitBreakerService? CircuitBreaker { get; private set; }
    public DuckDbVectorStore? VectorStore { get; private set; }
    public IEntityGraphStore? EntityStore { get; private set; }
#if FEATURE_LLAMASHARP
    public LLamaSharpLlmService? LLamaSharp { get; private set; }
#endif

    private CommandBootstrap(DoomConfig config, string dbPath, StorageService storage, IEmbeddingService embedding,
        VibeResolver vibeResolver)
    {
        Config = config;
        DbPath = dbPath;
        Storage = storage;
        Embedding = embedding;
        VibeResolver = vibeResolver;
    }

    /// <summary>
    ///     Create the core service stack: config → storage → embedding.
    /// </summary>
    public static async Task<CommandBootstrap> CreateAsync(CancellationToken ct = default)
    {
        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        var storage = new StorageService(dbPath);
        try
        {
            await storage.InitializeAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */
                                         || ex.Message.Contains("database is locked",
                                             StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]Error: Database is locked by another instance.[/]");
            AnsiConsole.MarkupLine("[yellow]DoomSummarizer uses SQLite which supports single-writer access.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]Please close other running instances, or use LucidRAG (PostgreSQL) for multi-user access.[/]");
            throw;
        }

        var embedding = await EmbeddingFactory.CreateAsync(ct: ct);

        var vibeResolver = new VibeResolver(config);
        vibeResolver.LoadLenses(typeof(CommandBootstrap).Assembly);

        return new CommandBootstrap(config, dbPath, storage, embedding, vibeResolver);
    }

    /// <summary>
    ///     Create an OllamaService wired to config.
    /// </summary>
    public OllamaService CreateOllama()
    {
        Ollama = new OllamaService(Config.Ollama);
        return Ollama;
    }

#if FEATURE_LLAMASHARP
    /// <summary>
    /// Create a LLamaSharp local LLM service for zero-config GGUF inference.
    /// Applies DoomConfig.LlamaSharp overrides from config profiles.
    /// Returns null if LLamaSharp is disabled in config or fails to initialize.
    /// </summary>
    public LLamaSharpLlmService? CreateLLamaSharp()
    {
        try
        {
            var llamaConfig = ApplyLlamaSharpOverrides(new LLamaSharpConfig(), Config.LlamaSharp);
            if (!llamaConfig.Enabled) return null;

            var downloader = new LLamaSharpModelDownloader(llamaConfig);
            LLamaSharp = new LLamaSharpLlmService(llamaConfig, downloader);
            return LLamaSharp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply DoomConfig.LlamaSharp profile overrides onto a LLamaSharpConfig.
    /// Only non-null fields in the override section are applied.
    /// </summary>
    private static LLamaSharpConfig ApplyLlamaSharpOverrides(LLamaSharpConfig baseConfig, Models.LlamaSharpConfigSection overrides)
    {
        return baseConfig with
        {
            Enabled = overrides.Enabled ?? baseConfig.Enabled,
            SynthesisModel = overrides.SynthesisModel ?? baseConfig.SynthesisModel,
            SentinelModel = overrides.SentinelModel ?? baseConfig.SentinelModel,
            ContextSize = overrides.ContextSize ?? baseConfig.ContextSize,
            GpuLayerCount = overrides.GpuLayerCount ?? baseConfig.GpuLayerCount,
            BatchSize = overrides.BatchSize ?? baseConfig.BatchSize,
        };
    }
#endif

    /// <summary>
    ///     Initialize the full LLM stack: API keys → rate limiter → budget → router.
    ///     Provider priority: Ollama (if running) → LLamaSharp (GPU fallback, complete builds only) → Cloud.
    /// </summary>
    public async Task<LlmRouter> InitializeLlmStackAsync(
        CircuitBreakerService? circuitBreaker = null,
        CancellationToken ct = default)
    {
        ApiKeys = ApiKeyService.Load(Config.Keys);
        ApiRateLimiter.Configure(ApiKeys);

        ApiBudget = new ApiBudgetService(Config.ApiBudget, ApiKeys, DbPath);
        await ApiBudget.InitializeAsync();

#if FEATURE_LLAMASHARP
        // Create LLamaSharp as fallback provider (used when Ollama isn't running)
        var llamaSharp = LLamaSharp ?? CreateLLamaSharp();
#else
        ILlmService? llamaSharp = null;

        // Only mention LLamaSharp when no Ollama model is configured (user might need it)
        if (string.IsNullOrEmpty(Config.Ollama.Model))
        {
            var ls = Config.LlamaSharp;
            if (ls.Enabled != false &&
                (ls.ContextSize != null || ls.GpuLayerCount != null || ls.SynthesisModel != null))
                AnsiConsole.MarkupLine(
                    "[yellow]Note:[/] Config has LLamaSharp settings but this build doesn't include it. Use [bold cyan]lucidrag[/] for local GGUF support.");
        }
#endif

        LlmRouter = await LlmRouter.BuildAsync(
            Config.Ollama, ApiKeys, ApiBudget, circuitBreaker,
            llamaSharp, ct);

        if (Ollama != null)
            Ollama.Router = LlmRouter;

        return LlmRouter;
    }

    /// <summary>
    ///     Initialize persistent circuit breaker and wire into rate limiter.
    /// </summary>
    public async Task<CircuitBreakerService> InitializeCircuitBreakerAsync()
    {
        CircuitBreaker = new CircuitBreakerService(DbPath);
        await CircuitBreaker.InitializeAsync();
        ApiRateLimiter.SetCircuitBreaker(CircuitBreaker);
        return CircuitBreaker;
    }

    /// <summary>
    ///     Initialize DuckDB vector store and entity graph store from the shared vector DB path.
    /// </summary>
    public async Task InitializeEntityStoresAsync()
    {
        var vectorDbPath = ConfigService.GetVectorDbPath();
        VectorStore = new DuckDbVectorStore(vectorDbPath);
        await VectorStore.InitializeAsync();
        EntityStore = new DuckDbEntityGraphStore(vectorDbPath);
        await EntityStore.InitializeAsync();
    }

    /// <summary>
    ///     Initialize only the entity graph store (no vector store needed).
    /// </summary>
    public async Task<IEntityGraphStore> InitializeEntityGraphStoreAsync()
    {
        var vectorDbPath = ConfigService.GetVectorDbPath();
        EntityStore = new DuckDbEntityGraphStore(vectorDbPath);
        await EntityStore.InitializeAsync();
        return EntityStore;
    }

    /// <summary>
    ///     Safely initialize entity graph store. Returns null if unavailable (non-fatal).
    /// </summary>
    public async Task<IEntityGraphStore?> TryInitializeEntityGraphStoreAsync()
    {
        try
        {
            return await InitializeEntityGraphStoreAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Initialize LLM stack, entity store, print availability warnings, and run the ask loop.
    ///     Consolidates the repeated pattern across AskCommand, ManCommand, and CrawlCommand.
    /// </summary>
    public async Task<int> StartAskLoopAsync(
        InteractiveAskOptions options,
        CancellationToken ct = default)
    {
        var ollama = CreateOllama();
        var llmRouter = await InitializeLlmStackAsync(ct: ct);

        await TryInitializeEntityGraphStoreAsync();

        var ollamaAvailable = await ollama.IsAvailableAsync();
        var hasCloudLlm = llmRouter.HasCloudProvider;
#if FEATURE_LLAMASHARP
        var hasLlamaSharp = LLamaSharp != null && await LLamaSharp.IsAvailableAsync();
#else
        var hasLlamaSharp = false;
#endif
        if (ollamaAvailable)
        {
            AnsiConsole.MarkupLine($"[green]LLM:[/] {FormattingHelpers.Esc(llmRouter.StatusDescription)}");
        }
        else if (hasLlamaSharp)
        {
            AnsiConsole.MarkupLine(
                "[cyan]Ollama not running — using local GGUF (LLamaSharp, first call loads model)[/]");
        }
        else if (hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                "[yellow]No LLM available (Ollama down, no cloud keys).[/] Answers will be limited to evidence listing.");
            AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }

        var loop = new InteractiveAskLoop(this, ollama, llmRouter, ollamaAvailable, options);
        return await loop.RunAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (EntityStore != null) await EntityStore.DisposeAsync();
        if (VectorStore != null) await VectorStore.DisposeAsync();
        if (CircuitBreaker != null) await CircuitBreaker.DisposeAsync();
        if (ApiBudget != null) await ApiBudget.DisposeAsync();
#if FEATURE_LLAMASHARP
        LLamaSharp?.Dispose();
#endif
        (Embedding as IDisposable)?.Dispose();
        await Storage.DisposeAsync();
    }
}