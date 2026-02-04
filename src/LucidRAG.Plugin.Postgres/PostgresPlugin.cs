using LucidRAG.Plugins;
using LucidRAG.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LucidRAG.Plugin.Postgres;

/// <summary>
///     PostgreSQL plugin. Provides ts_rank_cd full-text search and pgVector embeddings.
/// </summary>
public sealed class PostgresPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Name = "postgres",
        DisplayName = "PostgreSQL",
        Description = "PostgreSQL storage with ts_rank_cd FTS, pgVector embeddings, and schema-based multitenancy",
        Version = "1.0.0",
        Author = "Scott Galloway",
        Tags = ["storage", "postgresql", "fts", "pgvector"]
    };

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register PostgreSQL BM25 search as the IBm25SearchService implementation
        services.AddScoped<IBm25SearchService, PostgresBm25Service>();
    }
}