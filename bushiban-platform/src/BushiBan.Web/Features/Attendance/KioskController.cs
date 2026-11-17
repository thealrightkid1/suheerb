using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Features.Academics;
using BushiBan.Web.Security;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Attendance;

/// <summary>
/// The two self-service tablets: attendance check-in (PIN or name search) and registration forms.
/// Kiosk pages use their own full-screen layout, are never cached, and never show anyone's details beyond
/// a first name, last initial and belt.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class KioskController : Controller
{
    private readonly AppDbContext _db;
    private readonly AttendanceService _attendance;
    private readonly KioskPinThrottle _throttle;
    private readonly KioskFormService _forms;

    public KioskController(AppDbContext db, AttendanceService attendance, KioskPinThrottle throttle, KioskFormService forms)
    {
        _db = db; _attendance = attendance; _throttle = throttle; _forms = forms;
    }

    private string KioskUser => User.Identity?.Name ?? "kiosk";

    // ---------------------------------------------------------------- Attendance kiosk

    [HttpGet, HasPermission(P.Kiosk.Attendance)]
    public IActionResult Attendance() => View();

    /// <summary>"Find my name": first name, last initial and belt of matching active students.</summary>
    [HttpGet, HasPermission(P.Kiosk.Attendance)]
    public async Task<IActionResult> Search(string? q)
    {
        var term = (q ?? "").Trim();
        if (term.Length < 2) return Json(Array.Empty<KioskStudentMatch>());
        if (term.Length > 40) term = term[..40];
        var matches = await _db.Students
            .Where(s => s.IsActive && (s.FirstName.StartsWith(term) || s.LastName.StartsWith(term) || (s.FirstName + " " + s.LastName).StartsWith(term)))
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Take(12)
            .Select(s => new { s.Id, s.FirstName, s.LastName, Belt = s.Rank != null ? s.Rank.Name : null, Color = s.Rank != null ? s.Rank.Color : null })
            .ToListAsync();
        return Json(matches.Select(s => new KioskStudentMatch(s.Id, $"{s.FirstName} {Initial(s.LastName)}", s.Belt, s.Color)));
    }

    private static string Initial(string lastName) => string.IsNullOrWhiteSpace(lastName) ? "" : char.ToUpperInvariant(lastName.Trim()[0]) + ".";

    /// <summary>Checks a student in by PIN or by the id picked from "Find my name". Always answers JSON for the kiosk script.</summary>
    [HttpPost, HasPermission(P.Kiosk.Attendance)]
    public async Task<IActionResult> CheckIn(string? pin, int? studentId)
    {
        int? id;
        CheckInMethod method;
        if (!string.IsNullOrEmpty(pin))
        {
            var key = $"{User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value}|{HttpContext.Connection.RemoteIpAddress}";
            if (_throttle.RetryAfter(key) is TimeSpan wait)
            {
                return Json(new { ok = false, kind = "locked", retryAfter = (int)Math.Ceiling(wait.TotalSeconds),
                    message = "Too many wrong PINs. Please wait a minute, or tap \"Find my name\"." });
            }
            id = pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit) ? await _attendance.FindByPinAsync(pin) : null;
            if (id is null)
            {
                _throttle.RecordFailure(key);
                return Json(new { ok = false, kind = "wrong-pin", message = "We couldn't find that PIN. Please try again, or tap \"Find my name\"." });
            }
            method = CheckInMethod.KioskPin;
        }
        else if (studentId is int sid)
        {
            id = sid;
            method = CheckInMethod.KioskSearch;
        }
        else return Json(new { ok = false, kind = "invalid", message = "Enter your PIN or find your name to check in." });

        var result = await _attendance.KioskCheckInAsync(id.Value, method, KioskUser);
        return result.Status switch
        {
            CheckInStatus.CheckedIn => Json(new { ok = true, kind = "checked-in", firstName = result.FirstName, belt = result.Belt, beltColor = result.BeltColor,
                classesThisMonth = result.ClassesThisMonth, message = $"Welcome, {result.FirstName}! You're checked in." }),
            CheckInStatus.AlreadyCheckedIn => Json(new { ok = false, kind = "duplicate", firstName = result.FirstName, belt = result.Belt, beltColor = result.BeltColor,
                classesThisMonth = result.ClassesThisMonth, message = $"You're already checked in today, {result.FirstName}!" }),
            _ => Json(new { ok = false, kind = "not-found", message = "We couldn't find you. Please see the front desk." }),
        };
    }

    // ---------------------------------------------------------------- Registration kiosk

    [HttpGet, HasPermission(P.Kiosk.Registration)]
    public async Task<IActionResult> Register()
    {
        var forms = await _db.FormDefinitions.Where(f => f.IsActive && f.PublishedVersionId != null)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new KioskFormTile(f.Id, f.Name, f.Category)).ToListAsync();
        return View(forms);
    }

    [HttpGet, HasPermission(P.Kiosk.Registration)]
    public async Task<IActionResult> Form(int id)
    {
        var form = await _forms.GetPublishedAsync(id);
        if (form is null) return View("Unavailable");
        return View(await BuildView(form, null, null));
    }

    [HttpPost, HasPermission(P.Kiosk.Registration), RequestFormLimits(ValueLengthLimit = 4 * 1024 * 1024)]
    public async Task<IActionResult> Form(int id, IFormCollection form)
    {
        var published = await _forms.GetPublishedAsync(id);
        if (published is null) return View("Unavailable");

        var answers = await _forms.ReadAsync(published.Fields, form);
        if (!answers.IsValid)
        {
            return View(await BuildView(published, answers.Posted, answers.Errors));
        }
        var submission = await _forms.SubmitAsync(published, answers, HttpContext.Connection.RemoteIpAddress?.ToString(), KioskUser);
        TempData["kiosk.thanks"] = submission.FirstName ?? "";
        return RedirectToAction(nameof(Thanks));
    }

    [HttpGet, HasPermission(P.Kiosk.Registration)]
    public IActionResult Thanks()
    {
        ViewData["FirstName"] = TempData["kiosk.thanks"] as string;
        return View();
    }

    private async Task<KioskFormView> BuildView(KioskFormService.PublishedForm form, Dictionary<string, List<string>>? values, Dictionary<string, string>? errors) => new()
    {
        FormId = form.Definition.Id, Name = form.Definition.Name, Category = form.Definition.Category, Fields = form.Fields,
        Values = values ?? new(), Errors = errors ?? new(),
        Cities = form.Fields.Any(f => f.Type == "city") ? await _forms.CitiesAsync() : new(),
    };
}
