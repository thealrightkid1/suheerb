using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Billing;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Billing;

/// <summary>Branch settings: which accounts billing posts to, the late fee, and whether email and SMS are set up.</summary>
[HasPermission(P.Branches.Manage + "|" + P.Billing.Manage)]
public class SettingsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;
    private readonly IConfiguration _config;

    public SettingsController(AppDbContext db, ICurrentBranch branch, IConfiguration config) { _db = db; _branch = branch; _config = config; }

    public async Task<IActionResult> Index()
    {
        var s = await _db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync() ?? new AccountingSettings();
        var form = new SettingsForm
        {
            ReceivableAccountId = s.ReceivableAccountId, TuitionIncomeAccountId = s.TuitionIncomeAccountId, AdmissionIncomeAccountId = s.AdmissionIncomeAccountId,
            SecurityDepositAccountId = s.SecurityDepositAccountId, LateFeeIncomeAccountId = s.LateFeeIncomeAccountId,
            CashAccountId = s.CashAccountId, BankAccountId = s.BankAccountId, LateFeeAmount = s.LateFeeAmount, LateFeeGraceDays = s.LateFeeGraceDays,
        };
        return View(await PageAsync(form));
    }

    [HttpPost]
    public async Task<IActionResult> Index(SettingsForm form)
    {
        var required = new (string Field, int? Id, string Label)[]
        {
            (nameof(form.ReceivableAccountId), form.ReceivableAccountId, "student receivables"),
            (nameof(form.TuitionIncomeAccountId), form.TuitionIncomeAccountId, "tuition income"),
            (nameof(form.AdmissionIncomeAccountId), form.AdmissionIncomeAccountId, "admission income"),
            (nameof(form.SecurityDepositAccountId), form.SecurityDepositAccountId, "security deposits"),
            (nameof(form.LateFeeIncomeAccountId), form.LateFeeIncomeAccountId, "late fee income"),
            (nameof(form.CashAccountId), form.CashAccountId, "cash"),
            (nameof(form.BankAccountId), form.BankAccountId, "bank"),
        };
        var ids = required.Where(r => r.Id is not null).Select(r => r.Id!.Value).Distinct().ToList();
        var types = await _db.DetailAccounts.Where(a => ids.Contains(a.Id) && a.IsActive)
            .ToDictionaryAsync(a => a.Id, a => a.SubAccount!.MainAccount!.AccountType);
        foreach (var r in required)
        {
            if (r.Id is null) ModelState.AddModelError(r.Field, $"Choose the {r.Label} account.");
            else if (!types.ContainsKey(r.Id.Value)) ModelState.AddModelError(r.Field, "That account is missing or inactive.");
        }
        void Expect(string field, int? id, AccountType type, string what)
        {
            if (id is int i && types.TryGetValue(i, out var t) && t != type) ModelState.AddModelError(field, $"Pick {what} account.");
        }
        Expect(nameof(form.ReceivableAccountId), form.ReceivableAccountId, AccountType.Assets, "an asset");
        Expect(nameof(form.CashAccountId), form.CashAccountId, AccountType.Assets, "an asset");
        Expect(nameof(form.BankAccountId), form.BankAccountId, AccountType.Assets, "an asset");
        Expect(nameof(form.TuitionIncomeAccountId), form.TuitionIncomeAccountId, AccountType.Revenue, "a revenue");
        Expect(nameof(form.AdmissionIncomeAccountId), form.AdmissionIncomeAccountId, AccountType.Revenue, "a revenue");
        Expect(nameof(form.LateFeeIncomeAccountId), form.LateFeeIncomeAccountId, AccountType.Revenue, "a revenue");
        Expect(nameof(form.SecurityDepositAccountId), form.SecurityDepositAccountId, AccountType.Liabilities, "a liability");
        if (form.LateFeeAmount is < 0 or > 10_000) ModelState.AddModelError(nameof(form.LateFeeAmount), "Enter an amount between $0 and $10,000 (0 turns late fees off).");
        if (form.LateFeeGraceDays is < 0 or > 60) ModelState.AddModelError(nameof(form.LateFeeGraceDays), "Enter between 0 and 60 days.");
        if (!ModelState.IsValid) return View(await PageAsync(form));

        var s = await _db.AccountingSettings.FirstOrDefaultAsync();
        if (s is null) _db.AccountingSettings.Add(s = new AccountingSettings());
        s.ReceivableAccountId = form.ReceivableAccountId;
        s.TuitionIncomeAccountId = form.TuitionIncomeAccountId;
        s.AdmissionIncomeAccountId = form.AdmissionIncomeAccountId;
        s.SecurityDepositAccountId = form.SecurityDepositAccountId;
        s.LateFeeIncomeAccountId = form.LateFeeIncomeAccountId;
        s.CashAccountId = form.CashAccountId;
        s.BankAccountId = form.BankAccountId;
        s.LateFeeAmount = Math.Round(form.LateFeeAmount, 2);
        s.LateFeeGraceDays = form.LateFeeGraceDays;
        await _db.SaveChangesAsync();
        this.Success("Branch settings saved. New invoices and payments will post to these accounts.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsPage> PageAsync(SettingsForm form)
    {
        var accounts = await _db.AccountOptionsAsync(null);
        var id = _branch.BranchId;
        return new SettingsPage
        {
            Form = form, Accounts = accounts,
            BranchName = await _db.Branches.Where(b => b.Id == id).Select(b => b.Name).FirstOrDefaultAsync() ?? "",
            EmailConfigured = !string.IsNullOrWhiteSpace(_config["Email:Host"]),
            SmsConfigured = !string.IsNullOrWhiteSpace(_config["Sms:AccountSid"]),
            EmailFrom = _config["Email:FromAddress"],
            SmsProvider = _config["Sms:Provider"],
        };
    }
}
