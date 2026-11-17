using System.Security.Claims;
using BushiBan.Web.Security;

namespace BushiBan.Web.Services;

/// <summary>
/// The branch the current request works in. All branch data (students, vouchers…) is filtered to it.
/// Users switch branches from the top bar; the choice is kept in a cookie and checked against
/// the branches they're allowed to use on every request.
/// </summary>
public interface ICurrentBranch
{
    int? BranchId { get; }
    /// <summary>System admins can turn the filter off to see every branch (e.g. consolidated reports).</summary>
    bool FilterDisabled { get; }
    IReadOnlyList<int> AllowedBranchIds { get; }
    bool IsSystemAdmin { get; }
    string? UserName { get; }
    /// <summary>Used by background jobs, seeding and kiosks.</summary>
    IDisposable Override(int? branchId, bool disableFilter = false);
}

public sealed class CurrentBranch : ICurrentBranch
{
    public const string CookieName = "bb_branch";
    public const string HomeBranchClaim = "home_branch";
    public const string BranchesClaim = "branches";

    private readonly IHttpContextAccessor _http;
    private (int? Id, bool Disabled)? _override;
    private (int? Id, IReadOnlyList<int> Allowed)? _resolved;

    public CurrentBranch(IHttpContextAccessor http) => _http = http;

    private ClaimsPrincipal? User => _http.HttpContext?.User;
    public bool IsSystemAdmin => User?.IsSystemAdmin() == true;
    public string? UserName => User?.Identity?.IsAuthenticated == true ? User.Identity!.Name : null;

    public bool FilterDisabled => _override?.Disabled ?? false;

    public int? BranchId => _override.HasValue ? _override.Value.Id : Resolve().Id;
    public IReadOnlyList<int> AllowedBranchIds => Resolve().Allowed;

    private (int? Id, IReadOnlyList<int> Allowed) Resolve()
    {
        if (_resolved.HasValue) return _resolved.Value;
        var user = User;
        if (user?.Identity?.IsAuthenticated != true) return (_resolved = (null, Array.Empty<int>())).Value;

        var allowed = (user.FindFirst(BranchesClaim)?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var i) ? i : 0).Where(i => i > 0).Distinct().ToList();
        int? home = int.TryParse(user.FindFirst(HomeBranchClaim)?.Value, out var h) ? h : null;
        if (home is int hb && !allowed.Contains(hb)) allowed.Insert(0, hb);

        int? selected = null;
        if (int.TryParse(_http.HttpContext!.Request.Cookies[CookieName], out var c) && (IsSystemAdmin || allowed.Contains(c)))
            selected = c;
        selected ??= home ?? allowed.FirstOrDefault();
        if (selected == 0) selected = null;
        return (_resolved = (selected, allowed)).Value;
    }

    public IDisposable Override(int? branchId, bool disableFilter = false)
    {
        var previous = _override;
        _override = (branchId, disableFilter);
        return new Restore(() => _override = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }
}
