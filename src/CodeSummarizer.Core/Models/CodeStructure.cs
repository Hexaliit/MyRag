namespace CodeSummarizer.Models;

/// <summary>
///     Structural information extracted from code via Tree-sitter AST parsing.
/// </summary>
public record CodeStructure
{
    /// <summary>Function/method signatures with names and parameter lists.</summary>
    public IReadOnlyList<FunctionSignature> Functions { get; init; } = [];

    /// <summary>Class/struct/interface/enum definitions.</summary>
    public IReadOnlyList<ClassSignature> Classes { get; init; } = [];

    /// <summary>Import/using/require statements.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Total lines in the code block.</summary>
    public int TotalLines { get; init; }

    /// <summary>Language that was parsed.</summary>
    public string Language { get; init; } = "";

    /// <summary>Whether parsing succeeded (false = fallback to heuristic).</summary>
    public bool Parsed { get; init; }
}

/// <summary>
///     A function or method extracted from source code.
/// </summary>
public record FunctionSignature
{
    /// <summary>Function/method name.</summary>
    public required string Name { get; init; }

    /// <summary>Parameter list as a string (e.g., "string name, int count").</summary>
    public string Parameters { get; init; } = "";

    /// <summary>Return type if available.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Whether this is async.</summary>
    public bool IsAsync { get; init; }

    public override string ToString()
    {
        var asyncPrefix = IsAsync ? "async " : "";
        var returnPart = ReturnType != null ? $"{ReturnType} " : "";
        return $"{asyncPrefix}{returnPart}{Name}({Parameters})";
    }
}

/// <summary>
///     A class, struct, interface, or enum extracted from source code.
/// </summary>
public record ClassSignature
{
    /// <summary>Type name.</summary>
    public required string Name { get; init; }

    /// <summary>Kind: class, struct, interface, enum, record, trait, etc.</summary>
    public string Kind { get; init; } = "class";

    /// <summary>Base classes/interfaces.</summary>
    public IReadOnlyList<string> BaseTypes { get; init; } = [];

    /// <summary>Method count within the class (if detectable).</summary>
    public int MethodCount { get; init; }

    public override string ToString()
    {
        var bases = BaseTypes.Count > 0 ? $" : {string.Join(", ", BaseTypes)}" : "";
        return $"{Kind} {Name}{bases}";
    }
}
