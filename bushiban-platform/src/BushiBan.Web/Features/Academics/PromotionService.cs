using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Academics;

/// <summary>A student's next belt and how close they are to testing for it.</summary>
public record PromotionCandidate(
    int StudentId, string Name, string StudentNo, int CourseId, string Program,
    int CurrentRankId, string CurrentRank, string? CurrentColor,
    int NextRankId, string NextRank, string? NextColor,
    DateOnly Since, int DaysAtRank, int RequiredDays, int ClassesSince, int RequiredClasses)
{
    public double DaysProgress => RequiredDays <= 0 ? 1 : Math.Min(1, DaysAtRank / (double)RequiredDays);
    public double ClassesProgress => RequiredClasses <= 0 ? 1 : Math.Min(1, ClassesSince / (double)RequiredClasses);
    /// <summary>The slower of the two requirements decides how ready they are (0–1).</summary>
    public double Progress => Math.Min(DaysProgress, ClassesProgress);
    public bool IsReady => DaysAtRank >= RequiredDays && ClassesSince >= RequiredClasses;
    public int DaysToGo => Math.Max(0, RequiredDays - DaysAtRank);
    public int ClassesToGo => Math.Max(0, RequiredClasses - ClassesSince);
}

public class PromotionOverview
{
    public List<PromotionCandidate> Candidates { get; } = new();
    /// <summary>Active students whose belt isn't on their program's ranking system (or the program has none).</summary>
    public int NotOnPath { get; set; }
    /// <summary>Active students already at the last rank of their system.</summary>
    public int AtTopRank { get; set; }
}

public record PromotionResult(List<string> Promoted, List<string> Skipped);

/// <summary>
/// Belt readiness and promotions. A student is ready to test when they've spent the current rank's
/// DurationInDays at it (since RankSince, or enrollment) and attended at least its MinimumClasses since then.
/// The path is the student's program's first ranking system.
/// </summary>
public class PromotionService
{
    public const double AlmostReadyThreshold = 0.8;

    private readonly AppDbContext _db;
    private readonly BranchClock _clock;

    public PromotionService(AppDbContext db, BranchClock clock) { _db = db; _clock = clock; }

    public async Task<PromotionOverview> OverviewAsync(int? courseId = null, IReadOnlyCollection<int>? studentIds = null, CancellationToken ct = default)
    {
        var today = _clock.Today;
        var students = _db.Students.Where(s => s.IsActive && s.RankId != null && s.CourseId != null);
        if (courseId is int cid) students = students.Where(s => s.CourseId == cid);
        if (studentIds is not null) students = students.Where(s => studentIds.Contains(s.Id));

        var people = await students
            .Select(s => new { s.Id, s.FirstName, s.LastName, s.StudentNo, CourseId = s.CourseId!.Value, Program = s.Course!.Name, RankId = s.RankId!.Value, Since = s.RankSince ?? s.EnrollmentDate })
            .ToListAsync(ct);
        var overview = new PromotionOverview();
        if (people.Count == 0) return overview;

        // Each program's first ranking system.
        var courseIds = people.Select(p => p.CourseId).Distinct().ToList();
        var links = await _db.CourseRankingSystems.Where(l => courseIds.Contains(l.CourseId) && !l.RankingSystem!.IsDeleted)
            .Select(l => new { l.CourseId, l.RankingSystemId, l.Index }).ToListAsync(ct);
        var systemFor = links.GroupBy(l => l.CourseId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Index).First().RankingSystemId);

        var systemIds = systemFor.Values.Distinct().ToList();
        var steps = (await _db.RankingSystemRanks.Where(r => systemIds.Contains(r.RankingSystemId))
                .Select(r => new { r.RankingSystemId, r.RankId, r.Index, r.DurationInDays, r.MinimumClasses, r.Rank!.Name, r.Rank.Color })
                .ToListAsync(ct))
            .GroupBy(r => r.RankingSystemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Index).ToList());

        // Classes attended since each student reached their current rank, in one grouped query.
        var attendance = _db.AttendanceRecords.Where(a => a.Student!.IsActive && a.Student.RankId != null && a.Date <= today
                                                          && a.Date >= (a.Student.RankSince ?? a.Student.EnrollmentDate));
        if (courseId is int c2) attendance = attendance.Where(a => a.Student!.CourseId == c2);
        if (studentIds is not null) attendance = attendance.Where(a => studentIds.Contains(a.StudentId));
        var classes = await attendance.GroupBy(a => a.StudentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        foreach (var p in people)
        {
            if (!systemFor.TryGetValue(p.CourseId, out var systemId) || !steps.TryGetValue(systemId, out var path)) { overview.NotOnPath++; continue; }
            var at = path.FindIndex(s => s.RankId == p.RankId);
            if (at < 0) { overview.NotOnPath++; continue; }
            if (at == path.Count - 1) { overview.AtTopRank++; continue; }
            var current = path[at];
            var next = path[at + 1];
            overview.Candidates.Add(new PromotionCandidate(
                p.Id, $"{p.FirstName} {p.LastName}", p.StudentNo, p.CourseId, p.Program,
                current.RankId, current.Name, current.Color, next.RankId, next.Name, next.Color,
                p.Since, Math.Max(0, today.DayNumber - p.Since.DayNumber), current.DurationInDays,
                classes.GetValueOrDefault(p.Id), current.MinimumClasses));
        }
        return overview;
    }

    /// <summary>Moves each student up one belt on their path, records the promotion and notes it on their timeline.</summary>
    public async Task<PromotionResult> PromoteAsync(IReadOnlyCollection<int> studentIds, DateOnly promotedOn, string? notes, CancellationToken ct = default)
    {
        var ids = studentIds.Distinct().ToList();
        var overview = await OverviewAsync(null, ids, ct);
        var byStudent = overview.Candidates.ToDictionary(c => c.StudentId);
        var students = await _db.Students.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 500)];

        var promoted = new List<string>();
        var skipped = new List<string>();
        foreach (var student in students)
        {
            if (!byStudent.TryGetValue(student.Id, out var c)) { skipped.Add(student.FullName); continue; }
            _db.RankPromotions.Add(new RankPromotion
            {
                StudentId = student.Id, FromRankId = c.CurrentRankId, ToRankId = c.NextRankId, PromotedOn = promotedOn, Notes = cleanNotes,
            });
            student.RankId = c.NextRankId;
            student.RankSince = promotedOn;
            _db.Activities.Add(new Activity
            {
                StudentId = student.Id, Type = ActivityType.System,
                Body = $"Promoted from {c.CurrentRank} to {c.NextRank} belt on {promotedOn:MMM d, yyyy}." + (cleanNotes is null ? "" : $" {cleanNotes}"),
            });
            promoted.Add(student.FullName);
        }
        if (promoted.Count > 0) await _db.SaveChangesAsync(ct);
        return new(promoted, skipped);
    }
}
