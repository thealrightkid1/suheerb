using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Data;
using BushiBan.Web.Data.Seed;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Domain.Organization;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Administration;

[HasPermission(P.Branches.View)]
public class BranchesController : Controller
{
    /// <summary>Time zones offered for US branches (IANA ids, which work on Windows and Linux in .NET 8).</summary>
    public static readonly (string Id, string Label)[] TimeZones =
    {
        ("America/Chicago", "Central (Texas, Chicago)"),
        ("America/New_York", "Eastern (Connecticut, New York)"),
        ("America/Denver", "Mountain (Denver)"),
        ("America/Phoenix", "Arizona (no daylight saving)"),
        ("America/Los_Angeles", "Pacific (California)"),
    };

    private readonly AppDbContext _db;
    private readonly BranchProvisioner _provisioner;
    private readonly LookupService _lookups;
    private readonly ICurrentBranch _branch;
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly ILogger<BranchesController> _log;

    public BranchesController(AppDbContext db, BranchProvisioner provisioner, LookupService lookups, ICurrentBranch branch,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, ILogger<BranchesController> log)
    {
        _db = db; _provisioner = provisioner; _lookups = lookups; _branch = branch; _users = users; _signIn = signIn; _log = log;
    }

    public class BranchInput
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "Enter the branch name."), StringLength(150)] public string? Name { get; set; }
        [Required(ErrorMessage = "Choose the school.")] public int? SchoolId { get; set; }
        [Required(ErrorMessage = "Enter a short code."), RegularExpression("^[A-Za-z]{2,6}$", ErrorMessage = "Use 2 to 6 letters, e.g. PAS.")]
        public string? Code { get; set; }
        [StringLength(200)] public string? Address { get; set; }
        public int? CityId { get; set; }
        [StringLength(20)] public string? ZipCode { get; set; }
        [StringLength(30), Phone(ErrorMessage = "That phone number isn't valid.")] public string? Phone { get; set; }
        [StringLength(150), EmailAddress(ErrorMessage = "That email address isn't valid.")] public string? Email { get; set; }
        [Required] public string TimeZone { get; set; } = "America/Chicago";
        public bool IsActive { get; set; } = true;
    }

    public sealed record BranchRow(int Id, string Name, string Code, string? City, string? State, string? Zip, string TimeZone, string? Phone, bool IsActive, int Students);

    /// <summary>Branches the user can see: all for system admins, otherwise their own.</summary>
    private IQueryable<Branch> Visible() =>
        _branch.IsSystemAdmin ? _db.Branches : _db.Branches.Where(b => _branch.AllowedBranchIds.Contains(b.Id));

    [HttpGet]
    public async Task<IActionResult> Index(string? q, string? status, CancellationToken ct)
    {
        var query = Visible();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(b => b.Name.Contains(q) || b.Code.Contains(q) || (b.City != null && b.City.Name.Contains(q)));
        if (status == "active") query = query.Where(b => b.IsActive);
        else if (status == "inactive") query = query.Where(b => !b.IsActive);
        var branches = await query.OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name, b.Code, City = b.City != null ? b.City.Name : null, State = b.City != null ? b.City.State!.Name : null, b.ZipCode, b.TimeZone, b.Phone, b.IsActive })
            .ToListAsync(ct);

        // Students live in each branch's own data, so this cross-branch count bypasses the branch filter,
        // limited to the branches listed (which are the ones this user may see).
        var ids = branches.Select(b => b.Id).ToList();
        var counts = await _db.Students.IgnoreQueryFilters().Where(s => !s.IsDeleted && ids.Contains(s.BranchId))
            .GroupBy(s => s.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        ViewData["q"] = q;
        ViewData["status"] = status;
        ViewData["CanManage"] = User.HasPermission(P.Branches.Manage);
        return View(branches.Select(b => new BranchRow(b.Id, b.Name, b.Code, b.City, b.State, b.ZipCode, b.TimeZone, b.Phone, b.IsActive,
            counts.GetValueOrDefault(b.Id))).ToList());
    }

    [HttpGet, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var school = await _db.Schools.Where(s => s.IsActive).OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
        return await FormView(new BranchInput { SchoolId = school }, ct);
    }

    [HttpPost, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> Create(BranchInput input, CancellationToken ct)
    {
        await ValidateAsync(input, ct);
        if (!ModelState.IsValid) return await FormView(input, ct);

        var branch = new Branch();
        Apply(branch, input);
        branch.IsActive = true;
        _db.Branches.Add(branch);
        await _db.SaveChangesAsync(ct);

        // A branch that isn't a system admin's can't be switched to unless the creator is given access.
        if (!_branch.IsSystemAdmin && await _users.GetUserAsync(User) is { } me)
        {
            _db.UserBranches.Add(new UserBranch { UserId = me.Id, BranchId = branch.Id });
            await _db.SaveChangesAsync(ct);
            await _users.UpdateSecurityStampAsync(me);
            await _signIn.RefreshSignInAsync(me);
        }

        try
        {
            await _provisioner.ProvisionAsync(branch.Id, ct);
            this.Success($"{branch.Name} is ready: ranks, programs, chart of accounts, kiosk forms and message templates were set up. Next, add a manager under Users.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Provisioning branch {BranchId} failed", branch.Id);
            this.Error($"{branch.Name} was created, but its starting setup didn't finish. Open the branch and save it again to retry.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var b = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        return await FormView(new BranchInput
        {
            Id = b.Id, Name = b.Name, SchoolId = b.SchoolId, Code = b.Code, Address = b.Address, CityId = b.CityId, ZipCode = b.ZipCode,
            Phone = b.Phone, Email = b.Email, TimeZone = b.TimeZone, IsActive = b.IsActive,
        }, ct);
    }

    [HttpPost, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> Edit(int id, BranchInput input, CancellationToken ct)
    {
        var b = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        input.Id = id;
        await ValidateAsync(input, ct);
        if (!ModelState.IsValid) return await FormView(input, ct);
        Apply(b, input);
        await _db.SaveChangesAsync(ct);
        // Saving again finishes any starting setup that didn't complete (it only adds what's missing).
        await _provisioner.ProvisionAsync(b.Id, ct);
        this.Success($"{b.Name} saved.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Turns a branch off (it disappears from the branch switcher) or back on.</summary>
    [HttpPost, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> SetActive(int id, bool active, CancellationToken ct)
    {
        var b = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        b.IsActive = active;
        await _db.SaveChangesAsync(ct);
        this.Success(active ? $"{b.Name} is active again." : $"{b.Name} was deactivated. Its records are kept; staff can no longer switch to it.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Branches.Manage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var b = await Visible().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return NotFound();
        var students = await _db.Students.IgnoreQueryFilters().CountAsync(s => s.BranchId == id && !s.IsDeleted, ct);
        var users = await _db.Users.CountAsync(u => u.HomeBranchId == id, ct);
        if (students > 0 || users > 0)
        {
            this.Error($"{b.Name} can't be deleted because {Describe(students, "student")}{(students > 0 && users > 0 ? " and " : "")}{Describe(users, "user")} belong to it. Deactivate it instead.");
            return RedirectToAction(nameof(Index));
        }
        _db.Branches.Remove(b);
        await _db.SaveChangesAsync(ct);
        this.Success($"{b.Name} was deleted.");
        return RedirectToAction(nameof(Index));
    }

    private static string Describe(int n, string what) => n == 0 ? "" : $"{n} {what}{(n == 1 ? "" : "s")}";

    private async Task ValidateAsync(BranchInput input, CancellationToken ct)
    {
        input.Code = input.Code?.Trim().ToUpperInvariant();
        ModelState.Remove(nameof(input.Code));
        if (string.IsNullOrEmpty(input.Code) || !System.Text.RegularExpressions.Regex.IsMatch(input.Code, "^[A-Z]{2,6}$"))
            ModelState.AddModelError(nameof(input.Code), "Use 2 to 6 letters, e.g. PAS.");
        else if (await _db.Branches.AnyAsync(b => b.Code == input.Code && b.Id != input.Id, ct))
            ModelState.AddModelError(nameof(input.Code), $"Another branch already uses the code {input.Code}.");
        if (TimeZones.All(t => t.Id != input.TimeZone)) ModelState.AddModelError(nameof(input.TimeZone), "Choose a time zone.");
        if (input.SchoolId is int s && !await _db.Schools.AnyAsync(x => x.Id == s, ct)) ModelState.AddModelError(nameof(input.SchoolId), "Choose the school.");
        if (input.CityId is int c && !await _db.Cities.AnyAsync(x => x.Id == c, ct)) ModelState.AddModelError(nameof(input.CityId), "Choose a city from the list.");
    }

    private static void Apply(Branch b, BranchInput i)
    {
        b.Name = i.Name!.Trim();
        b.SchoolId = i.SchoolId!.Value;
        b.Code = i.Code!;
        b.Address = Clean(i.Address);
        b.CityId = i.CityId;
        b.ZipCode = Clean(i.ZipCode);
        b.Phone = Clean(i.Phone);
        b.Email = Clean(i.Email);
        b.TimeZone = i.TimeZone;
        if (i.Id != 0) b.IsActive = i.IsActive;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<IActionResult> FormView(BranchInput input, CancellationToken ct)
    {
        ViewData["Schools"] = await _db.Schools.OrderBy(s => s.Name).Select(s => new SelectListItem(s.Name, s.Id.ToString(), s.Id == input.SchoolId)).ToListAsync(ct);
        ViewData["Cities"] = await _lookups.CitiesAsync(input.CityId);
        return View("Form", input);
    }
}
