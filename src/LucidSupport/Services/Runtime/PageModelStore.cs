using LucidSupport.Models;

namespace LucidSupport.Services.Runtime;

/// <summary>
///     In-memory store of loaded PageModels, indexed by URL pattern and page_id.
///     Loaded at startup from .support.md files.
/// </summary>
internal sealed class PageModelStore : IPageModelStore
{
    private readonly List<PageModel> _models = [];
    private readonly Dictionary<string, PageModel> _byPageId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _filePathByPageId = new(StringComparer.OrdinalIgnoreCase);

    public void Add(PageModel model)
    {
        _models.Add(model);
        _byPageId[model.PageId] = model;
    }

    /// <summary>Add a model with its source file path for write-back support.</summary>
    public void Add(PageModel model, string filePath)
    {
        Add(model);
        _filePathByPageId[model.PageId] = filePath;
    }

    /// <summary>Replace an existing model in-place by page_id.</summary>
    public bool Update(PageModel model)
    {
        if (!_byPageId.ContainsKey(model.PageId))
            return false;

        var index = _models.FindIndex(m => m.PageId.Equals(model.PageId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _models[index] = model;

        _byPageId[model.PageId] = model;
        return true;
    }

    /// <summary>Remove a model from the store by page_id.</summary>
    public bool Remove(string pageId)
    {
        if (!_byPageId.Remove(pageId))
            return false;

        _models.RemoveAll(m => m.PageId.Equals(pageId, StringComparison.OrdinalIgnoreCase));
        _filePathByPageId.Remove(pageId);
        return true;
    }

    /// <summary>Get the source .support.md file path for a page_id.</summary>
    public string? GetFilePath(string pageId)
        => _filePathByPageId.GetValueOrDefault(pageId);

    /// <summary>Set or update the file path for a page_id.</summary>
    public void SetFilePath(string pageId, string filePath)
        => _filePathByPageId[pageId] = filePath;

    public int Count => _models.Count;

    /// <summary>Find a page model whose UrlPattern matches the given URL path.</summary>
    public PageModel? FindByUrl(string url)
    {
        foreach (var model in _models)
        {
            if (MatchesUrlPattern(model.UrlPattern, url))
                return model;
        }

        return null;
    }

    /// <summary>Find a page model by its page_id.</summary>
    public PageModel? FindByPageId(string pageId)
        => _byPageId.GetValueOrDefault(pageId);

    /// <summary>Get all loaded models.</summary>
    public IReadOnlyList<PageModel> GetAll() => _models;

    /// <summary>
    ///     URL pattern matching. Supports trailing wildcard (*) for prefix matching.
    ///     e.g. "/demo/contact*" matches "/demo/contact.html"
    /// </summary>
    internal static bool MatchesUrlPattern(string pattern, string url)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        // Exact match
        if (pattern.Equals(url, StringComparison.OrdinalIgnoreCase))
            return true;

        // Trailing wildcard → prefix match
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
