using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Accounting;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Infrastructure.Crud;
using BushiBan.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Accounting;

public class FiscalYearRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public bool IsActive { get; init; }
    public bool IsClosed { get; init; }
    public int Vouchers { get; init; }
    public int Drafts { get; init; }
}

/// <summary>
/// Fiscal years. Closing a year is just a flag: <see cref="Services.AccountingService"/> refuses new or posted
/// vouchers dated inside a closed year. Close and reopen are separate buttons so they can ask for confirmation.
/// </summary>
public class FiscalYearsController : CrudController<FiscalYear>
{
    public FiscalYearsController(AppDbContext db) : base(db) { }

    protected override string Title => "Fiscal years";
    protected override string Singular => "Fiscal year";
    protected override string ViewPermission => P.FiscalYears.View;
    protected override string CreatePermission => P.FiscalYears.Manage;
    protected override string EditPermission => P.FiscalYears.Manage;
    protected override string DeletePermission => P.FiscalYears.Manage;
    protected override IQueryable<FiscalYear> Search(IQueryable<FiscalYear> q, string t) => q.Where(f => f.Name.Contains(t));
    protected override IOrderedQueryable<FiscalYear> Sort(IQueryable<FiscalYear> q) => q.OrderByDescending(f => f.StartDate);
    protected override IEnumerable<CrudColumn<FiscalYear>> Columns => new CrudColumn<FiscalYear>[]
    {
        new("Name", f => f.Name), new("Start", f => f.StartDate), new("End", f => f.EndDate), new("Active", f => f.IsActive), new("Closed", f => f.IsClosed),
    };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(FiscalYear.Name), "Name", "For example FY 2027."),
        new(nameof(FiscalYear.StartDate), "Start date"),
        new(nameof(FiscalYear.EndDate), "End date"),
        new(nameof(FiscalYear.IsActive), "Active", "The active year is used when two years could match a date."),
    };

    public override async Task<IActionResult> Index(string? q, int page = 1)
    {
        if (!User.HasPermission(P.FiscalYears.View)) return Forbid();
        var query = Db.FiscalYears.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = Search(query, q.Trim());
        var rows = await Sort(query).Select(f => new FiscalYearRow
        {
            Id = f.Id, Name = f.Name, StartDate = f.StartDate, EndDate = f.EndDate, IsActive = f.IsActive, IsClosed = f.IsClosed,
            Vouchers = Db.Vouchers.Count(v => v.Date >= f.StartDate && v.Date <= f.EndDate && v.Status != VoucherStatus.Draft),
            Drafts = Db.Vouchers.Count(v => v.Date >= f.StartDate && v.Date <= f.EndDate && v.Status == VoucherStatus.Draft),
        }).ToPagedAsync(page, 50);
        ViewData["Search"] = q;
        return View("~/Views/FiscalYears/Index.cshtml", rows);
    }

    protected override async Task ValidateAsync(FiscalYear f, bool isNew)
    {
        if (!ModelState.IsValid) return;
        if (f.EndDate <= f.StartDate) ModelState.AddModelError(nameof(FiscalYear.EndDate), "The end date must be after the start date.");
        if (!isNew)
        {
            var original = await Db.FiscalYears.AsNoTracking().FirstAsync(x => x.Id == f.Id);
            if (original.IsClosed && (original.StartDate != f.StartDate || original.EndDate != f.EndDate))
                ModelState.AddModelError(nameof(FiscalYear.StartDate), "This year is closed. Reopen it before changing its dates.");
        }
        var overlap = await Db.FiscalYears.Where(x => x.Id != f.Id && x.StartDate <= f.EndDate && x.EndDate >= f.StartDate)
            .Select(x => x.Name).FirstOrDefaultAsync();
        if (overlap is not null) ModelState.AddModelError(nameof(FiscalYear.StartDate), $"These dates overlap {overlap}. Fiscal years in a branch can't overlap.");
    }

    protected override async Task<string?> CanDeleteAsync(FiscalYear f)
    {
        if (f.IsClosed) return "it's closed.";
        var vouchers = await Db.Vouchers.CountAsync(v => v.Date >= f.StartDate && v.Date <= f.EndDate);
        return vouchers == 0 ? null : $"{vouchers} voucher(s) are dated in this year.";
    }

    [HttpPost, HasPermission(P.FiscalYears.Manage)]
    public async Task<IActionResult> Close(int id) => await SetClosedAsync(id, true);

    [HttpPost, HasPermission(P.FiscalYears.Manage)]
    public async Task<IActionResult> Reopen(int id) => await SetClosedAsync(id, false);

    private async Task<IActionResult> SetClosedAsync(int id, bool closed)
    {
        var year = await Db.FiscalYears.FirstOrDefaultAsync(f => f.Id == id);
        if (year is null) return NotFound();
        if (closed)
        {
            var drafts = await Db.Vouchers.CountAsync(v => v.Status == VoucherStatus.Draft && v.Date >= year.StartDate && v.Date <= year.EndDate);
            if (drafts > 0)
            {
                this.Error($"{year.Name} still has {drafts} draft voucher(s). Post or delete them before closing the year.");
                return RedirectToAction(nameof(Index));
            }
        }
        year.IsClosed = closed;
        await Db.SaveChangesAsync();
        this.Success(closed ? $"{year.Name} is closed. No vouchers can be added or posted in it." : $"{year.Name} is open again.");
        return RedirectToAction(nameof(Index));
    }
}
