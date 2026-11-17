using System.Globalization;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Billing;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;
using Line = BushiBan.Web.Services.AccountingService.Line;

namespace BushiBan.Web.Features.Billing;

/// <summary>
/// Contracts, invoices and payments. Every amount that changes hands is posted through <see cref="AccountingService"/>,
/// so the student ledger and the books always agree:
/// <list type="bullet">
/// <item>Invoice: Dr Student receivables / Cr each line's income or deposit account (fee invoice voucher).</item>
/// <item>Payment: Dr Cash or Bank / Cr Student receivables (receipt voucher), then applied to invoices oldest first.</item>
/// <item>Voids reverse the original voucher; nothing posted is ever edited.</item>
/// </list>
/// Each public operation runs in its own database transaction.
/// </summary>
public class BillingService
{
    public const string InvoiceSourcePrefix = "Invoice ";
    public const string PaymentSourcePrefix = "Payment ";
    public const string LateFeePrefix = "Late fee for ";
    public const decimal CancellationFeeCap = 250m;

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private readonly AppDbContext _db;
    private readonly AccountingService _accounting;
    private readonly NumberSequenceService _numbers;
    private readonly ICurrentBranch _branch;
    private readonly BranchClock _clock;

    public BillingService(AppDbContext db, AccountingService accounting, NumberSequenceService numbers, ICurrentBranch branch, BranchClock clock)
    {
        _db = db; _accounting = accounting; _numbers = numbers; _branch = branch; _clock = clock;
    }

    // ───────────────────────────── Contracts ─────────────────────────────

    /// <summary>
    /// Signs a student up. Issues a sign-up invoice for the admission fee and security deposit, plus the first month's tuition
    /// when the start month has already arrived. Later months come from <see cref="RunMonthlyBillingAsync"/>.
    /// </summary>
    public async Task<StudentContract> CreateContractAsync(ContractTerms t, DateOnly? signedOn = null, CancellationToken ct = default)
    {
        var today = signedOn ?? _clock.Today;
        if (t.TermMonths is < 1 or > 120) throw new BillingException("The term must be between 1 and 120 months.");
        if (t.BillingDay is < 1 or > 28) throw new BillingException("The billing day must be between 1 and 28, so it exists in every month.");
        if (t.AdmissionFee < 0 || t.SecurityFee < 0 || t.MonthlyFee < 0 || t.MonthlyDiscount < 0) throw new BillingException("Amounts can't be negative.");
        if (t.MonthlyDiscount > t.MonthlyFee) throw new BillingException("The monthly discount can't be more than the monthly fee.");

        return await InTransactionAsync(async () =>
        {
            var acc = await AccountsAsync(ct);
            var student = await StudentAsync(t.StudentId, ct);
            var course = await _db.Courses.Where(c => c.Id == t.CourseId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct)
                ?? throw new BillingException("Pick a program.");
            if (await _db.StudentContracts.AnyAsync(c => c.StudentId == t.StudentId && c.CourseId == t.CourseId && c.Status == ContractStatus.Active, ct))
                throw new BillingException($"{student.Name} already has an active {course.Name} contract. Cancel it first or pick another program.");

            var contract = new StudentContract
            {
                StudentId = t.StudentId, CourseId = t.CourseId, StartDate = t.StartDate, TermMonths = t.TermMonths,
                AdmissionFee = Money(t.AdmissionFee), SecurityFee = Money(t.SecurityFee), MonthlyFee = Money(t.MonthlyFee), MonthlyDiscount = Money(t.MonthlyDiscount),
                BillingDay = t.BillingDay, PreferredMethod = t.PreferredMethod, Status = ContractStatus.Active,
                Notes = Trim(t.Notes, 500), FormSubmissionId = t.FormSubmissionId,
            };
            _db.StudentContracts.Add(contract);
            await _db.SaveChangesAsync(ct);

            var lines = new List<NewInvoiceLine>();
            if (contract.AdmissionFee > 0) lines.Add(new($"{course.Name} admission fee", contract.AdmissionFee, acc.Admission));
            if (contract.SecurityFee > 0) lines.Add(new($"{course.Name} security deposit (refundable)", contract.SecurityFee, acc.Deposit));

            var startMonth = FirstOfMonth(t.StartDate);
            DateOnly? period = null;
            if (startMonth <= FirstOfMonth(today))
            {
                if (contract.NetMonthly > 0) lines.Add(new(TuitionText(course.Name, startMonth), contract.NetMonthly, acc.Tuition));
                period = startMonth;
                contract.BilledThrough = startMonth;
            }

            if (lines.Count > 0)
            {
                await IssueInvoiceAsync(acc, student, contract.Id, today, today, contract.NetMonthly > 0 ? period : null,
                    $"{course.Name} sign-up", lines, ct);
            }
            await _db.SaveChangesAsync(ct);
            await ApplyCreditAsync(student.Id, ct);
            return contract;
        }, ct);
    }

    /// <summary>The agreement's cancellation fee: $250 or what's left of the agreement, whichever is less.</summary>
    public async Task<CancellationQuote> QuoteCancellationAsync(int contractId, DateOnly date, CancellationToken ct = default)
    {
        var c = await _db.StudentContracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new BillingException("Contract not found.");
        // The remainder is the tuition for the months of the term after the cancellation month.
        var cancelMonth = FirstOfMonth(date);
        var remaining = TermMonths(c).Count(m => m > cancelMonth);
        var remainder = Money(remaining * c.NetMonthly);
        return new CancellationQuote(remaining, remainder, Math.Min(CancellationFeeCap, remainder));
    }

    /// <summary>Stops billing. Optionally charges a cancellation fee (credited to the late-fee / other income account).</summary>
    public async Task<StudentContract> CancelContractAsync(int contractId, DateOnly date, string reason, decimal? cancellationFee, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("Enter a reason for the cancellation.");
        var fee = Money(cancellationFee ?? 0);
        if (fee < 0) throw new BillingException("The cancellation fee can't be negative.");

        return await InTransactionAsync(async () =>
        {
            var c = await _db.StudentContracts.Include(x => x.Course).FirstOrDefaultAsync(x => x.Id == contractId, ct)
                ?? throw new BillingException("Contract not found.");
            if (c.Status is ContractStatus.Cancelled or ContractStatus.Completed)
                throw new BillingException($"This contract is already {c.Status.DisplayName().ToLowerInvariant()}.");
            if (date < c.StartDate.AddMonths(-1)) throw new BillingException("The cancellation date is before the contract starts.");

            c.Status = ContractStatus.Cancelled;
            c.CancelledOn = date;
            c.Notes = Trim(string.Join("\n", new[] { c.Notes, $"Cancelled {Format.Date(date)}: {reason.Trim()}" }.Where(s => !string.IsNullOrWhiteSpace(s))), 500);

            if (fee > 0)
            {
                var acc = await AccountsAsync(ct);
                var student = await StudentAsync(c.StudentId, ct);
                await IssueInvoiceAsync(acc, student, c.Id, date, date, null, $"{c.Course!.Name} cancellation fee",
                    new[] { new NewInvoiceLine($"{c.Course.Name} early cancellation fee", fee, acc.LateFee) }, ct);
            }
            await _db.SaveChangesAsync(ct);
            if (fee > 0) await ApplyCreditAsync(c.StudentId, ct);
            return c;
        }, ct);
    }

    // ───────────────────────────── Invoices ─────────────────────────────

    /// <summary>A one-off invoice (uniforms, gear, events…). Each line names the income account it is credited to.</summary>
    public async Task<Invoice> CreateInvoiceAsync(int studentId, DateOnly issueDate, DateOnly dueDate, string description,
        IReadOnlyList<NewInvoiceLine> lines, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new BillingException("Enter a description for the invoice.");
        if (dueDate < issueDate) throw new BillingException("The due date can't be before the invoice date.");
        return await InTransactionAsync(async () =>
        {
            var acc = await AccountsAsync(ct);
            var student = await StudentAsync(studentId, ct);
            var invoice = await IssueInvoiceAsync(acc, student, null, issueDate, dueDate, null, description, lines, ct);
            await _db.SaveChangesAsync(ct);
            await ApplyCreditAsync(studentId, ct);
            return invoice;
        }, ct);
    }

    /// <summary>
    /// Bills tuition for every active contract, month by month, up to <paramref name="asOf"/>'s month and never past the end of the term.
    /// Safe to run any number of times: a month is only ever billed once per contract.
    /// </summary>
    public async Task<BillingRunResult> RunMonthlyBillingAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var acc = await AccountsAsync(ct);
        var contracts = await _db.StudentContracts.Where(c => c.Status == ContractStatus.Active)
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.StudentId, c.StartDate, c.TermMonths, c.BillingDay, c.MonthlyFee, c.MonthlyDiscount, c.BilledThrough,
                Program = c.Course!.Name, StudentName = c.Student!.FirstName + " " + c.Student.LastName,
            })
            .ToListAsync(ct);
        var ids = contracts.Select(c => c.Id).ToList();
        var billed = (await _db.Invoices.Where(i => i.ContractId != null && i.PeriodMonth != null && ids.Contains(i.ContractId.Value))
                .Select(i => new { ContractId = i.ContractId!.Value, Period = i.PeriodMonth!.Value }).ToListAsync(ct))
            .ToLookup(x => x.ContractId, x => x.Period);

        int created = 0, completed = 0;
        decimal total = 0;
        var problems = new List<string>();
        var asOfMonth = FirstOfMonth(asOf);

        foreach (var c in contracts)
        {
            var startMonth = FirstOfMonth(c.StartDate);
            var lastMonth = startMonth.AddMonths(c.TermMonths - 1);
            var from = c.BilledThrough is { } through && through.AddMonths(1) > startMonth ? FirstOfMonth(through).AddMonths(1) : startMonth;
            var already = billed[c.Id].ToHashSet();
            var months = new List<DateOnly>();
            for (var m = from; m <= asOfMonth && m <= lastMonth; m = m.AddMonths(1))
                if (!already.Contains(m)) months.Add(m);
            var endDate = c.StartDate.AddMonths(c.TermMonths).AddDays(-1);
            var lastMonthBilled = already.Contains(lastMonth) || months.Contains(lastMonth) || c.BilledThrough >= lastMonth;
            var finished = asOf > endDate && lastMonthBilled;
            if (months.Count == 0 && !finished) continue;

            try
            {
                var (count, amount) = await InTransactionAsync(async () =>
                {
                    var net = Math.Max(0, c.MonthlyFee - c.MonthlyDiscount);
                    var student = new StudentRef(c.StudentId, c.StudentName);
                    decimal sum = 0;
                    var n = 0;
                    if (net > 0)
                    {
                        foreach (var m in months)
                        {
                            var due = new DateOnly(m.Year, m.Month, c.BillingDay);
                            if (due < c.StartDate) due = c.StartDate;
                            var text = TuitionText(c.Program, m);
                            await IssueInvoiceAsync(acc, student, c.Id, due <= asOf ? due : asOf, due, m, text,
                                new[] { new NewInvoiceLine(text, net, acc.Tuition) }, ct);
                            sum += net;
                            n++;
                        }
                    }
                    var entity = await _db.StudentContracts.FirstAsync(x => x.Id == c.Id, ct);
                    if (months.Count > 0 && (entity.BilledThrough is null || entity.BilledThrough < months[^1])) entity.BilledThrough = months[^1];
                    if (finished) entity.Status = ContractStatus.Completed;
                    await _db.SaveChangesAsync(ct);
                    if (n > 0) await ApplyCreditAsync(c.StudentId, ct);
                    return (n, sum);
                }, ct);
                created += count;
                total += amount;
                if (finished) completed++;
            }
            catch (Exception ex) when (ex is BillingException or AccountingException)
            {
                problems.Add($"{c.StudentName} ({c.Program}): {ex.Message}");
            }
        }
        return new BillingRunResult(created, Money(total), completed, problems);
    }

    /// <summary>
    /// Adds one late-fee invoice for each unpaid tuition invoice more than the grace period past due.
    /// Never charges twice for the same invoice, even if the late fee was later voided.
    /// </summary>
    public async Task<LateFeeResult> ApplyLateFeesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var acc = await AccountsAsync(ct);
        if (acc.LateFeeAmount <= 0) return new LateFeeResult(0, 0, new[] { "The late fee is set to $0.00 in Branch settings, so nothing was charged." });
        var cutoff = asOf.AddDays(-acc.GraceDays);
        var overdue = await _db.Invoices
            .Where(i => i.PeriodMonth != null && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < cutoff)
            .OrderBy(i => i.DueDate).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.InvoiceNo, i.StudentId, i.ContractId, StudentName = i.Student!.FirstName + " " + i.Student.LastName })
            .ToListAsync(ct);
        var charged = (await _db.Invoices.Where(i => i.Description.StartsWith(LateFeePrefix)).Select(i => i.Description).ToListAsync(ct)).ToHashSet();

        int count = 0;
        var problems = new List<string>();
        foreach (var inv in overdue)
        {
            var description = LateFeePrefix + inv.InvoiceNo;
            if (charged.Contains(description)) continue;
            try
            {
                await InTransactionAsync(async () =>
                {
                    await IssueInvoiceAsync(acc, new StudentRef(inv.StudentId, inv.StudentName), inv.ContractId, asOf, asOf, null, description,
                        new[] { new NewInvoiceLine($"Late payment fee ({inv.InvoiceNo})", acc.LateFeeAmount, acc.LateFee) }, ct);
                    await _db.SaveChangesAsync(ct);
                    await ApplyCreditAsync(inv.StudentId, ct);
                    return true;
                }, ct);
                count++;
            }
            catch (Exception ex) when (ex is BillingException or AccountingException)
            {
                problems.Add($"{inv.StudentName} ({inv.InvoiceNo}): {ex.Message}");
            }
        }
        return new LateFeeResult(count, Money(count * acc.LateFeeAmount), problems);
    }

    /// <summary>Voids an unpaid invoice and reverses its voucher. Invoices with payments applied must have those payments voided first.</summary>
    public async Task VoidInvoiceAsync(int invoiceId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("Enter a reason for voiding the invoice.");
        await InTransactionAsync(async () =>
        {
            var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new BillingException("Invoice not found.");
            if (invoice.Status == InvoiceStatus.Void) throw new BillingException("This invoice is already void.");
            if (await _db.PaymentAllocations.AnyAsync(a => a.InvoiceId == invoiceId, ct))
                throw new BillingException("Payments are applied to this invoice. Void those payments first, then void the invoice.");
            if (invoice.VoucherId is int voucherId)
                await _accounting.ReverseAsync(voucherId, VoidDate(invoice.IssueDate), $"Invoice {invoice.InvoiceNo} voided: {reason.Trim()}", ct);
            invoice.Status = InvoiceStatus.Void;
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    // ───────────────────────────── Payments ─────────────────────────────

    /// <summary>
    /// Records money received. Applies it to the given invoices, or to the oldest due invoices first when none are given.
    /// Anything left over stays on the student's account as credit and is used automatically by their next invoice.
    /// </summary>
    public async Task<Payment> ReceivePaymentAsync(int studentId, DateOnly date, decimal amount, PaymentMethod method, string? reference,
        int? depositAccountId = null, IReadOnlyList<Allocation>? allocations = null, CancellationToken ct = default)
    {
        amount = Money(amount);
        if (amount <= 0) throw new BillingException("Enter an amount greater than zero.");
        if (amount > 1_000_000) throw new BillingException("That amount is too large for a single payment.");

        return await InTransactionAsync(async () =>
        {
            var acc = await AccountsAsync(ct);
            var student = await StudentAsync(studentId, ct);
            var open = await OpenInvoicesAsync(studentId, ct);

            List<Allocation> plan;
            if (allocations is null) plan = SuggestAllocation(open, amount);
            else
            {
                plan = allocations.Where(a => a.Amount != 0).Select(a => a with { Amount = Money(a.Amount) }).ToList();
                foreach (var a in plan)
                {
                    var inv = open.FirstOrDefault(o => o.Id == a.InvoiceId)
                        ?? throw new BillingException("One of the invoices isn't open for this student any more. Reload the page and try again.");
                    if (a.Amount < 0) throw new BillingException($"The amount applied to {inv.InvoiceNo} can't be negative.");
                    if (a.Amount > inv.Balance) throw new BillingException($"{Format.Money(a.Amount)} is more than the {Format.Money(inv.Balance)} still owed on {inv.InvoiceNo}.");
                }
                if (plan.GroupBy(a => a.InvoiceId).Any(g => g.Count() > 1)) throw new BillingException("Each invoice can only be listed once.");
                if (plan.Sum(a => a.Amount) > amount) throw new BillingException($"You've applied {Format.Money(plan.Sum(a => a.Amount))} to invoices, but the payment is only {Format.Money(amount)}.");
            }

            var deposit = depositAccountId ?? (method == PaymentMethod.Cash ? acc.Cash : acc.Bank);
            var branchId = _branch.BranchId ?? throw new BillingException("Pick a branch first.");
            var receiptNo = await _numbers.NextFormattedAsync(branchId, "RCT", date.Year, ct);
            var memo = string.IsNullOrWhiteSpace(reference) ? method.DisplayName() : $"{method.DisplayName()} {reference.Trim()}";
            var voucher = await _accounting.CreateAsync(VoucherType.Receipt, date, Trim($"Payment {receiptNo} from {student.Name} ({memo})", 500)!,
                new[] { new Line(deposit, amount, 0, Trim(memo, 300)), new Line(acc.Receivable, 0, amount, Trim(student.Name, 300)) },
                PaymentSourcePrefix + receiptNo, ct: ct);

            var payment = new Payment
            {
                ReceiptNo = receiptNo, StudentId = studentId, Date = date, Amount = amount, Method = method,
                Reference = Trim(reference, 100), DepositAccountId = deposit, Voucher = voucher,
                Allocations = plan.Select(a => new PaymentAllocation { InvoiceId = a.InvoiceId, Amount = a.Amount }).ToList(),
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync(ct);
            await RefreshStatusesAsync(plan.Select(a => a.InvoiceId), ct);
            return payment;
        }, ct);
    }

    /// <summary>Undoes a payment: takes it off the invoices it paid, reverses its voucher and marks it void.</summary>
    public async Task VoidPaymentAsync(int paymentId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BillingException("Enter a reason for voiding the payment.");
        await InTransactionAsync(async () =>
        {
            var payment = await _db.Payments.Include(p => p.Allocations).FirstOrDefaultAsync(p => p.Id == paymentId, ct)
                ?? throw new BillingException("Payment not found.");
            if (payment.IsVoided) throw new BillingException("This payment is already void.");
            var invoiceIds = payment.Allocations.Select(a => a.InvoiceId).ToList();
            _db.PaymentAllocations.RemoveRange(payment.Allocations);
            if (payment.VoucherId is int voucherId)
                await _accounting.ReverseAsync(voucherId, VoidDate(payment.Date), $"Payment {payment.ReceiptNo} voided: {reason.Trim()}", ct);
            payment.IsVoided = true;
            await _db.SaveChangesAsync(ct);
            await RefreshStatusesAsync(invoiceIds, ct);
            return true;
        }, ct);
    }

    /// <summary>Oldest due first; whatever doesn't fit stays as credit.</summary>
    public static List<Allocation> SuggestAllocation(IEnumerable<OpenInvoice> open, decimal amount)
    {
        var left = Money(amount);
        var plan = new List<Allocation>();
        foreach (var inv in open.Where(o => o.Balance > 0).OrderBy(o => o.DueDate).ThenBy(o => o.IssueDate).ThenBy(o => o.Id))
        {
            if (left <= 0) break;
            var apply = Math.Min(left, inv.Balance);
            plan.Add(new Allocation(inv.Id, apply));
            left -= apply;
        }
        return plan;
    }

    // ───────────────────────────── Balances ─────────────────────────────

    /// <summary>Open and partly paid invoices with something still owed, oldest due first.</summary>
    public async Task<List<OpenInvoice>> OpenInvoicesAsync(int studentId, CancellationToken ct = default)
    {
        var rows = await _db.Invoices
            .Where(i => i.StudentId == studentId && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid))
            .OrderBy(i => i.DueDate).ThenBy(i => i.IssueDate).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.InvoiceNo, i.IssueDate, i.DueDate, i.Description, Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount) })
            .ToListAsync(ct);
        return rows.Select(r => new OpenInvoice(r.Id, r.InvoiceNo, r.IssueDate, r.DueDate, r.Description, Money(r.Total), Money(r.Paid)))
            .Where(o => o.Balance > 0).ToList();
    }

    /// <summary>Unapplied money per payment (credit on account), oldest first.</summary>
    private async Task<List<(int PaymentId, decimal Left)>> CreditsAsync(int studentId, CancellationToken ct)
    {
        var rows = await _db.Payments.Where(p => p.StudentId == studentId && !p.IsVoided)
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Amount, Used = p.Allocations.Sum(a => a.Amount) })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, Money(r.Amount - r.Used))).Where(r => r.Item2 > 0).ToList();
    }

    public async Task<StudentBalance> GetStudentBalanceAsync(int studentId, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var today = asOf ?? _clock.Today;
        var open = await OpenInvoicesAsync(studentId, ct);
        var credit = (await CreditsAsync(studentId, ct)).Sum(c => c.Left);
        return new StudentBalance(open.Sum(o => o.Balance), open.Where(o => o.DueDate < today).Sum(o => o.Balance), credit);
    }

    /// <summary>Balance, credit, active contract and last payment, for the student profile.</summary>
    public async Task<StudentAccountSummary?> GetStudentSummaryAsync(int studentId, CancellationToken ct = default)
    {
        var name = await _db.Students.Where(s => s.Id == studentId).Select(s => s.FirstName + " " + s.LastName).FirstOrDefaultAsync(ct);
        if (name is null) return null;
        var balance = await GetStudentBalanceAsync(studentId, ct: ct);
        var openCount = (await OpenInvoicesAsync(studentId, ct)).Count;
        var contract = await _db.StudentContracts.Where(c => c.StudentId == studentId && c.Status == ContractStatus.Active)
            .OrderByDescending(c => c.StartDate)
            .Select(c => new { c.Id, Program = c.Course!.Name, c.MonthlyFee, c.MonthlyDiscount, c.StartDate, c.TermMonths })
            .FirstOrDefaultAsync(ct);
        var last = await _db.Payments.Where(p => p.StudentId == studentId && !p.IsVoided).OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .Select(p => new { p.Id, p.Date, p.Amount, p.ReceiptNo }).FirstOrDefaultAsync(ct);
        return new StudentAccountSummary
        {
            StudentId = studentId, StudentName = name, Balance = balance, OpenInvoices = openCount,
            ContractId = contract?.Id, ContractProgram = contract?.Program,
            ContractMonthly = contract is null ? null : Math.Max(0, contract.MonthlyFee - contract.MonthlyDiscount),
            ContractStart = contract?.StartDate, ContractEnd = contract?.StartDate.AddMonths(contract.TermMonths).AddDays(-1),
            LastPaymentId = last?.Id, LastPaymentDate = last?.Date, LastPaymentAmount = last?.Amount, LastPaymentReceipt = last?.ReceiptNo,
        };
    }

    /// <summary>Tiles and receivables aging for the billing overview.</summary>
    public async Task<BillingOverview> OverviewAsync(DateOnly today, CancellationToken ct = default)
    {
        var monthStart = FirstOfMonth(today);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var billed = await _db.Invoices.Where(i => i.Status != InvoiceStatus.Void && i.IssueDate >= monthStart && i.IssueDate <= monthEnd)
            .SelectMany(i => i.Lines).SumAsync(l => l.Amount, ct);
        var collected = await _db.Payments.Where(p => !p.IsVoided && p.Date >= monthStart && p.Date <= monthEnd).SumAsync(p => p.Amount, ct);
        var paymentsTotal = await _db.Payments.Where(p => !p.IsVoided).SumAsync(p => p.Amount, ct);
        var allocatedTotal = await _db.Payments.Where(p => !p.IsVoided).SelectMany(p => p.Allocations).SumAsync(a => a.Amount, ct);
        var activeContracts = await _db.StudentContracts.CountAsync(c => c.Status == ContractStatus.Active, ct);

        var open = await _db.Invoices.Where(i => i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid)
            .Select(i => new
            {
                i.StudentId, StudentName = i.Student!.FirstName + " " + i.Student.LastName, i.Student.StudentNo, i.DueDate,
                Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount),
            })
            .ToListAsync(ct);

        var aging = open.Select(o => new { o.StudentId, o.StudentName, o.StudentNo, Late = today.DayNumber - o.DueDate.DayNumber, Balance = Money(o.Total - o.Paid) })
            .Where(o => o.Balance > 0)
            .GroupBy(o => (o.StudentId, o.StudentName, o.StudentNo))
            .Select(g => new AgingRow(g.Key.StudentId, g.Key.StudentName, g.Key.StudentNo,
                g.Where(x => x.Late <= 0).Sum(x => x.Balance),
                g.Where(x => x.Late is >= 1 and <= 30).Sum(x => x.Balance),
                g.Where(x => x.Late is >= 31 and <= 60).Sum(x => x.Balance),
                g.Where(x => x.Late is >= 61 and <= 90).Sum(x => x.Balance),
                g.Where(x => x.Late > 90).Sum(x => x.Balance)))
            .OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Total).ThenBy(r => r.StudentName)
            .ToList();

        return new BillingOverview(Money(billed), Money(collected), aging.Sum(a => a.Total), aging.Sum(a => a.Overdue),
            Money(paymentsTotal - allocatedTotal), activeContracts, aging);
    }

    // ───────────────────────────── Internals ─────────────────────────────

    private sealed record StudentRef(int Id, string Name);

    /// <summary>The accounts billing posts to, from Branch settings. Fails with a plain explanation if any are missing.</summary>
    private sealed record BillingAccounts(int Receivable, int Tuition, int Admission, int Deposit, int LateFee, int Cash, int Bank, decimal LateFeeAmount, int GraceDays);

    private async Task<BillingAccounts> AccountsAsync(CancellationToken ct)
    {
        const string whereToFix = "An administrator can choose them under Setup → Branch settings.";
        var s = await _db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new BillingException("Billing accounts haven't been set up for this branch yet. " + whereToFix);
        var mapping = new (string Label, int? Id)[]
        {
            ("student receivables", s.ReceivableAccountId), ("tuition income", s.TuitionIncomeAccountId), ("admission income", s.AdmissionIncomeAccountId),
            ("security deposits", s.SecurityDepositAccountId), ("late fee income", s.LateFeeIncomeAccountId), ("cash", s.CashAccountId), ("bank", s.BankAccountId),
        };
        var missing = mapping.Where(m => m.Id is null).Select(m => m.Label).ToList();
        if (missing.Count > 0) throw new BillingException($"Billing can't post yet: no account is chosen for {string.Join(", ", missing)}. " + whereToFix);
        var ids = mapping.Select(m => m.Id!.Value).Distinct().ToList();
        var active = await _db.DetailAccounts.Where(a => ids.Contains(a.Id) && a.IsActive).Select(a => a.Id).ToListAsync(ct);
        var broken = mapping.Where(m => !active.Contains(m.Id!.Value)).Select(m => m.Label).ToList();
        if (broken.Count > 0) throw new BillingException($"The account chosen for {string.Join(", ", broken)} is deleted or inactive. " + whereToFix);
        return new BillingAccounts(s.ReceivableAccountId!.Value, s.TuitionIncomeAccountId!.Value, s.AdmissionIncomeAccountId!.Value, s.SecurityDepositAccountId!.Value,
            s.LateFeeIncomeAccountId!.Value, s.CashAccountId!.Value, s.BankAccountId!.Value, Money(s.LateFeeAmount), s.LateFeeGraceDays);
    }

    private async Task<StudentRef> StudentAsync(int studentId, CancellationToken ct) =>
        await _db.Students.Where(s => s.Id == studentId).Select(s => new StudentRef(s.Id, s.FirstName + " " + s.LastName)).FirstOrDefaultAsync(ct)
        ?? throw new BillingException("Pick a student.");

    /// <summary>Adds the invoice and its fee-invoice voucher (Dr receivables, Cr each line's account). The caller saves.</summary>
    private async Task<Invoice> IssueInvoiceAsync(BillingAccounts acc, StudentRef student, int? contractId, DateOnly issueDate, DateOnly dueDate,
        DateOnly? periodMonth, string description, IReadOnlyList<NewInvoiceLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0) throw new BillingException("Add at least one line to the invoice.");
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Description)) throw new BillingException("Every invoice line needs a description.");
            if (Money(l.Amount) <= 0) throw new BillingException($"The amount for \"{l.Description}\" must be greater than zero.");
        }
        var branchId = _branch.BranchId ?? throw new BillingException("Pick a branch first.");
        var invoiceNo = await _numbers.NextFormattedAsync(branchId, "INV", issueDate.Year, ct);
        var total = lines.Sum(l => Money(l.Amount));
        var voucherLines = new List<Line> { new(acc.Receivable, total, 0, Trim($"{student.Name}: {description}", 300)) };
        voucherLines.AddRange(lines.Select(l => new Line(l.CreditAccountId, 0, Money(l.Amount), Trim(l.Description, 300))));
        var voucher = await _accounting.CreateAsync(VoucherType.FeeInvoice, issueDate, Trim($"Invoice {invoiceNo} to {student.Name}: {description}", 500)!,
            voucherLines, InvoiceSourcePrefix + invoiceNo, ct: ct);

        var invoice = new Invoice
        {
            InvoiceNo = invoiceNo, StudentId = student.Id, ContractId = contractId, IssueDate = issueDate, DueDate = dueDate,
            PeriodMonth = periodMonth, Description = Trim(description, 300)!, Status = InvoiceStatus.Open, Voucher = voucher,
            Lines = lines.Select(l => new InvoiceLine { Description = Trim(l.Description, 200)!, Amount = Money(l.Amount), CreditAccountId = l.CreditAccountId }).ToList(),
        };
        _db.Invoices.Add(invoice);
        return invoice;
    }

    /// <summary>Uses any unapplied payments (credit on account) to pay the student's open invoices, oldest first.</summary>
    private async Task ApplyCreditAsync(int studentId, CancellationToken ct)
    {
        var credits = await CreditsAsync(studentId, ct);
        if (credits.Count == 0) return;
        var open = await OpenInvoicesAsync(studentId, ct);
        var touched = new List<int>();
        var c = 0;
        foreach (var inv in open)
        {
            var owed = inv.Balance;
            while (owed > 0 && c < credits.Count)
            {
                var apply = Math.Min(owed, credits[c].Left);
                _db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = credits[c].PaymentId, InvoiceId = inv.Id, Amount = apply });
                owed -= apply;
                credits[c] = credits[c] with { Left = credits[c].Left - apply };
                if (credits[c].Left <= 0) c++;
                touched.Add(inv.Id);
            }
            if (c >= credits.Count) break;
        }
        if (touched.Count == 0) return;
        await _db.SaveChangesAsync(ct);
        await RefreshStatusesAsync(touched, ct);
    }

    /// <summary>Sets Open / Partially paid / Paid from what's been applied (void invoices stay void).</summary>
    private async Task RefreshStatusesAsync(IEnumerable<int> invoiceIds, CancellationToken ct)
    {
        var ids = invoiceIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var amounts = await _db.Invoices.Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, Total = i.Lines.Sum(l => l.Amount), Paid = i.Allocations.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.Id, ct);
        var invoices = await _db.Invoices.Where(i => ids.Contains(i.Id) && i.Status != InvoiceStatus.Void).ToListAsync(ct);
        foreach (var inv in invoices)
        {
            var (total, paid) = (Money(amounts[inv.Id].Total), Money(amounts[inv.Id].Paid));
            inv.Status = paid <= 0 ? InvoiceStatus.Open : paid >= total ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction (inside the provider's retry strategy, which SQL Server requires).
    /// On failure nothing is kept: the transaction rolls back and pending changes are discarded.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null) return await work();
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                if (attempt++ > 0) _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var result = await work();
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Voids are dated today, but never before the original document.</summary>
    private DateOnly VoidDate(DateOnly original)
    {
        var today = _clock.Today;
        return today < original ? original : today;
    }

    private static IEnumerable<DateOnly> TermMonths(StudentContract c)
    {
        var start = FirstOfMonth(c.StartDate);
        for (var i = 0; i < c.TermMonths; i++) yield return start.AddMonths(i);
    }

    public static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);
    private static string TuitionText(string program, DateOnly month) => $"{program} tuition, {month.ToString("MMMM yyyy", Us)}";
    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string? Trim(string? s, int max) => s is null ? null : (s = s.Trim()).Length <= max ? s : s[..(max - 1)] + "…";
}
