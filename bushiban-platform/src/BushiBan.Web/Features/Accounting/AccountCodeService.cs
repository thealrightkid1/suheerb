using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Accounting;

/// <summary>
/// Hands out chart-of-accounts codes. Main accounts start with their type's prefix (Assets 10, Liabilities 20,
/// Equity 30, Revenue 40, Expenses 50) plus a two-digit number ("10-03"); sub accounts add two digits to their
/// main account's code ("10-03-01"); detail accounts add three digits to their sub account's code ("10-03-01-001").
/// </summary>
public class AccountCodeService
{
    private readonly AppDbContext _db;
    public AccountCodeService(AppDbContext db) => _db = db;

    public static string TypePrefix(AccountType type) => type switch
    {
        AccountType.Assets => "10",
        AccountType.Liabilities => "20",
        AccountType.Equity => "30",
        AccountType.Revenue => "40",
        AccountType.Expenses => "50",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public async Task<string> NextMainCodeAsync(AccountType type, CancellationToken ct = default)
    {
        var prefix = TypePrefix(type);
        var codes = await _db.MainAccounts.Where(a => a.Code.StartsWith(prefix + "-")).Select(a => a.Code).ToListAsync(ct);
        return Next(prefix, codes, digits: 2, "main accounts of this type");
    }

    public async Task<string> NextSubCodeAsync(string mainCode, CancellationToken ct = default)
    {
        var codes = await _db.SubAccounts.Where(a => a.Code.StartsWith(mainCode + "-")).Select(a => a.Code).ToListAsync(ct);
        return Next(mainCode, codes, digits: 2, "sub accounts under this main account");
    }

    public async Task<string> NextDetailCodeAsync(string subCode, CancellationToken ct = default)
    {
        var codes = await _db.DetailAccounts.Where(a => a.Code.StartsWith(subCode + "-")).Select(a => a.Code).ToListAsync(ct);
        return Next(subCode, codes, digits: 3, "detail accounts under this sub account");
    }

    /// <summary>Highest number already used directly under <paramref name="parent"/>, plus one.</summary>
    private static string Next(string parent, IEnumerable<string> codes, int digits, string what)
    {
        var used = codes
            .Select(c => c[(parent.Length + 1)..])
            .Where(rest => rest.Length == digits && rest.All(char.IsDigit))
            .Select(int.Parse)
            .DefaultIfEmpty(0)
            .Max();
        var next = used + 1;
        if (next >= (int)Math.Pow(10, digits))
            throw new AccountingException($"There's no room for more {what}: the code would need more than {digits} digits.");
        return $"{parent}-{next.ToString(new string('0', digits))}";
    }
}
