using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Crm;

public record SearchHit(int Id, string Name, string? Sub, string? Phone, string? Email, string? Badge, string? BadgeColor, bool Muted);

public class SearchModel
{
    public const int Limit = 20;
    public string Q { get; set; } = "";
    public List<SearchHit>? Students { get; set; }
    public int StudentTotal { get; set; }
    public List<SearchHit>? Leads { get; set; }
    public int LeadTotal { get; set; }
    public List<SearchHit>? Registrations { get; set; }
    public int RegistrationTotal { get; set; }
    public int Total => StudentTotal + LeadTotal + RegistrationTotal;
}

/// <summary>The search box in the top bar: students, leads and kiosk registrations in one place.</summary>
[HasPermission(P.Students.View + "|" + P.Leads.View + "|" + P.Registrations.View)]
public class SearchController : Controller
{
    private readonly AppDbContext _db;
    private readonly PeopleService _people;
    private readonly BranchClock _clock;

    public SearchController(AppDbContext db, PeopleService people, BranchClock clock) { _db = db; _people = people; _clock = clock; }

    [HttpGet]
    public async Task<IActionResult> Index(string? q)
    {
        var model = new SearchModel { Q = q?.Trim() ?? "" };
        if (model.Q.Length < 2) return View(model);

        if (User.HasPermission(P.Students.View))
        {
            var students = PeopleService.WhereSearch(_db.Students.AsQueryable(), model.Q);
            model.StudentTotal = await students.CountAsync();
            model.Students = await students.OrderByDescending(s => s.IsActive).ThenBy(s => s.LastName).ThenBy(s => s.FirstName).Take(SearchModel.Limit)
                .Select(s => new SearchHit(s.Id, s.FirstName + " " + s.LastName,
                    s.StudentNo + (s.Course != null ? " · " + s.Course.Name : ""), s.Phone1, s.Email1,
                    s.Rank != null ? s.Rank.Name : null, s.Rank != null ? s.Rank.Color : null, !s.IsActive))
                .ToListAsync();
        }
        if (User.HasPermission(P.Leads.View))
        {
            var leads = PeopleService.WhereSearch(_db.Leads.AsQueryable(), model.Q);
            model.LeadTotal = await leads.CountAsync();
            var rows = await leads.OrderBy(l => l.ConvertedStudentId != null).ThenByDescending(l => l.InquiryDate).Take(SearchModel.Limit)
                .Select(l => new { l.Id, Name = l.FirstName + " " + l.LastName, Program = l.Course != null ? l.Course.Name : null, l.InquiryDate,
                    l.Phone1, l.Email1, l.ProspectPhase, l.ConvertedStudentId, l.IsActive })
                .ToListAsync();
            model.Leads = rows.Select(l => new SearchHit(l.Id, l.Name,
                $"Inquired {Infrastructure.Format.Date(l.InquiryDate)}{(l.Program is null ? "" : " · " + l.Program)}", l.Phone1, l.Email1,
                l.ConvertedStudentId is null ? Infrastructure.EnumHelpers.DisplayName(l.ProspectPhase) : "Converted to student", null,
                l.ConvertedStudentId is not null || !l.IsActive)).ToList();
        }
        if (User.HasPermission(P.Registrations.View))
        {
            var t = model.Q;
            var digits = SubmissionMapper.DigitsOnly(t);
            var phone = digits.Length >= 3 && digits.Length >= t.Count(char.IsLetterOrDigit) - 1 ? digits : "\u0001";
            var subs = _db.FormSubmissions.Where(s => (s.FirstName + " " + s.LastName).Contains(t) || (s.LastName ?? "").Contains(t)
                || (s.Email ?? "").Contains(t)
                || (s.Phone ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Contains(phone));
            model.RegistrationTotal = await subs.CountAsync();
            var rows = await subs.OrderByDescending(s => s.SubmittedAt).Take(SearchModel.Limit)
                .Select(s => new { s.Id, s.FirstName, s.LastName, Form = s.FormDefinition!.Name, s.SubmittedAt, s.Phone, s.Email, s.Status })
                .ToListAsync();
            model.Registrations = rows.Select(s => new SearchHit(s.Id, $"{s.FirstName} {s.LastName}".Trim(),
                $"{s.Form} · {Infrastructure.Format.Date(_clock.ToLocal(s.SubmittedAt))}", s.Phone, s.Email,
                Infrastructure.EnumHelpers.DisplayName(s.Status), null, s.Status == SubmissionStatus.Archived)).ToList();
        }
        return View(model);
    }

    /// <summary>Live "possible duplicate" check used by the lead and student editors.</summary>
    [HttpGet, HasPermission(P.Students.View + "|" + P.Leads.View)]
    public async Task<IActionResult> Duplicates(string? phone, string? email, int? leadId, int? studentId)
    {
        var matches = await _people.FindDuplicatesAsync(new[] { phone }, new[] { email }, leadId, studentId);
        if (!User.HasPermission(P.Students.View)) matches.RemoveAll(m => m.Kind == "Student");
        if (!User.HasPermission(P.Leads.View)) matches.RemoveAll(m => m.Kind == "Lead");
        return Json(matches.Select(m => new
        {
            m.Kind, m.Name, m.Detail,
            url = Url.Action("Details", m.Kind == "Student" ? "Students" : "Leads", new { id = m.Id }),
        }));
    }
}
