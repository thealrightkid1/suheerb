using Microsoft.AspNetCore.Mvc;

namespace BushiBan.Web.Controllers;

public class DashboardController : Controller
{
    public IActionResult Index() => View();
}
