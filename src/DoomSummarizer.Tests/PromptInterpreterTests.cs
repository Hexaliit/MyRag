using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

public class PromptInterpreterTests
{
    /// <summary>
    ///     Create a PromptInterpreter with no LLM (forces fallback keyword-based interpretation).
    /// </summary>
    private static PromptInterpreter CreateInterpreter()
    {
        // OllamaService with non-existent host - will fail availability check and use fallback
        var ollama = new OllamaService(new OllamaConfig
        {
            BaseUrl = "http://localhost:59999",
            Model = "test"
        });
        return new PromptInterpreter(ollama);
    }

    [Fact]
    public async Task InterpretAsync_PharmaceuticalQuery_IncludesGoogleNews()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("new pharmaceutical news");

        // Should include Google News for non-tech topics
        result.Sources.Should().Contain(s => s.StartsWith("gnews"));
    }

    [Fact]
    public async Task InterpretAsync_PharmaceuticalQuery_IncludesBbcHealth()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("new pharmaceutical news");

        // Should route to BBC health, not BBC technology
        result.Sources.Should().Contain(s => s.Contains("bbc") && s.Contains("health"));
    }

    [Fact]
    public async Task InterpretAsync_HealthQuery_DetectsTopic()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("latest health news");

        result.Topics.Should().NotBeEmpty();
        result.Sources.Should().Contain(s => s.StartsWith("gnews"));
    }

    [Fact]
    public async Task InterpretAsync_TechQuery_IncludesHN()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("AI programming news");

        result.Sources.Should().Contain(s => s == "hn" || s.Contains("hn"));
    }

    [Fact]
    public async Task InterpretAsync_BusinessQuery_RoutesCorrectly()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("stock market investment news");

        result.Sources.Should().Contain(s => s.StartsWith("gnews"));
    }

    [Fact]
    public async Task InterpretAsync_DefaultQuery_HasSources()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("random stuff");

        result.Sources.Should().NotBeEmpty();
    }

    [Fact]
    public async Task InterpretAsync_ExplicitSourceBbc_IncludesBbc()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("what does bbc say");

        result.Sources.Should().Contain(s => s.StartsWith("bbc"));
    }

    [Fact]
    public async Task InterpretAsync_StopWords_Filtered()
    {
        var interpreter = CreateInterpreter();
        var result = await interpreter.InterpretAsync("show me the latest news");

        // "show", "me", "the", "latest", "news" are all stop words
        // Should still produce valid sources
        result.Sources.Should().NotBeEmpty();
    }
}