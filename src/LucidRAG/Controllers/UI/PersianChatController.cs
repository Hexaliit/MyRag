using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LucidRAG.Authorization;

namespace LucidRAG.Controllers.UI;

[Route("chat")]
[Authorize(Roles = Roles.AllAuthenticated)]
public class PersianChatController : Controller
{
    [HttpGet("~/home")]
    public IActionResult Index()
    {
        return View();
    }
}
