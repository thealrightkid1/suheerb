using System.Security.Claims;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Domain.Organization;
using BushiBan.Web.Domain.SystemTables;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Data.Seed;

public class DbSeeder
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly BranchProvisioner _provisioner;
    private readonly ICurrentBranch _branch;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DbSeeder> _log;
    private readonly IServiceProvider _services;

    public DbSeeder(AppDbContext db, UserManager<AppUser> users, BranchProvisioner provisioner, ICurrentBranch branch,
        IConfiguration config, IWebHostEnvironment env, ILogger<DbSeeder> log, IServiceProvider services)
    {
        _db = db; _users = users; _provisioner = provisioner; _branch = branch; _config = config; _env = env; _log = log; _services = services;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        using var _ = _branch.Override(null, disableFilter: true);

        if (!await _db.Countries.AnyAsync(ct))
        {
            var us = new Country { Name = "United States" };
            var states = ReferenceData.Cities.Select(c => c.State).Distinct().ToDictionary(s => s, s => new State { Name = s, Country = us });
            _db.Countries.Add(us);
            _db.Cities.AddRange(ReferenceData.Cities.Select(c => new City { Name = c.City, State = states[c.State] }));
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.Schools.AnyAsync(ct))
        {
            var school = new School { Name = "Bushi Ban International", LegalName = "Bushi Ban International, LLC" };
            var cities = await _db.Cities.Include(c => c.State).ToListAsync(ct);
            foreach (var b in ReferenceData.Branches)
            {
                var city = cities.FirstOrDefault(c => c.Name == b.City && c.State!.Name == b.State);
                _db.Branches.Add(new Branch { Name = b.Name, School = school, City = city, ZipCode = b.Zip, Code = b.Code, TimeZone = b.TimeZone });
            }
            await _db.SaveChangesAsync(ct);
        }

        foreach (var id in await _db.Branches.Select(b => b.Id).ToListAsync(ct))
            await _provisioner.ProvisionAsync(id, ct);

        if (!await _db.ChangelogEntries.AnyAsync(ct))
        {
            _db.ChangelogEntries.Add(new ChangelogEntry
            {
                Version = "1.0.0", ReleasedOn = DateOnly.FromDateTime(DateTime.UtcNow), Title = "One app for CRM, attendance, registration, billing and accounting",
                Body = "- CRM, Attendance and Registration now live in one app with one sign-in\n- New: billing (contracts, monthly invoices, payments) that posts to accounting automatically\n- New: lead follow-ups, activity timeline and one-click lead-to-student conversion\n- New: belt promotion tracking and a 'ready to test' list\n- New: email and SMS sending with templates and history\n- New: attendance kiosk with PIN or name search\n- Every change is recorded in the audit log",
            });
            await _db.SaveChangesAsync(ct);
        }

        await SeedUsersAsync(ct);

        if (_config.GetValue<bool>("Seed:DemoData"))
            await ActivatorUtilities.CreateInstance<DemoDataSeeder>(_services).SeedAsync(ct);
    }

    private async Task SeedUsersAsync(CancellationToken ct)
    {
        var adminEmail = _config["Seed:AdminEmail"] ?? "admin@bushiban.local";
        if (await _users.FindByEmailAsync(adminEmail) is null)
        {
            var password = _config["Seed:AdminPassword"];
            if (string.IsNullOrWhiteSpace(password))
            {
                password = "Bb!" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
                _log.LogWarning("Created admin account {Email} with a generated password: {Password}  (change it after signing in)", adminEmail, password);
            }
            var home = await _db.Branches.OrderBy(b => b.Id).FirstAsync(ct);
            await CreateUserAsync(adminEmail, "System Admin", password, "SystemAdmin", home.Id);
        }

        if (!(_env.IsDevelopment() || _env.IsEnvironment("Testing")) || !_config.GetValue<bool>("Seed:DemoUsers")) return;
        var pasadena = await _db.Branches.FirstAsync(b => b.Code == "PAS", ct);
        var demoPassword = _config["Seed:DemoPassword"] ?? "Demo!2026";
        foreach (var (email, name, preset) in new[]
        {
            ("manager@bushiban.local", "Pasadena Manager", "Manager"),
            ("accountant@bushiban.local", "Branch Accountant", "Accountant"),
            ("frontdesk@bushiban.local", "Front Desk", "Receptionist"),
            ("kiosk.attendance@bushiban.local", "Attendance Kiosk", "AttendanceUser"),
            ("kiosk.registration@bushiban.local", "Registration Kiosk", "RegistrationUser"),
        })
        {
            if (await _users.FindByEmailAsync(email) is null)
                await CreateUserAsync(email, name, demoPassword, preset, pasadena.Id);
        }
    }

    private async Task CreateUserAsync(string email, string name, string password, string preset, int branchId)
    {
        var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, HomeBranchId = branchId, Preset = preset };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException("Could not create " + email + ": " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await _users.AddClaimsAsync(user, Permissions.Presets[preset].Distinct().Select(p => new Claim(Permissions.ClaimType, p)));
    }
}
