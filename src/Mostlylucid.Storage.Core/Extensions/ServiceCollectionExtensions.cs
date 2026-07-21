using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Config;
using Mostlylucid.Storage.Core.Implementations;

namespace Mostlylucid.Storage.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStore(this IServiceCollection services)
    {
        return services.AddVectorStore(_ => { });
    }

    public static IServiceCollection AddVectorStore(
        this IServiceCollection services,
        Action<VectorStoreOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IVectorStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            return CreateVectorStore(options, sp);
        });
        return services;
    }

    public static IServiceCollection AddVectorStoreForToolMode(this IServiceCollection services)
    {
        return services.AddVectorStore(opt =>
        {
            var toolOptions = VectorStoreOptions.ForToolMode();
            opt.Backend = toolOptions.Backend;
            opt.PersistVectors = toolOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = toolOptions.ReuseExistingEmbeddings;
            opt.CollectionName = toolOptions.CollectionName;
        });
    }

    public static IServiceCollection AddVectorStoreForStandaloneMode(
        this IServiceCollection services,
        string dataDirectory = "./data")
    {
        return services.AddVectorStore(opt =>
        {
            var standaloneOptions = VectorStoreOptions.ForStandaloneMode(dataDirectory);
            opt.Backend = standaloneOptions.Backend;
            opt.PersistVectors = standaloneOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = standaloneOptions.ReuseExistingEmbeddings;
            opt.CollectionName = standaloneOptions.CollectionName;
            opt.SqliteVec = standaloneOptions.SqliteVec;
        });
    }

    public static IServiceCollection AddVectorStoreForProductionMode(
        this IServiceCollection services,
        string dataDirectory = "./data")
    {
        return services.AddVectorStore(opt =>
        {
            var prodOptions = VectorStoreOptions.ForProductionMode(dataDirectory);
            opt.Backend = prodOptions.Backend;
            opt.PersistVectors = prodOptions.PersistVectors;
            opt.ReuseExistingEmbeddings = prodOptions.ReuseExistingEmbeddings;
            opt.CollectionName = prodOptions.CollectionName;
            opt.SqliteVec = prodOptions.SqliteVec;
        });
    }

    public static IServiceCollection AddSqliteVec(
        this IServiceCollection services,
        string databasePath = "./data/rag.db")
    {
        return services.AddVectorStore(opt =>
        {
            opt.Backend = VectorStoreBackend.SqliteVec;
            opt.PersistVectors = true;
            opt.ReuseExistingEmbeddings = true;
            opt.ReindexOnStartup = false;
            opt.CollectionName = "documents";
            opt.SqliteVec = new SqliteVecOptions { DatabasePath = databasePath };
        });
    }

    private static IVectorStore CreateVectorStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        return options.Backend switch
        {
            VectorStoreBackend.InMemory => CreateInMemoryStore(options, serviceProvider),
            VectorStoreBackend.SqliteVec => CreateSqliteVecStore(options, serviceProvider),
            _ => throw new ArgumentException($"Unknown vector store backend: {options.Backend}")
        };
    }

    private static InMemoryVectorStore CreateInMemoryStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<InMemoryVectorStore>>();
        var wrappedOptions = Options.Create(options);
        return new InMemoryVectorStore(wrappedOptions, logger);
    }

    private static SqliteVecVectorStore CreateSqliteVecStore(VectorStoreOptions options, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<SqliteVecVectorStore>>();
        var wrappedOptions = Options.Create(options);
        return new SqliteVecVectorStore(wrappedOptions, logger);
    }
}
