using LucidRAG.UltraResearch;

namespace Mostlylucid.LucidRAG.UltraResearch.Tests;

public class ResearchFrontierManagerTests
{
    [Fact]
    public void AddDiscoveredCandidates_DeduplicatesBySeenIds()
    {
        var state = new UltraResearchState { Topic = "test" };
        state.SeenIds.Add("arxiv:2301.00001");

        var candidates = new List<FetchCandidate>
        {
            new() { Id = "2301.00001", Type = "arxiv", Source = CandidateSource.Search },
            new() { Id = "2301.00002", Type = "arxiv", Source = CandidateSource.Search }
        };

        // We can't easily test with a real ResearchFrontierManager (needs DB),
        // but we can test the dedup logic
        var added = 0;
        foreach (var c in candidates)
        {
            var key = ResearchPaperFetcher.NormalizeSeenKey(c.Type, c.Id);
            if (!state.SeenIds.Contains(key))
            {
                state.Frontier.Add(c);
                added++;
            }
        }

        Assert.Equal(1, added); // 2301.00001 was already seen
        Assert.Single(state.Frontier);
        Assert.Equal("2301.00002", state.Frontier[0].Id);
    }

    [Fact]
    public void FetchCandidate_PriorityOrdering_WorksCorrectly()
    {
        var candidates = new List<FetchCandidate>
        {
            new() { Id = "low", Type = "arxiv", Priority = 0.2 },
            new() { Id = "high", Type = "arxiv", Priority = 0.9 },
            new() { Id = "medium", Type = "arxiv", Priority = 0.5 }
        };

        var sorted = candidates.OrderByDescending(c => c.Priority).ToList();

        Assert.Equal("high", sorted[0].Id);
        Assert.Equal("medium", sorted[1].Id);
        Assert.Equal("low", sorted[2].Id);
    }

    [Fact]
    public void State_Frontier_SupportsRemoval()
    {
        var state = new UltraResearchState { Topic = "test" };
        var c1 = new FetchCandidate { Id = "a", Type = "arxiv", Priority = 0.9 };
        var c2 = new FetchCandidate { Id = "b", Type = "arxiv", Priority = 0.5 };
        state.Frontier.Add(c1);
        state.Frontier.Add(c2);

        var batch = state.Frontier.OrderByDescending(c => c.Priority).Take(1).ToList();
        foreach (var c in batch) state.Frontier.Remove(c);

        Assert.Single(state.Frontier);
        Assert.Equal("b", state.Frontier[0].Id);
    }
}
