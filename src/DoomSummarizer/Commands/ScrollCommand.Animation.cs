using System.Reflection;
using ConsoleImage.Player;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Easter egg animation methods for the scroll command.
/// </summary>
public sealed partial class ScrollCommand
{
    /// <summary>
    /// Play the DoomSummarizer easter egg animation with the title.
    /// </summary>
    private static async Task PlayEasterEggAnimationAsync(CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.WriteLine();

        var title = new FigletText("DoomSummarizer")
            .Color(Color.Cyan1);

        AnsiConsole.Write(title);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]AI-powered doom scrolling so you don't have to.[/]");
        AnsiConsole.WriteLine();

        // Try to load and play the embedded .cidz animation
        var doc = await LoadEmbeddedAnimationAsync(ct);

        if (doc != null)
        {
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit[/]");
            AnsiConsole.WriteLine();

            try
            {
                using var player = new ConsolePlayer(doc, loopCount: 3);
                await player.PlayAsync(ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[dim]Animation error: {ex.Message}[/]");
                await PlayInlineAnimationAsync(ct);
            }
        }
        else
        {
            // Fall back to inline ASCII animation
            await PlayInlineAnimationAsync(ct);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Ready to doom scroll![/]");
    }

    /// <summary>
    /// Load the embedded spin.cidz animation from assembly resources.
    /// </summary>
    private static async Task<PlayerDocument?> LoadEmbeddedAnimationAsync(CancellationToken ct)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Try different resource name patterns
            var resourceNames = new[] { "DoomSummarizer.spin.cidz", "DoomSummarizer.img.spin.cidz", "spin.cidz" };
            Stream? stream = null;
            string? foundName = null;

            foreach (var name in resourceNames)
            {
                stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    foundName = name;
                    break;
                }
            }

            if (stream == null)
            {
                AnsiConsole.MarkupLine("[dim]No embedded animation found[/]");
                return null;
            }

            AnsiConsole.MarkupLine($"[dim]Loading animation from {foundName} ({stream.Length} bytes)[/]");

            await using (stream)
            {
                var doc = await PlayerDocument.FromCompressedStreamAsync(stream, ct);
                AnsiConsole.MarkupLine($"[dim]Loaded {doc.FrameCount} frames[/]");
                return doc;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[dim]Animation load error: {ex.Message}[/]");
            return null;
        }
    }

    /// <summary>
    /// Fallback inline ASCII animation when .cidz file is not available.
    /// </summary>
    private static async Task PlayInlineAnimationAsync(CancellationToken ct)
    {
        var frames = new[]
        {
            @"
   ████████████████████████
   ██                    ██
   ██  ████        ████  ██
   ██  ████        ████  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            ",
            @"
   ████████████████████████
   ██                    ██
   ██  ▓▓▓▓        ▓▓▓▓  ██
   ██  ▓▓▓▓        ▓▓▓▓  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            ",
            @"
   ████████████████████████
   ██                    ██
   ██  ░░░░        ░░░░  ██
   ██  ░░░░        ░░░░  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            "
        };

        var colors = new[] { Color.Red, Color.Orange1, Color.Yellow };

        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit[/]");
        AnsiConsole.WriteLine();

        var loops = 0;
        var maxLoops = 6;
        var frameIndex = 0;

        while (!ct.IsCancellationRequested && loops < maxLoops)
        {
            var color = colors[frameIndex % colors.Length];
            var frame = frames[frameIndex % frames.Length];

            AnsiConsole.Cursor.SetPosition(0, 8);
            AnsiConsole.Write(new Text(frame, new Style(color)));

            frameIndex++;
            if (frameIndex >= frames.Length * 2)
            {
                frameIndex = 0;
                loops++;
            }

            try
            {
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
