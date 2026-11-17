using System.Net;
using BushiBan.Web.Domain.Academics;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Tests;

public class FoundationTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public FoundationTests(TestApp app) => _app = app;

    [Fact]
    public async Task Anonymous_users_are_sent_to_sign_in()
    {
        var res = await _app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/Students");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/Login", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Every_branch_is_provisioned_with_the_standard_setup()
    {
        var (branches, ranks, courses, accounts, forms) = await _app.DbAsync(async db => (
            await db.Branches.CountAsync(), await db.Ranks.CountAsync(), await db.Courses.CountAsync(),
            await db.DetailAccounts.CountAsync(), await db.FormDefinitions.CountAsync()));
        Assert.Equal(11, branches);
        Assert.Equal(11 * 22, ranks);
        Assert.Equal(11 * 10, courses);
        Assert.Equal(11 * 54, accounts);
        Assert.Equal(11 * 6, forms);
    }

    [Fact]
    public async Task Data_is_isolated_per_branch()
    {
        var c = await _app.SignInAsync("manager@bushiban.local");
        var html = await c.GetStringAsync("/Ranks");
        // Manager of one branch sees exactly that branch's 22 ranks, not 242.
        Assert.Contains("22 records", html);
    }

    [Fact]
    public async Task Kiosk_accounts_cannot_open_staff_screens()
    {
        var c = await _app.SignInAsync("kiosk.attendance@bushiban.local");
        var res = await c.GetAsync("/Ranks");
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (res.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Crud_create_and_soft_delete_a_rank()
    {
        var c = await _app.SignInAsync("manager@bushiban.local");
        var res = await c.PostFormAsync("/Ranks/Create", "/Ranks/Create", new() { ["Name"] = "Test Belt", ["Color"] = "#123456", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var rank = await _app.DbAsync(db => db.Ranks.SingleAsync(r => r.Name == "Test Belt"));

        // Duplicate names in the same branch are rejected with a friendly message.
        var dup = await c.PostFormAsync("/Ranks/Create", "/Ranks/Create", new() { ["Name"] = "Test Belt", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());

        res = await c.PostFormAsync("/Ranks", $"/Ranks/Delete/{rank.Id}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var deleted = await _app.DbAsync(db => db.Ranks.IgnoreQueryFilters().SingleAsync(r => r.Id == rank.Id));
        Assert.True(deleted.IsDeleted);
        Assert.True(await _app.DbAsync(db => db.AuditLogs.AnyAsync(a => a.EntityName == nameof(Rank) && a.EntityId == rank.Id.ToString() && a.Action == "Deleted")));
    }
}
