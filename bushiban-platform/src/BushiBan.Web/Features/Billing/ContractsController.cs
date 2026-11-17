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
public class ContractsController : Controller
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly BranchClock _clock;
    private readonly LookupService _lookups;

    public ContractsController(AppDbContext db, BillingService billing, BranchClock clock, LookupService lookups)
    {
        _db = db; _billing = billing; _clock = clock; _lookups = lookups;
    }

    public async Task<IActionResult> Index(ContractFilter f)
    {
        var q = _db.StudentContracts.AsQueryable();
        if (f.Status is not null) q = q.Where(c => c.Status == f.Status);
        if (f.CourseId is not null) q = q.Where(c => c.CourseId == f.CourseId);
        if (f.StudentId is not null) q = q.Where(c => c.StudentId == f.StudentId);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(c => (c.Student!.FirstName + " " + c.Student.LastName).Contains(t) || c.Student.StudentNo.Contains(t));
        }
        var rows = await q.OrderBy(c => c.Status).ThenByDescending(c => c.StartDate).ThenByDescending(c => c.Id)
            .Select(c => new ContractRow
            {
                Id = c.Id, StudentId = c.StudentId, StudentName = c.Student!.FirstName + " " + c.Student.LastName, StudentNo = c.Student.StudentNo,
                Program = c.Course!.Name, StartDate = c.StartDate, TermMonths = c.TermMonths, MonthlyFee = c.MonthlyFee,
                MonthlyDiscount = c.MonthlyDiscount, BilledThrough = c.BilledThrough, Status = c.Status,
            })
            .ToPagedAsync(f.Page, 25);
        return View(new ContractIndexPage { Filter = f, Rows = rows, Programs = await _lookups.CoursesAsync(f.CourseId, activeOnly: false) });
    }

    [HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Create(int? studentId)
    {
        var form = new ContractForm { StudentId = studentId, StartDate = _clock.Today };
        if (studentId is not null)
        {
            var student = await _db.Students.Where(s => s.Id == studentId)
                .Select(s => new { s.CourseId, s.Discount, Fee = s.Course!.FeeStructure, Term = (int?)s.Course.MinimumTermMonths })
                .FirstOrDefaultAsync();
            if (student is null) return NotFound();
            form.CourseId = student.CourseId;
            form.MonthlyDiscount = student.Discount;
            form.TermMonths = student.Term ?? 12;
            if (student.Fee is not null)
            {
                form.AdmissionFee = student.Fee.AdmissionFee;
                form.SecurityFee = student.Fee.SecurityFee;
                form.MonthlyFee = student.Fee.MonthlyFee;
            }
        }
        return await FormView(form);
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Create(ContractForm form)
    {
        if (form.StudentId is null) ModelState.AddModelError(nameof(form.StudentId), "Pick a student.");
        if (form.CourseId is null) ModelState.AddModelError(nameof(form.CourseId), "Pick a program.");
        if (!ModelState.IsValid) return await FormView(form);
        try
        {
            var contract = await _billing.CreateContractAsync(new ContractTerms(form.StudentId!.Value, form.CourseId!.Value, form.StartDate, form.TermMonths,
                form.AdmissionFee, form.SecurityFee, form.MonthlyFee, form.MonthlyDiscount, form.BillingDay, form.PreferredMethod, form.Notes));
            var invoice = await _db.Invoices.Where(i => i.ContractId == contract.Id).Select(i => new { i.InvoiceNo, Total = i.Lines.Sum(l => l.Amount) }).FirstOrDefaultAsync();
            this.Success(invoice is null
                ? "Contract created. Tuition will be billed by the monthly billing run."
                : $"Contract created, and sign-up invoice {invoice.InvoiceNo} issued for {Format.Money(invoice.Total)}.");
            return RedirectToAction(nameof(Details), new { id = contract.Id });
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            ModelState.AddModelError("", ex.Message);
            return await FormView(form);
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _db.StudentContracts.Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.StudentId, Program = x.Course!.Name, x.StartDate, x.TermMonths, x.AdmissionFee, x.SecurityFee, x.MonthlyFee, x.MonthlyDiscount,
                x.BillingDay, x.PreferredMethod, x.Status, x.CancelledOn, x.BilledThrough, x.Notes, x.CreatedBy, x.CreatedAt,
            })
            .FirstOrDefaultAsync();
        if (c is null) return NotFound();
        var today = _clock.Today;
        var invoices = await _db.Invoices.Where(i => i.ContractId == id).OrderBy(i => i.IssueDate).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.InvoiceNo, i.IssueDate, i.DueDate, i.PeriodMonth, i.Description, Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount), i.Status })
            .ToListAsync();

        var startMonth = BillingService.FirstOfMonth(c.StartDate);
        var byMonth = invoices.Where(i => i.PeriodMonth != null).GroupBy(i => i.PeriodMonth!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Status == InvoiceStatus.Void).First());
        var schedule = Enumerable.Range(0, c.TermMonths).Select(n =>
        {
            var m = startMonth.AddMonths(n);
            var due = new DateOnly(m.Year, m.Month, c.BillingDay);
            if (due < c.StartDate) due = c.StartDate;
            return byMonth.TryGetValue(m, out var inv)
                ? new ScheduleMonth(m, inv.DueDate, inv.Id, inv.InvoiceNo, inv.Status, inv.Status == InvoiceStatus.Void ? 0 : Math.Round(inv.Total - inv.Paid, 2))
                : new ScheduleMonth(m, due, null, null, null, null);
        }).ToList();

        var page = new ContractDetailsPage
        {
            Id = c.Id, Student = (await _db.StudentHeaderAsync(c.StudentId))!, Program = c.Program, StartDate = c.StartDate, TermMonths = c.TermMonths,
            AdmissionFee = c.AdmissionFee, SecurityFee = c.SecurityFee, MonthlyFee = c.MonthlyFee, MonthlyDiscount = c.MonthlyDiscount,
            BillingDay = c.BillingDay, PreferredMethod = c.PreferredMethod, Status = c.Status, CancelledOn = c.CancelledOn, BilledThrough = c.BilledThrough,
            Notes = c.Notes, CreatedBy = c.CreatedBy, CreatedAt = c.CreatedAt, Schedule = schedule, Today = today,
            Invoices = invoices.Select(i => new ContractInvoice(i.Id, i.InvoiceNo, i.IssueDate, i.DueDate, i.Description, Math.Round(i.Total, 2), Math.Round(i.Paid, 2), i.Status)).ToList(),
            Quote = c.Status is ContractStatus.Active or ContractStatus.OnHold ? await _billing.QuoteCancellationAsync(id, today) : null,
        };
        return View(page);
    }

    /// <summary>Fee suggestion for a different cancellation date (used by the cancel form).</summary>
    [HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Quote(int id, DateOnly date)
    {
        try { return Json(await _billing.QuoteCancellationAsync(id, date)); }
        catch (BillingException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> Cancel(int id, DateOnly? date, string? reason, bool chargeFee, decimal? fee)
    {
        try
        {
            var c = await _billing.CancelContractAsync(id, date ?? _clock.Today, reason ?? "", chargeFee ? fee : null);
            this.Success(chargeFee && fee > 0
                ? $"Contract cancelled. A cancellation fee of {Format.Money(fee)} was invoiced."
                : "Contract cancelled. No more tuition will be billed.");
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            this.Error($"Couldn't cancel the contract: {ex.Message}");
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> FormView(ContractForm form)
    {
        var programs = await _db.Courses.Where(c => c.IsActive || c.Id == form.CourseId).OrderBy(c => c.Name)
            .Select(c => new ProgramPrice(c.Id, c.Name,
                c.FeeStructure != null ? c.FeeStructure.AdmissionFee : 0, c.FeeStructure != null ? c.FeeStructure.SecurityFee : 0,
                c.FeeStructure != null ? c.FeeStructure.MonthlyFee : 0, c.MinimumTermMonths, c.FeeStructure != null))
            .ToListAsync();
        return View("Create", new ContractFormPage
        {
            Form = form, Students = await _db.StudentOptionsAsync(form.StudentId), Programs = programs, Today = _clock.Today,
            Account = form.StudentId is int sid ? await _billing.GetStudentSummaryAsync(sid) : null,
        });
    }
}
