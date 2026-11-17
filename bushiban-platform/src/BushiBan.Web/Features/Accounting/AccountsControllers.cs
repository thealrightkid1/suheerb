using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Accounting;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Infrastructure.Crud;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Accounting;

/// <summary>Level 1 of the chart of accounts. Codes come from the account type and can't be changed.</summary>
public class MainAccountsController : CrudController<MainAccount>
{
    private readonly AccountCodeService _codes;
    public MainAccountsController(AppDbContext db, AccountCodeService codes) : base(db) => _codes = codes;

    protected override string Title => "Main accounts";
    protected override string Singular => "Main account";
    protected override string? Intro => "The top level of the chart of accounts. Codes are assigned automatically from the account type " +
                                         "(Assets 10, Liabilities 20, Equity 30, Revenue 40, Expenses 50) and never change.";
    protected override string ViewPermission => P.MainAccounts.View;
    protected override string CreatePermission => P.MainAccounts.Manage;
    protected override string EditPermission => P.MainAccounts.Manage;
    protected override string DeletePermission => P.MainAccounts.Manage;
    protected override IQueryable<MainAccount> Query() => Db.MainAccounts.Include(a => a.SubAccounts);
    protected override IQueryable<MainAccount> Search(IQueryable<MainAccount> q, string t) => q.Where(a => a.Code.Contains(t) || a.Name.Contains(t));
    protected override IOrderedQueryable<MainAccount> Sort(IQueryable<MainAccount> q) => q.OrderBy(a => a.Code);

    protected override IEnumerable<CrudColumn<MainAccount>> Columns => new CrudColumn<MainAccount>[]
    {
        new("Code", a => a.Code), new("Name", a => a.Name), new("Type", a => a.AccountType),
        new("Sub accounts", a => a.SubAccounts.Count), new("Active", a => a.IsActive),
    };

    protected override IEnumerable<CrudField> Fields
    {
        get
        {
            if (AccountForms.IsEdit(this)) yield return new(nameof(MainAccount.Code), "Code", ReadOnlyOnEdit: true);
            yield return new(nameof(MainAccount.Name), "Account name");
            yield return new(nameof(MainAccount.AccountType), "Account type", "Decides the code prefix. It can't be changed later.", ReadOnlyOnEdit: true);
            yield return new(nameof(MainAccount.IsActive), "Active");
        }
    }

    protected override async Task ValidateAsync(MainAccount a, bool isNew)
    {
        if (!isNew || !ModelState.IsValid) return;
        if (!Enum.IsDefined(a.AccountType)) { ModelState.AddModelError(nameof(MainAccount.AccountType), "Pick an account type."); return; }
        await AccountForms.AssignCodeAsync(this, () => _codes.NextMainCodeAsync(a.AccountType), code => a.Code = code);
    }

    protected override async Task<string?> CanDeleteAsync(MainAccount a)
    {
        var subs = await Db.SubAccounts.CountAsync(s => s.MainAccountId == a.Id);
        return subs == 0 ? null : $"it still has {subs} sub account(s). Delete or move those first.";
    }
}

/// <summary>Level 2. The code is the main account's code plus two digits.</summary>
public class SubAccountsController : CrudController<SubAccount>
{
    private readonly AccountCodeService _codes;
    public SubAccountsController(AppDbContext db, AccountCodeService codes) : base(db) => _codes = codes;

    protected override string Title => "Sub accounts";
    protected override string Singular => "Sub account";
    protected override string? Intro => "Groups detail accounts under a main account. The code is the main account's code plus a two-digit number, assigned automatically.";
    protected override string ViewPermission => P.SubAccounts.View;
    protected override string CreatePermission => P.SubAccounts.Manage;
    protected override string EditPermission => P.SubAccounts.Manage;
    protected override string DeletePermission => P.SubAccounts.Manage;
    protected override IQueryable<SubAccount> Query() => Db.SubAccounts.Include(a => a.MainAccount).Include(a => a.DetailAccounts);
    protected override IQueryable<SubAccount> Search(IQueryable<SubAccount> q, string t) =>
        q.Where(a => a.Code.Contains(t) || a.Name.Contains(t) || a.MainAccount!.Name.Contains(t));
    protected override IOrderedQueryable<SubAccount> Sort(IQueryable<SubAccount> q) => q.OrderBy(a => a.Code);

    protected override IEnumerable<CrudColumn<SubAccount>> Columns => new CrudColumn<SubAccount>[]
    {
        new("Code", a => a.Code), new("Name", a => a.Name),
        new("Main account", a => a.MainAccount is null ? "" : $"{a.MainAccount.Code} - {a.MainAccount.Name}"),
        new("Type", a => a.MainAccount?.AccountType), new("Detail accounts", a => a.DetailAccounts.Count), new("Active", a => a.IsActive),
    };

    protected override IEnumerable<CrudField> Fields
    {
        get
        {
            if (AccountForms.IsEdit(this)) yield return new(nameof(SubAccount.Code), "Code", ReadOnlyOnEdit: true);
            yield return new(nameof(SubAccount.Name), "Account name");
            yield return new(nameof(SubAccount.MainAccountId), "Main account", "Decides the code. It can't be changed later.",
                Options: () => Db.MainAccounts.Where(a => a.IsActive).OrderBy(a => a.Code)
                    .Select(a => new SelectListItem(a.Code + " - " + a.Name, a.Id.ToString())).ToListAsync(),
                ReadOnlyOnEdit: true);
            yield return new(nameof(SubAccount.IsActive), "Active");
        }
    }

    protected override async Task ValidateAsync(SubAccount a, bool isNew)
    {
        if (!isNew || !ModelState.IsValid) return;
        var main = await Db.MainAccounts.FirstOrDefaultAsync(m => m.Id == a.MainAccountId);
        if (main is null) { ModelState.AddModelError(nameof(SubAccount.MainAccountId), "Pick a main account."); return; }
        await AccountForms.AssignCodeAsync(this, () => _codes.NextSubCodeAsync(main.Code), code => a.Code = code);
    }

    protected override async Task<string?> CanDeleteAsync(SubAccount a)
    {
        var details = await Db.DetailAccounts.CountAsync(d => d.SubAccountId == a.Id);
        return details == 0 ? null : $"it still has {details} detail account(s). Delete those first.";
    }
}

/// <summary>Level 3, the accounts vouchers post to. The code is the sub account's code plus three digits.</summary>
public class DetailAccountsController : CrudController<DetailAccount>
{
    private readonly AccountCodeService _codes;
    public DetailAccountsController(AppDbContext db, AccountCodeService codes) : base(db) => _codes = codes;

    protected override string Title => "Detail accounts";
    protected override string Singular => "Detail account";
    protected override string? Intro => "The accounts vouchers post to. The code is the sub account's code plus a three-digit number, assigned automatically.";
    protected override string ViewPermission => P.DetailAccounts.View;
    protected override string CreatePermission => P.DetailAccounts.Manage;
    protected override string EditPermission => P.DetailAccounts.Manage;
    protected override string DeletePermission => P.DetailAccounts.Manage;
    protected override IQueryable<DetailAccount> Query() => Db.DetailAccounts.Include(a => a.SubAccount).ThenInclude(s => s!.MainAccount);
    protected override IQueryable<DetailAccount> Search(IQueryable<DetailAccount> q, string t) =>
        q.Where(a => a.Code.Contains(t) || a.Name.Contains(t) || a.SubAccount!.Name.Contains(t));
    protected override IOrderedQueryable<DetailAccount> Sort(IQueryable<DetailAccount> q) => q.OrderBy(a => a.Code);

    protected override IEnumerable<CrudColumn<DetailAccount>> Columns => new CrudColumn<DetailAccount>[]
    {
        new("Code", a => a.Code), new("Name", a => a.Name),
        new("Sub account", a => a.SubAccount is null ? "" : $"{a.SubAccount.Code} - {a.SubAccount.Name}"),
        new("Type", a => a.SubAccount?.MainAccount?.AccountType), new("Active", a => a.IsActive),
    };

    protected override IEnumerable<CrudField> Fields
    {
        get
        {
            if (AccountForms.IsEdit(this)) yield return new(nameof(DetailAccount.Code), "Code", ReadOnlyOnEdit: true);
            yield return new(nameof(DetailAccount.Name), "Account name");
            yield return new(nameof(DetailAccount.SubAccountId), "Sub account", "Decides the code. It can't be changed later.",
                Options: () => Db.SubAccounts.Where(a => a.IsActive).OrderBy(a => a.Code)
                    .Select(a => new SelectListItem(a.Code + " - " + a.Name, a.Id.ToString())).ToListAsync(),
                ReadOnlyOnEdit: true);
            yield return new(nameof(DetailAccount.IsActive), "Active", "Inactive accounts can't be used on new vouchers.");
        }
    }

    protected override async Task ValidateAsync(DetailAccount a, bool isNew)
    {
        if (!isNew && !a.IsActive && await UsedByBillingSettingsAsync(a.Id))
            ModelState.AddModelError(nameof(DetailAccount.IsActive), "Billing posts to this account (see Branch settings). Pick a different account there before deactivating it.");
        if (!isNew || !ModelState.IsValid) return;
        var sub = await Db.SubAccounts.FirstOrDefaultAsync(s => s.Id == a.SubAccountId);
        if (sub is null) { ModelState.AddModelError(nameof(DetailAccount.SubAccountId), "Pick a sub account."); return; }
        await AccountForms.AssignCodeAsync(this, () => _codes.NextDetailCodeAsync(sub.Code), code => a.Code = code);
    }

    protected override async Task<string?> CanDeleteAsync(DetailAccount a)
    {
        var postings = await Db.VoucherEntries.CountAsync(e => e.DetailAccountId == a.Id && !e.Voucher!.IsDeleted);
        if (postings > 0) return $"{postings} voucher line(s) post to it. Mark it inactive instead.";
        if (await UsedByBillingSettingsAsync(a.Id)) return "billing posts to it (see Branch settings).";
        var billing = await Db.InvoiceLines.CountAsync(l => l.CreditAccountId == a.Id) + await Db.Payments.CountAsync(p => p.DepositAccountId == a.Id);
        return billing == 0 ? null : $"{billing} invoice line(s) or payment(s) use it. Mark it inactive instead.";
    }

    private Task<bool> UsedByBillingSettingsAsync(int id) => Db.AccountingSettings.AnyAsync(s =>
        s.ReceivableAccountId == id || s.TuitionIncomeAccountId == id || s.AdmissionIncomeAccountId == id || s.SecurityDepositAccountId == id ||
        s.LateFeeIncomeAccountId == id || s.CashAccountId == id || s.BankAccountId == id);
}

internal static class AccountForms
{
    /// <summary>The code is shown (read-only) on the edit form only; on create it is assigned when saving.</summary>
    public static bool IsEdit(Microsoft.AspNetCore.Mvc.Controller c) =>
        string.Equals(c.ControllerContext.ActionDescriptor?.ActionName, "Edit", StringComparison.OrdinalIgnoreCase);

    public static async Task AssignCodeAsync(Microsoft.AspNetCore.Mvc.Controller c, Func<Task<string>> next, Action<string> assign)
    {
        try { assign(await next()); }
        catch (AccountingException ex) { c.ModelState.AddModelError("", ex.Message); }
    }
}
