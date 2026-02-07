using DomainClassifier.Technical;
using DomainClassifier.Technical.Extractors;
using LucidRAG.Services;

namespace DomainClassifier.Tests;

/// <summary>
///     Integration tests using realistic academic paper text to verify all extractors
///     work together end-to-end on real-world content.
/// </summary>
public class LivePaperIntegrationTests
{
    // Realistic ML paper text with all the patterns we want to extract
    private const string RealisticPaperText = """
        Abstract

        We propose a novel approach to retrieval-augmented generation (RAG) using a combination of
        dense passage retrieval and knowledge graph-enhanced reasoning. Our methodology builds on the
        transformer architecture introduced by Vaswani et al. (2017) and extends it with a graph
        attention mechanism for structured knowledge integration.

        We evaluate our model on multiple benchmarks including SQuAD, Natural Questions, and TriviaQA,
        achieving state-of-the-art results. The ablation study demonstrates that each component
        contributes significantly to the final F1 score (p-value < 0.01). Our approach uses PyTorch
        and the Hugging Face transformers library for implementation.

        The model employs BERT embeddings [1] with a novel cross-attention layer [2,3] that allows
        bidirectional information flow between the retrieval and generation components. We apply
        dropout regularization and gradient descent optimization with the AdamW optimizer.

        Experiments show that our method outperforms existing baselines including DPR (Karpukhin et al., 2020)
        and RAG (Lewis et al., 2020) on all benchmark datasets. The results are statistically significant
        using a t-test with cross-validation.

        For reproducibility, our code is available at https://github.com/example/graphrag-project.
        The pre-trained models can be downloaded from https://huggingface.co/example/graphrag.
        See also the related work at arXiv: 2312.10997 and 10.1145/3539618.3591960 for background.

        References
        [1] Devlin, J., Chang, M., Lee, K., & Toutanova, K. (2019). BERT: Pre-training of Deep Bidirectional Transformers. arXiv: 1810.04805.
        [2] Vaswani, A., Shazeer, N., Parmar, N. et al. (2017). Attention Is All You Need. NeurIPS. 10.48550/arXiv.1706.03762.
        [3] Lewis, P., Perez, E., Piktus, A. et al. (2020). Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks. arXiv: 2005.11401.
        [4] Karpukhin, V. et al. (2020). Dense Passage Retrieval for Open-Domain Question Answering. 10.18653/v1/2020.emnlp-main.550.
        [5] Brown, T. et al. (2020). Language Models are Few-Shot Learners. arXiv: 2005.14165.
        """;

    [Fact]
    public void TechnicalClassifier_RealPaper_HighConfidence()
    {
        var result = TechnicalKeywordClassifier.Classify(RealisticPaperText);

        Assert.Equal("technical", result.DomainId);
        Assert.True(result.Confidence > 0.6,
            $"Expected confidence > 0.6 for real ML paper, got {result.Confidence}");

        // Should detect structural signals
        Assert.NotNull(result.Metadata);
        Assert.True((bool)result.Metadata!["has_bibliography"], "Should detect bibliography section");
        Assert.True((int)result.Metadata!["citation_count"] > 5, "Should find inline citations");
    }

    [Fact]
    public void CitationExtractor_RealPaper_FindsAllCitationTypes()
    {
        var entities = CitationExtractor.Extract(RealisticPaperText);

        // Should find numbered citations: [1], [2,3]
        var numbered = entities.Where(e => e.EntityType == "citation_numbered").ToList();
        Assert.True(numbered.Count >= 3,
            $"Expected >= 3 numbered citations, got {numbered.Count}");

        // Should find author-year citations: (Karpukhin et al., 2020), (Lewis et al., 2020), Vaswani et al. (2017)
        var authorYear = entities.Where(e => e.EntityType == "citation_author_year").ToList();
        Assert.True(authorYear.Count >= 2,
            $"Expected >= 2 author-year citations, got {authorYear.Count}");

        // Check specific citations
        Assert.Contains(numbered, e => e.Metadata!["citation_key"]?.ToString() == "1");
        Assert.Contains(authorYear, e => e.Metadata!["author"]?.ToString()?.Contains("Lewis") == true);
    }

    [Fact]
    public void BibliographyExtractor_RealPaper_ParsesAllEntries()
    {
        var entities = BibliographyExtractor.Extract(RealisticPaperText);

        Assert.True(entities.Count >= 5,
            $"Expected >= 5 bibliography entries, got {entities.Count}");
        Assert.All(entities, e => Assert.Equal("bibliography_entry", e.EntityType));

        // Check that years are extracted
        var withYear = entities.Where(e => e.Metadata!.ContainsKey("year")).ToList();
        Assert.True(withYear.Count >= 4,
            $"Expected >= 4 entries with years, got {withYear.Count}");

        // Check that arXiv IDs are extracted from bibliography
        var withArxiv = entities.Where(e => e.Metadata!.ContainsKey("arxiv_id")).ToList();
        Assert.True(withArxiv.Count >= 2,
            $"Expected >= 2 entries with arXiv IDs, got {withArxiv.Count}");

        // Check that DOIs are extracted from bibliography
        var withDoi = entities.Where(e => e.Metadata!.ContainsKey("doi")).ToList();
        Assert.True(withDoi.Count >= 1,
            $"Expected >= 1 entries with DOI, got {withDoi.Count}");
    }

    [Fact]
    public void MethodologyExtractor_RealPaper_FindsTerms()
    {
        var entities = MethodologyExtractor.Extract(RealisticPaperText);

        // Algorithms: BERT, transformer, attention mechanism, gradient descent, dropout, AdamW
        var algorithms = entities.Where(e => e.EntityType == "algorithm").ToList();
        Assert.True(algorithms.Count >= 4,
            $"Expected >= 4 algorithms, got {algorithms.Count}: {string.Join(", ", algorithms.Select(a => a.Text))}");
        Assert.Contains(algorithms, e => e.Text == "BERT");
        Assert.Contains(algorithms, e => e.Text == "transformer");

        // Datasets: SQuAD, TriviaQA, Natural Questions
        var datasets = entities.Where(e => e.EntityType == "dataset").ToList();
        Assert.True(datasets.Count >= 2,
            $"Expected >= 2 datasets, got {datasets.Count}: {string.Join(", ", datasets.Select(d => d.Text))}");

        // Frameworks: PyTorch, Hugging Face
        var frameworks = entities.Where(e => e.EntityType == "framework").ToList();
        Assert.True(frameworks.Count >= 2,
            $"Expected >= 2 frameworks, got {frameworks.Count}: {string.Join(", ", frameworks.Select(f => f.Text))}");

        // Statistical methods: t-test, cross-validation
        var stats = entities.Where(e => e.EntityType == "statistical_method").ToList();
        Assert.True(stats.Count >= 2,
            $"Expected >= 2 statistical methods, got {stats.Count}: {string.Join(", ", stats.Select(s => s.Text))}");
    }

    [Fact]
    public void LinkGraphExtractor_RealPaper_FindsAllLinkTypes()
    {
        var links = LinkGraphExtractor.Extract(RealisticPaperText);

        // Should find URLs
        var urls = links.Where(l => l.Type == LinkType.Url).ToList();
        Assert.True(urls.Count >= 2,
            $"Expected >= 2 URLs, got {urls.Count}: {string.Join(", ", urls.Select(u => u.Value))}");

        // Should find DOIs
        var dois = links.Where(l => l.Type == LinkType.Doi).ToList();
        Assert.True(dois.Count >= 1,
            $"Expected >= 1 DOI, got {dois.Count}: {string.Join(", ", dois.Select(d => d.Value))}");

        // Should find arXiv IDs
        var arxivs = links.Where(l => l.Type == LinkType.ArxivId).ToList();
        Assert.True(arxivs.Count >= 1,
            $"Expected >= 1 arXiv ID, got {arxivs.Count}: {string.Join(", ", arxivs.Select(a => a.Value))}");

        // No duplicates
        var uniqueValues = links.Select(l => l.Value).Distinct().Count();
        Assert.Equal(links.Count, uniqueValues);
    }

    [Fact]
    public void AllExtractors_CombinedStats_ShowRichExtraction()
    {
        // This test verifies that running ALL extractors together on a real paper
        // produces a comprehensive extraction result
        var classifier = TechnicalKeywordClassifier.Classify(RealisticPaperText);
        var citations = CitationExtractor.Extract(RealisticPaperText);
        var bibliography = BibliographyExtractor.Extract(RealisticPaperText);
        var methodology = MethodologyExtractor.Extract(RealisticPaperText);
        var links = LinkGraphExtractor.Extract(RealisticPaperText);

        var totalEntities = citations.Count + bibliography.Count + methodology.Count;
        var totalLinks = links.Count;

        // Should extract a meaningful number of entities
        Assert.True(totalEntities >= 15,
            $"Expected >= 15 total entities, got {totalEntities}");

        // Should find multiple link types
        Assert.True(totalLinks >= 4,
            $"Expected >= 4 links, got {totalLinks}");

        // Classifier should be confident
        Assert.True(classifier.Confidence > 0.5,
            $"Expected confidence > 0.5, got {classifier.Confidence}");

        // Should detect bibliography
        Assert.True(BibliographyExtractor.HasBibliography(RealisticPaperText));
    }
}
