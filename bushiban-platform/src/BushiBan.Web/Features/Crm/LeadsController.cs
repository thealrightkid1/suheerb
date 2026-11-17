using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Crm;

[HasPermission(P.Leads.View)]
public class LeadsController : Controller
{
    private readonly AppDbContext _db;
    private readonly PeopleService _people;
    private readonly LookupService _lookups;
    private readonly BranchClock _clock;

    public LeadsController(AppDbContext db, PeopleService people, LookupService lookups, BranchClock clock)
    {
        _db = db; _people = people; _lookups = lookups; _clock = clock;
    }

    // ---------- Lists ----------

    [HttpGet]
    public async Task<IActionResult> Index(LeadFilter filter, int page = 1)
    {
        var q = PeopleService.WhereSearch(_db.Leads.AsQueryable(), filter.Q);
        if (filter.Phase is { } phase) q = q.Where(l => l.ProspectPhase == phase);
        if (filter.CourseId is int c) q = q.Where(l => l.CourseId == c);
        if (filter.Source is { } src) q = q.Where(l => l.Source == src);
        if (filter.Active == "yes") q = q.Where(l => l.IsActive);
        else if (filter.Active == "no") q = q.Where(l => !l.IsActive);
        if (filter.From is { } from) q = q.Where(l => l.InquiryDate >= from);
        if (filter.To is { } to) q = q.Where(l => l.InquiryDate <= to);

        q = filter.Sort switch
        {
            "oldest" => q.OrderBy(l => l.InquiryDate).ThenBy(l => l.Id),
            "name" => q.OrderBy(l => l.LastName).ThenBy(l => l.FirstName),
            "followup" => q.OrderBy(l => l.NextFollowUpDate == null).ThenBy(l => l.NextFollowUpDate).ThenBy(l => l.LastName),
            _ => q.OrderByDescending(l => l.InquiryDate).ThenByDescending(l => l.Id),
        };

        var rows = await q.Select(l => new LeadRow(l.Id, l.FirstName + " " + l.LastName, l.Phone1, l.Email1,
                l.City != null ? l.City.Name : null, l.DateOfBirth,
                l.Rank != null ? l.Rank.Name : null, l.Rank != null ? l.Rank.Color : null,
                l.Course != null ? l.Course.Name : null, l.InquiryDate, l.ProspectPhase, l.IsActive,
                l.ConvertedStudentId, l.NextFollowUpDate, l.Source))
            .ToPagedAsync(page);

        return View(new LeadIndexModel { Filter = filter, Rows = rows, Courses = await _lookups.CoursesAsync(filter.CourseId, activeOnly: false), Today = _clock.Today });
    }

    [HttpGet]
    public async Task<IActionResult> Board(int? courseId, string? q)
    {
        var query = PeopleService.WhereSearch(_db.Leads.Where(l => l.IsActive && l.ConvertedStudentId == null && l.ProspectPhase != ProspectPhase.Enrolled), q);
        if (courseId is int c) query = query.Where(l => l.CourseId == c);

        var counts = await query.GroupBy(l => l.ProspectPhase).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var model = new BoardModel
        {
            CourseId = courseId, Q = q, Today = _clock.Today, CanEdit = User.HasPermission(P.Leads.Edit),
            Courses = await _lookups.CoursesAsync(courseId, activeOnly: false),
        };
        foreach (var phase in Enum.GetValues<ProspectPhase>().Where(p => p != ProspectPhase.Enrolled))
        {
            // Most recent first; very long columns show a link to the full list instead of every card.
            var cards = await query.Where(l => l.ProspectPhase == phase)
                .OrderBy(l => l.NextFollowUpDate == null).ThenBy(l => l.NextFollowUpDate).ThenByDescending(l => l.InquiryDate)
                .Take(BoardModel.PerColumn)
                .Select(l => new BoardCard(l.Id, l.FirstName + " " + l.LastName, l.Course != null ? l.Course.Name : null, l.Phone1,
                    l.InquiryDate, l.NextFollowUpDate, l.ProspectPhase))
                .ToListAsync();
            model.Columns.Add((phase, counts.GetValueOrDefault(phase), cards));
        }
        return View(model);
    }

    /// <summary>Called by the pipeline board when a card is dropped on another column.</summary>
    [HttpPost, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> ChangePhase(int id, ProspectPhase phase)
    {
        if (!Enum.IsDefined(phase)) return BadRequest(new { error = "Unknown phase." });
        if (phase == ProspectPhase.Enrolled) return BadRequest(new { error = "Use \"Convert to student\" to enroll a lead." });
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound(new { error = "That lead no longer exists." });
        if (lead.ConvertedStudentId is not null) return BadRequest(new { error = "This lead is already a student." });

        if (lead.ProspectPhase != phase)
        {
            RecordPhaseChange(lead, phase);
            await _db.SaveChangesAsync();
        }
        return Json(new { ok = true, id = lead.Id, phase = (int)phase, label = phase.DisplayName() });
    }

    [HttpGet]
    public async Task<IActionResult> FollowUps()
    {
        var today = _clock.Today;
        var open = _db.Leads.Where(l => l.ConvertedStudentId == null && l.NextFollowUpDate != null && l.ProspectPhase != ProspectPhase.Enrolled && l.ProspectPhase != ProspectPhase.StopFollowingUp);
        var due = await open.Where(l => l.NextFollowUpDate <= today)
            .OrderBy(l => l.NextFollowUpDate).ThenBy(l => l.LastName)
            .Select(l => new
            {
                l.Id, Name = l.FirstName + " " + l.LastName, l.Phone1, l.Email1, Program = l.Course != null ? l.Course.Name : null,
                l.ProspectPhase, l.NextFollowUpDate, l.InquiryDate,
                LastBody = _db.Activities.Where(a => a.LeadId == l.Id && a.Type != ActivityType.System)
                    .OrderByDescending(a => a.CreatedAt).Select(a => a.Body).FirstOrDefault(),
                LastAt = _db.Activities.Where(a => a.LeadId == l.Id && a.Type != ActivityType.System)
                    .OrderByDescending(a => a.CreatedAt).Select(a => (DateTime?)a.CreatedAt).FirstOrDefault(),
            })
            .Take(300).ToListAsync();
        return View(new FollowUpsModel
        {
            Today = today, CanEdit = User.HasPermission(P.Leads.Edit),
            Due = due.Select(l => new FollowUpRow(l.Id, l.Name, l.Phone1, l.Email1, l.Program, l.ProspectPhase, l.NextFollowUpDate!.Value,
                l.InquiryDate, l.LastBody, l.LastAt)).ToList(),
            UpcomingWeek = await open.CountAsync(l => l.NextFollowUpDate > today && l.NextFollowUpDate <= today.AddDays(7)),
        });
    }

    /// <summary>Quick "log call + set next date" from the follow-up list or the lead page.</summary>
    [HttpPost, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> LogFollowUp(int id, FollowUpLog input)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        var back = input.ReturnTo == "details" ? RedirectToAction(nameof(Details), new { id }) : RedirectToAction(nameof(FollowUps));
        if (input.NextFollowUpDate is { } next && next < _clock.Today)
        {
            this.Error("The next follow-up date can't be in the past.");
            return back;
        }
        var type = ActivityInput.Choices.Contains(input.Type) && input.Type != ActivityType.FollowUp ? input.Type : ActivityType.Call;
        var body = string.IsNullOrWhiteSpace(input.Body) ? $"{type.DisplayName()} logged." : input.Body.Trim();
        body += input.NextFollowUpDate is { } n ? $" Next follow-up {Format.Date(n)}." : " No further follow-up scheduled.";
        _people.NewActivity(type, body, lead);
        lead.NextFollowUpDate = input.NextFollowUpDate;
        if (input.Phase is { } phase && phase != lead.ProspectPhase && phase != ProspectPhase.Enrolled && lead.ConvertedStudentId is null)
        {
            RecordPhaseChange(lead, phase);
            if (phase == ProspectPhase.StopFollowingUp) lead.NextFollowUpDate = null;
        }
        await _db.SaveChangesAsync();
        this.Success($"Logged for {lead.FullName}.");
        return back;
    }

    // ---------- Profile ----------

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var lead = await _db.Leads.AsNoTracking()
            .Include(l => l.Course).Include(l => l.Rank).Include(l => l.BeltSize).Include(l => l.City!).ThenInclude(c => c.State)
            .Include(l => l.ConvertedStudent)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();

        var submissions = await _db.FormSubmissions.Where(s => s.LeadId == id).OrderByDescending(s => s.SubmittedAt)
            .Select(s => new { s.Id, Form = s.FormDefinition!.Name, s.SubmittedAt, s.Status }).ToListAsync();
        return View(new LeadDetailsModel
        {
            Lead = lead,
            AssignedTo = lead.AssignedToUserId is null ? null
                : await _db.Users.Where(u => u.Id == lead.AssignedToUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefaultAsync(),
            Timeline = new TimelineModel("Leads", id, await _people.TimelineAsync(id, null), User.HasPermission(P.Leads.Edit), _clock.ToLocal, DateTime.UtcNow),
            Submissions = submissions.Select(s => (s.Id, s.Form, s.SubmittedAt, s.Status)).ToList(),
            Today = _clock.Today,
        });
    }

    [HttpPost, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> AddActivity(int id, ActivityInput input)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        if (!ActivityInput.Choices.Contains(input.Type)) input.Type = ActivityType.Note;
        if (string.IsNullOrWhiteSpace(input.Body)) { this.Error("Write something before saving."); return RedirectToAction(nameof(Details), new { id }); }

        DateTime? dueUtc = null;
        if (input.Type == ActivityType.FollowUp)
        {
            if (input.DueAt is not { } due) { this.Error("Choose when the follow-up is due."); return RedirectToAction(nameof(Details), new { id }); }
            dueUtc = _people.LocalToUtc(due);
            // Keep the follow-up list in step with the earliest open task.
            var dueDate = DateOnly.FromDateTime(due);
            if (lead.ConvertedStudentId is null && (lead.NextFollowUpDate is null || dueDate < lead.NextFollowUpDate)) lead.NextFollowUpDate = dueDate;
        }
        _people.NewActivity(input.Type, input.Body.Trim(), lead, dueAt: dueUtc);
        await _db.SaveChangesAsync();
        this.Success(input.Type == ActivityType.FollowUp ? "Follow-up task added." : "Added to the timeline.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> CompleteTask(int id, int activityId)
    {
        var task = await _db.Activities.FirstOrDefaultAsync(a => a.Id == activityId && a.LeadId == id && a.Type == ActivityType.FollowUp);
        if (task is null) return NotFound();
        task.CompletedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        this.Success("Task marked as done.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- Create / edit / delete ----------

    [HttpGet, HasPermission(P.Leads.Create)]
    public async Task<IActionResult> Create()
    {
        var form = new LeadForm { InquiryDate = _clock.Today, NextFollowUpDate = _clock.Today.AddDays(1) };
        await _people.LoadOptionsAsync(form);
        return View("Form", form);
    }

    [HttpPost, HasPermission(P.Leads.Create)]
    public async Task<IActionResult> Create(LeadForm form)
    {
        form.Id = 0;
        await _people.ValidateAsync(form, ModelState);
        if (ModelState.IsValid && !form.IgnoreDuplicates)
        {
            form.Options.Duplicates = await _people.FindDuplicatesAsync(new[] { form.Phone1, form.Phone2 }, new[] { form.Email1, form.Email2 });
        }
        if (!ModelState.IsValid || form.Options.Duplicates.Count > 0)
        {
            await _people.LoadOptionsAsync(form);
            return View("Form", form);
        }

        var lead = new Lead();
        form.ApplyTo(lead, _clock.Today);
        _db.Leads.Add(lead);
        _people.NewActivity(ActivityType.System, $"Lead created ({lead.Source.DisplayName()}).", lead);
        await _db.SaveChangesAsync();
        this.Success($"{lead.FullName} was added as a lead.");
        return RedirectToAction(nameof(Details), new { id = lead.Id });
    }

    [HttpGet, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        var form = LeadForm.From(lead);
        await _people.LoadOptionsAsync(form);
        return View("Form", form);
    }

    [HttpPost, HasPermission(P.Leads.Edit)]
    public async Task<IActionResult> Edit(int id, LeadForm form)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        form.Id = id;
        if (lead.ConvertedStudentId is not null)
        {
            // The phase select is locked once a lead is enrolled.
            form.ProspectPhase = ProspectPhase.Enrolled;
            ModelState.Remove(nameof(form.ProspectPhase));
        }
        else if (form.ProspectPhase == ProspectPhase.Enrolled && lead.ProspectPhase != ProspectPhase.Enrolled)
            ModelState.AddModelError(nameof(form.ProspectPhase), "To enroll this lead, save your changes and then use \"Convert to student\".");
        await _people.ValidateAsync(form, ModelState);
        if (!ModelState.IsValid)
        {
            await _people.LoadOptionsAsync(form);
            return View("Form", form);
        }

        var oldPhase = lead.ProspectPhase;
        form.ApplyTo(lead, _clock.Today);
        var newPhase = lead.ProspectPhase;
        if (newPhase != oldPhase) { lead.ProspectPhase = oldPhase; RecordPhaseChange(lead, newPhase); }
        await _db.SaveChangesAsync();
        this.Success("Lead saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.Leads.Delete)]
    public async Task<IActionResult> Delete(int id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        _db.Leads.Remove(lead);
        await _db.SaveChangesAsync();
        this.Success($"{lead.FullName} was deleted.");
        return RedirectToAction(nameof(Index));
    }

    // ---------- Convert to student ----------

    [HttpGet, HasPermission(P.Leads.Edit), HasPermission(P.Students.Create)]
    public async Task<IActionResult> Convert(int id)
    {
        var lead = await LoadForConvert(id);
        if (lead is null) return NotFound();
        if (lead.ConvertedStudentId is int sid)
        {
            this.Success($"{lead.FullName} is already a student.");
            return RedirectToAction("Details", "Students", new { id = sid });
        }
        var form = new ConvertForm { CourseId = lead.CourseId, RankId = lead.RankId, BeltSizeId = lead.BeltSizeId, EnrollmentDate = _clock.Today };
        if (form.RankId is null)
        {
            // Default to the first rank of the progression (usually White).
            var ranks = await _lookups.RanksAsync();
            if (ranks.Count > 0) form.RankId = int.Parse(ranks[0].Value);
        }
        return View(await ConvertModelAsync(lead, form));
    }

    [HttpPost, HasPermission(P.Leads.Edit), HasPermission(P.Students.Create), ActionName("Convert")]
    public async Task<IActionResult> ConvertPost(int id, [Bind(Prefix = "Form")] ConvertForm form)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        if (lead.ConvertedStudentId is int existing) return RedirectToAction("Details", "Students", new { id = existing });

        if (form.CourseId is int c && !await _db.Courses.AnyAsync(x => x.Id == c)) ModelState.AddModelError("Form.CourseId", "Choose a program from the list.");
        if (form.RankId is int r && !await _db.Ranks.AnyAsync(x => x.Id == r)) ModelState.AddModelError("Form.RankId", "Choose a rank from the list.");
        if (form.BeltSizeId is int b && !await _db.BeltSizes.AnyAsync(x => x.Id == b)) ModelState.AddModelError("Form.BeltSizeId", "Choose a belt size from the list.");
        if (!ModelState.IsValid)
        {
            var display = await LoadForConvert(id);
            return View("Convert", await ConvertModelAsync(display!, form));
        }

        var student = new Student();
        PeopleService.CopyPersonFields(lead, student);
        student.Id = 0;
        student.IsActive = true;
        student.CourseId = form.CourseId;
        student.RankId = form.RankId;
        student.BeltSizeId = form.BeltSizeId;
        student.EnrollmentDate = form.EnrollmentDate ?? _clock.Today;
        student.RankSince = student.EnrollmentDate;
        student.Discount = form.Discount ?? 0;
        student.AgeGroup ??= PeopleService.SuggestAgeGroup(student.DateOfBirth, _clock.Today);
        student.IsEmail1Subscriber = !string.IsNullOrEmpty(student.Email1);
        student.IsPhone1Subscriber = !string.IsNullOrEmpty(student.Phone1);

        await _people.EnrollAsync(student, lead);
        this.Success($"{student.FullName} is now student {student.StudentNo}.");
        return RedirectToAction("Details", "Students", new { id = student.Id });
    }

    private Task<Lead?> LoadForConvert(int id) => _db.Leads.AsNoTracking()
        .Include(l => l.Course).Include(l => l.Rank).Include(l => l.City!).ThenInclude(c => c.State)
        .FirstOrDefaultAsync(l => l.Id == id);

    private async Task<ConvertModel> ConvertModelAsync(Lead lead, ConvertForm form)
    {
        var missing = new List<string>();
        if (lead.DateOfBirth is null) missing.Add("date of birth");
        if (lead.Gender is null) missing.Add("gender");
        if (string.IsNullOrWhiteSpace(lead.Phone1)) missing.Add("phone number");
        if (string.IsNullOrWhiteSpace(lead.Address1) || lead.CityId is null || string.IsNullOrWhiteSpace(lead.ZipCode)) missing.Add("full address");
        if (PeopleService.Age(lead.DateOfBirth, _clock.Today) is < 18 && string.IsNullOrWhiteSpace(lead.FatherName) && string.IsNullOrWhiteSpace(lead.MotherName))
            missing.Add("parent or guardian name");
        var matches = await _people.FindDuplicatesAsync(new[] { lead.Phone1, lead.Phone2 }, new[] { lead.Email1, lead.Email2 }, excludeLeadId: lead.Id);
        return new ConvertModel
        {
            Lead = lead, Form = form, Missing = missing,
            ExistingStudents = matches.Where(m => m.Kind == "Student").ToList(),
            Courses = await _lookups.CoursesAsync(form.CourseId),
            Ranks = await _lookups.RanksAsync(form.RankId),
            BeltSizes = await _lookups.BeltSizesAsync(form.BeltSizeId),
        };
    }

    private void RecordPhaseChange(Lead lead, ProspectPhase phase)
    {
        var old = lead.ProspectPhase;
        lead.ProspectPhase = phase;
        if (phase == ProspectPhase.StopFollowingUp) lead.NextFollowUpDate = null;
        _people.NewActivity(ActivityType.StageChange, $"Phase changed from \"{old.DisplayName()}\" to \"{phase.DisplayName()}\".", lead);
    }
}
