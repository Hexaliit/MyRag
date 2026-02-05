using LucidSupport.Commands;
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
        group.MapGet("/pages", (PageModelStore store) =>
        {
            var summaries = store.GetAll().Select(ToSummaryDto).ToList();
            return Results.Ok(summaries);
        });

        // POST /api/admin/pages — create a new page model (from extension)
        group.MapPost("/pages", (PageCreateDto dto, PageModelStore store, SupportConfig config) =>
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
                Fields = dto.Fields?.Select(f => new FieldDefinition
                {
                    Selector = f.Selector,
                    Label = f.Label,
                    Type = f.Type,
                    DisplayLabel = f.DisplayLabel,
                    Placeholder = f.Placeholder,
                    Pattern = f.Pattern,
                    Required = f.Required,
                    MinLength = f.MinLength,
                    MaxLength = f.MaxLength,
                    Autocomplete = f.Autocomplete,
                    Help = f.Help
                }).ToList() ?? [],
                Sections = dto.Sections?.Select(s => new Section
                {
                    Id = s.Id,
                    Label = s.Label,
                    Fields = s.Fields,
                    Order = s.Order
                }).ToList() ?? [],
                Conditions = dto.Conditions?.Select(c => new ConditionRule
                {
                    When = c.When,
                    Suggest = c.Suggest,
                    Highlight = c.Highlight
                }).ToList() ?? [],
                Topics = dto.Topics?.Select(t => new TopicMapping
                {
                    Question = t.Question,
                    ArticleId = t.ArticleId
                }).ToList() ?? []
            };

            var filePath = Path.Combine(config.SupportDir, $"{dto.PageId}.support.md");
            SupportMarkdownWriter.WriteFile(model, filePath);
            store.Add(model, filePath);

            return Results.Created($"/api/admin/pages/{dto.PageId}", ToDetailDto(model));
        });

        // GET /api/admin/pages/{pageId} — full detail
        group.MapGet("/pages/{pageId}", (string pageId, PageModelStore store) =>
        {
            var model = store.FindByPageId(pageId);
            if (model is null)
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            return Results.Ok(ToDetailDto(model));
        });

        // PUT /api/admin/pages/{pageId} — update + write .support.md
        group.MapPut("/pages/{pageId}", (string pageId, PageUpdateDto dto, PageModelStore store) =>
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
        group.MapDelete("/pages/{pageId}", (string pageId, PageModelStore store) =>
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
            (string pageId, SimulateRequestDto dto, PageModelStore store, TemplateResponseEngine engine) =>
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
                            HasFocus = kv.Value.HasFocus
                        }),
                    ViewportWidth = null,
                    Question = dto.Question
                };

                var response = engine.GenerateResponse(model, context);
                var matchedConditions = ConditionEvaluator.Evaluate(model.Conditions, context);

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
                        Source = response.Source
                    },
                    MatchedConditions = matchedConditions.Select(c => c.When).ToList(),
                    FrustrationScore = 0, // Server-side doesn't track frustration
                    DebugInfo = debug
                });
            });

        // GET /api/admin/pages/{pageId}/export — download .support.md
        group.MapGet("/pages/{pageId}/export", (string pageId, PageModelStore store) =>
        {
            var model = store.FindByPageId(pageId);
            if (model is null)
                return Results.NotFound(new { error = $"Page '{pageId}' not found" });

            var content = SupportMarkdownWriter.Write(model);
            return Results.Text(content, "text/markdown; charset=utf-8");
        });

        // POST /api/admin/pages/{pageId}/workflow/evaluate — evaluate workflow rules
        group.MapPost("/pages/{pageId}/workflow/evaluate",
            (string pageId, WorkflowEvaluateDto dto, PageModelStore store, WorkflowEvaluator evaluator) =>
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
        Fields = model.Fields.Select(f => new AdminFieldDto
        {
            Selector = f.Selector,
            Label = f.Label,
            Type = f.Type,
            DisplayLabel = f.DisplayLabel,
            Placeholder = f.Placeholder,
            Pattern = f.Pattern,
            Required = f.Required,
            MinLength = f.MinLength,
            MaxLength = f.MaxLength,
            Autocomplete = f.Autocomplete,
            ClientValidation = f.ClientValidation,
            ServerValidation = f.ServerValidation,
            Errors = f.Errors,
            Help = f.Help
        }).ToList(),
        Sections = model.Sections.Select(s => new AdminSectionDto
        {
            Id = s.Id,
            Label = s.Label,
            Fields = s.Fields,
            Order = s.Order
        }).ToList(),
        Conditions = model.Conditions.Select(c => new ConditionRuleDto
        {
            When = c.When,
            Suggest = c.Suggest,
            Highlight = c.Highlight
        }).ToList(),
        Topics = model.Topics.Select(t => new TopicMappingDto
        {
            Question = t.Question,
            ArticleId = t.ArticleId
        }).ToList(),
        WorkflowRules = model.WorkflowRules.Select(w => new AdminWorkflowRuleDto
        {
            When = w.When,
            Action = w.Action,
            Target = w.Target,
            Priority = w.Priority
        }).ToList(),
        Escalation = model.Escalation is not null
            ? new AdminEscalationDto
            {
                Plugin = model.Escalation.Plugin,
                Url = model.Escalation.Url,
                Threshold = model.Escalation.Threshold
            }
            : null
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
        Fields = dto.Fields?.Select(f => new FieldDefinition
        {
            Selector = f.Selector,
            Label = f.Label,
            Type = f.Type,
            DisplayLabel = f.DisplayLabel,
            Placeholder = f.Placeholder,
            Pattern = f.Pattern,
            Required = f.Required,
            MinLength = f.MinLength,
            MaxLength = f.MaxLength,
            Autocomplete = f.Autocomplete,
            ClientValidation = f.ClientValidation,
            ServerValidation = f.ServerValidation,
            Errors = f.Errors,
            Help = f.Help
        }).ToList() ?? existing.Fields,
        Sections = dto.Sections?.Select(s => new Section
        {
            Id = s.Id,
            Label = s.Label,
            Fields = s.Fields,
            Order = s.Order
        }).ToList() ?? existing.Sections,
        Conditions = dto.Conditions?.Select(c => new ConditionRule
        {
            When = c.When,
            Suggest = c.Suggest,
            Highlight = c.Highlight
        }).ToList() ?? existing.Conditions,
        Topics = dto.Topics?.Select(t => new TopicMapping
        {
            Question = t.Question,
            ArticleId = t.ArticleId
        }).ToList() ?? existing.Topics,
        WorkflowRules = dto.WorkflowRules?.Select(w => new WorkflowRule
        {
            When = w.When,
            Action = w.Action,
            Target = w.Target,
            Priority = w.Priority
        }).ToList() ?? existing.WorkflowRules,
        Escalation = dto.Escalation is not null
            ? new EscalationConfig
            {
                Plugin = dto.Escalation.Plugin,
                Url = dto.Escalation.Url,
                Threshold = dto.Escalation.Threshold
            }
            : existing.Escalation
    };
}
