using DoomSummarizer.Plugins;
using DoomSummarizer.Services;
using DoomSummarizer.Sources.YouTube;
using Mostlylucid.DocSummarizer.Resilience;

namespace DoomSummarizer.Tests.Plugins;

public class YouTubeSourcePluginTests
{
    private readonly YouTubeSourcePlugin _plugin = new();

    [Fact]
    public void Metadata_PrimaryKey_IsYouTube()
    {
        _plugin.Metadata.PrimaryKey.Should().Be("youtube");
    }

    [Fact]
    public void Metadata_Keys_IncludesShorthand()
    {
        _plugin.Metadata.Keys.Should().Contain("youtube");
        _plugin.Metadata.Keys.Should().Contain("yt");
    }

    [Fact]
    public void Metadata_Capabilities_IncludesSearchAndNoAuth()
    {
        _plugin.Metadata.Capabilities.Should().HaveFlag(SourceCapabilities.Search);
        _plugin.Metadata.Capabilities.Should().HaveFlag(SourceCapabilities.NoAuth);
    }

    [Fact]
    public void Metadata_HasDisplayName()
    {
        _plugin.Metadata.DisplayName.Should().Be("YouTube");
    }

    [Fact]
    public void Metadata_HasExamples()
    {
        _plugin.Metadata.Examples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_CompletesSuccessfully()
    {
        // InitializeAsync is a no-op for YouTube plugin; just verify no exception
        await _plugin.InitializeAsync(null!);
    }
}
