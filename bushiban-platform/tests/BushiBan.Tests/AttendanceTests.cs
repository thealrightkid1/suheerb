using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Attendance;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Tests;

/// <summary>Shared helpers for the attendance, kiosk and academics tests.</summary>
internal static class AreaTestHelpers
{
    public static Task<int> PasadenaIdAsync(this TestApp app) =>
        app.DbAsync(db => db.Branches.Where(b => b.Code == "PAS").Select(b => b.Id).SingleAsync());

    /// <summary>"Today" the way the app sees it for Pasadena (America/Chicago).</summary>
    public static DateOnly BranchToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Chicago"));

    public static async Task<string> TokenAsync(this TestClient client, string page)
    {
        var html = await client.GetStringAsync(page);
        return Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(this TestClient client, string page, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", await client.TokenAsync(page));
        return await client.Http.SendAsync(request);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static List<KeyValuePair<string, string>> Fields(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();
}

public class AttendanceTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public AttendanceTests(TestApp app) => _app = app;

    private async Task<(int CourseId, int A, int B)> TwoStudentsAsync(string program)
    {
        var branch = await _app.PasadenaIdAsync();
        return await _app.DbAsync(async db =>
        {
            var course = await db.Courses.Where(c => c.BranchId == branch && c.Name == program).Select(c => c.Id).SingleAsync();
            var ids = await db.Students.Where(s => s.BranchId == branch && s.IsActive).OrderBy(s => s.Id).Select(s => s.Id).Take(2).ToListAsync();
            return (course, ids[0], ids[1]);
        });
    }

    private Task<List<AttendanceRecord>> RecordsAsync(int courseId, DateOnly date) =>
        _app.DbAsync(db => db.AttendanceRecords.Where(a => a.CourseId == courseId && a.Date == date).ToListAsync());

    [Fact]
    public async Task Roll_call_creates_records_and_never_duplicates()
    {
        var (course, a, b) = await TwoStudentsAsync("Tai Chi");
        var date = AreaTestHelpers.BranchToday().AddDays(-120);
        var c = await _app.SignInAsync("manager@bushiban.local");
        var page = $"/Attendance/Roll?courseId={course}";
        var fields = AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd")), ("checkInTime", "17:30"),
            ("present", a.ToString()), ("present", b.ToString()));

        var res = await c.PostFormAsync(page, "/Attendance/Roll", fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var records = await RecordsAsync(course, date);
        Assert.Equal(2, records.Count);
        Assert.All(records, r =>
        {
            Assert.Equal(CheckInMethod.Manual, r.Method);
            Assert.Equal(new TimeOnly(17, 30), r.CheckInTime);
            Assert.False(string.IsNullOrEmpty(r.MarkedBy));
        });

        // Saving the same roll again (double tap, back button) changes nothing.
        await c.PostFormAsync(page, "/Attendance/Roll", fields);
        Assert.Equal(2, (await RecordsAsync(course, date)).Count);

        // Unticking a student removes their record.
        await c.PostFormAsync(page, "/Attendance/Roll", AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd")), ("present", a.ToString())));
        var left = await RecordsAsync(course, date);
        Assert.Equal(a, Assert.Single(left).StudentId);

        // The roster page shows them as already checked in.
        var html = await c.GetStringAsync($"/Attendance/Roll?courseId={course}&date={date:yyyy-MM-dd}");
        Assert.Contains($"name=\"present\" value=\"{a}\" checked", html.Replace("checked=\"checked\"", "checked"));
    }

    [Fact]
    public async Task Staff_without_delete_permission_cannot_untick_someone_elses_check_in()
    {
        var (course, a, _) = await TwoStudentsAsync("Summer Camp");
        var date = AreaTestHelpers.BranchToday().AddDays(-130);
        var manager = await _app.SignInAsync("manager@bushiban.local");
        await manager.PostFormAsync($"/Attendance/Roll?courseId={course}", "/Attendance/Roll",
            AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd")), ("present", a.ToString())));

        var frontDesk = await _app.SignInAsync("frontdesk@bushiban.local");
        var res = await frontDesk.PostFormAsync($"/Attendance/Roll?courseId={course}", "/Attendance/Roll",
            AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd"))));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Single(await RecordsAsync(course, date));
    }

    [Fact]
    public async Task Future_dates_are_rejected()
    {
        var (course, a, _) = await TwoStudentsAsync("Jiujitsu");
        var date = AreaTestHelpers.BranchToday().AddDays(3);
        var c = await _app.SignInAsync("manager@bushiban.local");
        await c.PostFormAsync($"/Attendance/Roll?courseId={course}", "/Attendance/Roll",
            AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd")), ("present", a.ToString())));
        Assert.Empty(await RecordsAsync(course, date));
    }

    [Fact]
    public async Task Records_list_filters_exports_and_edit_blocks_duplicates()
    {
        var (course, a, b) = await TwoStudentsAsync("MMA");
        var date = AreaTestHelpers.BranchToday().AddDays(-140);
        var c = await _app.SignInAsync("manager@bushiban.local");
        await c.PostFormAsync($"/Attendance/Roll?courseId={course}", "/Attendance/Roll",
            AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", date.ToString("yyyy-MM-dd")), ("present", a.ToString()), ("present", b.ToString())));
        var records = await RecordsAsync(course, date);
        var studentA = await _app.DbAsync(db => db.Students.SingleAsync(s => s.Id == a));

        var query = $"from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&courseId={course}&method=Manual&q={studentA.LastName}";
        var html = await c.GetStringAsync("/Attendance?" + query);
        Assert.Contains(studentA.FullName, html);

        var csv = await c.Http.GetAsync("/Attendance/Export?" + query);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("Student,Student No,Date,Check-in time,Program,Method", text.TrimStart('﻿'));
        Assert.Contains(studentA.FullName, text);

        // Editing a record's time and method.
        var recordB = records.Single(r => r.StudentId == b);
        var res = await c.PostFormAsync($"/Attendance/Edit/{recordB.Id}", $"/Attendance/Edit/{recordB.Id}",
            new Dictionary<string, string> { ["Date"] = date.ToString("yyyy-MM-dd"), ["CheckInTime"] = "18:15", ["CourseId"] = course.ToString(), ["Method"] = "KioskPin", ["Notes"] = "Fixed time" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var edited = await _app.DbAsync(db => db.AttendanceRecords.SingleAsync(r => r.Id == recordB.Id));
        Assert.Equal(new TimeOnly(18, 15), edited.CheckInTime);
        Assert.Equal(CheckInMethod.KioskPin, edited.Method);

        // A second record for A on another day can't be moved onto the day A is already checked in.
        var other = date.AddDays(-1);
        await c.PostFormAsync($"/Attendance/Roll?courseId={course}", "/Attendance/Roll",
            AreaTestHelpers.Fields(("courseId", course.ToString()), ("date", other.ToString("yyyy-MM-dd")), ("present", a.ToString())));
        var second = Assert.Single(await RecordsAsync(course, other));
        var dup = await c.PostFormAsync($"/Attendance/Edit/{second.Id}", $"/Attendance/Edit/{second.Id}",
            new Dictionary<string, string> { ["Date"] = date.ToString("yyyy-MM-dd"), ["CheckInTime"] = "18:00", ["CourseId"] = course.ToString(), ["Method"] = "Manual" });
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("already checked in", await dup.Content.ReadAsStringAsync());

        // Delete is soft.
        await c.PostFormAsync("/Attendance", $"/Attendance/Delete/{second.Id}", new Dictionary<string, string>());
        Assert.True(await _app.DbAsync(db => db.AttendanceRecords.IgnoreQueryFilters().AnyAsync(r => r.Id == second.Id && r.IsDeleted)));
    }

    [Fact]
    public async Task Report_lists_students_who_have_not_been_in()
    {
        var branch = await _app.PasadenaIdAsync();
        // An active student with no check-ins at all, enrolled long ago.
        var student = await _app.DbAsync(async db =>
        {
            var s = await db.Students.Where(x => x.BranchId == branch && x.IsActive).OrderByDescending(x => x.Id).FirstAsync();
            db.AttendanceRecords.RemoveRange(db.AttendanceRecords.Where(a => a.StudentId == s.Id));
            s.EnrollmentDate = AreaTestHelpers.BranchToday().AddDays(-200);
            await db.SaveChangesAsync();
            return s;
        });
        var c = await _app.SignInAsync("manager@bushiban.local");
        var html = await c.GetStringAsync("/Attendance/Report?days=14");
        Assert.Contains("been in for 14 days or more", html);
        Assert.Contains(student.FullName, html);
        Assert.Contains($"/Messages/Compose?studentId={student.Id}", html);
    }
}
