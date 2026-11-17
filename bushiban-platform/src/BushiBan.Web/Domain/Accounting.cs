using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Accounting;

/// <summary>Level 1 of the chart of accounts, e.g. 10-01 Current Assets.</summary>
public class MainAccount : BranchEntity
{
    [Required, StringLength(20)] public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public AccountType AccountType { get; set; }
    public bool IsActive { get; set; } = true;
    public List<SubAccount> SubAccounts { get; set; } = new();
}

/// <summary>Level 2, e.g. 10-01-01 Cash in Hand.</summary>
public class SubAccount : BranchEntity
{
    [Required, StringLength(20)] public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public int MainAccountId { get; set; }
    public MainAccount? MainAccount { get; set; }
    public bool IsActive { get; set; } = true;
    public List<DetailAccount> DetailAccounts { get; set; } = new();
}

/// <summary>Level 3, the posting level, e.g. 10-01-01-001 Cash.</summary>
public class DetailAccount : BranchEntity
{
    [Required, StringLength(20)] public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public int SubAccountId { get; set; }
    public SubAccount? SubAccount { get; set; }
    public bool IsActive { get; set; } = true;

    public string DisplayName => $"{Code} - {Name}";
}

public class FiscalYear : BranchEntity
{
    [Required, StringLength(60)] public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Closed years accept no new or changed vouchers.</summary>
    public bool IsClosed { get; set; }
}

public class Voucher : BranchEntity
{
    /// <summary>Unique and never reused, e.g. JV-2026-00012.</summary>
    [Required, StringLength(30)] public string VoucherNo { get; set; } = "";
    public DateOnly Date { get; set; }
    public VoucherType Type { get; set; }
    [Required, StringLength(500)] public string Description { get; set; } = "";
    public VoucherStatus Status { get; set; } = VoucherStatus.Posted;
    public int? FiscalYearId { get; set; }
    public FiscalYear? FiscalYear { get; set; }
    /// <summary>For reversal vouchers: the voucher being reversed.</summary>
    public int? ReversalOfId { get; set; }
    public Voucher? ReversalOf { get; set; }
    /// <summary>Set on vouchers created automatically by billing ("Invoice INV-…", "Payment RCT-…").</summary>
    [StringLength(60)] public string? SourceReference { get; set; }
    public List<VoucherEntry> Entries { get; set; } = new();

    public decimal TotalDebit => Entries.Sum(e => e.Debit);
    public decimal TotalCredit => Entries.Sum(e => e.Credit);
}

public class VoucherEntry : Entity
{
    public int VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int DetailAccountId { get; set; }
    public DetailAccount? DetailAccount { get; set; }
    [Range(0, 100_000_000)] public decimal Debit { get; set; }
    [Range(0, 100_000_000)] public decimal Credit { get; set; }
    [StringLength(300)] public string? Narration { get; set; }
}
