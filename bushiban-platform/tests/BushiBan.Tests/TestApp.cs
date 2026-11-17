using System.Net;
using System.Text.RegularExpressions;
using BushiBan.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BushiBan.Tests;

/// <summary>
/// Runs the real app against a private in-memory SQLite database, seeded with reference data and the demo users
/// (manager@, accountant@, frontdesk@, kiosk.attendance@, kiosk.registration@ — password Demo!2026; admin@ — Admin!2026).
/// One instance is shared by all tests in a class (IClassFixture&lt;TestApp&gt;).
/// </summary>
public class TestApp : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@bushiban.local";
    public const string AdminPassword = "Admin!2026";
    public const string DemoPassword = "Demo!2026";

    private readonly SqliteConnection _keepAlive;
    private readonly string _cs;

    public TestApp()
    {
        _cs = $"DataSource=file:bb{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (not ConfigureAppConfiguration) so Program.cs sees these before it registers the database.
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Default", _cs);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:DemoUsers", "true");
        builder.UseSetting("Seed:DemoData", "true");
        builder.UseSetting("Seed:DemoPassword", DemoPassword);
    }

    /// <summary>An HTTP client signed in as the given user. Cookies (auth, branch, antiforgery) are kept.</summary>
    public async Task<TestClient> SignInAsync(string email, string? password = null)
    {
        var client = new TestClient(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var res = await client.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Password"] = password ?? (email == AdminEmail ? AdminPassword : DemoPassword) });
        if (res.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException($"Sign-in failed for {email}: {(int)res.StatusCode}");
        return client;
    }

    /// <summary>Run code against the database (branch filter off) for arranging or checking test data.</summary>
    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        var branch = scope.ServiceProvider.GetRequiredService<BushiBan.Web.Services.ICurrentBranch>();
        using var _ = branch.Override(null, disableFilter: true);
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _keepAlive.Dispose();
    }
}

public class TestClient
{
    public TestClient(HttpClient http) => Http = http;
    public HttpClient Http { get; }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);

    public async Task<string> GetStringAsync(string url)
    {
        var res = await Http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GET {url} returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}");
        return body;
    }

    /// <summary>Loads <paramref name="formPage"/> to get an antiforgery token, then posts the fields to <paramref name="url"/>.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string formPage, string url, Dictionary<string, string> fields)
    {
        var html = await (await Http.GetAsync(formPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var data = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };
        return await Http.PostAsync(url, new FormUrlEncodedContent(data));
    }

    /// <summary>Posts with repeated keys (checkbox lists etc.).</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string formPage, string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var html = await (await Http.GetAsync(formPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        return await Http.PostAsync(url, new FormUrlEncodedContent(fields.Append(new("__RequestVerificationToken", token))));
    }
}
