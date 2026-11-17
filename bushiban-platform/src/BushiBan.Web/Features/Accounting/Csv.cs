using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace BushiBan.Web.Features.Accounting;

/// <summary>Builds CSV downloads for reports (Excel-friendly: UTF-8 with BOM, plain numbers).</summary>
public sealed class Csv
{
    private readonly StringBuilder _sb = new();

    public Csv Row(params object?[] cells)
    {
        _sb.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
        return this;
    }

    public Csv Blank() => Row();

    private static string Cell(object? v)
    {
        var s = v switch
        {
            null => "",
            decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        // Quote when needed, and stop spreadsheet formula injection.
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public FileContentResult ToFile(string fileName) =>
        new(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_sb.ToString())).ToArray(), "text/csv") { FileDownloadName = fileName };
}
