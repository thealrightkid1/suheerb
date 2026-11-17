using System.Text.Json;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Crm;

public class RegistrationFilter
{
    /// <summary>"inbox" (default: everything not archived), "all", or a <see cref="SubmissionStatus"/> name.</summary>
    public string? Status { get; set; }
    public int? FormId { get; set; }
    public string? Q { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Q) || FormId is not null || From is not null || To is not null || (Status ?? "inbox") != "inbox";
}

public record RegistrationRow(int Id, string Name, string? Contact, string Form, DateTime SubmittedAt, SubmissionStatus Status,
    int? LeadId, string? LeadName, int? StudentId, string? StudentName, string? Phone, string? Email);

public class RegistrationIndexModel
{
    public RegistrationFilter Filter { get; set; } = new();
    public PagedList<RegistrationRow> Rows { get; set; } = null!;
    public List<SelectListItem> Forms { get; set; } = new();
    public Func<DateTime, DateTime> ToLocal { get; set; } = d => d;
}

public class RegistrationDetailsModel
{
    public FormSubmission Submission { get; set; } = null!;
    public string FormName { get; set; } = "";
    public int VersionNumber { get; set; }
    public List<FormField> Fields { get; set; } = new();
    public Dictionary<string, JsonElement> Data { get; set; } = new();
    public HashSet<string> SignatureKeys { get; set; } = new();
    public Dictionary<int, string> Cities { get; set; } = new();
    public (int Id, string Name)? Lead { get; set; }
    public (int Id, string Name, string No)? Student { get; set; }
    public List<PersonMatch> Matches { get; set; } = new();
    public List<SelectListItem> LinkOptions { get; set; } = new();
    public bool CanManage { get; set; }
    public DateTime SubmittedLocal { get; set; }
}

/// <summary>The inbox of forms filled in on the registration kiosk.</summary>
[HasPermission(P.Registrations.View)]
public class RegistrationsController : Controller
{
    private readonly AppDbContext _db;
    private readonly PeopleService _people;
    private readonly BranchClock _clock;

    public RegistrationsController(AppDbContext db, PeopleService people, BranchClock clock) { _db = db; _people = people; _clock = clock; }

    [HttpGet]
    public async Task<IActionResult> Index(RegistrationFilter filter, int page = 1)
    {
        var q = _db.FormSubmissions.AsQueryable();
        filter.Status ??= "inbox";
        if (filter.Status == "inbox") q = q.Where(s => s.Status != SubmissionStatus.Archived);
        else if (Enum.TryParse<SubmissionStatus>(filter.Status, true, out var st)) q = q.Where(s => s.Status == st);
        if (filter.FormId is int f) q = q.Where(s => s.FormDefinitionId == f);
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var t = filter.Q.Trim();
            var digits = SubmissionMapper.DigitsOnly(t);
            var phone = digits.Length >= 3 ? digits : "\u0001";
            q = q.Where(s => (s.FirstName + " " + s.LastName).Contains(t) || (s.LastName ?? "").Contains(t) || (s.Email ?? "").Contains(t)
                             || (s.Phone ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Contains(phone));
        }
        // Dates are typed in the branch's time zone; SubmittedAt is UTC.
        if (filter.From is { } from) { var utc = _people.LocalToUtc(from.ToDateTime(TimeOnly.MinValue)); q = q.Where(s => s.SubmittedAt >= utc); }
        if (filter.To is { } to) { var utc = _people.LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue)); q = q.Where(s => s.SubmittedAt < utc); }

        var paged = await q.OrderByDescending(s => s.SubmittedAt)
            .Select(s => new
            {
                s.Id, s.FirstName, s.LastName, Form = s.FormDefinition!.Name, s.SubmittedAt, s.Status, s.Phone, s.Email, s.DataJson, s.FormVersionId,
                s.LeadId, LeadName = s.Lead != null ? s.Lead.FirstName + " " + s.Lead.LastName : null,
                s.StudentId, StudentName = s.Student != null ? s.Student.FirstName + " " + s.Student.LastName : null,
            })
            .ToPagedAsync(page);

        // "Contact" is the parent/guardian answer, read from the form itself (one query for the versions on this page).
        var versionIds = paged.Items.Select(i => i.FormVersionId).Distinct().ToList();
        var schemas = (await _db.FormVersions.Where(v => versionIds.Contains(v.Id)).Select(v => new { v.Id, v.SchemaJson }).ToListAsync())
            .ToDictionary(v => v.Id, v => FormSchema.Parse(v.SchemaJson));
        string? Contact(int versionId, string json)
        {
            if (!schemas.TryGetValue(versionId, out var schema)) return null;
            var data = FormSchema.ParseData(json);
            return schema.Where(x => x.MapsTo is "FatherName" or "MotherName").Select(x => data.Text(x.Key)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        var rows = paged.Items.Select(i => new RegistrationRow(i.Id, $"{i.FirstName} {i.LastName}".Trim(), Contact(i.FormVersionId, i.DataJson), i.Form,
            i.SubmittedAt, i.Status, i.LeadId, i.LeadName, i.StudentId, i.StudentName, i.Phone, i.Email)).ToList();

        return View(new RegistrationIndexModel
        {
            Filter = filter, ToLocal = _clock.ToLocal,
            Rows = new PagedList<RegistrationRow>(rows, paged.Total, paged.Page, paged.PageSize),
            Forms = await _db.FormDefinitions.OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == filter.FormId)).ToListAsync(),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var sub = await _db.FormSubmissions.Include(s => s.FormDefinition).Include(s => s.FormVersion)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();

        if (sub.Status == SubmissionStatus.New)
        {
            sub.Status = SubmissionStatus.Reviewed;
            await _db.SaveChangesAsync();
        }

        var fields = FormSchema.Parse(sub.FormVersion!.SchemaJson);
        var data = FormSchema.ParseData(sub.DataJson);
        var cityIds = fields.Where(f => f.Type == "city").Select(f => int.TryParse(data.Text(f.Key), out var c) ? c : 0).Where(c => c > 0).ToList();
        var model = new RegistrationDetailsModel
        {
            Submission = sub, FormName = sub.FormDefinition!.Name, VersionNumber = sub.FormVersion.VersionNumber,
            Fields = fields, Data = data, CanManage = User.HasPermission(P.Registrations.Manage),
            SubmittedLocal = _clock.ToLocal(sub.SubmittedAt),
            SignatureKeys = (await _db.SubmissionSignatures.Where(x => x.FormSubmissionId == id).Select(x => x.FieldKey).ToListAsync()).ToHashSet(),
            Cities = await _db.Cities.Where(c => cityIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name + ", " + c.State!.Name),
        };
        if (sub.LeadId is int lid)
        {
            var l = await _db.Leads.Where(x => x.Id == lid).Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName }).FirstOrDefaultAsync();
            if (l is not null) model.Lead = (l.Id, l.Name);
        }
        if (sub.StudentId is int sid)
        {
            var s = await _db.Students.Where(x => x.Id == sid).Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName, x.StudentNo }).FirstOrDefaultAsync();
            if (s is not null) model.Student = (s.Id, s.Name, s.StudentNo);
        }
        if (model.CanManage && (sub.LeadId is null || sub.StudentId is null))
        {
            model.Matches = await _people.FindDuplicatesAsync(new[] { sub.Phone }, new[] { sub.Email });
            model.Matches.RemoveAll(m => (m.Kind == "Lead" && m.Id == sub.LeadId) || (m.Kind == "Student" && m.Id == sub.StudentId));
            model.LinkOptions = await LinkOptionsAsync();
        }
        return View(model);
    }

    /// <summary>A signature image from a submission. Branch-checked through the submission.</summary>
    [HttpGet]
    public async Task<IActionResult> Signature(int id, string key)
    {
        var png = await _db.FormSubmissions.Where(s => s.Id == id).SelectMany(s => s.Signatures)
            .Where(x => x.FieldKey == key).Select(x => x.Png).FirstOrDefaultAsync();
        if (png is null || png.Length == 0) return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(png, "image/png");
    }

    [HttpPost, HasPermission(P.Registrations.Manage), HasPermission(P.Leads.Create)]
    public async Task<IActionResult> CreateLead(int id)
    {
        var sub = await _db.FormSubmissions.Include(s => s.FormDefinition).Include(s => s.FormVersion).FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();
        if (sub.LeadId is int existing)
        {
            this.Success("This registration is already linked to a lead.");
            return RedirectToAction("Details", "Leads", new { id = existing });
        }

        var today = _clock.Today;
        var lead = new Lead
        {
            Source = LeadSource.Kiosk,
            InquiryDate = DateOnly.FromDateTime(_clock.ToLocal(sub.SubmittedAt)),
            ProspectPhase = ProspectPhase.NoAppointment,
            NextFollowUpDate = today,
            CourseId = sub.FormDefinition!.CourseId,
        };
        SubmissionMapper.ApplyTo(lead, FormSchema.Parse(sub.FormVersion!.SchemaJson), FormSchema.ParseData(sub.DataJson));
        if (string.IsNullOrWhiteSpace(lead.FirstName)) lead.FirstName = sub.FirstName ?? "Unknown";
        if (string.IsNullOrWhiteSpace(lead.LastName)) lead.LastName = sub.LastName ?? "";
        if (lead.CityId is int city && !await _db.Cities.AnyAsync(c => c.Id == city)) lead.CityId = null;
        lead.AgeGroup = PeopleService.SuggestAgeGroup(lead.DateOfBirth, today) ?? lead.AgeGroup;

        _db.Leads.Add(lead);
        sub.Lead = lead;
        sub.Status = SubmissionStatus.Linked;
        _people.NewActivity(ActivityType.System, $"Created from the kiosk registration \"{sub.FormDefinition.Name}\" submitted {Format.DateTime(_clock.ToLocal(sub.SubmittedAt))}.", lead);
        await _db.SaveChangesAsync();
        this.Success($"{lead.FullName} was added as a lead.");
        return RedirectToAction("Details", "Leads", new { id = lead.Id });
    }

    [HttpPost, HasPermission(P.Registrations.Manage), HasPermission(P.Students.Create)]
    public async Task<IActionResult> CreateStudent(int id)
    {
        var sub = await _db.FormSubmissions.Where(s => s.Id == id).Select(s => new { s.Id, s.StudentId }).FirstOrDefaultAsync();
        if (sub is null) return NotFound();
        if (sub.StudentId is int sid) return RedirectToAction("Details", "Students", new { id = sid });
        return RedirectToAction("Create", "Students", new { submissionId = id });
    }

    /// <summary>Links the registration to an existing student or lead. <paramref name="target"/> is "Student:12" or "Lead:5".</summary>
    [HttpPost, HasPermission(P.Registrations.Manage)]
    public async Task<IActionResult> Link(int id, string? target)
    {
        var sub = await _db.FormSubmissions.Include(s => s.FormDefinition).FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();
        var parts = (target ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var personId))
        {
            this.Error("Choose a student or lead to link to.");
            return RedirectToAction(nameof(Details), new { id });
        }

        var note = $"Kiosk registration \"{sub.FormDefinition!.Name}\" was linked.";
        if (parts[0] == "Student")
        {
            var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == personId);
            if (student is null) { this.Error("That student no longer exists."); return RedirectToAction(nameof(Details), new { id }); }
            sub.StudentId = student.Id;
            _people.NewActivity(ActivityType.System, note, student: student);
            this.Success($"Linked to student {student.FullName}.");
        }
        else if (parts[0] == "Lead")
        {
            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == personId);
            if (lead is null) { this.Error("That lead no longer exists."); return RedirectToAction(nameof(Details), new { id }); }
            sub.LeadId = lead.Id;
            if (lead.ConvertedStudentId is int converted && sub.StudentId is null) sub.StudentId = converted;
            _people.NewActivity(ActivityType.System, note, lead);
            this.Success($"Linked to lead {lead.FullName}.");
        }
        else
        {
            this.Error("Choose a student or lead to link to.");
            return RedirectToAction(nameof(Details), new { id });
        }
        sub.Status = SubmissionStatus.Linked;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Archives the registration, or moves an archived one back to the inbox.</summary>
    [HttpPost, HasPermission(P.Registrations.Manage)]
    public async Task<IActionResult> Archive(int id)
    {
        var sub = await _db.FormSubmissions.FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();
        if (sub.Status == SubmissionStatus.Archived)
        {
            sub.Status = sub.LeadId is not null || sub.StudentId is not null ? SubmissionStatus.Linked : SubmissionStatus.Reviewed;
            this.Success("Moved back to the inbox.");
        }
        else
        {
            sub.Status = SubmissionStatus.Archived;
            this.Success("Registration archived.");
        }
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Discards a test or duplicate submission (soft delete, an admin can restore it).</summary>
    [HttpPost, HasPermission(P.Registrations.Manage)]
    public async Task<IActionResult> Delete(int id)
    {
        var sub = await _db.FormSubmissions.FirstOrDefaultAsync(s => s.Id == id);
        if (sub is null) return NotFound();
        _db.FormSubmissions.Remove(sub);
        await _db.SaveChangesAsync();
        this.Success("Registration discarded.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<List<SelectListItem>> LinkOptionsAsync()
    {
        var students = new SelectListGroup { Name = "Students" };
        var leads = new SelectListGroup { Name = "Leads" };
        var s = await _db.Students.OrderBy(x => x.LastName).ThenBy(x => x.FirstName)
            .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName, x.StudentNo, x.Phone1 }).ToListAsync();
        var l = await _db.Leads.Where(x => x.ConvertedStudentId == null).OrderBy(x => x.LastName).ThenBy(x => x.FirstName)
            .Select(x => new { x.Id, Name = x.FirstName + " " + x.LastName, x.Phone1 }).ToListAsync();
        return s.Select(x => new SelectListItem($"{x.Name} ({x.StudentNo}){(x.Phone1 is null ? "" : " · " + x.Phone1)}", $"Student:{x.Id}") { Group = students })
            .Concat(l.Select(x => new SelectListItem($"{x.Name}{(x.Phone1 is null ? "" : " · " + x.Phone1)}", $"Lead:{x.Id}") { Group = leads }))
            .ToList();
    }
}
