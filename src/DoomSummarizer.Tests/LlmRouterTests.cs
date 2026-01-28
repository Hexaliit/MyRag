using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for LlmRouter static helper methods:
/// InferContextSize (model name → context window mapping).
/// </summary>
public class LlmRouterTests
{
    #region InferContextSize

    [Theory]
    [InlineData("gpt-4o", 128000)]
    [InlineData("gpt-4o-mini", 128000)]
    [InlineData("gpt-4-turbo", 128000)]
    [InlineData("gpt-3.5-turbo", 16385)]
    [InlineData("claude-sonnet-4-20250514", 200000)]
    [InlineData("claude-3-5-sonnet-latest", 200000)]
    [InlineData("claude-3-5-haiku-latest", 200000)]
    [InlineData("claude-opus-4-20250514", 200000)]
    public void InferContextSize_KnownModels_ReturnsExact(string model, int expected)
    {
        LlmRouter.InferContextSize(model).Should().Be(expected);
    }

    [Theory]
    [InlineData("gpt-4-custom-finetune", 128000)]
    [InlineData("gpt-4.1-turbo", 128000)]
    public void InferContextSize_Gpt4Prefix_Returns128K(string model, int expected)
    {
        LlmRouter.InferContextSize(model).Should().Be(expected);
    }

    [Theory]
    [InlineData("claude-4-preview", 200000)]
    [InlineData("claude-unknown-model", 200000)]
    public void InferContextSize_ClaudePrefix_Returns200K(string model, int expected)
    {
        LlmRouter.InferContextSize(model).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void InferContextSize_NullOrEmpty_ReturnsZero(string? model)
    {
        LlmRouter.InferContextSize(model).Should().Be(0);
    }

    [Theory]
    [InlineData("llama3:8b")]
    [InlineData("mixtral:8x7b")]
    [InlineData("unknown-model")]
    public void InferContextSize_UnknownModel_ReturnsZero(string model)
    {
        LlmRouter.InferContextSize(model).Should().Be(0);
    }

    [Fact]
    public void InferContextSize_CaseInsensitive()
    {
        LlmRouter.InferContextSize("GPT-4o").Should().Be(128000);
        LlmRouter.InferContextSize("Claude-3-5-Sonnet-Latest").Should().Be(200000);
    }

    #endregion
}
