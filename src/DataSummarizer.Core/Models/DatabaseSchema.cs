namespace Mostlylucid.DocSummarizer.Data.Models;

/// <summary>
/// Represents the schema of a database file.
/// </summary>
public record DatabaseSchema
{
    /// <summary>
    /// Path to the database file.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// List of tables in the database.
    /// </summary>
    public List<DatabaseTableInfo> Tables { get; init; } = [];

    /// <summary>
    /// Foreign key relationships between tables.
    /// </summary>
    public List<RelationshipInfo> Relationships { get; init; } = [];

    /// <summary>
    /// Indexes defined in the database.
    /// </summary>
    public List<IndexInfo> Indexes { get; init; } = [];

    /// <summary>
    /// Database type (SQLite, Access, etc.)
    /// </summary>
    public DatabaseType Type { get; init; }

    /// <summary>
    /// Total row count across all tables.
    /// </summary>
    public long TotalRowCount => Tables.Sum(t => t.RowCount);

    /// <summary>
    /// Total table count.
    /// </summary>
    public int TableCount => Tables.Count;

    /// <summary>
    /// Get a summary of the database schema.
    /// </summary>
    public string GetSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Database: {Type} ({Tables.Count} tables, {TotalRowCount:N0} total rows)");

        if (Relationships.Count > 0)
            sb.AppendLine($"Relationships: {Relationships.Count} foreign keys");

        foreach (var table in Tables.Take(10))
        {
            sb.AppendLine($"  - {table.Name}: {table.Columns.Count} columns, {table.RowCount:N0} rows");
        }

        if (Tables.Count > 10)
            sb.AppendLine($"  ... and {Tables.Count - 10} more tables");

        return sb.ToString();
    }
}

/// <summary>
/// Information about a database table.
/// </summary>
public record DatabaseTableInfo
{
    /// <summary>
    /// Table name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Table schema (if applicable).
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Columns in the table.
    /// </summary>
    public List<DatabaseColumnInfo> Columns { get; init; } = [];

    /// <summary>
    /// Number of rows in the table.
    /// </summary>
    public long RowCount { get; init; }

    /// <summary>
    /// Primary key columns.
    /// </summary>
    public List<string> PrimaryKey { get; init; } = [];

    /// <summary>
    /// Whether this is a system table.
    /// </summary>
    public bool IsSystemTable { get; init; }
}

/// <summary>
/// Information about a database column.
/// </summary>
public record DatabaseColumnInfo
{
    /// <summary>
    /// Column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Database-specific data type.
    /// </summary>
    public required string DataType { get; init; }

    /// <summary>
    /// Whether the column allows NULL values.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether the column is part of the primary key.
    /// </summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Whether the column is auto-increment.
    /// </summary>
    public bool IsAutoIncrement { get; init; }

    /// <summary>
    /// Default value (if any).
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Column position (0-based).
    /// </summary>
    public int Ordinal { get; init; }
}

/// <summary>
/// Represents a foreign key relationship between tables.
/// </summary>
public record RelationshipInfo
{
    /// <summary>
    /// Name of the relationship/constraint (if available).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Source table (the table with the foreign key).
    /// </summary>
    public required string FromTable { get; init; }

    /// <summary>
    /// Source column(s) containing the foreign key.
    /// </summary>
    public required string FromColumn { get; init; }

    /// <summary>
    /// Target table (the referenced table).
    /// </summary>
    public required string ToTable { get; init; }

    /// <summary>
    /// Target column(s) being referenced.
    /// </summary>
    public required string ToColumn { get; init; }

    /// <summary>
    /// Type of relationship (inferred from schema).
    /// </summary>
    public RelationshipType Type { get; init; } = RelationshipType.ManyToOne;

    /// <summary>
    /// On delete action.
    /// </summary>
    public string? OnDelete { get; init; }

    /// <summary>
    /// On update action.
    /// </summary>
    public string? OnUpdate { get; init; }

    /// <summary>
    /// Confidence that this relationship is correctly detected (1.0 for explicit FK, lower for heuristic).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Get a human-readable description.
    /// </summary>
    public string Description => $"{FromTable}.{FromColumn} -> {ToTable}.{ToColumn}";
}

/// <summary>
/// Type of relationship between tables.
/// </summary>
public enum RelationshipType
{
    /// <summary>
    /// One-to-one relationship.
    /// </summary>
    OneToOne,

    /// <summary>
    /// One-to-many relationship (the FK table has many rows per parent).
    /// </summary>
    OneToMany,

    /// <summary>
    /// Many-to-one relationship (many FK table rows reference one parent).
    /// </summary>
    ManyToOne,

    /// <summary>
    /// Many-to-many relationship (requires junction table).
    /// </summary>
    ManyToMany
}

/// <summary>
/// Information about a database index.
/// </summary>
public record IndexInfo
{
    /// <summary>
    /// Index name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Table the index is on.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Columns included in the index.
    /// </summary>
    public List<string> Columns { get; init; } = [];

    /// <summary>
    /// Whether the index enforces uniqueness.
    /// </summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// Whether this is the primary key index.
    /// </summary>
    public bool IsPrimaryKey { get; init; }
}

/// <summary>
/// Supported database types.
/// </summary>
public enum DatabaseType
{
    Unknown,
    SQLite,
    Access
}

/// <summary>
/// Data from a single database table.
/// </summary>
public class TableData
{
    /// <summary>
    /// Table name.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Column names.
    /// </summary>
    public List<string> Columns { get; init; } = [];

    /// <summary>
    /// Rows as list of dictionaries.
    /// </summary>
    public List<Dictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>
    /// Row count.
    /// </summary>
    public int RowCount => Rows.Count;
}
