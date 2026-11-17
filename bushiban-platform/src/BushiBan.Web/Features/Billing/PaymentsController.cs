using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Billing;

[HasPermission(P.Billing.View + "|" + P.Billing.ReceivePayments)]
public class PaymentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly BranchClock _clock;
    private readonly ICurrentBranch _branch;

    public PaymentsController(AppDbContext db, BillingService billing, BranchClock clock, ICurrentBranch branch)
    {
        _db = db; _billing = billing; _clock = clock; _branch = branch;
    }

    public async Task<IActionResult> Index(PaymentFilter f)
    {
        var q = _db.Payments.AsQueryable();
        if (!f.ShowVoided) q = q.Where(p => !p.IsVoided);
        if (f.From is not null) q = q.Where(p => p.Date >= f.From);
        if (f.To is not null) q = q.Where(p => p.Date <= f.To);
        if (f.Method is not null) q = q.Where(p => p.Method == f.Method);
        if (f.StudentId is not null) q = q.Where(p => p.StudentId == f.StudentId);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(p => p.ReceiptNo.Contains(t) || (p.Reference != null && p.Reference.Contains(t)) || (p.Student!.FirstName + " " + p.Student.LastName).Contains(t));
        }
        var rows = await q.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .Select(p => new PaymentRow
            {
                Id = p.Id, ReceiptNo = p.ReceiptNo, Date = p.Date, StudentId = p.StudentId, StudentName = p.Student!.FirstName + " " + p.Student.LastName,
                Method = p.Method, Reference = p.Reference, Amount = p.Amount, Applied = p.Allocations.Sum(a => a.Amount), IsVoided = p.IsVoided,
            })
            .ToPagedAsync(f.Page, 25);
        var total = await q.Where(p => !p.IsVoided).SumAsync(p => p.Amount);
        return View(new PaymentIndexPage { Filter = f, Rows = rows, Students = await _db.StudentOptionsAsync(f.StudentId), FilteredTotal = Math.Round(total, 2) });
    }

    [HasPermission(P.Billing.ReceivePayments)]
    public async Task<IActionResult> Create(int? studentId)
    {
        var form = new PaymentForm { StudentId = studentId, Date = _clock.Today };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Billing.ReceivePayments)]
    public async Task<IActionResult> Create(PaymentForm form)
    {
        if (form.StudentId is null) ModelState.AddModelError(nameof(form.StudentId), "Pick a student.");
        if ((form.Amount ?? 0) <= 0) ModelState.AddModelError(nameof(form.Amount), "Enter the amount received.");
        if (!ModelState.IsValid) return await FormView(form);

        // Amounts typed against invoices are used as given; with none, the payment goes to the oldest invoices first.
        var typed = form.Allocations.Where(a => (a.Amount ?? 0) != 0).Select(a => new Allocation(a.InvoiceId, a.Amount!.Value)).ToList();
        try
        {
            var payment = await _billing.ReceivePaymentAsync(form.StudentId!.Value, form.Date, form.Amount!.Value, form.Method, form.Reference,
                form.DepositAccountId, typed.Count > 0 ? typed : null);
            var unapplied = payment.Amount - payment.Allocations.Sum(a => a.Amount);
            this.Success($"Payment {payment.ReceiptNo} recorded for {Format.Money(payment.Amount)}."
                         + (unapplied > 0 ? $" {Format.Money(unapplied)} is kept as credit on the student's account." : ""));
            return RedirectToAction(nameof(Details), new { id = payment.Id });
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            ModelState.AddModelError("", ex.Message);
            return await FormView(form);
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await _db.Payments.Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.ReceiptNo, x.Date, x.Amount, x.Method, x.Reference, x.IsVoided, x.VoucherId, x.StudentId, x.CreatedBy,
                VoucherNo = x.Voucher != null ? x.Voucher.VoucherNo : null,
                Deposit = x.DepositAccount!.Code + " - " + x.DepositAccount.Name,
                Lines = x.Allocations.OrderBy(a => a.Invoice!.DueDate).Select(a => new ReceiptLine(a.InvoiceId, a.Invoice!.InvoiceNo, a.Invoice.Description, a.Amount)).ToList(),
            })
            .FirstOrDefaultAsync();
        if (p is null) return NotFound();
        var page = new PaymentDetailsPage
        {
            Id = p.Id, ReceiptNo = p.ReceiptNo, Date = p.Date, Amount = p.Amount, Method = p.Method, Reference = p.Reference, IsVoided = p.IsVoided,
            VoucherId = p.VoucherId, VoucherNo = p.VoucherNo, DepositAccount = p.Deposit, CreatedBy = p.CreatedBy, Lines = p.Lines,
            Student = (await _db.StudentHeaderAsync(p.StudentId))!, Branch = await _db.LetterheadAsync(_branch.BranchId),
        };
        if (p.IsVoided && p.VoucherId is not null)
            page.VoidReason = await _db.Vouchers.Where(v => v.ReversalOfId == p.VoucherId).Select(v => v.Description).FirstOrDefaultAsync();
        page.StudentBalanceAfter = (await _billing.GetStudentBalanceAsync(p.StudentId)).Net;
        return View(page);
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Void(int id, string? reason)
    {
        try
        {
            await _billing.VoidPaymentAsync(id, reason ?? "");
            this.Success("Payment voided. It was taken off the invoices it paid and its voucher was reversed.");
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            this.Error($"Couldn't void the payment: {ex.Message}");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> FormView(PaymentForm form)
    {
        var page = new PaymentFormPage
        {
            Form = form, Today = _clock.Today,
            Students = await _db.StudentOptionsAsync(form.StudentId),
            DepositAccounts = await DepositAccountsAsync(form.DepositAccountId),
            OpenInvoices = form.StudentId is int sid ? await _billing.OpenInvoicesAsync(sid) : new(),
            Balance = form.StudentId is int s2 ? await _billing.GetStudentBalanceAsync(s2) : null,
        };
        return View("Create", page);
    }

    /// <summary>Accounts money can be deposited to: those beside the cash and bank accounts chosen in Branch settings (cash drawers, bank accounts).</summary>
    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> DepositAccountsAsync(int? selected)
    {
        var settings = await _db.AccountingSettings.Select(s => new { s.CashAccountId, s.BankAccountId }).FirstOrDefaultAsync();
        var mapped = new[] { settings?.CashAccountId, settings?.BankAccountId }.OfType<int>().ToList();
        var subIds = await _db.DetailAccounts.Where(a => mapped.Contains(a.Id)).Select(a => a.SubAccountId).Distinct().ToListAsync();
        return await _db.DetailAccounts
            .Where(a => (a.IsActive && (subIds.Contains(a.SubAccountId) || subIds.Count == 0 && a.SubAccount!.MainAccount!.AccountType == AccountType.Assets)) || a.Id == selected)
            .OrderBy(a => a.Code)
            .Select(a => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(a.Code + " - " + a.Name, a.Id.ToString(), a.Id == selected))
            .ToListAsync();
    }
}
