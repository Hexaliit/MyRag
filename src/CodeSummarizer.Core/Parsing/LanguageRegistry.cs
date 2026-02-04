namespace CodeSummarizer.Parsing;

/// <summary>
///     Maps markdown fence language tags to Tree-sitter grammar names.
///     TreeSitter.DotNet uses lowercase names with hyphens that map to
///     native library names (e.g., "c-sharp" → tree-sitter-c-sharp.dll).
///     Only languages with actual grammars in TreeSitter.DotNet 1.1.1 are listed.
/// </summary>
public static class LanguageRegistry
{
    private static readonly Dictionary<string, string> FenceToTreeSitter = new(StringComparer.OrdinalIgnoreCase)
    {
        // C-family
        ["c"] = "c",
        ["cpp"] = "cpp",
        ["c++"] = "cpp",
        ["csharp"] = "c-sharp",
        ["cs"] = "c-sharp",
        ["c#"] = "c-sharp",

        // JVM
        ["java"] = "java",
        ["scala"] = "scala",

        // Web
        ["javascript"] = "javascript",
        ["js"] = "javascript",
        ["jsx"] = "javascript",
        ["typescript"] = "typescript",
        ["ts"] = "typescript",
        ["tsx"] = "tsx",
        ["html"] = "html",
        ["css"] = "css",

        // Scripting
        ["python"] = "python",
        ["py"] = "python",
        ["ruby"] = "ruby",
        ["rb"] = "ruby",
        ["php"] = "php",
        ["julia"] = "julia",

        // Systems
        ["go"] = "go",
        ["golang"] = "go",
        ["rust"] = "rust",
        ["rs"] = "rust",
        ["swift"] = "swift",
        ["verilog"] = "verilog",

        // Shell
        ["bash"] = "bash",
        ["sh"] = "bash",
        ["shell"] = "bash",
        ["zsh"] = "bash",

        // Data
        ["json"] = "json",
        ["toml"] = "toml",

        // Functional
        ["haskell"] = "haskell",
        ["hs"] = "haskell",
        ["ocaml"] = "ocaml",
        ["ml"] = "ocaml",
        ["agda"] = "agda",
        ["ql"] = "ql"
    };

    /// <summary>
    ///     Get all supported fence language tags.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedLanguages => FenceToTreeSitter.Keys;

    /// <summary>
    ///     Get the Tree-sitter grammar name for a markdown fence language.
    ///     Returns null if the language isn't supported.
    /// </summary>
    public static string? GetTreeSitterName(string fenceLanguage)
    {
        return FenceToTreeSitter.GetValueOrDefault(fenceLanguage.Trim());
    }

    /// <summary>
    ///     Check if a fence language has a Tree-sitter grammar available.
    /// </summary>
    public static bool IsSupported(string fenceLanguage)
    {
        return FenceToTreeSitter.ContainsKey(fenceLanguage.Trim());
    }
}