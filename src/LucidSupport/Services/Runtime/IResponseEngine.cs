using LucidSupport.Models;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     Generates contextual help responses given a page model and runtime context.
/// </summary>
public interface IResponseEngine
{
    /// <summary>Generate a contextual help response.</summary>
    Task<HelpResponse> GenerateResponseAsync(PageModel model, PageContext context);
}
