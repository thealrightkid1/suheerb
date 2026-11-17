using BushiBan.Web.Data;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

[HasPermission(P.Users.View)]
public class UsersController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserAdministration _admin;
    private readonly SignInManager<AppUser> _signIn;
    private readonly BranchClock _clock;
    private readonly ICurrentBranch _branch;

    public UsersController(AppDbContext db, UserAdministration admin, SignInManager<AppUser> signIn, BranchClock clock, ICurrentBranch branch)
    {
        _db = db; _admin = admin; _signIn = signIn; _clock = clock; _branch = branch;
    }

    // ----- List -----

    [HttpGet]
    public async Task<IActionResult> Index(string? q, int? branchId, string? status, string? preset, int page = 1, CancellationToken ct = default)
    {
        var query = await _admin.VisibleUsersAsync(ct);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(u => (u.DisplayName != null && u.DisplayName.Contains(t)) || (u.Email != null && u.Email.Contains(t)));
        }
        if (branchId is int b) query = query.Where(u => u.HomeBranchId == b || u.Branches.Any(x => x.BranchId == b));
        if (!string.IsNullOrEmpty(preset))
        {
            var known = P.PresetLabels.Keys.ToArray();
            query = preset == "Custom" ? query.Where(u => u.Preset == null || !known.Contains(u.Preset)) : query.Where(u => u.Preset == preset);
        }
        switch (status)
        {
            case "active": query = query.Where(u => u.IsActive); break;
            case "inactive": query = query.Where(u => !u.IsActive); break;
            case "locked":
                // DateTimeOffset comparisons don't translate on SQLite, so find locked accounts in memory (there are few).
                var now = DateTimeOffset.UtcNow;
                var locked = (await _db.Users.Where(u => u.LockoutEnd != null).Select(u => new { u.Id, u.LockoutEnd }).ToListAsync(ct))
                    .Where(u => u.LockoutEnd > now).Select(u => u.Id).ToList();
                query = query.Where(u => locked.Contains(u.Id));
                break;
        }

        var paged = await query.OrderBy(u => u.DisplayName ?? u.Email)
            .Select(u => new UserRow
            {
                Id = u.Id, Name = u.DisplayName ?? u.Email ?? "", Email = u.Email ?? "", Preset = u.Preset,
                HomeBranch = u.HomeBranch != null ? u.HomeBranch.Name : null,
                OtherBranches = u.Branches.Where(x => x.BranchId != u.HomeBranchId).Select(x => x.Branch!.Name).ToList(),
                LastLoginAt = u.LastLoginAt, IsActive = u.IsActive, LockoutEnd = u.LockoutEnd,
            })
            .ToPagedAsync(page, 25, ct);

        // Permission counts and admin flags for the page in one query.
        var ids = paged.Items.Select(r => r.Id).ToList();
        var claims = await _db.UserClaims.Where(c => ids.Contains(c.UserId) && c.ClaimType == P.ClaimType)
            .GroupBy(c => c.UserId)
            .Select(g => new { g.Key, Count = g.Count(c => c.ClaimValue != P.SystemAdmin), Admin = g.Any(c => c.ClaimValue == P.SystemAdmin) })
            .ToDictionaryAsync(x => x.Key, ct);
        var canManage = User.HasPermission(P.Users.Manage);
        foreach (var r in paged.Items)
        {
            if (claims.TryGetValue(r.Id, out var c)) { r.PermissionCount = c.Count; r.IsSystemAdmin = c.Admin; }
            r.CanManage = canManage && (_admin.ActorIsAdmin || !r.IsSystemAdmin);
            r.IsSelf = r.Id == _admin.ActorId;
        }

        return View(new UserListModel
        {
            Paged = paged, Q = q, BranchId = branchId, Status = status, Preset = preset,
            Branches = await BranchOptionsAsync(branchId, ct), CanManage = canManage, Clock = _clock,
        });
    }

    // ----- Create / edit -----

    [HttpGet, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Create(CancellationToken ct) =>
        View("Form", await FormModelAsync(new UserFormInput { Preset = "Receptionist", HomeBranchId = _branch.BranchId }, isNew: true, ct: ct));

    [HttpPost, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Create(UserFormInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View("Form", await FormModelAsync(input, true, ct: ct));
        var (user, errors) = await _admin.CreateAsync(input, ct);
        if (user is null)
        {
            errors.ForEach(e => ModelState.AddModelError("", e));
            return View("Form", await FormModelAsync(input, true, ct: ct));
        }
        this.Success($"{user.Name} can now sign in with {user.Email}.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Edit(string id, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        if (await _admin.CannotManageReasonAsync(user, ct) is { } reason) { this.Error(reason); return RedirectToAction(nameof(Index)); }
        var input = new UserFormInput
        {
            Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, Preset = user.Preset, HomeBranchId = user.HomeBranchId,
            ExtraBranchIds = user.Branches.Select(b => b.BranchId).Where(b => b != user.HomeBranchId).ToList(), IsActive = user.IsActive,
        };
        return View("Form", await FormModelAsync(input, false, user, ct));
    }

    [HttpPost, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Edit(string id, UserFormInput input, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        input.Id = id;
        if (!ModelState.IsValid) return View("Form", await FormModelAsync(input, false, user, ct));
        var errors = await _admin.UpdateAsync(user, input, ct);
        if (errors.Count > 0)
        {
            errors.ForEach(e => ModelState.AddModelError("", e));
            return View("Form", await FormModelAsync(input, false, user, ct));
        }
        await RefreshIfSelfAsync(user);
        this.Success($"{user.Name}'s account was saved.");
        return RedirectToAction(nameof(Index));
    }

    // ----- Permissions -----

    [HttpGet, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Permissions(string id, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        if (await _admin.CannotManageReasonAsync(user, ct) is { } reason) { this.Error(reason); return RedirectToAction(nameof(Index)); }
        return View(await PermissionsModelAsync(user, null, ct));
    }

    [HttpPost, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Permissions(string id, string[]? permissions, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        var errors = await _admin.SetPermissionsAsync(user, permissions ?? Array.Empty<string>(), ct);
        if (errors.Count > 0)
        {
            errors.ForEach(e => ModelState.AddModelError("", e));
            return View(await PermissionsModelAsync(user, (permissions ?? Array.Empty<string>()).ToHashSet(), ct));
        }
        await RefreshIfSelfAsync(user);
        this.Success($"{user.Name}'s permissions were saved. They apply within a minute; {(user.Id == _admin.ActorId ? "your session was refreshed" : "if they're signed in, they'll be asked to sign in again")}.");
        return RedirectToAction(nameof(Index));
    }

    // ----- Password and lockout -----

    [HttpGet, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        if (user.Id == _admin.ActorId) return RedirectToAction("ChangePassword", "Profile");
        if (await _admin.CannotManageReasonAsync(user, ct) is { } reason) { this.Error(reason); return RedirectToAction(nameof(Index)); }
        return View(new ResetPasswordModel { Id = user.Id, Name = user.Name, Email = user.Email ?? "" });
    }

    [HttpPost, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordModel input, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        var errors = await _admin.ResetPasswordAsync(user, input.NewPassword ?? "", input.ConfirmPassword ?? "", ct);
        if (errors.Count > 0)
        {
            errors.ForEach(e => ModelState.AddModelError("", e));
            return View(new ResetPasswordModel { Id = user.Id, Name = user.Name, Email = user.Email ?? "" });
        }
        this.Success($"{user.Name}'s password was changed and the account unlocked. Give them the new password; they're signed out everywhere else.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Users.Manage)]
    public async Task<IActionResult> Unlock(string id, CancellationToken ct)
    {
        var user = await _admin.FindVisibleAsync(id, ct);
        if (user is null) return NotFound();
        var errors = await _admin.UnlockAsync(user, ct);
        if (errors.Count > 0) this.Error(errors[0]); else this.Success($"{user.Name} can sign in again.");
        return RedirectToAction(nameof(Index));
    }

    // ----- Helpers -----

    /// <summary>Changing your own account updates your security stamp; re-issue the cookie so you stay signed in.</summary>
    private async Task RefreshIfSelfAsync(AppUser user)
    {
        if (user.Id == _admin.ActorId) await _signIn.RefreshSignInAsync(user);
    }

    private async Task<List<SelectListItem>> BranchOptionsAsync(int? selected, CancellationToken ct)
    {
        var allowed = await _admin.ManageableBranchIdsAsync(ct);
        return await _db.Branches.Where(b => allowed.Contains(b.Id)).OrderBy(b => b.Name)
            .Select(b => new SelectListItem(b.Name + (b.IsActive ? "" : " (inactive)"), b.Id.ToString(), b.Id == selected)).ToListAsync(ct);
    }

    private async Task<UserFormModel> FormModelAsync(UserFormInput input, bool isNew, AppUser? user = null, CancellationToken ct = default)
    {
        var presets = P.PresetLabels
            .Where(p => _admin.ActorIsAdmin || (!P.Presets[p.Key].Contains(P.SystemAdmin) && P.Presets[p.Key].All(_admin.CanGrant)))
            .Select(p => new SelectListItem(p.Value, p.Key, p.Key == input.Preset)).ToList();
        var hiddenBranches = new List<string>();
        if (user is not null && !_admin.ActorIsAdmin)
        {
            // Branches the user has that this manager can't see stay as they are; say so.
            var allowed = await _admin.ManageableBranchIdsAsync(ct);
            var outside = user.Branches.Select(b => b.BranchId).Where(b => !allowed.Contains(b)).ToList();
            hiddenBranches = await _db.Branches.Where(b => outside.Contains(b.Id)).Select(b => b.Name).ToListAsync(ct);
        }
        return new UserFormModel
        {
            Input = input, IsNew = isNew, Presets = presets, Branches = await BranchOptionsAsync(null, ct),
            CurrentPresetLabel = user is null ? null : UserAdministration.PresetLabel(user.Preset),
            PermissionCount = user is null ? 0 : (await _admin.PermissionsOfAsync(user.Id, ct)).Count,
            IsSelf = user?.Id == _admin.ActorId, OtherBranchesNotShown = hiddenBranches,
            IsLocked = user?.LockoutEnd > DateTimeOffset.UtcNow,
        };
    }

    private async Task<PermissionsModel> PermissionsModelAsync(AppUser user, HashSet<string>? posted, CancellationToken ct)
    {
        var current = await _admin.PermissionsOfAsync(user.Id, ct);
        // After a failed save, show what was ticked (read-only boxes keep their saved state).
        var shown = posted is null ? current : current.Where(p => !_admin.CanGrant(p)).Concat(posted.Where(_admin.CanGrant)).ToHashSet();
        return new PermissionsModel
        {
            Id = user.Id, Name = user.Name, Email = user.Email ?? "", PresetLabel = UserAdministration.PresetLabel(user.Preset),
            Checked = shown, Grantable = P.All.Append(P.SystemAdmin).Where(_admin.CanGrant).ToHashSet(),
            ShowSystemAdmin = _admin.ActorIsAdmin || current.Contains(P.SystemAdmin), IsSelf = user.Id == _admin.ActorId,
            Presets = P.PresetLabels.Where(p => _admin.ActorIsAdmin || p.Key != "SystemAdmin")
                .ToDictionary(p => p.Key, p => (p.Value, P.Presets[p.Key])),
        };
    }
}

public class UserRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string? Preset { get; init; }
    public string? HomeBranch { get; init; }
    public List<string> OtherBranches { get; init; } = new();
    public DateTime? LastLoginAt { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset? LockoutEnd { get; init; }
    public int PermissionCount { get; set; }
    public bool IsSystemAdmin { get; set; }
    public bool CanManage { get; set; }
    public bool IsSelf { get; set; }
    public bool IsLocked => LockoutEnd > DateTimeOffset.UtcNow;
}

public class UserListModel
{
    public PagedList<UserRow> Paged { get; init; } = default!;
    public string? Q { get; init; }
    public int? BranchId { get; init; }
    public string? Status { get; init; }
    public string? Preset { get; init; }
    public List<SelectListItem> Branches { get; init; } = new();
    public bool CanManage { get; init; }
    public BranchClock Clock { get; init; } = default!;
}

public class UserFormModel
{
    public UserFormInput Input { get; init; } = new();
    public bool IsNew { get; init; }
    public List<SelectListItem> Presets { get; init; } = new();
    public List<SelectListItem> Branches { get; init; } = new();
    public string? CurrentPresetLabel { get; init; }
    public int PermissionCount { get; init; }
    public bool IsSelf { get; init; }
    public bool IsLocked { get; init; }
    public List<string> OtherBranchesNotShown { get; init; } = new();
}

public class PermissionsModel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string PresetLabel { get; init; } = "";
    public HashSet<string> Checked { get; init; } = new();
    public HashSet<string> Grantable { get; init; } = new();
    public bool ShowSystemAdmin { get; init; }
    public bool IsSelf { get; init; }
    public Dictionary<string, (string Label, string[] Permissions)> Presets { get; init; } = new();
}

public class ResetPasswordModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
}
