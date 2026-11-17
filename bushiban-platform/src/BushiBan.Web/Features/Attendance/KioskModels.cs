using BushiBan.Web.Domain;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Attendance;

public record KioskFormTile(int Id, string Name, FormCategory Category);

/// <summary>Everything the shared form renderer (Views/Kiosk/_FormFields.cshtml) needs. Used by the kiosk and the form builder preview.</summary>
public class KioskFormView
{
    public int FormId { get; set; }
    public string Name { get; set; } = "";
    public FormCategory Category { get; set; }
    public List<FormField> Fields { get; set; } = new();
    public Dictionary<string, List<string>> Values { get; set; } = new();
    public Dictionary<string, string> Errors { get; set; } = new();
    public List<SelectListItem> Cities { get; set; } = new();
    /// <summary>Form builder preview: nothing is submitted.</summary>
    public bool Preview { get; set; }
    public string? PreviewLabel { get; set; }

    public string Value(string key) => Values.TryGetValue(key, out var v) ? v.FirstOrDefault() ?? "" : "";
    public bool Has(string key, string option) => Values.TryGetValue(key, out var v) && v.Contains(option);
    public string? Error(string key) => Errors.TryGetValue(key, out var e) ? e : null;
}

public record KioskStudentMatch(int Id, string Name, string? Belt, string? Color);
