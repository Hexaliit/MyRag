using YamlDotNet.Serialization;

namespace DoomSummarizer.Services;

/// <summary>
/// YAML-deserializable manifest defining a documentation corpus to fetch and index.
/// Exportable format — other projects can ship their own manifest files.
/// </summary>
public sealed record ManualManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; init; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "base_url")]
    public string BaseUrl { get; init; } = "";

    [YamlMember(Alias = "documents")]
    public List<ManualDocument> Documents { get; init; } = [];
}

public sealed record ManualDocument
{
    [YamlMember(Alias = "path")]
    public string Path { get; init; } = "";

    [YamlMember(Alias = "title")]
    public string Title { get; init; } = "";

    [YamlMember(Alias = "tags")]
    public List<string> Tags { get; init; } = [];
}
