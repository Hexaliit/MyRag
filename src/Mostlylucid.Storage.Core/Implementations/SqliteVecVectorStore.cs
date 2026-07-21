using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;
using Mostlylucid.Storage.Core.Config;

namespace Mostlylucid.Storage.Core.Implementations;

public class SqliteVecVectorStore : IVectorStore
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteVecVectorStore> _logger;
    private readonly VectorStoreOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _collectionsInitialized = new();
    private bool _disposed;

    public SqliteVecVectorStore(IOptions<VectorStoreOptions> options, ILogger<SqliteVecVectorStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        var dbPath = _options.SqliteVec.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();

        InitializeGlobalTables();
        logger.LogInformation("SqliteVecVectorStore initialized at {Path}", dbPath);
    }

    public bool IsPersistent => true;
    public VectorStoreBackend Backend => VectorStoreBackend.SqliteVec;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }

    // ========== Collection Management ==========

    public Task CreateCollectionAsync(string collectionName, int vectorDimensions, CancellationToken ct = default)
    {
        return ExecuteWriteAsync(async () =>
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                using var insertCmd = _connection.CreateCommand();
                insertCmd.CommandText = """
                    INSERT OR IGNORE INTO collections (name, vector_dimension, created_at, updated_at)
                    VALUES (@name, @dim, datetime('now'), datetime('now'))
                """;
                insertCmd.Parameters.AddWithValue("@name", collectionName);
                insertCmd.Parameters.AddWithValue("@dim", vectorDimensions);
                insertCmd.Transaction = tx;
                insertCmd.ExecuteNonQuery();

                CreateDocTable(collectionName, vectorDimensions);
                CreateFtsTable(collectionName);
                CreateDocTriggers(collectionName);

                tx.Commit();
                _collectionsInitialized[collectionName] = true;
                _logger.LogDebug("Created collection {Collection} (dim={Dim})", collectionName, vectorDimensions);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        return ExecuteWriteAsync(() =>
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                using var dropCmd = _connection.CreateCommand();
                dropCmd.CommandText = $"DROP TABLE IF EXISTS \"docs_{SanitizeName(collectionName)}\"";
                dropCmd.Transaction = tx;
                dropCmd.ExecuteNonQuery();

                dropCmd.CommandText = $"DROP TABLE IF EXISTS \"fts_{SanitizeName(collectionName)}\"";
                dropCmd.ExecuteNonQuery();

                using var deleteCmd = _connection.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM collections WHERE name = @name";
                deleteCmd.Parameters.AddWithValue("@name", collectionName);
                deleteCmd.Transaction = tx;
                deleteCmd.ExecuteNonQuery();

                tx.Commit();
                _collectionsInitialized.TryRemove(collectionName, out _);
                _logger.LogDebug("Deleted collection {Collection}", collectionName);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    public Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM collections WHERE name = @name";
            cmd.Parameters.AddWithValue("@name", collectionName);
            var count = (long)(cmd.ExecuteScalar() ?? 0);
            return count > 0;
        }, ct);
    }

    // ========== Document Operations ==========

    public Task UpsertAsync(string collectionName, VectorStoreRecord record, CancellationToken ct = default)
    {
        return UpsertBatchAsync(collectionName, [record], ct);
    }

    public Task UpsertBatchAsync(string collectionName, IEnumerable<VectorStoreRecord> records, CancellationToken ct = default)
    {
        return ExecuteWriteAsync(() =>
        {
            EnsureCollectionInitialized(collectionName);
            var list = records.ToList();
            if (list.Count == 0) return;

            using var tx = _connection.BeginTransaction();
            try
            {
                var tableName = $"docs_{SanitizeName(collectionName)}";
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT OR REPLACE INTO "{tableName}"
                        (id, document_id, chunk_id, text, embedding, source_file, language, namespace, parent_id, content_hash, metadata, created_at, updated_at)
                    VALUES
                        (@id, @docId, @chunkId, @text, @emb, @src, @lang, @ns, @parent, @hash, @meta, @created, @updated)
                """;

                var idParam = cmd.Parameters.Add("@id", SqliteType.Text);
                var docIdParam = cmd.Parameters.Add("@docId", SqliteType.Text);
                var chunkIdParam = cmd.Parameters.Add("@chunkId", SqliteType.Text);
                var textParam = cmd.Parameters.Add("@text", SqliteType.Text);
                var embParam = cmd.Parameters.Add("@emb", SqliteType.Blob);
                var srcParam = cmd.Parameters.Add("@src", SqliteType.Text);
                var langParam = cmd.Parameters.Add("@lang", SqliteType.Text);
                var nsParam = cmd.Parameters.Add("@ns", SqliteType.Text);
                var parentParam = cmd.Parameters.Add("@parent", SqliteType.Text);
                var hashParam = cmd.Parameters.Add("@hash", SqliteType.Text);
                var metaParam = cmd.Parameters.Add("@meta", SqliteType.Text);
                var createdParam = cmd.Parameters.Add("@created", SqliteType.Text);
                var updatedParam = cmd.Parameters.Add("@updated", SqliteType.Text);

                foreach (var record in list)
                {
                    idParam.Value = record.Id;
                    docIdParam.Value = record.DocumentId;
                    chunkIdParam.Value = record.ChunkId;
                    textParam.Value = (object?)record.Text ?? DBNull.Value;
                    embParam.Value = FloatArrayToBlob(record.Embedding);
                    srcParam.Value = (object?)record.SourceFile ?? DBNull.Value;
                    langParam.Value = (object?)record.Language ?? DBNull.Value;
                    nsParam.Value = (object?)record.Namespace ?? DBNull.Value;
                    parentParam.Value = (object?)record.ParentId ?? DBNull.Value;
                    hashParam.Value = (object?)record.ContentHash ?? DBNull.Value;
                    metaParam.Value = record.SerializeMetadata();
                    createdParam.Value = record.CreatedAt.ToString("O");
                    updatedParam.Value = record.UpdatedAt.ToString("O");
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogDebug("Upserted {Count} documents into {Collection}", list.Count, collectionName);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    public Task DeleteAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        return ExecuteWriteAsync(() =>
        {
            EnsureCollectionInitialized(collectionName);
            var tableName = $"docs_{SanitizeName(collectionName)}";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""DELETE FROM "{tableName}" WHERE id = @id""";
            cmd.Parameters.AddWithValue("@id", documentId);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task<VectorStoreRecord?> GetByIdAsync(string collectionName, string documentId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_collectionsInitialized.ContainsKey(collectionName))
                return null;
            var tableName = $"docs_{SanitizeName(collectionName)}";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""SELECT * FROM "{tableName}" WHERE id = @id""";
            cmd.Parameters.AddWithValue("@id", documentId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }, ct);
    }

    public Task<long> CountAsync(string collectionName, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_collectionsInitialized.ContainsKey(collectionName))
                return 0L;
            var tableName = $"docs_{SanitizeName(collectionName)}";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""SELECT COUNT(*) FROM "{tableName}" """;
            return (long)(cmd.ExecuteScalar() ?? 0);
        }, ct);
    }

    public Task<List<VectorStoreRecord>> GetAllAsync(string collectionName, string? parentId = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_collectionsInitialized.ContainsKey(collectionName))
                return new List<VectorStoreRecord>();
            var tableName = $"docs_{SanitizeName(collectionName)}";
            using var cmd = _connection.CreateCommand();
            if (parentId != null)
            {
                cmd.CommandText = $"""SELECT * FROM "{tableName}" WHERE parent_id = @parent ORDER BY created_at""";
                cmd.Parameters.AddWithValue("@parent", parentId);
            }
            else
            {
                cmd.CommandText = $"""SELECT * FROM "{tableName}" ORDER BY created_at""";
            }
            var results = new List<VectorStoreRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRecord(reader));
            return results;
        }, ct);
    }

    // ========== Vector Search ==========

    public Task<List<SearchResult>> SearchAsync(string collectionName, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EnsureCollectionInitialized(collectionName);
            var tableName = $"docs_{SanitizeName(collectionName)}";
            var topK = filter?.TopK ?? 10;

            var records = new List<(VectorStoreRecord Record, double Score)>();

            using var cmd = _connection.CreateCommand();
            var sql = $"""SELECT * FROM "{tableName}" """;
            var conditions = new List<string>();

            if (filter?.Namespace != null)
            {
                conditions.Add("namespace = @ns");
                cmd.Parameters.AddWithValue("@ns", filter.Namespace);
            }
            if (filter?.DocumentId != null)
            {
                conditions.Add("document_id = @docId");
                cmd.Parameters.AddWithValue("@docId", filter.DocumentId);
            }
            if (filter?.Language != null)
            {
                conditions.Add("language = @lang");
                cmd.Parameters.AddWithValue("@lang", filter.Language);
            }
            if (filter?.SourceFile != null)
            {
                conditions.Add("source_file = @src");
                cmd.Parameters.AddWithValue("@src", filter.SourceFile);
            }
            if (filter?.MetadataFilter != null && filter.MetadataFilter.Count > 0)
            {
                foreach (var kvp in filter.MetadataFilter)
                {
                    conditions.Add($"metadata LIKE @meta_{kvp.Key}");
                    cmd.Parameters.AddWithValue($"@meta_{kvp.Key}", $"%\"{kvp.Key}\":\"{kvp.Value}\"%");
                }
            }

            if (conditions.Count > 0)
                sql += " WHERE " + string.Join(" AND ", conditions);

            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRecord(reader);
                var score = CosineSimilarity(queryVector, record.Embedding);
                if (score >= filter?.MinScore)
                    records.Add((record, score));
            }

            return records
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => new SearchResult
                {
                    Id = r.Record.Id,
                    Score = r.Score,
                    CosineScore = r.Score,
                    Record = r.Record,
                    Metadata = r.Record.Metadata,
                    Text = r.Record.Text
                })
                .ToList();
        }, ct);
    }

    // ========== Hybrid Search (FTS5 + Vector) ==========

    public Task<List<SearchResult>> HybridSearchAsync(string collectionName, string queryText, float[] queryVector, SearchFilter? filter = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            EnsureCollectionInitialized(collectionName);
            var tableName = $"docs_{SanitizeName(collectionName)}";
            var ftsName = $"fts_{SanitizeName(collectionName)}";
            var topK = filter?.TopK ?? 10;

            var ftsResults = new Dictionary<string, (double Score, int Rank)>();
            try
            {
                using var ftsCmd = _connection.CreateCommand();
                var ftsSql = $"""
                    SELECT rank, fts.id FROM "{ftsName}" fts
                    JOIN "{tableName}" d ON d.rowid = fts.rowid
                    WHERE fts MATCH @query
                """;
                var conditions = new List<string>();
                if (filter?.Namespace != null)
                {
                    conditions.Add("d.namespace = @ns");
                    ftsCmd.Parameters.AddWithValue("@ns", filter.Namespace);
                }
                if (filter?.DocumentId != null)
                {
                    conditions.Add("d.document_id = @docId");
                    ftsCmd.Parameters.AddWithValue("@docId", filter.DocumentId);
                }
                if (filter?.Language != null)
                {
                    conditions.Add("d.language = @lang");
                    ftsCmd.Parameters.AddWithValue("@lang", filter.Language);
                }
                if (conditions.Count > 0)
                    ftsSql += " AND " + string.Join(" AND ", conditions);

                ftsCmd.Parameters.AddWithValue("@query", queryText);
                ftsCmd.CommandText = ftsSql;

                var ftsRank = 0;
                using var ftsReader = ftsCmd.ExecuteReader();
                while (ftsReader.Read())
                {
                    var rank = ftsReader.GetDouble(0);
                    var id = ftsReader.GetString(1);
                    var bm25Normalized = 1.0 / (1.0 + Math.Abs(rank));
                    ftsResults[id] = (bm25Normalized, ftsRank++);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FTS5 search failed for {Collection}, falling back to vector-only", collectionName);
            }

            var vecResults = new Dictionary<string, (VectorStoreRecord Record, double Score, int Rank)>();
            using (var cmd = _connection.CreateCommand())
            {
                var sql = $"""SELECT * FROM "{tableName}" """;
                var conditions = new List<string>();
                if (filter?.Namespace != null)
                {
                    conditions.Add("namespace = @ns");
                    cmd.Parameters.AddWithValue("@ns", filter.Namespace);
                }
                if (filter?.DocumentId != null)
                {
                    conditions.Add("document_id = @docId");
                    cmd.Parameters.AddWithValue("@docId", filter.DocumentId);
                }
                if (filter?.Language != null)
                {
                    conditions.Add("language = @lang");
                    cmd.Parameters.AddWithValue("@lang", filter.Language);
                }
                if (filter?.SourceFile != null)
                {
                    conditions.Add("source_file = @src");
                    cmd.Parameters.AddWithValue("@src", filter.SourceFile);
                }
                if (conditions.Count > 0)
                    sql += " WHERE " + string.Join(" AND ", conditions);

                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var record = ReadRecord(reader);
                    var score = CosineSimilarity(queryVector, record.Embedding);
                    vecResults[record.Id] = (record, score, vecResults.Count);
                }
            }

            var fused = new List<(string Id, VectorStoreRecord Record, double CosineScore, double? Bm25Score, double FusedScore)>();
            var allIds = new HashSet<string>();
            foreach (var id in ftsResults.Keys) allIds.Add(id);
            foreach (var id in vecResults.Keys) allIds.Add(id);

            foreach (var id in allIds)
            {
                var ftsRank = ftsResults.GetValueOrDefault(id).Rank;
                var ftsScore = ftsResults.GetValueOrDefault(id).Score;
                var (record, vecScore, vecRank) = vecResults.GetValueOrDefault(id);

                var hasFts = ftsResults.ContainsKey(id);
                var hasVec = vecResults.ContainsKey(id);

                double fusedScore;
                if (hasFts && hasVec)
                {
                    fusedScore = 0.7 * vecScore + 0.3 * ftsScore;
                }
                else if (hasFts)
                {
                    fusedScore = 0.3 * ftsScore;
                }
                else
                {
                    fusedScore = 0.7 * vecScore;
                }

                fused.Add((id, record, vecScore, hasFts ? ftsScore : null, fusedScore));
            }

            return fused
                .OrderByDescending(r => r.FusedScore)
                .Take(topK)
                .Select(r => new SearchResult
                {
                    Id = r.Id,
                    Score = r.FusedScore,
                    CosineScore = r.CosineScore,
                    Bm25Score = r.Bm25Score,
                    Record = r.Record,
                    Metadata = r.Record?.Metadata ?? new Dictionary<string, object>(),
                    Text = r.Record?.Text
                })
                .ToList();
        }, ct);
    }

    // ========== Content Hash Operations ==========

    public Task<Dictionary<string, VectorStoreRecord>> GetByHashAsync(string collectionName, IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var result = new Dictionary<string, VectorStoreRecord>();
            if (!_collectionsInitialized.ContainsKey(collectionName))
                return result;

            var tableName = $"docs_{SanitizeName(collectionName)}";
            var hashList = contentHashes.ToList();
            if (hashList.Count == 0) return result;

            using var cmd = _connection.CreateCommand();
            var placeholders = hashList.Select((_, i) => $"@h{i}").ToList();
            cmd.CommandText = $"""SELECT * FROM "{tableName}" WHERE content_hash IN ({string.Join(",", placeholders)})""";
            for (var i = 0; i < hashList.Count; i++)
                cmd.Parameters.AddWithValue($"@h{i}", hashList[i]);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRecord(reader);
                if (record.ContentHash != null)
                    result[record.ContentHash] = record;
            }
            return result;
        }, ct);
    }

    public Task RemoveStaleAsync(string collectionName, string parentId, IEnumerable<string> validContentHashes, CancellationToken ct = default)
    {
        return ExecuteWriteAsync(() =>
        {
            EnsureCollectionInitialized(collectionName);
            var tableName = $"docs_{SanitizeName(collectionName)}";
            var validSet = new HashSet<string>(validContentHashes);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""SELECT id, content_hash FROM "{tableName}" WHERE parent_id = @parent""";
            cmd.Parameters.AddWithValue("@parent", parentId);
            var staleIds = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (hash == null || !validSet.Contains(hash))
                    staleIds.Add(id);
            }

            if (staleIds.Count == 0) return;

            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.CommandText = $"""DELETE FROM "{tableName}" WHERE id IN ({string.Join(",", staleIds.Select((_, i) => $"@s{i}"))})""";
            for (var i = 0; i < staleIds.Count; i++)
                deleteCmd.Parameters.AddWithValue($"@s{i}", staleIds[i]);
            deleteCmd.ExecuteNonQuery();
        }, ct);
    }

    // ========== Initialization ==========

    private void InitializeGlobalTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS collections (
                name TEXT PRIMARY KEY,
                vector_dimension INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """;
        cmd.ExecuteNonQuery();
    }

    private void CreateDocTable(string collectionName, int vectorDimensions)
    {
        var name = SanitizeName(collectionName);
        var createSql = $"CREATE TABLE IF NOT EXISTS \"docs_{name}\" (" +
                        "id TEXT PRIMARY KEY, " +
                        "document_id TEXT NOT NULL, " +
                        "chunk_id TEXT NOT NULL, " +
                        "text TEXT, " +
                        "embedding BLOB, " +
                        "source_file TEXT, " +
                        "language TEXT, " +
                        "namespace TEXT, " +
                        "parent_id TEXT, " +
                        "content_hash TEXT, " +
                        "metadata TEXT DEFAULT '{}', " +
                        "created_at TEXT NOT NULL, " +
                        "updated_at TEXT NOT NULL)";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = createSql;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"""CREATE INDEX IF NOT EXISTS "idx_{name}_doc" ON "docs_{name}"(document_id)""";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"""CREATE INDEX IF NOT EXISTS "idx_{name}_ns" ON "docs_{name}"(namespace)""";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"""CREATE INDEX IF NOT EXISTS "idx_{name}_hash" ON "docs_{name}"(content_hash)""";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"""CREATE INDEX IF NOT EXISTS "idx_{name}_parent" ON "docs_{name}"(parent_id)""";
        cmd.ExecuteNonQuery();
    }

    private void CreateFtsTable(string collectionName)
    {
        var name = SanitizeName(collectionName);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS "fts_{name}" USING fts5(
                id UNINDEXED,
                text,
                content='docs_{name}',
                content_rowid='rowid',
                tokenize='porter unicode61'
            )
        """;
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS5 table creation failed (may not be available), skipping FTS for {Collection}", collectionName);
        }
    }

    private void CreateDocTriggers(string collectionName)
    {
        var name = SanitizeName(collectionName);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS "tr_{name}_fts_ai" AFTER INSERT ON "docs_{name}" BEGIN
                    INSERT INTO "fts_{name}"(rowid, id, text) VALUES (new.rowid, new.id, new.text);
                END
            """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS "tr_{name}_fts_ad" AFTER DELETE ON "docs_{name}" BEGIN
                    INSERT INTO "fts_{name}"("fts_{name}", rowid, id, text) VALUES ('delete', old.rowid, old.id, old.text);
                END
            """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS "tr_{name}_fts_au" AFTER UPDATE ON "docs_{name}" BEGIN
                    INSERT INTO "fts_{name}"("fts_{name}", rowid, id, text) VALUES ('delete', old.rowid, old.id, old.text);
                    INSERT INTO "fts_{name}"(rowid, id, text) VALUES (new.rowid, new.id, new.text);
                END
            """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS5 triggers creation failed for {Collection}, FTS will not sync automatically", collectionName);
        }
    }

    private void EnsureCollectionInitialized(string collectionName)
    {
        if (_collectionsInitialized.ContainsKey(collectionName))
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT vector_dimension FROM collections WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", collectionName);
        var dim = cmd.ExecuteScalar();
        if (dim == null)
            throw new InvalidOperationException($"Collection '{collectionName}' not found. Call CreateCollectionAsync first.");

        _collectionsInitialized[collectionName] = true;
    }

    // ========== Helpers ==========

    private async Task ExecuteWriteAsync(Action action, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            action();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        return sb.ToString().ToLowerInvariant();
    }

    private static byte[] FloatArrayToBlob(float[] array)
    {
        var bytes = new byte[array.Length * 4];
        Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloatArray(byte[] blob)
    {
        var array = new float[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, array, 0, blob.Length);
        return array;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dotProduct = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var magnitude = Math.Sqrt(magA) * Math.Sqrt(magB);
        return magnitude == 0 ? 0 : dotProduct / magnitude;
    }

    private static VectorStoreRecord ReadRecord(SqliteDataReader reader)
    {
        var metadataStr = reader.IsDBNull(reader.GetOrdinal("metadata")) ? null : reader.GetString(reader.GetOrdinal("metadata"));

        return new VectorStoreRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
            ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
            Text = reader.IsDBNull(reader.GetOrdinal("text")) ? null : reader.GetString(reader.GetOrdinal("text")),
            Embedding = reader.IsDBNull(reader.GetOrdinal("embedding"))
                ? []
                : BlobToFloatArray((byte[])reader["embedding"]),
            SourceFile = reader.IsDBNull(reader.GetOrdinal("source_file")) ? null : reader.GetString(reader.GetOrdinal("source_file")),
            Language = reader.IsDBNull(reader.GetOrdinal("language")) ? null : reader.GetString(reader.GetOrdinal("language")),
            Namespace = reader.IsDBNull(reader.GetOrdinal("namespace")) ? null : reader.GetString(reader.GetOrdinal("namespace")),
            ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id")) ? null : reader.GetString(reader.GetOrdinal("parent_id")),
            ContentHash = reader.IsDBNull(reader.GetOrdinal("content_hash")) ? null : reader.GetString(reader.GetOrdinal("content_hash")),
            Metadata = VectorStoreRecord.DeserializeMetadata(metadataStr),
            CreatedAt = reader.IsDBNull(reader.GetOrdinal("created_at")) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = reader.IsDBNull(reader.GetOrdinal("updated_at")) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }
}
