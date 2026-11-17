using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Infrastructure;

/// <summary>Branch picker in the top bar.</summary>
public class BranchSwitcherViewComponent : ViewComponent
{
    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;
    public BranchSwitcherViewComponent(AppDbContext db, ICurrentBranch branch) { _db = db; _branch = branch; }

    public record Model(int? CurrentId, string CurrentName, List<(int Id, string Name)> Options);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var q = _db.Branches.Where(b => b.IsActive);
        if (!_branch.IsSystemAdmin) q = q.Where(b => _branch.AllowedBranchIds.Contains(b.Id));
        var options = (await q.OrderBy(b => b.Name).Select(b => new { b.Id, b.Name }).ToListAsync()).Select(b => (b.Id, b.Name)).ToList();
        var current = options.FirstOrDefault(o => o.Id == _branch.BranchId);
        return View(new Model(_branch.BranchId, current.Name ?? "No branch", options));
    }
}

/// <summary>Counts shown next to sidebar items (follow-ups due, new registrations).</summary>
public class NavBadgesViewComponent : ViewComponent
{
    private readonly AppDbContext _db;
    private readonly BranchClock _clock;
    public NavBadgesViewComponent(AppDbContext db, BranchClock clock) { _db = db; _clock = clock; }

    public async Task<IViewComponentResult> InvokeAsync(string badge)
    {
        int count = 0;
        if (badge == "followups" && UserClaimsPrincipal.HasPermission(Permissions.Leads.View))
        {
            var today = _clock.Today;
            count = await _db.Leads.CountAsync(l => l.ConvertedStudentId == null && l.NextFollowUpDate != null && l.NextFollowUpDate <= today
                                                     && l.ProspectPhase != ProspectPhase.StopFollowingUp && l.ProspectPhase != ProspectPhase.Enrolled);
        }
        else if (badge == "registrations")
        {
            count = await _db.FormSubmissions.CountAsync(s => s.Status == SubmissionStatus.New);
        }
        return Content(count > 0 ? count.ToString() : "");
    }
}
