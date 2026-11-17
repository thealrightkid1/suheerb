using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

/// <summary>The signed-in user's own account. Needs no permission (and works without a branch).</summary>
public class ProfileController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly BranchClock _clock;

    public ProfileController(AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn, BranchClock clock)
    {
        _db = db; _users = users; _signIn = signIn; _clock = clock;
    }

    public class ProfileModel
    {
        public AppUser User { get; init; } = default!;
        public string? HomeBranch { get; init; }
        public List<string> OtherBranches { get; init; } = new();
        public bool IsSystemAdmin { get; init; }
        public string PresetLabel { get; init; } = "";
        public List<(string Group, List<P.Feature> Features)> Permissions { get; init; } = new();
        public int PermissionCount { get; init; }
        public BranchClock Clock { get; init; } = default!;
    }

    public class EditInput
    {
        [Required(ErrorMessage = "Enter your name."), StringLength(120)] public string? DisplayName { get; set; }
        [Required(ErrorMessage = "Enter your email address."), EmailAddress(ErrorMessage = "That email address isn't valid."), StringLength(150)]
        public string? Email { get; set; }
        /// <summary>Only needed when the email (sign-in name) changes.</summary>
        public string? CurrentPassword { get; set; }
    }

    public class PasswordInput
    {
        [Required(ErrorMessage = "Enter your current password.")] public string? CurrentPassword { get; set; }
        [Required(ErrorMessage = "Choose a new password.")] public string? NewPassword { get; set; }
        [Required(ErrorMessage = "Type the new password again."), Compare(nameof(NewPassword), ErrorMessage = "The two new passwords don't match.")]
        public string? ConfirmPassword { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null) return Challenge();
        var claims = (await _db.UserClaims.Where(c => c.UserId == user.Id && c.ClaimType == P.ClaimType).Select(c => c.ClaimValue!).ToListAsync(ct)).ToHashSet();
        var isAdmin = claims.Contains(P.SystemAdmin);
        var groups = P.Groups
            .Select(g => (g.Name, g.Features.Where(f => isAdmin || claims.Contains(f.Value)).ToList()))
            .Where(g => g.Item2.Count > 0).ToList();
        var branchIds = user.Branches.Select(b => b.BranchId).Where(b => b != user.HomeBranchId).ToList();
        return View(new ProfileModel
        {
            User = user,
            HomeBranch = user.HomeBranch?.Name,
            OtherBranches = await _db.Branches.Where(b => branchIds.Contains(b.Id)).OrderBy(b => b.Name).Select(b => b.Name).ToListAsync(ct),
            IsSystemAdmin = isAdmin,
            PresetLabel = UserAdministration.PresetLabel(user.Preset),
            Permissions = groups,
            PermissionCount = groups.Sum(g => g.Item2.Count),
            Clock = _clock,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null) return Challenge();
        return View(new EditInput { DisplayName = user.DisplayName, Email = user.Email });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(EditInput input, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null) return Challenge();
        if (!ModelState.IsValid) return View(input);

        var email = input.Email!.Trim();
        var emailChanged = !string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            if (string.IsNullOrEmpty(input.CurrentPassword) || !await _users.CheckPasswordAsync(user, input.CurrentPassword))
            {
                ModelState.AddModelError(nameof(input.CurrentPassword), "To change your email, enter your current password.");
                return View(input);
            }
            var result = await _users.SetEmailAsync(user, email);
            if (result.Succeeded) result = await _users.SetUserNameAsync(user, email);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(nameof(input.Email), result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? "Someone else already uses that email address." : result.Errors.First().Description);
                return View(input);
            }
            user.EmailConfirmed = true;
        }
        user.DisplayName = input.DisplayName!.Trim();
        await _users.UpdateAsync(user);
        await _signIn.RefreshSignInAsync(user); // new name and email in the cookie
        this.Success(emailChanged ? $"Saved. Sign in with {email} from now on." : "Your details were saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult ChangePassword() => View(new PasswordInput());

    [HttpPost]
    public async Task<IActionResult> ChangePassword(PasswordInput input, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null) return Challenge();
        if (!ModelState.IsValid) return View(new PasswordInput());
        var result = await _users.ChangePasswordAsync(user, input.CurrentPassword!, input.NewPassword!);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code == "PasswordMismatch" ? nameof(input.CurrentPassword) : nameof(input.NewPassword),
                    e.Code == "PasswordMismatch" ? "Your current password isn't right." : e.Description);
            return View(new PasswordInput());
        }
        // Changing the password updates the security stamp; re-issue this browser's cookie so you stay signed in
        // while other devices are signed out.
        await _signIn.RefreshSignInAsync(user);
        this.Success("Your password was changed. Other devices will need the new password.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<AppUser?> CurrentUserAsync(CancellationToken ct)
    {
        var id = _users.GetUserId(User);
        return id is null ? null : await _db.Users.Include(u => u.HomeBranch).Include(u => u.Branches).FirstOrDefaultAsync(u => u.Id == id, ct);
    }
}
