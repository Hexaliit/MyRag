using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for DocumentProfileService keyword extraction with structural weighting.
/// </summary>
public class DocumentProfileServiceTests
{
    #region ExtractProfile

    [Fact]
    public void ExtractProfile_TitleKeywords_RankedHighest()
    {
        var profile = DocumentProfileService.ExtractProfile(
            "Machine Learning Advances",
            "This article discusses various topics including science and politics.");

        // Title terms should appear in top keywords
        profile.TopKeywords.Should().Contain(k =>
            k.Keyword.Equals("machine", StringComparison.OrdinalIgnoreCase));
        profile.TopKeywords.Should().Contain(k =>
            k.Keyword.Equals("learning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractProfile_HeadingTerms_WeightedAboveBody()
    {
        var content = """
                      # Introduction to Neural Networks

                      Neural networks are computational models inspired by biological systems.
                      They consist of layers of interconnected nodes that process information.

                      ## Training Methods

                      Training involves adjusting weights through backpropagation.
                      The process requires large datasets and computational resources.
                      """;

        var profile = DocumentProfileService.ExtractProfile("AI Guide", content);

        // "neural" appears in heading and body — should rank higher than body-only terms
        var neuralEntry = profile.TopKeywords.FirstOrDefault(k =>
            k.Keyword.Equals("neural", StringComparison.OrdinalIgnoreCase));
        var computationalEntry = profile.TopKeywords.FirstOrDefault(k =>
            k.Keyword.Equals("computational", StringComparison.OrdinalIgnoreCase));

        neuralEntry.Should().NotBeNull();
        if (computationalEntry != null)
            neuralEntry!.Weight.Should().BeGreaterThan(computationalEntry.Weight);
    }

    [Fact]
    public void ExtractProfile_ReturnsKeywordsText()
    {
        var profile = DocumentProfileService.ExtractProfile(
            "Climate Change Impact",
            "Global warming affects weather patterns worldwide. Rising temperatures cause ice to melt.");

        profile.KeywordsText.Should().NotBeNullOrWhiteSpace();
        // Should contain title-derived terms
        profile.KeywordsText.Should().ContainAny("climate", "change", "impact");
    }

    [Fact]
    public void ExtractProfile_EmptyContent_StillExtractsTitleKeywords()
    {
        var profile = DocumentProfileService.ExtractProfile("Quantum Computing Research", "");

        profile.TopKeywords.Should().NotBeEmpty();
        profile.TopKeywords.Should().Contain(k =>
            k.Keyword.Equals("quantum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractProfile_BigramsExtracted_WhenRepeated()
    {
        var content = string.Join("\n\n",
            Enumerable.Repeat(
                "Machine learning is transforming how we process data. Machine learning models are everywhere.", 3));

        var profile = DocumentProfileService.ExtractProfile("Tech", content);

        // "machine learning" bigram should appear (it occurs 6+ times)
        profile.TopBigrams.Should().Contain(b =>
            b.ngram.Contains("machine", StringComparison.OrdinalIgnoreCase) &&
            b.ngram.Contains("learning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractProfile_LimitsKeywordCount()
    {
        // Create content with many unique words
        var words = Enumerable.Range(0, 100).Select(i => $"word{i}");
        var content = string.Join(" ", words);

        var profile = DocumentProfileService.ExtractProfile("Test Title", content);

        // Should cap at 20 unigrams
        profile.TopKeywords.Should().HaveCountLessThanOrEqualTo(20);
        // Should cap at 10 bigrams
        profile.TopBigrams.Should().HaveCountLessThanOrEqualTo(10);
    }

    #endregion

    #region ContentTypeClassification

    [Theory]
    [InlineData("About Me", "My experience building systems...", "https://example.com/aboutme")]
    [InlineData("About the Author", "I've worked as a developer...", "https://example.com/about")]
    [InlineData("My Background", "Years of experience in tech...", "https://blog.com/bio")]
    public void ClassifyContentWeight_BioContent_ReturnsLowWeight(string title, string content, string url)
    {
        var weight = EvidencePreparationService.ClassifyContentWeight(title, content, url);
        weight.Should().BeLessThan(0.5f, "bio content should be heavily downweighted");
    }

    [Theory]
    [InlineData("HTMX Pagination Guide", "Using hx-get to fetch pages...", "https://example.com/blog/htmx-pagination")]
    [InlineData("ASP.NET Core Tutorial", "Configure services in Startup.cs...", "https://example.com/aspnet")]
    [InlineData("Building APIs", "REST endpoints with controllers...", "https://blog.com/api-guide")]
    public void ClassifyContentWeight_TechnicalContent_ReturnsFullWeight(string title, string content, string url)
    {
        var weight = EvidencePreparationService.ClassifyContentWeight(title, content, url);
        weight.Should().BeGreaterThanOrEqualTo(0.9f, "technical content should get full weight");
    }

    [Fact]
    public void ClassifyContentWeight_BioUrl_ReturnsLowWeight_EvenWithTechTitle()
    {
        var weight = EvidencePreparationService.ClassifyContentWeight(
            "Technical Projects",
            "My projects include building APIs...",
            "https://example.com/about/me");

        weight.Should().BeLessThan(0.5f, "URL with /about should trigger bio detection");
    }

    [Fact]
    public void ClassifyContentWeight_MultipleBioIndicators_ReturnsLowWeight()
    {
        var bioContent = """
                         As Head of Engineering, I have worked on many projects.
                         My experience includes leading teams and scaling systems.
                         I've built platforms for millions of users.
                         """;

        var weight = EvidencePreparationService.ClassifyContentWeight(
            "Projects",
            bioContent,
            "https://example.com/projects");

        weight.Should().BeLessThanOrEqualTo(0.3f, "multiple bio indicators should trigger heavy downweight");
    }

    #endregion
}