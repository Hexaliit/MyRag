using YamlDotNet.Serialization;

namespace DoomSummarizer.Models;

/// <summary>
///     YAML-driven source definitions and topic-based routing.
///     Loaded from Resources/sources.yaml.
/// </summary>
public class SourceRoutingConfig
{
    [YamlMember(Alias = "sources")] public Dictionary<string, SourceDefinition> Sources { get; set; } = new();

    [YamlMember(Alias = "routing")] public Dictionary<string, RoutingRule> Routing { get; set; } = new();

    [YamlMember(Alias = "topic_keywords")] public Dictionary<string, List<string>> TopicKeywords { get; set; } = new();
}

public class SourceDefinition
{
    [YamlMember(Alias = "type")] public string Type { get; set; } = "";

    [YamlMember(Alias = "description")] public string Description { get; set; } = "";

    [YamlMember(Alias = "search")] public bool Search { get; set; }

    [YamlMember(Alias = "feeds")] public Dictionary<string, List<string>>? Feeds { get; set; }

    /// <summary>
    ///     Topics/categories this source covers (e.g., ["technology", "science", "business"]).
    ///     Used for smarter source routing and display.
    /// </summary>
    [YamlMember(Alias = "scope")]
    public List<string>? Scope { get; set; }

    /// <summary>
    ///     Geographic region this source covers (e.g., "UK", "US", "global").
    /// </summary>
    [YamlMember(Alias = "region")]
    public string? Region { get; set; }
}

public class RoutingRule
{
    [YamlMember(Alias = "sources")] public List<string> Sources { get; set; } = [];

    [YamlMember(Alias = "bbc_category")] public string? BbcCategory { get; set; }

    [YamlMember(Alias = "google_news_topic")]
    public string? GoogleNewsTopic { get; set; }
}