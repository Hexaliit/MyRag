using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.LLamaSharp.Services;
using Mostlylucid.DocSummarizer.Resilience;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;

namespace DoomSummarizer.Commands;

/// <summary>
/// Shared bootstrap for CLI commands. Creates the common service stack
/// (config, storage, embedding) and provides opt-in methods for LLM,
/// entity stores, and circuit breaker initialization.
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
    public DoomSummarizer.Services.OllamaService? Ollama { get; private set; }
    public ApiKeyService? ApiKeys { get; private set; }
    public ApiBudgetService? ApiBudget { get; private set; }
    public LlmRouter? LlmRouter { get; private set; }
    public CircuitBreakerService? CircuitBreaker { get; private set; }
    public DuckDbVectorStore? VectorStore { get; private set; }
    public IEntityGraphStore? EntityStore { get; private set; }
    public LLamaSharpLlmService? LLamaSharp { get; private set; }

    private CommandBootstrap(DoomConfig config, string dbPath, StorageService storage, IEmbeddingService embedding, VibeResolver vibeResolver)
    {
        Config = config;
        DbPath = dbPath;
        Storage = storage;
        Embedding = embedding;
        VibeResolver = vibeResolver;
    }

    /// <summary>
    /// Create the core service stack: config → storage → embedding.
    /// </summary>
    public static async Task<CommandBootstrap> CreateAsync(CancellationToken ct = default)
    {
        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        var embedding = await EmbeddingFactory.CreateAsync(ct: ct);

        var vibeResolver = new VibeResolver(config);
        vibeResolver.LoadLenses(typeof(CommandBootstrap).Assembly);

        return new CommandBootstrap(config, dbPath, storage, embedding, vibeResolver);
    }

    /// <summary>
    /// Create an OllamaService wired to config.
    /// </summary>
    public DoomSummarizer.Services.OllamaService CreateOllama()
    {
        Ollama = new DoomSummarizer.Services.OllamaService(Config.Ollama);
        return Ollama;
    }

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

    /// <summary>
    /// Initialize the full LLM stack: API keys → rate limiter → budget → router.
    /// Optionally wires the router into an OllamaService for fallback.
    /// LLamaSharp is added as the highest-priority local provider when available.
    /// </summary>
    public async Task<LlmRouter> InitializeLlmStackAsync(
        CircuitBreakerService? circuitBreaker = null,
        CancellationToken ct = default)
    {
        ApiKeys = ApiKeyService.Load(Config.Keys);
        ApiRateLimiter.Configure(ApiKeys);

        ApiBudget = new ApiBudgetService(Config.ApiBudget, ApiKeys, DbPath);
        await ApiBudget.InitializeAsync();

        // Create LLamaSharp as highest-priority local provider (zero-config GGUF inference)
        var llamaSharp = LLamaSharp ?? CreateLLamaSharp();

        LlmRouter = await Services.LlmRouter.BuildAsync(
            Config.Ollama, ApiKeys, ApiBudget, circuitBreaker,
            localLlmService: llamaSharp, ct: ct);

        if (Ollama != null)
            Ollama.Router = LlmRouter;

        return LlmRouter;
    }

    /// <summary>
    /// Initialize persistent circuit breaker and wire into rate limiter.
    /// </summary>
    public async Task<CircuitBreakerService> InitializeCircuitBreakerAsync()
    {
        CircuitBreaker = new CircuitBreakerService(DbPath);
        await CircuitBreaker.InitializeAsync();
        ApiRateLimiter.SetCircuitBreaker(CircuitBreaker);
        return CircuitBreaker;
    }

    /// <summary>
    /// Initialize DuckDB vector store and entity graph store from the shared vector DB path.
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
    /// Initialize only the entity graph store (no vector store needed).
    /// </summary>
    public async Task<IEntityGraphStore> InitializeEntityGraphStoreAsync()
    {
        var vectorDbPath = ConfigService.GetVectorDbPath();
        EntityStore = new DuckDbEntityGraphStore(vectorDbPath);
        await EntityStore.InitializeAsync();
        return EntityStore;
    }

    /// <summary>
    /// Safely initialize entity graph store. Returns null if unavailable (non-fatal).
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
    /// Initialize LLM stack, entity store, print availability warnings, and run the ask loop.
    /// Consolidates the repeated pattern across AskCommand, ManCommand, and CrawlCommand.
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
        var hasLlamaSharp = LLamaSharp != null && await LLamaSharp.IsAvailableAsync();
        if (hasLlamaSharp)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[green]Local LLM ready (LLamaSharp GGUF)[/]");
        }
        else if (!ollamaAvailable && !hasCloudLlm)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[yellow]No LLM available (Ollama down, no cloud keys, LLamaSharp disabled).[/] Answers will be limited to evidence listing.");
            Spectre.Console.AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }
        else if (!ollamaAvailable && hasCloudLlm)
        {
            Spectre.Console.AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
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
        LLamaSharp?.Dispose();
        (Embedding as IDisposable)?.Dispose();
        await Storage.DisposeAsync();
    }
}
