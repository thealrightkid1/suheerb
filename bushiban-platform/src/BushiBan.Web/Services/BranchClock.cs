using BushiBan.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Services;

/// <summary>"Today" and "now" in the current branch's time zone (Texas and Connecticut differ).</summary>
public class BranchClock
{
    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;
    private TimeZoneInfo? _tz;

    public BranchClock(AppDbContext db, ICurrentBranch branch) { _db = db; _branch = branch; }

    public TimeZoneInfo TimeZone
    {
        get
        {
            if (_tz is not null) return _tz;
            var id = _branch.BranchId is int b
                ? _db.Branches.AsNoTracking().Where(x => x.Id == b).Select(x => x.TimeZone).FirstOrDefault()
                : null;
            try { _tz = TimeZoneInfo.FindSystemTimeZoneById(id ?? "America/Chicago"); }
            catch (TimeZoneNotFoundException) { _tz = TimeZoneInfo.Utc; }
            return _tz;
        }
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone);
    public DateOnly Today => DateOnly.FromDateTime(Now);
    public TimeOnly TimeOfDay => TimeOnly.FromDateTime(Now);
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
}
