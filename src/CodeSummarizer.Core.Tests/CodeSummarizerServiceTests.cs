using CodeSummarizer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSummarizer.Core.Tests;

public class CodeSummarizerServiceTests
{
    private readonly CodeSummarizerService _service = new(NullLogger<CodeSummarizerService>.Instance);

    [Fact]
    public async Task SummarizeCodeAsync_CSharp_ReturnsStructuralSummary()
    {
        var code = """
                   public class UserService
                   {
                       public async Task<User> GetUserAsync(int id)
                       {
                           return await _db.Users.FindAsync(id);
                       }
                   }
                   """;

        var result = await _service.SummarizeCodeAsync(code, "csharp");

        result.Language.Should().Be("csharp");
        result.Source.Should().BeOneOf("tree-sitter", "heuristic");
        result.Description.Should().NotBeNullOrWhiteSpace();
        result.Description.Should().Contain("UserService");
        result.OriginalCode.Should().Be(code);
    }

    [Fact]
    public async Task SummarizeCodeAsync_Python_ReturnsDescription()
    {
        var code = """
                   def fibonacci(n):
                       if n <= 1:
                           return n
                       return fibonacci(n-1) + fibonacci(n-2)
                   """;

        var result = await _service.SummarizeCodeAsync(code, "python");

        result.Language.Should().Be("python");
        result.Description.Should().Contain("fibonacci");
        result.Functions.Should().Contain(f => f.Contains("fibonacci"));
    }

    [Fact]
    public async Task SummarizeCodeAsync_EmptyCode_ReturnsEmptySummary()
    {
        var result = await _service.SummarizeCodeAsync("", "csharp");

        result.Description.Should().Contain("Empty");
        result.Source.Should().Be("heuristic");
    }

    [Fact]
    public async Task SummarizeMermaidAsync_Flowchart_ReturnsParsedDescription()
    {
        var mermaid = """
                      graph TD
                          A[Start] --> B{Decision}
                          B -->|Yes| C[Process]
                          B -->|No| D[End]
                      """;

        var result = await _service.SummarizeMermaidAsync(mermaid);

        result.DiagramType.Should().Be("flowchart");
        result.Source.Should().Be("parser");
        result.Description.Should().Contain("Flowchart");
        result.Nodes.Should().NotBeEmpty();
        result.Edges.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SummarizeMermaidAsync_Empty_ReturnsEmpty()
    {
        var result = await _service.SummarizeMermaidAsync("");

        result.DiagramType.Should().Be("unknown");
        result.Description.Should().Contain("Empty");
    }

    [Fact]
    public void IsLanguageSupported_KnownLanguages_ReturnsTrue()
    {
        _service.IsLanguageSupported("csharp").Should().BeTrue();
        _service.IsLanguageSupported("cs").Should().BeTrue();
        _service.IsLanguageSupported("python").Should().BeTrue();
        _service.IsLanguageSupported("py").Should().BeTrue();
        _service.IsLanguageSupported("javascript").Should().BeTrue();
        _service.IsLanguageSupported("js").Should().BeTrue();
        _service.IsLanguageSupported("go").Should().BeTrue();
        _service.IsLanguageSupported("rust").Should().BeTrue();
    }

    [Fact]
    public void IsLanguageSupported_UnknownLanguage_ReturnsFalse()
    {
        _service.IsLanguageSupported("brainfuck").Should().BeFalse();
        _service.IsLanguageSupported("").Should().BeFalse();
    }

    [Fact]
    public void SummarizeCodeSync_ReturnsWithoutAsync()
    {
        var code = """
                   class Animal:
                       def __init__(self, name):
                           self.name = name

                       def speak(self):
                           pass
                   """;

        var result = _service.SummarizeCodeSync(code, "python");

        result.Language.Should().Be("python");
        result.Description.Should().NotBeNullOrWhiteSpace();
        result.Source.Should().BeOneOf("tree-sitter", "heuristic");
    }

    [Fact]
    public async Task SummarizeCodeAsync_NoLlm_UsesStructuralFallback()
    {
        // SentinelGenerate is null by default → structural only
        _service.SentinelGenerate.Should().BeNull();

        var code = "function hello() { console.log('hi'); }";
        var result = await _service.SummarizeCodeAsync(code, "javascript");

        result.Source.Should().BeOneOf("tree-sitter", "heuristic");
        result.Description.Should().Contain("hello");
    }
}