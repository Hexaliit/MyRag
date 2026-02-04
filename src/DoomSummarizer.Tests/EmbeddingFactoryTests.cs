using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Config;

namespace DoomSummarizer.Tests;

public class EmbeddingFactoryTests
{
    // ── ParseModel ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null, OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("", OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("  ", OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("all-MiniLM-L6-v2", OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("ALL-MINILM-L6-V2", OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("minilm", OnnxEmbeddingModel.AllMiniLmL6V2)]
    [InlineData("bge-small-en-v1.5", OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData("bge-small", OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData("bge", OnnxEmbeddingModel.BgeSmallEnV15)]
    [InlineData("gte-small", OnnxEmbeddingModel.GteSmall)]
    [InlineData("gte", OnnxEmbeddingModel.GteSmall)]
    [InlineData("multi-qa-MiniLM-L6-cos-v1", OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData("multi-qa", OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData("multiqa", OnnxEmbeddingModel.MultiQaMiniLm)]
    [InlineData("paraphrase-MiniLM-L3-v2", OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    [InlineData("paraphrase", OnnxEmbeddingModel.ParaphraseMiniLmL3)]
    [InlineData("unknown-model-xyz", OnnxEmbeddingModel.AllMiniLmL6V2)] // Fallback
    public void ParseModel_ResolvesCorrectly(string? input, OnnxEmbeddingModel expected)
    {
        var result = EmbeddingFactory.ParseModel(input);

        Assert.Equal(expected, result);
    }
}
