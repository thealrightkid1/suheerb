using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Features.Communications.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BushiBan.Tests;

public class CommunicationsTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public CommunicationsTests(TestApp app) => _app = app;

    private const string Manager = "manager@bushiban.local";

    [Fact]
    public void Templates_fill_known_placeholders_and_blank_unknown_ones()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = "Liam", ["LastName"] = "Garcia", ["BranchName"] = "Pearland", ["Program"] = "MMA", ["Balance"] = "$80.00",
        };
        var text = TemplateRenderer.Render("Hi {{FirstName}} {{ lastname }}, {{Program}} at {{BranchName}} owes {{Balance}}.{{Nickname}}", values);
        Assert.Equal("Hi Liam Garcia, MMA at Pearland owes $80.00.", text);
        Assert.Equal("", TemplateRenderer.Render(null, values));
    }

    [Theory]
    [InlineData("(713) 555-0123", "+17135550123")]
    [InlineData("713.555.0123", "+17135550123")]
    [InlineData("1-713-555-0123", "+17135550123")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("713-555-0123 x12", "+17135550123")]
    [InlineData("555-0123", null)]
    [InlineData("", null)]
    public void Phone_numbers_are_normalised_to_E164(string raw, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.ToE164(raw));

    [Fact]
    public void Sms_segments_follow_carrier_rules()
    {
        Assert.Equal(1, SmsLength.Segments(new string('a', 160)));
        Assert.Equal(2, SmsLength.Segments(new string('a', 161)));
        Assert.Equal(1, SmsLength.Segments("Hi 😀"));
        Assert.Equal(2, SmsLength.Segments(new string('a', 70) + "😀"));
    }

    [Fact]
    public async Task Sending_to_a_student_without_a_provider_logs_NotConfigured_and_an_activity()
    {
        var student = await PasadenaStudentAsync(s => s.IsEmail1Subscriber && s.Email1 != null);
        var c = await _app.SignInAsync(Manager);
        var res = await c.PostFormAsync("/Messages/Compose", "/Messages/Compose", new()
        {
            ["Mode"] = "one", ["Recipient"] = $"s:{student.Id}", ["Channel"] = "Email",
            ["Subject"] = "Hello {{FirstName}}", ["Body"] = "Hi {{FirstName}}, see you in {{Program}} at {{BranchName}}.",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var log = await _app.DbAsync(db => db.MessageLogs.Where(m => m.StudentId == student.Id).OrderByDescending(m => m.Id).FirstAsync());
        Assert.Equal(MessageStatus.NotConfigured, log.Status);
        Assert.Equal(student.Email1, log.To);
        Assert.Equal($"Hello {student.FirstName}", log.Subject);
        Assert.Contains($"Hi {student.FirstName}, see you in", log.Body);
        Assert.Contains("Pasadena (Deer Park)", log.Body);
        Assert.NotNull(log.Error);
        Assert.Null(log.BatchId);
        Assert.True(await _app.DbAsync(db => db.Activities.AnyAsync(a => a.StudentId == student.Id && a.Type == ActivityType.Email && a.Body.Contains("not sent"))));

        var history = await c.GetStringAsync("/Messages?Q=" + Uri.EscapeDataString(student.LastName));
        Assert.Contains("Not sent: provider not set up", history);
    }

    [Fact]
    public async Task Unsubscribed_students_are_not_sent_a_single_message()
    {
        var student = await PasadenaStudentAsync(s => s.Phone1 != null);
        await _app.DbAsync(async db => { (await db.Students.SingleAsync(s => s.Id == student.Id)).IsPhone1Subscriber = false; return await db.SaveChangesAsync(); });
        var before = await _app.DbAsync(db => db.MessageLogs.CountAsync(m => m.StudentId == student.Id && m.Channel == MessageChannel.SMS));

        var c = await _app.SignInAsync(Manager);
        var res = await c.PostFormAsync("/Messages/Compose", "/Messages/Compose", new()
        {
            ["Mode"] = "one", ["Recipient"] = $"s:{student.Id}", ["Channel"] = "SMS", ["Body"] = "Class is cancelled today.",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("unsubscribed", await res.Content.ReadAsStringAsync());
        Assert.Equal(before, await _app.DbAsync(db => db.MessageLogs.CountAsync(m => m.StudentId == student.Id && m.Channel == MessageChannel.SMS)));
    }

    [Fact]
    public async Task Bulk_send_skips_unsubscribed_and_unreachable_students()
    {
        // A program of its own with five students: two unsubscribed from texts, one with no phone, two reachable.
        var (courseId, ids) = await _app.DbAsync(async db =>
        {
            var pas = await db.Branches.SingleAsync(b => b.Code == "PAS");
            var course = new Course { BranchId = pas.Id, Name = "Bulk Test Program " + Guid.NewGuid().ToString("N")[..6] };
            db.Courses.Add(course);
            await db.SaveChangesAsync();
            var students = await db.Students.Where(s => s.BranchId == pas.Id && s.Phone1 != null).OrderByDescending(s => s.Id).Take(5).ToListAsync();
            for (var i = 0; i < students.Count; i++)
            {
                var s = students[i];
                s.CourseId = course.Id; s.IsActive = true; s.IsPhone1Subscriber = i >= 2;
                if (i == 2) s.Phone1 = null;
            }
            await db.SaveChangesAsync();
            return (course.Id, students.Select(s => s.Id).ToList());
        });

        var c = await _app.SignInAsync(Manager);
        var preview = JsonDocument.Parse(await c.GetStringAsync($"/Messages/Preview?Audience.Kind=Students&Audience.CourseId={courseId}&channel=SMS")).RootElement;
        Assert.Equal(5, preview.GetProperty("total").GetInt32());
        Assert.Equal(2, preview.GetProperty("reachable").GetInt32());
        Assert.Equal(2, preview.GetProperty("unsubscribed").GetInt32());
        Assert.Equal(1, preview.GetProperty("noAddress").GetInt32());

        var res = await c.PostFormAsync("/Messages/Compose", "/Messages/Compose", new()
        {
            ["Mode"] = "group", ["Audience.Kind"] = "Students", ["Audience.CourseId"] = courseId.ToString(), ["Audience.Status"] = "active",
            ["Channel"] = "SMS", ["Body"] = "Hi {{FirstName}}, no class Monday.",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var batch = Guid.Parse(Regex.Match(res.Headers.Location!.ToString(), "batch=([0-9a-f-]+)").Groups[1].Value);

        var logs = await _app.DbAsync(db => db.MessageLogs.Where(m => m.BatchId == batch).ToListAsync());
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(MessageStatus.NotConfigured, l.Status));
        Assert.All(logs, l => Assert.Matches(@"^\+1\d{10}$", l.To));
        Assert.Equal(ids.Skip(3).OrderBy(i => i), logs.Select(l => l.StudentId!.Value).OrderBy(i => i));
    }

    [Fact]
    public async Task With_a_working_provider_messages_are_marked_Sent()
    {
        var fake = new FakeSender();
        using var factory = _app.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IEmailSender>(fake);
            s.AddSingleton<ISmsSender>(fake);
        }));
        var c = await SignInAsync(factory, Manager, TestApp.DemoPassword);
        var student = await PasadenaStudentAsync(s => s.IsEmail1Subscriber && s.Email1 != null && s.IsPhone1Subscriber && s.Phone1 != null);

        var res = await c.PostFormAsync("/Messages/Compose", "/Messages/Compose", new()
        {
            ["Mode"] = "one", ["Recipient"] = $"s:{student.Id}", ["Channel"] = "Email", ["Subject"] = "Welcome {{FirstName}}", ["Body"] = "Balance: {{Balance}}",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        res = await c.PostFormAsync("/Messages/Compose", "/Messages/Compose", new()
        {
            ["Mode"] = "one", ["Recipient"] = $"s:{student.Id}", ["Channel"] = "SMS", ["Body"] = "Hi {{FirstName}}!",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var email = Assert.Single(fake.Emails);
        Assert.Equal($"Welcome {student.FirstName}", email.Subject);
        Assert.Equal("Balance: $0.00", email.Body);
        var sms = Assert.Single(fake.Texts);
        Assert.Equal($"Hi {student.FirstName}!", sms.Body);
        Assert.StartsWith("+1", sms.To);

        var logs = await _app.DbAsync(db => db.MessageLogs.Where(m => m.StudentId == student.Id).OrderByDescending(m => m.Id).Take(2).ToListAsync());
        Assert.All(logs, l => { Assert.Equal(MessageStatus.Sent, l.Status); Assert.NotNull(l.SentAt); Assert.Null(l.Error); });
    }

    [Fact]
    public async Task Templates_can_be_created_and_listed()
    {
        var c = await _app.SignInAsync(Manager);
        var res = await c.PostFormAsync("/MessageTemplates/Create", "/MessageTemplates/Create", new()
        {
            ["Name"] = "Holiday closure", ["Channel"] = "SMS", ["Body"] = "Hi {{FirstName}}, we're closed Thursday.", ["IsActive"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("Holiday closure", await c.GetStringAsync("/MessageTemplates"));

        // Email templates need a subject.
        res = await c.PostFormAsync("/MessageTemplates/Create", "/MessageTemplates/Create", new()
        {
            ["Name"] = "No subject", ["Channel"] = "Email", ["Body"] = "Hello", ["IsActive"] = "true",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("need a subject", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Calendar_feed_includes_class_times_and_saved_events()
    {
        var scheduleId = await _app.DbAsync(async db =>
        {
            var course = await db.Courses.Include(x => x.Branch).FirstAsync(x => x.Branch!.Code == "PAS" && x.Name == "Youth Martial Arts");
            var slot = new ClassSchedule { CourseId = course.Id, DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(17, 30), DurationMinutes = 45, Instructor = "Sensei Ray" };
            db.ClassSchedules.Add(slot);
            await db.SaveChangesAsync();
            return slot.Id;
        });

        var c = await _app.SignInAsync(Manager);
        var page = await c.GetStringAsync("/Calendar");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var save = new HttpRequestMessage(HttpMethod.Post, "/Calendar/Save")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { title = "Belt test", start = "2026-11-21T10:00", end = "2026-11-21T12:00", allDay = false, category = (int)EventCategory.Event, location = "Main mat" }), Encoding.UTF8, "application/json"),
        };
        save.Headers.Add("RequestVerificationToken", token);
        var saved = await c.Http.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Without the antiforgery header the post is rejected.
        var forged = await c.Http.PostAsync("/Calendar/Save", new StringContent("{\"title\":\"x\",\"start\":\"2026-11-21\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var json = JsonDocument.Parse(await c.GetStringAsync("/Calendar/Events?start=2026-11-01T00:00:00Z&end=2026-12-06T00:00:00Z")).RootElement;
        var items = json.EnumerateArray().ToList();
        var classes = items.Where(i => i.GetProperty("id").GetString()!.StartsWith($"c{scheduleId}-")).ToList();
        Assert.Equal(5, classes.Count); // Mondays: Nov 2, 9, 16, 23, 30
        Assert.Contains(classes, i => i.GetProperty("start").GetString() == "2026-11-02T17:30:00" && i.GetProperty("end").GetString() == "2026-11-02T18:15:00");
        Assert.All(classes, i => { Assert.Equal("class", i.GetProperty("kind").GetString()); Assert.False(i.GetProperty("editable").GetBoolean()); });
        Assert.Contains(items, i => i.GetProperty("title").GetString() == "Belt test" && i.GetProperty("start").GetString() == "2026-11-21T10:00:00");

        // Class times can be hidden.
        json = JsonDocument.Parse(await c.GetStringAsync("/Calendar/Events?start=2026-11-01&end=2026-12-06&classes=false")).RootElement;
        Assert.DoesNotContain(json.EnumerateArray(), i => i.GetProperty("kind").GetString() == "class");
    }

    [Fact]
    public async Task Receptionists_can_view_the_calendar_but_not_change_it()
    {
        var c = await _app.SignInAsync("frontdesk@bushiban.local");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Calendar")).StatusCode);
        var res = await c.PostFormAsync("/Calendar", "/Calendar/Delete", new());
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (res.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", res.Headers.Location!.ToString());
    }

    private Task<Student> PasadenaStudentAsync(System.Linq.Expressions.Expression<Func<Student, bool>> where) =>
        _app.DbAsync(db => db.Students.Include(s => s.Branch).Where(s => s.Branch!.Code == "PAS").Where(where).OrderBy(s => s.Id).FirstAsync());

    internal static async Task<TestClient> SignInAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = new TestClient(factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var res = await client.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return client;
    }

    private sealed class FakeSender : IEmailSender, ISmsSender
    {
        public List<EmailMessage> Emails { get; } = new();
        public List<SmsMessage> Texts { get; } = new();
        public bool IsConfigured => true;
        public Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default) { lock (Emails) Emails.Add(message); return Task.FromResult(SendResult.Sent("fake")); }
        public Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default) { lock (Texts) Texts.Add(message); return Task.FromResult(SendResult.Sent("SMfake")); }
    }
}
