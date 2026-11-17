using System.Net;
using System.Security.Claims;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Accounting;
using BushiBan.Web.Features.Accounting;
using BushiBan.Web.Features.Billing;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Line = BushiBan.Web.Services.AccountingService.Line;

namespace BushiBan.Tests;

public class AccountingTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public AccountingTests(TestApp app) => _app = app;

    private async Task<Voucher> PostAsync(DateOnly date, params (string Code, decimal Dr, decimal Cr)[] lines)
    {
        var ids = new Dictionary<string, int>();
        foreach (var code in lines.Select(l => l.Code).Distinct()) ids[code] = await _app.AccountIdAsync(code);
        return await _app.InPasadenaAsync(async sp =>
        {
            var v = await sp.GetRequiredService<AccountingService>().CreateAsync(VoucherType.Journal, date, "Test entry",
                lines.Select(l => new Line(ids[l.Code], l.Dr, l.Cr)));
            await sp.GetRequiredService<AppDbContext>().SaveChangesAsync();
            return v;
        });
    }

    [Fact]
    public async Task Unbalanced_vouchers_are_rejected()
    {
        var ex = await Assert.ThrowsAsync<AccountingException>(() => PostAsync(new DateOnly(2026, 3, 1), ("10-01-01-001", 100, 0), ("20-01-04-001", 0, 90)));
        Assert.Contains("must be equal", ex.Message);

        // Same through the screen: the message is shown and nothing is saved.
        var cash = await _app.AccountIdAsync("10-01-01-001");
        var rent = await _app.AccountIdAsync("50-01-04-002");
        var c = await _app.SignInAsync("accountant@bushiban.local");
        var before = await _app.DbAsync(db => db.Vouchers.CountAsync());
        var res = await c.PostFormAsync("/Vouchers/Create", "/Vouchers/Create", new()
        {
            ["Date"] = "2026-03-02", ["Type"] = ((int)VoucherType.Payment).ToString(), ["Description"] = "Rent",
            ["Lines[0].AccountId"] = rent.ToString(), ["Lines[0].Debit"] = "1000",
            ["Lines[1].AccountId"] = cash.ToString(), ["Lines[1].Credit"] = "900", ["Intent"] = "post",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("must be equal", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, await _app.DbAsync(db => db.Vouchers.CountAsync()));

        // Balanced: posted and shown.
        res = await c.PostFormAsync("/Vouchers/Create", "/Vouchers/Create", new()
        {
            ["Date"] = "2026-03-02", ["Type"] = ((int)VoucherType.Payment).ToString(), ["Description"] = "Rent",
            ["Lines[0].AccountId"] = rent.ToString(), ["Lines[0].Debit"] = "1000",
            ["Lines[1].AccountId"] = cash.ToString(), ["Lines[1].Credit"] = "1000", ["Intent"] = "post",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("PV-2026-", await c.GetStringAsync(res.Headers.Location!.ToString()));
    }

    [Fact]
    public async Task Vouchers_in_a_closed_fiscal_year_are_rejected()
    {
        await _app.InPasadenaAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.FiscalYears.Add(new FiscalYear { Name = "FY 2019", StartDate = new DateOnly(2019, 1, 1), EndDate = new DateOnly(2019, 12, 31), IsClosed = true });
            await db.SaveChangesAsync();
        });
        var ex = await Assert.ThrowsAsync<AccountingException>(() => PostAsync(new DateOnly(2019, 6, 1), ("10-01-01-001", 10, 0), ("20-01-04-001", 0, 10)));
        Assert.Contains("closed", ex.Message);
    }

    [Fact]
    public async Task Reversal_nets_to_zero_and_marks_the_original()
    {
        var original = await PostAsync(new DateOnly(2026, 4, 1), ("50-01-04-001", 45.5m, 0), ("10-01-01-001", 0, 45.5m));
        var c = await _app.SignInAsync("accountant@bushiban.local");
        var res = await c.PostFormAsync($"/Vouchers/Details/{original.Id}", $"/Vouchers/Reverse/{original.Id}", new() { ["reason"] = "Duplicate", ["date"] = "2026-04-02" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var (saved, reversal) = await _app.DbAsync(async db => (
            await db.Vouchers.SingleAsync(v => v.Id == original.Id),
            await db.Vouchers.Include(v => v.Entries).SingleAsync(v => v.ReversalOfId == original.Id)));
        Assert.Equal(VoucherStatus.Reversed, saved.Status);
        Assert.Contains("Duplicate", reversal.Description);
        var net = await _app.DbAsync(db => db.VoucherEntries.Where(e => e.VoucherId == original.Id || e.VoucherId == reversal.Id)
            .GroupBy(e => e.DetailAccountId).Select(g => g.Sum(e => e.Debit) - g.Sum(e => e.Credit)).ToListAsync());
        Assert.All(net, n => Assert.Equal(0m, Math.Round(n, 2)));

        // A reversal can't be reversed again.
        res = await c.PostFormAsync($"/Vouchers/Details/{original.Id}", $"/Vouchers/Reverse/{original.Id}", new() { ["reason"] = "Again" });
        Assert.Equal(1, await _app.DbAsync(db => db.Vouchers.CountAsync(v => v.ReversalOfId == original.Id)));
    }

    [Fact]
    public async Task Trial_balance_and_balance_sheet_balance_after_mixed_postings()
    {
        await PostAsync(new DateOnly(2026, 1, 2), ("10-01-02-001", 50_000, 0), ("20-01-04-001", 0, 50_000));          // loan received
        await PostAsync(new DateOnly(2026, 2, 1), ("50-01-04-002", 2_500, 0), ("10-01-02-001", 0, 2_500));            // rent
        await PostAsync(new DateOnly(2026, 2, 3), ("10-02-01-001", 4_000, 0), ("20-01-02-001", 0, 4_000));            // equipment on credit
        var student = await _app.NewStudentAsync();
        var income = await _app.AccountIdAsync("40-01-03-002");
        await _app.InPasadenaAsync(async sp =>
        {
            var billing = sp.GetRequiredService<BillingService>();
            await billing.CreateInvoiceAsync(student, new DateOnly(2026, 2, 6), new DateOnly(2026, 2, 6), "Gloves", new[] { new NewInvoiceLine("Gloves", 65, income) });
            await billing.ReceivePaymentAsync(student, new DateOnly(2026, 2, 7), 100, PaymentMethod.Cash, null);
        });
        // A draft never counts.
        var cash = await _app.AccountIdAsync("10-01-01-001");
        await _app.InPasadenaAsync(async sp =>
        {
            await sp.GetRequiredService<AccountingService>().CreateAsync(VoucherType.Journal, new DateOnly(2026, 2, 8), "Draft",
                new[] { new Line(cash, 999, 0) }, status: VoucherStatus.Draft);
            await sp.GetRequiredService<AppDbContext>().SaveChangesAsync();
        });

        var (tb, bs) = await _app.InPasadenaAsync(async sp =>
        {
            var reports = sp.GetRequiredService<FinancialReportService>();
            return (await reports.TrialBalanceAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), showZero: false),
                    await reports.BalanceSheetAsync(new DateOnly(2026, 12, 31)));
        });
        Assert.True(tb.TotalDebit > 50_000);
        Assert.Equal(tb.TotalDebit, tb.TotalCredit);
        Assert.True(tb.Balanced);
        Assert.DoesNotContain(tb.Rows, r => r.Debit == 999);
        Assert.True(bs.Balanced, $"Assets {bs.TotalAssets} vs liabilities + equity {bs.LiabilitiesAndEquity}");
        Assert.Equal(bs.TotalAssets, bs.LiabilitiesAndEquity);

        // The screens render, and CSV downloads work.
        var c = await _app.SignInAsync("accountant@bushiban.local");
        Assert.Contains("Debits equal credits", await c.GetStringAsync("/Reports/TrialBalance?From=2026-01-01&To=2026-12-31"));
        Assert.Contains("Assets = Liabilities + Equity", await c.GetStringAsync("/Reports/BalanceSheet?AsOf=2026-12-31"));
        Assert.Contains("Net income", await c.GetStringAsync("/Reports/IncomeStatement?From=2026-01-01&To=2026-12-31"));
        Assert.Contains("Opening balance", await c.GetStringAsync("/Reports/Ledger?Code=10-01-02-001&From=2026-02-01&To=2026-12-31"));
        var csv = await c.GetAsync("/Reports/TrialBalance?From=2026-01-01&To=2026-12-31&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("10-01-02-001", await csv.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/ChartOfAccounts")).StatusCode);
    }

    [Fact]
    public async Task Ledger_shows_opening_and_running_balance()
    {
        var acct = "10-01-01-002"; // petty cash: only this test posts to it
        await PostAsync(new DateOnly(2026, 5, 1), (acct, 200, 0), ("20-01-04-001", 0, 200));
        await PostAsync(new DateOnly(2026, 6, 1), (acct, 0, 30), ("50-01-04-001", 30, 0));
        await PostAsync(new DateOnly(2026, 6, 2), (acct, 0, 20), ("50-01-04-001", 20, 0));
        var ledger = await _app.InPasadenaAsync(sp => sp.GetRequiredService<FinancialReportService>().LedgerAsync(acct, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)));
        Assert.NotNull(ledger);
        Assert.Equal(200m, ledger!.Opening);
        Assert.Equal(new[] { 170m, 150m }, ledger.Lines.Select(l => l.Balance).ToArray());
        Assert.Equal(150m, ledger.Closing);
    }

    [Fact]
    public async Task Account_codes_are_generated_automatically()
    {
        var c = await _app.SignInAsync("accountant@bushiban.local");
        var res = await c.PostFormAsync("/MainAccounts/Create", "/MainAccounts/Create", new()
            { ["Name"] = "Test Assets", ["AccountType"] = ((int)AccountType.Assets).ToString(), ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var main = await _app.DbAsync(db => db.MainAccounts.SingleAsync(a => a.Name == "Test Assets" && !a.IsDeleted));
        Assert.Matches(@"^10-\d\d$", main.Code);
        Assert.Equal("10-03", main.Code); // seeded: 10-01 and 10-02

        res = await c.PostFormAsync("/SubAccounts/Create", "/SubAccounts/Create", new() { ["Name"] = "Test Sub", ["MainAccountId"] = main.Id.ToString(), ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var sub = await _app.DbAsync(db => db.SubAccounts.SingleAsync(a => a.Name == "Test Sub"));
        Assert.Equal("10-03-01", sub.Code);

        foreach (var name in new[] { "Detail One", "Detail Two" })
        {
            res = await c.PostFormAsync("/DetailAccounts/Create", "/DetailAccounts/Create", new() { ["Name"] = name, ["SubAccountId"] = sub.Id.ToString(), ["IsActive"] = "true" });
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        var details = await _app.DbAsync(db => db.DetailAccounts.Where(a => a.SubAccountId == sub.Id).OrderBy(a => a.Code).Select(a => a.Code).ToListAsync());
        Assert.Equal(new[] { "10-03-01-001", "10-03-01-002" }, details);

        // Expenses go under 50; a sub under an existing main continues its numbering.
        res = await c.PostFormAsync("/MainAccounts/Create", "/MainAccounts/Create", new() { ["Name"] = "Test Expenses", ["AccountType"] = ((int)AccountType.Expenses).ToString(), ["IsActive"] = "true" });
        Assert.Equal("50-05", await _app.DbAsync(db => db.MainAccounts.Where(a => a.Name == "Test Expenses").Select(a => a.Code).SingleAsync()));
        var cashSubId = await _app.InPasadenaAsync(sp => sp.GetRequiredService<AppDbContext>().SubAccounts.Where(s => s.Code == "10-01-01").Select(s => s.Id).SingleAsync());
        await c.PostFormAsync("/DetailAccounts/Create", "/DetailAccounts/Create", new() { ["Name"] = "Cash Drawer 2", ["SubAccountId"] = cashSubId.ToString(), ["IsActive"] = "true" });
        Assert.Equal("10-01-01-003", await _app.InPasadenaAsync(sp => sp.GetRequiredService<AppDbContext>().DetailAccounts.Where(a => a.Name == "Cash Drawer 2").Select(a => a.Code).SingleAsync()));

        // Codes can't be changed on edit, and parents with children can't be deleted.
        res = await c.PostFormAsync($"/SubAccounts/Edit/{sub.Id}", $"/SubAccounts/Edit/{sub.Id}", new() { ["Name"] = "Renamed", ["Code"] = "99-99-99", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var renamed = await _app.DbAsync(db => db.SubAccounts.SingleAsync(a => a.Id == sub.Id));
        Assert.Equal(("Renamed", "10-03-01", main.Id), (renamed.Name, renamed.Code, renamed.MainAccountId));
        await c.PostFormAsync("/MainAccounts", $"/MainAccounts/Delete/{main.Id}", new());
        Assert.False(await _app.DbAsync(db => db.MainAccounts.Where(a => a.Id == main.Id).Select(a => a.IsDeleted).SingleAsync()));
    }

    [Fact]
    public async Task Detail_accounts_with_postings_cannot_be_deleted()
    {
        var id = await _app.AccountIdAsync("50-03-03-001");
        await PostAsync(new DateOnly(2026, 7, 1), ("50-03-03-001", 5, 0), ("10-01-01-001", 0, 5));
        var c = await _app.SignInAsync("accountant@bushiban.local");
        await c.PostFormAsync("/DetailAccounts", $"/DetailAccounts/Delete/{id}", new());
        Assert.False(await _app.DbAsync(db => db.DetailAccounts.Where(a => a.Id == id).Select(a => a.IsDeleted).SingleAsync()));
        // The receivables account is used by billing settings.
        var receivable = await _app.AccountIdAsync("10-01-03-001");
        await c.PostFormAsync("/DetailAccounts", $"/DetailAccounts/Delete/{receivable}", new());
        Assert.False(await _app.DbAsync(db => db.DetailAccounts.Where(a => a.Id == receivable).Select(a => a.IsDeleted).SingleAsync()));
    }

    [Fact]
    public async Task Drafts_can_be_unbalanced_are_left_out_of_reports_and_post_when_balanced()
    {
        var cash = await _app.AccountIdAsync("10-01-01-001");
        var supplies = await _app.AccountIdAsync("50-01-04-001");
        var c = await _app.SignInAsync("accountant@bushiban.local");
        var res = await c.PostFormAsync("/Vouchers/Create", "/Vouchers/Create", new()
        {
            ["Date"] = "2026-08-01", ["Type"] = ((int)VoucherType.Journal).ToString(), ["Description"] = "Supplies (draft)",
            ["Lines[0].AccountId"] = supplies.ToString(), ["Lines[0].Debit"] = "12.34", ["Intent"] = "draft",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var draft = await _app.DbAsync(db => db.Vouchers.SingleAsync(v => v.Description == "Supplies (draft)"));
        Assert.Equal(VoucherStatus.Draft, draft.Status);

        // Posting an unbalanced draft fails and leaves it a draft.
        await c.PostFormAsync($"/Vouchers/Details/{draft.Id}", $"/Vouchers/Post/{draft.Id}", new());
        Assert.Equal(VoucherStatus.Draft, await _app.DbAsync(db => db.Vouchers.Where(v => v.Id == draft.Id).Select(v => v.Status).SingleAsync()));

        res = await c.PostFormAsync($"/Vouchers/Edit/{draft.Id}", $"/Vouchers/Edit/{draft.Id}", new()
        {
            ["Date"] = "2026-08-01", ["Type"] = ((int)VoucherType.Journal).ToString(), ["Description"] = "Supplies (draft)",
            ["Lines[0].AccountId"] = supplies.ToString(), ["Lines[0].Debit"] = "12.34",
            ["Lines[1].AccountId"] = cash.ToString(), ["Lines[1].Credit"] = "12.34", ["Intent"] = "draft",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        await c.PostFormAsync($"/Vouchers/Details/{draft.Id}", $"/Vouchers/Post/{draft.Id}", new());
        var posted = await _app.DbAsync(db => db.Vouchers.Include(v => v.Entries).SingleAsync(v => v.Id == draft.Id));
        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.Equal(2, posted.Entries.Count);

        // Posted vouchers can't be edited or deleted.
        res = await c.PostFormAsync($"/Vouchers/Details/{draft.Id}", $"/Vouchers/Delete/{draft.Id}", new());
        Assert.False(await _app.DbAsync(db => db.Vouchers.Where(v => v.Id == draft.Id).Select(v => v.IsDeleted).SingleAsync()));
        Assert.Contains("Voucher", await c.GetStringAsync("/Vouchers?Q=Supplies"));
    }

    [Fact]
    public async Task Fiscal_years_cannot_overlap_or_end_before_they_start()
    {
        var c = await _app.SignInAsync("accountant@bushiban.local");
        var res = await c.PostFormAsync("/FiscalYears/Create", "/FiscalYears/Create", new()
            { ["Name"] = "Overlap", ["StartDate"] = "2026-06-01", ["EndDate"] = "2027-05-31", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("overlap", await res.Content.ReadAsStringAsync());
        res = await c.PostFormAsync("/FiscalYears/Create", "/FiscalYears/Create", new()
            { ["Name"] = "Backwards", ["StartDate"] = "2030-12-31", ["EndDate"] = "2030-01-01", ["IsActive"] = "true" });
        Assert.Contains("must be after", await res.Content.ReadAsStringAsync());
        res = await c.PostFormAsync("/FiscalYears/Create", "/FiscalYears/Create", new()
            { ["Name"] = "FY 2031", ["StartDate"] = "2031-01-01", ["EndDate"] = "2031-12-31", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var fy = await _app.DbAsync(db => db.FiscalYears.SingleAsync(f => f.Name == "FY 2031"));
        await c.PostFormAsync("/FiscalYears", $"/FiscalYears/Close/{fy.Id}", new());
        Assert.True(await _app.DbAsync(db => db.FiscalYears.Where(f => f.Id == fy.Id).Select(f => f.IsClosed).SingleAsync()));
        await Assert.ThrowsAsync<AccountingException>(() => PostAsync(new DateOnly(2031, 3, 1), ("10-01-01-001", 1, 0), ("20-01-04-001", 0, 1)));
    }

    [Fact]
    public async Task Accountant_has_finance_access_but_cannot_manage_users()
    {
        var claims = await _app.DbAsync(db => db.UserClaims
            .Where(c => c.UserId == db.Users.Where(u => u.Email == "accountant@bushiban.local").Select(u => u.Id).Single() && c.ClaimType == Permissions.ClaimType)
            .Select(c => c.ClaimValue!).ToListAsync());
        var accountant = new ClaimsPrincipal(new ClaimsIdentity(claims.Select(v => new Claim(Permissions.ClaimType, v)), "test"));
        Assert.True(accountant.HasPermission(Permissions.Vouchers.Create));
        Assert.False(accountant.HasPermission(Permissions.Users.Manage));
        Assert.False(accountant.HasPermission(Permissions.Users.View));

        var c = await _app.SignInAsync("accountant@bushiban.local");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Vouchers/Create")).StatusCode);
        var home = await c.GetStringAsync("/Vouchers");
        Assert.DoesNotContain("href=\"/Users\"", home);
        Assert.Contains("href=\"/Reports/TrialBalance\"", home);
        var users = await c.GetAsync("/Users");
        Assert.NotEqual(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task Billing_vouchers_must_be_corrected_from_billing()
    {
        var student = await _app.NewStudentAsync();
        var income = await _app.AccountIdAsync("40-01-03-001");
        var invoice = await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().CreateInvoiceAsync(
            student, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), "Uniform", new[] { new NewInvoiceLine("Uniform", 40, income) }));
        var c = await _app.SignInAsync("accountant@bushiban.local");
        await c.PostFormAsync($"/Vouchers/Details/{invoice.VoucherId}", $"/Vouchers/Reverse/{invoice.VoucherId}", new() { ["reason"] = "Try" });
        Assert.Equal(VoucherStatus.Posted, await _app.DbAsync(db => db.Vouchers.Where(v => v.Id == invoice.VoucherId).Select(v => v.Status).SingleAsync()));
        var details = await c.GetStringAsync($"/Vouchers/Details/{invoice.VoucherId}");
        Assert.Contains(invoice.InvoiceNo, details);
    }

    [Fact]
    public async Task System_admins_can_consolidate_reports_across_branches()
    {
        var admin = await _app.SignInAsync(TestApp.AdminEmail);
        var page = await admin.GetStringAsync("/Reports/TrialBalance?Consolidated=true&From=2026-01-01&To=2026-12-31");
        Assert.Contains("data-scope=\"All branches (consolidated)\"", page);
        var manager = await _app.SignInAsync("manager@bushiban.local");
        var own = await manager.GetStringAsync("/Reports/TrialBalance?Consolidated=true&From=2026-01-01&To=2026-12-31");
        Assert.DoesNotContain("data-scope=\"All branches (consolidated)\"", own);
        Assert.Contains("data-scope=\"Pasadena", own);
    }
}
