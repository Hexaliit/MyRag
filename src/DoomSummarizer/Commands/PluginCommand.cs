using System.ComponentModel;
using DoomSummarizer.Plugins.Runtime;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     Manage runtime source and output plugins.
///     Supports shorthand names (e.g. "source-imap") and arbitrary NuGet packages.
/// </summary>
public sealed class PluginCommand : AsyncCommand<PluginCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var action = settings.Action.ToLowerInvariant();

        return action switch
        {
            "list" or "ls" => ListPlugins(),
            "install" or "add" => await InstallPlugin(settings),
            "uninstall" or "remove" or "rm" => UninstallPlugin(settings),
            "enable" => TogglePlugin(settings, true),
            "disable" => TogglePlugin(settings, false),
            "shorthands" or "names" => ListShorthands(),
            _ => ShowHelp(action)
        };
    }

    private static int ListPlugins()
    {
        // Show statically-linked processor/CLI plugins (discovered from loaded assemblies)
        var processorPlugins = PluginDiscovery.DiscoverAllProcessorPlugins();
        var cliPlugins = PluginDiscovery.DiscoverAllCliPlugins();

        if (processorPlugins.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold cyan]Processor Plugins[/]");
            var procTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[cyan]Name[/]")
                .AddColumn("[cyan]Display Name[/]")
                .AddColumn("[cyan]Extensions[/]")
                .AddColumn("[cyan]CLI[/]");

            foreach (var p in processorPlugins)
            {
                var extensions = string.Join(", ", p.Metadata.SupportedExtensions);
                var hasCli = cliPlugins.Any(c =>
                    c.GetType() == p.GetType() ||
                    c.CliMetadata.CommandName.Equals(p.Metadata.Name, StringComparison.OrdinalIgnoreCase));
                procTable.AddRow(
                    Markup.Escape(p.Metadata.Name),
                    Markup.Escape(p.Metadata.DisplayName),
                    $"[dim]{Markup.Escape(extensions)}[/]",
                    hasCli ? "[green]yes[/]" : "[grey]no[/]");
            }

            AnsiConsole.Write(procTable);
            AnsiConsole.WriteLine();
        }

        // Show runtime-installed plugins (source/output)
        var manager = new PluginManager();
        var plugins = manager.List();

        AnsiConsole.MarkupLine("[bold cyan]Runtime Plugins[/]");
        if (plugins.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No runtime plugins installed.[/]");
            AnsiConsole.MarkupLine("Install with: [cyan]doomsummarizer plugin install source-imap[/]");
            AnsiConsole.MarkupLine(
                "Or any NuGet package: [cyan]doomsummarizer plugin install Acme.CustomSource --version 1.0.0[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[cyan]Package[/]")
            .AddColumn("[cyan]Version[/]")
            .AddColumn("[cyan]Status[/]")
            .AddColumn("[cyan]Shorthand[/]")
            .AddColumn("[cyan]Installed[/]");

        foreach (var p in plugins)
        {
            var status = p.Enabled ? "[green]enabled[/]" : "[red]disabled[/]";
            var shorthand = p.Shorthand ?? "[grey]-[/]";
            table.AddRow(
                Markup.Escape(p.PackageId),
                p.Version,
                status,
                shorthand,
                p.InstalledAt.ToString("yyyy-MM-dd"));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> InstallPlugin(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Package))
        {
            AnsiConsole.MarkupLine("[red]Usage: plugin install <package> [--version <ver>][/]");
            AnsiConsole.MarkupLine(
                "[grey]Package can be a shorthand (source-imap) or full NuGet ID (Acme.CustomSource)[/]");
            return 1;
        }

        var manager = new PluginManager();
        var success = await manager.InstallAsync(
            settings.Package,
            settings.Version,
            msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));

        return success ? 0 : 1;
    }

    private static int UninstallPlugin(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Package))
        {
            AnsiConsole.MarkupLine("[red]Usage: plugin uninstall <package>[/]");
            return 1;
        }

        var manager = new PluginManager();
        var success = manager.Uninstall(
            settings.Package,
            msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));

        return success ? 0 : 1;
    }

    private static int TogglePlugin(Settings settings, bool enable)
    {
        if (string.IsNullOrEmpty(settings.Package))
        {
            AnsiConsole.MarkupLine($"[red]Usage: plugin {(enable ? "enable" : "disable")} <package>[/]");
            return 1;
        }

        var manager = new PluginManager();
        var success = enable
            ? manager.Enable(settings.Package, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"))
            : manager.Disable(settings.Package, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"));

        return success ? 0 : 1;
    }

    private static int ListShorthands()
    {
        AnsiConsole.MarkupLine("[bold cyan]Known Plugin Shorthands[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[cyan]Shorthand[/]")
            .AddColumn("[cyan]NuGet Package ID[/]");

        foreach (var (shorthand, packageId) in PluginManager.Shorthands.OrderBy(kv => kv.Key))
            table.AddRow($"[green]{Markup.Escape(shorthand)}[/]", Markup.Escape(packageId));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Install via shorthand:[/] doomsummarizer plugin install source-imap");
        AnsiConsole.MarkupLine(
            "[grey]Install arbitrary pkg:[/] doomsummarizer plugin install Acme.CustomSource --version 2.1.0");

        return 0;
    }

    private static int ShowHelp(string action)
    {
        AnsiConsole.MarkupLine($"[red]Unknown action: {Markup.Escape(action)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  plugin list                              List installed plugins");
        AnsiConsole.MarkupLine("  plugin install <package> [--version ver]  Install a plugin");
        AnsiConsole.MarkupLine("  plugin uninstall <package>               Uninstall a plugin");
        AnsiConsole.MarkupLine("  plugin enable <package>                  Enable a plugin");
        AnsiConsole.MarkupLine("  plugin disable <package>                 Disable a plugin");
        AnsiConsole.MarkupLine("  plugin shorthands                        List known shorthand names");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]<package> can be a shorthand (source-imap) or full NuGet ID (Acme.Foo)[/]");
        return 1;
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Action: install, uninstall, list, enable, disable, shorthands")]
        [CommandArgument(0, "<action>")]
        public string Action { get; set; } = "list";

        [Description("Plugin shorthand or NuGet package ID (e.g. source-imap, Acme.CustomSource)")]
        [CommandArgument(1, "[package]")]
        public string? Package { get; set; }

        [Description("Specific version to install (default: latest)")]
        [CommandOption("--version|-v")]
        public string? Version { get; set; }
    }
}