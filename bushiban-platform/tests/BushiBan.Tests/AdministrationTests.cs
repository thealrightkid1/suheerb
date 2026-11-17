using System.Net;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Tests;

public class AdministrationTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public AdministrationTests(TestApp app) => _app = app;

    private const string Manager = "manager@bushiban.local";
    private const string NewPassword = "Welcome!2026";

    private Task<int> BranchIdAsync(string code) => _app.DbAsync(db => db.Branches.Where(b => b.Code == code).Select(b => b.Id).SingleAsync());
    private Task<AppUser> UserAsync(string email) => _app.DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
    private Task<List<string>> PermissionsAsync(string userId) =>
        _app.DbAsync(db => db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == Permissions.ClaimType).Select(c => c.ClaimValue!).ToListAsync());

    private async Task<HttpResponseMessage> CreateUserAsync(TestClient c, string email, string preset, int homeBranch, params int[] extraBranches)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("DisplayName", "Test " + email), new("Email", email), new("Password", NewPassword), new("ConfirmPassword", NewPassword),
            new("Preset", preset), new("HomeBranchId", homeBranch.ToString()), new("IsActive", "true"),
        };
        fields.AddRange(extraBranches.Select(b => new KeyValuePair<string, string>("ExtraBranchIds", b.ToString())));
        return await c.PostFormAsync("/Users/Create", "/Users/Create", fields);
    }

    [Fact]
    public async Task Creating_a_user_with_a_preset_grants_its_permissions()
    {
        var c = await _app.SignInAsync(Manager);
        var pas = await BranchIdAsync("PAS");
        var res = await CreateUserAsync(c, "new.desk@bushiban.local", "Receptionist", pas);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var user = await UserAsync("new.desk@bushiban.local");
        Assert.Equal(pas, user.HomeBranchId);
        Assert.Equal("Receptionist", user.Preset);
        var perms = await PermissionsAsync(user.Id);
        Assert.Equal(Permissions.Presets["Receptionist"].OrderBy(p => p), perms.OrderBy(p => p));

        // They can sign in with the password they were given.
        await _app.SignInAsync("new.desk@bushiban.local", NewPassword);
        Assert.Contains("new.desk@bushiban.local", await c.GetStringAsync("/Users"));
    }

    [Fact]
    public async Task Managers_cannot_create_or_grant_system_admin()
    {
        var c = await _app.SignInAsync(Manager);
        var pas = await BranchIdAsync("PAS");
        var res = await CreateUserAsync(c, "sneaky@bushiban.local", "SystemAdmin", pas);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Only a system admin", await res.Content.ReadAsStringAsync());
        Assert.False(await _app.DbAsync(db => db.Users.AnyAsync(u => u.Email == "sneaky@bushiban.local")));

        var desk = await UserAsync("frontdesk@bushiban.local");
        var fields = (await PermissionsAsync(desk.Id)).Append(Permissions.SystemAdmin).Select(p => new KeyValuePair<string, string>("permissions", p));
        res = await c.PostFormAsync($"/Users/Permissions/{desk.Id}", $"/Users/Permissions/{desk.Id}", fields);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(Permissions.SystemAdmin, await PermissionsAsync(desk.Id));

        // Nor can they edit a system admin's account.
        var admin = await UserAsync(TestApp.AdminEmail);
        res = await c.GetAsync($"/Users/Permissions/{admin.Id}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task Managers_only_see_and_assign_their_own_branches()
    {
        var admin = await _app.SignInAsync(TestApp.AdminEmail);
        var frw = await BranchIdAsync("FRW");
        Assert.Equal(HttpStatusCode.Redirect, (await CreateUserAsync(admin, "friendswood.desk@bushiban.local", "Receptionist", frw)).StatusCode);

        var c = await _app.SignInAsync(Manager);
        var list = await c.GetStringAsync("/Users");
        Assert.DoesNotContain("friendswood.desk@bushiban.local", list);
        Assert.Contains("frontdesk@bushiban.local", list);

        var res = await CreateUserAsync(c, "elsewhere@bushiban.local", "Receptionist", frw);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("branches you manage", await res.Content.ReadAsStringAsync());

        var other = await UserAsync("friendswood.desk@bushiban.local");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/Users/Edit/{other.Id}")).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_deactivate_themselves_or_drop_their_own_user_management()
    {
        var c = await _app.SignInAsync(Manager);
        var me = await UserAsync(Manager);
        var res = await c.PostFormAsync($"/Users/Edit/{me.Id}", $"/Users/Edit/{me.Id}", new()
        {
            ["DisplayName"] = me.DisplayName!, ["Email"] = me.Email!, ["Preset"] = me.Preset!, ["HomeBranchId"] = me.HomeBranchId!.ToString()!, ["IsActive"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("can&#x27;t deactivate your own account", await res.Content.ReadAsStringAsync());
        Assert.True((await UserAsync(Manager)).IsActive);

        var without = (await PermissionsAsync(me.Id)).Where(p => p != Permissions.Users.Manage).Select(p => new KeyValuePair<string, string>("permissions", p));
        res = await c.PostFormAsync($"/Users/Permissions/{me.Id}", $"/Users/Permissions/{me.Id}", without);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(Permissions.Users.Manage, await PermissionsAsync(me.Id));
    }

    [Fact]
    public async Task The_last_system_admin_cannot_be_demoted()
    {
        var c = await _app.SignInAsync(TestApp.AdminEmail);
        var admin = await UserAsync(TestApp.AdminEmail);
        var everythingElse = Permissions.All.Select(p => new KeyValuePair<string, string>("permissions", p));
        var res = await c.PostFormAsync($"/Users/Permissions/{admin.Id}", $"/Users/Permissions/{admin.Id}", everythingElse);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("last active system admin", await res.Content.ReadAsStringAsync());
        Assert.Contains(Permissions.SystemAdmin, await PermissionsAsync(admin.Id));
    }

    [Fact]
    public async Task Permission_changes_update_the_security_stamp_and_take_effect()
    {
        var admin = await _app.SignInAsync(TestApp.AdminEmail);
        var pas = await BranchIdAsync("PAS");
        Assert.Equal(HttpStatusCode.Redirect, (await CreateUserAsync(admin, "promoted@bushiban.local", "Receptionist", pas)).StatusCode);
        var user = await UserAsync("promoted@bushiban.local");

        var before = await _app.SignInAsync("promoted@bushiban.local", NewPassword);
        Assert.NotEqual(HttpStatusCode.OK, (await before.GetAsync("/Users")).StatusCode);

        var fields = Permissions.Presets["Receptionist"].Append(Permissions.Users.View).Select(p => new KeyValuePair<string, string>("permissions", p));
        var res = await admin.PostFormAsync($"/Users/Permissions/{user.Id}", $"/Users/Permissions/{user.Id}", fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var after = await UserAsync("promoted@bushiban.local");
        Assert.NotEqual(user.SecurityStamp, after.SecurityStamp);
        Assert.Equal("Custom", after.Preset);
        Assert.Contains(Permissions.Users.View, await PermissionsAsync(user.Id));

        var fresh = await _app.SignInAsync("promoted@bushiban.local", NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/Users")).StatusCode);
    }

    [Fact]
    public async Task Reset_password_and_unlock()
    {
        var admin = await _app.SignInAsync(TestApp.AdminEmail);
        var pas = await BranchIdAsync("PAS");
        await CreateUserAsync(admin, "forgetful@bushiban.local", "Receptionist", pas);
        var user = await UserAsync("forgetful@bushiban.local");
        await _app.DbAsync(async db => { (await db.Users.SingleAsync(u => u.Id == user.Id)).LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10); return await db.SaveChangesAsync(); });
        var locked = await admin.GetStringAsync("/Users?status=locked");
        Assert.Contains("forgetful@bushiban.local", locked);
        Assert.DoesNotContain("frontdesk@bushiban.local", locked);

        var res = await admin.PostFormAsync($"/Users/ResetPassword/{user.Id}", $"/Users/ResetPassword/{user.Id}", new()
        {
            ["NewPassword"] = "Brand!New2026", ["ConfirmPassword"] = "Brand!New2026",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Null((await UserAsync("forgetful@bushiban.local")).LockoutEnd);
        await _app.SignInAsync("forgetful@bushiban.local", "Brand!New2026");
    }

    [Fact]
    public async Task A_new_branch_is_provisioned_with_the_standard_setup()
    {
        var c = await _app.SignInAsync(TestApp.AdminEmail);
        var school = await _app.DbAsync(db => db.Schools.Select(s => s.Id).FirstAsync());
        var res = await c.PostFormAsync("/Branches/Create", "/Branches/Create", new()
        {
            ["Name"] = "Clear Creek", ["SchoolId"] = school.ToString(), ["Code"] = "clc", ["TimeZone"] = "America/Chicago", ["ZipCode"] = "77573",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var id = await BranchIdAsync("CLC");
        var counts = await _app.DbAsync(async db => new
        {
            Ranks = await db.Ranks.CountAsync(r => r.BranchId == id),
            Programs = await db.Courses.CountAsync(r => r.BranchId == id),
            Accounts = await db.DetailAccounts.CountAsync(r => r.BranchId == id),
            Forms = await db.FormDefinitions.CountAsync(r => r.BranchId == id),
            Templates = await db.MessageTemplates.CountAsync(r => r.BranchId == id),
            Settings = await db.AccountingSettings.CountAsync(r => r.BranchId == id),
        });
        Assert.Equal(22, counts.Ranks);
        Assert.Equal(10, counts.Programs);
        Assert.Equal(54, counts.Accounts);
        Assert.Equal(6, counts.Forms);
        Assert.Equal(5, counts.Templates);
        Assert.Equal(1, counts.Settings);

        // Codes are unique.
        res = await c.PostFormAsync("/Branches/Create", "/Branches/Create", new()
        {
            ["Name"] = "Another", ["SchoolId"] = school.ToString(), ["Code"] = "PAS", ["TimeZone"] = "America/Chicago",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("already uses the code PAS", await res.Content.ReadAsStringAsync());

        // A branch with students can't be deleted.
        var pas = await BranchIdAsync("PAS");
        res = await c.PostFormAsync("/Branches", $"/Branches/Delete/{pas}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.False(await _app.DbAsync(db => db.Branches.IgnoreQueryFilters().Where(b => b.Id == pas).Select(b => b.IsDeleted).SingleAsync()));
    }

    [Fact]
    public async Task Audit_log_shows_field_changes()
    {
        var c = await _app.SignInAsync(Manager);
        var template = await _app.DbAsync(db => db.MessageTemplates.Include(t => t.Branch).FirstAsync(t => t.Branch!.Code == "PAS" && t.Name == "We miss you"));
        var res = await c.PostFormAsync($"/MessageTemplates/Edit/{template.Id}", $"/MessageTemplates/Edit/{template.Id}", new()
        {
            ["Name"] = "We miss you on the mat", ["Channel"] = "SMS", ["Body"] = template.Body, ["IsActive"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var html = await c.GetStringAsync($"/AuditLog?Entity=MessageTemplate&EntityId={template.Id}&Action=Updated");
        Assert.Contains("We miss you on the mat", html);
        Assert.Contains("change-old", html);
        Assert.Contains(">We miss you<", html);

        // Other branches' changes aren't visible to a branch manager.
        var otherBranchLog = await _app.DbAsync(db => db.AuditLogs.Where(a => a.BranchId != null && a.BranchId != template.BranchId && a.EntityName == "MessageTemplate").Select(a => a.EntityId).FirstAsync());
        Assert.Contains("No changes found", await c.GetStringAsync($"/AuditLog?Entity=MessageTemplate&EntityId={otherBranchLog}&Action=Created"));
    }

    [Fact]
    public async Task Changelog_is_readable_by_everyone_and_editable_with_permission()
    {
        var desk = await _app.SignInAsync("frontdesk@bushiban.local");
        var html = await desk.GetStringAsync("/Changelog");
        Assert.Contains("<li>CRM, Attendance and Registration now live in one app with one sign-in</li>", html);
        var res = await desk.GetAsync("/Changelog/Create");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);

        var c = await _app.SignInAsync(Manager);
        res = await c.PostFormAsync("/Changelog/Create", "/Changelog/Create", new()
        {
            ["Version"] = "1.1.0", ["ReleasedOn"] = "2026-11-17", ["Title"] = "Messaging", ["Body"] = "Email and texts.\n- Send to groups\n- Templates",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        html = await desk.GetStringAsync("/Changelog");
        Assert.Contains("<li>Send to groups</li>", html);
        Assert.Contains("<p class=\"mb-2\">Email and texts.</p>", html);
    }

    [Fact]
    public async Task Profile_change_password_works()
    {
        var admin = await _app.SignInAsync(TestApp.AdminEmail);
        var pas = await BranchIdAsync("PAS");
        await CreateUserAsync(admin, "profile.user@bushiban.local", "Receptionist", pas);

        var c = await _app.SignInAsync("profile.user@bushiban.local", NewPassword);
        Assert.Contains("profile.user@bushiban.local", await c.GetStringAsync("/Profile"));

        var res = await c.PostFormAsync("/Profile/ChangePassword", "/Profile/ChangePassword", new()
        {
            ["CurrentPassword"] = "wrong-password", ["NewPassword"] = "Changed!2026", ["ConfirmPassword"] = "Changed!2026",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("current password isn", await res.Content.ReadAsStringAsync());

        res = await c.PostFormAsync("/Profile/ChangePassword", "/Profile/ChangePassword", new()
        {
            ["CurrentPassword"] = NewPassword, ["NewPassword"] = "Changed!2026", ["ConfirmPassword"] = "Changed!2026",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        // Still signed in on this browser.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Profile")).StatusCode);
        await _app.SignInAsync("profile.user@bushiban.local", "Changed!2026");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _app.SignInAsync("profile.user@bushiban.local", NewPassword));

        // Changing email needs the current password.
        res = await c.PostFormAsync("/Profile/Edit", "/Profile/Edit", new() { ["DisplayName"] = "Pro File", ["Email"] = "renamed@bushiban.local" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        res = await c.PostFormAsync("/Profile/Edit", "/Profile/Edit", new() { ["DisplayName"] = "Pro File", ["Email"] = "renamed@bushiban.local", ["CurrentPassword"] = "Changed!2026" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        await _app.SignInAsync("renamed@bushiban.local", "Changed!2026");
    }
}
