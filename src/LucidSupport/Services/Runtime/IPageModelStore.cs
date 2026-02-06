using LucidSupport.Models;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     In-memory store of loaded PageModels, indexed by URL pattern and page_id.
/// </summary>
public interface IPageModelStore
{
    /// <summary>Find a page model whose UrlPattern matches the given URL path.</summary>
    PageModel? FindByUrl(string url);

    /// <summary>Find a page model by its page_id.</summary>
    PageModel? FindByPageId(string pageId);

    /// <summary>Add a model with its source file path.</summary>
    void Add(PageModel model, string filePath);

    /// <summary>Get all loaded models.</summary>
    IReadOnlyList<PageModel> GetAll();

    /// <summary>Number of loaded models.</summary>
    int Count { get; }

    /// <summary>Replace an existing model in-place by page_id.</summary>
    bool Update(PageModel model);

    /// <summary>Remove a model from the store by page_id.</summary>
    bool Remove(string pageId);

    /// <summary>Get the source .support.md file path for a page_id.</summary>
    string? GetFilePath(string pageId);

    /// <summary>Set or update the file path for a page_id.</summary>
    void SetFilePath(string pageId, string filePath);
}
