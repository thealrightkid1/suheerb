using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.SystemTables;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

/// <summary>"What's new": release notes everyone can read; people with Changelogs.Manage publish them.</summary>
public class ChangelogController : Controller
{
    private readonly AppDbContext _db;
    private readonly BranchClock _clock;
    public ChangelogController(AppDbContext db, BranchClock clock) { _db = db; _clock = clock; }

    public class EntryInput
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "Enter a version, e.g. 1.2.0."), StringLength(30)] public string? Version { get; set; }
        [Required(ErrorMessage = "Choose the release date.")] public DateOnly? ReleasedOn { get; set; }
        [Required(ErrorMessage = "Give the release a title."), StringLength(200)] public string? Title { get; set; }
        [Required(ErrorMessage = "Describe what changed.")] public string? Body { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["CanManage"] = User.HasPermission(P.Changelogs.Manage);
        return View(await _db.ChangelogEntries.OrderByDescending(e => e.ReleasedOn).ThenByDescending(e => e.Id).ToListAsync(ct));
    }

    [HttpGet, HasPermission(P.Changelogs.Manage)]
    public IActionResult Create() => View("Form", new EntryInput { ReleasedOn = _clock.Today });

    [HttpPost, HasPermission(P.Changelogs.Manage)]
    public async Task<IActionResult> Create(EntryInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View("Form", input);
        var entry = new ChangelogEntry();
        Apply(entry, input);
        _db.ChangelogEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        this.Success($"Release {entry.Version} published.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet, HasPermission(P.Changelogs.Manage)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var e = await _db.ChangelogEntries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        return View("Form", new EntryInput { Id = e.Id, Version = e.Version, ReleasedOn = e.ReleasedOn, Title = e.Title, Body = e.Body });
    }

    [HttpPost, HasPermission(P.Changelogs.Manage)]
    public async Task<IActionResult> Edit(int id, EntryInput input, CancellationToken ct)
    {
        var e = await _db.ChangelogEntries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        input.Id = id;
        if (!ModelState.IsValid) return View("Form", input);
        Apply(e, input);
        await _db.SaveChangesAsync(ct);
        this.Success($"Release {e.Version} saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Changelogs.Manage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var e = await _db.ChangelogEntries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        _db.ChangelogEntries.Remove(e);
        await _db.SaveChangesAsync(ct);
        this.Success($"Release {e.Version} deleted.");
        return RedirectToAction(nameof(Index));
    }

    private static void Apply(ChangelogEntry e, EntryInput i)
    {
        e.Version = i.Version!.Trim();
        e.ReleasedOn = i.ReleasedOn!.Value;
        e.Title = i.Title!.Trim();
        e.Body = i.Body!.Trim();
    }

    /// <summary>Splits a body into paragraphs and bullet lists: lines starting with "- " are bullets.</summary>
    public static IEnumerable<(bool IsList, List<string> Lines)> Blocks(string body)
    {
        var current = new List<string>();
        bool? isList = null;
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0) { yield return (isList == true, current); current = new(); isList = null; }
                continue;
            }
            var bullet = line.TrimStart().StartsWith("- ");
            if (isList is not null && isList != bullet) { yield return (isList == true, current); current = new(); }
            isList = bullet;
            current.Add(bullet ? line.TrimStart()[2..].Trim() : line.Trim());
        }
        if (current.Count > 0) yield return (isList == true, current);
    }
}
