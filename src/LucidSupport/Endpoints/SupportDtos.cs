namespace LucidSupport.Endpoints;

/// <summary>
///     DTOs matching the TypeScript widget types. All use camelCase JSON serialization.
/// </summary>

// --- Page Model DTOs ---

public sealed record SupportPageModelDto
{
    public required string PageId { get; init; }
    public required string UrlPattern { get; init; }
    public required string Title { get; init; }
    public List<SupportFieldDto> Fields { get; init; } = [];
    public List<ConditionRuleDto> Conditions { get; init; } = [];
    public List<TopicMappingDto> Topics { get; init; } = [];
    public List<SupportSectionDto> Sections { get; init; } = [];
    public List<SupportWorkflowRuleDto> WorkflowRules { get; init; } = [];
}

public sealed record SupportSectionDto
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public List<string> Fields { get; init; } = [];
    public int Order { get; init; }
}

public sealed record SupportWorkflowRuleDto
{
    public required string When { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public int Priority { get; init; }
}

public sealed record SupportFieldDto
{
    public required string Selector { get; init; }
    public required string Label { get; init; }
    public string Type { get; init; } = "text";
    public string? Pattern { get; init; }
    public string? Help { get; init; }
}

public sealed record ConditionRuleDto
{
    public required string When { get; init; }
    public required string Suggest { get; init; }
    public string? Highlight { get; init; }
}

public sealed record TopicMappingDto
{
    public required string Question { get; init; }
    public required string ArticleId { get; init; }
}

// --- Contextual Help DTOs ---

public sealed record PageContextDto
{
    public required string Url { get; init; }
    public List<string> VisibleFieldIds { get; init; } = [];
    public Dictionary<string, FieldStateDto> FieldStates { get; init; } = new();
    public int? ViewportWidth { get; init; }
    public string? Question { get; init; }
}

public sealed record FieldStateDto
{
    public bool HasValue { get; init; }
    public bool HasError { get; init; }
    public string? ErrorText { get; init; }
    public bool HasFocus { get; init; }
}

public sealed record HelpResponseDto
{
    public required string Text { get; init; }
    public List<HighlightTargetDto> Highlights { get; init; } = [];
    public List<string> Suggestions { get; init; } = [];
    public List<TopicLinkDto> Topics { get; init; } = [];
    public string? Source { get; init; }
}

public sealed record HighlightTargetDto
{
    public required string Selector { get; init; }
    public string Style { get; init; } = "info";
}

public sealed record TopicLinkDto
{
    public required string Id { get; init; }
    public required string Label { get; init; }
}

// --- Analytics ---

public sealed record AnalyticsEventDto
{
    public string? Event { get; init; }
    public long? Ts { get; init; }
}
