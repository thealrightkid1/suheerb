using System.ComponentModel.DataAnnotations;
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

public record RankingSystemRow(int Id, string Name, string? Description, bool IsActive, int RankCount, string? FirstRank, string? FirstColor,
    string? LastRank, string? LastColor, int Programs);

public record RankStepView(int Index, string Rank, string? Color, int DurationInDays, int MinimumClasses);

public class RankingSystemDetails
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public List<RankStepView> Steps { get; set; } = new();
    public List<(int Id, string Name)> Programs { get; set; } = new();
    public int TotalDays => Steps.Sum(s => s.DurationInDays);
}

public class RankStepInput
{
    public int? RankId { get; set; }
    [Range(0, 3650, ErrorMessage = "Days must be between 0 and 3,650.")] public int DurationInDays { get; set; } = 90;
    [Range(0, 1000, ErrorMessage = "Minimum classes must be between 0 and 1,000.")] public int MinimumClasses { get; set; }
}

public class RankingSystemForm
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Give the ranking system a name."), StringLength(120)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RankStepInput> Steps { get; set; } = new();
    public List<SelectListItem> RankOptions { get; set; } = new();
    public Dictionary<string, string?> RankColors { get; set; } = new();
}

public class RankingSystemsController : Controller
{
    private readonly AppDbContext _db;
    private readonly LookupService _lookups;

    public RankingSystemsController(AppDbContext db, LookupService lookups) { _db = db; _lookups = lookups; }

    [HttpGet, HasPermission(P.RankingSystems.View)]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _db.RankingSystems.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q.Trim()));
        var rows = await query.OrderBy(s => s.Name).Select(s => new RankingSystemRow(
                s.Id, s.Name, s.Description, s.IsActive, s.Ranks.Count,
                s.Ranks.OrderBy(r => r.Index).Select(r => r.Rank!.Name).FirstOrDefault(),
                s.Ranks.OrderBy(r => r.Index).Select(r => r.Rank!.Color).FirstOrDefault(),
                s.Ranks.OrderByDescending(r => r.Index).Select(r => r.Rank!.Name).FirstOrDefault(),
                s.Ranks.OrderByDescending(r => r.Index).Select(r => r.Rank!.Color).FirstOrDefault(),
                _db.CourseRankingSystems.Count(l => l.RankingSystemId == s.Id && !l.Course!.IsDeleted)))
            .ToListAsync();
        ViewData["Search"] = q;
        return View(rows);
    }

    [HttpGet, HasPermission(P.RankingSystems.View)]
    public async Task<IActionResult> Details(int id)
    {
        var model = await _db.RankingSystems.Where(s => s.Id == id).Select(s => new RankingSystemDetails
        {
            Id = s.Id, Name = s.Name, Description = s.Description, IsActive = s.IsActive,
            Steps = s.Ranks.OrderBy(r => r.Index).Select(r => new RankStepView(r.Index, r.Rank!.Name, r.Rank.Color, r.DurationInDays, r.MinimumClasses)).ToList(),
        }).FirstOrDefaultAsync();
        if (model is null) return NotFound();
        model.Programs = (await _db.CourseRankingSystems.Where(l => l.RankingSystemId == id && !l.Course!.IsDeleted)
                .OrderBy(l => l.Course!.Name).Select(l => new { l.CourseId, l.Course!.Name }).ToListAsync())
            .Select(x => (x.CourseId, x.Name)).ToList();
        return View(model);
    }

    [HttpGet, HasPermission(P.RankingSystems.Create)]
    public async Task<IActionResult> Create()
    {
        var form = new RankingSystemForm { Steps = { new RankStepInput() } };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.RankingSystems.Create)]
    public async Task<IActionResult> Create(RankingSystemForm form)
    {
        await ValidateAsync(form);
        if (!ModelState.IsValid) return await FormView(form);
        var system = new RankingSystem();
        Apply(system, form);
        _db.RankingSystems.Add(system);
        await _db.SaveChangesAsync();
        this.Success($"Ranking system \"{system.Name}\" created.");
        return RedirectToAction(nameof(Details), new { id = system.Id });
    }

    [HttpGet, HasPermission(P.RankingSystems.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var system = await _db.RankingSystems.AsNoTracking().Include(s => s.Ranks).FirstOrDefaultAsync(s => s.Id == id);
        if (system is null) return NotFound();
        var form = new RankingSystemForm
        {
            Id = system.Id, Name = system.Name, Description = system.Description, IsActive = system.IsActive,
            Steps = system.Ranks.OrderBy(r => r.Index).Select(r => new RankStepInput { RankId = r.RankId, DurationInDays = r.DurationInDays, MinimumClasses = r.MinimumClasses }).ToList(),
        };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.RankingSystems.Edit)]
    public async Task<IActionResult> Edit(int id, RankingSystemForm form)
    {
        var system = await _db.RankingSystems.Include(s => s.Ranks).FirstOrDefaultAsync(s => s.Id == id);
        if (system is null) return NotFound();
        form.Id = id;
        await ValidateAsync(form);
        if (!ModelState.IsValid) return await FormView(form);
        _db.RankingSystemRanks.RemoveRange(system.Ranks);
        system.Ranks.Clear();
        Apply(system, form);
        await _db.SaveChangesAsync();
        this.Success("Ranking system saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, HasPermission(P.RankingSystems.Delete)]
    public async Task<IActionResult> Delete(int id)
    {
        var system = await _db.RankingSystems.FirstOrDefaultAsync(s => s.Id == id);
        if (system is null) return NotFound();
        var programs = await _db.CourseRankingSystems.Where(l => l.RankingSystemId == id && !l.Course!.IsDeleted).Select(l => l.Course!.Name).ToListAsync();
        if (programs.Count > 0)
        {
            this.Error($"Can't delete \"{system.Name}\": it's used by {string.Join(", ", programs)}. Remove it from those programs first.");
            return RedirectToAction(nameof(Index));
        }
        _db.RankingSystems.Remove(system);
        await _db.SaveChangesAsync();
        this.Success("Ranking system deleted.");
        return RedirectToAction(nameof(Index));
    }

    private static void Apply(RankingSystem system, RankingSystemForm form)
    {
        system.Name = form.Name.Trim();
        system.Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        system.IsActive = form.IsActive;
        system.Ranks = form.Steps.Select((s, i) => new RankingSystemRank
        {
            RankId = s.RankId!.Value, Index = i + 1, DurationInDays = s.DurationInDays, MinimumClasses = s.MinimumClasses,
        }).ToList();
    }

    private async Task ValidateAsync(RankingSystemForm form)
    {
        form.Steps ??= new();
        if (form.Steps.Count == 0) ModelState.AddModelError(nameof(form.Steps), "Add at least one rank.");
        var validRanks = (await _db.Ranks.Select(r => r.Id).ToListAsync()).ToHashSet();
        for (var i = 0; i < form.Steps.Count; i++)
        {
            if (!validRanks.Contains(form.Steps[i].RankId ?? 0)) ModelState.AddModelError($"Steps[{i}].RankId", "Pick a rank.");
        }
        var duplicates = form.Steps.Where(s => s.RankId != null).GroupBy(s => s.RankId!.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            var names = await _db.Ranks.Where(r => duplicates.Contains(r.Id)).Select(r => r.Name).ToListAsync();
            ModelState.AddModelError(nameof(form.Steps), $"Each rank can appear only once. Listed more than once: {string.Join(", ", names)}.");
        }
        var name = form.Name?.Trim() ?? "";
        if (name.Length > 0 && await _db.RankingSystems.AnyAsync(s => s.Name == name && s.Id != form.Id))
            ModelState.AddModelError(nameof(form.Name), "Another ranking system already has this name.");
    }

    private async Task<IActionResult> FormView(RankingSystemForm form)
    {
        form.RankOptions = await _lookups.RanksAsync();
        form.RankColors = await _db.Ranks.ToDictionaryAsync(r => r.Id.ToString(), r => r.Color);
        return View("Form", form);
    }
}
