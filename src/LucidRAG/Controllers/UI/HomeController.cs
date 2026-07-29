using Htmx;
using LucidRAG.Authorization;
using LucidRAG.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LucidRAG.Controllers.UI;

/// <summary>
///     Admin home controller - requires authentication.
///     Provides full document management, upload, and chat functionality.
/// </summary>
[Route("admin")]
[Authorize(Roles = Roles.AllAuthenticated)]
public class HomeController(
    IDocumentProcessingService documentService) : Controller
{
    [HttpGet]
    [HttpGet("~/home")] // Also accessible at /home for backwards compat
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var documents = await documentService.GetDocumentsAsync(ct: ct);

        ViewBag.Documents = documents;
        ViewBag.TotalSegments = documents.Sum(d => d.SegmentCount);
        ViewBag.UserId = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;

       // Uncomment the line below for Persian RTL view, comment out `return View()`:
        return View("Index.fa");
       // return View();
    }

    [HttpGet("documents")]
    public async Task<IActionResult> DocumentList([FromQuery] string filter = "ready", CancellationToken ct = default)
    {
        var readyOnly = filter == "ready";
        var documents = await documentService.GetDocumentsAsync(readyOnly: readyOnly, ct: ct);
        return PartialView("_DocumentList", documents);
    }

    [HttpGet("documents/{id:guid}/status-badge")]
    public async Task<IActionResult> DocumentStatusBadge(Guid id, CancellationToken ct = default)
    {
        var doc = await documentService.GetDocumentAsync(id, ct);
        if (doc is null) return NotFound();

        return PartialView("_DocumentStatusBadge", doc);
    }

    /// <summary>
    ///     Returns the File Explorer partial view for HTMX requests.
    /// </summary>
    [HttpGet("explorer")]
    public IActionResult Explorer()
    {
        if (Request.IsHtmx()) return PartialView("_FileExplorer");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    ///     Returns the Chat sidebar partial view for HTMX requests.
    /// </summary>
    [HttpGet("chat-sidebar")]
    public async Task<IActionResult> ChatSidebar(CancellationToken ct = default)
    {
        if (Request.IsHtmx())
        {
            var documents = await documentService.GetDocumentsAsync(ct: ct);
            return PartialView("_ChatSidebar", documents);
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    ///     Returns the collection selector partial view for HTMX requests.
    /// </summary>
    [HttpGet("collection-selector")]
    public IActionResult CollectionSelector()
    {
        if (Request.IsHtmx()) return PartialView("_SidebarCollectionSelector");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    ///     Returns the add content section partial view for HTMX requests.
    /// </summary>
    [HttpGet("add-content")]
    public IActionResult AddContent()
    {
        if (Request.IsHtmx()) return PartialView("_SidebarAddContent");
        return RedirectToAction(nameof(Index));
    }
}