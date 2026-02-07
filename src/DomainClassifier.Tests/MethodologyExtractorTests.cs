using DomainClassifier.Technical.Extractors;

namespace DomainClassifier.Tests;

public class MethodologyExtractorTests
{
    [Fact]
    public void Extract_Algorithms_ReturnsEntities()
    {
        var text = "We use a transformer architecture with attention mechanism and gradient descent for training.";

        var entities = MethodologyExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "algorithm" && e.Text == "transformer");
        Assert.Contains(entities, e => e.EntityType == "algorithm" && e.Text == "attention mechanism");
        Assert.Contains(entities, e => e.EntityType == "algorithm" && e.Text == "gradient descent");
    }

    [Fact]
    public void Extract_Datasets_ReturnsEntities()
    {
        var text = "We evaluate on ImageNet and CIFAR-10 datasets. We also test on SQuAD.";

        var entities = MethodologyExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "dataset" && e.Text == "ImageNet");
        Assert.Contains(entities, e => e.EntityType == "dataset" && e.Text == "CIFAR-10");
        Assert.Contains(entities, e => e.EntityType == "dataset" && e.Text == "SQuAD");
    }

    [Fact]
    public void Extract_Frameworks_ReturnsEntities()
    {
        var text = "Our implementation uses PyTorch and Hugging Face transformers library.";

        var entities = MethodologyExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "framework" && e.Text == "PyTorch");
        Assert.Contains(entities, e => e.EntityType == "framework" && e.Text == "Hugging Face");
    }

    [Fact]
    public void Extract_StatisticalMethods_ReturnsEntities()
    {
        var text = "We use cross-validation with a t-test for statistical significance. " +
                   "Bayesian analysis confirms the results.";

        var entities = MethodologyExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "statistical_method" && e.Text == "cross-validation");
        Assert.Contains(entities, e => e.EntityType == "statistical_method" && e.Text == "t-test");
        Assert.Contains(entities, e => e.EntityType == "statistical_method" && e.Text == "Bayesian");
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(MethodologyExtractor.Extract(""));
    }

    [Fact]
    public void Extract_NoMethodologyTerms_ReturnsEmpty()
    {
        var entities = MethodologyExtractor.Extract("The cat sat on the mat in the sunny park.");
        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_NoDuplicates_ForRepeatedTerms()
    {
        var text = "We use PyTorch for training. The PyTorch framework is great. PyTorch again.";

        var entities = MethodologyExtractor.Extract(text);

        var pytorchEntities = entities.Where(e => e.Text == "PyTorch").ToList();
        Assert.Single(pytorchEntities);
    }
}
