using System.Text.Json;
using System.Text.Json.Serialization;

namespace BushiBan.Web.Services.Forms;

/// <summary>
/// One field on a kiosk form. Forms are stored as a JSON array of these (FormVersion.SchemaJson).
/// "MapsTo" connects a field to a lead/student property so submissions can create leads and pre-fill students.
/// </summary>
public class FormField
{
    public string Key { get; set; } = "";
    /// <summary>heading, notice, text, textarea, number, date, email, tel, select, radio, checkboxes, checkbox, signature, city</summary>
    public string Type { get; set; } = "text";
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    public List<string>? Options { get; set; }
    public string? Help { get; set; }
    public string? Placeholder { get; set; }
    /// <summary>HTML for "notice" fields (agreement wording). Sanitised on save.</summary>
    public string? Html { get; set; }
    /// <summary>"full" or "half" width on wide screens.</summary>
    public string Width { get; set; } = "full";
    /// <summary>Lead/Student property this answer fills, e.g. FirstName, DateOfBirth, Phone1, Email1, CityId.</summary>
    public string? MapsTo { get; set; }

    [JsonIgnore] public bool IsInput => Type is not ("heading" or "notice");
}

public static class FormSchema
{
    public static readonly string[] FieldTypes =
        { "heading", "notice", "text", "textarea", "number", "date", "email", "tel", "select", "radio", "checkboxes", "checkbox", "signature", "city" };

    /// <summary>Properties a field can map to, with friendly labels for the form builder.</summary>
    public static readonly IReadOnlyDictionary<string, string> MapTargets = new Dictionary<string, string>
    {
        ["FirstName"] = "Student first name", ["LastName"] = "Student last name", ["DateOfBirth"] = "Date of birth",
        ["Gender"] = "Sex / gender", ["FatherName"] = "Parent / guardian (father) name", ["MotherName"] = "Mother name",
        ["Phone1"] = "Main phone", ["Phone2"] = "Second phone", ["Email1"] = "Email", ["Address1"] = "Address",
        ["CityId"] = "City", ["ZipCode"] = "Zip code", ["MedicalConcerns"] = "Medical concerns",
        ["AdditionalInformation"] = "Additional information", ["MarketingSource"] = "How they heard about us",
        ["MarketingSourceDetail"] = "Referred by",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static List<FormField> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<FormField>>(json, Json) ?? new();

    public static string Serialize(IEnumerable<FormField> fields) => JsonSerializer.Serialize(fields, Json);

    public static Dictionary<string, JsonElement> ParseData(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json) ?? new();

    public static string SerializeData(Dictionary<string, object?> data) => JsonSerializer.Serialize(data, Json);

    /// <summary>Text value of an answer (arrays are joined with ", ").</summary>
    public static string? Text(this Dictionary<string, JsonElement> data, string key) =>
        !data.TryGetValue(key, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ToString())),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.ToString(),
        };
}
