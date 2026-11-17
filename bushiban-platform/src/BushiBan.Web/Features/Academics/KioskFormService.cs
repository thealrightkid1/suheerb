using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Academics;

/// <summary>Answers read from a kiosk form post: what to store, what to show again, and what's wrong.</summary>
public class FormAnswers
{
    /// <summary>Posted values (trimmed), used to refill the form when something needs fixing.</summary>
    public Dictionary<string, List<string>> Posted { get; } = new(StringComparer.Ordinal);
    /// <summary>What goes into FormSubmission.DataJson: field key → string or string[].</summary>
    public Dictionary<string, object?> Data { get; } = new(StringComparer.Ordinal);
    /// <summary>Decoded signature PNGs by field key.</summary>
    public Dictionary<string, byte[]> Signatures { get; } = new(StringComparer.Ordinal);
    /// <summary>Field key → plain-English problem.</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The registration kiosk's server side: loads the published form, validates answers authoritatively
/// (whatever the browser checked), stores the submission with its signatures, and for admission forms
/// links it to an existing lead/student or creates a new lead.
/// </summary>
public class KioskFormService
{
    public const string InputPrefix = "f_";
    /// <summary>City answers keep the city Id under the field key and its display name under key + this suffix.</summary>
    public const string CityNameSuffix = "__name";
    public const int MaxSignatureBytes = 500 * 1024;
    private const string PngDataUrlPrefix = "data:image/png;base64,";
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly AppDbContext _db;
    private readonly BranchClock _clock;
    private readonly LookupService _lookups;
    private readonly ICurrentBranch _branch;

    public KioskFormService(AppDbContext db, BranchClock clock, LookupService lookups, ICurrentBranch branch)
    {
        _db = db; _clock = clock; _lookups = lookups; _branch = branch;
    }

    public record PublishedForm(FormDefinition Definition, FormVersion Version, List<FormField> Fields);

    /// <summary>The form as the kiosk shows it, or null when it's missing, inactive or unpublished.</summary>
    public async Task<PublishedForm?> GetPublishedAsync(int formId, CancellationToken ct = default)
    {
        var def = await _db.FormDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.Id == formId && f.IsActive && f.PublishedVersionId != null, ct);
        if (def is null) return null;
        var version = await _db.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == def.PublishedVersionId && v.FormDefinitionId == def.Id, ct);
        return version is null ? null : new PublishedForm(def, version, FormSchema.Parse(version.SchemaJson));
    }

    /// <summary>Cities grouped by state, with the branch's own state first.</summary>
    public async Task<List<SelectListItem>> CitiesAsync(CancellationToken ct = default)
    {
        var cities = await _lookups.CitiesAsync();
        var homeState = _branch.BranchId is int b
            ? await _db.Branches.Where(x => x.Id == b).Select(x => x.City != null ? x.City.State!.Name : null).FirstOrDefaultAsync(ct)
            : null;
        return cities.OrderBy(c => c.Group?.Name == homeState ? 0 : 1).ToList();
    }

    // ------------------------------------------------------------------ Validation

    public async Task<FormAnswers> ReadAsync(IReadOnlyList<FormField> fields, IFormCollection form, CancellationToken ct = default)
    {
        var answers = new FormAnswers();
        var cityIds = new List<int>();

        foreach (var field in fields.Where(f => f.IsInput))
        {
            var values = form[InputPrefix + field.Key].Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).ToList();
            answers.Posted[field.Key] = values;
            var value = values.FirstOrDefault();

            switch (field.Type)
            {
                case "checkboxes":
                {
                    var allowed = field.Options ?? new List<string>();
                    var picked = values.Distinct().ToList();
                    if (picked.Any(v => !allowed.Contains(v))) answers.Errors[field.Key] = "Please choose from the options shown.";
                    else if (field.Required && picked.Count == 0) answers.Errors[field.Key] = "Please choose at least one.";
                    else if (picked.Count > 0) answers.Data[field.Key] = allowed.Where(picked.Contains).ToArray();
                    break;
                }
                case "checkbox":
                    if (value is not null) answers.Data[field.Key] = "Yes";
                    else if (field.Required) answers.Errors[field.Key] = "Please tick this box to continue.";
                    break;
                case "signature":
                    if (value is null)
                    {
                        if (field.Required) answers.Errors[field.Key] = "Please sign here.";
                        break;
                    }
                    var png = DecodeSignature(value, out var problem);
                    if (png is null) answers.Errors[field.Key] = problem!;
                    else { answers.Signatures[field.Key] = png; answers.Data[field.Key] = "Signed"; }
                    break;
                default:
                    if (value is null)
                    {
                        if (field.Required) answers.Errors[field.Key] = field.Type is "select" or "radio" or "city" ? "Please choose one." : "Please fill this in.";
                        break;
                    }
                    var error = CheckValue(field, value, out var normalized);
                    if (error is not null) { answers.Errors[field.Key] = error; break; }
                    answers.Data[field.Key] = normalized;
                    if (field.Type == "city") cityIds.Add(int.Parse(normalized, CultureInfo.InvariantCulture));
                    break;
            }
        }

        // Cities must be real, active cities; keep the name alongside the Id for display.
        if (cityIds.Count > 0)
        {
            var names = await _db.Cities.Where(c => cityIds.Contains(c.Id) && c.IsActive)
                .Select(c => new { c.Id, Name = c.Name + ", " + c.State!.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            foreach (var field in fields.Where(f => f.Type == "city" && answers.Data.ContainsKey(f.Key)))
            {
                var id = int.Parse((string)answers.Data[field.Key]!, CultureInfo.InvariantCulture);
                if (names.TryGetValue(id, out var name)) answers.Data[field.Key + CityNameSuffix] = name;
                else { answers.Data.Remove(field.Key); answers.Errors[field.Key] = "Please choose a city from the list."; }
            }
        }
        return answers;
    }

    /// <summary>Checks one single-value answer. Returns a message when it's not acceptable.</summary>
    private static string? CheckValue(FormField field, string value, out string normalized)
    {
        normalized = value;
        switch (field.Type)
        {
            case "text" or "tel" or "email" when value.Length > 500:
                return "That's too long. Please shorten it.";
            case "textarea" when value.Length > 4000:
                return "That's too long. Please keep it under 4,000 characters.";
        }
        switch (field.Type)
        {
            case "email":
                if (!MailAddress.TryCreate(value, out var mail) || !mail.Host.Contains('.') || mail.Address != value)
                    return "Please enter a valid email address, like name@example.com.";
                return null;
            case "tel":
                if (value.Any(ch => !(char.IsDigit(ch) || " ()+-.xX#".Contains(ch))) || SubmissionMapper.DigitsOnly(value).Length < 7)
                    return "Please enter a phone number with at least 7 digits.";
                return null;
            case "number":
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return "Please enter a number.";
                normalized = n.ToString(CultureInfo.InvariantCulture);
                return null;
            case "date":
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && !DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    return "Please enter a valid date.";
                if (d.Year < 1900 || d.Year > 2200) return "Please enter a valid date.";
                normalized = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return null;
            case "select" or "radio":
                return field.Options?.Contains(value) == true ? null : "Please choose from the options shown.";
            case "city":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? null : "Please choose a city from the list.";
            default:
                return null;
        }
    }

    /// <summary>Decodes a "data:image/png;base64,…" signature. Returns null with a reason when it's not a usable PNG.</summary>
    public static byte[]? DecodeSignature(string dataUrl, out string? problem)
    {
        problem = null;
        if (!dataUrl.StartsWith(PngDataUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = "The signature didn't come through. Please clear it and sign again.";
            return null;
        }
        var base64 = dataUrl[PngDataUrlPrefix.Length..];
        if (base64.Length > (MaxSignatureBytes / 3 + 1) * 4)
        {
            problem = "The signature is too large. Please clear it and sign again.";
            return null;
        }
        var buffer = new byte[base64.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var length) || length < PngMagic.Length || !buffer.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic))
        {
            problem = "The signature didn't come through. Please clear it and sign again.";
            return null;
        }
        if (length > MaxSignatureBytes)
        {
            problem = "The signature is too large. Please clear it and sign again.";
            return null;
        }
        return buffer[..length];
    }

    // ------------------------------------------------------------------ Submission

    public async Task<FormSubmission> SubmitAsync(PublishedForm form, FormAnswers answers, string? ipAddress, string? kioskUser, CancellationToken ct = default)
    {
        if (!answers.IsValid) throw new InvalidOperationException("Answers have errors; validate before submitting.");
        var dataJson = FormSchema.SerializeData(answers.Data);
        var data = FormSchema.ParseData(dataJson);
        var (first, last, phone, email) = SubmissionMapper.Identity(form.Fields, data);
        var now = DateTime.UtcNow;

        var submission = new FormSubmission
        {
            FormDefinitionId = form.Definition.Id, FormVersionId = form.Version.Id, SubmittedAt = now, DataJson = dataJson,
            FirstName = Cut(first, 80), LastName = Cut(last, 80), Phone = Cut(phone, 30), Email = Cut(email, 150),
            Status = SubmissionStatus.New, IpAddress = Cut(ipAddress, 60), KioskUser = Cut(kioskUser, 150),
        };
        foreach (var (key, png) in answers.Signatures)
            submission.Signatures.Add(new SubmissionSignature { FieldKey = key, Png = png, SignedAt = now });

        if (form.Definition.Category == FormCategory.Admission)
            await LinkToPersonAsync(submission, form, data, ct);

        _db.FormSubmissions.Add(submission);
        await _db.SaveChangesAsync(ct);
        return submission;
    }

    /// <summary>
    /// Admission forms: link to the existing student or lead with the same phone or email, otherwise create a new lead.
    /// Either way staff see a timeline entry, and the submission is marked Linked.
    /// </summary>
    private async Task LinkToPersonAsync(FormSubmission submission, PublishedForm form, Dictionary<string, JsonElement> data, CancellationToken ct)
    {
        var note = $"Submitted {form.Definition.Name} at the kiosk";
        var phone = PhoneKey(submission.Phone);
        var email = string.IsNullOrWhiteSpace(submission.Email) ? null : submission.Email.Trim().ToLowerInvariant();

        if (await FindMatchAsync(_db.Students, phone, email, ct) is int studentId)
        {
            submission.StudentId = studentId;
            submission.Status = SubmissionStatus.Linked;
            _db.Activities.Add(new Activity { StudentId = studentId, Type = ActivityType.System, Body = note });
            return;
        }
        if (await FindMatchAsync(_db.Leads, phone, email, ct) is int leadId)
        {
            submission.LeadId = leadId;
            submission.Status = SubmissionStatus.Linked;
            _db.Activities.Add(new Activity { LeadId = leadId, Type = ActivityType.System, Body = note });
            return;
        }
        if (string.IsNullOrWhiteSpace(submission.FirstName) || string.IsNullOrWhiteSpace(submission.LastName)) return;

        var lead = new Lead
        {
            Source = LeadSource.Kiosk, InquiryDate = _clock.Today, ProspectPhase = ProspectPhase.AppointmentSet,
            CourseId = form.Definition.CourseId,
        };
        SubmissionMapper.ApplyTo(lead, form.Fields, data);
        lead.FirstName = lead.FirstName.Trim();
        lead.LastName = lead.LastName.Trim();
        submission.Lead = lead;
        submission.Status = SubmissionStatus.Linked;
        _db.Leads.Add(lead);
        _db.Activities.Add(new Activity { Lead = lead, Type = ActivityType.System, Body = note });
    }

    /// <summary>Last 10 digits for full numbers (ignores +1 and formatting); null when too short to match safely.</summary>
    private static string? PhoneKey(string? phone)
    {
        var digits = SubmissionMapper.DigitsOnly(phone);
        return digits.Length >= 10 ? digits[^10..] : digits.Length >= 7 ? digits : null;
    }

    private static async Task<int?> FindMatchAsync<T>(IQueryable<T> people, string? phone, string? email, CancellationToken ct) where T : PersonBase
    {
        if (phone is null && email is null) return null;
        var fullNumber = phone?.Length == 10;
        // Phone numbers are stored as typed, e.g. "(713) 555-0142"; strip the usual punctuation in SQL before comparing.
        return await people
            .Where(p =>
                (phone != null && p.Phone1 != null && (fullNumber
                    ? p.Phone1.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(phone)
                    : p.Phone1.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "") == phone)) ||
                (phone != null && p.Phone2 != null && (fullNumber
                    ? p.Phone2.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "").EndsWith(phone)
                    : p.Phone2.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "").Replace("+", "") == phone)) ||
                (email != null && p.Email1 != null && p.Email1.ToLower() == email) ||
                (email != null && p.Email2 != null && p.Email2.ToLower() == email))
            .OrderByDescending(p => p.IsActive).ThenByDescending(p => p.Id)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
