using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// Relationship Detection Wave - Detects foreign key relationships in databases.
/// For database files, extracts explicit FKs from schema.
/// For any data file, detects implicit relationships via column naming conventions.
/// Priority: 85 (after TypeInference, before Profile)
/// </summary>
public class RelationshipWave : IDataAnalysisWave
{
    private readonly IDatabaseReader? _databaseReader;
    private readonly ILogger<RelationshipWave>? _logger;

    public RelationshipWave(
        IDatabaseReader? databaseReader = null,
        ILogger<RelationshipWave>? logger = null)
    {
        _databaseReader = databaseReader;
        _logger = logger;
    }

    public string Name => "RelationshipWave";
    public int Priority => 85; // After TypeInference (80), before Profile (70)
    public IReadOnlyList<string> Tags => [DataSignalTags.Schema, "relationship"];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Run for database files if we have a reader
        if (IsDatabaseFile(file) && _databaseReader != null)
            return true;

        // Also run for regular data files to detect implicit relationships
        // (e.g., columns named "*_id" that might reference other tables)
        return context.HasSignal(DataSignalKeys.ColumnNames);
    }

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();

        // Handle database files with explicit FK extraction
        if (IsDatabaseFile(file) && _databaseReader != null)
        {
            var dbSignals = await AnalyzeDatabaseAsync(file.FilePath, context, ct);
            signals.AddRange(dbSignals);
        }

        // Detect implicit relationships from column names
        var implicitSignals = DetectImplicitRelationships(context);
        signals.AddRange(implicitSignals);

        return signals;
    }

    private async Task<IEnumerable<DataSignal>> AnalyzeDatabaseAsync(
        string filePath,
        DataAnalysisContext context,
        CancellationToken ct)
    {
        var signals = new List<DataSignal>();

        try
        {
            // Check if schema is already in context (from IdentityWave)
            var cachedSchema = context.GetValue<DatabaseSchema>("database.schema");

            DatabaseSchema schema;
            if (cachedSchema != null)
            {
                schema = cachedSchema;
            }
            else
            {
                schema = await _databaseReader!.GetSchemaAsync(filePath, ct);
                // Store in context for other waves
                signals.Add(new DataSignal
                {
                    Key = "database.schema",
                    Value = schema,
                    Source = Name,
                    Confidence = 1.0,
                    Tags = [DataSignalTags.Schema, "database"]
                });
            }

            // Emit relationship count
            signals.Add(new DataSignal
            {
                Key = "relationship.count",
                Value = schema.Relationships.Count,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Schema, "relationship"]
            });

            // Emit each explicit FK relationship
            foreach (var rel in schema.Relationships)
            {
                signals.Add(new DataSignal
                {
                    Key = $"relationship.fk.{rel.FromTable}.{rel.FromColumn}",
                    Value = new Dictionary<string, object?>
                    {
                        ["from_table"] = rel.FromTable,
                        ["from_column"] = rel.FromColumn,
                        ["to_table"] = rel.ToTable,
                        ["to_column"] = rel.ToColumn,
                        ["type"] = rel.Type.ToString(),
                        ["on_delete"] = rel.OnDelete,
                        ["on_update"] = rel.OnUpdate
                    },
                    Source = Name,
                    Confidence = rel.Confidence,
                    Tags = [DataSignalTags.Schema, "relationship", "explicit"],
                    Metadata = new Dictionary<string, object>
                    {
                        ["description"] = rel.Description
                    }
                });
            }

            // Emit table summary
            signals.Add(new DataSignal
            {
                Key = "database.table_count",
                Value = schema.Tables.Count,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Identity, "database"]
            });

            // Emit table list with row counts
            foreach (var table in schema.Tables)
            {
                signals.Add(new DataSignal
                {
                    Key = $"database.table.{table.Name}.row_count",
                    Value = table.RowCount,
                    Source = Name,
                    Confidence = 1.0,
                    Tags = [DataSignalTags.Identity, "database", "table"]
                });

                signals.Add(new DataSignal
                {
                    Key = $"database.table.{table.Name}.columns",
                    Value = table.Columns.Select(c => c.Name).ToArray(),
                    Source = Name,
                    Confidence = 1.0,
                    Tags = [DataSignalTags.Schema, "database", "table"]
                });

                // Emit primary key info
                if (table.PrimaryKey.Count > 0)
                {
                    signals.Add(new DataSignal
                    {
                        Key = $"database.table.{table.Name}.primary_key",
                        Value = table.PrimaryKey.ToArray(),
                        Source = Name,
                        Confidence = 1.0,
                        Tags = [DataSignalTags.Schema, "database", "pk"]
                    });
                }
            }

            // Build relationship graph summary
            if (schema.Relationships.Count > 0)
            {
                var graphSummary = BuildRelationshipGraphSummary(schema);
                signals.Add(new DataSignal
                {
                    Key = "relationship.graph_summary",
                    Value = graphSummary,
                    Source = Name,
                    Confidence = 1.0,
                    Tags = [DataSignalTags.Schema, "relationship", "summary"]
                });
            }

            _logger?.LogInformation("Detected {RelCount} explicit FK relationships in {FilePath}",
                schema.Relationships.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to analyze database relationships for {FilePath}", filePath);
            signals.Add(new DataSignal
            {
                Key = "relationship.error",
                Value = ex.Message,
                Source = Name,
                Confidence = 0,
                Tags = [DataSignalTags.Schema, "error"]
            });
        }

        return signals;
    }

    private IEnumerable<DataSignal> DetectImplicitRelationships(DataAnalysisContext context)
    {
        var signals = new List<DataSignal>();

        // Get column names from context
        var columnNames = context.GetValue<string[]>(DataSignalKeys.ColumnNames);
        if (columnNames == null || columnNames.Length == 0)
            return signals;

        var implicitRelationships = new List<Dictionary<string, object?>>();

        foreach (var column in columnNames)
        {
            var lowerColumn = column.ToLowerInvariant();

            // Heuristic 1: Column ends with "_id" (e.g., "user_id" references "user" table)
            if (lowerColumn.EndsWith("_id") && lowerColumn.Length > 3)
            {
                var inferredTable = lowerColumn[..^3]; // Remove "_id"
                implicitRelationships.Add(new Dictionary<string, object?>
                {
                    ["from_column"] = column,
                    ["inferred_table"] = inferredTable,
                    ["pattern"] = "*_id",
                    ["confidence"] = 0.7
                });

                signals.Add(new DataSignal
                {
                    Key = $"relationship.implicit.{column}",
                    Value = new Dictionary<string, object?>
                    {
                        ["column"] = column,
                        ["inferred_table"] = inferredTable,
                        ["pattern"] = "suffix_id"
                    },
                    Source = Name,
                    Confidence = 0.7,
                    Tags = [DataSignalTags.Schema, "relationship", "implicit"]
                });
            }
            // Heuristic 2: Column ends with "Id" (PascalCase, e.g., "UserId")
            else if (column.EndsWith("Id") && column.Length > 2 && char.IsUpper(column[0]))
            {
                var inferredTable = column[..^2]; // Remove "Id"
                implicitRelationships.Add(new Dictionary<string, object?>
                {
                    ["from_column"] = column,
                    ["inferred_table"] = inferredTable,
                    ["pattern"] = "*Id",
                    ["confidence"] = 0.65
                });

                signals.Add(new DataSignal
                {
                    Key = $"relationship.implicit.{column}",
                    Value = new Dictionary<string, object?>
                    {
                        ["column"] = column,
                        ["inferred_table"] = inferredTable,
                        ["pattern"] = "suffix_Id_pascal"
                    },
                    Source = Name,
                    Confidence = 0.65,
                    Tags = [DataSignalTags.Schema, "relationship", "implicit"]
                });
            }
            // Heuristic 3: Column named "fk_*" or "FK_*"
            else if (lowerColumn.StartsWith("fk_") && lowerColumn.Length > 3)
            {
                var inferredTable = lowerColumn[3..]; // Remove "fk_"
                implicitRelationships.Add(new Dictionary<string, object?>
                {
                    ["from_column"] = column,
                    ["inferred_table"] = inferredTable,
                    ["pattern"] = "fk_*",
                    ["confidence"] = 0.8
                });

                signals.Add(new DataSignal
                {
                    Key = $"relationship.implicit.{column}",
                    Value = new Dictionary<string, object?>
                    {
                        ["column"] = column,
                        ["inferred_table"] = inferredTable,
                        ["pattern"] = "prefix_fk"
                    },
                    Source = Name,
                    Confidence = 0.8,
                    Tags = [DataSignalTags.Schema, "relationship", "implicit"]
                });
            }
        }

        if (implicitRelationships.Count > 0)
        {
            signals.Add(new DataSignal
            {
                Key = "relationship.implicit.count",
                Value = implicitRelationships.Count,
                Source = Name,
                Confidence = 0.7,
                Tags = [DataSignalTags.Schema, "relationship", "implicit"]
            });

            _logger?.LogDebug("Detected {Count} implicit relationships from column naming patterns",
                implicitRelationships.Count);
        }

        return signals;
    }

    private static string BuildRelationshipGraphSummary(DatabaseSchema schema)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Database Relationships:");

        // Group relationships by source table
        var bySourceTable = schema.Relationships.GroupBy(r => r.FromTable);
        foreach (var group in bySourceTable.OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {group.Key}:");
            foreach (var rel in group)
            {
                sb.AppendLine($"    .{rel.FromColumn} -> {rel.ToTable}.{rel.ToColumn}");
            }
        }

        // Find tables with no relationships (orphan tables)
        var tablesWithFKs = schema.Relationships.Select(r => r.FromTable).Distinct().ToHashSet();
        var tablesReferencedByFKs = schema.Relationships.Select(r => r.ToTable).Distinct().ToHashSet();
        var allTablesInRelationships = tablesWithFKs.Union(tablesReferencedByFKs).ToHashSet();
        var orphanTables = schema.Tables
            .Select(t => t.Name)
            .Where(t => !allTablesInRelationships.Contains(t))
            .ToList();

        if (orphanTables.Count > 0)
        {
            sb.AppendLine($"  Standalone tables: {string.Join(", ", orphanTables)}");
        }

        return sb.ToString();
    }

    private static bool IsDatabaseFile(DataFile file)
    {
        return file.Extension is ".db" or ".sqlite" or ".sqlite3" or ".accdb" or ".mdb";
    }
}
