using LucidSupport.Models;
using LucidSupport.Services.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LucidSupport.Endpoints;

/// <summary>
///     Minimal API endpoints for the support widget.
/// </summary>
internal static class SupportEndpoints
{
    public static void MapSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api");

        // GET /api/support/page?url=/demo/contact.html → SupportPageModelDto
        group.MapGet("/support/page", (string url, PageModelStore store) =>
        {
            var model = store.FindByUrl(url);
            if (model is null)
                return Results.NotFound();

            return Results.Ok(ToPageModelDto(model));
        });

        // POST /api/help/contextual → HelpResponseDto
        group.MapPost("/help/contextual", (PageContextDto dto, PageModelStore store, TemplateResponseEngine engine) =>
        {
            var model = store.FindByUrl(dto.Url);
            if (model is null)
                return Results.NotFound(new { error = "Unknown page" });

            var context = ToPageContext(dto);
            var response = engine.GenerateResponse(model, context);

            return Results.Ok(ToHelpResponseDto(response));
        });

        // POST /api/analytics → 200 OK (log to console)
        group.MapPost("/analytics", (HttpContext httpContext, ILogger<PageModelStore> logger) =>
        {
            // Fire-and-forget analytics — just log it
            // sendBeacon sends with Content-Type: text/plain, so we read as string
            logger.LogDebug("Analytics event received from {RemoteIp}",
                httpContext.Connection.RemoteIpAddress);
            return Results.Ok();
        });
    }

    // --- Mapping helpers ---

    private static SupportPageModelDto ToPageModelDto(PageModel model) => new()
    {
        PageId = model.PageId,
        UrlPattern = model.UrlPattern,
        Title = model.Title,
        Fields = model.Fields.Select(f => new SupportFieldDto
        {
            Selector = f.Selector,
            Label = f.Label,
            Type = f.Type ?? "text",
            Pattern = f.Pattern,
            Help = f.Help
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
        Sections = model.Sections.Select(s => new SupportSectionDto
        {
            Id = s.Id,
            Label = s.Label,
            Fields = s.Fields,
            Order = s.Order
        }).ToList(),
        WorkflowRules = model.WorkflowRules.Select(w => new SupportWorkflowRuleDto
        {
            When = w.When,
            Action = w.Action,
            Target = w.Target,
            Priority = w.Priority
        }).ToList()
    };

    private static PageContext ToPageContext(PageContextDto dto) => new()
    {
        Url = dto.Url,
        VisibleFieldIds = dto.VisibleFieldIds,
        FieldStates = dto.FieldStates.ToDictionary(
            kv => kv.Key,
            kv => new FieldState
            {
                HasValue = kv.Value.HasValue,
                HasError = kv.Value.HasError,
                ErrorText = kv.Value.ErrorText,
                HasFocus = kv.Value.HasFocus
            }),
        ViewportWidth = dto.ViewportWidth,
        Question = dto.Question
    };

    private static HelpResponseDto ToHelpResponseDto(HelpResponse response) => new()
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
    };
}
