using LucidRAG.Data;
using Microsoft.EntityFrameworkCore;

namespace LucidRAG.Tests;

/// <summary>
///     Factory for creating test database contexts
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    ///     Create an in-memory database context for unit testing.
    ///
    ///     NOTE: Prefer SQLite in-memory over EF InMemory so that provider-specific mappings
    ///     (e.g. conditional pgvector/SQLite behavior) execute correctly in the model.
    /// </summary>
    public static RagDocumentsDbContext CreateInMemory(string? databaseName = null)
    {
        _ = databaseName; // Reserved for future use (e.g. shared-cache SQLite DBs)

        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        var context = new RagDocumentsDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    ///     Create a real PostgreSQL database context for integration testing
    ///     Uses the existing dev database
    /// </summary>
    public static RagDocumentsDbContext CreatePostgres(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RagDocumentsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var context = new RagDocumentsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
