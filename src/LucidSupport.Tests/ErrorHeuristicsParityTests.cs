using System.Text.RegularExpressions;

namespace LucidSupport.Tests;

public class ErrorHeuristicsParityTests
{
    [Fact]
    public void ErrorClassRegex_IsAligned_AcrossLearningWidgetAndExtension()
    {
        var learning = ReadRepoFile("src", "LucidSupport", "Services", "Learning", "DomScriptSnippets.cs");
        var widget = ReadRepoFile("src", "LucidSupport", "Widget", "observer.ts");
        var extension = ReadRepoFile("src", "LucidSupport.Extension", "lib", "extractor.ts");

        var learningTokens = ExtractErrorClassTokens(learning);
        var widgetTokens = ExtractErrorClassTokens(widget);
        var extensionTokens = ExtractErrorClassTokens(extension);

        Assert.Equal(learningTokens, widgetTokens);
        Assert.Equal(learningTokens, extensionTokens);
    }

    [Fact]
    public void ParentErrorQuerySelectors_AreAligned_AcrossLearningWidgetAndExtension()
    {
        var learning = ReadRepoFile("src", "LucidSupport", "Services", "Learning", "DomScriptSnippets.cs");
        var widget = ReadRepoFile("src", "LucidSupport", "Widget", "observer.ts");
        var extension = ReadRepoFile("src", "LucidSupport.Extension", "lib", "extractor.ts");

        var learningSelectors = ExtractErrorQuerySelectors(learning);
        var widgetSelectors = ExtractErrorQuerySelectors(widget);
        var extensionSelectors = ExtractErrorQuerySelectors(extension);

        Assert.Equal(learningSelectors, widgetSelectors);
        Assert.Equal(learningSelectors, extensionSelectors);
    }

    private static SortedSet<string> ExtractErrorClassTokens(string source)
    {
        var match = Regex.Match(source, @"ERROR_CLASS_RE\s*=\s*/\\b\(([^)]+)\)\\b/i");
        Assert.True(match.Success, "Could not locate ERROR_CLASS_RE.");

        var tokens = match.Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0);

        return new SortedSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    private static SortedSet<string> ExtractErrorQuerySelectors(string source)
    {
        var match = Regex.Match(source, @"querySelector\(\s*'([^']*\.error-message[^']*)'\s*,?\s*\)");
        Assert.True(match.Success, "Could not locate error querySelector list.");

        var selectors = match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

        return new SortedSet<string>(selectors, StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var path = Path.Combine(GetRepoRoot(), Path.Combine(parts));
        return File.ReadAllText(path);
    }

    private static string GetRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var srcDir = Path.Combine(current.FullName, "src");
            if (Directory.Exists(Path.Combine(srcDir, "LucidSupport")) &&
                Directory.Exists(Path.Combine(srcDir, "LucidSupport.Extension")) &&
                Directory.Exists(Path.Combine(srcDir, "LucidSupport.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test runtime directory.");
    }
}
