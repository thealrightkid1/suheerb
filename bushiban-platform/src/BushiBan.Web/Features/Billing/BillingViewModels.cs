using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Billing;

// ───────────── Shared ─────────────

/// <summary>Branch letterhead for printed invoices and receipts.</summary>
public record Letterhead(string School, string Branch, string? Address, string? CityLine, string? Phone, string? Email);

public record StudentHeader(int Id, string Name, string StudentNo, string? Address, string? CityLine, string? Email, string? Phone);

// ───────────── Contracts ─────────────

public class ContractFilter
{
    public ContractStatus? Status { get; set; }
    public int? CourseId { get; set; }
    public int? StudentId { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
}

public class ContractRow
{
    public int Id { get; init; }
    public int StudentId { get; init; }
    public string StudentName { get; init; } = "";
    public string StudentNo { get; init; } = "";
    public string Program { get; init; } = "";
    public DateOnly StartDate { get; init; }
    public int TermMonths { get; init; }
    public decimal MonthlyFee { get; init; }
    public decimal MonthlyDiscount { get; init; }
    public DateOnly? BilledThrough { get; init; }
    public ContractStatus Status { get; init; }
    public decimal NetMonthly => Math.Max(0, MonthlyFee - MonthlyDiscount);
    public DateOnly EndDate => StartDate.AddMonths(TermMonths).AddDays(-1);
}

public class ContractIndexPage
{
    public required ContractFilter Filter { get; init; }
    public required PagedList<ContractRow> Rows { get; init; }
    public required List<SelectListItem> Programs { get; init; }
}

public class ContractForm
{
    public int? StudentId { get; set; }
    public int? CourseId { get; set; }
    public DateOnly StartDate { get; set; }
    public int TermMonths { get; set; } = 12;
    public decimal AdmissionFee { get; set; }
    public decimal SecurityFee { get; set; }
    public decimal MonthlyFee { get; set; }
    public decimal MonthlyDiscount { get; set; }
    public int BillingDay { get; set; } = 1;
    public PaymentMethod PreferredMethod { get; set; } = PaymentMethod.Card;
    public string? Notes { get; set; }
}

/// <summary>Standard prices per program, so the form can fill them in when the program changes.</summary>
public record ProgramPrice(int Id, string Name, decimal AdmissionFee, decimal SecurityFee, decimal MonthlyFee, int MinimumTermMonths, bool HasFeeStructure);

public class ContractFormPage
{
    public required ContractForm Form { get; init; }
    public required List<SelectListItem> Students { get; init; }
    public required List<ProgramPrice> Programs { get; init; }
    public DateOnly Today { get; init; }
    public StudentAccountSummary? Account { get; init; }
}

public record ScheduleMonth(DateOnly Month, DateOnly DueDate, int? InvoiceId, string? InvoiceNo, InvoiceStatus? Status, decimal? Balance);

public record ContractInvoice(int Id, string InvoiceNo, DateOnly IssueDate, DateOnly DueDate, string Description, decimal Total, decimal Paid, InvoiceStatus Status)
{
    public decimal Balance => Status == InvoiceStatus.Void ? 0 : Total - Paid;
}

public class ContractDetailsPage
{
    public int Id { get; init; }
    public required StudentHeader Student { get; init; }
    public string Program { get; init; } = "";
    public DateOnly StartDate { get; init; }
    public int TermMonths { get; init; }
    public decimal AdmissionFee { get; init; }
    public decimal SecurityFee { get; init; }
    public decimal MonthlyFee { get; init; }
    public decimal MonthlyDiscount { get; init; }
    public int BillingDay { get; init; }
    public PaymentMethod PreferredMethod { get; init; }
    public ContractStatus Status { get; init; }
    public DateOnly? CancelledOn { get; init; }
    public DateOnly? BilledThrough { get; init; }
    public string? Notes { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ScheduleMonth> Schedule { get; init; } = new();
    public List<ContractInvoice> Invoices { get; init; } = new();
    public CancellationQuote? Quote { get; init; }
    public DateOnly Today { get; init; }
    public decimal NetMonthly => Math.Max(0, MonthlyFee - MonthlyDiscount);
    public DateOnly EndDate => StartDate.AddMonths(TermMonths).AddDays(-1);
    public bool CanCancel => Status is ContractStatus.Active or ContractStatus.OnHold;
}

// ───────────── Invoices ─────────────

public class InvoiceFilter
{
    public InvoiceStatus? Status { get; set; }
    /// <summary>Open or partly paid.</summary>
    public bool Unpaid { get; set; }
    public bool Overdue { get; set; }
    public int? StudentId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
}

public class InvoiceRow
{
    public int Id { get; init; }
    public string InvoiceNo { get; init; } = "";
    public DateOnly IssueDate { get; init; }
    public DateOnly DueDate { get; init; }
    public int StudentId { get; init; }
    public string StudentName { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Total { get; init; }
    public decimal Paid { get; init; }
    public InvoiceStatus Status { get; init; }
    public decimal Balance => Status == InvoiceStatus.Void ? 0 : Math.Round(Total - Paid, 2);
}

public class InvoiceIndexPage
{
    public required InvoiceFilter Filter { get; init; }
    public required PagedList<InvoiceRow> Rows { get; init; }
    public required List<SelectListItem> Students { get; init; }
    public DateOnly Today { get; init; }
    public decimal FilteredBalance { get; init; }
}

public class InvoiceLineForm
{
    public string? Description { get; set; }
    public decimal? Amount { get; set; }
    public int? AccountId { get; set; }
    public bool IsBlank => string.IsNullOrWhiteSpace(Description) && (Amount ?? 0) == 0 && AccountId is null;
}

public class InvoiceForm
{
    public int? StudentId { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string? Description { get; set; }
    public List<InvoiceLineForm> Lines { get; set; } = new();
}

public class InvoiceFormPage
{
    public required InvoiceForm Form { get; init; }
    public required List<SelectListItem> Students { get; init; }
    public required List<SelectListItem> Accounts { get; init; }
    public int? DefaultAccountId { get; init; }
}

public record InvoiceDetailsLine(string Description, string AccountCode, string AccountName, decimal Amount);
public record InvoicePaymentLine(int PaymentId, string ReceiptNo, DateOnly Date, PaymentMethod Method, decimal Amount);

public class InvoiceDetailsPage
{
    public int Id { get; init; }
    public string InvoiceNo { get; init; } = "";
    public DateOnly IssueDate { get; init; }
    public DateOnly DueDate { get; init; }
    public DateOnly? PeriodMonth { get; init; }
    public string Description { get; init; } = "";
    public InvoiceStatus Status { get; init; }
    public int? ContractId { get; init; }
    public string? ContractProgram { get; init; }
    public int? VoucherId { get; init; }
    public string? VoucherNo { get; init; }
    public string? VoidReason { get; set; }
    public required StudentHeader Student { get; init; }
    public required Letterhead Branch { get; init; }
    public List<InvoiceDetailsLine> Lines { get; init; } = new();
    public List<InvoicePaymentLine> Payments { get; init; } = new();
    public DateOnly Today { get; init; }
    public decimal Total => Lines.Sum(l => l.Amount);
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Status == InvoiceStatus.Void ? 0 : Total - Paid;
    public bool IsOverdue => Balance > 0 && DueDate < Today;
}

// ───────────── Payments ─────────────

public class PaymentFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public PaymentMethod? Method { get; set; }
    public int? StudentId { get; set; }
    public bool ShowVoided { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
}

public class PaymentRow
{
    public int Id { get; init; }
    public string ReceiptNo { get; init; } = "";
    public DateOnly Date { get; init; }
    public int StudentId { get; init; }
    public string StudentName { get; init; } = "";
    public PaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public decimal Amount { get; init; }
    public decimal Applied { get; init; }
    public bool IsVoided { get; init; }
    public decimal Unapplied => IsVoided ? 0 : Math.Round(Amount - Applied, 2);
}

public class PaymentIndexPage
{
    public required PaymentFilter Filter { get; init; }
    public required PagedList<PaymentRow> Rows { get; init; }
    public required List<SelectListItem> Students { get; init; }
    public decimal FilteredTotal { get; init; }
}

public class AllocationForm
{
    public int InvoiceId { get; set; }
    public decimal? Amount { get; set; }
}

public class PaymentForm
{
    public int? StudentId { get; set; }
    public DateOnly Date { get; set; }
    public decimal? Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    public string? Reference { get; set; }
    public int? DepositAccountId { get; set; }
    public List<AllocationForm> Allocations { get; set; } = new();
}

public class PaymentFormPage
{
    public required PaymentForm Form { get; init; }
    public required List<SelectListItem> Students { get; init; }
    public required List<SelectListItem> DepositAccounts { get; init; }
    public List<OpenInvoice> OpenInvoices { get; init; } = new();
    public StudentBalance? Balance { get; init; }
    public DateOnly Today { get; init; }
}

public record ReceiptLine(int InvoiceId, string InvoiceNo, string Description, decimal Amount);

public class PaymentDetailsPage
{
    public int Id { get; init; }
    public string ReceiptNo { get; init; } = "";
    public DateOnly Date { get; init; }
    public decimal Amount { get; init; }
    public PaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public string DepositAccount { get; init; } = "";
    public bool IsVoided { get; init; }
    public string? VoidReason { get; set; }
    public int? VoucherId { get; init; }
    public string? VoucherNo { get; init; }
    public string? CreatedBy { get; init; }
    public required StudentHeader Student { get; init; }
    public required Letterhead Branch { get; init; }
    public List<ReceiptLine> Lines { get; init; } = new();
    public decimal Applied => Lines.Sum(l => l.Amount);
    public decimal Unapplied => IsVoided ? 0 : Amount - Applied;
    public decimal? StudentBalanceAfter { get; set; }
}

// ───────────── Settings ─────────────

public class SettingsForm
{
    public int? ReceivableAccountId { get; set; }
    public int? TuitionIncomeAccountId { get; set; }
    public int? AdmissionIncomeAccountId { get; set; }
    public int? SecurityDepositAccountId { get; set; }
    public int? LateFeeIncomeAccountId { get; set; }
    public int? CashAccountId { get; set; }
    public int? BankAccountId { get; set; }
    public decimal LateFeeAmount { get; set; }
    public int LateFeeGraceDays { get; set; }
}

public class SettingsPage
{
    public required SettingsForm Form { get; init; }
    public required List<SelectListItem> Accounts { get; init; }
    public required string BranchName { get; init; }
    public bool EmailConfigured { get; init; }
    public bool SmsConfigured { get; init; }
    public string? EmailFrom { get; init; }
    public string? SmsProvider { get; init; }
}
