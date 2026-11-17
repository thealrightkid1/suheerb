using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Accounting;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Services;

public class AccountingException : Exception
{
    public AccountingException(string message) : base(message) { }
}

/// <summary>
/// The only place vouchers are created, so the books always balance:
/// debits equal credits, closed fiscal years are locked, and posted vouchers are corrected by reversal, never edited.
/// </summary>
public class AccountingService
{
    private readonly AppDbContext _db;
    private readonly NumberSequenceService _numbers;
    private readonly ICurrentBranch _branch;

    public AccountingService(AppDbContext db, NumberSequenceService numbers, ICurrentBranch branch)
    {
        _db = db; _numbers = numbers; _branch = branch;
    }

    public record Line(int DetailAccountId, decimal Debit, decimal Credit, string? Narration = null);

    public static string Prefix(VoucherType type) => type switch
    {
        VoucherType.Receipt => "RV",
        VoucherType.Payment => "PV",
        VoucherType.Contra => "CV",
        VoucherType.FeeInvoice => "FV",
        _ => "JV",
    };

    /// <summary>Validates and adds a posted voucher (not saved: the caller saves, so it can share a transaction).</summary>
    public async Task<Voucher> CreateAsync(VoucherType type, DateOnly date, string description, IEnumerable<Line> lines,
        string? sourceReference = null, VoucherStatus status = VoucherStatus.Posted, CancellationToken ct = default)
    {
        var branchId = _branch.BranchId ?? throw new AccountingException("Pick a branch first.");
        if (status == VoucherStatus.Reversed) throw new AccountingException("A voucher can only be saved as a draft or posted.");
        if (string.IsNullOrWhiteSpace(description)) throw new AccountingException("Enter a description for the voucher.");
        var entries = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (status == VoucherStatus.Draft) await ValidateDraftAsync(entries, ct);
        else await ValidateAsync(branchId, date, entries, ct);

        var fy = await FiscalYearForAsync(date, ct);
        var voucher = new Voucher
        {
            BranchId = branchId,
            VoucherNo = await _numbers.NextFormattedAsync(branchId, Prefix(type), date.Year, ct),
            Date = date,
            Type = type,
            Description = description.Trim(),
            Status = status,
            FiscalYearId = fy?.Id,
            SourceReference = sourceReference,
            Entries = entries.Select(ToEntry).ToList(),
        };
        _db.Vouchers.Add(voucher);
        return voucher;
    }

    /// <summary>
    /// Replaces the header and lines of a draft voucher (not saved). Drafts may be unbalanced; they are checked in full when posted.
    /// The voucher gets a new number if its type or year changes, so the number always matches its prefix.
    /// </summary>
    public async Task<Voucher> UpdateDraftAsync(int voucherId, VoucherType type, DateOnly date, string description, IEnumerable<Line> lines, CancellationToken ct = default)
    {
        var branchId = _branch.BranchId ?? throw new AccountingException("Pick a branch first.");
        var voucher = await _db.Vouchers.Include(v => v.Entries).FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new AccountingException("Voucher not found.");
        if (voucher.Status != VoucherStatus.Draft) throw new AccountingException("Only draft vouchers can be edited. Reverse a posted voucher instead.");
        if (string.IsNullOrWhiteSpace(description)) throw new AccountingException("Enter a description for the voucher.");
        var entries = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        await ValidateDraftAsync(entries, ct);

        if (type != voucher.Type || date.Year != voucher.Date.Year)
            voucher.VoucherNo = await _numbers.NextFormattedAsync(branchId, Prefix(type), date.Year, ct);
        voucher.Type = type;
        voucher.Date = date;
        voucher.Description = description.Trim();
        voucher.FiscalYearId = (await FiscalYearForAsync(date, ct))?.Id;
        _db.VoucherEntries.RemoveRange(voucher.Entries);
        voucher.Entries = entries.Select(ToEntry).ToList();
        return voucher;
    }

    /// <summary>Checks a draft in full (balanced, open fiscal year) and posts it (not saved).</summary>
    public async Task<Voucher> PostDraftAsync(int voucherId, CancellationToken ct = default)
    {
        var branchId = _branch.BranchId ?? throw new AccountingException("Pick a branch first.");
        var voucher = await _db.Vouchers.Include(v => v.Entries).FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new AccountingException("Voucher not found.");
        if (voucher.Status != VoucherStatus.Draft) throw new AccountingException("This voucher is already posted.");
        await ValidateAsync(branchId, voucher.Date,
            voucher.Entries.Select(e => new Line(e.DetailAccountId, e.Debit, e.Credit, e.Narration)).ToList(), ct);
        voucher.Status = VoucherStatus.Posted;
        voucher.FiscalYearId = (await FiscalYearForAsync(voucher.Date, ct))?.Id;
        return voucher;
    }

    private static VoucherEntry ToEntry(Line l) => new()
    {
        DetailAccountId = l.DetailAccountId,
        Debit = Math.Round(l.Debit, 2),
        Credit = Math.Round(l.Credit, 2),
        Narration = string.IsNullOrWhiteSpace(l.Narration) ? null : l.Narration.Trim(),
    };

    /// <summary>Creates the mirror-image voucher and marks the original as reversed.</summary>
    public async Task<Voucher> ReverseAsync(int voucherId, DateOnly date, string reason, CancellationToken ct = default)
    {
        var original = await _db.Vouchers.Include(v => v.Entries).FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new AccountingException("Voucher not found.");
        if (original.Status != VoucherStatus.Posted) throw new AccountingException("Only posted vouchers can be reversed.");
        if (original.ReversalOfId is not null) throw new AccountingException("A reversal can't itself be reversed. Create a new voucher instead.");

        var reversal = await CreateAsync(original.Type, date, $"Reversal of {original.VoucherNo}: {reason}".Trim(),
            original.Entries.Select(e => new Line(e.DetailAccountId, e.Credit, e.Debit, e.Narration)), original.SourceReference, ct: ct);
        reversal.ReversalOfId = original.Id;
        original.Status = VoucherStatus.Reversed;
        return reversal;
    }

    public async Task ValidateAsync(int branchId, DateOnly date, IReadOnlyCollection<Line> entries, CancellationToken ct = default)
    {
        if (entries.Count < 2) throw new AccountingException("A voucher needs at least two lines.");
        if (entries.Any(l => l.Debit < 0 || l.Credit < 0)) throw new AccountingException("Amounts can't be negative.");
        if (entries.Any(l => l.Debit != 0 && l.Credit != 0)) throw new AccountingException("Each line is either a debit or a credit, not both.");
        var dr = entries.Sum(l => Math.Round(l.Debit, 2));
        var cr = entries.Sum(l => Math.Round(l.Credit, 2));
        if (dr != cr) throw new AccountingException($"Debits ({dr:N2}) and credits ({cr:N2}) must be equal.");
        if (dr == 0) throw new AccountingException("The voucher total can't be zero.");

        var ids = entries.Select(l => l.DetailAccountId).Distinct().ToList();
        var found = await _db.DetailAccounts.Where(a => ids.Contains(a.Id) && a.IsActive).CountAsync(ct);
        if (found != ids.Count) throw new AccountingException("One of the accounts doesn't exist or is inactive.");

        var fy = await FiscalYearForAsync(date, ct);
        if (fy is { IsClosed: true }) throw new AccountingException($"Fiscal year {fy.Name} is closed. Pick a date in an open year.");
    }

    /// <summary>Lighter checks for drafts: lines must be sensible, but debits and credits may not match yet.</summary>
    public async Task ValidateDraftAsync(IReadOnlyCollection<Line> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) throw new AccountingException("Add at least one line with an amount.");
        if (entries.Any(l => l.Debit < 0 || l.Credit < 0)) throw new AccountingException("Amounts can't be negative.");
        if (entries.Any(l => l.Debit != 0 && l.Credit != 0)) throw new AccountingException("Each line is either a debit or a credit, not both.");
        var ids = entries.Select(l => l.DetailAccountId).Distinct().ToList();
        var found = await _db.DetailAccounts.Where(a => ids.Contains(a.Id) && a.IsActive).CountAsync(ct);
        if (found != ids.Count) throw new AccountingException("One of the accounts doesn't exist or is inactive.");
    }

    public Task<FiscalYear?> FiscalYearForAsync(DateOnly date, CancellationToken ct = default) =>
        _db.FiscalYears.Where(f => f.StartDate <= date && f.EndDate >= date).OrderByDescending(f => f.IsActive).FirstOrDefaultAsync(ct);

    /// <summary>Balance per detail account for posted vouchers in a period (reversed vouchers net out via their reversal).</summary>
    public async Task<Dictionary<int, (decimal Debit, decimal Credit)>> TotalsAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var q = _db.VoucherEntries.Where(e => e.Voucher!.Status != VoucherStatus.Draft && !e.Voucher.IsDeleted);
        if (_branch.BranchId is int b && !_branch.FilterDisabled) q = q.Where(e => e.Voucher!.BranchId == b);
        if (from is not null) q = q.Where(e => e.Voucher!.Date >= from);
        if (to is not null) q = q.Where(e => e.Voucher!.Date <= to);
        var rows = await q.GroupBy(e => e.DetailAccountId)
            .Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (r.Dr, r.Cr));
    }

    /// <summary>Assets and expenses grow with debits; liabilities, equity and revenue grow with credits.</summary>
    public static bool IsDebitNature(AccountType t) => t is AccountType.Assets or AccountType.Expenses;
}
