using BushiBan.Web.Data;
using BushiBan.Web.Domain.SystemTables;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Services;

/// <summary>
/// Hands out numbers that are unique per branch and never reused, even if the record is later deleted
/// (student numbers, voucher numbers, invoice and receipt numbers).
/// </summary>
public class NumberSequenceService
{
    private readonly AppDbContext _db;
    public NumberSequenceService(AppDbContext db) => _db = db;

    /// <summary>Returns the next value for <paramref name="key"/> in the branch. Atomic across concurrent requests.</summary>
    public async Task<int> NextAsync(int branchId, string key, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var updated = await _db.NumberSequences
                .Where(s => s.BranchId == branchId && s.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextValue, x => x.NextValue + 1), ct);
            if (updated == 1)
            {
                var next = await _db.NumberSequences.AsNoTracking()
                    .Where(s => s.BranchId == branchId && s.Key == key).Select(s => s.NextValue).SingleAsync(ct);
                return next - 1;
            }
            try
            {
                _db.NumberSequences.Add(new NumberSequence { BranchId = branchId, Key = key, NextValue = 2 });
                await _db.SaveChangesAsync(ct);
                return 1;
            }
            catch (DbUpdateException)
            {
                // Another request created the row first: detach and retry the increment.
                foreach (var e in _db.ChangeTracker.Entries<NumberSequence>().ToList()) e.State = EntityState.Detached;
            }
        }
        throw new InvalidOperationException($"Could not allocate a number for {key}.");
    }

    public async Task<string> NextStudentNoAsync(int branchId, string branchCode, CancellationToken ct = default) =>
        $"{branchCode}-{await NextAsync(branchId, "Student", ct):00000}";

    public async Task<string> NextFormattedAsync(int branchId, string prefix, int year, CancellationToken ct = default) =>
        $"{prefix}-{year}-{await NextAsync(branchId, $"{prefix}-{year}", ct):00000}";
}
