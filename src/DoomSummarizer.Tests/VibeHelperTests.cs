using DoomSummarizer.Commands;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for vibe qualification, sentiment scoring, and custom vibe detection.
/// </summary>
public class VibeHelperTests
{
    [Theory]
    [InlineData("doom")]
    [InlineData("hopeful")]
    [InlineData("snarky")]
    [InlineData("neutral")]
    [InlineData("Doom")]
    [InlineData("HOPEFUL")]
    public void IsCustomVibe_ReturnsFalse_ForPredefinedVibes(string vibe)
    {
        ScrollCommand.IsCustomVibe(vibe).Should().BeFalse();
    }

    [Theory]
    [InlineData("excited about space")]
    [InlineData("cynical")]
    [InlineData("optimistic tech")]
    [InlineData("")]
    public void IsCustomVibe_ReturnsTrue_ForCustomVibes(string vibe)
    {
        ScrollCommand.IsCustomVibe(vibe).Should().BeTrue();
    }

    [Fact]
    public void QualifySearchQuery_PrependsDoomQualifier()
    {
        var result = ScrollCommand.QualifySearchQuery("AI safety", "doom");

        result.Should().Contain("AI safety");
        result.Should().Contain("concerning");
    }

    [Fact]
    public void QualifySearchQuery_PrependsHopefulQualifier()
    {
        var result = ScrollCommand.QualifySearchQuery("AI safety", "hopeful");

        result.Should().Contain("AI safety");
        result.Should().Contain("positive");
    }

    [Fact]
    public void QualifySearchQuery_NeutralReturnsQueryUnchanged()
    {
        var result = ScrollCommand.QualifySearchQuery("AI safety", "neutral");

        result.Should().Be("AI safety");
    }

    [Fact]
    public void QualifySearchQuery_CustomVibeUsesVibeText()
    {
        var result = ScrollCommand.QualifySearchQuery("AI safety", "excited about innovation");

        result.Should().Contain("AI safety");
        result.Should().Contain("excited about innovation");
    }

    [Fact]
    public void GetVibeRepresentativeText_DoomContainsNegativeTerms()
    {
        var text = ScrollCommand.GetVibeRepresentativeText("doom");

        text.Should().Contain("vulnerability");
        text.Should().Contain("crisis");
    }

    [Fact]
    public void GetVibeRepresentativeText_HopefulContainsPositiveTerms()
    {
        var text = ScrollCommand.GetVibeRepresentativeText("hopeful");

        text.Should().Contain("innovation");
        text.Should().Contain("breakthrough");
    }

    [Fact]
    public void GetVibeRepresentativeText_CustomVibeReturnsVibeDirectly()
    {
        var customVibe = "excited about quantum computing";
        var text = ScrollCommand.GetVibeRepresentativeText(customVibe);

        text.Should().Be(customVibe);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1, 2, 3 };
        var similarity = VectorMath.CosineSimilarity(v, v);

        similarity.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var v1 = new float[] { 1, 0, 0 };
        var v2 = new float[] { -1, 0, 0 };
        var similarity = VectorMath.CosineSimilarity(v1, v2);

        similarity.Should().BeApproximately(-1.0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var v1 = new float[] { 1, 0, 0 };
        var v2 = new float[] { 0, 1, 0 };
        var similarity = VectorMath.CosineSimilarity(v1, v2);

        similarity.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var v1 = new float[] { 1, 2, 3 };
        var v2 = new float[] { 1, 2 };
        var similarity = VectorMath.CosineSimilarity(v1, v2);

        similarity.Should().Be(0f);
    }

    [Fact]
    public void EmbeddingRoundTrip_ToAndFromBytes()
    {
        var original = new[] { 1.5f, -0.3f, 0.0f, 42.0f };
        var bytes = EmbeddingCompat.ToBytes(original);
        var restored = EmbeddingCompat.FromBytes(bytes);

        restored.Should().BeEquivalentTo(original);
    }
}