using System.Net;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Billing;
using BushiBan.Web.Features.Billing;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BushiBan.Tests;

public class BillingTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public BillingTests(TestApp app) => _app = app;

    private Task<int> CourseIdAsync(string name = "Youth Martial Arts") =>
        _app.InPasadenaAsync(sp => sp.GetRequiredService<AppDbContext>().Courses.Where(c => c.Name == name).Select(c => c.Id).SingleAsync());

    private async Task<StudentContract> ContractAsync(int studentId, DateOnly start, int months, decimal monthly = 80, decimal discount = 0, int billingDay = 1)
    {
        var courseId = await CourseIdAsync();
        return await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().CreateContractAsync(
            new ContractTerms(studentId, courseId, start, months, 50, 100, monthly, discount, billingDay, PaymentMethod.Card), signedOn: start));
    }

    private async Task<Invoice> InvoiceAsync(int studentId, DateOnly date, decimal amount, string what = "Uniform")
    {
        var income = await _app.AccountIdAsync("40-01-03-001");
        return await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().CreateInvoiceAsync(
            studentId, date, date.AddDays(10), what, new[] { new NewInvoiceLine(what, amount, income) }));
    }

    private Task<Payment> PayAsync(int studentId, DateOnly date, decimal amount, IReadOnlyList<Allocation>? allocations = null) =>
        _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().ReceivePaymentAsync(studentId, date, amount, PaymentMethod.Cash, "test", allocations: allocations));

    private Task<Invoice> LoadInvoiceAsync(int id) =>
        _app.DbAsync(db => db.Invoices.Include(i => i.Lines).Include(i => i.Allocations).SingleAsync(i => i.Id == id));

    [Fact]
    public async Task Creating_a_contract_issues_a_sign_up_invoice_with_its_voucher()
    {
        var studentId = await _app.NewStudentAsync();
        var contract = await ContractAsync(studentId, new DateOnly(2026, 1, 15), 12, monthly: 80, discount: 10);

        var invoice = await _app.DbAsync(db => db.Invoices.Include(i => i.Lines).ThenInclude(l => l.CreditAccount)
            .Include(i => i.Voucher!).ThenInclude(v => v.Entries).ThenInclude(e => e.DetailAccount)
            .SingleAsync(i => i.ContractId == contract.Id));

        Assert.StartsWith("INV-2026-", invoice.InvoiceNo);
        Assert.Equal(new DateOnly(2026, 1, 1), invoice.PeriodMonth);
        Assert.Equal(220m, invoice.Total);
        Assert.Equal(new[] { ("40-01-02-001", 50m), ("20-01-01-001", 100m), ("40-01-01-001", 70m) },
            invoice.Lines.OrderBy(l => l.Id).Select(l => (l.CreditAccount!.Code, l.Amount)).ToArray());

        var v = invoice.Voucher!;
        Assert.Equal(VoucherType.FeeInvoice, v.Type);
        Assert.Equal(VoucherStatus.Posted, v.Status);
        Assert.Equal("Invoice " + invoice.InvoiceNo, v.SourceReference);
        Assert.Equal(220m, v.Entries.Single(e => e.DetailAccount!.Code == "10-01-03-001").Debit);
        Assert.Equal(v.TotalDebit, v.TotalCredit);

        var saved = await _app.DbAsync(db => db.StudentContracts.SingleAsync(c => c.Id == contract.Id));
        Assert.Equal(new DateOnly(2026, 1, 1), saved.BilledThrough);
    }

    [Fact]
    public async Task A_contract_starting_next_month_only_bills_the_sign_up_fees()
    {
        var studentId = await _app.NewStudentAsync();
        var courseId = await CourseIdAsync();
        var contract = await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().CreateContractAsync(
            new ContractTerms(studentId, courseId, new DateOnly(2026, 3, 1), 6, 50, 0, 80, 0, 5, PaymentMethod.Cash), signedOn: new DateOnly(2026, 2, 20)));
        var invoice = await _app.DbAsync(db => db.Invoices.Include(i => i.Lines).SingleAsync(i => i.ContractId == contract.Id));
        Assert.Single(invoice.Lines);
        Assert.Equal(50m, invoice.Total);
        Assert.Null(invoice.PeriodMonth);
    }

    [Fact]
    public async Task Monthly_billing_is_idempotent_and_stops_at_the_end_of_the_contract()
    {
        var studentId = await _app.NewStudentAsync();
        var contract = await ContractAsync(studentId, new DateOnly(2026, 1, 10), 3, monthly: 90, billingDay: 5);

        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().RunMonthlyBillingAsync(new DateOnly(2026, 2, 15)));
        var afterFirst = await _app.DbAsync(db => db.Invoices.Where(i => i.ContractId == contract.Id && i.PeriodMonth != null).Select(i => i.PeriodMonth).ToListAsync());
        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().RunMonthlyBillingAsync(new DateOnly(2026, 2, 15)));
        var afterSecond = await _app.DbAsync(db => db.Invoices.Where(i => i.ContractId == contract.Id && i.PeriodMonth != null).Select(i => i.PeriodMonth).ToListAsync());
        Assert.Equal(2, afterFirst.Count);
        Assert.Equal(afterFirst.OrderBy(x => x), afterSecond.OrderBy(x => x));

        // Far past the end: only the third month is added, and the contract is completed.
        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().RunMonthlyBillingAsync(new DateOnly(2026, 9, 1)));
        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().RunMonthlyBillingAsync(new DateOnly(2026, 10, 1)));
        var tuition = await _app.DbAsync(db => db.Invoices.Where(i => i.ContractId == contract.Id && i.PeriodMonth != null)
            .OrderBy(i => i.PeriodMonth).Select(i => new { i.PeriodMonth, i.DueDate, Total = i.Lines.Sum(l => l.Amount) }).ToListAsync());
        Assert.Equal(new DateOnly?[] { new(2026, 1, 1), new(2026, 2, 1), new(2026, 3, 1) }, tuition.Select(t => t.PeriodMonth).ToArray());
        Assert.Equal(new DateOnly(2026, 3, 5), tuition[2].DueDate);
        Assert.Equal(90m, tuition[2].Total);
        var saved = await _app.DbAsync(db => db.StudentContracts.SingleAsync(c => c.Id == contract.Id));
        Assert.Equal(ContractStatus.Completed, saved.Status);
        Assert.Equal(new DateOnly(2026, 3, 1), saved.BilledThrough);
    }

    [Fact]
    public async Task Payments_are_applied_oldest_first_and_overpayments_become_credit()
    {
        var studentId = await _app.NewStudentAsync();
        var older = await InvoiceAsync(studentId, new DateOnly(2026, 1, 5), 100);
        var newer = await InvoiceAsync(studentId, new DateOnly(2026, 2, 5), 100);

        await PayAsync(studentId, new DateOnly(2026, 2, 10), 150);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(older.Id)).Status);
        var partly = await LoadInvoiceAsync(newer.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, partly.Status);
        Assert.Equal(50m, partly.Balance);

        var second = await PayAsync(studentId, new DateOnly(2026, 2, 20), 80);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(newer.Id)).Status);
        var balance = await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().GetStudentBalanceAsync(studentId, new DateOnly(2026, 3, 1)));
        Assert.Equal(0m, balance.Open);
        Assert.Equal(30m, balance.Credit);
        Assert.Equal(-30m, balance.Net);
        Assert.StartsWith("RCT-2026-", second.ReceiptNo);

        // The credit is used automatically by the next invoice.
        var next = await InvoiceAsync(studentId, new DateOnly(2026, 3, 5), 50);
        var reloaded = await LoadInvoiceAsync(next.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, reloaded.Status);
        Assert.Equal(20m, reloaded.Balance);
        balance = await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().GetStudentBalanceAsync(studentId));
        Assert.Equal(0m, balance.Credit);
    }

    [Fact]
    public async Task Explicit_allocations_are_checked()
    {
        var studentId = await _app.NewStudentAsync();
        var a = await InvoiceAsync(studentId, new DateOnly(2026, 1, 5), 40);
        var b = await InvoiceAsync(studentId, new DateOnly(2026, 2, 5), 60);

        await Assert.ThrowsAsync<BillingException>(() => PayAsync(studentId, new DateOnly(2026, 2, 10), 50, new[] { new Allocation(b.Id, 70) }));
        await Assert.ThrowsAsync<BillingException>(() => PayAsync(studentId, new DateOnly(2026, 2, 10), 50, new[] { new Allocation(a.Id, 40), new Allocation(b.Id, 20) }));

        // Paying the newer invoice first when staff say so.
        await PayAsync(studentId, new DateOnly(2026, 2, 10), 60, new[] { new Allocation(b.Id, 60) });
        Assert.Equal(InvoiceStatus.Open, (await LoadInvoiceAsync(a.Id)).Status);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(b.Id)).Status);
    }

    [Fact]
    public async Task Voiding_a_payment_restores_balances_and_reverses_its_voucher()
    {
        var studentId = await _app.NewStudentAsync();
        var invoice = await InvoiceAsync(studentId, new DateOnly(2026, 4, 1), 120);
        var payment = await PayAsync(studentId, new DateOnly(2026, 4, 2), 120);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);

        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().VoidPaymentAsync(payment.Id, "Card declined"));

        var reloaded = await LoadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Open, reloaded.Status);
        Assert.Equal(120m, reloaded.Balance);
        var saved = await _app.DbAsync(db => db.Payments.Include(p => p.Allocations).SingleAsync(p => p.Id == payment.Id));
        Assert.True(saved.IsVoided);
        Assert.Empty(saved.Allocations);

        var (original, reversal) = await _app.DbAsync(async db => (
            await db.Vouchers.Include(v => v.Entries).SingleAsync(v => v.Id == payment.VoucherId),
            await db.Vouchers.Include(v => v.Entries).SingleAsync(v => v.ReversalOfId == payment.VoucherId)));
        Assert.Equal(VoucherStatus.Reversed, original.Status);
        Assert.Contains("Card declined", reversal.Description);
        foreach (var e in original.Entries)
            Assert.Equal(0m, e.Debit - e.Credit + reversal.Entries.Where(r => r.DetailAccountId == e.DetailAccountId).Sum(r => r.Debit - r.Credit));

        var balance = await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().GetStudentBalanceAsync(studentId));
        Assert.Equal(120m, balance.Open);
        Assert.Equal(0m, balance.Credit);
        await Assert.ThrowsAsync<BillingException>(() => _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().VoidPaymentAsync(payment.Id, "again")));
    }

    [Fact]
    public async Task Invoices_with_payments_cannot_be_voided_until_the_payment_is_voided()
    {
        var studentId = await _app.NewStudentAsync();
        var invoice = await InvoiceAsync(studentId, new DateOnly(2026, 5, 1), 30);
        var payment = await PayAsync(studentId, new DateOnly(2026, 5, 1), 10);
        var billing = (IServiceProvider sp) => sp.GetRequiredService<BillingService>();

        var ex = await Assert.ThrowsAsync<BillingException>(() => _app.InPasadenaAsync(sp => billing(sp).VoidInvoiceAsync(invoice.Id, "Wrong size")));
        Assert.Contains("Void those payments first", ex.Message);

        await _app.InPasadenaAsync(sp => billing(sp).VoidPaymentAsync(payment.Id, "Refunded"));
        await _app.InPasadenaAsync(sp => billing(sp).VoidInvoiceAsync(invoice.Id, "Wrong size"));
        var saved = await LoadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Void, saved.Status);
        Assert.Equal(0m, saved.Balance);
        Assert.Equal(VoucherStatus.Reversed, await _app.DbAsync(db => db.Vouchers.Where(v => v.Id == saved.VoucherId).Select(v => v.Status).SingleAsync()));
    }

    [Fact]
    public async Task Late_fees_are_charged_once_per_overdue_tuition_invoice()
    {
        var studentId = await _app.NewStudentAsync();
        var contract = await ContractAsync(studentId, new DateOnly(2026, 1, 1), 1, monthly: 80, billingDay: 1);
        var tuition = await _app.DbAsync(db => db.Invoices.SingleAsync(i => i.ContractId == contract.Id && i.PeriodMonth != null));

        // Still within the 7-day grace period: nothing happens.
        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().ApplyLateFeesAsync(tuition.DueDate.AddDays(7)));
        Assert.False(await _app.DbAsync(db => db.Invoices.AnyAsync(i => i.Description == BillingService.LateFeePrefix + tuition.InvoiceNo)));

        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().ApplyLateFeesAsync(new DateOnly(2026, 3, 1)));
        await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().ApplyLateFeesAsync(new DateOnly(2026, 4, 1)));
        var fees = await _app.DbAsync(db => db.Invoices.Include(i => i.Lines).ThenInclude(l => l.CreditAccount)
            .Where(i => i.Description == BillingService.LateFeePrefix + tuition.InvoiceNo).ToListAsync());
        var fee = Assert.Single(fees);
        Assert.Equal(15m, fee.Total);
        Assert.Equal("40-01-04-001", fee.Lines.Single().CreditAccount!.Code);
        Assert.Equal(studentId, fee.StudentId);
    }

    [Fact]
    public async Task Cancelling_suggests_the_smaller_of_250_or_the_remainder()
    {
        var studentId = await _app.NewStudentAsync();
        var contract = await ContractAsync(studentId, new DateOnly(2026, 1, 1), 12, monthly: 100);
        var billing = (IServiceProvider sp) => sp.GetRequiredService<BillingService>();

        var early = await _app.InPasadenaAsync(sp => billing(sp).QuoteCancellationAsync(contract.Id, new DateOnly(2026, 1, 20)));
        Assert.Equal(11, early.RemainingMonths);
        Assert.Equal(250m, early.SuggestedFee);
        var late = await _app.InPasadenaAsync(sp => billing(sp).QuoteCancellationAsync(contract.Id, new DateOnly(2026, 11, 20)));
        Assert.Equal(1, late.RemainingMonths);
        Assert.Equal(100m, late.SuggestedFee);

        var billedBefore = await _app.DbAsync(db => db.Invoices.CountAsync(i => i.ContractId == contract.Id && i.PeriodMonth != null));
        await _app.InPasadenaAsync(sp => billing(sp).CancelContractAsync(contract.Id, new DateOnly(2026, 11, 20), "Moving away", late.SuggestedFee));
        var saved = await _app.DbAsync(db => db.StudentContracts.SingleAsync(c => c.Id == contract.Id));
        Assert.Equal(ContractStatus.Cancelled, saved.Status);
        Assert.True(await _app.DbAsync(db => db.Invoices.AnyAsync(i => i.ContractId == contract.Id && i.Description.Contains("cancellation fee"))));

        // Cancelled contracts are never billed again.
        await _app.InPasadenaAsync(sp => billing(sp).RunMonthlyBillingAsync(new DateOnly(2026, 12, 31)));
        Assert.Equal(billedBefore, await _app.DbAsync(db => db.Invoices.CountAsync(i => i.ContractId == contract.Id && i.PeriodMonth != null)));
    }

    [Fact]
    public async Task Missing_billing_accounts_give_a_clear_message()
    {
        await _app.InPasadenaAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var settings = await db.AccountingSettings.SingleAsync();
            var original = settings.BankAccountId;
            settings.BankAccountId = null;
            await db.SaveChangesAsync();
            try
            {
                var studentId = await db.Students.Select(s => s.Id).FirstAsync();
                var ex = await Assert.ThrowsAsync<BillingException>(() => sp.GetRequiredService<BillingService>()
                    .ReceivePaymentAsync(studentId, new DateOnly(2026, 6, 1), 10, PaymentMethod.Card, null));
                Assert.Contains("Branch settings", ex.Message);
                Assert.Contains("bank", ex.Message);
            }
            finally
            {
                settings = await db.AccountingSettings.SingleAsync();
                settings.BankAccountId = original;
                await db.SaveChangesAsync();
            }
        });
    }

    [Fact]
    public async Task Front_desk_can_receive_payments_but_not_create_vouchers()
    {
        var studentId = await _app.NewStudentAsync("Desk");
        await InvoiceAsync(studentId, new DateOnly(2026, 6, 1), 75);
        var c = await _app.SignInAsync("frontdesk@bushiban.local");

        var page = await c.GetStringAsync($"/Payments/Create?studentId={studentId}");
        Assert.Contains("Uniform", page);
        var res = await c.PostFormAsync($"/Payments/Create?studentId={studentId}", "/Payments/Create", new()
        {
            ["StudentId"] = studentId.ToString(), ["Date"] = "2026-06-02", ["Amount"] = "75", ["Method"] = ((int)PaymentMethod.Cash).ToString(), ["Reference"] = "",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Payments/Details/", res.Headers.Location!.ToString());
        Assert.Equal(0m, (await _app.InPasadenaAsync(sp => sp.GetRequiredService<BillingService>().GetStudentBalanceAsync(studentId))).Open);

        var vouchers = await c.GetAsync("/Vouchers/Create");
        Assert.True(vouchers.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (vouchers.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", vouchers.Headers.Location!.ToString());
        var settings = await c.GetAsync("/Settings");
        Assert.True(settings.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Staff_can_sign_a_student_up_and_see_the_invoice()
    {
        var courseId = await CourseIdAsync("Adult Martial Arts");
        var studentId = await _app.NewStudentAsync("Signup", courseId, discount: 5);
        var c = await _app.SignInAsync("manager@bushiban.local");

        var form = await c.GetStringAsync($"/Contracts/Create?studentId={studentId}");
        Assert.Contains("value=\"80.00\"", form); // monthly fee from the program's fee structure
        Assert.Contains("value=\"5.00\"", form);  // the student's discount
        var res = await c.PostFormAsync($"/Contracts/Create?studentId={studentId}", "/Contracts/Create", new()
        {
            ["StudentId"] = studentId.ToString(), ["CourseId"] = courseId.ToString(), ["StartDate"] = "2026-02-01", ["TermMonths"] = "12",
            ["AdmissionFee"] = "50", ["SecurityFee"] = "100", ["MonthlyFee"] = "80", ["MonthlyDiscount"] = "0", ["BillingDay"] = "1",
            ["PreferredMethod"] = ((int)PaymentMethod.Card).ToString(),
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var details = await c.GetStringAsync(res.Headers.Location!.ToString());
        Assert.Contains("INV-", details);

        var invoiceId = await _app.DbAsync(db => db.Invoices.Where(i => i.StudentId == studentId).Select(i => i.Id).SingleAsync());
        var invoice = await c.GetStringAsync($"/Invoices/Details/{invoiceId}");
        Assert.Contains("$230.00", invoice);
        Assert.Contains("Pasadena", invoice);

        foreach (var url in new[] { "/Billing", "/Contracts", "/Invoices", "/Payments", "/Settings", $"/Invoices?studentId={studentId}" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);
    }
}
