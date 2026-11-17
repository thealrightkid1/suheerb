using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Billing;

[HasPermission(P.Billing.View)]
public class InvoicesController : Controller
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly BranchClock _clock;
    private readonly ICurrentBranch _branch;

    public InvoicesController(AppDbContext db, BillingService billing, BranchClock clock, ICurrentBranch branch)
    {
        _db = db; _billing = billing; _clock = clock; _branch = branch;
    }

    public async Task<IActionResult> Index(InvoiceFilter f)
    {
        var today = _clock.Today;
        var q = _db.Invoices.AsQueryable();
        if (f.Status is not null) q = q.Where(i => i.Status == f.Status);
        if (f.Unpaid) q = q.Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid);
        if (f.Overdue) q = q.Where(i => (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < today);
        if (f.StudentId is not null) q = q.Where(i => i.StudentId == f.StudentId);
        if (f.From is not null) q = q.Where(i => i.IssueDate >= f.From);
        if (f.To is not null) q = q.Where(i => i.IssueDate <= f.To);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(i => i.InvoiceNo.Contains(t) || i.Description.Contains(t) || (i.Student!.FirstName + " " + i.Student.LastName).Contains(t));
        }
        var rows = await q.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id)
            .Select(i => new InvoiceRow
            {
                Id = i.Id, InvoiceNo = i.InvoiceNo, IssueDate = i.IssueDate, DueDate = i.DueDate, StudentId = i.StudentId,
                StudentName = i.Student!.FirstName + " " + i.Student.LastName, Description = i.Description,
                Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount), Status = i.Status,
            })
            .ToPagedAsync(f.Page, 25);
        // Two flat sums (SQL Server can't aggregate over a subquery).
        var unpaid = q.Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid);
        var outstanding = await unpaid.SelectMany(i => i.Lines).SumAsync(l => l.Amount) - await unpaid.SelectMany(i => i.Allocations).SumAsync(a => a.Amount);
        return View(new InvoiceIndexPage
        {
            Filter = f, Rows = rows, Students = await _db.StudentOptionsAsync(f.StudentId), Today = today, FilteredBalance = Math.Round(outstanding, 2),
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var i = await _db.Invoices.Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.InvoiceNo, x.IssueDate, x.DueDate, x.PeriodMonth, x.Description, x.Status, x.StudentId, x.ContractId,
                ContractProgram = x.Contract != null ? x.Contract.Course!.Name : null, x.VoucherId, VoucherNo = x.Voucher != null ? x.Voucher.VoucherNo : null,
                Lines = x.Lines.OrderBy(l => l.Id).Select(l => new InvoiceDetailsLine(l.Description, l.CreditAccount!.Code, l.CreditAccount.Name, l.Amount)).ToList(),
                Payments = x.Allocations.OrderBy(a => a.Payment!.Date).Select(a => new InvoicePaymentLine(a.PaymentId, a.Payment!.ReceiptNo, a.Payment.Date, a.Payment.Method, a.Amount)).ToList(),
            })
            .FirstOrDefaultAsync();
        if (i is null) return NotFound();
        var page = new InvoiceDetailsPage
        {
            Id = i.Id, InvoiceNo = i.InvoiceNo, IssueDate = i.IssueDate, DueDate = i.DueDate, PeriodMonth = i.PeriodMonth, Description = i.Description,
            Status = i.Status, ContractId = i.ContractId, ContractProgram = i.ContractProgram, VoucherId = i.VoucherId, VoucherNo = i.VoucherNo,
            Lines = i.Lines, Payments = i.Payments, Today = _clock.Today,
            Student = (await _db.StudentHeaderAsync(i.StudentId))!, Branch = await _db.LetterheadAsync(_branch.BranchId),
        };
        if (i.Status == InvoiceStatus.Void && i.VoucherId is not null)
            page.VoidReason = await _db.Vouchers.Where(v => v.ReversalOfId == i.VoucherId).Select(v => v.Description).FirstOrDefaultAsync();
        return View(page);
    }

    [HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Create(int? studentId)
    {
        var today = _clock.Today;
        var form = new InvoiceForm { StudentId = studentId, IssueDate = today, DueDate = today, Lines = { new() } };
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Create(InvoiceForm form)
    {
        if (form.StudentId is null) ModelState.AddModelError(nameof(form.StudentId), "Pick a student.");
        form.Lines = form.Lines.Where(l => !l.IsBlank).ToList();
        var lines = new List<NewInvoiceLine>();
        for (var n = 0; n < form.Lines.Count; n++)
        {
            var l = form.Lines[n];
            if (string.IsNullOrWhiteSpace(l.Description)) ModelState.AddModelError("", $"Line {n + 1}: enter what the charge is for.");
            else if ((l.Amount ?? 0) <= 0) ModelState.AddModelError("", $"Line {n + 1}: enter an amount greater than zero.");
            else if (l.AccountId is null) ModelState.AddModelError("", $"Line {n + 1}: pick the income account.");
            else lines.Add(new NewInvoiceLine(l.Description.Trim(), l.Amount!.Value, l.AccountId.Value));
        }
        if (form.Lines.Count == 0) { ModelState.AddModelError("", "Add at least one line."); form.Lines.Add(new()); }
        if (!ModelState.IsValid) return await FormView(form);
        try
        {
            var invoice = await _billing.CreateInvoiceAsync(form.StudentId!.Value, form.IssueDate, form.DueDate, form.Description ?? "", lines);
            this.Success($"Invoice {invoice.InvoiceNo} created.");
            return RedirectToAction(nameof(Details), new { id = invoice.Id });
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            ModelState.AddModelError("", ex.Message);
            return await FormView(form);
        }
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Void(int id, string? reason)
    {
        try
        {
            await _billing.VoidInvoiceAsync(id, reason ?? "");
            this.Success("Invoice voided and its voucher reversed.");
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            this.Error($"Couldn't void the invoice: {ex.Message}");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> FormView(InvoiceForm form)
    {
        var defaultAccount = await _db.DetailAccounts.Where(a => a.Code == "40-01-03-001").Select(a => (int?)a.Id).FirstOrDefaultAsync();
        return View("Create", new InvoiceFormPage
        {
            Form = form, Students = await _db.StudentOptionsAsync(form.StudentId),
            Accounts = await _db.AccountOptionsAsync(null, AccountType.Revenue, AccountType.Liabilities),
            DefaultAccountId = defaultAccount,
        });
    }
}
