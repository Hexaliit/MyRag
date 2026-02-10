using DoomSummarizer.Models;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests;

/// <summary>
///     Tests for ConfigService path helpers.
/// </summary>
public class ConfigServiceTests
{
    [Fact]
    public void GetVectorDbPath_ReturnsPathInConfigDir()
    {
        var path = ConfigService.GetVectorDbPath();

        path.Should().Contain(".doomsummarizer");
        path.Should().EndWith("vectors.duckdb");
    }

    [Fact]
    public void GetVectorDbPath_DirectoryExists()
    {
        var path = ConfigService.GetVectorDbPath();
        var dir = Path.GetDirectoryName(path);

        Directory.Exists(dir).Should().BeTrue();
    }

    [Fact]
    public void GetVectorDbPath_RespectsConfigOverride()
    {
        var config = new DoomConfig
        {
            Storage = new StorageConfig { VectorDbPath = "~/custom/my-vectors.duckdb" }
        };
        var path = ConfigService.GetVectorDbPath(config);

        path.Should().Contain("custom");
        path.Should().EndWith("my-vectors.duckdb");
    }

    [Fact]
    public void GetConfigDir_IsUnderUserProfile()
    {
        var configDir = ConfigService.GetConfigDir();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        configDir.Should().StartWith(userProfile);
    }
}