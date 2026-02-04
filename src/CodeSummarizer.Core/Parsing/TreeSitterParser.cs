using System.Text.RegularExpressions;
using CodeSummarizer.Models;
using Microsoft.Extensions.Logging;
using TreeSitter;

namespace CodeSummarizer.Parsing;

/// <summary>
///     Uses Tree-sitter to parse code into an AST and extract structural information:
///     function signatures, class definitions, and import statements.
/// </summary>
public sealed partial class TreeSitterParser : IDisposable
{
    private readonly ILogger<TreeSitterParser>? _logger;

    public TreeSitterParser(ILogger<TreeSitterParser>? logger = null)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        // Tree-sitter resources are disposed per-parse via using statements
    }

    /// <summary>
    ///     Parse code and extract structural elements using Tree-sitter.
    ///     Falls back to regex-based heuristics if the language isn't supported
    ///     or parsing fails.
    /// </summary>
    public CodeStructure Parse(string code, string fenceLanguage)
    {
        var totalLines = code.Split('\n').Length;
        var tsName = LanguageRegistry.GetTreeSitterName(fenceLanguage);

        if (tsName == null)
        {
            _logger?.LogDebug("No Tree-sitter grammar for '{Language}', using heuristic fallback", fenceLanguage);
            return HeuristicParse(code, fenceLanguage, totalLines);
        }

        try
        {
            using var language = new Language(tsName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(code);

            if (tree == null)
            {
                _logger?.LogWarning("Tree-sitter returned null tree for '{Language}'", fenceLanguage);
                return HeuristicParse(code, fenceLanguage, totalLines);
            }

            var functions = new List<FunctionSignature>();
            var classes = new List<ClassSignature>();
            var imports = new List<string>();

            WalkTree(tree.RootNode, code, functions, classes, imports, fenceLanguage);

            return new CodeStructure
            {
                Functions = functions,
                Classes = classes,
                Imports = imports,
                TotalLines = totalLines,
                Language = fenceLanguage,
                Parsed = true
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tree-sitter parse failed for '{Language}', falling back to heuristic",
                fenceLanguage);
            return HeuristicParse(code, fenceLanguage, totalLines);
        }
    }

    private void WalkTree(
        Node node, string source,
        List<FunctionSignature> functions,
        List<ClassSignature> classes,
        List<string> imports,
        string language)
    {
        var nodeType = node.Type;

        // Function/method definitions
        if (IsFunctionNode(nodeType))
        {
            var sig = ExtractFunctionSignature(node, source, language);
            if (sig != null) functions.Add(sig);
        }
        // Class/struct/interface definitions
        else if (IsClassNode(nodeType))
        {
            var sig = ExtractClassSignature(node, source, language);
            if (sig != null) classes.Add(sig);
        }
        // Import/using/require statements
        else if (IsImportNode(nodeType))
        {
            var text = GetNodeText(node, source).Trim();
            if (text.Length > 0 && text.Length < 200)
                imports.Add(text);
        }

        // Recurse into children
        foreach (var child in node.Children)
            WalkTree(child, source, functions, classes, imports, language);
    }

    private static bool IsFunctionNode(string nodeType)
    {
        return nodeType is "function_definition" or "function_declaration"
            or "method_definition" or "method_declaration"
            or "function_item" // Rust
            or "arrow_function"
            or "function_expression"
            or "constructor_declaration"
            or "function" // some grammars
            or "decorated_definition";
        // Python with decorators
    }

    private static bool IsClassNode(string nodeType)
    {
        return nodeType is "class_definition" or "class_declaration"
            or "struct_definition" or "struct_declaration"
            or "struct_item" // Rust
            or "interface_declaration"
            or "enum_declaration" or "enum_definition"
            or "record_declaration"
            or "trait_item";
        // Rust
    }

    private static bool IsImportNode(string nodeType)
    {
        return nodeType is "import_statement" or "import_declaration"
            or "using_directive" or "using_declaration"
            or "include_directive" or "preproc_include"
            or "require_expression"
            or "use_declaration" // Rust
            or "import_from_statement";
        // Python
    }

    private FunctionSignature? ExtractFunctionSignature(Node node, string source, string language)
    {
        string? name = null;
        var parameters = "";
        string? returnType = null;
        var isAsync = false;

        foreach (var child in node.Children)
        {
            var childType = child.Type;
            var childText = GetNodeText(child, source);

            switch (childType)
            {
                case "identifier" or "name" or "property_identifier":
                    name ??= childText;
                    break;
                case "parameters" or "formal_parameters" or "parameter_list":
                    parameters = childText.Trim('(', ')');
                    break;
                case "type" or "return_type" or "type_annotation" or "predefined_type":
                    returnType ??= childText;
                    break;
            }

            if (childText is "async")
                isAsync = true;
        }

        if (name == null) return null;

        // Simplify long parameter lists
        if (parameters.Length > 80)
            parameters = TruncateParams(parameters);

        return new FunctionSignature
        {
            Name = name,
            Parameters = parameters,
            ReturnType = returnType,
            IsAsync = isAsync
        };
    }

    private ClassSignature? ExtractClassSignature(Node node, string source, string language)
    {
        string? name = null;
        var kind = node.Type switch
        {
            var t when t.Contains("struct") => "struct",
            var t when t.Contains("interface") => "interface",
            var t when t.Contains("enum") => "enum",
            var t when t.Contains("record") => "record",
            var t when t.Contains("trait") => "trait",
            _ => "class"
        };
        var baseTypes = new List<string>();
        var methodCount = 0;

        foreach (var child in node.Children)
        {
            var childType = child.Type;
            var childText = GetNodeText(child, source);

            switch (childType)
            {
                case "identifier" or "name" or "type_identifier":
                    name ??= childText;
                    break;
                case "superclass" or "base_list" or "superinterfaces" or "superclass_clause":
                    baseTypes.Add(childText.TrimStart(':', '(').Trim());
                    break;
                case "class_body" or "block" or "declaration_list" or "enum_body":
                    methodCount = CountMethods(child);
                    break;
            }
        }

        if (name == null) return null;

        return new ClassSignature
        {
            Name = name,
            Kind = kind,
            BaseTypes = baseTypes,
            MethodCount = methodCount
        };
    }

    private static int CountMethods(Node bodyNode)
    {
        var count = 0;
        foreach (var child in bodyNode.Children)
            if (IsFunctionNode(child.Type))
                count++;

        return count;
    }

    private static string GetNodeText(Node node, string source)
    {
        var start = node.StartIndex;
        var end = node.EndIndex;
        if (start >= 0 && end <= source.Length && start < end)
            return source[start..end];
        return "";
    }

    private static string TruncateParams(string parameters)
    {
        // Keep first 3 params, indicate more
        var parts = parameters.Split(',');
        if (parts.Length <= 3) return parameters;
        return string.Join(", ", parts.Take(3).Select(p => p.Trim())) + $", ... ({parts.Length - 3} more)";
    }

    /// <summary>
    ///     Regex-based fallback for languages without Tree-sitter support.
    ///     Extracts basic structural elements using common patterns.
    /// </summary>
    private CodeStructure HeuristicParse(string code, string language, int totalLines)
    {
        var functions = new List<FunctionSignature>();
        var classes = new List<ClassSignature>();
        var imports = new List<string>();

        // Function patterns (cover most C-like and scripting languages)
        foreach (Match m in FunctionPatternRegex().Matches(code))
            functions.Add(new FunctionSignature
            {
                Name = m.Groups[2].Value,
                Parameters = m.Groups[3].Value,
                IsAsync = m.Groups[1].Value == "async"
            });

        // Class/struct/interface patterns
        foreach (Match m in ClassPatternRegex().Matches(code))
        {
            var baseType = m.Groups[3].Value.Trim();
            classes.Add(new ClassSignature
            {
                Name = m.Groups[2].Value,
                Kind = m.Groups[1].Value.ToLowerInvariant(),
                BaseTypes = string.IsNullOrWhiteSpace(baseType) ? [] : [baseType]
            });
        }

        // Import/using patterns
        foreach (Match m in ImportPatternRegex().Matches(code))
            imports.Add(m.Value.Trim().TrimEnd(';'));

        return new CodeStructure
        {
            Functions = functions,
            Classes = classes,
            Imports = imports,
            TotalLines = totalLines,
            Language = language,
            Parsed = false
        };
    }

    // Heuristic regexes for languages without tree-sitter support
    [GeneratedRegex(
        @"(?:^|\n)\s*(?:export\s+)?(?:public\s+|private\s+|protected\s+|static\s+|internal\s+)*(async\s+)?(?:def|function|fun|func|fn|sub)\s+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Multiline)]
    private static partial Regex FunctionPatternRegex();

    [GeneratedRegex(
        @"(?:^|\n)\s*(?:export\s+)?(?:public\s+|abstract\s+|sealed\s+|final\s+)*(class|struct|interface|enum|record|trait)\s+(\w+)(?:\s*(?:extends|implements|:)\s*(\w+))?",
        RegexOptions.Multiline)]
    private static partial Regex ClassPatternRegex();

    [GeneratedRegex(@"^(?:using|import|require|include|from\s+\S+\s+import)\s+.+$", RegexOptions.Multiline)]
    private static partial Regex ImportPatternRegex();
}