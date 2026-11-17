using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BushiBan.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly ICurrentBranch _branch;

    public AccountController(SignInManager<AppUser> signIn, UserManager<AppUser> users, ICurrentBranch branch)
    {
        _signIn = signIn; _users = users; _branch = branch;
    }

    public class LoginModel
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(SafeReturn(returnUrl));
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginModel());
    }

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(model);
        var user = await _users.FindByEmailAsync(model.Email.Trim());
        if (user is null || !user.IsActive)
        {
            ModelState.AddModelError("", "Wrong email or password.");
            return View(model);
        }
        var result = await _signIn.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Too many failed attempts. The account is locked for 15 minutes.");
            return View(model);
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "Wrong email or password.");
            return View(model);
        }
        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user);
        Response.Cookies.Delete(CurrentBranch.CookieName);

        // Kiosk accounts go straight to their kiosk.
        var principal = await _signIn.CreateUserPrincipalAsync(user);
        if (returnUrl is null && !principal.HasPermission(Permissions.Students.View, Permissions.Leads.View, Permissions.Vouchers.View, Permissions.Billing.View))
        {
            if (principal.HasPermission(Permissions.Kiosk.Attendance)) return RedirectToAction("Attendance", "Kiosk");
            if (principal.HasPermission(Permissions.Kiosk.Registration)) return RedirectToAction("Register", "Kiosk");
        }
        return LocalRedirect(SafeReturn(returnUrl));
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        Response.Cookies.Delete(CurrentBranch.CookieName);
        return RedirectToAction(nameof(Login));
    }

    [HttpPost]
    public IActionResult SwitchBranch(int branchId, string? returnUrl)
    {
        if (_branch.IsSystemAdmin || _branch.AllowedBranchIds.Contains(branchId))
        {
            Response.Cookies.Append(CurrentBranch.CookieName, branchId.ToString(), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddDays(30),
            });
        }
        // Detail pages belong to the old branch, so land on the list instead.
        var url = SafeReturn(returnUrl);
        var parts = url.Split('?')[0].Trim('/').Split('/');
        if (parts.Length >= 3 && int.TryParse(parts[^1], out _)) url = "/" + parts[0];
        return LocalRedirect(url);
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private string SafeReturn(string? url) => !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/";
}
