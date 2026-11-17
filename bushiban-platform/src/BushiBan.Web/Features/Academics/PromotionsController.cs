using BushiBan.Web.Data;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Academics;

public class PromotionsPage
{
    public int? CourseId { get; set; }
    public List<SelectListItem> Courses { get; set; } = new();
    public List<PromotionCandidate> Ready { get; set; } = new();
    public List<PromotionCandidate> AlmostReady { get; set; } = new();
    public int NotYet { get; set; }
    public int NotOnPath { get; set; }
    public int AtTopRank { get; set; }
    public bool CanPromote { get; set; }
    public DateOnly Today { get; set; }
}

public record PromotionHistoryRow(int Id, int StudentId, string Student, string StudentNo, string? FromRank, string? FromColor,
    string ToRank, string? ToColor, DateOnly PromotedOn, string? Notes, string? RecordedBy);

public class PromotionsController : Controller
{
    private const string ViewOrPromote = P.Ranks.View + "|" + P.Ranks.Promote;

    private readonly AppDbContext _db;
    private readonly PromotionService _promotions;
    private readonly LookupService _lookups;
    private readonly BranchClock _clock;

    public PromotionsController(AppDbContext db, PromotionService promotions, LookupService lookups, BranchClock clock)
    {
        _db = db; _promotions = promotions; _lookups = lookups; _clock = clock;
    }

    /// <summary>"Ready to test": students who've met their current belt's time and class requirements, plus those close to it.</summary>
    [HttpGet, HasPermission(ViewOrPromote)]
    public async Task<IActionResult> Index(int? courseId)
    {
        var overview = await _promotions.OverviewAsync(courseId);
        var page = new PromotionsPage
        {
            CourseId = courseId,
            Courses = await _lookups.CoursesAsync(courseId),
            Ready = overview.Candidates.Where(c => c.IsReady).OrderByDescending(c => c.DaysAtRank - c.RequiredDays).ThenBy(c => c.Name).ToList(),
            AlmostReady = overview.Candidates.Where(c => !c.IsReady && c.Progress >= PromotionService.AlmostReadyThreshold)
                .OrderByDescending(c => c.Progress).ThenBy(c => c.Name).ToList(),
            NotYet = overview.Candidates.Count(c => !c.IsReady && c.Progress < PromotionService.AlmostReadyThreshold),
            NotOnPath = overview.NotOnPath,
            AtTopRank = overview.AtTopRank,
            CanPromote = User.HasPermission(P.Ranks.Promote),
            Today = _clock.Today,
        };
        return View(page);
    }

    [HttpPost, HasPermission(P.Ranks.Promote)]
    public async Task<IActionResult> Promote(List<int>? studentIds, int? only, DateOnly? promotedOn, string? notes, int? courseId, string? returnUrl)
    {
        var back = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(Index), new { courseId })!;
        // A row's own "Promote" button promotes just that student, whatever else is ticked.
        if (only is int one) studentIds = new List<int> { one };
        if (studentIds is null || studentIds.Count == 0)
        {
            this.Error("Tick at least one student to promote.");
            return LocalRedirect(back);
        }
        var date = promotedOn ?? _clock.Today;
        if (date > _clock.Today)
        {
            this.Error("The promotion date can't be in the future.");
            return LocalRedirect(back);
        }

        var result = await _promotions.PromoteAsync(studentIds, date, notes);
        if (result.Promoted.Count > 0)
            this.Success(result.Promoted.Count == 1 ? $"{result.Promoted[0]} has been promoted. Congratulations!" : $"{result.Promoted.Count} students have been promoted.");
        if (result.Skipped.Count > 0)
            this.Error($"Not promoted: {string.Join(", ", result.Skipped)}. They're already at the top belt, or their belt isn't on their program's ranking system.");
        return LocalRedirect(back);
    }

    [HttpGet, HasPermission(ViewOrPromote)]
    public async Task<IActionResult> History(int? courseId, int page = 1)
    {
        var q = _db.RankPromotions.AsQueryable();
        if (courseId is int cid) q = q.Where(p => p.Student!.CourseId == cid);
        var rows = await q.OrderByDescending(p => p.PromotedOn).ThenByDescending(p => p.Id)
            .Select(p => new PromotionHistoryRow(p.Id, p.StudentId, p.Student!.FirstName + " " + p.Student.LastName, p.Student.StudentNo,
                p.FromRank != null ? p.FromRank.Name : null, p.FromRank != null ? p.FromRank.Color : null,
                p.ToRank!.Name, p.ToRank.Color, p.PromotedOn, p.Notes, p.CreatedBy))
            .ToPagedAsync(page, 50);
        ViewData["Courses"] = await _lookups.CoursesAsync(courseId, activeOnly: false);
        ViewData["CourseId"] = courseId;
        return View(rows);
    }
}
