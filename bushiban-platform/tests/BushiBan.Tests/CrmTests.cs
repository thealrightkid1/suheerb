using System.Net;
using System.Text.Json;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.People;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Tests;

public class CrmTests : IClassFixture<TestApp>
{
    private const string Manager = "manager@bushiban.local";
    private readonly TestApp _app;
    public CrmTests(TestApp app) => _app = app;

    // ---------- Helpers (every test uses its own unique names and phone numbers) ----------

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A phone number no other test or demo record uses (demo data uses 713-555-xxxx).</summary>
    private static string UniquePhone() => $"(832) {Random.Shared.Next(200, 999)}-{Random.Shared.Next(1000, 9999)}";

    private Task<int> BranchIdAsync(string code) => _app.DbAsync(db => db.Branches.Where(b => b.Code == code).Select(b => b.Id).SingleAsync());

    private async Task<Lead> AddLeadAsync(string branchCode = "PAS", Action<Lead>? setup = null)
    {
        var branchId = await BranchIdAsync(branchCode);
        return await _app.DbAsync(async db =>
        {
            var lead = new Lead
            {
                BranchId = branchId, Title = "Ms.", FirstName = "Test", LastName = Unique("Lead"), Phone1 = UniquePhone(),
                InquiryDate = DateOnly.FromDateTime(DateTime.UtcNow), ProspectPhase = ProspectPhase.AppointmentSet,
                CourseId = await db.Courses.Where(c => c.BranchId == branchId).Select(c => (int?)c.Id).FirstAsync(),
            };
            setup?.Invoke(lead);
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            return lead;
        });
    }

    // ---------- Leads ----------

    [Fact]
    public async Task Created_lead_appears_in_the_list()
    {
        var c = await _app.SignInAsync(Manager);
        var last = Unique("Listed");
        var res = await c.PostFormAsync("/Leads/Create", "/Leads/Create", new()
        {
            ["Title"] = "Mr.", ["FirstName"] = "Kenji", ["LastName"] = last, ["IsActive"] = "true",
            ["Phone1Label"] = "Mobile", ["Phone1"] = UniquePhone(), ["InquiryDate"] = "2026-11-02",
            ["ProspectPhase"] = "NoAppointment", ["Source"] = "Website", ["DateOfBirth"] = "2016-05-01",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Leads/Details/", res.Headers.Location!.ToString());

        var lead = await _app.DbAsync(db => db.Leads.SingleAsync(l => l.LastName == last));
        Assert.Equal(LeadSource.Website, lead.Source);
        Assert.Equal(AgeGroup.Kids, lead.AgeGroup); // suggested from the date of birth

        var html = await c.GetStringAsync($"/Leads?q={last}");
        Assert.Contains(last, html);
        Assert.Contains("1 record", html);
    }

    [Fact]
    public async Task Lead_requires_title_name_inquiry_date_and_phase()
    {
        var c = await _app.SignInAsync(Manager);
        var res = await c.PostFormAsync("/Leads/Create", "/Leads/Create", new() { ["FirstName"] = "", ["ProspectPhase"] = "" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("Enter the first name.", html);
        Assert.Contains("Choose a title.", html);
        Assert.Contains("Enter the inquiry date.", html);
    }

    [Fact]
    public async Task Moving_a_card_on_the_board_changes_the_phase_and_records_an_activity()
    {
        var lead = await AddLeadAsync();
        var c = await _app.SignInAsync(Manager);

        var res = await c.PostFormAsync("/Leads/Board", "/Leads/ChangePhase", new() { ["id"] = lead.Id.ToString(), ["phase"] = ((int)ProspectPhase.InTrial).ToString() });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());

        var (phase, activities) = await _app.DbAsync(async db => (
            await db.Leads.Where(l => l.Id == lead.Id).Select(l => l.ProspectPhase).SingleAsync(),
            await db.Activities.Where(a => a.LeadId == lead.Id && a.Type == ActivityType.StageChange).Select(a => a.Body).ToListAsync()));
        Assert.Equal(ProspectPhase.InTrial, phase);
        var body = Assert.Single(activities);
        Assert.Contains("In Trial", body);

        // Enrolling goes through "Convert to student", not the board.
        var enroll = await c.PostFormAsync("/Leads/Board", "/Leads/ChangePhase", new() { ["id"] = lead.Id.ToString(), ["phase"] = ((int)ProspectPhase.Enrolled).ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, enroll.StatusCode);
    }

    [Fact]
    public async Task Converting_a_lead_creates_a_numbered_student_and_links_the_lead()
    {
        var email = Unique("convert") + "@example.com";
        var lead = await AddLeadAsync(setup: l => { l.Email1 = email; l.DateOfBirth = new DateOnly(2014, 2, 3); l.FatherName = "Hiro Tanaka"; l.MedicalConcerns = "Asthma"; });
        var c = await _app.SignInAsync(Manager);
        var (rankId, beltId, courseId) = await _app.DbAsync(async db => (
            await db.Ranks.Where(r => r.BranchId == lead.BranchId && r.Name == "White").Select(r => r.Id).FirstAsync(),
            await db.BeltSizes.Where(b => b.BranchId == lead.BranchId).Select(b => b.Id).FirstAsync(),
            lead.CourseId!.Value));

        var page = await c.GetStringAsync($"/Leads/Convert/{lead.Id}");
        Assert.Contains("Enroll as student", page);

        var res = await c.PostFormAsync($"/Leads/Convert/{lead.Id}", $"/Leads/Convert/{lead.Id}", new()
        {
            ["Form.CourseId"] = courseId.ToString(), ["Form.RankId"] = rankId.ToString(), ["Form.BeltSizeId"] = beltId.ToString(),
            ["Form.EnrollmentDate"] = "2026-11-17", ["Form.Discount"] = "10",
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Students/Details/", res.Headers.Location!.ToString());

        var (converted, student, activities, promotions) = await _app.DbAsync(async db =>
        {
            var l = await db.Leads.SingleAsync(x => x.Id == lead.Id);
            var s = await db.Students.SingleAsync(x => x.Id == l.ConvertedStudentId);
            return (l, s, await db.Activities.CountAsync(a => a.StudentId == s.Id), await db.RankPromotions.CountAsync(p => p.StudentId == s.Id));
        });
        Assert.Equal(ProspectPhase.Enrolled, converted.ProspectPhase);
        Assert.NotNull(converted.ConvertedAt);
        Assert.StartsWith("PAS-", student.StudentNo);
        Assert.Equal(lead.LastName, student.LastName);
        Assert.Equal(email, student.Email1);
        Assert.Equal("Hiro Tanaka", student.FatherName);
        Assert.Equal("Asthma", student.MedicalConcerns);
        Assert.Equal(lead.Phone1, student.Phone1);
        Assert.Equal(lead.Id, student.LeadId);
        Assert.Equal(ProspectPhase.Enrolled, student.ProspectPhase);
        Assert.Equal(10m, student.Discount);
        Assert.Matches("^[0-9]{4}$", student.KioskPin!);
        Assert.True(activities >= 1);
        Assert.Equal(1, promotions);

        // Converting again just opens the existing student.
        var again = await c.GetAsync($"/Leads/Convert/{lead.Id}");
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains($"/Students/Details/{student.Id}", again.Headers.Location!.ToString());

        var profile = await c.GetStringAsync($"/Students/Details/{student.Id}");
        Assert.Contains(student.StudentNo, profile);
    }

    [Fact]
    public async Task Creating_a_lead_with_a_known_phone_warns_about_a_possible_duplicate()
    {
        var phone = UniquePhone();
        await AddLeadAsync(setup: l => l.Phone1 = phone);
        var c = await _app.SignInAsync(Manager);
        var last = Unique("Dup");
        var digitsOnly = new string(phone.Where(char.IsDigit).ToArray());
        var fields = new Dictionary<string, string>
        {
            ["Title"] = "Mrs.", ["FirstName"] = "Sam", ["LastName"] = last, ["IsActive"] = "true",
            ["Phone1"] = $"+1 {digitsOnly[..3]}.{digitsOnly[3..6]}.{digitsOnly[6..]}", ["InquiryDate"] = "2026-11-10", ["ProspectPhase"] = "NoAppointment",
        };

        var res = await c.PostFormAsync("/Leads/Create", "/Leads/Create", fields);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("might be someone you already have", await res.Content.ReadAsStringAsync());
        Assert.False(await _app.DbAsync(db => db.Leads.AnyAsync(l => l.LastName == last)));

        // It's only a warning: "Save anyway" goes through.
        fields["IgnoreDuplicates"] = "true";
        res = await c.PostFormAsync("/Leads/Create", "/Leads/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.True(await _app.DbAsync(db => db.Leads.AnyAsync(l => l.LastName == last)));

        // The live check used by the editor finds it as well.
        var json = await c.GetStringAsync($"/Search/Duplicates?phone={Uri.EscapeDataString(phone)}");
        Assert.Contains("\"kind\":\"Lead\"", json);
    }

    [Fact]
    public async Task Follow_ups_list_shows_due_leads_and_logging_a_call_moves_the_date()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lead = await AddLeadAsync(setup: l => l.NextFollowUpDate = today.AddDays(-3));
        var c = await _app.SignInAsync(Manager);

        var html = await c.GetStringAsync("/Leads/FollowUps");
        Assert.Contains(lead.LastName, html);
        Assert.Contains("overdue", html);

        var next = today.AddDays(5).ToString("yyyy-MM-dd");
        var res = await c.PostFormAsync("/Leads/FollowUps", $"/Leads/LogFollowUp/{lead.Id}", new() { ["Type"] = "Call", ["Body"] = "Left a voicemail", ["NextFollowUpDate"] = next });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var (date, logged) = await _app.DbAsync(async db => (
            await db.Leads.Where(l => l.Id == lead.Id).Select(l => l.NextFollowUpDate).SingleAsync(),
            await db.Activities.AnyAsync(a => a.LeadId == lead.Id && a.Type == ActivityType.Call && a.Body.StartsWith("Left a voicemail"))));
        Assert.Equal(today.AddDays(5), date);
        Assert.True(logged);
    }

    [Fact]
    public async Task Notes_and_follow_up_tasks_go_on_the_timeline()
    {
        var lead = await AddLeadAsync();
        var c = await _app.SignInAsync(Manager);
        var res = await c.PostFormAsync($"/Leads/Details/{lead.Id}", $"/Leads/AddActivity/{lead.Id}",
            new() { ["Type"] = "FollowUp", ["Body"] = "Send trial schedule", ["DueAt"] = "2026-12-01T10:00" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var task = await _app.DbAsync(db => db.Activities.SingleAsync(a => a.LeadId == lead.Id && a.Type == ActivityType.FollowUp));
        Assert.NotNull(task.DueAt);

        res = await c.PostFormAsync($"/Leads/Details/{lead.Id}", $"/Leads/CompleteTask/{lead.Id}?activityId={task.Id}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotNull(await _app.DbAsync(db => db.Activities.Where(a => a.Id == task.Id).Select(a => a.CompletedAt).SingleAsync()));
        Assert.Contains("Send trial schedule", await c.GetStringAsync($"/Leads/Details/{lead.Id}"));
    }

    // ---------- Students ----------

    [Fact]
    public async Task Creating_a_student_assigns_a_number_and_a_pin_and_enforces_student_rules()
    {
        var c = await _app.SignInAsync(Manager);
        var branchId = await BranchIdAsync("PAS");
        var (courseId, rankId, beltId, cityId) = await _app.DbAsync(async db => (
            await db.Courses.Where(x => x.BranchId == branchId).Select(x => x.Id).FirstAsync(),
            await db.Ranks.Where(x => x.BranchId == branchId).Select(x => x.Id).FirstAsync(),
            await db.BeltSizes.Where(x => x.BranchId == branchId).Select(x => x.Id).FirstAsync(),
            await db.Cities.Select(x => x.Id).FirstAsync()));
        var last = Unique("Student");
        var fields = new Dictionary<string, string>
        {
            ["Title"] = "Master", ["FirstName"] = "Aiko", ["LastName"] = last, ["IsActive"] = "true", ["DateOfBirth"] = "2015-06-01",
            ["Gender"] = "2", ["Phone1"] = UniquePhone(), ["Address1"] = "1 Dojo Way", ["CityId"] = cityId.ToString(), ["ZipCode"] = "77505",
            ["CourseId"] = courseId.ToString(), ["RankId"] = rankId.ToString(), ["BeltSizeId"] = beltId.ToString(),
            ["EnrollmentDate"] = "2026-11-17", ["Discount"] = "0",
        };

        // A child needs a parent or guardian name.
        var res = await c.PostFormAsync("/Students/Create", "/Students/Create", fields);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("parent or guardian", await res.Content.ReadAsStringAsync());

        fields["MotherName"] = "Yuki " + last;
        res = await c.PostFormAsync("/Students/Create", "/Students/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var student = await _app.DbAsync(db => db.Students.SingleAsync(s => s.LastName == last));
        Assert.StartsWith("PAS-", student.StudentNo);
        Assert.NotNull(student.KioskPin);
        Assert.Equal(AgeGroup.Kids, student.AgeGroup);

        // Resetting the PIN gives a different, unique 4-digit PIN.
        res = await c.PostFormAsync($"/Students/Details/{student.Id}", $"/Students/ResetPin/{student.Id}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var pin = await _app.DbAsync(db => db.Students.Where(s => s.Id == student.Id).Select(s => s.KioskPin).SingleAsync());
        Assert.Matches("^[0-9]{4}$", pin!);
        Assert.Equal(1, await _app.DbAsync(db => db.Students.CountAsync(s => s.BranchId == branchId && s.KioskPin == pin)));
    }

    // ---------- Registrations ----------

    private async Task<FormSubmission> AddSubmissionAsync(string last, string phone, string email)
    {
        var branchId = await BranchIdAsync("PAS");
        return await _app.DbAsync(async db =>
        {
            var form = await db.FormDefinitions.FirstAsync(f => f.BranchId == branchId && f.Category == FormCategory.Admission);
            var data = new Dictionary<string, object?>
            {
                ["studentFirstName"] = "Rina", ["studentLastName"] = last, ["parentName"] = "Pat " + last, ["dob"] = "2015-03-04",
                ["sex"] = "Female", ["cellPhone"] = phone, ["email"] = email, ["q2HearAbout"] = "Google",
                ["q10HealthExplain"] = "Peanut allergy", ["programs"] = new[] { "After School Program" }, ["releaseAck"] = "true",
            };
            var sub = new FormSubmission
            {
                BranchId = branchId, FormDefinitionId = form.Id, FormVersionId = form.PublishedVersionId!.Value,
                SubmittedAt = DateTime.UtcNow.AddHours(-2), DataJson = JsonSerializer.Serialize(data),
                FirstName = "Rina", LastName = last, Phone = phone, Email = email, Status = SubmissionStatus.New,
            };
            sub.Signatures.Add(new SubmissionSignature { FieldKey = "customerSignature", Png = new byte[] { 0x89, 0x50, 0x4E, 0x47 }, SignedAt = DateTime.UtcNow });
            db.FormSubmissions.Add(sub);
            await db.SaveChangesAsync();
            return sub;
        });
    }

    [Fact]
    public async Task Creating_a_lead_from_a_registration_maps_the_answers()
    {
        var last = Unique("Kiosk");
        var phone = UniquePhone();
        var sub = await AddSubmissionAsync(last, phone, last.ToLowerInvariant() + "@example.com");
        var c = await _app.SignInAsync(Manager);

        // Opening it marks it reviewed and shows the answers and signature.
        var html = await c.GetStringAsync($"/Registrations/Details/{sub.Id}");
        Assert.Contains("Peanut allergy", html);
        Assert.Contains($"/Registrations/Signature/{sub.Id}?key=customerSignature", html);
        Assert.Equal(SubmissionStatus.Reviewed, await _app.DbAsync(db => db.FormSubmissions.Where(s => s.Id == sub.Id).Select(s => s.Status).SingleAsync()));
        var sig = await c.GetAsync($"/Registrations/Signature/{sub.Id}?key=customerSignature");
        Assert.Equal("image/png", sig.Content.Headers.ContentType!.MediaType);

        var res = await c.PostFormAsync($"/Registrations/Details/{sub.Id}", $"/Registrations/CreateLead/{sub.Id}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Leads/Details/", res.Headers.Location!.ToString());

        var (lead, linked) = await _app.DbAsync(async db =>
        {
            var s = await db.FormSubmissions.SingleAsync(x => x.Id == sub.Id);
            return (await db.Leads.SingleAsync(l => l.Id == s.LeadId), s);
        });
        Assert.Equal("Rina", lead.FirstName);
        Assert.Equal(last, lead.LastName);
        Assert.Equal(new DateOnly(2015, 3, 4), lead.DateOfBirth);
        Assert.Equal(Gender.Female, lead.Gender);
        Assert.Equal("Pat " + last, lead.FatherName);
        Assert.Equal(phone, lead.Phone1);
        Assert.Equal("Google", lead.MarketingSource);
        Assert.Equal("Peanut allergy", lead.MedicalConcerns);
        Assert.Equal(LeadSource.Kiosk, lead.Source);
        // The inquiry date is the submission date in the branch's time zone.
        Assert.InRange(lead.InquiryDate.DayNumber, DateOnly.FromDateTime(sub.SubmittedAt).DayNumber - 1, DateOnly.FromDateTime(sub.SubmittedAt).DayNumber);
        Assert.Equal(SubmissionStatus.Linked, linked.Status);

        // Enrolling from the same registration uses the lead it created.
        var form = await c.GetStringAsync($"/Students/Create?submissionId={sub.Id}");
        Assert.Contains(last, form);
        Assert.Contains($"value=\"{lead.Id}\"", form);
    }

    [Fact]
    public async Task Registration_can_be_linked_to_an_existing_student_and_archived()
    {
        var last = Unique("Link");
        var sub = await AddSubmissionAsync(last, UniquePhone(), last + "@example.com");
        var studentId = await _app.DbAsync(db => db.Students.Where(s => s.BranchId == sub.BranchId).Select(s => s.Id).FirstAsync());
        var c = await _app.SignInAsync(Manager);

        var res = await c.PostFormAsync($"/Registrations/Details/{sub.Id}", $"/Registrations/Link/{sub.Id}", new() { ["target"] = $"Student:{studentId}" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var linked = await _app.DbAsync(db => db.FormSubmissions.SingleAsync(s => s.Id == sub.Id));
        Assert.Equal(studentId, linked.StudentId);
        Assert.Equal(SubmissionStatus.Linked, linked.Status);

        res = await c.PostFormAsync("/Registrations", $"/Registrations/Archive/{sub.Id}", new());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(SubmissionStatus.Archived, await _app.DbAsync(db => db.FormSubmissions.Where(s => s.Id == sub.Id).Select(s => s.Status).SingleAsync()));
        Assert.DoesNotContain(last, await c.GetStringAsync("/Registrations"));
        Assert.Contains(last, await c.GetStringAsync("/Registrations?status=Archived"));
    }

    // ---------- Security ----------

    [Fact]
    public async Task Leads_from_another_branch_are_not_found()
    {
        var other = await _app.DbAsync(db => db.Branches.Where(b => b.Code != "PAS").Select(b => b.Code).FirstAsync());
        var lead = await AddLeadAsync(other);
        var c = await _app.SignInAsync(Manager);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/Leads/Details/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/Leads/Edit/{lead.Id}")).StatusCode);
        var move = await c.PostFormAsync("/Leads/Board", "/Leads/ChangePhase", new() { ["id"] = lead.Id.ToString(), ["phase"] = "3" });
        Assert.Equal(HttpStatusCode.NotFound, move.StatusCode);
        Assert.Contains("Nothing matches", await c.GetStringAsync($"/Search?q={lead.LastName}"));
    }

    [Fact]
    public async Task Users_without_lead_permission_are_denied()
    {
        // The accountant preset can see students but not leads.
        var c = await _app.SignInAsync("accountant@bushiban.local");
        foreach (var url in new[] { "/Leads", "/Leads/Board", "/Leads/FollowUps", "/Registrations" })
        {
            var res = await c.GetAsync(url);
            Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, url);
            if (res.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", res.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Students")).StatusCode);

        // The dashboard only shows what they may see: money, not leads.
        var home = await c.GetStringAsync("/");
        Assert.Contains("Collected this month", home);
        Assert.DoesNotContain("Follow-ups due", home);
    }

    [Fact]
    public async Task Kiosk_accounts_are_sent_to_their_kiosk_from_the_dashboard()
    {
        var c = await _app.SignInAsync("kiosk.attendance@bushiban.local");
        var res = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Kiosk/Attendance", res.Headers.Location!.ToString());
    }

    // ---------- Every screen renders ----------

    [Fact]
    public async Task Crm_screens_render_for_a_manager()
    {
        var c = await _app.SignInAsync(Manager);
        var studentId = await _app.DbAsync(db => db.Students.Where(s => s.Branch!.Code == "PAS").Select(s => s.Id).FirstAsync());
        var leadId = (await AddLeadAsync()).Id;
        foreach (var url in new[]
        {
            "/", "/Leads", "/Leads?phase=3&active=yes&sort=followup&from=2026-01-01&to=2026-12-31", "/Leads/Board", "/Leads/FollowUps",
            "/Leads/Create", $"/Leads/Details/{leadId}", $"/Leads/Edit/{leadId}",
            "/Students", "/Students?active=all&sort=number&q=PAS", "/Students?q=713", "/Students/Create", $"/Students/Details/{studentId}", $"/Students/Edit/{studentId}",
            $"/Students/Create?leadId={leadId}", "/Registrations", "/Registrations?status=all", "/Search?q=garcia", "/Search?q=555",
        })
        {
            var res = await c.GetAsync(url);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)res.StatusCode}: {(await res.Content.ReadAsStringAsync())[..Math.Min(600, (int)(res.Content.Headers.ContentLength ?? 600))]}");
        }
        var dashboard = await c.GetStringAsync("/");
        Assert.Contains("Active students", dashboard);
        Assert.Contains("checkinsChart", dashboard);
    }
}
