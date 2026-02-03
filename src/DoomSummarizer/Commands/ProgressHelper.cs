using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Standard progress bar setup used across commands.
/// </summary>
public static class ProgressHelper
{
    public static async Task RunAsync(Func<ProgressContext, Task> action) =>
        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(action);
}
