using System.ComponentModel;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     Base settings shared by all commands that support --quiet and --name.
/// </summary>
public abstract class CommonSettings : CommandSettings
{
    [CommandOption("-q|--quiet")]
    [Description("Minimal output")]
    public bool Quiet { get; init; }

    [CommandOption("-n|--name")]
    [Description("Named knowledge base collection")]
    public string? Name { get; init; }
}

/// <summary>
///     Settings for commands that process and output content (scroll, page).
///     Adds vibe, output, template, raw, no-llm, force, and no-entities options.
/// </summary>
public abstract class ContentProcessingSettings : CommonSettings
{
    [CommandOption("-v|--vibe")]
    [Description("Tone: neutral, doom, hopeful, snarky, or any custom text")]
    [DefaultValue("neutral")]
    public string Vibe { get; init; } = "neutral";

    [CommandOption("-o|--output")]
    [Description("Output file path (.md, .txt, .html, .json)")]
    public string? Output { get; init; }

    [CommandOption("-t|--template")]
    [Description("Output template")]
    [DefaultValue("default")]
    public string Template { get; init; } = "default";

    [CommandOption("--raw")]
    [Description("Show raw extracted content before processing")]
    public bool ShowRaw { get; init; }

    [CommandOption("--no-llm|--nollm")]
    [Description("Skip LLM processing — still runs embeddings, sentiment, topic inference")]
    public bool NoLlm { get; init; }

    [CommandOption("-f|--force")]
    [Description("Ignore cache and re-process")]
    public bool Force { get; init; }

    [CommandOption("--no-entities")]
    [Description("Disable NER entity extraction")]
    public bool NoEntities { get; init; }
}

/// <summary>
///     Settings for interactive Q&amp;A commands (ask, man).
/// </summary>
public abstract class InteractiveSettings : CommonSettings
{
    [CommandOption("--once")]
    [Description("Answer once and exit (no interactive loop)")]
    public bool Once { get; init; }
}