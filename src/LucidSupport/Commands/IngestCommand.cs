using Spectre.Console;
using Spectre.Console.Cli;

namespace LucidSupport.Commands;

/// <summary>
///     CLI command: lucidsupport ingest [file]
///     Ingests a .support.md file into the RAG corpus.
///     (Phase 2 - stub for now)
/// </summary>
internal sealed class IngestCommand : AsyncCommand<IngestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        public string FilePath { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {settings.FilePath}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Ingestion is Phase 2 - not yet implemented.[/]");
        AnsiConsole.MarkupLine($"[blue]File parsed successfully:[/] {settings.FilePath}");

        await Task.CompletedTask;
        return 0;
    }
}
