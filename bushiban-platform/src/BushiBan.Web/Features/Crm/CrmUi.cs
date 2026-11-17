using System.Text.RegularExpressions;
using BushiBan.Web.Domain;

namespace BushiBan.Web.Features.Crm;

/// <summary>Small presentation helpers shared by the CRM views.</summary>
public static partial class CrmUi
{
    /// <summary>Bootstrap badge classes per pipeline phase.</summary>
    public static string PhaseClass(ProspectPhase phase) => phase switch
    {
        ProspectPhase.NoAppointment => "text-bg-secondary",
        ProspectPhase.AppointmentSet => "text-bg-info",
        ProspectPhase.InTrial => "text-bg-primary",
        ProspectPhase.ProgressCheck => "text-bg-primary",
        ProspectPhase.PresentedWaiting => "text-bg-warning",
        ProspectPhase.PresentedDenied => "text-bg-danger",
        ProspectPhase.Enrolled => "text-bg-success",
        _ => "text-bg-light border",
    };

    public static string StatusClass(SubmissionStatus status) => status switch
    {
        SubmissionStatus.New => "text-bg-danger",
        SubmissionStatus.Reviewed => "text-bg-warning",
        SubmissionStatus.Linked => "text-bg-success",
        _ => "text-bg-secondary",
    };

    /// <summary>CSS custom property for a belt badge. Only plain colours are passed through, so a rank colour can't inject CSS.</summary>
    public static string? BeltStyle(string? color) =>
        color is not null && SafeColor().IsMatch(color.Trim()) ? $"--belt: {color.Trim()}" : null;

    [GeneratedRegex("^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,20})$")]
    private static partial Regex SafeColor();

    /// <summary>"tel:" link target with only the characters a dialer understands.</summary>
    public static string TelHref(string phone) => "tel:" + new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());

    /// <summary>Social profile links: accept full URLs, otherwise build one from a handle.</summary>
    public static string? SocialUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return v;
        if (v.Contains('.') && !v.StartsWith('@') && baseUrl.Length == 0) return "https://" + v;
        return baseUrl.Length == 0 ? null : baseUrl + v.TrimStart('@');
    }
}
