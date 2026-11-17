using System.Net;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Features.Academics;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BushiBan.Tests;

public class AcademicsTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public AcademicsTests(TestApp app) => _app = app;

    // ---------------------------------------------------------------- Form builder

    [Fact]
    public async Task Save_draft_then_publish_makes_a_new_version_and_old_submissions_keep_theirs()
    {
        var branch = await _app.PasadenaIdAsync();
        var (formId, v1) = await _app.DbAsync(async db =>
        {
            var f = await db.FormDefinitions.Where(x => x.BranchId == branch && x.Category == FormCategory.ChangeForm).SingleAsync();
            return (f.Id, f.PublishedVersionId!.Value);
        });
        // Someone signed version 1 already.
        var oldSubmission = await _app.DbAsync(async db =>
        {
            var s = new FormSubmission { BranchId = branch, FormDefinitionId = formId, FormVersionId = v1, SubmittedAt = DateTime.UtcNow, DataJson = "{}", FirstName = "Old", LastName = "Signer" };
            db.FormSubmissions.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var v1Schema = await _app.DbAsync(db => db.FormVersions.Where(v => v.Id == v1).Select(v => v.SchemaJson).SingleAsync());

        var c = await _app.SignInAsync("manager@bushiban.local");
        var fields = FormSchema.Parse(v1Schema);
        fields.Add(new FormField { Key = "bestTime", Type = "radio", Label = "Best time to call you", Options = new() { "Morning", "Evening", " " }, Width = "half" });
        fields.Add(new FormField { Type = "notice", Html = "<p onclick=\"steal()\">Read me <a href=\"javascript:alert(1)\">here</a></p><script>alert(1)</script>" });

        var res = await c.PostJsonAsync($"/FormBuilder/Edit/{formId}", $"/FormBuilder/SaveDraft/{formId}", new { fields });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (draftId, published, draftJson) = await _app.DbAsync(async db =>
        {
            var f = await db.FormDefinitions.SingleAsync(x => x.Id == formId);
            return (f.DraftVersionId, f.PublishedVersionId, await db.FormVersions.Where(v => v.Id == f.DraftVersionId).Select(v => v.SchemaJson).SingleAsync());
        });
        Assert.NotNull(draftId);
        Assert.Equal(v1, published); // the kiosk still shows version 1
        Assert.DoesNotContain("<script", draftJson);
        Assert.DoesNotContain("onclick", draftJson);
        Assert.DoesNotContain("javascript:", draftJson);
        var saved = FormSchema.Parse(draftJson);
        Assert.Equal(new[] { "Morning", "Evening" }, saved.Single(f => f.Key == "bestTime").Options);
        Assert.StartsWith("n_", saved.Last().Key);

        // Saving again updates the same draft rather than creating another.
        await c.PostJsonAsync($"/FormBuilder/Edit/{formId}", $"/FormBuilder/SaveDraft/{formId}", new { fields });
        Assert.Equal(draftId, await _app.DbAsync(db => db.FormDefinitions.Where(x => x.Id == formId).Select(x => x.DraftVersionId).SingleAsync()));

        res = await c.PostFormAsync($"/FormBuilder/Edit/{formId}", $"/FormBuilder/Publish/{formId}", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var (def, version) = await _app.DbAsync(async db =>
        {
            var f = await db.FormDefinitions.SingleAsync(x => x.Id == formId);
            return (f, await db.FormVersions.SingleAsync(v => v.Id == f.PublishedVersionId));
        });
        Assert.Equal(draftId, def.PublishedVersionId);
        Assert.Null(def.DraftVersionId);
        Assert.Equal(2, version.VersionNumber);
        Assert.NotNull(version.PublishedAt);
        Assert.Equal(v1, await _app.DbAsync(db => db.FormSubmissions.Where(s => s.Id == oldSubmission).Select(s => s.FormVersionId).SingleAsync()));
        Assert.Equal(v1Schema, await _app.DbAsync(db => db.FormVersions.Where(v => v.Id == v1).Select(v => v.SchemaJson).SingleAsync()));

        // The kiosk now shows the new question, and the preview of version 1 still shows the old form.
        var kiosk = await _app.SignInAsync("kiosk.registration@bushiban.local");
        Assert.Contains("Best time to call you", await kiosk.GetStringAsync($"/Kiosk/Form/{formId}"));
        Assert.DoesNotContain("Best time to call you", await c.GetStringAsync($"/FormBuilder/Preview/{formId}?version={v1}"));

        // Nothing left to publish.
        await c.PostFormAsync($"/FormBuilder/Edit/{formId}", $"/FormBuilder/Publish/{formId}", new Dictionary<string, string>());
        Assert.Equal(draftId, await _app.DbAsync(db => db.FormDefinitions.Where(f => f.Id == formId).Select(f => f.PublishedVersionId).SingleAsync()));
    }

    [Fact]
    public async Task Save_draft_rejects_duplicate_keys_unknown_types_and_missing_options()
    {
        var branch = await _app.PasadenaIdAsync();
        var formId = await _app.DbAsync(db => db.FormDefinitions.Where(x => x.BranchId == branch && x.Category == FormCategory.Agreement).OrderByDescending(x => x.Id).Select(x => x.Id).FirstAsync());
        var c = await _app.SignInAsync("manager@bushiban.local");
        var fields = new[]
        {
            new FormField { Key = "name", Type = "text", Label = "Name" },
            new FormField { Key = "name", Type = "text", Label = "Name again" },
            new FormField { Key = "pick", Type = "select", Label = "Pick one" },
            new FormField { Key = "odd", Type = "hologram", Label = "Odd" },
            new FormField { Key = "where", Type = "text", Label = "Where", MapsTo = "PasswordHash" },
        };
        var res = await c.PostJsonAsync($"/FormBuilder/Edit/{formId}", $"/FormBuilder/SaveDraft/{formId}", new { fields });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = (await res.JsonAsync()).ToString();
        Assert.Contains("already uses the key", body);
        Assert.Contains("add at least one option", body);
        Assert.Contains("pick a field type", body);
        Assert.Contains("isn't something a field can fill in", body.Replace("\\u0027", "'"));
        Assert.Null(await _app.DbAsync(db => db.FormDefinitions.Where(f => f.Id == formId).Select(f => f.DraftVersionId).SingleAsync()));
    }

    [Fact]
    public async Task Create_a_form_by_cloning_reorder_and_delete()
    {
        var branch = await _app.PasadenaIdAsync();
        var source = await _app.DbAsync(db => db.FormDefinitions.Where(x => x.BranchId == branch && x.Category == FormCategory.Admission).Select(x => x.Id).SingleAsync());
        var c = await _app.SignInAsync("manager@bushiban.local");
        var res = await c.PostFormAsync("/FormBuilder/Create", "/FormBuilder/Create",
            new Dictionary<string, string> { ["Name"] = "Summer Camp Registration", ["Category"] = "Generic", ["CloneFromId"] = source.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var created = await _app.DbAsync(db => db.FormDefinitions.SingleAsync(f => f.Name == "Summer Camp Registration"));
        Assert.Null(created.PublishedVersionId);
        var draft = await _app.DbAsync(db => db.FormVersions.SingleAsync(v => v.Id == created.DraftVersionId));
        Assert.Contains("studentFirstName", draft.SchemaJson);

        // Not on the kiosk until published.
        var kiosk = await _app.SignInAsync("kiosk.registration@bushiban.local");
        Assert.DoesNotContain("Summer Camp Registration", await kiosk.GetStringAsync("/Kiosk/Register"));

        // Move it to the top.
        var ids = await _app.DbAsync(db => db.FormDefinitions.Where(f => f.BranchId == branch).OrderBy(f => f.SortOrder).Select(f => f.Id).ToListAsync());
        ids.Remove(created.Id);
        ids.Insert(0, created.Id);
        res = await c.PostFormAsync("/FormBuilder", "/FormBuilder/SaveOrder", ids.Select(id => new KeyValuePair<string, string>("ids", id.ToString())));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, await _app.DbAsync(db => db.FormDefinitions.Where(f => f.Id == created.Id).Select(f => f.SortOrder).SingleAsync()));

        await c.PostFormAsync("/FormBuilder", $"/FormBuilder/Delete/{created.Id}", new Dictionary<string, string>());
        Assert.True(await _app.DbAsync(db => db.FormDefinitions.IgnoreQueryFilters().AnyAsync(f => f.Id == created.Id && f.IsDeleted)));
    }

    // ---------------------------------------------------------------- Programs and ranking systems

    [Fact]
    public async Task Create_program_with_same_time_schedule_and_edit_to_per_day_times()
    {
        var branch = await _app.PasadenaIdAsync();
        var system = await _app.DbAsync(db => db.RankingSystems.Where(s => s.BranchId == branch).Select(s => s.Id).FirstAsync());
        var c = await _app.SignInAsync("manager@bushiban.local");
        // Browsers send the checkbox value before the hidden "false" fallback.
        var res = await c.PostFormAsync("/Courses/Create", "/Courses/Create", AreaTestHelpers.Fields(
            ("Name", "Little Dragons"), ("Description", "Ages 4-6"), ("MinimumTermMonths", "6"), ("IsActive", "true"), ("IsActive", "false"),
            ("RankingSystemIds[0]", system.ToString()), ("ScheduleMode", "same"), ("Days", "Monday"), ("Days", "Wednesday"),
            ("StartTime", "17:30"), ("DurationMinutes", "45"), ("Instructor", "Sensei Kim")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var course = await _app.DbAsync(db => db.Courses.Include(x => x.Schedule).Include(x => x.RankingSystems).AsSplitQuery().SingleAsync(x => x.Name == "Little Dragons"));
        Assert.True(course.IsActive);
        Assert.Equal(6, course.MinimumTermMonths);
        Assert.Equal(system, Assert.Single(course.RankingSystems).RankingSystemId);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }, course.Schedule.Select(s => s.DayOfWeek).OrderBy(d => d));
        Assert.All(course.Schedule, s => { Assert.Equal(new TimeOnly(17, 30), s.StartTime); Assert.Equal(45, s.DurationMinutes); Assert.Equal("Sensei Kim", s.Instructor); });

        var details = await c.GetStringAsync($"/Courses/Details/{course.Id}");
        Assert.Contains("5:30 PM", details);

        res = await c.PostFormAsync($"/Courses/Edit/{course.Id}", $"/Courses/Edit/{course.Id}", AreaTestHelpers.Fields(
            ("Name", "Little Dragons"), ("MinimumTermMonths", "6"), ("IsActive", "false"), ("ScheduleMode", "perDay"),
            ("Rows[0].Day", "Tuesday"), ("Rows[0].StartTime", "16:00"), ("Rows[0].DurationMinutes", "30"),
            ("Rows[1].Day", "Saturday"), ("Rows[1].StartTime", "10:00"), ("Rows[1].DurationMinutes", "60"), ("Rows[1].Instructor", "Coach Lee")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        course = await _app.DbAsync(db => db.Courses.Include(x => x.Schedule).Include(x => x.RankingSystems).AsSplitQuery().SingleAsync(x => x.Id == course.Id));
        Assert.False(course.IsActive);
        Assert.Empty(course.RankingSystems);
        Assert.Equal(2, course.Schedule.Count);
        Assert.Contains(course.Schedule, s => s.DayOfWeek == DayOfWeek.Saturday && s.StartTime == new TimeOnly(10, 0) && s.Instructor == "Coach Lee");
        Assert.Equal(2, await _app.DbAsync(db => db.ClassSchedules.CountAsync(s => s.CourseId == course.Id)));

        // Two classes at the same time on the same day are refused.
        res = await c.PostFormAsync($"/Courses/Edit/{course.Id}", $"/Courses/Edit/{course.Id}", AreaTestHelpers.Fields(
            ("Name", "Little Dragons"), ("MinimumTermMonths", "6"), ("ScheduleMode", "perDay"),
            ("Rows[0].Day", "Tuesday"), ("Rows[0].StartTime", "16:00"), ("Rows[0].DurationMinutes", "30"),
            ("Rows[1].Day", "Tuesday"), ("Rows[1].StartTime", "16:00"), ("Rows[1].DurationMinutes", "30")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("two classes on Tuesday", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Programs_and_ranking_systems_in_use_cannot_be_deleted()
    {
        var branch = await _app.PasadenaIdAsync();
        var (course, system) = await _app.DbAsync(async db => (
            await db.Courses.Where(c => c.BranchId == branch && db.Students.Any(s => s.CourseId == c.Id)).Select(c => c.Id).FirstAsync(),
            await db.RankingSystems.Where(s => s.BranchId == branch).Select(s => s.Id).FirstAsync()));
        var c = await _app.SignInAsync("manager@bushiban.local");
        await c.PostFormAsync("/Courses", $"/Courses/Delete/{course}", new Dictionary<string, string>());
        await c.PostFormAsync("/RankingSystems", $"/RankingSystems/Delete/{system}", new Dictionary<string, string>());
        Assert.False(await _app.DbAsync(db => db.Courses.IgnoreQueryFilters().Where(x => x.Id == course).Select(x => x.IsDeleted).SingleAsync()));
        Assert.False(await _app.DbAsync(db => db.RankingSystems.IgnoreQueryFilters().Where(x => x.Id == system).Select(x => x.IsDeleted).SingleAsync()));
    }

    [Fact]
    public async Task Ranking_system_needs_unique_ranks_and_at_least_one()
    {
        var branch = await _app.PasadenaIdAsync();
        var ranks = await _app.DbAsync(db => db.Ranks.Where(r => r.BranchId == branch).OrderBy(r => r.Id).Select(r => r.Id).Take(2).ToListAsync());
        var c = await _app.SignInAsync("manager@bushiban.local");

        var res = await c.PostFormAsync("/RankingSystems/Create", "/RankingSystems/Create", AreaTestHelpers.Fields(("Name", "Kids Path"), ("IsActive", "true"),
            ("Steps[0].RankId", ranks[0].ToString()), ("Steps[0].DurationInDays", "60"), ("Steps[1].RankId", ranks[0].ToString()), ("Steps[1].DurationInDays", "60")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Each rank can appear only once", await res.Content.ReadAsStringAsync());

        res = await c.PostFormAsync("/RankingSystems/Create", "/RankingSystems/Create", AreaTestHelpers.Fields(("Name", "Kids Path"), ("IsActive", "true")));
        Assert.Contains("Add at least one rank", await res.Content.ReadAsStringAsync());

        res = await c.PostFormAsync("/RankingSystems/Create", "/RankingSystems/Create", AreaTestHelpers.Fields(("Name", "Kids Path"), ("IsActive", "true"), ("IsActive", "false"),
            ("Steps[0].RankId", ranks[1].ToString()), ("Steps[0].DurationInDays", "30"), ("Steps[0].MinimumClasses", "8"),
            ("Steps[1].RankId", ranks[0].ToString()), ("Steps[1].DurationInDays", "45")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var system = await _app.DbAsync(db => db.RankingSystems.Include(s => s.Ranks).SingleAsync(s => s.Name == "Kids Path"));
        Assert.True(system.IsActive);
        Assert.Equal(new[] { (1, ranks[1], 30, 8), (2, ranks[0], 45, 0) },
            system.Ranks.OrderBy(r => r.Index).Select(r => (r.Index, r.RankId, r.DurationInDays, r.MinimumClasses)));
    }

    // ---------------------------------------------------------------- Promotions

    /// <summary>A fresh program on a two-belt path (White 30 days / 3 classes, then Gold) with one student on it.</summary>
    private async Task<(int StudentId, int White, int Gold)> PromotionSetupAsync(string lastName, int classesSinceRank, int daysAtRank)
    {
        var branch = await _app.PasadenaIdAsync();
        var today = AreaTestHelpers.BranchToday();
        return await _app.DbAsync(async db =>
        {
            var white = await db.Ranks.Where(r => r.BranchId == branch && r.Name == "White").Select(r => r.Id).SingleAsync();
            var gold = await db.Ranks.Where(r => r.BranchId == branch && r.Name == "Gold").Select(r => r.Id).SingleAsync();
            var system = new RankingSystem { BranchId = branch, Name = "Promo path " + lastName, Ranks =
            {
                new RankingSystemRank { RankId = white, Index = 1, DurationInDays = 30, MinimumClasses = 3 },
                new RankingSystemRank { RankId = gold, Index = 2, DurationInDays = 60 },
            } };
            var course = new Course { BranchId = branch, Name = "Promo program " + lastName, RankingSystems = { new CourseRankingSystem { RankingSystem = system, Index = 1 } } };
            var student = new Student
            {
                BranchId = branch, StudentNo = "T-" + lastName, FirstName = "Pat", LastName = lastName, Course = course, RankId = white,
                RankSince = today.AddDays(-daysAtRank), EnrollmentDate = today.AddDays(-400), IsActive = true,
            };
            db.Students.Add(student);
            await db.SaveChangesAsync();
            for (var i = 0; i < classesSinceRank; i++)
                db.AttendanceRecords.Add(new AttendanceRecord { BranchId = branch, StudentId = student.Id, CourseId = course.Id, Date = today.AddDays(-i - 1), CheckInTime = new TimeOnly(17, 0) });
            // A class before they reached White doesn't count.
            db.AttendanceRecords.Add(new AttendanceRecord { BranchId = branch, StudentId = student.Id, CourseId = course.Id, Date = today.AddDays(-daysAtRank - 5), CheckInTime = new TimeOnly(17, 0) });
            await db.SaveChangesAsync();
            return (student.Id, white, gold);
        });
    }

    private async Task<PromotionCandidate?> CandidateAsync(int studentId)
    {
        using var scope = _app.Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ICurrentBranch>().Override(await _app.PasadenaIdAsync());
        var overview = await scope.ServiceProvider.GetRequiredService<PromotionService>().OverviewAsync(studentIds: new[] { studentId });
        return overview.Candidates.SingleOrDefault();
    }

    [Fact]
    public async Task Eligibility_needs_both_the_days_and_the_classes()
    {
        var (enoughBoth, _, gold) = await PromotionSetupAsync("Ready", classesSinceRank: 3, daysAtRank: 40);
        var ready = await CandidateAsync(enoughBoth);
        Assert.NotNull(ready);
        Assert.True(ready!.IsReady);
        Assert.Equal(gold, ready.NextRankId);
        Assert.Equal(3, ready.ClassesSince);
        Assert.Equal(40, ready.DaysAtRank);

        var (tooFewClasses, _, _) = await PromotionSetupAsync("FewClasses", classesSinceRank: 2, daysAtRank: 40);
        var almost = await CandidateAsync(tooFewClasses);
        Assert.False(almost!.IsReady);
        Assert.Equal(2.0 / 3, almost.Progress, 3);

        var (tooSoon, _, _) = await PromotionSetupAsync("TooSoon", classesSinceRank: 5, daysAtRank: 25);
        var soon = await CandidateAsync(tooSoon);
        Assert.False(soon!.IsReady);
        Assert.True(soon.Progress >= PromotionService.AlmostReadyThreshold); // 25 of 30 days: "almost ready"
    }

    [Fact]
    public async Task Promote_moves_the_student_up_a_belt_and_records_it()
    {
        var (studentId, white, gold) = await PromotionSetupAsync("Promoted", classesSinceRank: 4, daysAtRank: 35);
        var c = await _app.SignInAsync("manager@bushiban.local");
        var list = await c.GetStringAsync("/Promotions");
        Assert.Contains("Pat Promoted", list);

        var today = AreaTestHelpers.BranchToday();
        var res = await c.PostFormAsync("/Promotions", "/Promotions/Promote", AreaTestHelpers.Fields(
            ("studentIds", studentId.ToString()), ("promotedOn", today.ToString("yyyy-MM-dd")), ("notes", "November test")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var student = await _app.DbAsync(db => db.Students.SingleAsync(s => s.Id == studentId));
        Assert.Equal(gold, student.RankId);
        Assert.Equal(today, student.RankSince);
        var promotion = await _app.DbAsync(db => db.RankPromotions.SingleAsync(p => p.StudentId == studentId));
        Assert.Equal((white, gold, today, "November test"), (promotion.FromRankId!.Value, promotion.ToRankId, promotion.PromotedOn, promotion.Notes));
        Assert.True(await _app.DbAsync(db => db.Activities.AnyAsync(a => a.StudentId == studentId && a.Body.StartsWith("Promoted from White to Gold"))));
        Assert.Contains("Pat Promoted", await c.GetStringAsync("/Promotions/History"));

        // Gold is the top of this path, so there's nothing further to promote to.
        await c.PostFormAsync("/Promotions", "/Promotions/Promote", AreaTestHelpers.Fields(("only", studentId.ToString())));
        Assert.Equal(1, await _app.DbAsync(db => db.RankPromotions.CountAsync(p => p.StudentId == studentId)));
    }

    [Fact]
    public async Task Promoting_needs_the_promote_permission()
    {
        var (studentId, white, _) = await PromotionSetupAsync("NoPermission", classesSinceRank: 4, daysAtRank: 35);
        var frontDesk = await _app.SignInAsync("frontdesk@bushiban.local");
        var res = await frontDesk.PostFormAsync("/Attendance", "/Promotions/Promote", AreaTestHelpers.Fields(("studentIds", studentId.ToString())));
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        Assert.Equal(white, await _app.DbAsync(db => db.Students.Where(s => s.Id == studentId).Select(s => s.RankId).SingleAsync()));
    }
}
