using DoomSummarizer.Models;
using DoomSummarizer.Services;
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
    /// Initialize the full LLM stack: API keys → rate limiter → budget → router.
    /// Optionally wires the router into an OllamaService for fallback.
    /// </summary>
    public async Task<LlmRouter> InitializeLlmStackAsync(
        CircuitBreakerService? circuitBreaker = null,
        CancellationToken ct = default)
    {
        ApiKeys = ApiKeyService.Load(Config.Keys);
        ApiRateLimiter.Configure(ApiKeys);

        ApiBudget = new ApiBudgetService(Config.ApiBudget, ApiKeys, DbPath);
        await ApiBudget.InitializeAsync();

        LlmRouter = await Services.LlmRouter.BuildAsync(
            Config.Ollama, ApiKeys, ApiBudget, circuitBreaker, ct);

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

    public async ValueTask DisposeAsync()
    {
        if (EntityStore != null) await EntityStore.DisposeAsync();
        if (VectorStore != null) await VectorStore.DisposeAsync();
        if (CircuitBreaker != null) await CircuitBreaker.DisposeAsync();
        if (ApiBudget != null) await ApiBudget.DisposeAsync();
        (Embedding as IDisposable)?.Dispose();
        await Storage.DisposeAsync();
    }
}
