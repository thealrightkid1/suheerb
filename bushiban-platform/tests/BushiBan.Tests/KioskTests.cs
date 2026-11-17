using System.Net;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Features.Attendance;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Tests;

public class KioskTests : IClassFixture<TestApp>
{
    // A real 1x1 PNG, as the signature pad would send it (only much smaller).
    private const string TinyPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    private const string KioskAttendance = "kiosk.attendance@bushiban.local";
    private const string KioskRegistration = "kiosk.registration@bushiban.local";

    private readonly TestApp _app;
    public KioskTests(TestApp app) => _app = app;

    /// <summary>An active Pasadena student with a PIN who isn't checked in today. Each test asks for a different one.</summary>
    private async Task<Student> StudentAsync(int skip)
    {
        var branch = await _app.PasadenaIdAsync();
        var today = AreaTestHelpers.BranchToday();
        return await _app.DbAsync(async db =>
        {
            var s = await db.Students.Where(x => x.BranchId == branch && x.IsActive && x.KioskPin != null).OrderBy(x => x.Id).Skip(skip).FirstAsync();
            db.AttendanceRecords.RemoveRange(db.AttendanceRecords.Where(a => a.StudentId == s.Id && a.Date == today));
            await db.SaveChangesAsync();
            return s;
        });
    }

    // ---------------------------------------------------------------- Attendance kiosk

    [Fact]
    public async Task Pin_check_in_succeeds_once_and_rejects_a_second_check_in_today()
    {
        var student = await StudentAsync(0);
        var kiosk = await _app.SignInAsync(KioskAttendance);
        var started = DateTime.UtcNow.AddSeconds(-1);

        var first = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["pin"] = student.KioskPin! })).JsonAsync();
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal("checked-in", first.GetProperty("kind").GetString());
        Assert.Equal(student.FirstName, first.GetProperty("firstName").GetString());
        Assert.True(first.GetProperty("classesThisMonth").GetInt32() >= 1);
        // Nothing beyond first name and belt is revealed.
        Assert.DoesNotContain(student.LastName, first.ToString());

        var again = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["pin"] = student.KioskPin! })).JsonAsync();
        Assert.False(again.GetProperty("ok").GetBoolean());
        Assert.Equal("duplicate", again.GetProperty("kind").GetString());
        Assert.Equal($"You're already checked in today, {student.FirstName}!", again.GetProperty("message").GetString());

        var records = await _app.DbAsync(db => db.AttendanceRecords.Where(a => a.StudentId == student.Id && a.CreatedAt >= started).ToListAsync());
        var record = Assert.Single(records);
        Assert.Equal(CheckInMethod.KioskPin, record.Method);
        Assert.Equal(KioskAttendance, record.MarkedBy);
        Assert.Equal(student.CourseId, record.CourseId);
    }

    [Fact]
    public async Task Wrong_pin_gets_a_friendly_message()
    {
        var kiosk = await _app.SignInAsync(KioskAttendance);
        var res = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["pin"] = "000000" })).JsonAsync();
        Assert.Equal("wrong-pin", res.GetProperty("kind").GetString());
        Assert.Contains("couldn't find that PIN", res.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Find_my_name_shows_first_name_and_initial_and_checks_in()
    {
        var student = await StudentAsync(1);
        var kiosk = await _app.SignInAsync(KioskAttendance);
        var list = await (await kiosk.GetAsync($"/Kiosk/Search?q={Uri.EscapeDataString(student.FirstName[..2])}")).JsonAsync();
        var match = list.EnumerateArray().Single(m => m.GetProperty("id").GetInt32() == student.Id);
        Assert.Equal($"{student.FirstName} {student.LastName[0]}.", match.GetProperty("name").GetString());
        Assert.DoesNotContain(student.LastName, list.ToString());

        // One letter isn't enough to search.
        Assert.Equal(0, (await (await kiosk.GetAsync("/Kiosk/Search?q=a")).JsonAsync()).GetArrayLength());

        var res = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["studentId"] = student.Id.ToString() })).JsonAsync();
        Assert.Equal("checked-in", res.GetProperty("kind").GetString());
        var today = AreaTestHelpers.BranchToday();
        Assert.Equal(CheckInMethod.KioskSearch, await _app.DbAsync(db => db.AttendanceRecords.Where(a => a.StudentId == student.Id && a.Date == today).Select(a => a.Method).SingleAsync()));
    }

    [Fact]
    public async Task Kiosk_accounts_only_reach_their_own_kiosk()
    {
        var attendance = await _app.SignInAsync(KioskAttendance);
        // (The AttendanceUser preset includes Attendance.Create, so Take attendance itself is allowed.)
        foreach (var url in new[] { "/Attendance", "/Attendance/Report", "/Courses", "/FormBuilder", "/Promotions", "/Kiosk/Register" })
        {
            var res = await attendance.GetAsync(url);
            Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, url);
            if (res.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", res.Headers.Location!.ToString());
        }
        var registration = await _app.SignInAsync(KioskRegistration);
        foreach (var url in new[] { "/Attendance", "/Kiosk/Attendance", "/RankingSystems", "/FormBuilder/Edit/1" })
        {
            var res = await registration.GetAsync(url);
            Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, url);
        }
        // Kiosk pages are never cached, so the back button can't show someone else's answers.
        var page = await registration.GetAsync("/Kiosk/Register");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl?.NoStore);
    }

    // ---------------------------------------------------------------- Registration kiosk

    private async Task<(int FormId, int VersionId)> TrialFormAsync()
    {
        var branch = await _app.PasadenaIdAsync();
        return await _app.DbAsync(async db =>
        {
            var f = await db.FormDefinitions.Where(x => x.BranchId == branch && x.Category == FormCategory.Admission).SingleAsync();
            return (f.Id, f.PublishedVersionId!.Value);
        });
    }

    private static List<KeyValuePair<string, string>> TrialAnswers(string phone, string first = "Noa", string last = "Kioskson") => AreaTestHelpers.Fields(
        ("f_programs", "After School Program"), ("f_programs", "Mixed Martial Arts (MMA)"),
        ("f_studentFirstName", first), ("f_studentLastName", last), ("f_parentName", "Sam Kioskson"),
        ("f_dob", "2015-06-01"), ("f_sex", "Female"), ("f_emergencyContactName", "Sam Kioskson"), ("f_emergencyContactPhone", "(713) 555-0101"),
        ("f_cellPhone", phone), ("f_email", "noa.kioskson@example.com"), ("f_q3TrainedBefore", "No"),
        ("f_releaseAck", "Yes"), ("f_customerSignature", TinyPng), ("f_signatureDate", "2026-11-17"), ("f_guardianSignature", ""));

    [Fact]
    public async Task Lists_published_active_forms_in_order()
    {
        var kiosk = await _app.SignInAsync(KioskRegistration);
        var html = await kiosk.GetStringAsync("/Kiosk/Register");
        var trial = html.IndexOf("Trial Session / Starter Course Registration", StringComparison.Ordinal);
        var change = html.IndexOf("Account / Agreement Change Form", StringComparison.Ordinal);
        Assert.True(trial > 0 && change > trial);
    }

    [Fact]
    public async Task Trial_form_creates_submission_signatures_and_lead_then_links_a_repeat_visit()
    {
        var (formId, versionId) = await TrialFormAsync();
        var kiosk = await _app.SignInAsync(KioskRegistration);
        var cityId = await _app.DbAsync(db => db.Cities.Where(c => c.Name == "Pasadena").Select(c => c.Id).FirstAsync());
        var answers = TrialAnswers("(832) 555-7781");
        answers.Add(new("f_city", cityId.ToString()));

        var res = await kiosk.PostFormAsync($"/Kiosk/Form/{formId}", $"/Kiosk/Form/{formId}", answers);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Kiosk/Thanks", res.Headers.Location!.ToString());

        var submission = await _app.DbAsync(db => db.FormSubmissions.Include(s => s.Signatures).SingleAsync(s => s.LastName == "Kioskson"));
        Assert.Equal(versionId, submission.FormVersionId);
        Assert.Equal(("Noa", "(832) 555-7781", "noa.kioskson@example.com"), (submission.FirstName, submission.Phone, submission.Email));
        Assert.Equal(KioskRegistration, submission.KioskUser);
        Assert.Equal(SubmissionStatus.Linked, submission.Status);
        var signature = Assert.Single(submission.Signatures);
        Assert.Equal("customerSignature", signature.FieldKey);
        Assert.Equal(0x89, signature.Png[0]);
        Assert.Contains("\"programs\":[\"After School Program\",\"Mixed Martial Arts (MMA)\"]", submission.DataJson);
        Assert.Contains("\"city__name\":\"Pasadena, Texas\"", submission.DataJson);

        var lead = await _app.DbAsync(db => db.Leads.SingleAsync(l => l.Id == submission.LeadId));
        Assert.Equal(("Noa", "Kioskson", LeadSource.Kiosk, ProspectPhase.AppointmentSet), (lead.FirstName, lead.LastName, lead.Source, lead.ProspectPhase));
        Assert.Equal(new DateOnly(2015, 6, 1), lead.DateOfBirth);
        Assert.Equal(cityId, lead.CityId);
        Assert.Equal("Sam Kioskson", lead.FatherName);
        Assert.True(await _app.DbAsync(db => db.Activities.AnyAsync(a => a.LeadId == lead.Id && a.Body == "Submitted Trial Session / Starter Course Registration at the kiosk")));

        // The same family comes back and types the phone differently: linked, no second lead.
        res = await kiosk.PostFormAsync($"/Kiosk/Form/{formId}", $"/Kiosk/Form/{formId}", TrialAnswers("+1 832.555.7781", first: "Noa", last: "Kioskson-Again"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var second = await _app.DbAsync(db => db.FormSubmissions.SingleAsync(s => s.LastName == "Kioskson-Again"));
        Assert.Equal(lead.Id, second.LeadId);
        Assert.Equal(SubmissionStatus.Linked, second.Status);
        Assert.False(await _app.DbAsync(db => db.Leads.AnyAsync(l => l.LastName == "Kioskson-Again")));
        Assert.Equal(2, await _app.DbAsync(db => db.Activities.CountAsync(a => a.LeadId == lead.Id && a.Body.StartsWith("Submitted"))));
    }

    [Fact]
    public async Task Existing_student_with_the_same_email_is_linked_instead_of_creating_a_lead()
    {
        var (formId, _) = await TrialFormAsync();
        var student = await StudentAsync(2);
        var kiosk = await _app.SignInAsync(KioskRegistration);
        var answers = TrialAnswers("", first: "Linky", last: "Student-Match");
        answers.RemoveAll(a => a.Key is "f_cellPhone" or "f_email");
        answers.Add(new("f_email", student.Email1!.ToUpperInvariant()));

        var res = await kiosk.PostFormAsync($"/Kiosk/Form/{formId}", $"/Kiosk/Form/{formId}", answers);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var submission = await _app.DbAsync(db => db.FormSubmissions.SingleAsync(s => s.LastName == "Student-Match"));
        Assert.Equal(student.Id, submission.StudentId);
        Assert.Null(submission.LeadId);
        Assert.False(await _app.DbAsync(db => db.Leads.AnyAsync(l => l.LastName == "Student-Match")));
    }

    [Fact]
    public async Task Server_validation_catches_missing_answers_bad_options_and_bad_formats()
    {
        var (formId, _) = await TrialFormAsync();
        var kiosk = await _app.SignInAsync(KioskRegistration);
        var answers = TrialAnswers("(713) 555-0000", last: "Invalidson");
        answers.RemoveAll(a => a.Key is "f_studentFirstName" or "f_customerSignature" or "f_releaseAck" or "f_sex" or "f_email" or "f_dob" or "f_programs");
        answers.AddRange(AreaTestHelpers.Fields(("f_sex", "Robot"), ("f_email", "not-an-email"), ("f_dob", "31/31/2015"), ("f_programs", "Underwater Basket Weaving"),
            ("f_q8TimesPerWeek", "lots"), ("f_guardianSignature", "data:image/png;base64,bm90IGEgcG5n")));

        var res = await kiosk.PostFormAsync($"/Kiosk/Form/{formId}", $"/Kiosk/Form/{formId}", answers);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("Please fill this in.", html);                 // first name
        Assert.Contains("Please sign here.", html);                    // customer signature
        Assert.Contains("Please tick this box to continue.", html);    // release
        Assert.Contains("Please choose from the options shown.", html); // sex / programs
        Assert.Contains("Please enter a valid email address", html);
        Assert.Contains("Please enter a valid date.", html);
        Assert.Contains("Please enter a number.", html);
        Assert.Contains("The signature didn&#x27;t come through", html);
        // What they typed is kept so they only fix the problems.
        Assert.Contains("value=\"Invalidson\"", html);
        Assert.False(await _app.DbAsync(db => db.FormSubmissions.AnyAsync(s => s.LastName == "Invalidson")));
    }

    [Fact]
    public void Signature_larger_than_the_limit_is_rejected()
    {
        var bytes = new byte[BushiBan.Web.Features.Academics.KioskFormService.MaxSignatureBytes + 10];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        var png = BushiBan.Web.Features.Academics.KioskFormService.DecodeSignature("data:image/png;base64," + Convert.ToBase64String(bytes), out var problem);
        Assert.Null(png);
        Assert.Contains("too large", problem);
        Assert.NotNull(BushiBan.Web.Features.Academics.KioskFormService.DecodeSignature(TinyPng, out _));
    }

    [Fact]
    public async Task Inactive_or_missing_forms_send_people_to_the_front_desk()
    {
        var branch = await _app.PasadenaIdAsync();
        var formId = await _app.DbAsync(async db =>
        {
            var f = await db.FormDefinitions.Where(x => x.BranchId == branch && x.Category == FormCategory.Agreement).OrderBy(x => x.Id).FirstAsync();
            f.IsActive = false;
            await db.SaveChangesAsync();
            return f.Id;
        });
        var kiosk = await _app.SignInAsync(KioskRegistration);
        Assert.Contains("available right now", await kiosk.GetStringAsync($"/Kiosk/Form/{formId}"));
        Assert.Contains("available right now", await kiosk.GetStringAsync("/Kiosk/Form/999999"));
        Assert.DoesNotContain($"/Kiosk/Form/{formId}\"", await kiosk.GetStringAsync("/Kiosk/Register"));
    }
}

/// <summary>Own app instance, so the PIN guessing brake doesn't affect other kiosk tests.</summary>
public class KioskPinThrottleTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public KioskPinThrottleTests(TestApp app) => _app = app;

    [Fact]
    public async Task Five_wrong_pins_in_a_minute_lock_the_pad_even_for_a_correct_pin()
    {
        var branch = await _app.PasadenaIdAsync();
        var pin = await _app.DbAsync(db => db.Students.Where(s => s.BranchId == branch && s.IsActive && s.KioskPin != null).Select(s => s.KioskPin!).FirstAsync());
        var kiosk = await _app.SignInAsync("kiosk.attendance@bushiban.local");
        for (var i = 0; i < KioskPinThrottle.MaxFailures; i++)
        {
            var wrong = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["pin"] = "99999" + i })).JsonAsync();
            Assert.Equal("wrong-pin", wrong.GetProperty("kind").GetString());
        }
        var locked = await (await kiosk.PostFormAsync("/Kiosk/Attendance", "/Kiosk/CheckIn", new() { ["pin"] = pin })).JsonAsync();
        Assert.Equal("locked", locked.GetProperty("kind").GetString());
        Assert.InRange(locked.GetProperty("retryAfter").GetInt32(), 1, 60);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void The_lock_lifts_a_minute_after_the_oldest_wrong_pin()
    {
        var clock = new ManualClock();
        var throttle = new KioskPinThrottle(clock);
        for (var i = 0; i < KioskPinThrottle.MaxFailures; i++) { throttle.RecordFailure("k"); clock.Now = clock.Now.AddSeconds(5); }
        Assert.NotNull(throttle.RetryAfter("k"));
        Assert.Null(throttle.RetryAfter("another-kiosk"));
        clock.Now = clock.Now.AddSeconds(40);
        Assert.Null(throttle.RetryAfter("k"));
    }
}
