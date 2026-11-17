using System.Globalization;
using System.Text;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Attendance;

public class AttendanceController : Controller
{
    private readonly AppDbContext _db;
    private readonly AttendanceService _attendance;
    private readonly BranchClock _clock;
    private readonly LookupService _lookups;

    public AttendanceController(AppDbContext db, AttendanceService attendance, BranchClock clock, LookupService lookups)
    {
        _db = db; _attendance = attendance; _clock = clock; _lookups = lookups;
    }

    private string CurrentUserName => User.FindFirst("display_name")?.Value ?? User.Identity?.Name ?? "staff";

    // ---------------------------------------------------------------- Take attendance

    [HttpGet, HasPermission(P.Attendance.Create)]
    public async Task<IActionResult> Roll(int? courseId, DateOnly? date)
    {
        var today = _clock.Today;
        var day = date ?? today;
        var page = new RollPage
        {
            CourseId = courseId, Date = day, IsToday = day == today,
            Courses = await _lookups.CoursesAsync(courseId),
            Classes = await ClassesOnAsync(day.DayOfWeek),
        };

        // Default check-in time: now for today, otherwise the program's first class that day.
        var firstClass = page.Classes.Where(c => c.CourseId == courseId).Select(c => (TimeOnly?)c.Start).FirstOrDefault();
        var defaultTime = page.IsToday ? _clock.TimeOfDay : firstClass ?? new TimeOnly(17, 0);
        page.CheckInTime = defaultTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (courseId is int cid)
        {
            page.CourseName = await _db.Courses.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync();
            if (page.CourseName is null) return NotFound();

            var records = await _db.AttendanceRecords.Where(a => a.CourseId == cid && a.Date == day)
                .Select(a => new { a.StudentId, a.MarkedBy, a.CheckInTime }).ToListAsync();
            var recordedIds = records.Select(r => r.StudentId).ToList();
            var canRemoveOthers = User.HasPermission(P.Attendance.Delete);

            var students = await _db.Students
                .Where(s => (s.IsActive && s.CourseId == cid) || recordedIds.Contains(s.Id))
                .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new { s.Id, s.FirstName, s.LastName, s.StudentNo, s.CourseId, Belt = s.Rank != null ? s.Rank.Name : null, Color = s.Rank != null ? s.Rank.Color : null })
                .ToListAsync();
            page.Roster = students.Select(s =>
            {
                var rec = records.FirstOrDefault(r => r.StudentId == s.Id);
                return new RollStudent
                {
                    Id = s.Id, Name = $"{s.FirstName} {s.LastName}", StudentNo = s.StudentNo, Belt = s.Belt, BeltColor = s.Color,
                    Present = rec is not null, Visitor = s.CourseId != cid, CheckedInAt = rec is null ? null : Format.Time(rec.CheckInTime),
                    Locked = rec is not null && !canRemoveOthers && !string.Equals(rec.MarkedBy, CurrentUserName, StringComparison.OrdinalIgnoreCase),
                };
            }).ToList();

            var rosterIds = page.Roster.Select(r => r.Id).ToList();
            page.OtherStudents = await _db.Students.Where(s => s.IsActive && !rosterIds.Contains(s.Id))
                .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new SelectListItem(s.FirstName + " " + s.LastName + " · " + s.StudentNo + (s.Course != null ? " · " + s.Course.Name : ""), s.Id.ToString()))
                .ToListAsync();
        }
        return View(page);
    }

    [HttpPost, HasPermission(P.Attendance.Create)]
    public async Task<IActionResult> Roll(int courseId, DateOnly? date, string? checkInTime, List<int>? present)
    {
        var day = date ?? _clock.Today;
        if (day > _clock.Today)
        {
            this.Error("Attendance can't be taken for a future date.");
            return RedirectToAction(nameof(Roll), new { courseId, date = day.ToString("yyyy-MM-dd") });
        }
        if (!await _db.Courses.AnyAsync(c => c.Id == courseId)) return NotFound();
        var time = TimeOnly.TryParse(checkInTime, CultureInfo.InvariantCulture, out var t) ? t : _clock.TimeOfDay;

        var result = await _attendance.SaveRollAsync(courseId, day, time, present ?? new List<int>(), CurrentUserName, User.HasPermission(P.Attendance.Delete));
        var parts = new List<string>();
        if (result.Added > 0) parts.Add($"{result.Added} checked in");
        if (result.Removed > 0) parts.Add($"{result.Removed} removed");
        var message = parts.Count == 0 ? "Attendance saved. Nothing changed." : $"Attendance saved: {string.Join(", ", parts)}.";
        if (result.NotRemoved > 0) message += $" {result.NotRemoved} check-in(s) recorded by someone else were kept; ask a manager to remove them.";
        this.Success(message);
        return RedirectToAction(nameof(Roll), new { courseId, date = day.ToString("yyyy-MM-dd") });
    }

    private async Task<List<ClassSlot>> ClassesOnAsync(DayOfWeek day) =>
        await _db.ClassSchedules.Where(c => c.IsActive && c.DayOfWeek == day && c.Course!.IsActive && !c.Course.IsDeleted)
            .OrderBy(c => c.StartTime).ThenBy(c => c.Course!.Name)
            .Select(c => new ClassSlot(c.CourseId, c.Course!.Name, c.DayOfWeek, c.StartTime, c.DurationMinutes, c.Instructor))
            .ToListAsync();

    // ---------------------------------------------------------------- Records

    [HttpGet, HasPermission(P.Attendance.View)]
    public async Task<IActionResult> Index([FromQuery] AttendanceFilter filter)
    {
        Normalize(filter);
        var page = new AttendanceIndexPage
        {
            Filter = filter,
            Rows = await Filtered(filter).ToPagedAsync(filter.Page, 50),
            Courses = await _lookups.CoursesAsync(filter.CourseId, activeOnly: false),
            CanEdit = User.HasPermission(P.Attendance.Edit),
            CanDelete = User.HasPermission(P.Attendance.Delete),
        };
        return View(page);
    }

    [HttpGet, HasPermission(P.Attendance.View)]
    public async Task<IActionResult> Export([FromQuery] AttendanceFilter filter)
    {
        Normalize(filter);
        var rows = await Filtered(filter).Take(50_000).ToListAsync();
        var csv = new StringBuilder();
        csv.AppendLine("Student,Student No,Date,Check-in time,Program,Method,Marked by,Confidence,Notes");
        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(r.StudentName), Csv(r.StudentNo), r.Date.ToString("yyyy-MM-dd"), r.CheckInTime.ToString("HH:mm"), Csv(r.Program),
                Csv(r.Method.DisplayName()), Csv(r.MarkedBy), r.Confidence?.ToString("0.#", CultureInfo.InvariantCulture) ?? "", Csv(r.Notes),
            }));
        }
        var name = $"attendance-{filter.From:yyyy-MM-dd}-to-{filter.To:yyyy-MM-dd}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", name);
    }

    private void Normalize(AttendanceFilter f)
    {
        var today = _clock.Today;
        f.To ??= today;
        f.From ??= f.To.Value.AddDays(-6);
        if (f.From > f.To) (f.From, f.To) = (f.To, f.From);
        f.Q = string.IsNullOrWhiteSpace(f.Q) ? null : f.Q.Trim();
    }

    private IQueryable<AttendanceRow> Filtered(AttendanceFilter f)
    {
        var q = _db.AttendanceRecords.Where(a => a.Date >= f.From && a.Date <= f.To);
        if (f.CourseId is int cid) q = q.Where(a => a.CourseId == cid);
        if (f.Method is CheckInMethod m) q = q.Where(a => a.Method == m);
        if (f.Q is string t)
            q = q.Where(a => (a.Student!.FirstName + " " + a.Student.LastName).Contains(t) || a.Student.LastName.Contains(t) || a.Student.StudentNo.Contains(t));
        return q.OrderByDescending(a => a.Date).ThenByDescending(a => a.CheckInTime).ThenBy(a => a.Id)
            .Select(a => new AttendanceRow(a.Id, a.StudentId, a.Student!.FirstName + " " + a.Student.LastName, a.Student.StudentNo, a.Date, a.CheckInTime,
                a.Course != null ? a.Course.Name : null, a.Method, a.MarkedBy, a.Confidence, a.Notes));
    }

    /// <summary>CSV cell: quoted, and protected against spreadsheet formula injection.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    [HttpGet, HasPermission(P.Attendance.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var record = await _db.AttendanceRecords.AsNoTracking().Include(a => a.Student).FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        var model = new AttendanceEditModel
        {
            Id = record.Id, StudentId = record.StudentId, StudentName = $"{record.Student?.FullName} ({record.Student?.StudentNo})",
            Date = record.Date, CheckInTime = record.CheckInTime.ToString("HH:mm", CultureInfo.InvariantCulture), CourseId = record.CourseId,
            Method = record.Method, Notes = record.Notes, Confidence = record.Confidence, MarkedBy = record.MarkedBy,
            Courses = await _lookups.CoursesAsync(record.CourseId, activeOnly: false),
        };
        return View(model);
    }

    [HttpPost, HasPermission(P.Attendance.Edit)]
    public async Task<IActionResult> Edit(int id, AttendanceEditModel model, string? returnUrl)
    {
        var record = await _db.AttendanceRecords.Include(a => a.Student).FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        model.Id = id;
        model.StudentId = record.StudentId;
        model.StudentName = $"{record.Student?.FullName} ({record.Student?.StudentNo})";
        model.MarkedBy = record.MarkedBy;
        model.Confidence = record.Confidence;

        TimeOnly time = default;
        if (!string.IsNullOrWhiteSpace(model.CheckInTime) && !TimeOnly.TryParse(model.CheckInTime, CultureInfo.InvariantCulture, out time))
            ModelState.AddModelError(nameof(model.CheckInTime), "Enter a time like 5:30 PM.");
        if (model.Date > _clock.Today) ModelState.AddModelError(nameof(model.Date), "The date can't be in the future.");
        if (model.CourseId is int cid && !await _db.Courses.AnyAsync(c => c.Id == cid)) ModelState.AddModelError(nameof(model.CourseId), "Pick a program from the list.");
        if (!Enum.IsDefined(model.Method)) ModelState.AddModelError(nameof(model.Method), "Pick a method from the list.");
        if (ModelState.IsValid && await _attendance.IsDuplicateAsync(record.StudentId, model.CourseId, model.Date!.Value, id))
            ModelState.AddModelError("", "This student is already checked in to that program on that date.");
        if (!ModelState.IsValid)
        {
            model.Courses = await _lookups.CoursesAsync(model.CourseId, activeOnly: false);
            return View(model);
        }

        record.Date = model.Date!.Value;
        record.CheckInTime = time;
        record.CourseId = model.CourseId;
        record.Method = model.Method;
        record.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
        await _db.SaveChangesAsync();
        this.Success("Attendance record saved.");
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Attendance.Delete)]
    public async Task<IActionResult> Delete(int id, string? returnUrl)
    {
        var record = await _db.AttendanceRecords.FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        _db.AttendanceRecords.Remove(record);
        await _db.SaveChangesAsync();
        this.Success("Attendance record deleted.");
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------- Report

    [HttpGet, HasPermission(P.Attendance.View)]
    public async Task<IActionResult> Report(DateOnly? from, DateOnly? to, int? courseId, int days = 14)
    {
        var today = _clock.Today;
        var page = new AttendanceReportPage
        {
            Today = today, To = to ?? today, CourseId = courseId, Days = Math.Clamp(days, 1, 365),
            Courses = await _lookups.CoursesAsync(courseId, activeOnly: false),
            CanMessage = User.HasPermission(P.Communications.Send),
        };
        page.From = from ?? page.To.AddDays(-29);
        if (page.From > page.To) (page.From, page.To) = (page.To, page.From);

        var students = _db.Students.Where(s => s.IsActive);
        if (courseId is int cid) students = students.Where(s => s.CourseId == cid);
        var list = await students.OrderBy(s => s.FirstName).ThenBy(s => s.LastName).Select(s => new
        {
            s.Id, Name = s.FirstName + " " + s.LastName, s.StudentNo, Program = s.Course != null ? s.Course.Name : null,
            Belt = s.Rank != null ? s.Rank.Name : null, Color = s.Rank != null ? s.Rank.Color : null, s.Phone1, s.EnrollmentDate,
        }).ToListAsync();

        var inPeriod = await _db.AttendanceRecords.Where(a => a.Date >= page.From && a.Date <= page.To && a.Student!.IsActive)
            .GroupBy(a => a.StudentId).Select(g => new { g.Key, Count = g.Count(), Last = g.Max(a => a.Date) })
            .ToDictionaryAsync(x => x.Key);
        var lastSeen = await _db.AttendanceRecords.Where(a => a.Student!.IsActive && a.Date <= today)
            .GroupBy(a => a.StudentId).Select(g => new { g.Key, Last = g.Max(a => a.Date) })
            .ToDictionaryAsync(x => x.Key, x => x.Last);

        page.Summary = list.Select(s =>
        {
            inPeriod.TryGetValue(s.Id, out var p);
            DateOnly? last = lastSeen.TryGetValue(s.Id, out var l) ? l : null;
            return new StudentSummaryRow(s.Id, s.Name, s.StudentNo, s.Program, s.Belt, s.Color, p?.Count ?? 0, p?.Last, last);
        }).OrderByDescending(r => r.Classes).ThenBy(r => r.Name).ToList();

        var cutoff = today.AddDays(-page.Days);
        page.Absent = list.Select(s =>
            {
                DateOnly? last = lastSeen.TryGetValue(s.Id, out var l) ? l : null;
                return new AbsentRow(s.Id, s.Name, s.StudentNo, s.Program, s.Phone1, last, last is null ? null : today.DayNumber - last.Value.DayNumber, s.EnrollmentDate);
            })
            .Where(r => r.LastSeen is null ? r.EnrolledOn <= cutoff : r.LastSeen < cutoff)
            .OrderBy(r => r.LastSeen ?? DateOnly.MinValue).ThenBy(r => r.Name).ToList();
        return View(page);
    }
}
