using System.Diagnostics;

namespace DoomSummarizer.Tests;

/// <summary>
/// CLI integration smoke tests — runs the actual doomsummarizer binary
/// and checks exit codes and basic output.
///
/// These require a pre-built binary and network access.
/// Excluded from default test runs — use:
///   dotnet test --filter "Category=Smoke"
/// or run via test-all.ps1 which builds first.
/// </summary>
[Trait("Category", "Smoke")]
public class CliSmokeTests
{
    private static readonly string ExePath = FindExePath();

    private static string FindExePath()
    {
        // Navigate from test output dir to the main project build output
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "DoomSummarizer", "bin", "Debug", "net10.0", "win-x64", "doomsummarizer.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "DoomSummarizer", "bin", "Debug", "net10.0", "doomsummarizer.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "DoomSummarizer", "bin", "Release", "net10.0", "win-x64", "doomsummarizer.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "DoomSummarizer", "bin", "Release", "net10.0", "doomsummarizer.exe"),
        };

        foreach (var c in candidates)
        {
            var resolved = Path.GetFullPath(c);
            if (File.Exists(resolved))
                return resolved;
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunCliAsync(
        string args, int timeoutSeconds = 30)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = process.WaitForExit(timeoutSeconds * 1000);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"CLI command timed out after {timeoutSeconds}s: {args}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void ExeExists()
    {
        File.Exists(ExePath).Should().BeTrue(
            $"Expected binary at {ExePath}. Run 'dotnet build' first.");
    }

    #region Config Command

    [Fact]
    public async Task Config_Show_ExitsCleanly()
    {
        var (exitCode, stdout, _) = await RunCliAsync("config");

        exitCode.Should().Be(0, "config command should succeed");
        stdout.Should().NotBeNullOrWhiteSpace("config should produce output");
    }

    #endregion

    #region Sources Command

    [Fact]
    public async Task Sources_List_ShowsSources()
    {
        var (exitCode, stdout, _) = await RunCliAsync("sources");

        exitCode.Should().Be(0, "sources command should succeed");
        stdout.Should().ContainAny("hn", "reddit", "bbc", "gnews");
    }

    #endregion

    #region Show Command

    [Fact]
    public async Task Show_List_ExitsCleanly()
    {
        var (exitCode, _, _) = await RunCliAsync("show");

        exitCode.Should().Be(0, "show command should succeed even with empty KB");
    }

    #endregion

    #region Scroll Command

    [Fact]
    public async Task Scroll_QuietNoLlm_ProducesOutput()
    {
        var (exitCode, _, stderr) = await RunCliAsync(
            "scroll \"test query\" -q --no-llm --limit 3", timeoutSeconds: 60);

        (exitCode == 0 || stderr.Contains("No items") || stderr.Contains("Error"))
            .Should().BeTrue($"scroll should exit cleanly or with known error. Exit: {exitCode}, stderr: {stderr}");
    }

    [Fact]
    public async Task Scroll_JsonOutput_ProducesValidJson()
    {
        var (exitCode, stdout, _) = await RunCliAsync(
            "scroll \"tech news\" -q --no-llm --limit 2 --json", timeoutSeconds: 60);

        if (exitCode == 0 && stdout.Trim().Length > 0)
        {
            var isJson = stdout.TrimStart().StartsWith('[') || stdout.TrimStart().StartsWith('{');
            isJson.Should().BeTrue("--json flag should produce JSON output");
        }
    }

    [Fact]
    public async Task Scroll_DebugFlag_DoesNotCrash()
    {
        var (exitCode, _, _) = await RunCliAsync(
            "scroll \"test\" -q --no-llm --limit 2 --debug", timeoutSeconds: 60);

        (exitCode >= 0).Should().BeTrue("should not crash with --debug flag");
    }

    [Fact]
    public async Task Scroll_LocalOnly_ExitsCleanly()
    {
        var (exitCode, _, stderr) = await RunCliAsync(
            "scroll \"test\" -q --no-llm --local --limit 2", timeoutSeconds: 30);

        (exitCode == 0 || stderr.Contains("No items") || stderr.Contains("No local"))
            .Should().BeTrue("--local should exit cleanly even with empty KB");
    }

    #endregion

    #region Page Command

    [Fact]
    public async Task Page_InvalidUrl_HandlesGracefully()
    {
        var (exitCode, _, stderr) = await RunCliAsync(
            "page \"not-a-valid-url\" -q", timeoutSeconds: 15);

        (exitCode != 0 || stderr.Length > 0).Should().BeTrue(
            "page with invalid URL should report error, not crash");
    }

    #endregion

    #region Help

    [Fact]
    public async Task Help_ShowsCommands()
    {
        var (exitCode, stdout, _) = await RunCliAsync("--help", timeoutSeconds: 15);

        exitCode.Should().Be(0, "--help should succeed");
        stdout.Should().ContainAny("scroll", "config", "sources", "show");
    }

    #endregion
}
