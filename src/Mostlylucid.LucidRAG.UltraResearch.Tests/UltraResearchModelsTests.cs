using System.Text.Json;
using LucidRAG.UltraResearch;

namespace Mostlylucid.LucidRAG.UltraResearch.Tests;

public class UltraResearchModelsTests
{
    [Fact]
    public void UltraResearchConfig_DefaultValues_AreReasonable()
    {
        var config = new UltraResearchConfig { Topic = "test" };

        Assert.Equal(200, config.MaxPapers);
        Assert.Equal(10, config.BatchSize);
        Assert.Equal(50, config.MaxIterations);
        Assert.Equal(TimeSpan.FromHours(8), config.MaxDuration);
        Assert.Equal(5, config.SentinelInterval);
        Assert.Equal(0.15, config.ConvergenceThreshold);
        Assert.True(config.IncludeSemanticScholar);
        Assert.False(config.DryRun);
    }

    [Fact]
    public void UltraResearchState_InitialState_IsCorrect()
    {
        var state = new UltraResearchState { Topic = "attention mechanisms" };

        Assert.Equal(UltraResearchStatus.Running, state.Status);
        Assert.Equal(0, state.Iteration);
        Assert.Equal(0, state.PapersFetched);
        Assert.Empty(state.SeenIds);
        Assert.Empty(state.Frontier);
        Assert.Empty(state.Checkpoints);
        Assert.Null(state.CompletedAt);
    }

    [Fact]
    public void UltraResearchState_SerializesAndDeserializes()
    {
        var state = new UltraResearchState
        {
            Topic = "test topic",
            Iteration = 5,
            PapersFetched = 42,
            PapersIngested = 38,
            SeenIds = ["arxiv:2301.12345", "doi:10.1234/test"],
            Frontier =
            [
                new FetchCandidate
                {
                    Id = "2302.00001", Type = "arxiv", Source = CandidateSource.Search, Priority = 0.8
                }
            ],
            SearchQueriesUsed = ["attention mechanisms"],
            Checkpoints =
            [
                new SentinelCheckpoint
                {
                    Iteration = 3, TotalPapers = 20, NewInfoRatio = 0.35,
                    IdentifiedGaps = ["missing survey papers"],
                    SuggestedQueries = ["transformer survey 2023"]
                }
            ]
        };

        var json = JsonSerializer.Serialize(state);
        var deserialized = JsonSerializer.Deserialize<UltraResearchState>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("test topic", deserialized.Topic);
        Assert.Equal(5, deserialized.Iteration);
        Assert.Equal(42, deserialized.PapersFetched);
        Assert.Equal(38, deserialized.PapersIngested);
        Assert.Contains("arxiv:2301.12345", deserialized.SeenIds);
        Assert.Single(deserialized.Frontier);
        Assert.Single(deserialized.Checkpoints);
        Assert.Equal(0.35, deserialized.Checkpoints[0].NewInfoRatio);
    }

    [Fact]
    public void FetchCandidate_AllSourceTypes_AreValid()
    {
        var sources = Enum.GetValues<CandidateSource>();
        Assert.Contains(CandidateSource.Search, sources);
        Assert.Contains(CandidateSource.Citation, sources);
        Assert.Contains(CandidateSource.SemanticScholar, sources);
        Assert.Contains(CandidateSource.Orphan, sources);
        Assert.Contains(CandidateSource.Sentinel, sources);
    }

    [Fact]
    public void SentinelCheckpoint_DefaultShouldContinue_IsTrue()
    {
        var checkpoint = new SentinelCheckpoint();
        Assert.True(checkpoint.ShouldContinue);
        Assert.Empty(checkpoint.IdentifiedGaps);
        Assert.Empty(checkpoint.SuggestedQueries);
    }

    [Fact]
    public void UltraResearchProgress_Record_CreatesCorrectly()
    {
        var progress = new UltraResearchProgress(
            ResearchStage.Fetching, "Fetching batch...", 3, 15, 12, 45, 0.28);

        Assert.Equal(ResearchStage.Fetching, progress.Stage);
        Assert.Equal("Fetching batch...", progress.Message);
        Assert.Equal(3, progress.Iteration);
        Assert.Equal(15, progress.PapersFetched);
        Assert.Equal(12, progress.PapersIngested);
        Assert.Equal(45, progress.FrontierSize);
        Assert.Equal(0.28, progress.NewInfoRatio);
    }

    [Fact]
    public void UltraResearchStatus_SerializesAsString()
    {
        var state = new UltraResearchState
        {
            Topic = "test",
            Status = UltraResearchStatus.Completed
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("\"Completed\"", json);

        var deserialized = JsonSerializer.Deserialize<UltraResearchState>(json);
        Assert.Equal(UltraResearchStatus.Completed, deserialized!.Status);
    }
}
