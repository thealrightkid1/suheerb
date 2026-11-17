using System.Security.Claims;
using System.Security.Cryptography;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Crm;

/// <summary>A row on a lead or student timeline.</summary>
public record TimelineItem(int Id, ActivityType Type, string Body, DateTime CreatedAt, DateTime? DueAt, DateTime? CompletedAt, string? By)
{
    public bool IsOpenTask => Type == ActivityType.FollowUp && DueAt is not null && CompletedAt is null;

    public string Icon => Type switch
    {
        ActivityType.Call => "telephone",
        ActivityType.Email => "envelope",
        ActivityType.SMS => "chat-dots",
        ActivityType.Meeting => "people",
        ActivityType.FollowUp => "alarm",
        ActivityType.StageChange => "arrow-right-circle",
        ActivityType.System => "gear",
        _ => "sticky",
    };
}

/// <summary>
/// Shared lead/student logic: editor dropdowns and validation, duplicate detection, the activity timeline,
/// kiosk PINs and enrolling a student (from scratch, from a lead or from a kiosk registration).
/// </summary>
public class PeopleService
{
    private readonly AppDbContext _db;
    private readonly LookupService _lookups;
    private readonly NumberSequenceService _numbers;
    private readonly ICurrentBranch _branch;
    private readonly BranchClock _clock;
    private readonly IHttpContextAccessor _http;

    public PeopleService(AppDbContext db, LookupService lookups, NumberSequenceService numbers, ICurrentBranch branch, BranchClock clock, IHttpContextAccessor http)
    {
        _db = db; _lookups = lookups; _numbers = numbers; _branch = branch; _clock = clock; _http = http;
    }

    private string? CurrentUserId => _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public static AgeGroup? SuggestAgeGroup(DateOnly? dob, DateOnly today)
    {
        if (dob is not { } d) return null;
        var age = today.Year - d.Year;
        if (d > today.AddYears(-age)) age--;
        return age < 6 ? AgeGroup.PreSchool : age < 13 ? AgeGroup.Kids : age < 18 ? AgeGroup.Teens : AgeGroup.Adults;
    }

    public static int? Age(DateOnly? dob, DateOnly today)
    {
        if (dob is not { } d) return null;
        var age = today.Year - d.Year;
        if (d > today.AddYears(-age)) age--;
        return age;
    }

    // ---------- Editor ----------

    public async Task LoadOptionsAsync(PersonForm form)
    {
        var o = form.Options;
        o.Courses = await _lookups.CoursesAsync(form.CourseId);
        o.Ranks = await _lookups.RanksAsync(form.RankId);
        o.BeltSizes = await _lookups.BeltSizesAsync(form.BeltSizeId);
        o.Cities = await _lookups.CitiesAsync(form.CityId);
        if (form is LeadForm lead) o.Staff = await _lookups.StaffAsync(lead.AssignedToUserId);
        if (form.IsStudent)
        {
            o.Fees = await _db.Courses.Where(c => c.FeeStructure != null)
                .Select(c => new { c.Id, c.FeeStructure!.Name, c.FeeStructure.AdmissionFee, c.FeeStructure.SecurityFee, c.FeeStructure.MonthlyFee })
                .ToDictionaryAsync(c => c.Id, c => new ProgramFees(c.Name, c.AdmissionFee, c.SecurityFee, c.MonthlyFee));
        }
    }

    /// <summary>Rules beyond the attributes: stricter fields for students, and dropdown ids that must belong to this branch.</summary>
    public async Task ValidateAsync(PersonForm f, ModelStateDictionary ms)
    {
        var today = _clock.Today;
        if (f.DateOfBirth is { } dob && dob > today) ms.AddModelError(nameof(f.DateOfBirth), "The date of birth can't be in the future.");

        if (f is StudentForm s)
        {
            void Require(object? value, string key, string message)
            {
                if (value is null || value is string str && string.IsNullOrWhiteSpace(str)) ms.AddModelError(key, message);
            }
            Require(s.DateOfBirth, nameof(s.DateOfBirth), "Enter the date of birth.");
            Require(s.Gender, nameof(s.Gender), "Choose the gender.");
            Require(s.Phone1, nameof(s.Phone1), "Enter a phone number.");
            Require(s.Address1, nameof(s.Address1), "Enter the street address.");
            Require(s.CityId, nameof(s.CityId), "Choose the city.");
            Require(s.ZipCode, nameof(s.ZipCode), "Enter the zip code.");
            Require(s.CourseId, nameof(s.CourseId), "Choose the program.");
            Require(s.RankId, nameof(s.RankId), "Choose the rank.");
            Require(s.BeltSizeId, nameof(s.BeltSizeId), "Choose the belt size.");
            if (Age(s.DateOfBirth, today) is < 18 && string.IsNullOrWhiteSpace(s.FatherName) && string.IsNullOrWhiteSpace(s.MotherName))
                ms.AddModelError(nameof(s.FatherName), "Students under 18 need a parent or guardian name.");
        }
        if (f is LeadForm l && l.NextFollowUpDate is { } next && l.InquiryDate is { } inq && next < inq)
            ms.AddModelError(nameof(l.NextFollowUpDate), "The next follow-up can't be before the inquiry date.");

        if (f.CourseId is int c && !await _db.Courses.AnyAsync(x => x.Id == c)) ms.AddModelError(nameof(f.CourseId), "Choose a program from the list.");
        if (f.RankId is int r && !await _db.Ranks.AnyAsync(x => x.Id == r)) ms.AddModelError(nameof(f.RankId), "Choose a rank from the list.");
        if (f.BeltSizeId is int b && !await _db.BeltSizes.AnyAsync(x => x.Id == b)) ms.AddModelError(nameof(f.BeltSizeId), "Choose a belt size from the list.");
        if (f.CityId is int city && !await _db.Cities.AnyAsync(x => x.Id == city)) ms.AddModelError(nameof(f.CityId), "Choose a city from the list.");
        if (f is LeadForm { AssignedToUserId: { Length: > 0 } uid } && !await _db.Users.AnyAsync(u => u.Id == uid && u.IsActive))
            ms.AddModelError(nameof(LeadForm.AssignedToUserId), "Choose a staff member from the list.");
    }

    // ---------- Duplicates and search ----------

    /// <summary>The last 10 digits of a phone number, or null when there are too few digits to match on.</summary>
    public static string? PhoneKey(string? phone)
    {
        var digits = SubmissionMapper.DigitsOnly(phone);
        if (digits.Length < 7) return null;
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    /// <summary>People whose phone digits end with <paramref name="phoneKey"/> or whose email matches exactly (case-insensitive).</summary>
    public static IQueryable<T> WhereContactMatches<T>(IQueryable<T> q, IReadOnlyCollection<string> phoneKeys, IReadOnlyCollection<string> emails) where T : PersonBase
    {
        // Strip the usual formatting characters in SQL so "(713) 555-0101" matches "713.555.0101".
        var p1 = phoneKeys.ElementAtOrDefault(0) ?? "\u0001";
        var p2 = phoneKeys.ElementAtOrDefault(1) ?? "\u0001";
        var e1 = emails.ElementAtOrDefault(0) ?? "\u0001";
        var e2 = emails.ElementAtOrDefault(1) ?? "\u0001";
        return q.Where(x =>
            (x.Phone1 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(p1) ||
            (x.Phone1 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(p2) ||
            (x.Phone2 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(p1) ||
            (x.Phone2 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(p2) ||
            (x.Email1 ?? "").ToLower() == e1 || (x.Email1 ?? "").ToLower() == e2 ||
            (x.Email2 ?? "").ToLower() == e1 || (x.Email2 ?? "").ToLower() == e2);
    }

    /// <summary>Digits to match phone numbers on when the search text looks like a phone number; otherwise a value that never matches.</summary>
    private static string PhoneSearchDigits(string term)
    {
        var digits = SubmissionMapper.DigitsOnly(term);
        return digits.Length >= 3 && digits.Length >= term.Count(char.IsLetterOrDigit) - 1 ? digits : "\u0001";
    }

    /// <summary>Students: name, student number, phone digits or email contains the search text.</summary>
    public static IQueryable<Student> WhereSearch(IQueryable<Student> q, string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return q;
        var t = term.Trim();
        var phone = PhoneSearchDigits(t);
        return q.Where(x =>
            (x.FirstName + " " + x.LastName).Contains(t) || x.LastName.Contains(t) || x.StudentNo.Contains(t) ||
            (x.Email1 ?? "").Contains(t) || (x.Email2 ?? "").Contains(t) ||
            (x.Phone1 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").Contains(phone) ||
            (x.Phone2 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").Contains(phone));
    }

    /// <summary>Name, phone digits or email contains the search text.</summary>
    public static IQueryable<T> WhereSearch<T>(IQueryable<T> q, string? term) where T : PersonBase
    {
        if (string.IsNullOrWhiteSpace(term)) return q;
        var t = term.Trim();
        var phone = PhoneSearchDigits(t);
        return q.Where(x =>
            (x.FirstName + " " + x.LastName).Contains(t) || x.LastName.Contains(t) ||
            (x.Email1 ?? "").Contains(t) || (x.Email2 ?? "").Contains(t) ||
            (x.Phone1 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").Contains(phone) ||
            (x.Phone2 ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").Contains(phone));
    }

    public async Task<List<PersonMatch>> FindDuplicatesAsync(IEnumerable<string?> phones, IEnumerable<string?> emails, int? excludeLeadId = null, int? excludeStudentId = null)
    {
        var keys = phones.Select(PhoneKey).OfType<string>().Distinct().Take(2).ToList();
        var mails = emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim().ToLowerInvariant()).Distinct().Take(2).ToList();
        if (keys.Count == 0 && mails.Count == 0) return new();

        var students = await WhereContactMatches(_db.Students.Where(s => s.Id != excludeStudentId), keys, mails)
            .OrderBy(s => s.LastName).Take(10)
            .Select(s => new PersonMatch("Student", s.Id, s.FirstName + " " + s.LastName, s.Phone1, s.Email1,
                s.StudentNo + (s.Course != null ? " · " + s.Course.Name : "") + (s.IsActive ? "" : " · inactive")))
            .ToListAsync();
        var leads = await WhereContactMatches(_db.Leads.Where(l => l.Id != excludeLeadId && l.ConvertedStudentId == null), keys, mails)
            .OrderBy(l => l.LastName).Take(10)
            .Select(l => new { l.Id, Name = l.FirstName + " " + l.LastName, l.Phone1, l.Email1, l.ProspectPhase, l.InquiryDate })
            .ToListAsync();
        return students.Concat(leads.Select(l => new PersonMatch("Lead", l.Id, l.Name, l.Phone1, l.Email1,
            $"{l.ProspectPhase.DisplayName()} · inquired {Format.Date(l.InquiryDate)}"))).ToList();
    }

    // ---------- Timeline ----------

    public async Task<List<TimelineItem>> TimelineAsync(int? leadId, int? studentId, int take = 100)
    {
        var q = _db.Activities.AsQueryable();
        q = leadId is int l ? q.Where(a => a.LeadId == l) : q.Where(a => a.StudentId == studentId);
        return await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take)
            .Select(a => new TimelineItem(a.Id, a.Type, a.Body, a.CreatedAt, a.DueAt, a.CompletedAt,
                _db.Users.Where(u => u.Id == a.UserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault()))
            .ToListAsync();
    }

    public Activity NewActivity(ActivityType type, string body, Lead? lead = null, Student? student = null, DateTime? dueAt = null)
    {
        var a = new Activity { Type = type, Body = body.Length > 4000 ? body[..4000] : body, DueAt = dueAt, UserId = CurrentUserId };
        if (lead is not null) { if (lead.Id > 0) a.LeadId = lead.Id; else a.Lead = lead; }
        if (student is not null) { if (student.Id > 0) a.StudentId = student.Id; else a.Student = student; }
        _db.Activities.Add(a);
        return a;
    }

    /// <summary>Converts a local date/time typed by staff (branch time zone) to UTC for storage.</summary>
    public DateTime LocalToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _clock.TimeZone);

    // ---------- Students ----------

    /// <summary>A random PIN nobody else in the branch uses (4 digits while any are free).</summary>
    public async Task<string> NewKioskPinAsync()
    {
        var used = (await _db.Students.Where(s => s.KioskPin != null).Select(s => s.KioskPin!).ToListAsync()).ToHashSet();
        for (var digits = 4; digits <= 6; digits++)
        {
            int min = (int)Math.Pow(10, digits - 1), max = (int)Math.Pow(10, digits);
            for (var i = 0; i < 200; i++)
            {
                var pin = RandomNumberGenerator.GetInt32(min, max).ToString();
                if (!used.Contains(pin)) return pin;
            }
        }
        throw new InvalidOperationException("No free kiosk PIN is left in this branch.");
    }

    /// <summary>Copies every PersonBase value (not ids of the row itself, branch or audit fields).</summary>
    public static void CopyPersonFields(PersonBase from, PersonBase to)
    {
        foreach (var prop in typeof(PersonBase).GetProperties())
        {
            if (prop.DeclaringType != typeof(PersonBase) || !prop.CanWrite) continue;
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!(type.IsValueType || type == typeof(string))) continue; // skip navigations
            prop.SetValue(to, prop.GetValue(from));
        }
    }

    /// <summary>
    /// Saves a new student with the next student number and a kiosk PIN. When it came from a lead or a kiosk registration,
    /// links them up: the lead is marked enrolled and its history carries over to the student.
    /// </summary>
    public async Task<Student> EnrollAsync(Student student, Lead? lead = null, FormSubmission? submission = null)
    {
        var branchId = _branch.BranchId ?? throw new InvalidOperationException("No branch selected.");
        var code = await _db.Branches.Where(b => b.Id == branchId).Select(b => b.Code).SingleAsync();

        student.StudentNo = await _numbers.NextStudentNoAsync(branchId, code);
        student.KioskPin ??= await NewKioskPinAsync();
        student.ProspectPhase = ProspectPhase.Enrolled;
        if (lead is not null) student.LeadId = lead.Id;
        _db.Students.Add(student);
        if (student.RankId is int rankId)
            _db.RankPromotions.Add(new RankPromotion { Student = student, ToRankId = rankId, PromotedOn = student.RankSince ?? student.EnrollmentDate, Notes = "Starting rank at enrollment." });

        if (lead is not null)
        {
            lead.ConvertedStudent = student;
            lead.ConvertedAt = DateTime.UtcNow;
            var oldPhase = lead.ProspectPhase;
            lead.ProspectPhase = ProspectPhase.Enrolled;
            lead.NextFollowUpDate = null;
            // The lead's notes, calls and stage changes appear on the student's timeline too.
            foreach (var a in await _db.Activities.Where(a => a.LeadId == lead.Id && a.StudentId == null).ToListAsync()) a.Student = student;
            foreach (var s in await _db.FormSubmissions.Where(s => s.LeadId == lead.Id && s.StudentId == null).ToListAsync()) s.Student = student;
            if (oldPhase != ProspectPhase.Enrolled)
                NewActivity(ActivityType.StageChange, $"Phase changed from \"{oldPhase.DisplayName()}\" to \"{ProspectPhase.Enrolled.DisplayName()}\".", lead, student);
            NewActivity(ActivityType.System, $"Converted to student {student.StudentNo}.", lead, student);
        }
        else
        {
            NewActivity(ActivityType.System, $"Enrolled as student {student.StudentNo}.", student: student);
        }

        if (submission is not null)
        {
            submission.Student = student;
            submission.Status = SubmissionStatus.Linked;
            if (submission.LeadId is null && lead is not null) submission.LeadId = lead.Id;
            NewActivity(ActivityType.System, $"Linked kiosk registration #{submission.Id}.", student: student);
        }

        await _db.SaveChangesAsync();
        return student;
    }
}
