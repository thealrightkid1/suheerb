using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Features.Billing;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Accounting;

/// <summary>
/// Journal vouchers. Posted vouchers are never edited: they're corrected by reversal. Drafts can be edited, posted or deleted,
/// and are left out of every report. Every voucher goes through <see cref="AccountingService"/>, so debits always equal credits.
/// </summary>
[HasPermission(P.Vouchers.View)]
public class VouchersController : Controller
{
    private readonly AppDbContext _db;
    private readonly AccountingService _accounting;
    private readonly BranchClock _clock;
    private readonly LookupService _lookups;
    private readonly ICurrentBranch _branch;

    public VouchersController(AppDbContext db, AccountingService accounting, BranchClock clock, LookupService lookups, ICurrentBranch branch)
    {
        _db = db; _accounting = accounting; _clock = clock; _lookups = lookups; _branch = branch;
    }

    /// <summary>Billing creates fee-invoice vouchers itself; people pick from the other types.</summary>
    public static IEnumerable<SelectListItem> ManualTypes(VoucherType selected) =>
        EnumHelpers.SelectList<VoucherType>(selected).Where(i => i.Value != ((int)VoucherType.FeeInvoice).ToString());

    public async Task<IActionResult> Index(VoucherFilter f)
    {
        var q = _db.Vouchers.AsQueryable();
        if (f.From is not null) q = q.Where(v => v.Date >= f.From);
        if (f.To is not null) q = q.Where(v => v.Date <= f.To);
        if (f.Type is not null) q = q.Where(v => v.Type == f.Type);
        if (f.Status is not null) q = q.Where(v => v.Status == f.Status);
        if (f.AccountId is not null) q = q.Where(v => v.Entries.Any(e => e.DetailAccountId == f.AccountId));
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(v => v.VoucherNo.Contains(t) || v.Description.Contains(t) || (v.SourceReference != null && v.SourceReference.Contains(t)));
        }
        var rows = await q.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Select(v => new VoucherListRow
            {
                Id = v.Id, Date = v.Date, VoucherNo = v.VoucherNo, Type = v.Type, Description = v.Description,
                Amount = v.Entries.Sum(e => e.Debit), Status = v.Status, SourceReference = v.SourceReference, IsReversal = v.ReversalOfId != null,
            })
            .ToPagedAsync(f.Page, 25);
        return View(new VoucherIndexPage { Filter = f, Rows = rows, Accounts = await _lookups.DetailAccountsAsync(f.AccountId) });
    }

    [HasPermission(P.Vouchers.Create)]
    public async Task<IActionResult> Create()
    {
        var form = new VoucherForm { Date = _clock.Today, Lines = { new(), new() } };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Vouchers.Create)]
    public async Task<IActionResult> Create(VoucherForm form)
    {
        var lines = ReadLines(form);
        if (form.Type == VoucherType.FeeInvoice) ModelState.AddModelError("", "Fee invoice vouchers are created by billing. Pick another type.");
        if (!ModelState.IsValid) return await FormView(form);
        var draft = form.Intent == "draft";
        try
        {
            var voucher = await _accounting.CreateAsync(form.Type, form.Date, form.Description ?? "", lines!,
                status: draft ? VoucherStatus.Draft : VoucherStatus.Posted);
            await _db.SaveChangesAsync();
            this.Success(draft ? $"Draft {voucher.VoucherNo} saved. It won't appear in reports until it's posted." : $"Voucher {voucher.VoucherNo} posted.");
            return RedirectToAction(nameof(Details), new { id = voucher.Id });
        }
        catch (AccountingException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return await FormView(form);
        }
    }

    [HasPermission(P.Vouchers.Edit)]
    public async Task<IActionResult> Edit(int id)
    {
        var v = await _db.Vouchers.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        if (v.Status != VoucherStatus.Draft)
        {
            this.Error("Posted vouchers can't be edited. Reverse it and create a new one instead.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var form = new VoucherForm
        {
            Id = v.Id, VoucherNo = v.VoucherNo, Date = v.Date, Type = v.Type, Description = v.Description,
            Lines = v.Entries.OrderBy(e => e.Id).Select(e => new VoucherLineForm
            {
                AccountId = e.DetailAccountId, Debit = e.Debit == 0 ? null : e.Debit, Credit = e.Credit == 0 ? null : e.Credit, Narration = e.Narration,
            }).ToList(),
        };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Vouchers.Edit)]
    public async Task<IActionResult> Edit(int id, VoucherForm form)
    {
        form.Id = id;
        var lines = ReadLines(form);
        if (form.Type == VoucherType.FeeInvoice) ModelState.AddModelError("", "Fee invoice vouchers are created by billing. Pick another type.");
        var post = form.Intent == "post";
        if (post && !User.HasPermission(P.Vouchers.Create)) ModelState.AddModelError("", "You can save the draft, but you don't have permission to post vouchers.");
        if (!ModelState.IsValid) return await FormView(form);
        try
        {
            var voucher = await _accounting.UpdateDraftAsync(id, form.Type, form.Date, form.Description ?? "", lines!);
            if (post)
            {
                // Check the new lines in full before saving anything, so a failed post leaves the draft untouched.
                await _accounting.ValidateAsync(_branch.BranchId!.Value, form.Date, lines!);
                voucher.Status = VoucherStatus.Posted;
            }
            await _db.SaveChangesAsync();
            this.Success(post ? $"Voucher {voucher.VoucherNo} posted." : $"Draft {voucher.VoucherNo} saved.");
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (AccountingException ex)
        {
            _db.ChangeTracker.Clear();
            ModelState.AddModelError("", ex.Message);
            return await FormView(form);
        }
    }

    [HttpPost, HasPermission(P.Vouchers.Create)]
    public async Task<IActionResult> Post(int id)
    {
        try
        {
            var v = await _accounting.PostDraftAsync(id);
            await _db.SaveChangesAsync();
            this.Success($"Voucher {v.VoucherNo} posted.");
        }
        catch (AccountingException ex)
        {
            this.Error($"Couldn't post the draft: {ex.Message}");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Only drafts can be deleted; posted vouchers are reversed.</summary>
    [HttpPost, HasPermission(P.Vouchers.Edit)]
    public async Task<IActionResult> Delete(int id)
    {
        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        if (v.Status != VoucherStatus.Draft)
        {
            this.Error("Posted vouchers can't be deleted. Reverse it instead.");
            return RedirectToAction(nameof(Details), new { id });
        }
        _db.Vouchers.Remove(v);
        await _db.SaveChangesAsync();
        this.Success($"Draft {v.VoucherNo} deleted.");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id)
    {
        var page = await _db.Vouchers.Where(v => v.Id == id).Select(v => new VoucherDetailsPage
        {
            Id = v.Id, VoucherNo = v.VoucherNo, Date = v.Date, Type = v.Type, Description = v.Description, Status = v.Status,
            FiscalYear = v.FiscalYear != null ? v.FiscalYear.Name : null, CreatedBy = v.CreatedBy, CreatedAt = v.CreatedAt,
            SourceReference = v.SourceReference, BranchName = v.Branch!.Name,
            ReversalOfId = v.ReversalOfId, ReversalOfNo = v.ReversalOf != null ? v.ReversalOf.VoucherNo : null,
            ReversedById = _db.Vouchers.Where(r => r.ReversalOfId == v.Id).Select(r => (int?)r.Id).FirstOrDefault(),
            ReversedByNo = _db.Vouchers.Where(r => r.ReversalOfId == v.Id).Select(r => r.VoucherNo).FirstOrDefault(),
            Lines = v.Entries.OrderBy(e => e.Id).Select(e => new VoucherDetailsLine(e.DetailAccount!.Code, e.DetailAccount.Name, e.Debit, e.Credit, e.Narration)).ToList(),
        }).FirstOrDefaultAsync();
        if (page is null) return NotFound();
        page.SourceUrl = await SourceUrlAsync(page.SourceReference);
        return View(page);
    }

    [HttpPost, HasPermission(P.Vouchers.Delete)]
    public async Task<IActionResult> Reverse(int id, string? reason, DateOnly? date)
    {
        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason))
        {
            this.Error("Enter a reason for the reversal.");
            return RedirectToAction(nameof(Details), new { id });
        }
        if (v.SourceReference is not null)
        {
            this.Error($"This voucher was created by billing ({v.SourceReference}). Void the invoice or payment instead, so the student's balance stays right.");
            return RedirectToAction(nameof(Details), new { id });
        }
        try
        {
            var reversal = await _accounting.ReverseAsync(id, date ?? _clock.Today, reason.Trim());
            await _db.SaveChangesAsync();
            this.Success($"Voucher {v.VoucherNo} reversed by {reversal.VoucherNo}.");
            return RedirectToAction(nameof(Details), new { id = reversal.Id });
        }
        catch (AccountingException ex)
        {
            this.Error($"Couldn't reverse the voucher: {ex.Message}");
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    /// <summary>Opens the invoice or payment a billing voucher came from.</summary>
    public async Task<IActionResult> Source(int id)
    {
        var reference = await _db.Vouchers.Where(v => v.Id == id).Select(v => v.SourceReference).FirstOrDefaultAsync();
        var url = await SourceUrlAsync(reference);
        return url is null ? RedirectToAction(nameof(Details), new { id }) : Redirect(url);
    }

    private async Task<string?> SourceUrlAsync(string? reference)
    {
        if (reference is null) return null;
        if (reference.StartsWith(BillingService.InvoiceSourcePrefix, StringComparison.Ordinal))
        {
            var no = reference[BillingService.InvoiceSourcePrefix.Length..];
            var invoiceId = await _db.Invoices.Where(i => i.InvoiceNo == no).Select(i => (int?)i.Id).FirstOrDefaultAsync();
            return invoiceId is null ? null : Url.Action("Details", "Invoices", new { id = invoiceId });
        }
        if (reference.StartsWith(BillingService.PaymentSourcePrefix, StringComparison.Ordinal))
        {
            var no = reference[BillingService.PaymentSourcePrefix.Length..];
            var paymentId = await _db.Payments.Where(p => p.ReceiptNo == no).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            return paymentId is null ? null : Url.Action("Details", "Payments", new { id = paymentId });
        }
        return null;
    }

    /// <summary>Turns the posted rows into voucher lines, reporting problems per line. Blank rows are ignored.</summary>
    private List<AccountingService.Line>? ReadLines(VoucherForm form)
    {
        if (string.IsNullOrWhiteSpace(form.Description)) ModelState.AddModelError(nameof(form.Description), "Enter a description.");
        form.Lines = form.Lines.Where(l => !l.IsBlank).ToList();
        var lines = new List<AccountingService.Line>();
        for (var i = 0; i < form.Lines.Count; i++)
        {
            var l = form.Lines[i];
            var dr = Math.Round(l.Debit ?? 0, 2);
            var cr = Math.Round(l.Credit ?? 0, 2);
            if (l.AccountId is null) ModelState.AddModelError("", $"Line {i + 1}: pick an account.");
            else if (dr == 0 && cr == 0) ModelState.AddModelError("", $"Line {i + 1}: enter a debit or a credit amount.");
            else lines.Add(new AccountingService.Line(l.AccountId.Value, dr, cr, l.Narration));
        }
        if (form.Lines.Count == 0) ModelState.AddModelError("", "Add at least two lines.");
        while (form.Lines.Count < 2) form.Lines.Add(new VoucherLineForm());
        return ModelState.IsValid ? lines : null;
    }

    private async Task<IActionResult> FormView(VoucherForm form) => View("Form", new VoucherFormPage
    {
        Form = form,
        Accounts = await _lookups.DetailAccountsAsync(),
        CanPost = User.HasPermission(P.Vouchers.Create),
    });
}
