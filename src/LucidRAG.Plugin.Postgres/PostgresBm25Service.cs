using System.Diagnostics;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LucidRAG.Plugin.Postgres;

/// <summary>
///     PostgreSQL-native full-text search service using ts_rank_cd.
///     Registered by PostgresPlugin as IBm25SearchService.
/// </summary>
public class PostgresBm25Service : IBm25SearchService
{
    private readonly string _connectionString;
    private readonly RagDocumentsDbContext _db;
    private readonly ILogger<PostgresBm25Service> _logger;

    public PostgresBm25Service(
        RagDocumentsDbContext db,
        IConfiguration configuration,
        ILogger<PostgresBm25Service> logger)
    {
        _db = db;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException(
                                "DefaultConnection string not found in configuration");
    }

    public async Task<List<(EvidenceArtifact artifact, double score)>> SearchAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var stopwatch = Stopwatch.StartNew();

        try
        {
            List<EvidenceArtifact> results;

            if (documentIds != null && documentIds.Any())
            {
                var docIdArray = documentIds.ToArray();
                var sql = @"
                    SELECT ea.*,
                        ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', {0}), 32) as rank_score
                    FROM evidence_artifacts ea
                    WHERE ea.content_tokens @@ websearch_to_tsquery('english', {0})
                    AND ea.document_id = ANY({1})
                    ORDER BY rank_score DESC
                    LIMIT {2}";

                results = await _db.EvidenceArtifacts
                    .FromSqlRaw(sql, query, docIdArray, topK)
                    .AsNoTracking()
                    .ToListAsync(ct);
            }
            else
            {
                var sql = @"
                    SELECT ea.*,
                        ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', {0}), 32) as rank_score
                    FROM evidence_artifacts ea
                    WHERE ea.content_tokens @@ websearch_to_tsquery('english', {0})
                    ORDER BY rank_score DESC
                    LIMIT {1}";

                results = await _db.EvidenceArtifacts
                    .FromSqlRaw(sql, query, topK)
                    .AsNoTracking()
                    .ToListAsync(ct);
            }

            var scored = results.Select(r => (r, score: 1.0)).ToList();

            stopwatch.Stop();
            _logger.LogDebug(
                "PostgreSQL FTS query completed in {ElapsedMs}ms: '{Query}' returned {Count} results",
                stopwatch.ElapsedMilliseconds, query, results.Count);

            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgreSQL FTS search failed for query: {Query}", query);
            throw;
        }
    }

    public async Task<List<(EvidenceArtifact artifact, double score)>> SearchWithScoresAsync(
        string query,
        int topK = 25,
        IEnumerable<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sql = @"
                SELECT
                    ea.""Id"",
                    ts_rank_cd(ea.content_tokens, websearch_to_tsquery('english', $1), 32) as score
                FROM evidence_artifacts ea
                WHERE ea.content_tokens @@ websearch_to_tsquery('english', $1)
                ORDER BY score DESC
                LIMIT $2";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(query);
            command.Parameters.AddWithValue(topK);

            var idsAndScores = new List<(Guid id, double score)>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) idsAndScores.Add((reader.GetGuid(0), reader.GetDouble(1)));
            await reader.CloseAsync();

            var ids = idsAndScores.Select(x => x.id).ToList();
            var artifacts = await _db.EvidenceArtifacts
                .Where(ea => ids.Contains(ea.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            var results = idsAndScores
                .Select(x => (artifacts.First(a => a.Id == x.id), x.score))
                .ToList();

            stopwatch.Stop();
            _logger.LogDebug(
                "PostgreSQL FTS query completed in {ElapsedMs}ms: '{Query}' returned {Count} results",
                stopwatch.ElapsedMilliseconds, query, results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgreSQL FTS search with scores failed for query: {Query}", query);
            throw;
        }
    }

    /// <summary>
    ///     Hybrid search combining dense embeddings, BM25 (FTS), and salience using RRF.
    ///     Runs entirely in PostgreSQL.
    /// </summary>
    public async Task<List<(EvidenceArtifact artifact, double rrfScore)>> HybridSearchAsync(
        string query,
        float[]? queryEmbedding = null,
        int topK = 25,
        int rrfK = 60,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sql = @"
                WITH dense_ranks AS (
                    SELECT ""Id"", ROW_NUMBER() OVER (ORDER BY embedding <=> $1::vector) as rank
                    FROM evidence_artifacts
                    WHERE embedding IS NOT NULL AND $1 IS NOT NULL
                ),
                bm25_ranks AS (
                    SELECT ""Id"",
                        ROW_NUMBER() OVER (ORDER BY ts_rank_cd(content_tokens, websearch_to_tsquery('english', $2), 32) DESC) as rank
                    FROM evidence_artifacts
                    WHERE content_tokens @@ websearch_to_tsquery('english', $2)
                ),
                salience_ranks AS (
                    SELECT ""Id"",
                        ROW_NUMBER() OVER (ORDER BY (metadata->>'salience_score')::float DESC NULLS LAST) as rank
                    FROM evidence_artifacts
                    WHERE metadata ? 'salience_score'
                )
                SELECT ea.""Id"",
                    (1.0 / ($3 + COALESCE(d.rank, 1000)) +
                     1.0 / ($3 + COALESCE(b.rank, 1000)) +
                     1.0 / ($3 + COALESCE(s.rank, 1000))) as rrf_score
                FROM evidence_artifacts ea
                LEFT JOIN dense_ranks d ON ea.""Id"" = d.""Id""
                LEFT JOIN bm25_ranks b ON ea.""Id"" = b.""Id""
                LEFT JOIN salience_ranks s ON ea.""Id"" = s.""Id""
                WHERE d.rank IS NOT NULL OR b.rank IS NOT NULL OR s.rank IS NOT NULL
                ORDER BY rrf_score DESC
                LIMIT $4";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(queryEmbedding != null ? queryEmbedding : DBNull.Value);
            command.Parameters.AddWithValue(query);
            command.Parameters.AddWithValue(rrfK);
            command.Parameters.AddWithValue(topK);

            var idsAndScores = new List<(Guid id, double score)>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) idsAndScores.Add((reader.GetGuid(0), reader.GetDouble(1)));
            await reader.CloseAsync();

            var ids = idsAndScores.Select(x => x.id).ToList();
            var artifacts = await _db.EvidenceArtifacts
                .Where(ea => ids.Contains(ea.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            var results = idsAndScores
                .Select(x => (artifacts.First(a => a.Id == x.id), x.score))
                .ToList();

            stopwatch.Stop();
            _logger.LogInformation(
                "PostgreSQL Hybrid RRF search completed in {ElapsedMs}ms: '{Query}' returned {Count} results",
                stopwatch.ElapsedMilliseconds, query, results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgreSQL Hybrid RRF search failed for query: {Query}", query);
            throw;
        }
    }

    /// <summary>
    ///     Refresh corpus statistics materialized view.
    /// </summary>
    public async Task RefreshCorpusStatsAsync(CancellationToken ct = default)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "REFRESH MATERIALIZED VIEW CONCURRENTLY corpus_stats", ct);
        _logger.LogInformation("Corpus statistics materialized view refreshed");
    }
}