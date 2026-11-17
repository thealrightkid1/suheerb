using System.Globalization;
using System.Text.Json;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;

namespace BushiBan.Web.Services.Forms;

/// <summary>
/// Copies kiosk answers onto a lead or student using each field's "MapsTo".
/// Used when a trial form creates a lead, and when staff turn a submission into a student.
/// Only fills empty properties unless <paramref name="overwrite"/> is true.
/// </summary>
public static class SubmissionMapper
{
    public static void ApplyTo(PersonBase person, IEnumerable<FormField> schema, Dictionary<string, JsonElement> data, bool overwrite = false)
    {
        foreach (var field in schema.Where(f => f.IsInput && !string.IsNullOrEmpty(f.MapsTo)))
        {
            var text = data.Text(field.Key)?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var prop = typeof(PersonBase).GetProperty(field.MapsTo!);
            if (prop is null || !prop.CanWrite) continue;
            var current = prop.GetValue(person);
            if (!overwrite && current is not null && !(current is string s && s.Length == 0)) continue;

            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object? value = null;
            if (type == typeof(string)) value = Truncate(text, prop);
            else if (type == typeof(DateOnly) && DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var d)) value = d;
            else if (type == typeof(int) && int.TryParse(text, out var i)) value = i;
            else if (type == typeof(Gender) && Enum.TryParse<Gender>(text, true, out var g)) value = g;
            if (value is not null) prop.SetValue(person, value);
        }
        if (person.DateOfBirth is { } dob && person.AgeGroup is null)
        {
            var age = DateTime.Today.Year - dob.Year;
            person.AgeGroup = age < 6 ? AgeGroup.PreSchool : age < 13 ? AgeGroup.Kids : age < 18 ? AgeGroup.Teens : AgeGroup.Adults;
        }
    }

    /// <summary>The denormalised name/phone/email kept on the submission row for listing and matching.</summary>
    public static (string? First, string? Last, string? Phone, string? Email) Identity(IEnumerable<FormField> schema, Dictionary<string, JsonElement> data)
    {
        string? Get(string target) => schema.Where(f => f.MapsTo == target).Select(f => data.Text(f.Key)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        return (Get("FirstName"), Get("LastName"), Get("Phone1") ?? Get("Phone2"), Get("Email1"));
    }

    private static string Truncate(string text, System.Reflection.PropertyInfo prop)
    {
        var max = prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), true)
            .OfType<System.ComponentModel.DataAnnotations.StringLengthAttribute>().FirstOrDefault()?.MaximumLength;
        return max is int m && text.Length > m ? text[..m] : text;
    }

    /// <summary>Digits only, for matching phone numbers typed in different formats.</summary>
    public static string DigitsOnly(string? phone) => new((phone ?? "").Where(char.IsDigit).ToArray());
}
