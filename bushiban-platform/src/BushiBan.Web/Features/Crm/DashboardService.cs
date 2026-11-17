using System.Security.Claims;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Crm;

public record DashboardTile(string Label, string Value, string? Sub, string Icon, string Tint, string? Url);
public record DashboardPerson(int Id, string Name, string Detail, bool Highlight = false);

public class DashboardModel
{
    public string BranchName { get; set; } = "";
    public DateOnly Today { get; set; }
    public List<DashboardTile> Tiles { get; set; } = new();
    public (List<string> Labels, List<int> Values)? CheckInsChart { get; set; }
    public (List<string> Labels, List<int> Values)? LeadsChart { get; set; }
    public List<DashboardPerson>? FollowUps { get; set; }
    public int FollowUpsTotal { get; set; }
    public List<DashboardPerson>? Birthdays { get; set; }
    public List<DashboardPerson>? NotSeen { get; set; }
    public bool IsEmpty => Tiles.Count == 0 && CheckInsChart is null && LeadsChart is null && FollowUps is null && Birthdays is null;
}

/// <summary>Numbers for the home page. Each block is only worked out when the user may see it.</summary>
public class DashboardService
{
    public const int NotSeenDays = 14;
    public const int BirthdayDays = 14;

    private readonly AppDbContext _db;
    private readonly BranchClock _clock;
    private readonly ICurrentBranch _branch;

    public DashboardService(AppDbContext db, BranchClock clock, ICurrentBranch branch) { _db = db; _clock = clock; _branch = branch; }

    public async Task<DashboardModel> BuildAsync(ClaimsPrincipal user, Func<string, string, object?, string?> url)
    {
        var today = _clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var model = new DashboardModel
        {
            Today = today,
            BranchName = await _db.Branches.Where(b => b.Id == _branch.BranchId).Select(b => b.Name).FirstOrDefaultAsync() ?? "",
        };
        bool students = user.HasPermission(P.Students.View), leads = user.HasPermission(P.Leads.View),
             attendance = user.HasPermission(P.Attendance.View), registrations = user.HasPermission(P.Registrations.View),
             billing = user.HasPermission(P.Billing.View);

        if (students)
        {
            var active = await _db.Students.CountAsync(s => s.IsActive);
            var joined = await _db.Students.CountAsync(s => s.IsActive && s.EnrollmentDate >= monthStart);
            model.Tiles.Add(new("Active students", active.ToString("N0"), joined > 0 ? $"{joined} enrolled this month" : null,
                "people", "tint-blue", url("Index", "Students", null)));
        }

        if (leads)
        {
            var newLeads = await _db.Leads.CountAsync(l => l.InquiryDate >= monthStart && l.InquiryDate <= today);
            model.Tiles.Add(new("New leads this month", newLeads.ToString("N0"), null, "person-plus", "tint-red",
                url("Index", "Leads", new { from = monthStart.ToString("yyyy-MM-dd") })));

            // Of the leads who came in over the last 90 days and got as far as a trial class, how many enrolled.
            var since = today.AddDays(-90);
            var recent = _db.Leads.Where(l => l.InquiryDate >= since);
            var tried = await recent.CountAsync(l => l.ConvertedStudentId != null || l.ProspectPhase == ProspectPhase.InTrial
                || l.ProspectPhase == ProspectPhase.ProgressCheck || l.ProspectPhase == ProspectPhase.PresentedWaiting
                || l.ProspectPhase == ProspectPhase.PresentedDenied || l.ProspectPhase == ProspectPhase.Enrolled);
            var enrolled = await recent.CountAsync(l => l.ConvertedStudentId != null || l.ProspectPhase == ProspectPhase.Enrolled);
            model.Tiles.Add(new("Trial → enrolled (90 days)", tried == 0 ? "–" : $"{Math.Round(100m * enrolled / tried)}%",
                tried == 0 ? "No trial classes yet" : $"{enrolled} of {tried} trial students enrolled", "graph-up-arrow", "tint-green",
                url("Board", "Leads", null)));

            var dueQuery = _db.Leads.Where(l => l.ConvertedStudentId == null && l.NextFollowUpDate != null && l.NextFollowUpDate <= today
                && l.ProspectPhase != ProspectPhase.StopFollowingUp && l.ProspectPhase != ProspectPhase.Enrolled);
            model.FollowUpsTotal = await dueQuery.CountAsync();
            var overdue = await dueQuery.CountAsync(l => l.NextFollowUpDate < today);
            model.Tiles.Add(new("Follow-ups due", model.FollowUpsTotal.ToString("N0"), overdue > 0 ? $"{overdue} overdue" : "All caught up",
                "telephone-forward", "tint-amber", url("FollowUps", "Leads", null)));

            var due = await dueQuery.OrderBy(l => l.NextFollowUpDate).ThenBy(l => l.LastName).Take(8)
                .Select(l => new { l.Id, Name = l.FirstName + " " + l.LastName, l.NextFollowUpDate, l.ProspectPhase, l.Phone1 }).ToListAsync();
            model.FollowUps = due.Select(l => new DashboardPerson(l.Id, l.Name,
                (l.NextFollowUpDate < today ? $"Overdue since {Format.Date(l.NextFollowUpDate)}" : "Due today") + $" · {l.ProspectPhase.DisplayName()}",
                l.NextFollowUpDate < today)).ToList();

            var byPhase = await _db.Leads.Where(l => l.IsActive && l.ConvertedStudentId == null && l.ProspectPhase != ProspectPhase.Enrolled)
                .GroupBy(l => l.ProspectPhase).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var phases = Enum.GetValues<ProspectPhase>().Where(p => p != ProspectPhase.Enrolled).ToList();
            model.LeadsChart = (phases.Select(p => p.DisplayName()).ToList(), phases.Select(p => byPhase.GetValueOrDefault(p)).ToList());
        }

        if (attendance)
        {
            var todayCount = await _db.AttendanceRecords.CountAsync(a => a.Date == today);
            var lastWeekSameDay = await _db.AttendanceRecords.CountAsync(a => a.Date == today.AddDays(-7));
            model.Tiles.Add(new("Check-ins today", todayCount.ToString("N0"), $"{lastWeekSameDay} on the same day last week",
                "check2-square", "tint-purple", url("Index", "Attendance", null)));

            var from = today.AddDays(-29);
            var perDay = await _db.AttendanceRecords.Where(a => a.Date >= from && a.Date <= today)
                .GroupBy(a => a.Date).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var days = Enumerable.Range(0, 30).Select(i => from.AddDays(i)).ToList();
            model.CheckInsChart = (days.Select(d => d.ToString("MMM d")).ToList(), days.Select(d => perDay.GetValueOrDefault(d)).ToList());
        }

        if (registrations)
        {
            var fresh = await _db.FormSubmissions.CountAsync(s => s.Status == SubmissionStatus.New);
            model.Tiles.Add(new("New registrations", fresh.ToString("N0"), fresh > 0 ? "Waiting to be reviewed" : "Inbox is clear",
                "file-earmark-text", "tint-slate", url("Index", "Registrations", new { status = "New" })));
        }

        if (billing)
        {
            var collected = await _db.Payments.Where(p => !p.IsVoided && p.Date >= monthStart && p.Date <= today).SumAsync(p => p.Amount);
            model.Tiles.Add(new("Collected this month", Format.Money(collected), null, "cash-coin", "tint-green", url("Index", "Payments", null)));

            var overdue = await _db.Invoices
                .Where(i => (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < today)
                .Select(i => new { i.StudentId, Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount) })
                .ToListAsync();
            var owing = overdue.Where(i => i.Total - i.Paid > 0).ToList();
            model.Tiles.Add(new("Overdue balance", Format.Money(owing.Sum(i => i.Total - i.Paid)),
                owing.Count == 0 ? "Nothing overdue" : $"{owing.Select(i => i.StudentId).Distinct().Count()} student(s)",
                "exclamation-triangle", "tint-red", url("Index", "Invoices", null)));
        }

        if (students)
        {
            // Birthdays: only id, name and date are loaded; the year wrap-around is easier in memory.
            var dobs = await _db.Students.Where(s => s.IsActive && s.DateOfBirth != null)
                .Select(s => new { s.Id, Name = s.FirstName + " " + s.LastName, Dob = s.DateOfBirth!.Value }).ToListAsync();
            model.Birthdays = dobs
                .Select(s => new { s.Id, s.Name, s.Dob, Next = NextBirthday(s.Dob, today) })
                .Where(s => s.Next.DayNumber - today.DayNumber <= BirthdayDays)
                .OrderBy(s => s.Next).Take(8)
                .Select(s => new DashboardPerson(s.Id, s.Name,
                    $"{(s.Next == today ? "Today" : s.Next.ToString("ddd, MMM d"))} · turns {s.Next.Year - s.Dob.Year}", s.Next == today))
                .ToList();
        }

        if (students && attendance)
        {
            var cutoff = today.AddDays(-NotSeenDays);
            var rows = await _db.Students.Where(s => s.IsActive && s.EnrollmentDate <= cutoff)
                .Select(s => new
                {
                    s.Id, Name = s.FirstName + " " + s.LastName,
                    Last = _db.AttendanceRecords.Where(a => a.StudentId == s.Id).Max(a => (DateOnly?)a.Date),
                })
                .Where(s => s.Last == null || s.Last < cutoff)
                .OrderBy(s => s.Last == null).ThenByDescending(s => s.Last).Take(8)
                .ToListAsync();
            model.NotSeen = rows.Select(s => new DashboardPerson(s.Id, s.Name,
                s.Last is null ? "No check-ins yet" : $"Last seen {Format.Date(s.Last)} ({today.DayNumber - s.Last.Value.DayNumber} days ago)")).ToList();
        }

        return model;
    }

    private static DateOnly NextBirthday(DateOnly dob, DateOnly today)
    {
        DateOnly On(int year) => new(year, dob.Month, Math.Min(dob.Day, DateTime.DaysInMonth(year, dob.Month)));
        var next = On(today.Year);
        return next < today ? On(today.Year + 1) : next;
    }
}
