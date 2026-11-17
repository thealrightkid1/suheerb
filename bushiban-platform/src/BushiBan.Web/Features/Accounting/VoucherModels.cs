using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Accounting;

public class VoucherFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public VoucherType? Type { get; set; }
    public VoucherStatus? Status { get; set; }
    public string? Q { get; set; }
    public int? AccountId { get; set; }
    public int Page { get; set; } = 1;
}

public class VoucherListRow
{
    public int Id { get; init; }
    public DateOnly Date { get; init; }
    public string VoucherNo { get; init; } = "";
    public VoucherType Type { get; init; }
    public string Description { get; init; } = "";
    public decimal Amount { get; init; }
    public VoucherStatus Status { get; init; }
    public string? SourceReference { get; init; }
    public bool IsReversal { get; init; }
}

public class VoucherIndexPage
{
    public required VoucherFilter Filter { get; init; }
    public required PagedList<VoucherListRow> Rows { get; init; }
    public required List<SelectListItem> Accounts { get; init; }
}

public class VoucherLineForm
{
    public int? AccountId { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public string? Narration { get; set; }

    public bool IsBlank => AccountId is null && (Debit ?? 0) == 0 && (Credit ?? 0) == 0 && string.IsNullOrWhiteSpace(Narration);
}

public class VoucherForm
{
    public int? Id { get; set; }
    public string? VoucherNo { get; set; }
    public DateOnly Date { get; set; }
    public VoucherType Type { get; set; } = VoucherType.Journal;
    public string? Description { get; set; }
    public List<VoucherLineForm> Lines { get; set; } = new();
    /// <summary>"post" or "draft", from the submit button pressed.</summary>
    public string? Intent { get; set; }
}

public class VoucherFormPage
{
    public required VoucherForm Form { get; init; }
    public required List<SelectListItem> Accounts { get; init; }
    public bool CanPost { get; init; }
}

public record VoucherDetailsLine(string AccountCode, string AccountName, decimal Debit, decimal Credit, string? Narration);

public class VoucherDetailsPage
{
    public int Id { get; init; }
    public string VoucherNo { get; init; } = "";
    public DateOnly Date { get; init; }
    public VoucherType Type { get; init; }
    public string Description { get; init; } = "";
    public VoucherStatus Status { get; init; }
    public string? FiscalYear { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? SourceReference { get; init; }
    public string? SourceUrl { get; set; }
    public int? ReversalOfId { get; init; }
    public string? ReversalOfNo { get; init; }
    public int? ReversedById { get; init; }
    public string? ReversedByNo { get; init; }
    public string BranchName { get; init; } = "";
    public List<VoucherDetailsLine> Lines { get; init; } = new();
    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
    public bool IsFromBilling => SourceReference is not null;
}
