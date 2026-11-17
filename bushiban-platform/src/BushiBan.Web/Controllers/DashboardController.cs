using BushiBan.Web.Features.Crm;
using BushiBan.Web.Security;
using Microsoft.AspNetCore.Mvc;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Controllers;

/// <summary>The home page. Tiles, charts and lists depend on what the user is allowed to see.</summary>
public class DashboardController : Controller
{
    private readonly DashboardService _dashboard;
    public DashboardController(DashboardService dashboard) => _dashboard = dashboard;

    public async Task<IActionResult> Index()
    {
        // Kiosk-only accounts belong on their kiosk, not the staff home page.
        if (!User.HasPermission(P.Students.View, P.Leads.View, P.Vouchers.View, P.Billing.View, P.Attendance.View, P.Registrations.View))
        {
            if (User.HasPermission(P.Kiosk.Attendance)) return RedirectToAction("Attendance", "Kiosk");
            if (User.HasPermission(P.Kiosk.Registration)) return RedirectToAction("Register", "Kiosk");
        }
        var model = await _dashboard.BuildAsync(User, (action, controller, values) => Url.Action(action, controller, values));
        return View(model);
    }
}
