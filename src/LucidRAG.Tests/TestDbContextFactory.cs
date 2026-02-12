using LucidRAG.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace LucidRAG.Tests;

/// <summary>
///     Factory for creating test database contexts using PostgreSQL via Testcontainers.
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    ///     Create a PostgreSQL database context for testing via Testcontainers.
    ///     Uses the pgvector-enabled image so vector columns work correctly.
    /// </summary>
    public static async Task<(RagDocumentsDbContext Context, PostgreSqlContainer Container)> CreatePostgresContainerAsync()
    {
        var container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .Build();

        await container.StartAsync();

        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseNpgsql(container.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options;

        var context = new RagDocumentsDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (context, container);
    }

    /// <summary>
    ///     Create a real PostgreSQL database context for integration testing.
    ///     Uses an existing running database.
    /// </summary>
    public static RagDocumentsDbContext CreatePostgres(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .Options;

        var context = new RagDocumentsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
