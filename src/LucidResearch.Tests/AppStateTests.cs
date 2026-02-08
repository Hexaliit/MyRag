using LucidRAG.UltraResearch;
using LucidResearch;

namespace LucidResearch.Tests;

public class AppStateTests
{
    private readonly AppState _state = new();

    [Fact]
    public void Initial_state_has_default_values()
    {
        _state.ActiveSessionId.Value.Should().BeNull();
        _state.Topic.Value.Should().BeEmpty();
        _state.Status.Value.Should().Be(UltraResearchStatus.Completed);
        _state.Iteration.Value.Should().Be(0);
        _state.PapersFetched.Value.Should().Be(0);
        _state.PapersIngested.Value.Should().Be(0);
        _state.PapersFailed.Value.Should().Be(0);
        _state.FrontierSize.Value.Should().Be(0);
        _state.SeenCount.Value.Should().Be(0);
        _state.NewInfoRatio.Value.Should().Be(1.0);
        _state.TotalEntities.Value.Should().Be(0);
        _state.OrphanCitations.Value.Should().Be(0);
        _state.NewInfoHistory.Value.Should().BeEmpty();
        _state.CurrentView.Value.Should().Be(ViewMode.Dashboard);
        _state.ExitRequested.Value.Should().BeFalse();
        _state.ActivityLog.Value.Should().BeEmpty();
    }

    [Fact]
    public void AddActivity_appends_timestamped_entry()
    {
        _state.AddActivity("Session started");

        _state.ActivityLog.Value.Should().HaveCount(1);
        _state.ActivityLog.Value[0].Should().MatchRegex(@"^\[\d{2}:\d{2}:\d{2}\] Session started$");
    }

    [Fact]
    public void AddActivity_preserves_insertion_order()
    {
        _state.AddActivity("First");
        _state.AddActivity("Second");
        _state.AddActivity("Third");

        _state.ActivityLog.Value.Should().HaveCount(3);
        _state.ActivityLog.Value[0].Should().Contain("First");
        _state.ActivityLog.Value[1].Should().Contain("Second");
        _state.ActivityLog.Value[2].Should().Contain("Third");
    }

    [Fact]
    public void AddActivity_caps_at_100_entries()
    {
        for (var i = 0; i < 110; i++)
            _state.AddActivity($"Entry {i}");

        _state.ActivityLog.Value.Should().HaveCount(100);
        // Oldest entries (0-9) should have been evicted
        _state.ActivityLog.Value[0].Should().Contain("Entry 10");
        _state.ActivityLog.Value[99].Should().Contain("Entry 109");
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        // Set various state values
        var sessionId = Guid.NewGuid();
        _state.ActiveSessionId.Value = sessionId;
        _state.Topic.Value = "test topic";
        _state.Status.Value = UltraResearchStatus.Running;
        _state.Iteration.Value = 5;
        _state.PapersFetched.Value = 20;
        _state.PapersIngested.Value = 18;
        _state.PapersFailed.Value = 2;
        _state.FrontierSize.Value = 100;
        _state.SeenCount.Value = 50;
        _state.NewInfoRatio.Value = 0.3;
        _state.TotalEntities.Value = 42;
        _state.OrphanCitations.Value = 5;
        _state.NewInfoHistory.Value = [0.9, 0.7, 0.5, 0.3];
        _state.SynthesisText.Value = "Some synthesis";
        _state.SynthesisFilePath.Value = "/tmp/synthesis.md";
        _state.AddActivity("Some activity");

        _state.Reset();

        _state.ActiveSessionId.Value.Should().BeNull();
        _state.Topic.Value.Should().BeEmpty();
        _state.Status.Value.Should().Be(UltraResearchStatus.Completed);
        _state.Iteration.Value.Should().Be(0);
        _state.PapersFetched.Value.Should().Be(0);
        _state.PapersIngested.Value.Should().Be(0);
        _state.PapersFailed.Value.Should().Be(0);
        _state.FrontierSize.Value.Should().Be(0);
        _state.SeenCount.Value.Should().Be(0);
        _state.NewInfoRatio.Value.Should().Be(1.0);
        _state.TotalEntities.Value.Should().Be(0);
        _state.OrphanCitations.Value.Should().Be(0);
        _state.NewInfoHistory.Value.Should().BeEmpty();
        _state.SynthesisText.Value.Should().BeNull();
        _state.SynthesisFilePath.Value.Should().BeNull();
        _state.ActivityLog.Value.Should().BeEmpty();
    }

    [Fact]
    public void Reset_allows_new_activity_after_clear()
    {
        _state.AddActivity("Before reset");
        _state.Reset();
        _state.AddActivity("After reset");

        _state.ActivityLog.Value.Should().HaveCount(1);
        _state.ActivityLog.Value[0].Should().Contain("After reset");
    }

    [Fact]
    public void ViewMode_enum_has_expected_values()
    {
        Enum.GetValues<ViewMode>().Should().HaveCount(6);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.Dashboard);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.StartResearch);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.Frontier);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.Checkpoints);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.Sessions);
        Enum.GetValues<ViewMode>().Should().Contain(ViewMode.Summary);
    }

    [Fact]
    public void State_properties_are_mutable()
    {
        var sid = Guid.NewGuid();
        _state.ActiveSessionId.Value = sid;
        _state.ActiveSessionId.Value.Should().Be(sid);

        _state.Topic.Value = "RAG systems";
        _state.Topic.Value.Should().Be("RAG systems");

        _state.Status.Value = UltraResearchStatus.Running;
        _state.Status.Value.Should().Be(UltraResearchStatus.Running);
    }

    [Fact]
    public void NewInfoHistory_accepts_list_replacement()
    {
        var history = new List<double> { 1.0, 0.8, 0.6, 0.4 };
        _state.NewInfoHistory.Value = history;

        _state.NewInfoHistory.Value.Should().HaveCount(4);
        _state.NewInfoHistory.Value.Should().BeEquivalentTo([1.0, 0.8, 0.6, 0.4]);
    }
}
