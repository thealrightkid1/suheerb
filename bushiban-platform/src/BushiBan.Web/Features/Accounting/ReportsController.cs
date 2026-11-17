using BushiBan.Web.Data;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Accounting;

/// <summary>Query string shared by the financial reports.</summary>
public class ReportFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public DateOnly? AsOf { get; set; }
    public string? Code { get; set; }
    public bool ShowZero { get; set; }
    /// <summary>System admins only: every branch, merged by account code.</summary>
    public bool Consolidated { get; set; }
    /// <summary>"csv" downloads the report instead of showing it.</summary>
    public string? Format { get; set; }
}

public class ReportPage<T>
{
    public required T? Report { get; init; }
    public required ReportFilter Filter { get; init; }
    public required string Scope { get; init; }
    public required bool CanConsolidate { get; init; }
    public List<SelectListItem> Accounts { get; init; } = new();
}

[HasPermission(P.Finance.Reports)]
public class ReportsController : Controller
{
    private readonly FinancialReportService _reports;
    private readonly AccountingService _accounting;
    private readonly ICurrentBranch _branch;
    private readonly BranchClock _clock;
    private readonly AppDbContext _db;

    public ReportsController(FinancialReportService reports, AccountingService accounting, ICurrentBranch branch, BranchClock clock, AppDbContext db)
    {
        _reports = reports; _accounting = accounting; _branch = branch; _clock = clock; _db = db;
    }

    public IActionResult Index() => RedirectToAction(nameof(TrialBalance));

    public async Task<IActionResult> TrialBalance(ReportFilter f)
    {
        await DefaultPeriodAsync(f);
        var report = await InScope(f, () => _reports.TrialBalanceAsync(f.From, f.To!.Value, f.ShowZero));
        if (IsCsv(f))
        {
            var csv = new Csv().Row("Trial balance", await ScopeNameAsync(f)).Row("From", f.From, "To", f.To).Blank()
                .Row("Code", "Account", "Main category", "Debit", "Credit", "Balance");
            foreach (var r in report.Rows) csv.Row(r.Account.Code, r.Account.Name, r.Account.MainName, r.Debit, r.Credit, r.Balance);
            return csv.Row("", "Total", "", report.TotalDebit, report.TotalCredit, "").ToFile($"trial-balance-{f.To:yyyy-MM-dd}.csv");
        }
        return View(await PageAsync(report, f));
    }

    public async Task<IActionResult> Ledger(ReportFilter f)
    {
        await DefaultPeriodAsync(f);
        var accounts = await InScope(f, () => _reports.AccountsAsync());
        LedgerReport? report = null;
        if (!string.IsNullOrWhiteSpace(f.Code))
        {
            report = await InScope(f, () => _reports.LedgerAsync(f.Code.Trim(), f.From!.Value, f.To!.Value));
            if (report is null) ModelState.AddModelError("", "That account wasn't found.");
        }
        if (IsCsv(f) && report is not null)
        {
            var csv = new Csv().Row("Account ledger", await ScopeNameAsync(f)).Row("Account", report.Account.Code, report.Account.Name)
                .Row("From", report.From, "To", report.To).Blank()
                .Row("Date", "Voucher no", "Type", "Description", "Narration", "Debit", "Credit", "Balance")
                .Row(report.From, "", "", "Opening balance", "", "", "", report.Opening);
            foreach (var l in report.Lines) csv.Row(l.Date, l.VoucherNo, l.Type.DisplayName(), l.Description, l.Narration, l.Debit, l.Credit, l.Balance);
            return csv.Row(report.To, "", "", "Closing balance", "", report.TotalDebit, report.TotalCredit, report.Closing)
                .ToFile($"ledger-{report.Account.Code}-{f.To:yyyy-MM-dd}.csv");
        }
        var page = await PageAsync(report, f);
        page.Accounts.AddRange(accounts.Select(a => new SelectListItem($"{a.Code} - {a.Name}", a.Code, a.Code == f.Code)));
        return View(page);
    }

    public async Task<IActionResult> IncomeStatement(ReportFilter f)
    {
        await DefaultPeriodAsync(f);
        var report = await InScope(f, () => _reports.IncomeStatementAsync(f.From!.Value, f.To!.Value));
        if (IsCsv(f))
        {
            var csv = new Csv().Row("Income statement", await ScopeNameAsync(f)).Row("From", f.From, "To", f.To).Blank();
            void Section(string title, IReadOnlyList<StatementGroup> groups, decimal total)
            {
                csv.Row(title);
                foreach (var g in groups)
                {
                    csv.Row(g.Code, g.Name);
                    foreach (var l in g.Lines) csv.Row(l.Code, "  " + l.Name, l.Amount);
                    csv.Row("", "Total " + g.Name, g.Total);
                }
                csv.Row("", "Total " + title.ToLowerInvariant(), total).Blank();
            }
            Section("Revenue", report.Revenue, report.GrossRevenue);
            if (report.ContraRevenue.Count > 0) Section("Less discounts and returns", report.ContraRevenue, report.TotalContraRevenue);
            csv.Row("", "Net revenue", report.NetRevenue).Blank();
            Section("Expenses", report.Expenses, report.TotalExpenses);
            return csv.Row("", "Net income", report.NetIncome).ToFile($"income-statement-{f.To:yyyy-MM-dd}.csv");
        }
        return View(await PageAsync(report, f));
    }

    public async Task<IActionResult> BalanceSheet(ReportFilter f)
    {
        f.AsOf ??= _clock.Today;
        var report = await InScope(f, () => _reports.BalanceSheetAsync(f.AsOf.Value));
        if (IsCsv(f))
        {
            var csv = new Csv().Row("Balance sheet", await ScopeNameAsync(f)).Row("As of", f.AsOf).Blank();
            void Section(string title, IReadOnlyList<StatementGroup> groups)
            {
                csv.Row(title);
                foreach (var g in groups)
                {
                    csv.Row(g.Code, g.Name);
                    foreach (var l in g.Lines) csv.Row(l.Code, "  " + l.Name, l.Amount);
                    csv.Row("", "Total " + g.Name, g.Total);
                }
            }
            Section("Assets", report.Assets);
            csv.Row("", "Total assets", report.TotalAssets).Blank();
            Section("Liabilities", report.Liabilities);
            csv.Row("", "Total liabilities", report.TotalLiabilities).Blank();
            Section("Equity", report.Equity);
            csv.Row("", "Earnings to date (not yet closed)", report.EarningsToDate)
               .Row("", "Total equity", report.TotalEquity).Blank()
               .Row("", "Total liabilities and equity", report.LiabilitiesAndEquity);
            return csv.ToFile($"balance-sheet-{f.AsOf:yyyy-MM-dd}.csv");
        }
        return View(await PageAsync(report, f));
    }

    private static bool IsCsv(ReportFilter f) => string.Equals(f.Format, "csv", StringComparison.OrdinalIgnoreCase);

    /// <summary>Defaults to the current fiscal year so far.</summary>
    private async Task DefaultPeriodAsync(ReportFilter f)
    {
        var today = _clock.Today;
        f.To ??= today;
        if (f.From is null)
        {
            var fy = await _accounting.FiscalYearForAsync(f.To.Value);
            f.From = fy?.StartDate ?? new DateOnly(f.To.Value.Year, 1, 1);
        }
        if (f.From > f.To) (f.From, f.To) = (f.To, f.From);
    }

    /// <summary>Runs the query for this branch, or for every branch when a system admin asks for a consolidated report.</summary>
    private async Task<T> InScope<T>(ReportFilter f, Func<Task<T>> query)
    {
        f.Consolidated &= User.IsSystemAdmin();
        if (!f.Consolidated) return await query();
        using (_branch.Override(null, disableFilter: true)) return await query();
    }

    private async Task<string> ScopeNameAsync(ReportFilter f)
    {
        if (f.Consolidated) return "All branches (consolidated)";
        var id = _branch.BranchId;
        return await _db.Branches.Where(b => b.Id == id).Select(b => b.Name).FirstOrDefaultAsync() ?? "";
    }

    private async Task<ReportPage<T>> PageAsync<T>(T? report, ReportFilter f) => new()
    {
        Report = report, Filter = f, Scope = await ScopeNameAsync(f), CanConsolidate = User.IsSystemAdmin(),
    };
}
