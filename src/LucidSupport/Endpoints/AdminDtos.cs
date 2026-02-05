namespace LucidSupport.Endpoints;

// ── Page list / summary ─────────────────────────────────────────────

public sealed record PageSummaryDto
{
    public required string PageId { get; init; }
    public required string UrlPattern { get; init; }
    public required string Title { get; init; }
    public int FieldCount { get; init; }
    public int ConditionCount { get; init; }
    public int TopicCount { get; init; }
    public int SectionCount { get; init; }
    public int WorkflowRuleCount { get; init; }
    public string? Flow { get; init; }
    public int? Step { get; init; }
}

// ── Full page detail ────────────────────────────────────────────────

public sealed record PageDetailDto
{
    public required string PageId { get; init; }
    public required string UrlPattern { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? Site { get; init; }
    public string? Flow { get; init; }
    public int? Step { get; init; }
    public string? Prev { get; init; }
    public string? Next { get; init; }
    public List<AdminFieldDto> Fields { get; init; } = [];
    public List<AdminSectionDto> Sections { get; init; } = [];
    public List<ConditionRuleDto> Conditions { get; init; } = [];
    public List<TopicMappingDto> Topics { get; init; } = [];
    public List<AdminWorkflowRuleDto> WorkflowRules { get; init; } = [];
    public AdminEscalationDto? Escalation { get; init; }
}

public sealed record AdminFieldDto
{
    public required string Selector { get; init; }
    public required string Label { get; init; }
    public string? Type { get; init; }
    public string? DisplayLabel { get; init; }
    public string? Placeholder { get; init; }
    public string? Pattern { get; init; }
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Autocomplete { get; init; }
    public List<string> ClientValidation { get; init; } = [];
    public List<string> ServerValidation { get; init; } = [];
    public Dictionary<string, string> Errors { get; init; } = new();
    public string? Help { get; init; }
}

public sealed record AdminSectionDto
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public List<string> Fields { get; init; } = [];
    public int Order { get; init; }
}

public sealed record AdminWorkflowRuleDto
{
    public required string When { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public int Priority { get; init; }
}

public sealed record AdminEscalationDto
{
    public string Plugin { get; init; } = "console";
    public string? Url { get; init; }
    public double Threshold { get; init; } = 8.0;
}

// ── Create request (from extension) ───────────────────────────────

public sealed record PageCreateDto
{
    public required string PageId { get; init; }
    public required string Title { get; init; }
    public required string UrlPattern { get; init; }
    public string? Description { get; init; }
    public string? Site { get; init; }
    public List<AdminFieldDto>? Fields { get; init; }
    public List<AdminSectionDto>? Sections { get; init; }
    public List<ConditionRuleDto>? Conditions { get; init; }
    public List<TopicMappingDto>? Topics { get; init; }
}

// ── Update request ─────────────────────────────────────────────────

public sealed record PageUpdateDto
{
    public string? Title { get; init; }
    public string? UrlPattern { get; init; }
    public string? Description { get; init; }
    public string? Site { get; init; }
    public string? Flow { get; init; }
    public int? Step { get; init; }
    public string? Prev { get; init; }
    public string? Next { get; init; }
    public List<AdminFieldDto>? Fields { get; init; }
    public List<AdminSectionDto>? Sections { get; init; }
    public List<ConditionRuleDto>? Conditions { get; init; }
    public List<TopicMappingDto>? Topics { get; init; }
    public List<AdminWorkflowRuleDto>? WorkflowRules { get; init; }
    public AdminEscalationDto? Escalation { get; init; }
}

// ── Simulator request/response ─────────────────────────────────────

public sealed record SimulateRequestDto
{
    public Dictionary<string, FieldStateDto> FieldStates { get; init; } = new();
    public int IdleSeconds { get; init; }
    public int SubmitAttempts { get; init; }
    public string? Question { get; init; }
}

public sealed record SimulateResponseDto
{
    public required HelpResponseDto Response { get; init; }
    public List<string> MatchedConditions { get; init; } = [];
    public double FrustrationScore { get; init; }
    public List<string> DebugInfo { get; init; } = [];
}

// ── Workflow evaluation request/response ────────────────────────────

public sealed record WorkflowEvaluateDto
{
    public Dictionary<string, WorkflowFieldStateDto> FieldStates { get; init; } = new();
    public string? CurrentSection { get; init; }
}

public sealed record WorkflowFieldStateDto
{
    public string? Value { get; init; }
    public bool IsChecked { get; init; }
    public bool HasError { get; init; }
}

public sealed record WorkflowResultDto
{
    public List<string> VisibleFields { get; init; } = [];
    public List<string> VisibleSections { get; init; } = [];
    public List<WorkflowEventDto> Events { get; init; } = [];
}

public sealed record WorkflowEventDto
{
    public required string Type { get; init; }
    public required string Target { get; init; }
    public required string Rule { get; init; }
}
