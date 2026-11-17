using System.Text.Json;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.SystemTables;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

/// <summary>Who changed what and when. System admins see every branch; everyone else sees the current branch only.</summary>
[HasPermission(P.AuditLog.View)]
public class AuditLogController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;
    private readonly BranchClock _clock;

    public AuditLogController(AppDbContext db, ICurrentBranch branch, BranchClock clock) { _db = db; _branch = branch; _clock = clock; }

    public class AuditFilter
    {
        public string? Entity { get; set; }
        public string? EntityId { get; set; }
        public string? User { get; set; }
        public string? Action { get; set; }
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
        /// <summary>System admins only: limit to one branch.</summary>
        public int? BranchId { get; set; }
        public bool Any => !string.IsNullOrEmpty(Entity) || !string.IsNullOrEmpty(EntityId) || !string.IsNullOrEmpty(User)
                           || !string.IsNullOrEmpty(Action) || From is not null || To is not null || BranchId is not null;
    }

    public sealed record Change(string Field, string? Old, string? New, bool IsCreate);

    public sealed record AuditRow(AuditLog Log, string? BranchName, List<Change> Changes);

    public class AuditPageModel
    {
        public AuditFilter Filter { get; init; } = new();
        public PagedList<AuditRow> Paged { get; init; } = default!;
        public List<string> EntityNames { get; init; } = new();
        public List<(int Id, string Name)> Branches { get; init; } = new();
        public bool AllBranches { get; init; }
        public BranchClock Clock { get; init; } = default!;
    }

    [HttpGet]
    // [FromQuery]: otherwise the route value "action" (= "Index") would bind to Filter.Action.
    public async Task<IActionResult> Index([FromQuery] AuditFilter f, int page = 1, CancellationToken ct = default)
    {
        var scoped = Scope();
        var q = scoped;
        if (!string.IsNullOrEmpty(f.Entity)) q = q.Where(a => a.EntityName == f.Entity);
        if (!string.IsNullOrWhiteSpace(f.EntityId)) q = q.Where(a => a.EntityId == f.EntityId.Trim());
        if (!string.IsNullOrWhiteSpace(f.User)) q = q.Where(a => a.UserName != null && a.UserName.Contains(f.User.Trim()));
        if (!string.IsNullOrEmpty(f.Action)) q = q.Where(a => a.Action == f.Action);
        if (f.BranchId is int b && _branch.IsSystemAdmin) q = q.Where(a => a.BranchId == b);
        if (f.From is { } from) { var utc = ToUtc(from); q = q.Where(a => a.At >= utc); }
        if (f.To is { } to) { var utc = ToUtc(to.AddDays(1)); q = q.Where(a => a.At < utc); }

        var paged = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).ToPagedAsync(page, 50, ct);
        var branchNames = await _db.Branches.Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var rows = paged.Items.Select(a => new AuditRow(a, a.BranchId is int id ? branchNames.GetValueOrDefault(id) : null, ParseChanges(a.Changes))).ToList();

        return View(new AuditPageModel
        {
            Filter = f,
            Paged = new PagedList<AuditRow>(rows, paged.Total, paged.Page, paged.PageSize),
            EntityNames = await scoped.Select(a => a.EntityName).Distinct().OrderBy(n => n).ToListAsync(ct),
            Branches = _branch.IsSystemAdmin ? branchNames.OrderBy(x => x.Value).Select(x => (x.Key, x.Value)).ToList() : new(),
            AllBranches = _branch.IsSystemAdmin,
            Clock = _clock,
        });
    }

    /// <summary>System admins see everything; others only their current branch's changes.</summary>
    private IQueryable<AuditLog> Scope()
    {
        if (_branch.IsSystemAdmin) return _db.AuditLogs;
        var current = _branch.BranchId;
        return _db.AuditLogs.Where(a => a.BranchId == current);
    }

    private DateTime ToUtc(DateOnly branchDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(branchDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), _clock.TimeZone);

    /// <summary>Reads AuditLog.Changes: { "Field": ["old", "new"] } for updates, { "Field": ["value"] } for creates.</summary>
    public static List<Change> ParseChanges(string? json)
    {
        var list = new List<Change>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) { list.Add(new Change(p.Name, null, Text(p.Value), true)); continue; }
                var values = p.Value.EnumerateArray().Select(Text).ToList();
                list.Add(values.Count >= 2 ? new Change(p.Name, values[0], values[1], false) : new Change(p.Name, null, values.FirstOrDefault(), true));
            }
        }
        catch (JsonException)
        {
            list.Add(new Change("(details)", null, json, true));
        }
        return list;
    }

    /// <summary>Plain-English name for a record type ("IdentityUserClaim`1" becomes "User permission").</summary>
    public static string FriendlyEntity(string name)
    {
        var tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return name switch
        {
            "AppUser" => "User",
            "IdentityUserClaim" => "User permission",
            "IdentityUserRole" => "User role",
            "UserBranch" => "User branch access",
            "Course" => "Program",
            "CourseRankingSystem" => "Program ranking system",
            _ => EnumHelpers.Humanize(name),
        };
    }

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        _ => e.GetRawText(),
    };
}
