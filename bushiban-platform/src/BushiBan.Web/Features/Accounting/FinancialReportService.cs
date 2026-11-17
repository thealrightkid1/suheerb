using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Accounting;

/// <summary>A detail account as reports see it. When branches are consolidated, one row stands for every branch's account with that code.</summary>
public record ReportAccount(string Code, string Name, string SubCode, string SubName, string MainCode, string MainName, AccountType Type, IReadOnlyList<int> Ids)
{
    public bool IsDebitNature => AccountingService.IsDebitNature(Type);
    /// <summary>Contra revenue (sales discounts and returns) reduces revenue.</summary>
    public bool IsContraRevenue => Type == AccountType.Revenue && (MainCode == "40-03" || MainName.Contains("contra", StringComparison.OrdinalIgnoreCase));
    /// <summary>Debit minus credit for asset and expense accounts, credit minus debit for the rest.</summary>
    public decimal Natural(decimal debit, decimal credit) => Math.Round(IsDebitNature ? debit - credit : credit - debit, 2);
}

public record AccountAmount(ReportAccount Account, decimal Debit, decimal Credit)
{
    public decimal Balance => Account.Natural(Debit, Credit);
}

public record TrialBalanceReport(DateOnly? From, DateOnly To, IReadOnlyList<AccountAmount> Rows)
{
    public decimal TotalDebit => Rows.Sum(r => r.Debit);
    public decimal TotalCredit => Rows.Sum(r => r.Credit);
    public bool Balanced => TotalDebit == TotalCredit;
}

public record LedgerLine(DateOnly Date, int VoucherId, string VoucherNo, VoucherType Type, string Description, string? Narration, decimal Debit, decimal Credit, decimal Balance);

public record LedgerReport(ReportAccount Account, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<LedgerLine> Lines)
{
    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
    public decimal Closing => Lines.Count == 0 ? Opening : Lines[^1].Balance;
}

public record StatementLine(string Code, string Name, decimal Amount);

public record StatementGroup(string Code, string Name, IReadOnlyList<StatementLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}

public record IncomeStatementReport(DateOnly From, DateOnly To, IReadOnlyList<StatementGroup> Revenue, IReadOnlyList<StatementGroup> ContraRevenue, IReadOnlyList<StatementGroup> Expenses)
{
    public decimal GrossRevenue => Revenue.Sum(g => g.Total);
    /// <summary>Shown as a positive number that is subtracted from revenue.</summary>
    public decimal TotalContraRevenue => ContraRevenue.Sum(g => g.Total);
    public decimal NetRevenue => GrossRevenue - TotalContraRevenue;
    public decimal TotalExpenses => Expenses.Sum(g => g.Total);
    public decimal NetIncome => NetRevenue - TotalExpenses;
}

public record BalanceSheetReport(DateOnly AsOf, IReadOnlyList<StatementGroup> Assets, IReadOnlyList<StatementGroup> Liabilities, IReadOnlyList<StatementGroup> Equity, decimal EarningsToDate)
{
    public decimal TotalAssets => Assets.Sum(g => g.Total);
    public decimal TotalLiabilities => Liabilities.Sum(g => g.Total);
    public decimal TotalEquity => Equity.Sum(g => g.Total) + EarningsToDate;
    public decimal LiabilitiesAndEquity => TotalLiabilities + TotalEquity;
    public bool Balanced => Math.Round(TotalAssets - LiabilitiesAndEquity, 2) == 0;
}

/// <summary>
/// Trial balance, ledger, income statement and balance sheet. Only posted vouchers count (drafts are left out;
/// reversed vouchers cancel out with their reversal). Works on whatever branch scope is current, so wrapping a call in
/// <c>ICurrentBranch.Override(null, disableFilter: true)</c> consolidates every branch by account code.
/// </summary>
public class FinancialReportService
{
    private readonly AppDbContext _db;
    private readonly AccountingService _accounting;

    public FinancialReportService(AppDbContext db, AccountingService accounting) { _db = db; _accounting = accounting; }

    /// <summary>Detail accounts in code order, merged by code.</summary>
    public async Task<List<ReportAccount>> AccountsAsync(CancellationToken ct = default)
    {
        var rows = await _db.DetailAccounts
            .Select(d => new
            {
                d.Id, d.Code, d.Name, SubCode = d.SubAccount!.Code, SubName = d.SubAccount.Name,
                MainCode = d.SubAccount.MainAccount!.Code, MainName = d.SubAccount.MainAccount.Name, Type = d.SubAccount.MainAccount.AccountType,
            })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Code).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var f = g.OrderBy(r => r.Id).First();
                return new ReportAccount(f.Code, f.Name, f.SubCode, f.SubName, f.MainCode, f.MainName, f.Type, g.Select(r => r.Id).ToList());
            })
            .ToList();
    }

    /// <summary>Debit and credit totals per account for posted vouchers dated in the range.</summary>
    public async Task<List<AccountAmount>> AmountsAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var accounts = await AccountsAsync(ct);
        var totals = await _accounting.TotalsAsync(from, to, ct);
        return accounts.Select(a =>
        {
            decimal dr = 0, cr = 0;
            foreach (var id in a.Ids)
                if (totals.TryGetValue(id, out var t)) { dr += t.Debit; cr += t.Credit; }
            return new AccountAmount(a, Math.Round(dr, 2), Math.Round(cr, 2));
        }).ToList();
    }

    public async Task<TrialBalanceReport> TrialBalanceAsync(DateOnly? from, DateOnly to, bool showZero, CancellationToken ct = default)
    {
        var amounts = await AmountsAsync(from, to, ct);
        return new TrialBalanceReport(from, to, amounts.Where(a => showZero || a.Debit != 0 || a.Credit != 0).ToList());
    }

    public async Task<LedgerReport?> LedgerAsync(string code, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var account = (await AccountsAsync(ct)).FirstOrDefault(a => a.Code == code);
        if (account is null) return null;
        var ids = account.Ids;

        var posted = _db.VoucherEntries.Where(e => ids.Contains(e.DetailAccountId) && e.Voucher!.Status != VoucherStatus.Draft && !e.Voucher.IsDeleted);
        var before = posted.Where(e => e.Voucher!.Date < from);
        var opening = account.Natural(await before.SumAsync(e => e.Debit, ct), await before.SumAsync(e => e.Credit, ct));

        var rows = await posted.Where(e => e.Voucher!.Date >= from && e.Voucher.Date <= to)
            .OrderBy(e => e.Voucher!.Date).ThenBy(e => e.Voucher!.Id).ThenBy(e => e.Id)
            .Select(e => new { e.Voucher!.Date, e.VoucherId, e.Voucher.VoucherNo, e.Voucher.Type, e.Voucher.Description, e.Narration, e.Debit, e.Credit })
            .ToListAsync(ct);

        var running = opening;
        var lines = new List<LedgerLine>(rows.Count);
        foreach (var r in rows)
        {
            running += account.Natural(r.Debit, r.Credit);
            lines.Add(new LedgerLine(r.Date, r.VoucherId, r.VoucherNo, r.Type, r.Description, r.Narration, Math.Round(r.Debit, 2), Math.Round(r.Credit, 2), Math.Round(running, 2)));
        }
        return new LedgerReport(account, from, to, opening, lines);
    }

    public async Task<IncomeStatementReport> IncomeStatementAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var amounts = await AmountsAsync(from, to, ct);
        var revenue = amounts.Where(a => a.Account.Type == AccountType.Revenue && !a.Account.IsContraRevenue);
        // Contra revenue has a debit balance; show it as a positive deduction.
        var contra = amounts.Where(a => a.Account.IsContraRevenue).Select(a => a with { Debit = a.Credit, Credit = a.Debit });
        var expenses = amounts.Where(a => a.Account.Type == AccountType.Expenses);
        return new IncomeStatementReport(from, to, GroupBySub(revenue), GroupBySub(contra), GroupBySub(expenses));
    }

    public async Task<BalanceSheetReport> BalanceSheetAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var amounts = await AmountsAsync(null, asOf, ct);
        // Revenue and expenses are never closed into retained earnings, so their running total is the period's earnings.
        var earnings = amounts.Where(a => a.Account.Type == AccountType.Revenue).Sum(a => a.Balance)
                     - amounts.Where(a => a.Account.Type == AccountType.Expenses).Sum(a => a.Balance);
        return new BalanceSheetReport(asOf,
            GroupBySub(amounts.Where(a => a.Account.Type == AccountType.Assets)),
            GroupBySub(amounts.Where(a => a.Account.Type == AccountType.Liabilities)),
            GroupBySub(amounts.Where(a => a.Account.Type == AccountType.Equity)),
            Math.Round(earnings, 2));
    }

    private static List<StatementGroup> GroupBySub(IEnumerable<AccountAmount> amounts) =>
        amounts.Where(a => a.Balance != 0)
            .GroupBy(a => (a.Account.SubCode, a.Account.SubName))
            .OrderBy(g => g.Key.SubCode, StringComparer.Ordinal)
            .Select(g => new StatementGroup(g.Key.SubCode, g.Key.SubName, g.Select(a => new StatementLine(a.Account.Code, a.Account.Name, a.Balance)).ToList()))
            .ToList();
}
