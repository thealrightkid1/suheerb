using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using BushiBan.Web.Domain;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Academics;

public record FormRow(int Id, string Name, FormCategory Category, bool IsActive, string? Program, int? PublishedVersion,
    DateTime? PublishedAt, bool HasDraft, int Submissions)
{
    public bool OnKiosk => IsActive && PublishedVersion is not null;
}

public class FormCreateModel
{
    [Required(ErrorMessage = "Give the form a name."), StringLength(200)] public string Name { get; set; } = "";
    public FormCategory Category { get; set; } = FormCategory.Generic;
    public int? CourseId { get; set; }
    /// <summary>Start from a copy of another form's latest version (draft if it has one, else published).</summary>
    public int? CloneFromId { get; set; }
    public List<SelectListItem> Courses { get; set; } = new();
    public List<SelectListItem> Forms { get; set; } = new();
}

public class FormSettingsModel
{
    [Required(ErrorMessage = "Give the form a name."), StringLength(200)] public string Name { get; set; } = "";
    public FormCategory Category { get; set; }
    public int? CourseId { get; set; }
    public bool IsActive { get; set; }
}

public record FormVersionRow(int Id, int VersionNumber, DateTime? PublishedAt, DateTime? UpdatedAt, bool IsDraft, bool IsLive, int Submissions);

public class FormEditPage
{
    public int Id { get; set; }
    public FormSettingsModel Settings { get; set; } = new();
    public List<SelectListItem> Courses { get; set; } = new();
    /// <summary>The draft's fields, or the published fields when there's no draft yet.</summary>
    public string SchemaJson { get; set; } = "[]";
    public bool HasDraft { get; set; }
    public int? DraftVersionId { get; set; }
    public int? PublishedVersion { get; set; }
    public List<FormVersionRow> Versions { get; set; } = new();
    public int Submissions { get; set; }
}

public class SaveDraftRequest
{
    public List<FormField>? Fields { get; set; }
}

public record SchemaProblem(int Index, string Message);

/// <summary>Cleans and checks a form schema from the builder before it's stored.</summary>
public static partial class FormSchemaRules
{
    public const int MaxFields = 300;
    private static readonly HashSet<string> OptionTypes = new() { "select", "radio", "checkboxes" };
    private static readonly HashSet<string> Unmappable = new() { "heading", "notice", "signature" };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,79}$")]
    private static partial Regex KeyPattern();

    /// <summary>Normalises <paramref name="fields"/> in place and returns any problems (empty when it's fine to save).</summary>
    public static List<SchemaProblem> Normalize(List<FormField> fields)
    {
        var problems = new List<SchemaProblem>();
        if (fields.Count > MaxFields) problems.Add(new(-1, $"A form can have at most {MaxFields} fields."));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var n = i + 1;
            f.Type = (f.Type ?? "").Trim().ToLowerInvariant();
            f.Label = (f.Label ?? "").Trim();
            f.Key = (f.Key ?? "").Trim();
            var name = f.Label.Length > 0 ? $"Field {n} (\"{Short(f.Label)}\")" : $"Field {n}";

            if (!FormSchema.FieldTypes.Contains(f.Type)) { problems.Add(new(i, $"{name}: pick a field type.")); continue; }
            var structural = f.Type is "heading" or "notice";

            if (f.Key.Length == 0 && structural) f.Key = (f.Type == "heading" ? "h_" : "n_") + Guid.NewGuid().ToString("N")[..8];
            if (f.Key.Length == 0) problems.Add(new(i, $"{name}: add a key."));
            else if (!KeyPattern().IsMatch(f.Key) || f.Key.Contains("__"))
                problems.Add(new(i, $"{name}: the key \"{Short(f.Key)}\" can only use letters, numbers and single underscores, and must start with a letter."));
            else if (!keys.Add(f.Key)) problems.Add(new(i, $"{name}: another field already uses the key \"{f.Key}\". Each key must be unique."));

            if (f.Type != "notice" && f.Label.Length == 0) problems.Add(new(i, $"{name}: add a label."));
            if (f.Label.Length > 500) problems.Add(new(i, $"{name}: the label is too long (500 characters at most)."));

            if (OptionTypes.Contains(f.Type))
            {
                f.Options = (f.Options ?? new()).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct().ToList();
                if (f.Options.Count == 0) problems.Add(new(i, $"{name}: add at least one option."));
                if (f.Options.Any(o => o.Length > 200)) problems.Add(new(i, $"{name}: options can be 200 characters at most."));
            }
            else f.Options = null;

            if (f.Type == "notice")
            {
                f.Html = FormHtmlSanitizer.Clean(f.Html);
                if (f.Html.Length == 0) problems.Add(new(i, $"{name}: add the notice text."));
            }
            else f.Html = null;

            f.Help = Trim(f.Help, 300);
            f.Placeholder = Trim(f.Placeholder, 120);
            f.Width = structural || f.Width != "half" ? "full" : "half";
            if (structural) { f.Required = false; f.Help = null; f.Placeholder = null; }

            f.MapsTo = string.IsNullOrWhiteSpace(f.MapsTo) || Unmappable.Contains(f.Type) ? null : f.MapsTo.Trim();
            if (f.MapsTo is not null && !FormSchema.MapTargets.ContainsKey(f.MapsTo))
                problems.Add(new(i, $"{name}: \"{Short(f.MapsTo)}\" isn't something a field can fill in."));
            if (f.MapsTo == "CityId" && f.Type != "city") problems.Add(new(i, $"{name}: only a City field can fill in the city."));
        }
        return problems;
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>A friendly starting point for a brand-new form.</summary>
    public static List<FormField> Starter() => new()
    {
        new() { Key = "h_details", Type = "heading", Label = "Your details", Width = "full" },
        new() { Key = "firstName", Type = "text", Label = "First name", Required = true, Width = "half", MapsTo = "FirstName" },
        new() { Key = "lastName", Type = "text", Label = "Last name", Required = true, Width = "half", MapsTo = "LastName" },
        new() { Key = "phone", Type = "tel", Label = "Phone", Width = "half", MapsTo = "Phone1" },
        new() { Key = "email", Type = "email", Label = "Email", Width = "half", MapsTo = "Email1" },
        new() { Key = "signature", Type = "signature", Label = "Signature", Required = true, Width = "full" },
    };
}
