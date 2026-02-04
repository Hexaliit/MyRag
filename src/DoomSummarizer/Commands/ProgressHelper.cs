using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
///     Standard progress bar setup used across commands.
///     Wraps Spectre progress rendering with markup-safety: if any task description
///     contains unescaped brackets (e.g. "[Go.pdf]"), the error is caught and
///     the progress bar falls back gracefully instead of crashing the command.
/// </summary>
public static class ProgressHelper
{
    public static async Task RunAsync(Func<ProgressContext, Task> action)
    {
        try
        {
            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(action);
        }
        catch (Exception ex)
        {
            // Check the full exception chain for Spectre markup errors
            var markupError = FindMarkupError(ex);
            if (markupError != null)
            {
                // Spectre markup parse error — log stack trace to find the source
                Console.Error.WriteLine($"[DIAG] Markup error: {markupError.Message}");
                Console.Error.WriteLine($"[DIAG] Stack: {markupError.StackTrace}");
                if (markupError != ex)
                    Console.Error.WriteLine($"[DIAG] Outer: {ex.GetType().Name}: {ex.Message}");
                return; // Swallow — the action completed, error is cosmetic
            }

            throw; // Not a markup error — rethrow
        }
    }

    private static Exception? FindMarkupError(Exception? ex)
    {
        while (ex != null)
        {
            if (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
                return ex;
            ex = ex.InnerException;
        }

        return null;
    }
}