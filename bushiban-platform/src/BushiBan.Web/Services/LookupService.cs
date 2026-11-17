using BushiBan.Web.Data;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Services;

/// <summary>Dropdown options used across many screens (all filtered to the current branch).</summary>
public class LookupService
{
    private readonly AppDbContext _db;
    public LookupService(AppDbContext db) => _db = db;

    public async Task<List<SelectListItem>> CoursesAsync(int? selected = null, bool activeOnly = true) =>
        await _db.Courses.Where(c => !activeOnly || c.IsActive || c.Id == selected).OrderBy(c => c.Name)
            .Select(c => new SelectListItem(c.Name, c.Id.ToString(), c.Id == selected)).ToListAsync();

    /// <summary>Ranks in progression order (Standard System order first, then any others by name).</summary>
    public async Task<List<SelectListItem>> RanksAsync(int? selected = null)
    {
        var order = await _db.RankingSystemRanks.GroupBy(r => r.RankId).Select(g => new { g.Key, Min = g.Min(x => x.Index) }).ToDictionaryAsync(x => x.Key, x => x.Min);
        var ranks = await _db.Ranks.Where(r => r.IsActive || r.Id == selected).ToListAsync();
        return ranks.OrderBy(r => order.TryGetValue(r.Id, out var i) ? i : 999).ThenBy(r => r.Name)
            .Select(r => new SelectListItem(r.Name, r.Id.ToString(), r.Id == selected)).ToList();
    }

    public async Task<List<SelectListItem>> BeltSizesAsync(int? selected = null) =>
        await _db.BeltSizes.Where(b => b.IsActive || b.Id == selected).OrderBy(b => b.SizeInInch)
            .Select(b => new SelectListItem(b.Name, b.Id.ToString(), b.Id == selected)).ToListAsync();

    public async Task<List<SelectListItem>> FeeStructuresAsync(int? selected = null) =>
        await _db.FeeStructures.Where(f => f.IsActive || f.Id == selected).OrderBy(f => f.Name)
            .Select(f => new SelectListItem(f.Name, f.Id.ToString(), f.Id == selected)).ToListAsync();

    /// <summary>Cities grouped by state, e.g. for address pickers.</summary>
    public async Task<List<SelectListItem>> CitiesAsync(int? selected = null)
    {
        var cities = await _db.Cities.Where(c => c.IsActive || c.Id == selected).Include(c => c.State)
            .OrderBy(c => c.State!.Name).ThenBy(c => c.Name).ToListAsync();
        var groups = cities.Select(c => c.State!.Name).Distinct().ToDictionary(n => n, n => new SelectListGroup { Name = n });
        return cities.Select(c => new SelectListItem(c.Name, c.Id.ToString(), c.Id == selected) { Group = groups[c.State!.Name] }).ToList();
    }

    public async Task<List<SelectListItem>> DetailAccountsAsync(int? selected = null) =>
        await _db.DetailAccounts.Where(a => a.IsActive || a.Id == selected).OrderBy(a => a.Code)
            .Select(a => new SelectListItem(a.Code + " - " + a.Name, a.Id.ToString(), a.Id == selected)).ToListAsync();

    public async Task<List<SelectListItem>> StaffAsync(string? selected = null) =>
        await _db.Users.Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new SelectListItem(u.DisplayName ?? u.Email!, u.Id, u.Id == selected)).ToListAsync();
}
