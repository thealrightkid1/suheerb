using System.Globalization;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Academics;

/// <summary>Programs (called Courses in the code): fees, belt path and the weekly class schedule.</summary>
public class CoursesController : Controller
{
    private readonly AppDbContext _db;
    private readonly LookupService _lookups;

    public CoursesController(AppDbContext db, LookupService lookups) { _db = db; _lookups = lookups; }

    [HttpGet, HasPermission(P.Courses.View)]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _db.Courses.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Name.Contains(q.Trim()));
        var rows = await query.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name).Select(c => new CourseRow(
                c.Id, c.Name,
                c.FeeStructure != null ? c.FeeStructure.Name : null,
                c.FeeStructure != null ? c.FeeStructure.MonthlyFee : null,
                c.RankingSystems.Where(r => !r.RankingSystem!.IsDeleted).OrderBy(r => r.Index).Select(r => r.RankingSystem!.Name).ToList(),
                c.Schedule.Count(s => s.IsActive),
                _db.Students.Count(s => s.CourseId == c.Id && s.IsActive),
                c.IsActive))
            .ToListAsync();
        ViewData["Search"] = q;
        return View(rows);
    }

    [HttpGet, HasPermission(P.Courses.View)]
    public async Task<IActionResult> Details(int id)
    {
        var model = await _db.Courses.Where(c => c.Id == id).Select(c => new CourseDetails
        {
            Id = c.Id, Name = c.Name, Description = c.Description, IsActive = c.IsActive, MinimumTermMonths = c.MinimumTermMonths,
            FeeStructure = c.FeeStructure != null ? c.FeeStructure.Name : null,
            AdmissionFee = c.FeeStructure != null ? c.FeeStructure.AdmissionFee : null,
            SecurityFee = c.FeeStructure != null ? c.FeeStructure.SecurityFee : null,
            MonthlyFee = c.FeeStructure != null ? c.FeeStructure.MonthlyFee : null,
            Schedule = c.Schedule.OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
                .Select(s => new ScheduleView(s.DayOfWeek, s.StartTime, s.DurationMinutes, s.Instructor, s.IsActive)).ToList(),
        }).FirstOrDefaultAsync();
        if (model is null) return NotFound();

        var systems = await _db.CourseRankingSystems.Where(l => l.CourseId == id && !l.RankingSystem!.IsDeleted)
            .OrderBy(l => l.Index).Select(l => new { l.RankingSystemId, l.RankingSystem!.Name }).ToListAsync();
        model.RankingSystems = systems.Select(s => (s.RankingSystemId, s.Name)).ToList();
        if (systems.Count > 0)
        {
            var first = systems[0].RankingSystemId;
            model.Path = await _db.RankingSystemRanks.Where(r => r.RankingSystemId == first).OrderBy(r => r.Index)
                .Select(r => new RankStepView(r.Index, r.Rank!.Name, r.Rank.Color, r.DurationInDays, r.MinimumClasses)).ToListAsync();
        }
        model.Students = await _db.Students.Where(s => s.CourseId == id && s.IsActive).OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new EnrolledStudent(s.Id, s.FirstName + " " + s.LastName, s.StudentNo,
                s.Rank != null ? s.Rank.Name : null, s.Rank != null ? s.Rank.Color : null, s.EnrollmentDate))
            .ToListAsync();
        model.Leads = await _db.Leads.CountAsync(l => l.CourseId == id && l.ConvertedStudentId == null);
        model.Schedule = model.Schedule.OrderBy(s => ((int)s.Day + 6) % 7).ThenBy(s => s.Start).ToList();
        return View(model);
    }

    [HttpGet, HasPermission(P.Courses.Create)]
    public async Task<IActionResult> Create()
    {
        var standard = await _db.RankingSystems.Where(s => s.IsActive).OrderBy(s => s.Name).Select(s => (int?)s.Id).FirstOrDefaultAsync();
        var form = new CourseForm { RankingSystemIds = standard is null ? new() : new() { standard } };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Courses.Create)]
    public async Task<IActionResult> Create(CourseForm form)
    {
        var schedule = await ValidateAsync(form);
        if (!ModelState.IsValid) return await FormView(form);
        var course = new Course();
        Apply(course, form, schedule);
        _db.Courses.Add(course);
        await _db.SaveChangesAsync();
        this.Success($"Program \"{course.Name}\" created.");
        return RedirectToAction(nameof(Details), new { id = course.Id });
    }

    [HttpGet, HasPermission(P.Courses.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var course = await _db.Courses.AsNoTracking().Include(c => c.RankingSystems).Include(c => c.Schedule).AsSplitQuery().FirstOrDefaultAsync(c => c.Id == id);
        if (course is null) return NotFound();
        var form = new CourseForm
        {
            Id = course.Id, Name = course.Name, Description = course.Description, FeeStructureId = course.FeeStructureId,
            MinimumTermMonths = course.MinimumTermMonths, IsActive = course.IsActive,
            RankingSystemIds = course.RankingSystems.OrderBy(r => r.Index).Select(r => (int?)r.RankingSystemId).ToList(),
        };
        LoadSchedule(form, course.Schedule.Where(s => s.IsActive).ToList());
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Courses.Edit)]
    public async Task<IActionResult> Edit(int id, CourseForm form)
    {
        var course = await _db.Courses.Include(c => c.RankingSystems).Include(c => c.Schedule).AsSplitQuery().FirstOrDefaultAsync(c => c.Id == id);
        if (course is null) return NotFound();
        form.Id = id;
        var schedule = await ValidateAsync(form);
        if (!ModelState.IsValid) return await FormView(form);
        _db.CourseRankingSystems.RemoveRange(course.RankingSystems);
        _db.ClassSchedules.RemoveRange(course.Schedule);
        course.RankingSystems.Clear();
        course.Schedule.Clear();
        Apply(course, form, schedule);
        await _db.SaveChangesAsync();
        this.Success("Program saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.Courses.Delete)]
    public async Task<IActionResult> Delete(int id)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id);
        if (course is null) return NotFound();
        var students = await _db.Students.CountAsync(s => s.CourseId == id);
        var leads = await _db.Leads.CountAsync(l => l.CourseId == id);
        if (students + leads > 0)
        {
            this.Error($"Can't delete \"{course.Name}\": {students} student(s) and {leads} lead(s) are in this program. Move them to another program, or mark this one inactive instead.");
            return RedirectToAction(nameof(Details), new { id });
        }
        _db.Courses.Remove(course);
        await _db.SaveChangesAsync();
        this.Success("Program deleted.");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Shows a schedule in "same time" mode when every class shares one start time, length and instructor; otherwise per day.</summary>
    private static void LoadSchedule(CourseForm form, List<ClassSchedule> rows)
    {
        if (rows.Count == 0) { form.ScheduleMode = CourseForm.SameTime; return; }
        var first = rows[0];
        var uniform = rows.All(r => r.StartTime == first.StartTime && r.DurationMinutes == first.DurationMinutes && r.Instructor == first.Instructor)
                      && rows.Select(r => r.DayOfWeek).Distinct().Count() == rows.Count;
        if (uniform)
        {
            form.ScheduleMode = CourseForm.SameTime;
            form.Days = rows.Select(r => r.DayOfWeek).ToList();
            form.StartTime = first.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            form.DurationMinutes = first.DurationMinutes;
            form.Instructor = first.Instructor;
        }
        else
        {
            form.ScheduleMode = CourseForm.PerDay;
            form.Rows = rows.OrderBy(r => ((int)r.DayOfWeek + 6) % 7).ThenBy(r => r.StartTime).Select(r => new ScheduleRowInput
            {
                Day = r.DayOfWeek, StartTime = r.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture), DurationMinutes = r.DurationMinutes, Instructor = r.Instructor,
            }).ToList();
        }
    }

    /// <summary>Checks the form and returns the class schedule it describes.</summary>
    private async Task<List<ClassSchedule>> ValidateAsync(CourseForm form)
    {
        form.RankingSystemIds = (form.RankingSystemIds ?? new()).Where(x => x is not null).ToList();
        form.Days ??= new();
        form.Rows ??= new();

        var name = form.Name?.Trim() ?? "";
        if (name.Length > 0 && await _db.Courses.AnyAsync(c => c.Name == name && c.Id != form.Id))
            ModelState.AddModelError(nameof(form.Name), "Another program already has this name.");
        if (form.FeeStructureId is int fs && !await _db.FeeStructures.AnyAsync(f => f.Id == fs))
            ModelState.AddModelError(nameof(form.FeeStructureId), "Pick a fee structure from the list.");

        var systemIds = form.RankingSystemIds.Select(x => x!.Value).ToList();
        if (systemIds.Distinct().Count() != systemIds.Count)
            ModelState.AddModelError(nameof(form.RankingSystemIds), "Each ranking system can be added only once.");
        var known = await _db.RankingSystems.Where(s => systemIds.Contains(s.Id)).Select(s => s.Id).ToListAsync();
        if (systemIds.Except(known).Any())
            ModelState.AddModelError(nameof(form.RankingSystemIds), "Pick ranking systems from the list.");

        var schedule = new List<ClassSchedule>();
        if (form.ScheduleMode == CourseForm.PerDay)
        {
            for (var i = 0; i < form.Rows.Count; i++)
            {
                var r = form.Rows[i];
                if (!Enum.IsDefined(r.Day)) ModelState.AddModelError($"Rows[{i}].Day", "Pick a day.");
                if (!TryTime(r.StartTime, out var start)) ModelState.AddModelError($"Rows[{i}].StartTime", "Enter a start time.");
                if (r.DurationMinutes is < 5 or > 600) ModelState.AddModelError($"Rows[{i}].DurationMinutes", "5 to 600 minutes.");
                schedule.Add(new ClassSchedule { DayOfWeek = r.Day, StartTime = start, DurationMinutes = r.DurationMinutes, Instructor = Clean(r.Instructor) });
            }
            var clash = schedule.GroupBy(s => (s.DayOfWeek, s.StartTime)).FirstOrDefault(g => g.Count() > 1);
            if (clash is not null) ModelState.AddModelError(nameof(form.Rows), $"There are two classes on {clash.Key.DayOfWeek} at {Format.Time(clash.Key.StartTime)}.");
        }
        else
        {
            form.ScheduleMode = CourseForm.SameTime;
            var days = form.Days.Where(Enum.IsDefined).Distinct().ToList();
            if (days.Count > 0)
            {
                if (!TryTime(form.StartTime, out var start)) ModelState.AddModelError(nameof(form.StartTime), "Enter the class start time.");
                if (form.DurationMinutes is < 5 or > 600) ModelState.AddModelError(nameof(form.DurationMinutes), "Classes run 5 to 600 minutes.");
                schedule.AddRange(days.Select(d => new ClassSchedule { DayOfWeek = d, StartTime = start, DurationMinutes = form.DurationMinutes, Instructor = Clean(form.Instructor) }));
            }
        }
        return schedule;
    }

    private static bool TryTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out time) || TimeOnly.TryParse(value, CultureInfo.GetCultureInfo("en-US"), out time);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(Course course, CourseForm form, List<ClassSchedule> schedule)
    {
        course.Name = form.Name.Trim();
        course.Description = Clean(form.Description);
        course.FeeStructureId = form.FeeStructureId;
        course.MinimumTermMonths = form.MinimumTermMonths;
        course.IsActive = form.IsActive;
        course.RankingSystems = form.RankingSystemIds.Select((id, i) => new CourseRankingSystem { RankingSystemId = id!.Value, Index = i + 1 }).ToList();
        course.Schedule = schedule;
    }

    private async Task<IActionResult> FormView(CourseForm form)
    {
        form.FeeStructures = await _lookups.FeeStructuresAsync(form.FeeStructureId);
        form.RankingSystemOptions = await _db.RankingSystems.OrderBy(s => s.Name)
            .Select(s => new SelectListItem(s.Name + (s.IsActive ? "" : " (inactive)"), s.Id.ToString())).ToListAsync();
        if (form.ScheduleMode == CourseForm.PerDay && form.Rows.Count == 0)
            form.Rows.Add(new ScheduleRowInput { StartTime = "17:00" });
        return View("Form", form);
    }
}
