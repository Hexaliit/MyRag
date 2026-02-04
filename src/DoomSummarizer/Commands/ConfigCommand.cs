using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class ConfigCommand : AsyncCommand<ConfigCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--init")]
        [Description("Create config file (minimal starter; use --full for all settings)")]
        public bool Init { get; init; }

        [CommandOption("--full")]
        [Description("With --init: generate a complete config.json with every setting")]
        public bool Full { get; init; }

        [CommandOption("--show")]
        [Description("Show current configuration with load sources and overrides")]
        public bool Show { get; init; }

        [CommandOption("--reference")]
        [Description("Print the full YAML configuration reference to stdout")]
        public bool Reference { get; init; }

        [CommandOption("--profile")]
        [Description("Apply a hardware profile (pi, laptop, desktop, server, enterprise, dynamic)")]
        public string? Profile { get; init; }

        [CommandOption("--list-profiles")]
        [Description("Show available hardware profiles with descriptions")]
        public bool ListProfiles { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Reference)
        {
            ShowReference();
            return 0;
        }

        if (settings.ListProfiles)
        {
            ShowProfiles();
            return 0;
        }

        if (settings.Init)
        {
            if (settings.Full)
            {
                await ConfigService.InitializeFullConfigAsync();
                AnsiConsole.MarkupLine($"[green]Created full config:[/] {FormattingHelpers.Esc(ConfigService.GetConfigDir())}/config.json");
            }
            else
            {
                await ConfigService.InitializeStarterConfigAsync();
                AnsiConsole.MarkupLine($"[green]Created starter config:[/] {FormattingHelpers.Esc(ConfigService.GetConfigDir())}/config.json");
                AnsiConsole.MarkupLine("[grey]Only commonly-changed settings included. Run 'config --init --full' for all settings.[/]");
                AnsiConsole.MarkupLine("[grey]Run 'config --reference' for the complete setting reference.[/]");
            }
            return 0;
        }

        if (!string.IsNullOrEmpty(settings.Profile))
        {
            return await ApplyProfileAsync(settings);
        }

        var config = await ConfigService.LoadAsync();

        if (settings.Show || !settings.Init)
        {
            ShowConfig(config);
        }

        return 0;
    }

    private static async Task<int> ApplyProfileAsync(Settings settings)
    {
        var profileName = settings.Profile!;

        if (!ProfileService.IsValidProfile(profileName))
        {
            AnsiConsole.MarkupLine($"[red]Unknown profile:[/] {Markup.Escape(profileName)}");
            AnsiConsole.MarkupLine("[grey]Valid profiles: pi, laptop, desktop, server, enterprise, dynamic[/]");
            return 1;
        }

        var resolvedName = profileName;
        if (profileName == "dynamic")
        {
            var hw = await HardwareDetector.DetectAsync();
            resolvedName = HardwareDetector.ResolveProfile(hw);
            AnsiConsole.MarkupLine($"[cyan]Hardware detected:[/] {hw.TotalRamGb:F1} GB RAM, {hw.CpuCores} cores, GPU: {(hw.HasGpu ? "yes" : "no")}, Ollama: {(hw.OllamaAvailable ? "yes" : "no")}");
            AnsiConsole.MarkupLine($"[cyan]Resolved profile:[/] [bold]{resolvedName}[/]");
        }

        if (settings.Show)
        {
            // Dry-run: show what the profile would change
            AnsiConsole.MarkupLine($"[cyan]Preview of profile:[/] [bold]{resolvedName}[/]");
            AnsiConsole.WriteLine();

            DoomConfig previewConfig;
            try
            {
                ConfigService.RequestedProfile = resolvedName;
                previewConfig = await ConfigService.LoadAsync();
            }
            finally
            {
                ConfigService.RequestedProfile = null;
            }
            ShowConfig(previewConfig);
            AnsiConsole.MarkupLine("[grey]This is a preview. Remove --show to apply.[/]");
            return 0;
        }

        // Apply: set the profile in user config and reload
        DoomConfig config;
        try
        {
            ConfigService.RequestedProfile = resolvedName;
            config = await ConfigService.LoadAsync();
        }
        finally
        {
            ConfigService.RequestedProfile = null;
        }

        // Persist the profile name in config
        var updatedConfig = config with { Profile = resolvedName };
        await ConfigService.SaveAsync(updatedConfig);

        var desc = ProfileService.ProfileDescriptions.TryGetValue(resolvedName, out var d) ? d.Description : "";
        AnsiConsole.MarkupLine($"[green]Applied profile:[/] [bold]{resolvedName}[/]");
        if (!string.IsNullOrEmpty(desc))
            AnsiConsole.MarkupLine($"[grey]{desc}[/]");
        AnsiConsole.MarkupLine($"[grey]Config saved to {FormattingHelpers.Esc(ConfigService.GetConfigDir())}/config.json[/]");

#if !FEATURE_LLAMASHARP
        // Warn if the profile configured LLamaSharp settings but this build doesn't include it
        var ls = config.LlamaSharp;
        if (ls.Enabled != false && (ls.ContextSize != null || ls.GpuLayerCount != null || ls.SynthesisModel != null))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Note:[/] This profile configures local GGUF inference (LLamaSharp), which is not included in this build.");
            AnsiConsole.MarkupLine("  LLamaSharp settings will be ignored. LLM providers: Ollama, cloud APIs.");
            AnsiConsole.MarkupLine("  For local GGUF support, use [bold cyan]lucidrag[/] (the complete build).");
        }
#endif

        return 0;
    }

    private static void ShowProfiles()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Hardware Profiles[/]")
            .AddColumn("[bold]Profile[/]")
            .AddColumn("[bold]Target[/]")
            .AddColumn("[bold]Description[/]");

        foreach (var (name, (target, description)) in ProfileService.ProfileDescriptions)
        {
            var nameMarkup = name == "dynamic" ? $"[cyan]{name}[/]" : name;
            table.AddRow(nameMarkup, target, description);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Usage: doomsummarizer config --profile <name>[/]");
        AnsiConsole.MarkupLine("[grey]Preview: doomsummarizer config --profile <name> --show[/]");
    }

    private static void ShowReference()
    {
        var yaml = ConfigService.LoadYamlDefaultsText();
        if (yaml == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not load embedded YAML reference.");
            return;
        }

        AnsiConsole.Write(new Rule("[bold cyan]Configuration Reference (default-config.yaml)[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(yaml);
    }

    private static void ShowConfig(DoomConfig config)
    {
        AnsiConsole.Write(new Rule("[bold cyan]DoomSummarizer Configuration[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Show load sources
        ShowLoadSources();

        // Show overrides from defaults
        ShowOverrides(config);

        // Sources
        var sourcesTree = new Tree("[bold]Sources[/]");

        var hnNode = sourcesTree.AddNode(config.Sources.HackerNews.Enabled
            ? "[green]>[/] Hacker News"
            : "[grey]x[/] Hacker News");
        hnNode.AddNode($"Sections: {string.Join(", ", config.Sources.HackerNews.Sections)}");
        hnNode.AddNode($"Max stories: {config.Sources.HackerNews.MaxStories}");
        hnNode.AddNode($"Min score: {config.Sources.HackerNews.MinScore}");

        var redditNode = sourcesTree.AddNode(config.Sources.Reddit.Enabled
            ? "[green]>[/] Reddit"
            : "[grey]x[/] Reddit");
        redditNode.AddNode($"Subreddits: {string.Join(", ", config.Sources.Reddit.Subreddits)}");
        redditNode.AddNode($"Sort: {config.Sources.Reddit.Sort}");
        redditNode.AddNode($"Max posts: {config.Sources.Reddit.MaxPosts}");
        redditNode.AddNode($"Min score: {config.Sources.Reddit.MinScore}");

        if (config.Sources.Websites.Count > 0)
        {
            var webNode = sourcesTree.AddNode("[green]>[/] Custom Websites");
            foreach (var site in config.Sources.Websites)
            {
                webNode.AddNode($"{site.Url} {(site.UsePlaywright ? "[grey](Playwright)[/]" : "")}");
            }
        }

        AnsiConsole.Write(sourcesTree);
        AnsiConsole.WriteLine();

        // Ollama
        var ollamaTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Ollama Configuration[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        ollamaTable.AddRow("Base URL", config.Ollama.BaseUrl);
        ollamaTable.AddRow("Model", config.Ollama.Model);
        ollamaTable.AddRow("Embed Model", config.Ollama.EmbedModel);
        ollamaTable.AddRow("Temperature", config.Ollama.Temperature.ToString("F1"));
        ollamaTable.AddRow("Timeout", $"{config.Ollama.TimeoutSeconds}s");

        AnsiConsole.Write(ollamaTable);
        AnsiConsole.WriteLine();

        // Embedding
        var embedTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Embedding Configuration[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        embedTable.AddRow("Backend", config.Embedding.Backend);
        embedTable.AddRow("Model", config.Embedding.Model);
        embedTable.AddRow("Similarity Threshold", config.Embedding.SimilarityThreshold.ToString("F2"));

        AnsiConsole.Write(embedTable);
        AnsiConsole.WriteLine();

        // Vibes
        AnsiConsole.Write(new Rule("[bold]Available Vibes[/]").RuleStyle("grey").LeftJustified());

        foreach (var (name, prompt) in config.Vibes)
        {
            var color = name switch
            {
                "doom" => "red",
                "hopeful" => "green",
                "snarky" => "yellow",
                _ => "grey"
            };
            AnsiConsole.MarkupLine($"  [{color}]{name}[/]: [grey]{FormattingHelpers.Truncate(prompt, 60)}[/]");
        }

        AnsiConsole.WriteLine();

        // Storage
        var storagePath = ConfigService.GetDbPath(config);
        AnsiConsole.MarkupLine($"[bold]Storage:[/] {FormattingHelpers.Esc(storagePath)}");
        AnsiConsole.MarkupLine($"[bold]Retention:[/] {config.Storage.RetentionDays} days");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Edit config: ~/.doomsummarizer/config.json[/]");
        AnsiConsole.MarkupLine("[grey]Full reference: doomsummarizer config --reference[/]");
    }

    private static void ShowLoadSources()
    {
        var sources = ConfigService.LoadedSources;
        if (sources.Count == 0) return;

        AnsiConsole.Write(new Rule("[bold]Config Sources (load order)[/]").RuleStyle("grey").LeftJustified());
        for (var i = 0; i < sources.Count; i++)
        {
            var marker = i == 0 ? "[blue]base[/]" : "[yellow]override[/]";
            AnsiConsole.MarkupLine($"  {i + 1}. {marker} {Markup.Escape(sources[i])}");
        }
        AnsiConsole.WriteLine();
    }

    private static void ShowOverrides(DoomConfig config)
    {
        var defaults = ConfigService.LoadYamlDefaults();
        var overrides = ConfigService.GetOverrides(defaults, config);

        if (overrides.Count == 0) return;

        AnsiConsole.Write(new Rule("[bold]Values overridden from defaults[/]").RuleStyle("grey").LeftJustified());
        foreach (var (path, value) in overrides)
        {
            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(path)}[/] = [green]{Markup.Escape(value)}[/]");
        }
        AnsiConsole.WriteLine();
    }
}
