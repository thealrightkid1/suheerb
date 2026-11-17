using System.Text.RegularExpressions;

namespace BushiBan.Web.Features.Communications.Messaging;

/// <summary>Fills {{Placeholders}} in message templates.</summary>
public static partial class TemplateRenderer
{
    /// <summary>The placeholders staff can use, with what each one becomes.</summary>
    public static readonly IReadOnlyList<(string Name, string Description, string Sample)> Placeholders = new[]
    {
        ("FirstName", "The person's first name", "Liam"),
        ("LastName", "The person's last name", "Garcia"),
        ("BranchName", "Your branch name", "Pasadena (Deer Park)"),
        ("Program", "The program they're enrolled in or interested in", "Youth Martial Arts"),
        ("Balance", "What the student owes on open invoices (blank for leads)", "$80.00"),
    };

    public static IReadOnlyDictionary<string, string?> SampleValues { get; } =
        Placeholders.ToDictionary(p => p.Name, p => (string?)p.Sample, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Replaces each {{Name}} with its value. Unknown or empty placeholders become blank.</summary>
    public static string Render(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return PlaceholderPattern().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
    }
}

public static class PhoneNumbers
{
    /// <summary>
    /// Normalises a phone number to E.164 for SMS. US numbers ("(713) 555-0123", "713.555.0123", "1-713-555-0123")
    /// become +17135550123. Numbers already starting with + are kept if they have 8–15 digits. Anything else returns null.
    /// </summary>
    public static string? ToE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // Drop an extension ("x123", "ext. 123").
        var ext = Regex.Match(trimmed, @"(?i)\s*(x|ext\.?)\s*\d+$");
        if (ext.Success) trimmed = trimmed[..ext.Index];
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());

        if (trimmed.StartsWith('+'))
            return digits.Length is >= 8 and <= 15 ? "+" + digits : null;
        if (digits.Length == 10 && digits[0] is >= '2' and <= '9') return "+1" + digits;
        if (digits.Length == 11 && digits[0] == '1' && digits[1] is >= '2' and <= '9') return "+" + digits;
        return null;
    }
}

/// <summary>SMS length rules, mirrored in wwwroot/js/compose.js for the live counter.</summary>
public static class SmsLength
{
    private const string Gsm7 = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extended = "^{}\\[~]|€";

    /// <summary>Number of SMS segments the text will be billed as.</summary>
    public static int Segments(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var gsm = text.All(c => Gsm7.Contains(c) || Gsm7Extended.Contains(c));
        if (gsm)
        {
            var units = text.Sum(c => Gsm7Extended.Contains(c) ? 2 : 1);
            return units <= 160 ? 1 : (int)Math.Ceiling(units / 153.0);
        }
        var len = text.Length; // UTF-16 code units, as carriers count them
        return len <= 70 ? 1 : (int)Math.Ceiling(len / 67.0);
    }
}
