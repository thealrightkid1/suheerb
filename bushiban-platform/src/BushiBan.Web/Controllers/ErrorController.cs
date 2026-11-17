using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BushiBan.Web.Controllers;

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("/Error")]
    public IActionResult Index() => View("~/Views/Shared/Error.cshtml");
}
