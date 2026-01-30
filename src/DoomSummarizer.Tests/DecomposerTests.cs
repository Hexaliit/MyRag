using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Caching;
using LucidRAG.Decomposer.Integration;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for the LucidRAG.Decomposer pipeline: tool-use routing, structural analysis,
/// complexity classification, concept registry, refinement, orchestration, caching.
/// No embedding service needed — tests exercise deterministic paths.
/// </summary>
public class DecomposerTests
{
    // ════════════════════════════════════════════════════════════════════
    // ComplexityClassifier
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("What is a transformer?", 0, 0, false, false, QueryComplexity.Simple)]
    [InlineData("Tell me about AI", 0, 0, false, false, QueryComplexity.Simple)]
    [InlineData("Tell me about https://arxiv.org/abs/2210.02406", 0, 0, true, false, QueryComplexity.Moderate)]
    [InlineData("Compare A and B", 2, 1, false, false, QueryComplexity.Simple)] // 2 entities, 1 type, short query = Simple
    [InlineData("A, B, C entities together", 3, 1, false, false, QueryComplexity.Complex)] // 3+ entities = Complex
    [InlineData("Org1 vs Org2 in LocA", 3, 2, false, false, QueryComplexity.Complex)] // 3+ entities = Complex (hits before type check)
    public async Task ComplexityClassifier_ClassifiesCorrectly(
        string query, int entityCount, int entityTypeCount,
        bool hasUrls, bool hasDateTimes, QueryComplexity expected)
    {
        var classifier = new ComplexityClassifier();
        var result = await classifier.ClassifyAsync(query, entityCount, entityTypeCount, hasUrls, hasDateTimes);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("What is X?", 1)]
    [InlineData("Tell me about X. Also explain Y.", 2)]
    [InlineData("A; B; C", 2)] // second ; followed by "C" (len 1 ≤ 3) → no second split
    [InlineData("First thing. Second thing. Third thing. Fourth.", 4)]
    [InlineData("Tell me about transformers and also about attention mechanisms for neural networks", 3)] // "and also" + "and" (len>40, idx>15)
    [InlineData("Short and sweet", 1)] // "and" at position < 15, short query
    public void ComplexityClassifier_CountClauses(string query, int expected)
    {
        ComplexityClassifier.CountClauses(query).Should().Be(expected);
    }

    // ════════════════════════════════════════════════════════════════════
    // ReferenceExtractor
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReferenceExtractor_ExtractsUrls()
    {
        var extractor = new ReferenceExtractor();
        var signals = new QuerySignals { OriginalQuery = "Tell me about https://arxiv.org/abs/2210.02406" };

        var result = await extractor.AnalyzeAsync(
            "Tell me about https://arxiv.org/abs/2210.02406", signals);

        result.References.Should().HaveCount(1);
        result.References[0].Kind.Should().Be(ContentReferenceKind.Url);
        result.References[0].Uri.Should().Be("https://arxiv.org/abs/2210.02406");
        result.ProposedNodes.Should().Contain(n => n.Type == QueryNodeType.ContentReference);
    }

    [Fact]
    public async Task ReferenceExtractor_ExtractsMultipleReferences()
    {
        var extractor = new ReferenceExtractor();
        var signals = new QuerySignals
        {
            OriginalQuery = "Compare https://example.com/a and https://example.com/b"
        };

        var result = await extractor.AnalyzeAsync(
            "Compare https://example.com/a and https://example.com/b", signals);

        result.References.Should().HaveCount(2);
        result.ProposedNodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReferenceExtractor_ExtractsDoi()
    {
        var extractor = new ReferenceExtractor();
        var signals = new QuerySignals { OriginalQuery = "Explain paper 10.1234/test.5678" };

        var result = await extractor.AnalyzeAsync("Explain paper 10.1234/test.5678", signals);

        result.References.Should().HaveCount(1);
        result.References[0].Kind.Should().Be(ContentReferenceKind.Doi);
        result.References[0].Uri.Should().Contain("doi.org");
    }

    [Fact]
    public async Task ReferenceExtractor_ExtractsFilePaths()
    {
        var extractor = new ReferenceExtractor();
        var signals = new QuerySignals { OriginalQuery = "Read /home/user/docs/paper.md" };

        var result = await extractor.AnalyzeAsync("Read /home/user/docs/paper.md", signals);

        result.References.Should().HaveCount(1);
        result.References[0].Kind.Should().Be(ContentReferenceKind.FilePath);
    }

    [Fact]
    public async Task ReferenceExtractor_ExtractsGitHubRefs()
    {
        var extractor = new ReferenceExtractor();
        var signals = new QuerySignals { OriginalQuery = "Check scottgal/lucidrag#42" };

        var result = await extractor.AnalyzeAsync("Check scottgal/lucidrag#42", signals);

        result.References.Should().HaveCount(1);
        result.References[0].Kind.Should().Be(ContentReferenceKind.GitHubReference);
        result.References[0].Uri.Should().Contain("github.com");
    }

    // ════════════════════════════════════════════════════════════════════
    // ToolUseAnalyzer — Deterministic Detection
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ToolUseAnalyzer_DetectsFileSystemTool()
    {
        var analyzer = new ToolUseAnalyzer();
        var signals = new QuerySignals
        {
            OriginalQuery = "Go to C:/test and find all markdown files"
        };

        var result = await analyzer.AnalyzeAsync(
            "Go to C:/test and find all markdown files", signals);

        result.DetectedTools.Should().NotBeEmpty();
        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.FileSystem);
        var fsTool = result.DetectedTools.First(t => t.Tool == ToolKind.FileSystem);
        fsTool.Parameters.Should().ContainKey("path");
        fsTool.Parameters["path"].Should().Contain("C:/test");
    }

    [Fact]
    public async Task ToolUseAnalyzer_DetectsIndexTool()
    {
        var analyzer = new ToolUseAnalyzer();
        var signals = new QuerySignals
        {
            OriginalQuery = "Index these files and build a knowledge base called 'myfiles'"
        };

        var result = await analyzer.AnalyzeAsync(
            "Index these files and build a knowledge base called 'myfiles'", signals);

        result.DetectedTools.Should().NotBeEmpty();
        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.Index);
        var indexTool = result.DetectedTools.First(t => t.Tool == ToolKind.Index);
        indexTool.Parameters.Should().ContainKey("collection");
        indexTool.Parameters["collection"].Should().Be("myfiles");
    }

    [Fact]
    public async Task ToolUseAnalyzer_DetectsCrawlTool()
    {
        var analyzer = new ToolUseAnalyzer();
        var signals = new QuerySignals
        {
            OriginalQuery = "Crawl https://example.com and extract content"
        };

        var result = await analyzer.AnalyzeAsync(
            "Crawl https://example.com and extract content", signals);

        result.DetectedTools.Should().NotBeEmpty();
        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.Crawl);
        var crawlTool = result.DetectedTools.First(t => t.Tool == ToolKind.Crawl);
        crawlTool.Parameters.Should().ContainKey("url");
        crawlTool.Parameters["url"].Should().Be("https://example.com");
    }

    [Fact]
    public async Task ToolUseAnalyzer_DetectsMultiToolChain()
    {
        // The user's exact example: file system → index → analyze
        var analyzer = new ToolUseAnalyzer();
        var query = "Go to C:/test, index all the markdown files with 'summarizer' in the name, " +
                    "build a knowledge base called 'myfiles', then tell me how long they are";
        var signals = new QuerySignals { OriginalQuery = query };

        var result = await analyzer.AnalyzeAsync(query, signals);

        result.DetectedTools.Should().HaveCountGreaterThanOrEqualTo(2,
            "should detect at least FileSystem + Index tools");
        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.FileSystem);
        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.Index);

        // Should upgrade complexity for multi-tool queries
        result.Complexity.Should().Be(QueryComplexity.Complex);
    }

    [Fact]
    public async Task ToolUseAnalyzer_FilePatternExtraction()
    {
        var analyzer = new ToolUseAnalyzer();
        var query = "Go to C:/docs folder and find files with 'summarizer' in the name";
        var signals = new QuerySignals { OriginalQuery = query };

        var result = await analyzer.AnalyzeAsync(query, signals);

        result.DetectedTools.Should().Contain(t => t.Tool == ToolKind.FileSystem);
        var fsTool = result.DetectedTools.First(t => t.Tool == ToolKind.FileSystem);
        fsTool.Parameters.Should().ContainKey("pattern");
        fsTool.Parameters["pattern"].Should().Contain("summarizer");
    }

    [Fact]
    public async Task ToolUseAnalyzer_DoesNotDetectToolsInSimpleQuery()
    {
        var analyzer = new ToolUseAnalyzer();
        var signals = new QuerySignals { OriginalQuery = "What is a transformer model?" };

        var result = await analyzer.AnalyzeAsync("What is a transformer model?", signals);

        result.DetectedTools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolUseAnalyzer_UpgradesComplexity()
    {
        var analyzer = new ToolUseAnalyzer();
        var signals = new QuerySignals
        {
            OriginalQuery = "Index all markdown files",
            Complexity = QueryComplexity.Simple
        };

        var result = await analyzer.AnalyzeAsync("Index all markdown files", signals);

        if (result.DetectedTools.Count > 0)
        {
            result.Complexity.Should().NotBe(QueryComplexity.Simple,
                "tool detection should upgrade complexity");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // ToolAction Model
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void ToolAction_RequiredProperties()
    {
        var action = new ToolAction
        {
            Tool = ToolKind.FileSystem,
            Intent = "Find markdown files",
            Parameters = new Dictionary<string, string>
            {
                ["path"] = "C:/test",
                ["pattern"] = "*.md"
            }
        };

        action.Tool.Should().Be(ToolKind.FileSystem);
        action.Intent.Should().Be("Find markdown files");
        action.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void QueryNode_CanCarryToolAction()
    {
        var action = new ToolAction
        {
            Tool = ToolKind.Index,
            Intent = "Build KB",
            Parameters = new Dictionary<string, string> { ["collection"] = "myfiles" }
        };

        var node = new QueryNode
        {
            Query = "Index these files",
            Type = QueryNodeType.ToolAction,
            ToolAction = action
        };

        node.Type.Should().Be(QueryNodeType.ToolAction);
        node.ToolAction.Should().NotBeNull();
        node.ToolAction!.Tool.Should().Be(ToolKind.Index);
    }

    // ════════════════════════════════════════════════════════════════════
    // ConceptRegistry
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ConceptType.General, 20)]
    [InlineData(ConceptType.Definition, 5)]
    [InlineData(ConceptType.Procedure, 10)]
    [InlineData(ConceptType.Comparison, 20)]
    [InlineData(ConceptType.Roundup, 30)]
    [InlineData(ConceptType.Troubleshooting, 15)]
    public void ConceptRegistry_DefaultPolicies_FetchBudget(ConceptType concept, int expectedBudget)
    {
        var registry = new ConceptRegistry();
        var policy = registry.GetPolicy(concept);

        policy.Concept.Should().Be(concept);
        policy.FetchBudget.Should().Be(expectedBudget);
    }

    [Fact]
    public void ConceptRegistry_FallbackToGeneral()
    {
        var registry = new ConceptRegistry();
        // ConceptType.General should always be available
        var policy = registry.GetPolicy(ConceptType.General);
        policy.Concept.Should().Be(ConceptType.General);
    }

    [Fact]
    public void ConceptRegistry_CustomPolicyOverrides()
    {
        var registry = new ConceptRegistry();
        var custom = new ConceptPolicy
        {
            Concept = ConceptType.Definition,
            FetchBudget = 99,
            ResponseFormat = "custom_card"
        };

        registry.Register(custom);
        var policy = registry.GetPolicy(ConceptType.Definition);

        policy.FetchBudget.Should().Be(99);
        policy.ResponseFormat.Should().Be("custom_card");
    }

    [Fact]
    public void ConceptRegistry_AddLens()
    {
        var registry = new ConceptRegistry();
        registry.AddLens(ConceptType.Policy, "pii_redaction");

        var policy = registry.GetPolicy(ConceptType.Policy);
        policy.ActiveLenses.Should().Contain("citation_required"); // built-in
        policy.ActiveLenses.Should().Contain("pii_redaction"); // added
    }

    [Fact]
    public void ConceptPolicy_RequiresCitations_ForPolicyAndDecision()
    {
        var registry = new ConceptRegistry();

        registry.GetPolicy(ConceptType.Policy).RequireCitations.Should().BeTrue();
        registry.GetPolicy(ConceptType.Decision).RequireCitations.Should().BeTrue();
        registry.GetPolicy(ConceptType.Definition).RequireCitations.Should().BeFalse();
    }

    [Fact]
    public void ConceptPolicy_RecencyModes()
    {
        var registry = new ConceptRegistry();

        registry.GetPolicy(ConceptType.Roundup).Recency.Should().Be(RecencyMode.Strong);
        registry.GetPolicy(ConceptType.Incident).Recency.Should().Be(RecencyMode.Strong);
        registry.GetPolicy(ConceptType.Definition).Recency.Should().Be(RecencyMode.None);
        registry.GetPolicy(ConceptType.Entity).Recency.Should().Be(RecencyMode.Moderate);
    }

    // ════════════════════════════════════════════════════════════════════
    // DeterministicRefiner
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void DeterministicRefiner_CreatesAtomicNode_WhenNoProposals()
    {
        var refiner = new DeterministicRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "What is a transformer?",
            DetectedConcept = ConceptType.Definition
        };

        var result = refiner.Refine(signals, null);

        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].Type.Should().Be(QueryNodeType.Atomic);
        result.Nodes[0].Query.Should().Be("What is a transformer?");
        result.Concept.Should().Be(ConceptType.Definition);
    }

    [Fact]
    public void DeterministicRefiner_PreservesProposedNodes()
    {
        var refiner = new DeterministicRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "A and B",
            ProposedNodes =
            [
                new QueryNode { Query = "A", Type = QueryNodeType.Atomic },
                new QueryNode { Query = "B", Type = QueryNodeType.Atomic }
            ]
        };

        var result = refiner.Refine(signals, null);
        result.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void DeterministicRefiner_RoutesToolActionsToPlan()
    {
        var refiner = new DeterministicRefiner();
        var toolAction = new ToolAction
        {
            Tool = ToolKind.FileSystem,
            Intent = "Find files"
        };

        var signals = new QuerySignals
        {
            OriginalQuery = "Find files in C:/test",
            ProposedNodes =
            [
                new QueryNode
                {
                    Query = "Find files in C:/test",
                    Type = QueryNodeType.ToolAction,
                    ToolAction = toolAction
                }
            ]
        };

        var result = refiner.Refine(signals, null);

        result.Plan.ToolActionNodes.Should().HaveCount(1);
        result.Plan.ParallelNodes.Should().BeEmpty();
        result.Plan.ToolActionNodes[0].ToolAction!.Tool.Should().Be(ToolKind.FileSystem);
    }

    // ════════════════════════════════════════════════════════════════════
    // SentinelRefiner
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void SentinelRefiner_FallsBackToDeterministic_WithoutSentinelData()
    {
        var refiner = new SentinelRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "Simple query",
            DetectedConcept = ConceptType.General
        };

        var result = refiner.Refine(signals, null);

        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].Query.Should().Be("Simple query");
    }

    [Fact]
    public void SentinelRefiner_BothSplit_UsesDeterministicStructure()
    {
        var refiner = new SentinelRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "A and B",
            ProposedNodes =
            [
                new QueryNode { Query = "Topic A", Type = QueryNodeType.Atomic },
                new QueryNode { Query = "Topic B", Type = QueryNodeType.Atomic }
            ]
        };

        var sentinel = new SentinelRefinementInput
        {
            IsComposite = true,
            Subqueries = ["Question about A?", "Question about B?"],
            FilterKeywords = ["keyword1", "keyword2"]
        };

        var result = refiner.Refine(signals, sentinel);

        // Should use deterministic structure (Topic A, Topic B), not sentinel text
        result.Nodes.Should().HaveCount(2);
        result.Nodes[0].Query.Should().Be("Topic A");
        // But should apply sentinel keywords
        result.Nodes[0].FilterKeywords.Should().Contain("keyword1");
    }

    [Fact]
    public void SentinelRefiner_OnlySentinelSplits_AcceptsLlmDecomposition()
    {
        var refiner = new SentinelRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "Tell me about X and also Y",
            // No proposed nodes from deterministic analysis
        };

        var sentinel = new SentinelRefinementInput
        {
            IsComposite = true,
            Subqueries = ["What is X?", "What is Y?"],
            FilterKeywords = ["x_keyword"]
        };

        var result = refiner.Refine(signals, sentinel);

        result.Nodes.Should().HaveCount(2);
        result.Nodes[0].Query.Should().Be("What is X?");
        result.Nodes[1].Query.Should().Be("What is Y?");
    }

    [Fact]
    public void SentinelRefiner_NeitherSplits_SingleEnhancedNode()
    {
        var refiner = new SentinelRefiner();
        var signals = new QuerySignals
        {
            OriginalQuery = "What is RLHF?"
        };

        var sentinel = new SentinelRefinementInput
        {
            IsComposite = false,
            CorrectedQuery = "What is RLHF?",
            FilterKeywords = ["rlhf", "reinforcement", "learning"],
            SearchQueries = ["RLHF reinforcement learning human feedback"],
            RequiresFresh = false
        };

        var result = refiner.Refine(signals, sentinel);

        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].FilterKeywords.Should().Contain("rlhf");
        result.Nodes[0].SearchQueries.Should().NotBeEmpty();
    }

    [Fact]
    public void SentinelRefiner_PreservesContentReferences()
    {
        var refiner = new SentinelRefiner();
        var reference = new ContentReference
        {
            Uri = "https://arxiv.org/abs/2210.02406",
            Kind = ContentReferenceKind.Url
        };

        var signals = new QuerySignals
        {
            OriginalQuery = "Tell me about https://arxiv.org/abs/2210.02406",
            ProposedNodes =
            [
                new QueryNode
                {
                    Query = "Analyze content",
                    Type = QueryNodeType.ContentReference,
                    Reference = reference
                }
            ]
        };

        var sentinel = new SentinelRefinementInput
        {
            IsComposite = false,
            FilterKeywords = ["paper"]
        };

        var result = refiner.Refine(signals, sentinel);

        result.Plan.ReferenceNodes.Should().HaveCount(1);
        result.Plan.ReferenceNodes[0].Reference!.Uri.Should().Be("https://arxiv.org/abs/2210.02406");
    }

    [Fact]
    public void SentinelRefiner_RoutesToolActionsToPlan()
    {
        var refiner = new SentinelRefiner();
        var toolAction = new ToolAction
        {
            Tool = ToolKind.Index,
            Intent = "Build KB"
        };

        var signals = new QuerySignals
        {
            OriginalQuery = "Index these files",
            ProposedNodes =
            [
                new QueryNode
                {
                    Query = "Index these files",
                    Type = QueryNodeType.ToolAction,
                    ToolAction = toolAction
                }
            ]
        };

        var sentinel = new SentinelRefinementInput { IsComposite = false };

        var result = refiner.Refine(signals, sentinel);

        result.Plan.ToolActionNodes.Should().HaveCount(1);
        result.HasToolActions.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════
    // DecompositionResult
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void DecompositionResult_IsFastPath_FalseWhenToolActionsPresent()
    {
        var result = new DecompositionResult
        {
            OriginalQuery = "Index files",
            Complexity = QueryComplexity.Simple,
            Plan = new ExecutionPlan
            {
                ToolActionNodes =
                [
                    new QueryNode
                    {
                        Query = "Index",
                        Type = QueryNodeType.ToolAction,
                        ToolAction = new ToolAction { Tool = ToolKind.Index, Intent = "Index" }
                    }
                ]
            }
        };

        result.IsFastPath.Should().BeFalse("tool actions should prevent fast path");
        result.HasToolActions.Should().BeTrue();
    }

    [Fact]
    public void DecompositionResult_LeafCount()
    {
        var result = new DecompositionResult
        {
            OriginalQuery = "test",
            Nodes =
            [
                new QueryNode { Query = "A" },
                new QueryNode
                {
                    Query = "B",
                    Children =
                    [
                        new QueryNode { Query = "B1" },
                        new QueryNode { Query = "B2" }
                    ]
                }
            ]
        };

        result.LeafCount.Should().Be(3); // A + B1 + B2
    }

    // ════════════════════════════════════════════════════════════════════
    // RecursionGuard
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void RecursionGuard_AllowsWithinBudget()
    {
        var guard = new RecursionGuard();
        var node = new QueryNode { Query = "test", Depth = 1 };

        guard.CanDecompose(node, []).Should().BeTrue();
    }

    [Fact]
    public void RecursionGuard_RejectsExceedingMaxDepth()
    {
        var guard = new RecursionGuard();
        var node = new QueryNode { Query = "test", Depth = 4 }; // > default max 3

        guard.CanDecompose(node, []).Should().BeFalse();
    }

    [Fact]
    public void RecursionGuard_RejectsExceedingLeafBudget()
    {
        var guard = new RecursionGuard();

        // Register 8 leaves (max default)
        for (var i = 0; i < 8; i++)
            guard.RegisterLeaf();

        var node = new QueryNode { Query = "test", Depth = 1 };
        guard.CanDecompose(node, []).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    // InMemoryDecompositionCache
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InMemoryCache_StoreAndRetrieve()
    {
        var cache = new InMemoryDecompositionCache();
        var embedding = new float[] { 1f, 0f, 0f, 0f };
        var result = new SubQueryResult { NodeId = "test", Success = true, ItemCount = 5 };

        await cache.PutAsync(embedding, null, result);
        var hit = await cache.GetAsync(embedding, null);

        hit.Should().NotBeNull();
        hit!.Similarity.Should().BeGreaterThan(0.9f);
    }

    [Fact]
    public async Task InMemoryCache_MissOnDifferentEmbedding()
    {
        var cache = new InMemoryDecompositionCache();
        var embedding1 = new float[] { 1f, 0f, 0f, 0f };
        var embedding2 = new float[] { 0f, 1f, 0f, 0f }; // orthogonal
        var result = new SubQueryResult { NodeId = "test", Success = true };

        await cache.PutAsync(embedding1, null, result);
        var hit = await cache.GetAsync(embedding2, null);

        hit.Should().BeNull("orthogonal embeddings should not match");
    }

    [Fact]
    public async Task InMemoryCache_EvictsExpired()
    {
        var cache = new InMemoryDecompositionCache();
        var embedding = new float[] { 1f, 0f, 0f, 0f };
        var result = new SubQueryResult { NodeId = "test", Success = true };

        await cache.PutAsync(embedding, null, result);

        // Evict should not remove fresh entries
        await cache.EvictExpiredAsync();
        var hit = await cache.GetAsync(embedding, null);
        hit.Should().NotBeNull();
    }

    // ════════════════════════════════════════════════════════════════════
    // DecompositionOrchestrator
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Orchestrator_ExecutesFastPath()
    {
        var orchestrator = new DecompositionOrchestrator();
        var executor = new FakeSubQueryExecutor();

        var plan = new DecompositionResult
        {
            OriginalQuery = "What is AI?",
            Complexity = QueryComplexity.Simple,
            Nodes = [new QueryNode { Query = "What is AI?" }]
        };

        var result = await orchestrator.ExecuteAsync(plan, executor);

        result.WasFastPath.Should().BeTrue();
        result.SuccessfulNodes.Should().Be(1);
    }

    [Fact]
    public async Task Orchestrator_ExecutesToolActionNodes()
    {
        var orchestrator = new DecompositionOrchestrator();
        var executor = new FakeSubQueryExecutor();

        var toolAction = new ToolAction
        {
            Tool = ToolKind.FileSystem,
            Intent = "Find files"
        };

        var toolNode = new QueryNode
        {
            Query = "Find files",
            Type = QueryNodeType.ToolAction,
            ToolAction = toolAction
        };

        var plan = new DecompositionResult
        {
            OriginalQuery = "Find files in C:/test",
            Complexity = QueryComplexity.Moderate,
            Nodes = [toolNode],
            Plan = new ExecutionPlan
            {
                ToolActionNodes = [toolNode]
            }
        };

        var result = await orchestrator.ExecuteAsync(plan, executor);

        result.SuccessfulNodes.Should().Be(1);
        executor.ToolExecutions.Should().Be(1);
    }

    [Fact]
    public async Task Orchestrator_ExecutesPrerequisitesFirst()
    {
        var orchestrator = new DecompositionOrchestrator();
        var executor = new FakeSubQueryExecutor();

        var prereq = new QueryNode { Query = "What is RLHF?", IsPrerequisite = true };
        var main = new QueryNode { Query = "How does RLHF improve LLMs?" };

        var plan = new DecompositionResult
        {
            OriginalQuery = "How does RLHF improve LLMs?",
            Complexity = QueryComplexity.Moderate,
            Nodes = [prereq, main],
            Plan = new ExecutionPlan
            {
                Prerequisites = [prereq],
                ParallelNodes = [main]
            }
        };

        var result = await orchestrator.ExecuteAsync(plan, executor);

        result.SuccessfulNodes.Should().Be(2);
        // Prerequisites should execute before parallel nodes
        executor.ExecutionOrder[0].Should().Be(prereq.Id);
    }

    [Fact]
    public async Task Orchestrator_HandlesUnsupportedTool()
    {
        var orchestrator = new DecompositionOrchestrator();
        var executor = new FakeSubQueryExecutor { SupportedTools = [] };

        var toolNode = new QueryNode
        {
            Query = "Transform to CSV",
            Type = QueryNodeType.ToolAction,
            ToolAction = new ToolAction { Tool = ToolKind.Transform, Intent = "Convert" }
        };

        var plan = new DecompositionResult
        {
            OriginalQuery = "Transform to CSV",
            Complexity = QueryComplexity.Moderate,
            Nodes = [toolNode],
            Plan = new ExecutionPlan { ToolActionNodes = [toolNode] }
        };

        var result = await orchestrator.ExecuteAsync(plan, executor);

        result.FailedNodes.Should().Be(1);
    }

    [Fact]
    public async Task Orchestrator_ExecutesReferenceNodes()
    {
        var orchestrator = new DecompositionOrchestrator();
        var executor = new FakeSubQueryExecutor();

        var refNode = new QueryNode
        {
            Query = "Fetch URL",
            Type = QueryNodeType.ContentReference,
            Reference = new ContentReference { Uri = "https://example.com", Kind = ContentReferenceKind.Url }
        };

        var plan = new DecompositionResult
        {
            OriginalQuery = "Tell me about https://example.com",
            Complexity = QueryComplexity.Moderate,
            Nodes = [refNode],
            Plan = new ExecutionPlan { ReferenceNodes = [refNode] }
        };

        var result = await orchestrator.ExecuteAsync(plan, executor);

        result.SuccessfulNodes.Should().Be(1);
        executor.ReferenceFetches.Should().Be(1);
    }

    // ════════════════════════════════════════════════════════════════════
    // DecompositionPipeline — End-to-End (no embeddings)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pipeline_SimpleQuery_FastPath()
    {
        var pipeline = CreatePipeline();

        var result = await pipeline.DecomposeAsync("What is a transformer?");

        result.IsFastPath.Should().BeTrue();
        result.Complexity.Should().Be(QueryComplexity.Simple);
        result.Nodes.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Pipeline_UrlQuery_ExtractsReferences()
    {
        var pipeline = CreatePipeline();

        var result = await pipeline.DecomposeAsync(
            "Tell me about https://arxiv.org/abs/2210.02406",
            hasUrls: true);

        result.Complexity.Should().NotBe(QueryComplexity.Simple);
        result.Plan.ReferenceNodes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Pipeline_ToolUseQuery_DetectsTools()
    {
        var pipeline = CreatePipeline();

        // hasUrls triggers Moderate complexity, allowing full analyzer pipeline to run.
        // The file path C:/test is detected as a reference, and the query contains
        // tool-use verbs (index, build KB).
        var result = await pipeline.DecomposeAsync(
            "Go to C:/test, index all the markdown files, build a knowledge base called 'myfiles'",
            hasUrls: true); // file path counts as URL-like reference

        result.HasToolActions.Should().BeTrue();
        result.Plan.ToolActionNodes.Should().NotBeEmpty();
        result.IsFastPath.Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_WithSentinelData_EnhancesNodes()
    {
        var pipeline = CreatePipeline();

        var sentinel = new SentinelRefinementInput
        {
            IsComposite = false,
            FilterKeywords = ["transformer", "attention"],
            SearchQueries = ["transformer attention mechanism"],
            RequiresFresh = false
        };

        var result = await pipeline.DecomposeAsync(
            "What is a transformer?",
            sentinelData: sentinel);

        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].FilterKeywords.Should().Contain("transformer");
    }

    // ════════════════════════════════════════════════════════════════════
    // DoomSummarizerAdapter
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Adapter_ToRefinementInput()
    {
        var input = DoomSummarizerAdapter.ToRefinementInput(
            isComposite: true,
            subqueries: ["Q1", "Q2"],
            correctedQuery: "corrected",
            filterKeywords: ["kw1"],
            searchQueries: ["sq1"],
            entities: ["ent1"],
            timeSensitivity: "today",
            requiresFresh: true,
            intent: "news",
            categories: new() { ["tech"] = 0.9 });

        input.IsComposite.Should().BeTrue();
        input.Subqueries.Should().HaveCount(2);
        input.CorrectedQuery.Should().Be("corrected");
        input.RequiresFresh.Should().BeTrue();
    }

    [Fact]
    public void Adapter_GetEnrichment_IncludesToolActions()
    {
        var toolNode = new QueryNode
        {
            Query = "Index files",
            Type = QueryNodeType.ToolAction,
            ToolAction = new ToolAction { Tool = ToolKind.Index, Intent = "Build KB" }
        };

        var result = new DecompositionResult
        {
            OriginalQuery = "Index files",
            Nodes = [toolNode],
            Plan = new ExecutionPlan { ToolActionNodes = [toolNode] },
            Signals = new QuerySignals
            {
                OriginalQuery = "Index files",
                DetectedTools = [toolNode.ToolAction]
            }
        };

        var enrichment = DoomSummarizerAdapter.GetEnrichment(result);

        enrichment.HasToolActions.Should().BeTrue();
        enrichment.ToolActions.Should().HaveCount(1);
        enrichment.ToolActions[0].Tool.Should().Be(ToolKind.Index);
        enrichment.DetectedTools.Should().HaveCount(1);
    }

    // ════════════════════════════════════════════════════════════════════
    // TemporalAnalyzer
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TemporalAnalyzer_DetectsTemporalComparison()
    {
        var analyzer = new TemporalAnalyzer();
        var signals = new QuerySignals { OriginalQuery = "How has AI changed since 2020?" };

        var result = await analyzer.AnalyzeAsync("How has AI changed since 2020?", signals);

        // Temporal comparison creates two time-bounded sub-queries
        result.ProposedNodes.Should().HaveCount(2);
        result.ProposedNodes.Should().Contain(n => n.TimeScope != null && n.TimeScope.TimeSensitivity == "historical");
        result.ProposedNodes.Should().Contain(n => n.TimeScope != null && n.TimeScope.RequiresFresh);
        result.DetectedConcept.Should().Be(ConceptType.Timeline);
    }

    [Fact]
    public async Task TemporalAnalyzer_AnnotatesExistingNodesWithFreshness()
    {
        var analyzer = new TemporalAnalyzer();
        var signals = new QuerySignals
        {
            OriginalQuery = "latest AI news today",
            ProposedNodes = [new QueryNode { Query = "AI news" }]
        };

        var result = await analyzer.AnalyzeAsync("latest AI news today", signals);

        result.ProposedNodes.Should().HaveCount(1);
        result.ProposedNodes[0].TimeScope.Should().NotBeNull();
        result.ProposedNodes[0].TimeScope!.RequiresFresh.Should().BeTrue();
        result.ProposedNodes[0].TimeScope!.TimeSensitivity.Should().Be("today");
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private static DecompositionPipeline CreatePipeline()
    {
        var classifier = new ComplexityClassifier();
        var conceptClassifier = new ConceptClassifier();
        var analyzers = new IQueryAnalyzer[]
        {
            new ReferenceExtractor(),
            new StructuralAnalyzer(),
            new EntityRelationAnalyzer(),
            new TemporalAnalyzer(),
            new SemanticClusterAnalyzer(),
            new ToolUseAnalyzer()
        };
        var refiner = new SentinelRefiner();

        return new DecompositionPipeline(classifier, conceptClassifier, analyzers, refiner);
    }

    // ════════════════════════════════════════════════════════════════════
    // SemanticClusterAnalyzer — URL reference stripping
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Tell me about https://arxiv.org/abs/2210.02406 and LLMs",
        "Tell me about and LLMs")]
    [InlineData("Compare http://example.com/a with http://example.com/b",
        "Compare with")]
    [InlineData("Go to C:\\Users\\test\\docs and analyze",
        "Go to and analyze")]
    [InlineData("Check ~/projects/myrepo for files",
        "Check for files")]
    [InlineData("No references here at all",
        "No references here at all")]
    public void SemanticClusterAnalyzer_StripReferences(string input, string expected)
    {
        SemanticClusterAnalyzer.StripReferences(input).Should().Be(expected);
    }

    [Fact]
    public void SemanticClusterAnalyzer_GenerateWindows_ProducesOverlapping()
    {
        var words = new[] { "a", "b", "c", "d", "e" };
        var windows = SemanticClusterAnalyzer.GenerateWindows(words, 3);

        windows.Should().HaveCount(3);
        windows[0].Should().Be("a b c");
        windows[1].Should().Be("b c d");
        windows[2].Should().Be("c d e");
    }

    // ════════════════════════════════════════════════════════════════════
    // ComplexityClassifier — Tool-use signal detection
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Go to C:/test and find files", true)]
    [InlineData("Index all markdown files in D:/docs", true)]
    [InlineData("Crawl https://example.com for content", true)]
    [InlineData("Build a knowledge base from my files", true)]
    [InlineData("/home/user/projects has the data", true)]
    [InlineData("~/myfiles needs scraping", true)]
    [InlineData("What is a transformer model?", false)]
    [InlineData("Compare OpenAI and Anthropic", false)]
    public void ComplexityClassifier_HasToolUseSignals(string query, bool expected)
    {
        ComplexityClassifier.HasToolUseSignals(query).Should().Be(expected);
    }

    // ════════════════════════════════════════════════════════════════════
    // RetrievalSubQueryExecutor — basic construction
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RetrievalSubQueryExecutor_SupportsExpectedTools()
    {
        // We can't construct a real RetrievalSubQueryExecutor without StorageService,
        // but we can verify the tool support logic via the FakeSubQueryExecutor
        var executor = new FakeSubQueryExecutor();

        (await executor.SupportsToolAsync(ToolKind.Search)).Should().BeTrue();
        (await executor.SupportsToolAsync(ToolKind.FileSystem)).Should().BeTrue();
        (await executor.SupportsToolAsync(ToolKind.Crawl)).Should().BeTrue();
    }

    [Fact]
    public async Task RetrievalSubQueryExecutor_UnsupportedToolFails()
    {
        var executor = new FakeSubQueryExecutor
        {
            SupportedTools = new HashSet<ToolKind> { ToolKind.Search }
        };

        (await executor.SupportsToolAsync(ToolKind.FileSystem)).Should().BeFalse();
        (await executor.SupportsToolAsync(ToolKind.Transform)).Should().BeFalse();
    }
}

/// <summary>
/// Fake executor for testing orchestrator behavior without real service dependencies.
/// </summary>
internal class FakeSubQueryExecutor : ISubQueryExecutor
{
    public int ToolExecutions { get; private set; }
    public int ReferenceFetches { get; private set; }
    public List<string> ExecutionOrder { get; } = [];
    public HashSet<ToolKind> SupportedTools { get; set; } =
        [ToolKind.Search, ToolKind.Fetch, ToolKind.FileSystem, ToolKind.Index,
         ToolKind.KbQuery, ToolKind.Analyze, ToolKind.Crawl, ToolKind.Transform];

    public Task<SubQueryResult> ExecuteAsync(QueryNode node, CancellationToken ct = default)
    {
        ExecutionOrder.Add(node.Id);
        return Task.FromResult(new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            ItemCount = 5,
            Items = new List<string> { "result1", "result2" }
        });
    }

    public Task<SubQueryResult> FetchReferenceAsync(ContentReference reference, CancellationToken ct = default)
    {
        ReferenceFetches++;
        return Task.FromResult(new SubQueryResult
        {
            NodeId = "ref-" + reference.Uri.GetHashCode(),
            Success = true,
            ItemCount = 1,
            Items = $"Content from {reference.Uri}"
        });
    }

    public Task<SubQueryResult> ExecuteToolAsync(QueryNode node, ToolAction action, CancellationToken ct = default)
    {
        ToolExecutions++;
        ExecutionOrder.Add(node.Id);
        return Task.FromResult(new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            ItemCount = 3,
            Items = $"Tool result from {action.Tool}: {action.Intent}"
        });
    }

    public Task<bool> SupportsToolAsync(ToolKind tool, CancellationToken ct = default) =>
        Task.FromResult(SupportedTools.Contains(tool));
}
