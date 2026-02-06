using LucidSupport.Models;
using LucidSupport.Services.Learning;
using LucidSupport.Services.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LucidSupport.Endpoints;

/// <summary>
///     Admin CRUD endpoints for managing page models.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        // GET /api/admin/pages — list all page models (summary)
        group.MapGet("/pages", (IPageModelStore store) =>
        {
            var summaries = store.GetAll().Select(ToSummaryDto).ToList();
            return Results.Ok(summaries);
        });

        // POST /api/admin/pages — create a new page model (from extension)
        group.MapPost("/pages", (PageCreateDto dto, IPageModelStore store, LucidSupportConfig config) =>
        {
            if (store.FindByPageId(dto.PageId) is not null)
                return Results.Conflict(new { error = $"Page '{dto.PageId}' already exists" });

            var model = new PageModel
            {
                PageId = dto.PageId,
                Title = dto.Title,
                UrlPattern = dto.UrlPattern,
                Description = dto.Description,
                Site = dto.Site,
                Learned = DateTimeOffset.UtcNow,
                Fields = dto.Fields?.Select(ToFieldDefinition).ToList() ?? [],
                Sections = dto.Sections?.Select(ToSection).ToList() ?? [],
                Conditions = dto.Conditions?.Select(ToConditionRule).ToList() ?? [],
                Topics = dto.Topics?.Select(ToTopicMapping).ToList() ?? []
            };

            var filePath = Path.Combine(config.SupportDirectory, $"{dto.PageId}.support.md");
            SupportMarkdownWriter.WriteFile(model, filePath);
            store.Add(model, filePath);

            return Results.Created($"/api/admin/pages/{dto.PageId}", ToDetailDto(model));
        });

        // GET /api/admin/pages/{pageId} — full detail
        group.MapGet("/pages/{pageId}", (string pageId, IPageModelStore store) =>
        {
            var model = store.FindByPageId(pageId);
            if (model is null)
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            return Results.Ok(ToDetailDto(model));
        });

        // PUT /api/admin/pages/{pageId} — update + write .support.md
        group.MapPut("/pages/{pageId}", (string pageId, PageUpdateDto dto, IPageModelStore store) =>
        {
            var existing = store.FindByPageId(pageId);
            if (existing is null)
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            var updated = ApplyUpdate(existing, dto);
            store.Update(updated);

            // Write back to .support.md file
            var filePath = store.GetFilePath(pageId);
            if (filePath is not null)
                SupportMarkdownWriter.WriteFile(updated, filePath);

            return Results.Ok(ToDetailDto(updated));
        });

        // DELETE /api/admin/pages/{pageId} — remove from store + delete file
        group.MapDelete("/pages/{pageId}", (string pageId, IPageModelStore store) =>
        {
            var filePath = store.GetFilePath(pageId);
            if (!store.Remove(pageId))
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            if (filePath is not null && File.Exists(filePath))
                File.Delete(filePath);

            return Results.Ok(new { deleted = pageId });
        });

        // POST /api/admin/pages/{pageId}/simulate — run TemplateResponseEngine
        group.MapPost("/pages/{pageId}/simulate",
            async (string pageId, SimulateRequestDto dto, IPageModelStore store, IResponseEngine engine, ConditionEvaluator conditionEvaluator) =>
            {
                var model = store.FindByPageId(pageId);
                if (model is null)
                    return Results.NotFound(new { error = $"Page '{pageId}' not found" });

                var context = new PageContext
                {
                    Url = model.UrlPattern,
                    VisibleFieldIds = model.Fields.Select(f => f.Selector).ToList(),
                    FieldStates = dto.FieldStates.ToDictionary(
                        kv => kv.Key,
                        kv => new FieldState
                        {
                            HasValue = kv.Value.HasValue,
                            HasError = kv.Value.HasError,
                            ErrorText = kv.Value.ErrorText,
                            HasFocus = kv.Value.HasFocus,
                            FocusCount = kv.Value.FocusCount,
                            DwellMs = kv.Value.DwellMs
                        }),
                    ViewportWidth = null,
                    Question = dto.Question
                };

                var response = await engine.GenerateResponseAsync(model, context);
                var matchedConditions = conditionEvaluator.Evaluate(model.Conditions, context);

                var debug = new List<string>();
                foreach (var mc in matchedConditions)
                    debug.Add($"Matched: {mc.When} → {mc.Suggest}");

                if (matchedConditions.Count == 0)
                    debug.Add("No conditions matched");

                return Results.Ok(new SimulateResponseDto
                {
                    Response = new HelpResponseDto
                    {
                        Text = response.Text,
                        Highlights = response.Highlights.Select(h => new HighlightTargetDto
                        {
                            Selector = h.Selector,
                            Style = h.Style
                        }).ToList(),
                        Suggestions = response.Suggestions,
                        Topics = response.Topics.Select(t => new TopicLinkDto
                        {
                            Id = t.Id,
                            Label = t.Label
                        }).ToList(),
                        FieldGuidance = response.FieldGuidance.Select(g => new FieldGuidanceDto
                        {
                            Selector = g.Selector,
                            Pattern = g.Pattern,
                            FormatHint = g.FormatHint,
                            Example = g.Example,
                            PrivacyNote = g.PrivacyNote,
                            IsProactive = g.IsProactive
                        }).ToList(),
                        Source = response.Source
                    },
                    MatchedConditions = matchedConditions.Select(c => c.When).ToList(),
                    FrustrationScore = 0, // Server-side doesn't track frustration
                    DebugInfo = debug
                });
            });

        // GET /api/admin/pages/{pageId}/export — download .support.md
        group.MapGet("/pages/{pageId}/export", (string pageId, IPageModelStore store) =>
        {
            var model = store.FindByPageId(pageId);
            if (model is null)
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            var content = SupportMarkdownWriter.Write(model);
            return Results.Text(content, "text/markdown; charset=utf-8");
        });

        // POST /api/admin/pages/{pageId}/workflow/evaluate — evaluate workflow rules
        group.MapPost("/pages/{pageId}/workflow/evaluate",
            (string pageId, WorkflowEvaluateDto dto, IPageModelStore store, WorkflowEvaluator evaluator) =>
            {
                var model = store.FindByPageId(pageId);
                if (model is null)
                    return Results.NotFound(new { error = $"Page '{pageId}' not found" });

                var fieldStates = dto.FieldStates.ToDictionary(
                    kv => kv.Key,
                    kv => new FieldWorkflowState
                    {
                        Value = kv.Value.Value,
                        IsChecked = kv.Value.IsChecked,
                        HasError = kv.Value.HasError
                    });

                var result = evaluator.Evaluate(model, fieldStates, dto.CurrentSection);

                return Results.Ok(new WorkflowResultDto
                {
                    VisibleFields = result.VisibleFields,
                    VisibleSections = result.VisibleSections,
                    Events = result.Events.Select(e => new WorkflowEventDto
                    {
                        Type = e.Type,
                        Target = e.Target,
                        Rule = e.Rule
                    }).ToList()
                });
            });
    }

    // ── Mapping helpers ─────────────────────────────────────────────

    private static PageSummaryDto ToSummaryDto(PageModel model) => new()
    {
        PageId = model.PageId,
        UrlPattern = model.UrlPattern,
        Title = model.Title,
        FieldCount = model.Fields.Count,
        ConditionCount = model.Conditions.Count,
        TopicCount = model.Topics.Count,
        SectionCount = model.Sections.Count,
        WorkflowRuleCount = model.WorkflowRules.Count,
        Flow = model.Flow,
        Step = model.Step
    };

    private static PageDetailDto ToDetailDto(PageModel model) => new()
    {
        PageId = model.PageId,
        UrlPattern = model.UrlPattern,
        Title = model.Title,
        Description = model.Description,
        Site = model.Site,
        Flow = model.Flow,
        Step = model.Step,
        Prev = model.Prev,
        Next = model.Next,
        Fields = model.Fields.Select(ToAdminFieldDto).ToList(),
        Sections = model.Sections.Select(ToAdminSectionDto).ToList(),
        Conditions = model.Conditions.Select(ToConditionRuleDto).ToList(),
        Topics = model.Topics.Select(ToTopicMappingDto).ToList(),
        WorkflowRules = model.WorkflowRules.Select(ToAdminWorkflowRuleDto).ToList(),
        Escalation = model.Escalation is not null ? ToAdminEscalationDto(model.Escalation) : null
    };

    private static PageModel ApplyUpdate(PageModel existing, PageUpdateDto dto) => existing with
    {
        Title = dto.Title ?? existing.Title,
        UrlPattern = dto.UrlPattern ?? existing.UrlPattern,
        Description = dto.Description ?? existing.Description,
        Site = dto.Site ?? existing.Site,
        Flow = dto.Flow ?? existing.Flow,
        Step = dto.Step ?? existing.Step,
        Prev = dto.Prev ?? existing.Prev,
        Next = dto.Next ?? existing.Next,
        Fields = dto.Fields?.Select(ToFieldDefinition).ToList() ?? existing.Fields,
        Sections = dto.Sections?.Select(ToSection).ToList() ?? existing.Sections,
        Conditions = dto.Conditions?.Select(ToConditionRule).ToList() ?? existing.Conditions,
        Topics = dto.Topics?.Select(ToTopicMapping).ToList() ?? existing.Topics,
        WorkflowRules = dto.WorkflowRules?.Select(ToWorkflowRule).ToList() ?? existing.WorkflowRules,
        Escalation = dto.Escalation is not null ? ToEscalationConfig(dto.Escalation) : existing.Escalation
    };

    private static FieldDefinition ToFieldDefinition(AdminFieldDto dto) => new()
    {
        Selector = dto.Selector,
        Label = dto.Label,
        Type = dto.Type,
        DisplayLabel = dto.DisplayLabel,
        Placeholder = dto.Placeholder,
        Pattern = dto.Pattern,
        Required = dto.Required,
        MinLength = dto.MinLength,
        MaxLength = dto.MaxLength,
        Autocomplete = dto.Autocomplete,
        ClientValidation = dto.ClientValidation,
        ServerValidation = dto.ServerValidation,
        Errors = dto.Errors,
        Help = dto.Help
    };

    private static AdminFieldDto ToAdminFieldDto(FieldDefinition field) => new()
    {
        Selector = field.Selector,
        Label = field.Label,
        Type = field.Type,
        DisplayLabel = field.DisplayLabel,
        Placeholder = field.Placeholder,
        Pattern = field.Pattern,
        Required = field.Required,
        MinLength = field.MinLength,
        MaxLength = field.MaxLength,
        Autocomplete = field.Autocomplete,
        ClientValidation = field.ClientValidation,
        ServerValidation = field.ServerValidation,
        Errors = field.Errors,
        Help = field.Help
    };

    private static Section ToSection(AdminSectionDto dto) => new()
    {
        Id = dto.Id,
        Label = dto.Label,
        Fields = dto.Fields,
        Order = dto.Order
    };

    private static AdminSectionDto ToAdminSectionDto(Section section) => new()
    {
        Id = section.Id,
        Label = section.Label,
        Fields = section.Fields,
        Order = section.Order
    };

    private static ConditionRule ToConditionRule(ConditionRuleDto dto) => new()
    {
        When = dto.When,
        Suggest = dto.Suggest,
        Highlight = dto.Highlight
    };

    private static ConditionRuleDto ToConditionRuleDto(ConditionRule condition) => new()
    {
        When = condition.When,
        Suggest = condition.Suggest,
        Highlight = condition.Highlight
    };

    private static TopicMapping ToTopicMapping(TopicMappingDto dto) => new()
    {
        Question = dto.Question,
        ArticleId = dto.ArticleId
    };

    private static TopicMappingDto ToTopicMappingDto(TopicMapping topic) => new()
    {
        Question = topic.Question,
        ArticleId = topic.ArticleId
    };

    private static WorkflowRule ToWorkflowRule(AdminWorkflowRuleDto dto) => new()
    {
        When = dto.When,
        Action = dto.Action,
        Target = dto.Target,
        Priority = dto.Priority
    };

    private static AdminWorkflowRuleDto ToAdminWorkflowRuleDto(WorkflowRule rule) => new()
    {
        When = rule.When,
        Action = rule.Action,
        Target = rule.Target,
        Priority = rule.Priority
    };

    private static EscalationConfig ToEscalationConfig(AdminEscalationDto dto) => new()
    {
        Plugin = dto.Plugin,
        Url = dto.Url,
        Threshold = dto.Threshold
    };

    private static AdminEscalationDto ToAdminEscalationDto(EscalationConfig config) => new()
    {
        Plugin = config.Plugin,
        Url = config.Url,
        Threshold = config.Threshold
    };
}
