using System.Text.RegularExpressions;

namespace BushiBan.Web.Features.Academics;

/// <summary>
/// Cleans the HTML staff type into "notice" fields (agreement wording) before it's saved.
/// Notices are rendered as-is on the kiosk, so scripts, event handlers, embedded content
/// and javascript: links are removed. Plain formatting (p, strong, ul, h5, a…) is kept.
/// </summary>
public static partial class FormHtmlSanitizer
{
    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html;
        // Whole elements whose content must never run or render.
        s = DangerousBlocks().Replace(s, "");
        // Stray opening/closing tags of the same kinds (e.g. unclosed <script src=…>).
        s = DangerousTags().Replace(s, "");
        // on…= event handler attributes, quoted or not.
        s = EventAttributes().Replace(s, "");
        // javascript:, vbscript: and data: URLs in href/src/action attributes become harmless.
        s = ScriptUrls().Replace(s, m => $"{m.Groups["attr"].Value}=\"#\"");
        // style attributes can carry expression()/url(javascript:) in old browsers.
        s = StyleWithScript().Replace(s, "");
        return s.Trim();
    }

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|frameset|frame|applet|noscript|template|svg|math)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousBlocks();

    [GeneratedRegex(@"<\s*/?\s*(script|style|iframe|object|embed|frameset|frame|applet|noscript|template|svg|math|form|input|button|textarea|select|link|meta|base)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTags();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttributes();

    [GeneratedRegex(@"(?<attr>href|src|action|formaction|xlink:href)\s*=\s*(""\s*(javascript|vbscript|data):[^""]*""|'\s*(javascript|vbscript|data):[^']*'|(javascript|vbscript|data):[^\s>]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrls();

    [GeneratedRegex(@"\s+style\s*=\s*(""[^""]*(expression|javascript:|url\s*\()[^""]*""|'[^']*(expression|javascript:|url\s*\()[^']*')", RegexOptions.IgnoreCase)]
    private static partial Regex StyleWithScript();
}
