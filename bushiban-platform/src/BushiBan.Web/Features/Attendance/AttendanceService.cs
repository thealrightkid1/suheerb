using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Attendance;

public enum CheckInStatus { CheckedIn, AlreadyCheckedIn, NotFound }

/// <summary>What the kiosk shows after a check-in attempt. Only first name and belt, never other details.</summary>
public record CheckInResult(CheckInStatus Status, string FirstName = "", string? Belt = null, string? BeltColor = null, int ClassesThisMonth = 0);

public record RollResult(int Added, int Removed, int NotRemoved);

/// <summary>
/// Creates and removes attendance records. One student is checked in at most once per program per day
/// (and once per day on the kiosk), so double taps and repeated saves never create duplicates.
/// </summary>
public class AttendanceService
{
    // Check-ins are tiny writes; serialising them closes the gap between "already checked in?" and the insert.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly AppDbContext _db;
    private readonly BranchClock _clock;

    public AttendanceService(AppDbContext db, BranchClock clock) { _db = db; _clock = clock; }

    /// <summary>The active student with this kiosk PIN in the current branch, if any.</summary>
    public async Task<int?> FindByPinAsync(string pin, CancellationToken ct = default) =>
        await _db.Students.Where(s => s.IsActive && s.KioskPin == pin).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);

    /// <summary>Kiosk check-in for today, into the student's own program. Rejects a second check-in on the same day.</summary>
    public async Task<CheckInResult> KioskCheckInAsync(int studentId, CheckInMethod method, string markedBy, CancellationToken ct = default)
    {
        var student = await _db.Students.Where(s => s.Id == studentId && s.IsActive)
            .Select(s => new { s.Id, s.FirstName, s.CourseId, Belt = s.Rank != null ? s.Rank.Name : null, Color = s.Rank != null ? s.Rank.Color : null })
            .FirstOrDefaultAsync(ct);
        if (student is null) return new(CheckInStatus.NotFound);

        var now = _clock.Now;
        var today = DateOnly.FromDateTime(now);
        bool already;
        await WriteLock.WaitAsync(ct);
        try
        {
            already = await _db.AttendanceRecords.AnyAsync(a => a.StudentId == student.Id && a.Date == today, ct);
            if (!already)
            {
                _db.AttendanceRecords.Add(new AttendanceRecord
                {
                    StudentId = student.Id, CourseId = student.CourseId, Date = today,
                    CheckInTime = TimeOnly.FromDateTime(now), Method = method, MarkedBy = markedBy,
                });
                await _db.SaveChangesAsync(ct);
            }
        }
        finally { WriteLock.Release(); }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var thisMonth = await _db.AttendanceRecords.CountAsync(a => a.StudentId == student.Id && a.Date >= monthStart && a.Date <= today, ct);
        return new(already ? CheckInStatus.AlreadyCheckedIn : CheckInStatus.CheckedIn, student.FirstName, student.Belt, student.Color, thisMonth);
    }

    /// <summary>
    /// Makes the program's attendance for <paramref name="date"/> match <paramref name="presentIds"/>:
    /// adds missing students and removes unticked ones. Records someone else entered are only removed
    /// when <paramref name="canRemoveOthers"/> is true (the Attendance.Delete permission).
    /// </summary>
    public async Task<RollResult> SaveRollAsync(int courseId, DateOnly date, TimeOnly checkInTime, IReadOnlyCollection<int> presentIds,
        string markedBy, bool canRemoveOthers, CancellationToken ct = default)
    {
        var present = presentIds.ToHashSet();
        await WriteLock.WaitAsync(ct);
        try
        {
            var existing = await _db.AttendanceRecords.Where(a => a.CourseId == courseId && a.Date == date).ToListAsync(ct);
            var recorded = existing.Select(a => a.StudentId).ToHashSet();
            var toAdd = present.Where(id => !recorded.Contains(id)).ToList();
            // Only real, active students of this branch (the branch filter applies) can be added.
            var valid = await _db.Students.Where(s => toAdd.Contains(s.Id) && s.IsActive).Select(s => s.Id).ToListAsync(ct);
            foreach (var id in valid)
            {
                _db.AttendanceRecords.Add(new AttendanceRecord
                {
                    StudentId = id, CourseId = courseId, Date = date, CheckInTime = checkInTime,
                    Method = CheckInMethod.Manual, MarkedBy = markedBy,
                });
            }

            int removed = 0, kept = 0;
            foreach (var record in existing.Where(a => !present.Contains(a.StudentId)))
            {
                if (canRemoveOthers || string.Equals(record.MarkedBy, markedBy, StringComparison.OrdinalIgnoreCase))
                {
                    _db.AttendanceRecords.Remove(record);
                    removed++;
                }
                else kept++;
            }
            await _db.SaveChangesAsync(ct);
            return new(valid.Count, removed, kept);
        }
        finally { WriteLock.Release(); }
    }

    /// <summary>True when another record already has this student in this program on this date.</summary>
    public Task<bool> IsDuplicateAsync(int studentId, int? courseId, DateOnly date, int exceptId = 0, CancellationToken ct = default) =>
        _db.AttendanceRecords.AnyAsync(a => a.Id != exceptId && a.StudentId == studentId && a.CourseId == courseId && a.Date == date, ct);
}
