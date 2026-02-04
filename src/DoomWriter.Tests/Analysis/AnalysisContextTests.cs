using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Tests.Analysis;

public class AnalysisContextTests
{
    [Fact]
    public void AddSignal_StoresSignal()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "test.key", Value = 42, Source = "test" });

        ctx.HasSignal("test.key").Should().BeTrue();
        ctx.GetValue<int>("test.key").Should().Be(42);
    }

    [Fact]
    public void GetBestSignal_ReturnsHighestConfidence()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "test", Value = "low", Source = "a", Confidence = 0.3 });
        ctx.AddSignal(new Signal { Key = "test", Value = "high", Source = "b", Confidence = 0.9 });
        ctx.AddSignal(new Signal { Key = "test", Value = "mid", Source = "c", Confidence = 0.5 });

        ctx.GetBestSignal("test")!.GetValue<string>().Should().Be("high");
    }

    [Fact]
    public void GetSignals_ReturnsAllForKey()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "test", Value = 1, Source = "a" });
        ctx.AddSignal(new Signal { Key = "test", Value = 2, Source = "b" });

        ctx.GetSignals("test").Should().HaveCount(2);
    }

    [Fact]
    public void HasSignal_ReturnsFalseForMissingKey()
    {
        var ctx = new AnalysisContext();
        ctx.HasSignal("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void Cache_StoresAndRetrievesData()
    {
        var ctx = new AnalysisContext();
        ctx.SetCached("items", new List<string> { "a", "b" });

        ctx.HasCached("items").Should().BeTrue();
        ctx.GetCached<List<string>>("items").Should().HaveCount(2);
    }

    [Fact]
    public void ClearCache_RemovesAllCachedData()
    {
        var ctx = new AnalysisContext();
        ctx.SetCached("key1", "value1");
        ctx.SetCached("key2", "value2");

        ctx.ClearCache();

        ctx.HasCached("key1").Should().BeFalse();
        ctx.HasCached("key2").Should().BeFalse();
    }

    [Fact]
    public void SkipWave_MarksWaveAsSkipped()
    {
        var ctx = new AnalysisContext();
        ctx.SkipWave("expensive_wave");

        ctx.IsWaveSkipped("expensive_wave").Should().BeTrue();
        ctx.IsWaveSkipped("other_wave").Should().BeFalse();
    }

    [Fact]
    public void GetSignalsByTag_FiltersCorrectly()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "a", Value = 1, Source = "test", Tags = ["structure"] });
        ctx.AddSignal(new Signal { Key = "b", Value = 2, Source = "test", Tags = ["entity"] });
        ctx.AddSignal(new Signal { Key = "c", Value = 3, Source = "test", Tags = ["structure", "content"] });

        ctx.GetSignalsByTag("structure").Should().HaveCount(2);
        ctx.GetSignalsByTag("entity").Should().HaveCount(1);
    }

    [Fact]
    public void Aggregate_HighestConfidence_ReturnsCorrectSignal()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "score", Value = 0.5, Source = "a", Confidence = 0.3 });
        ctx.AddSignal(new Signal { Key = "score", Value = 0.9, Source = "b", Confidence = 0.95 });

        var agg = ctx.Aggregate("score", AggregationStrategy.HighestConfidence);
        agg.Should().NotBeNull();
        agg!.GetValue<double>().Should().Be(0.9);
    }

    [Fact]
    public void Aggregate_WeightedAverage_ComputesCorrectly()
    {
        var ctx = new AnalysisContext();
        ctx.AddSignal(new Signal { Key = "temp", Value = 10.0, Source = "a", Confidence = 0.8 });
        ctx.AddSignal(new Signal { Key = "temp", Value = 20.0, Source = "b", Confidence = 0.2 });

        var agg = ctx.Aggregate("temp", AggregationStrategy.WeightedAverage);
        agg.Should().NotBeNull();
        // Weighted: (10*0.8 + 20*0.2) / (0.8+0.2) = 12.0
        agg!.GetValue<double>().Should().Be(12.0);
    }

    [Fact]
    public void SelectedRoute_ControlsFastAndQualityFlags()
    {
        var ctx = new AnalysisContext { SelectedRoute = "fast" };
        ctx.IsFastRoute.Should().BeTrue();
        ctx.IsQualityRoute.Should().BeFalse();

        ctx.SelectedRoute = "quality";
        ctx.IsFastRoute.Should().BeFalse();
        ctx.IsQualityRoute.Should().BeTrue();
    }
}