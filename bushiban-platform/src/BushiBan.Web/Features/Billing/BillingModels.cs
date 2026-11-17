using BushiBan.Web.Domain;

namespace BushiBan.Web.Features.Billing;

/// <summary>A problem staff can fix (missing setup, wrong amount…). The message is shown as is.</summary>
public class BillingException : Exception
{
    public BillingException(string message) : base(message) { }
}

/// <summary>What staff agree with the student when signing them up.</summary>
public record ContractTerms(
    int StudentId, int CourseId, DateOnly StartDate, int TermMonths,
    decimal AdmissionFee, decimal SecurityFee, decimal MonthlyFee, decimal MonthlyDiscount,
    int BillingDay, PaymentMethod PreferredMethod, string? Notes = null, int? FormSubmissionId = null);

public record NewInvoiceLine(string Description, decimal Amount, int CreditAccountId);

public record Allocation(int InvoiceId, decimal Amount);

public record BillingRunResult(int InvoicesCreated, decimal Total, int ContractsCompleted, IReadOnlyList<string> Problems);

public record LateFeeResult(int FeesApplied, decimal Total, IReadOnlyList<string> Problems);

/// <summary>The cancellation fee from the agreement: $250 or the rest of the agreement, whichever is less.</summary>
public record CancellationQuote(int RemainingMonths, decimal Remainder, decimal SuggestedFee);

public record OpenInvoice(int Id, string InvoiceNo, DateOnly IssueDate, DateOnly DueDate, string Description, decimal Total, decimal Paid)
{
    public decimal Balance => Math.Round(Total - Paid, 2);
}

public record StudentBalance(decimal Open, decimal Overdue, decimal Credit)
{
    /// <summary>What the student owes after using their credit (negative when they're in credit).</summary>
    public decimal Net => Open - Credit;
}

/// <summary>Everything the student profile shows about money (see Views/Billing/_StudentAccount.cshtml).</summary>
public class StudentAccountSummary
{
    public int StudentId { get; init; }
    public string StudentName { get; init; } = "";
    public required StudentBalance Balance { get; init; }
    public int OpenInvoices { get; init; }
    public int? ContractId { get; init; }
    public string? ContractProgram { get; init; }
    public decimal? ContractMonthly { get; init; }
    public DateOnly? ContractStart { get; init; }
    public DateOnly? ContractEnd { get; init; }
    public int? LastPaymentId { get; init; }
    public DateOnly? LastPaymentDate { get; init; }
    public decimal? LastPaymentAmount { get; init; }
    public string? LastPaymentReceipt { get; init; }
}

public record AgingRow(int StudentId, string StudentName, string StudentNo, decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
    public decimal Overdue => Total - Current;
}

public record BillingOverview(decimal BilledThisMonth, decimal CollectedThisMonth, decimal Outstanding, decimal Overdue, decimal CreditOnAccount,
    int ActiveContracts, IReadOnlyList<AgingRow> Aging)
{
    public decimal AgingCurrent => Aging.Sum(a => a.Current);
    public decimal Aging1To30 => Aging.Sum(a => a.Days1To30);
    public decimal Aging31To60 => Aging.Sum(a => a.Days31To60);
    public decimal Aging61To90 => Aging.Sum(a => a.Days61To90);
    public decimal AgingOver90 => Aging.Sum(a => a.Over90);
}
