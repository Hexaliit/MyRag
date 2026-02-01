using YamlDotNet.Serialization;

namespace DoomSummarizer.Services;

/// <summary>
/// YAML-deserializable manifest defining a documentation corpus seed URL.
/// The crawler starts at seed_url and discovers docs by following markdown links.
/// Other projects can ship their own manifest files in this format.
/// </summary>
public sealed record ManualManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; init; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "seed_url")]
    public string SeedUrl { get; init; } = "";

    [YamlMember(Alias = "path_filter")]
    public string? PathFilter { get; init; }

    [YamlMember(Alias = "max_depth")]
    public int MaxDepth { get; init; } = 3;

    [YamlMember(Alias = "max_pages")]
    public int MaxPages { get; init; } = 50;
}
