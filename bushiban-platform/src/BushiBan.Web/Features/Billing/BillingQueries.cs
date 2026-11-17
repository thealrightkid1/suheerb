using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Billing;

/// <summary>Small read-only queries shared by the billing screens (all scoped to the current branch).</summary>
internal static class BillingQueries
{
    /// <summary>Students for a searchable dropdown: active students first, "Name (PAS-00012)".</summary>
    public static async Task<List<SelectListItem>> StudentOptionsAsync(this AppDbContext db, int? selected)
    {
        var rows = await db.Students.Where(s => s.IsActive || s.Id == selected)
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new { s.Id, s.FirstName, s.LastName, s.StudentNo, s.IsActive })
            .ToListAsync();
        return rows.Select(s => new SelectListItem($"{s.FirstName} {s.LastName} ({s.StudentNo}){(s.IsActive ? "" : " - inactive")}", s.Id.ToString(), s.Id == selected)).ToList();
    }

    /// <summary>Detail accounts of the given types, grouped by type, "code - name".</summary>
    public static async Task<List<SelectListItem>> AccountOptionsAsync(this AppDbContext db, int? selected, params AccountType[] types)
    {
        var rows = await db.DetailAccounts
            .Where(a => (a.IsActive || a.Id == selected) && (types.Length == 0 || types.Contains(a.SubAccount!.MainAccount!.AccountType)))
            .OrderBy(a => a.Code)
            .Select(a => new { a.Id, a.Code, a.Name, Type = a.SubAccount!.MainAccount!.AccountType })
            .ToListAsync();
        var groups = rows.Select(r => r.Type).Distinct().ToDictionary(t => t, t => new SelectListGroup { Name = t.ToString() });
        return rows.Select(r => new SelectListItem($"{r.Code} - {r.Name}", r.Id.ToString(), r.Id == selected) { Group = groups[r.Type] }).ToList();
    }

    public static async Task<Letterhead> LetterheadAsync(this AppDbContext db, int? branchId) =>
        await db.Branches.Where(b => b.Id == branchId)
            .Select(b => new Letterhead(b.School != null ? b.School.Name : "Bushi Ban International", b.Name, b.Address,
                b.City == null ? b.ZipCode : b.City.Name + ", " + (b.City.State != null ? b.City.State.Name : "") + " " + (b.ZipCode ?? ""),
                b.Phone, b.Email))
            .FirstOrDefaultAsync()
        ?? new Letterhead("Bushi Ban International", "", null, null, null, null);

    public static Task<StudentHeader?> StudentHeaderAsync(this AppDbContext db, int studentId) =>
        db.Students.Where(s => s.Id == studentId)
            .Select(s => new StudentHeader(s.Id, s.FirstName + " " + s.LastName, s.StudentNo,
                s.Address2 == null ? s.Address1 : s.Address1 + ", " + s.Address2,
                s.City == null ? s.ZipCode : s.City.Name + ", " + (s.City.State != null ? s.City.State.Name : "") + " " + (s.ZipCode ?? ""),
                s.Email1, s.Phone1))
            .FirstOrDefaultAsync();
}
