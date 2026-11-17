using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Billing;

/// <summary>A student's enrollment agreement: program, term and price. Generates monthly invoices.</summary>
public class StudentContract : BranchEntity
{
    public int StudentId { get; set; }
    public People.Student? Student { get; set; }
    public int CourseId { get; set; }
    public Academics.Course? Course { get; set; }
    public DateOnly StartDate { get; set; }
    [Range(1, 120)] public int TermMonths { get; set; } = 12;
    [Range(0, 1_000_000)] public decimal AdmissionFee { get; set; }
    [Range(0, 1_000_000)] public decimal SecurityFee { get; set; }
    [Range(0, 1_000_000)] public decimal MonthlyFee { get; set; }
    [Range(0, 1_000_000)] public decimal MonthlyDiscount { get; set; }
    [Range(0, 1_000_000)] public decimal DownPayment { get; set; }
    /// <summary>Day of month tuition is due (1–28).</summary>
    [Range(1, 28)] public int BillingDay { get; set; } = 1;
    public PaymentMethod PreferredMethod { get; set; } = PaymentMethod.Card;
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public DateOnly? CancelledOn { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
    /// <summary>The signed agreement this contract came from, if any.</summary>
    public int? FormSubmissionId { get; set; }
    /// <summary>The last month (first day) a tuition invoice was generated for.</summary>
    public DateOnly? BilledThrough { get; set; }

    public decimal NetMonthly => Math.Max(0, MonthlyFee - MonthlyDiscount);
    public DateOnly EndDate => StartDate.AddMonths(TermMonths).AddDays(-1);
}

public class Invoice : BranchEntity
{
    [Required, StringLength(30)] public string InvoiceNo { get; set; } = "";
    public int StudentId { get; set; }
    public People.Student? Student { get; set; }
    public int? ContractId { get; set; }
    public StudentContract? Contract { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    /// <summary>For tuition invoices: the month billed (first of month). Prevents double billing.</summary>
    public DateOnly? PeriodMonth { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = "";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;
    public int? VoucherId { get; set; }
    public Accounting.Voucher? Voucher { get; set; }
    public List<InvoiceLine> Lines { get; set; } = new();
    public List<PaymentAllocation> Allocations { get; set; } = new();

    public decimal Total => Lines.Sum(l => l.Amount);
    public decimal Paid => Allocations.Sum(a => a.Amount);
    public decimal Balance => Status == InvoiceStatus.Void ? 0 : Total - Paid;
}

public class InvoiceLine : Entity
{
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    [Required, StringLength(200)] public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>The account credited when this line is posted (tuition income, admission income, student deposits…).</summary>
    public int CreditAccountId { get; set; }
    public Accounting.DetailAccount? CreditAccount { get; set; }
}

public class Payment : BranchEntity
{
    [Required, StringLength(30)] public string ReceiptNo { get; set; } = "";
    public int StudentId { get; set; }
    public People.Student? Student { get; set; }
    public DateOnly Date { get; set; }
    [Range(0.01, 1_000_000)] public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    [StringLength(100)] public string? Reference { get; set; }
    /// <summary>The cash/bank account debited.</summary>
    public int DepositAccountId { get; set; }
    public Accounting.DetailAccount? DepositAccount { get; set; }
    public int? VoucherId { get; set; }
    public Accounting.Voucher? Voucher { get; set; }
    public bool IsVoided { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = new();
}

public class PaymentAllocation : Entity
{
    public int PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Which accounts billing posts to, per branch. Seeded from the standard chart of accounts.</summary>
public class AccountingSettings : BranchEntity
{
    public int? ReceivableAccountId { get; set; }
    public int? TuitionIncomeAccountId { get; set; }
    public int? AdmissionIncomeAccountId { get; set; }
    public int? SecurityDepositAccountId { get; set; }
    public int? LateFeeIncomeAccountId { get; set; }
    public int? CashAccountId { get; set; }
    public int? BankAccountId { get; set; }
    [Range(0, 10_000)] public decimal LateFeeAmount { get; set; } = 15m;
    [Range(0, 60)] public int LateFeeGraceDays { get; set; } = 7;
}
