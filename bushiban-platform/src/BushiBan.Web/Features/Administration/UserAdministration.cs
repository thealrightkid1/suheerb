using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

/// <summary>
/// Everything that changes a user account, with the safety rules in one place:
/// <list type="bullet">
/// <item>Only system admins can grant system admin (permission or preset), or change a system admin.</item>
/// <item>Other managers only see and manage users whose home branch is one of their branches, can only assign
///       those branches, and can only grant permissions they hold themselves.</item>
/// <item>Nobody can deactivate themselves or remove their own permission to manage users.</item>
/// <item>The last active system admin can't be demoted or deactivated.</item>
/// </list>
/// Every permission or branch change updates the user's security stamp so it applies within a minute
/// (see SecurityStampValidatorOptions in Program.cs).
/// </summary>
public class UserAdministration
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly ICurrentBranch _branch;
    private readonly IHttpContextAccessor _http;
    private IReadOnlyList<int>? _manageable;

    public UserAdministration(AppDbContext db, UserManager<AppUser> users, ICurrentBranch branch, IHttpContextAccessor http)
    {
        _db = db; _users = users; _branch = branch; _http = http;
    }

    private ClaimsPrincipal Actor => _http.HttpContext?.User ?? new ClaimsPrincipal();
    public string? ActorId => Actor.FindFirstValue(ClaimTypes.NameIdentifier);
    public bool ActorIsAdmin => Actor.IsSystemAdmin();

    /// <summary>Branches the signed-in user may assign: every branch for system admins, otherwise their own.</summary>
    public async Task<IReadOnlyList<int>> ManageableBranchIdsAsync(CancellationToken ct = default) =>
        _manageable ??= ActorIsAdmin
            ? await _db.Branches.Select(b => b.Id).ToListAsync(ct)
            : _branch.AllowedBranchIds;

    /// <summary>Users the signed-in user can see.</summary>
    public async Task<IQueryable<AppUser>> VisibleUsersAsync(CancellationToken ct = default)
    {
        if (ActorIsAdmin) return _db.Users;
        var allowed = await ManageableBranchIdsAsync(ct);
        return _db.Users.Where(u => u.HomeBranchId != null && allowed.Contains(u.HomeBranchId.Value));
    }

    public async Task<AppUser?> FindVisibleAsync(string id, CancellationToken ct = default) =>
        await (await VisibleUsersAsync(ct)).Include(u => u.Branches).FirstOrDefaultAsync(u => u.Id == id, ct);

    /// <summary>Permissions the signed-in user may hand out.</summary>
    public bool CanGrant(string permission) =>
        ActorIsAdmin || (permission != P.SystemAdmin && Actor.HasClaim(P.ClaimType, permission));

    public async Task<HashSet<string>> PermissionsOfAsync(string userId, CancellationToken ct = default) =>
        (await _db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == P.ClaimType).Select(c => c.ClaimValue!).ToListAsync(ct)).ToHashSet();

    public Task<bool> IsSystemAdminAsync(string userId, CancellationToken ct = default) =>
        _db.UserClaims.AnyAsync(c => c.UserId == userId && c.ClaimType == P.ClaimType && c.ClaimValue == P.SystemAdmin, ct);

    private Task<int> ActiveSystemAdminCountAsync(CancellationToken ct) =>
        _db.Users.CountAsync(u => u.IsActive && _db.UserClaims.Any(c => c.UserId == u.Id && c.ClaimType == P.ClaimType && c.ClaimValue == P.SystemAdmin), ct);

    /// <summary>Why the signed-in user can't change this account, or null when they can.</summary>
    public async Task<string?> CannotManageReasonAsync(AppUser target, CancellationToken ct = default)
    {
        if (!Actor.HasPermission(P.Users.Manage)) return "You don't have permission to manage users.";
        if (ActorIsAdmin) return null;
        if (await IsSystemAdminAsync(target.Id, ct)) return "Only a system admin can change a system admin's account.";
        var allowed = await ManageableBranchIdsAsync(ct);
        if (target.HomeBranchId is not int home || !allowed.Contains(home)) return "This person's home branch isn't one of your branches.";
        return null;
    }

    /// <summary>The preset whose permissions exactly match, or "Custom".</summary>
    public static string PresetFor(IReadOnlySet<string> permissions) =>
        P.Presets.FirstOrDefault(p => p.Value.ToHashSet().SetEquals(permissions)).Key ?? "Custom";

    public static string PresetLabel(string? preset) =>
        preset is not null && P.PresetLabels.TryGetValue(preset, out var label) ? label : preset is null or "" ? "None" : "Custom";

    // ----- Create -----

    public async Task<(AppUser? User, List<string> Errors)> CreateAsync(UserFormInput input, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Password)) errors.Add("Choose a password.");
        else if (input.Password != input.ConfirmPassword) errors.Add("The two passwords don't match.");
        var presetPermissions = CheckPreset(input.Preset, errors);
        await CheckBranchesAsync(input, errors, ct);
        if (errors.Count > 0) return (null, errors);

        var email = input.Email!.Trim();
        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayName = input.DisplayName?.Trim(),
            HomeBranchId = input.HomeBranchId, Preset = input.Preset, IsActive = true,
        };
        var created = await _users.CreateAsync(user, input.Password!);
        if (!created.Succeeded) return (null, created.Errors.Select(Describe).ToList());

        await _users.AddClaimsAsync(user, presetPermissions!.Select(p => new Claim(P.ClaimType, p)));
        _db.UserBranches.AddRange(ExtraBranches(input).Select(b => new UserBranch { UserId = user.Id, BranchId = b }));
        await _db.SaveChangesAsync(ct);
        return (user, errors);
    }

    // ----- Edit -----

    public async Task<List<string>> UpdateAsync(AppUser target, UserFormInput input, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (await CannotManageReasonAsync(target, ct) is { } reason) return new() { reason };
        var isSelf = target.Id == ActorId;
        var targetIsAdmin = await IsSystemAdminAsync(target.Id, ct);

        if (!input.IsActive && target.IsActive)
        {
            if (isSelf) errors.Add("You can't deactivate your own account.");
            else if (targetIsAdmin && await ActiveSystemAdminCountAsync(ct) <= 1) errors.Add($"{target.Name} is the last active system admin, so the account can't be deactivated.");
        }

        var presetChanged = !string.IsNullOrEmpty(input.Preset) && input.Preset != target.Preset;
        HashSet<string>? newPermissions = null;
        if (presetChanged)
        {
            var preset = CheckPreset(input.Preset, errors);
            if (preset is not null)
            {
                newPermissions = preset.ToHashSet();
                errors.AddRange(await CheckPermissionChangeAsync(target, targetIsAdmin, newPermissions, ct));
            }
        }
        await CheckBranchesAsync(input, errors, ct);
        if (errors.Count > 0) return errors;

        var email = input.Email!.Trim();
        if (!string.Equals(email, target.Email, StringComparison.OrdinalIgnoreCase))
        {
            var r = await _users.SetEmailAsync(target, email);
            if (r.Succeeded) r = await _users.SetUserNameAsync(target, email);
            if (!r.Succeeded) return r.Errors.Select(Describe).ToList();
            target.EmailConfirmed = true;
        }

        var securityChanged = presetChanged || target.IsActive != input.IsActive || target.HomeBranchId != input.HomeBranchId;
        target.DisplayName = input.DisplayName?.Trim();
        target.HomeBranchId = input.HomeBranchId;
        target.IsActive = input.IsActive;
        if (presetChanged) target.Preset = input.Preset;
        securityChanged |= await SyncExtraBranchesAsync(target, input, ct);
        var update = await _users.UpdateAsync(target);
        if (!update.Succeeded) return update.Errors.Select(Describe).ToList();

        if (newPermissions is not null) await ReplaceClaimsAsync(target, newPermissions, ct);
        if (securityChanged) await _users.UpdateSecurityStampAsync(target);
        return errors;
    }

    // ----- Permissions -----

    public async Task<List<string>> SetPermissionsAsync(AppUser target, IEnumerable<string> requested, CancellationToken ct = default)
    {
        if (await CannotManageReasonAsync(target, ct) is { } reason) return new() { reason };
        var known = P.All.Append(P.SystemAdmin).ToHashSet();
        var current = await PermissionsOfAsync(target.Id, ct);
        var wanted = requested.Where(known.Contains).ToHashSet();

        var errors = new List<string>();
        if (!ActorIsAdmin && wanted.Contains(P.SystemAdmin) && !current.Contains(P.SystemAdmin))
            errors.Add("Only a system admin can make someone a system admin.");

        // Permissions the actor can't grant keep their current state (their checkboxes are read-only).
        var final = current.Where(p => !CanGrant(p)).Concat(wanted.Where(CanGrant)).ToHashSet();
        errors.AddRange(await CheckPermissionChangeAsync(target, current.Contains(P.SystemAdmin), final, ct));
        if (errors.Count > 0) return errors;

        if (!final.SetEquals(current))
        {
            await ReplaceClaimsAsync(target, final, ct);
            target.Preset = PresetFor(final);
            await _users.UpdateAsync(target);
            await _users.UpdateSecurityStampAsync(target);
        }
        return errors;
    }

    /// <summary>Rules for replacing a user's permissions with <paramref name="final"/>.</summary>
    private async Task<List<string>> CheckPermissionChangeAsync(AppUser target, bool targetIsAdmin, IReadOnlySet<string> final, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!ActorIsAdmin && final.Contains(P.SystemAdmin) && !targetIsAdmin)
            errors.Add("Only a system admin can make someone a system admin.");
        if (target.Id == ActorId && !final.Contains(P.Users.Manage) && !final.Contains(P.SystemAdmin))
            errors.Add("You can't remove your own permission to manage users. Ask another admin if you really need to.");
        if (targetIsAdmin && !final.Contains(P.SystemAdmin) && target.IsActive && await ActiveSystemAdminCountAsync(ct) <= 1)
            errors.Add($"{target.Name} is the last active system admin. Make someone else a system admin first.");
        return errors;
    }

    private async Task ReplaceClaimsAsync(AppUser target, IReadOnlySet<string> final, CancellationToken ct)
    {
        var existing = (await _users.GetClaimsAsync(target)).Where(c => c.Type == P.ClaimType).ToList();
        var remove = existing.Where(c => !final.Contains(c.Value)).ToList();
        var add = final.Where(p => existing.All(c => c.Value != p)).Select(p => new Claim(P.ClaimType, p)).ToList();
        if (remove.Count > 0) await _users.RemoveClaimsAsync(target, remove);
        if (add.Count > 0) await _users.AddClaimsAsync(target, add);
    }

    // ----- Passwords and lockout -----

    public async Task<List<string>> ResetPasswordAsync(AppUser target, string password, string confirm, CancellationToken ct = default)
    {
        if (await CannotManageReasonAsync(target, ct) is { } reason) return new() { reason };
        if (string.IsNullOrWhiteSpace(password)) return new() { "Choose a new password." };
        if (password != confirm) return new() { "The two passwords don't match." };
        var token = await _users.GeneratePasswordResetTokenAsync(target);
        var result = await _users.ResetPasswordAsync(target, token, password); // also updates the security stamp
        if (!result.Succeeded) return result.Errors.Select(Describe).ToList();
        await _users.SetLockoutEndDateAsync(target, null);
        await _users.ResetAccessFailedCountAsync(target);
        return new();
    }

    public async Task<List<string>> UnlockAsync(AppUser target, CancellationToken ct = default)
    {
        if (await CannotManageReasonAsync(target, ct) is { } reason) return new() { reason };
        await _users.SetLockoutEndDateAsync(target, null);
        await _users.ResetAccessFailedCountAsync(target);
        return new();
    }

    // ----- Helpers -----

    private string[]? CheckPreset(string? preset, List<string> errors)
    {
        if (string.IsNullOrEmpty(preset) || !P.Presets.TryGetValue(preset, out var permissions))
        {
            errors.Add("Choose a permission preset.");
            return null;
        }
        if (!ActorIsAdmin && permissions.Contains(P.SystemAdmin))
        {
            errors.Add("Only a system admin can create or promote system admins.");
            return null;
        }
        var missing = permissions.Where(p => !CanGrant(p)).ToList();
        if (missing.Count > 0)
        {
            errors.Add($"The {PresetLabel(preset)} preset includes {missing.Count} permission(s) you don't have yourself, so you can't give it. Ask a system admin.");
            return null;
        }
        return permissions.Distinct().ToArray();
    }

    private async Task CheckBranchesAsync(UserFormInput input, List<string> errors, CancellationToken ct)
    {
        var allowed = await ManageableBranchIdsAsync(ct);
        if (input.HomeBranchId is not int home) errors.Add("Choose a home branch.");
        else if (!allowed.Contains(home)) errors.Add("You can only assign branches you manage.");
        if (input.ExtraBranchIds.Any(b => !allowed.Contains(b))) errors.Add("You can only give access to branches you manage.");
    }

    private static IEnumerable<int> ExtraBranches(UserFormInput input) =>
        input.ExtraBranchIds.Where(b => b != input.HomeBranchId).Distinct();

    /// <summary>Applies the extra branches the actor can see; branches outside their reach are left alone. True if anything changed.</summary>
    private async Task<bool> SyncExtraBranchesAsync(AppUser target, UserFormInput input, CancellationToken ct)
    {
        var allowed = await ManageableBranchIdsAsync(ct);
        var wanted = ExtraBranches(input).ToHashSet();
        var existing = await _db.UserBranches.Where(b => b.UserId == target.Id).ToListAsync(ct);
        var remove = existing.Where(b => allowed.Contains(b.BranchId) && (!wanted.Contains(b.BranchId) || b.BranchId == input.HomeBranchId)).ToList();
        var add = wanted.Where(b => existing.All(e => e.BranchId != b)).Select(b => new UserBranch { UserId = target.Id, BranchId = b }).ToList();
        _db.UserBranches.RemoveRange(remove);
        _db.UserBranches.AddRange(add);
        if (remove.Count + add.Count == 0) return false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string Describe(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Someone already uses that email address.",
        "InvalidEmail" or "InvalidUserName" => "That email address isn't valid.",
        _ => e.Description,
    };
}

public class UserFormInput
{
    public string? Id { get; set; }
    [Required(ErrorMessage = "Enter the person's name."), StringLength(120)]
    public string? DisplayName { get; set; }
    [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "That email address isn't valid."), StringLength(150)]
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
    public string? Preset { get; set; }
    public int? HomeBranchId { get; set; }
    public List<int> ExtraBranchIds { get; set; } = new();
    public bool IsActive { get; set; } = true;
}
