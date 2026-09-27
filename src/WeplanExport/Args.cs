using System.Globalization;

namespace WeplanExport;

public class StepException(string message) : Exception(message);

/// <summary>Helpers to read loosely-typed step arguments coming from YAML.</summary>
public static class Args
{
    public static string Str(object? v) => v switch
    {
        null => "",
        string s => s,
        List<object?> l => "[" + string.Join(", ", l.Select(Str)) + "]",
        Dictionary<string, object?> d => "{" + string.Join(", ", d.Select(kv => $"{kv.Key}: {Str(kv.Value)}")) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static List<string> StrList(object? v) => v switch
    {
        null => new(),
        List<object?> l => l.Select(Str).ToList(),
        _ => new() { Str(v) },
    };

    public static bool IsTrue(object? v) =>
        v is bool b ? b : Str(v).Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1";

    public static Dictionary<string, object?> Map(object? v, string defaultKey)
    {
        if (v is Dictionary<string, object?> d) return d;
        var m = new Dictionary<string, object?>();
        if (v is not null && Str(v) != "") m[defaultKey] = v;
        return m;
    }

    public static string? Get(this Dictionary<string, object?> m, string key) =>
        m.TryGetValue(key, out var v) && v is not null ? Str(v) : null;

    public static int GetInt(this Dictionary<string, object?> m, string key, int fallback) =>
        m.Get(key) is { } s && int.TryParse(s, out var i) ? i : fallback;

    public static bool GetBool(this Dictionary<string, object?> m, string key, bool fallback) =>
        m.TryGetValue(key, out var v) && v is not null ? IsTrue(v) : fallback;
}
