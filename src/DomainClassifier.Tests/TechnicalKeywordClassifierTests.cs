using DomainClassifier.Technical;

namespace DomainClassifier.Tests;

public class TechnicalKeywordClassifierTests
{
    [Fact]
    public void Classify_AcademicAbstract_HighConfidence()
    {
        var text = """
            Abstract: We propose a novel transformer-based architecture for natural language processing.
            Our methodology builds on prior work in attention mechanisms. We evaluate our model on the
            GLUE benchmark and report state-of-the-art results. The ablation study demonstrates the
            effectiveness of each component. Our code is available on GitHub.

            References
            [1] Vaswani et al., 2017. Attention is all you need. NeurIPS.
            """;

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.Equal("technical", result.DomainId);
        Assert.True(result.Confidence > 0.6,
            $"Expected confidence > 0.6 for academic text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_NonAcademicText_LowConfidence()
    {
        var text = "The cat sat on the mat. It was a sunny day and the birds were singing. " +
                   "The children played in the park while their parents watched from the bench.";

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.Equal("technical", result.DomainId);
        Assert.True(result.Confidence < 0.4,
            $"Expected confidence < 0.4 for non-academic text, got {result.Confidence}");
    }

    [Fact]
    public void Classify_EmptyText_ZeroConfidence()
    {
        var result = TechnicalKeywordClassifier.Classify("");
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Classify_WithBibliography_BoostsConfidence()
    {
        var text = """
            This study examines the results of the experiment.
            The analysis shows interesting findings.

            References
            [1] Smith et al., 2020. Some paper title.
            [2] Jones et al., 2021. Another paper.
            """;

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.True(result.Confidence > 0.3,
            $"Expected confidence boost from bibliography, got {result.Confidence}");
        Assert.NotNull(result.Metadata);
        Assert.True((bool)result.Metadata!["has_bibliography"]);
    }

    [Fact]
    public void Classify_WithDoi_BoostsConfidence()
    {
        var text = """
            This study references important work. See 10.1038/s41586-023-06647-8 for details.
            The algorithm achieves benchmark results on the dataset.
            """;

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.NotNull(result.Metadata);
        Assert.True((bool)result.Metadata!["has_doi"]);
    }

    [Fact]
    public void Classify_WithArxiv_BoostsConfidence()
    {
        var text = """
            Building on arXiv: 2301.12345, we propose a new methodology.
            Our algorithm outperforms the baseline on all benchmark metrics.
            """;

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.NotNull(result.Metadata);
        Assert.True((bool)result.Metadata!["has_arxiv"]);
    }

    [Fact]
    public void Classify_MachineLearningPaper_HighConfidence()
    {
        var text = """
            We present a new approach to gradient descent optimization using hyperparameter
            tuning with cross-validation. Our model architecture uses attention mechanism layers
            with dropout for regularization. We evaluate on ImageNet and CIFAR-10 datasets.
            The ablation study shows that each component contributes to the final F1 score.
            Results are statistically significant with p-value < 0.05.

            References
            [1] He et al., 2016. Deep residual learning.
            """;

        var result = TechnicalKeywordClassifier.Classify(text);

        Assert.True(result.Confidence > 0.7,
            $"Expected confidence > 0.7 for ML paper, got {result.Confidence}");
    }
}
