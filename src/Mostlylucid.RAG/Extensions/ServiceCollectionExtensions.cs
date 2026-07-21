using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.RAG.Config;
using Mostlylucid.RAG.Services;
using Mostlylucid.Storage.Core.Abstractions;

namespace Mostlylucid.RAG.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddSemanticSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration.GetSection(SemanticSearchConfig.Section).Get<SemanticSearchConfig>()
                     ?? new SemanticSearchConfig();

        services.AddSingleton(config);
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
        services.AddSingleton<IVectorStoreService>(sp =>
        {
            var store = sp.GetRequiredService<IVectorStore>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqliteVecVectorStoreService>>();
            return new SqliteVecVectorStoreService(logger, config, store);
        });
        services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
    }
}
