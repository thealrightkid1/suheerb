using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Crm;

[HasPermission(P.Students.View)]
public class StudentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly PeopleService _people;
    private readonly LookupService _lookups;
    private readonly BranchClock _clock;

    public StudentsController(AppDbContext db, PeopleService people, LookupService lookups, BranchClock clock)
    {
        _db = db; _people = people; _lookups = lookups; _clock = clock;
    }

    // ---------- List ----------

    [HttpGet]
    public async Task<IActionResult> Index(StudentFilter filter, int page = 1)
    {
        var q = _db.Students.AsQueryable();
        q = PeopleService.WhereSearch(q, filter.Q);
        if (filter.CourseId is int c) q = q.Where(s => s.CourseId == c);
        if (filter.RankId is int r) q = q.Where(s => s.RankId == r);
        if (filter.AgeGroup is { } g) q = q.Where(s => s.AgeGroup == g);
        filter.Active ??= "yes";
        if (filter.Active == "yes") q = q.Where(s => s.IsActive);
        else if (filter.Active == "no") q = q.Where(s => !s.IsActive);

        q = filter.Sort switch
        {
            "number" => q.OrderBy(s => s.StudentNo),
            "newest" => q.OrderByDescending(s => s.EnrollmentDate).ThenByDescending(s => s.Id),
            _ => q.OrderBy(s => s.LastName).ThenBy(s => s.FirstName),
        };

        var rows = await q.Select(s => new StudentRow(s.Id, s.StudentNo, s.FirstName + " " + s.LastName, s.Phone1, s.Email1,
                s.City != null ? s.City.Name : null, s.DateOfBirth,
                s.Rank != null ? s.Rank.Name : null, s.Rank != null ? s.Rank.Color : null,
                s.Course != null ? s.Course.Name : null, s.EnrollmentDate, s.IsActive))
            .ToPagedAsync(page);

        return View(new StudentIndexModel
        {
            Filter = filter, Rows = rows, Today = _clock.Today,
            Courses = await _lookups.CoursesAsync(filter.CourseId, activeOnly: false),
            Ranks = await _lookups.RanksAsync(filter.RankId),
        });
    }

    // ---------- Profile ----------

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var student = await _db.Students.AsNoTracking()
            .Include(s => s.Course!).ThenInclude(c => c.FeeStructure)
            .Include(s => s.Rank).Include(s => s.BeltSize).Include(s => s.City!).ThenInclude(c => c.State)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();

        var today = _clock.Today;
        var fee = student.Course?.FeeStructure;
        var model = new StudentDetailsModel
        {
            Student = student, Today = today,
            Fees = fee is null ? null : new ProgramFees(fee.Name, fee.AdmissionFee, fee.SecurityFee, fee.MonthlyFee),
            Timeline = new TimelineModel("Students", id, await _people.TimelineAsync(null, id), User.HasPermission(P.Students.Edit), _clock.ToLocal, DateTime.UtcNow),
        };

        model.ShowAttendance = User.HasPermission(P.Attendance.View, P.Attendance.Create);
        if (model.ShowAttendance)
        {
            var attendance = _db.AttendanceRecords.Where(a => a.StudentId == id);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            model.CheckIns = await attendance.OrderByDescending(a => a.Date).ThenByDescending(a => a.CheckInTime).Take(10)
                .Select(a => new CheckInRow(a.Date, a.CheckInTime, a.Course != null ? a.Course.Name : null, a.Method, a.MarkedBy)).ToListAsync();
            model.CheckInsThisMonth = await attendance.CountAsync(a => a.Date >= monthStart && a.Date <= today);
            model.CheckInsLast30 = await attendance.CountAsync(a => a.Date > today.AddDays(-30) && a.Date <= today);
            model.LastSeen = model.CheckIns.FirstOrDefault()?.Date;
        }

        model.Promotions = await _db.RankPromotions.Where(p => p.StudentId == id).OrderByDescending(p => p.PromotedOn).ThenByDescending(p => p.Id)
            .Select(p => new PromotionRow(p.PromotedOn, p.FromRank != null ? p.FromRank.Name : null, p.FromRank != null ? p.FromRank.Color : null,
                p.ToRank!.Name, p.ToRank.Color, p.Notes))
            .ToListAsync();

        model.ShowBilling = User.HasPermission(P.Billing.View);
        if (model.ShowBilling)
        {
            var open = _db.Invoices.Where(i => i.StudentId == id && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid));
            var balances = await open.Select(i => new
            {
                i.DueDate,
                Total = i.Lines.Sum(l => l.Amount),
                Paid = i.Allocations.Sum(a => a.Amount),
            }).ToListAsync();
            model.OpenInvoices = balances.Count;
            model.OpenBalance = balances.Sum(b => b.Total - b.Paid);
            model.OverdueBalance = balances.Where(b => b.DueDate < today).Sum(b => b.Total - b.Paid);
        }

        model.ShowMessages = User.HasPermission(P.Communications.View);
        if (model.ShowMessages)
        {
            model.Messages = await _db.MessageLogs.Where(m => m.StudentId == id).OrderByDescending(m => m.CreatedAt).Take(10)
                .Select(m => new MessageRow(m.CreatedAt, m.Channel, m.To, m.Subject, m.Status)).ToListAsync();
        }

        if (student.LeadId is int leadId)
        {
            var lead = await _db.Leads.Where(l => l.Id == leadId).Select(l => new { l.Id, Name = l.FirstName + " " + l.LastName }).FirstOrDefaultAsync();
            if (lead is not null) model.FromLead = (lead.Id, lead.Name);
        }
        model.Submissions = (await _db.FormSubmissions.Where(s => s.StudentId == id).OrderByDescending(s => s.SubmittedAt)
                .Select(s => new { s.Id, Form = s.FormDefinition!.Name, s.SubmittedAt }).ToListAsync())
            .Select(s => (s.Id, s.Form, s.SubmittedAt)).ToList();
        return View(model);
    }

    [HttpPost, HasPermission(P.Students.Edit)]
    public async Task<IActionResult> AddActivity(int id, ActivityInput input)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();
        if (!ActivityInput.Choices.Contains(input.Type)) input.Type = ActivityType.Note;
        if (string.IsNullOrWhiteSpace(input.Body)) { this.Error("Write something before saving."); return RedirectToAction(nameof(Details), new { id }); }
        DateTime? dueUtc = null;
        if (input.Type == ActivityType.FollowUp)
        {
            if (input.DueAt is not { } due) { this.Error("Choose when the follow-up is due."); return RedirectToAction(nameof(Details), new { id }); }
            dueUtc = _people.LocalToUtc(due);
        }
        _people.NewActivity(input.Type, input.Body.Trim(), student: student, dueAt: dueUtc);
        await _db.SaveChangesAsync();
        this.Success(input.Type == ActivityType.FollowUp ? "Follow-up task added." : "Added to the timeline.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.Students.Edit)]
    public async Task<IActionResult> CompleteTask(int id, int activityId)
    {
        var task = await _db.Activities.FirstOrDefaultAsync(a => a.Id == activityId && a.StudentId == id && a.Type == ActivityType.FollowUp);
        if (task is null) return NotFound();
        task.CompletedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        this.Success("Task marked as done.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- Create / edit / delete ----------

    [HttpGet, HasPermission(P.Students.Create)]
    public async Task<IActionResult> Create(int? leadId, int? submissionId)
    {
        var today = _clock.Today;
        var form = new StudentForm { EnrollmentDate = today };
        var draft = new Student { EnrollmentDate = today };

        if (leadId is int lid)
        {
            var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid);
            if (lead is null) return NotFound();
            if (lead.ConvertedStudentId is int sid) { this.Success($"{lead.FullName} is already a student."); return RedirectToAction(nameof(Details), new { id = sid }); }
            PeopleService.CopyPersonFields(lead, draft);
            form.LeadId = lid;
            form.Options.PrefilledFrom = $"lead {lead.FullName}";
        }
        if (submissionId is int subId)
        {
            var sub = await _db.FormSubmissions.AsNoTracking().Include(s => s.FormVersion).Include(s => s.FormDefinition)
                .FirstOrDefaultAsync(s => s.Id == subId);
            if (sub is null) return NotFound();
            if (sub.StudentId is int sid) { this.Success("That registration is already linked to a student."); return RedirectToAction(nameof(Details), new { id = sid }); }
            SubmissionMapper.ApplyTo(draft, FormSchema.Parse(sub.FormVersion!.SchemaJson), FormSchema.ParseData(sub.DataJson));
            draft.CourseId ??= sub.FormDefinition!.CourseId;
            form.SubmissionId = subId;
            if (sub.LeadId is int subLead && form.LeadId is null)
            {
                var linked = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == subLead && l.ConvertedStudentId == null);
                if (linked is not null) form.LeadId = linked.Id;
            }
            form.Options.PrefilledFrom = $"the kiosk registration \"{sub.FormDefinition!.Name}\"";
        }

        form.CopyFrom(draft);
        form.Id = 0;
        form.IsActive = true;
        form.AgeGroup ??= PeopleService.SuggestAgeGroup(form.DateOfBirth, today);
        if (form.RankId is null)
        {
            var ranks = await _lookups.RanksAsync();
            if (ranks.Count > 0) form.RankId = int.Parse(ranks[0].Value);
        }
        form.IsEmail1Subscriber = true;
        form.IsPhone1Subscriber = true;
        await _people.LoadOptionsAsync(form);
        return View("Form", form);
    }

    [HttpPost, HasPermission(P.Students.Create)]
    public async Task<IActionResult> Create(StudentForm form)
    {
        form.Id = 0;
        await _people.ValidateAsync(form, ModelState);

        var lead = form.LeadId is int lid ? await _db.Leads.FirstOrDefaultAsync(l => l.Id == lid && l.ConvertedStudentId == null) : null;
        var submission = form.SubmissionId is int sid ? await _db.FormSubmissions.FirstOrDefaultAsync(s => s.Id == sid && s.StudentId == null) : null;
        if (form.LeadId is not null && lead is null) form.LeadId = null;
        if (form.SubmissionId is not null && submission is null) form.SubmissionId = null;

        if (ModelState.IsValid && !form.IgnoreDuplicates)
        {
            // The lead being enrolled is expected to match, so it doesn't count as a duplicate.
            form.Options.Duplicates = await _people.FindDuplicatesAsync(new[] { form.Phone1, form.Phone2 }, new[] { form.Email1, form.Email2 }, excludeLeadId: lead?.Id);
            if (submission?.LeadId is int subLead) form.Options.Duplicates.RemoveAll(d => d.Kind == "Lead" && d.Id == subLead);
        }
        if (!ModelState.IsValid || form.Options.Duplicates.Count > 0)
        {
            await _people.LoadOptionsAsync(form);
            return View("Form", form);
        }

        // A registration that already created a lead enrolls that lead.
        if (lead is null && submission?.LeadId is int subLeadId)
            lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == subLeadId && l.ConvertedStudentId == null);

        var student = new Student();
        form.ApplyTo(student, _clock.Today);
        await _people.EnrollAsync(student, lead, submission);
        this.Success($"{student.FullName} was enrolled as student {student.StudentNo}.");
        return RedirectToAction(nameof(Details), new { id = student.Id });
    }

    [HttpGet, HasPermission(P.Students.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();
        var form = StudentForm.From(student);
        await _people.LoadOptionsAsync(form);
        return View("Form", form);
    }

    [HttpPost, HasPermission(P.Students.Edit)]
    public async Task<IActionResult> Edit(int id, StudentForm form)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();
        form.Id = id;
        form.StudentNo = student.StudentNo;
        await _people.ValidateAsync(form, ModelState);
        if (!ModelState.IsValid)
        {
            await _people.LoadOptionsAsync(form);
            return View("Form", form);
        }

        var oldRank = student.RankId;
        form.ApplyTo(student, _clock.Today);
        if (student.RankId is int newRank && newRank != oldRank)
        {
            // Keep the rank history complete even when the rank is corrected on the profile.
            _db.RankPromotions.Add(new RankPromotion
            {
                StudentId = id, FromRankId = oldRank, ToRankId = newRank, PromotedOn = student.RankSince ?? _clock.Today,
                Notes = "Rank changed on the student profile.",
            });
        }
        await _db.SaveChangesAsync();
        this.Success("Student saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.Students.Delete)]
    public async Task<IActionResult> Delete(int id)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();
        // Free the kiosk PIN so it can't check anyone in.
        student.KioskPin = null;
        _db.Students.Remove(student);
        await _db.SaveChangesAsync();
        this.Success($"{student.FullName} was deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Students.Edit)]
    public async Task<IActionResult> ResetPin(int id)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (student is null) return NotFound();
        student.KioskPin = await _people.NewKioskPinAsync();
        _people.NewActivity(ActivityType.System, "Kiosk PIN was reset.", student: student);
        await _db.SaveChangesAsync();
        this.Success($"New kiosk PIN for {student.FullName}: {student.KioskPin}");
        return RedirectToAction(nameof(Details), new { id });
    }
}
