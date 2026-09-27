using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WeplanExport;

public record DateLimits(DateOnly? Min, DateOnly? Max);

/// <summary>Parses 2026-08-31, 31/08/2026, today, today-7d, max, max-30d, min+1m.</summary>
public static partial class DateParser
{
    [GeneratedRegex(@"^(today|yesterday|max|min)\s*(?:([+-])\s*(\d+)\s*([dwm]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeRegex();

    /// <summary>Reads window.dateLimits ({"maxDate":"20260923","minDate":"20250923"}) from the dashboard.</summary>
    public static async Task<DateLimits> GetLimitsAsync(IPage page)
    {
        try
        {
            var lim = await page.EvaluateAsync<JsonElement?>("() => window.dateLimits || null");
            if (lim is not { ValueKind: JsonValueKind.Object } o) return new(null, null);
            return new(Read(o, "minDate"), Read(o, "maxDate"));
        }
        catch (PlaywrightException)
        {
            return new(null, null);
        }

        static DateOnly? Read(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && DateOnly.TryParseExact(v.ToString(), "yyyyMMdd", out var d) ? d : null;
    }

    public static DateOnly Parse(string value, DateLimits limits, DateOnly? today = null)
    {
        var s = value.Trim();
        if (DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "yyyyMMdd" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return exact;
        var m = RelativeRegex().Match(s);
        if (!m.Success) throw new StepException($"Unrecognized date '{value}'");

        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var anchor = m.Groups[1].Value.ToLowerInvariant();
        var d = anchor switch
        {
            "today" => now,
            "yesterday" => now.AddDays(-1),
            "max" => limits.Max ?? throw new StepException("'max' needs window.dateLimits on the page, which was not found"),
            _ => limits.Min ?? throw new StepException("'min' needs window.dateLimits on the page, which was not found"),
        };
        if (!m.Groups[2].Success) return d;
        var n = int.Parse(m.Groups[3].Value) * (m.Groups[2].Value == "-" ? -1 : 1);
        return m.Groups[4].Value.ToLowerInvariant() switch
        {
            "d" => d.AddDays(n),
            "w" => d.AddDays(7 * n),
            _ => d.AddMonths(n),
        };
    }
}
