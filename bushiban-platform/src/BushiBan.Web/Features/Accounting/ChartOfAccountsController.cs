using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Accounting;

public record ChartDetail(int Id, string Code, string Name, bool IsActive, decimal Balance);
public record ChartSub(int Id, string Code, string Name, bool IsActive, IReadOnlyList<ChartDetail> Details)
{
    public decimal Balance => Details.Sum(d => d.Balance);
}
public record ChartMain(int Id, string Code, string Name, AccountType Type, bool IsActive, IReadOnlyList<ChartSub> Subs)
{
    public decimal Balance => Subs.Sum(s => s.Balance);
}
public record ChartOfAccountsPage(DateOnly AsOf, IReadOnlyList<ChartMain> Mains)
{
    public IEnumerable<(AccountType Type, decimal Total)> TypeTotals =>
        Enum.GetValues<AccountType>().Select(t => (t, Mains.Where(m => m.Type == t).Sum(m => m.Balance)));
}

/// <summary>The whole chart as a tree (main → sub → detail) with balances from posted vouchers to date.</summary>
[HasPermission(P.MainAccounts.View + "|" + P.SubAccounts.View + "|" + P.DetailAccounts.View)]
public class ChartOfAccountsController : Controller
{
    private readonly AppDbContext _db;
    private readonly AccountingService _accounting;
    private readonly BranchClock _clock;

    public ChartOfAccountsController(AppDbContext db, AccountingService accounting, BranchClock clock) { _db = db; _accounting = accounting; _clock = clock; }

    public async Task<IActionResult> Index()
    {
        var today = _clock.Today;
        var totals = await _accounting.TotalsAsync(null, today);
        var mains = await _db.MainAccounts.OrderBy(m => m.Code).Select(m => new { m.Id, m.Code, m.Name, m.AccountType, m.IsActive }).ToListAsync();
        var subs = await _db.SubAccounts.OrderBy(s => s.Code).Select(s => new { s.Id, s.Code, s.Name, s.IsActive, s.MainAccountId }).ToListAsync();
        var details = await _db.DetailAccounts.OrderBy(d => d.Code).Select(d => new { d.Id, d.Code, d.Name, d.IsActive, d.SubAccountId }).ToListAsync();

        var detailsBySub = details.ToLookup(d => d.SubAccountId);
        var subsByMain = subs.ToLookup(s => s.MainAccountId);
        var tree = mains.Select(m =>
        {
            var debitNature = AccountingService.IsDebitNature(m.AccountType);
            return new ChartMain(m.Id, m.Code, m.Name, m.AccountType, m.IsActive, subsByMain[m.Id].Select(s =>
                new ChartSub(s.Id, s.Code, s.Name, s.IsActive, detailsBySub[s.Id].Select(d =>
                {
                    var (dr, cr) = totals.TryGetValue(d.Id, out var t) ? t : (0m, 0m);
                    return new ChartDetail(d.Id, d.Code, d.Name, d.IsActive, Math.Round(debitNature ? dr - cr : cr - dr, 2));
                }).ToList())).ToList());
        }).ToList();
        return View(new ChartOfAccountsPage(today, tree));
    }
}
