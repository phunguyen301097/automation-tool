using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace WeplanExport;

public record Step(string Name, object? Args);

public class Scenario
{
    public required string Name { get; init; }
    public required List<Step> Steps { get; init; }
    public Dictionary<string, object?> Vars { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public string Source { get; init; } = "";
}

/// <summary>Loads scenario YAML files: vars, before/after, matrix and ${var} templating.</summary>
public static partial class ScenarioLoader
{
    [GeneratedRegex(@"\$\{([\w.]+)\}")]
    private static partial Regex VarRegex();

    public static List<Scenario> Load(IEnumerable<string> paths)
    {
        var result = new List<Scenario>();
        var deserializer = new DeserializerBuilder().Build();
        foreach (var path in paths)
        {
            var doc = Normalize(deserializer.Deserialize<object?>(File.ReadAllText(path))) as Dictionary<string, object?>
                      ?? new Dictionary<string, object?>();
            var fileVars = Map(doc.GetValueOrDefault("vars"));
            var before = List(doc.GetValueOrDefault("before"));
            var after = List(doc.GetValueOrDefault("after"));

            foreach (var rawObj in List(doc.GetValueOrDefault("scenarios")))
            {
                if (rawObj is not Dictionary<string, object?> raw) continue;
                if (Args.IsTrue(raw.GetValueOrDefault("skip"))) continue;
                var rawName = raw.GetValueOrDefault("name")?.ToString() ?? Path.GetFileNameWithoutExtension(path);

                foreach (var combo in ExpandMatrix(Map(raw.GetValueOrDefault("matrix"))))
                {
                    var vars = new Dictionary<string, object?>(fileVars);
                    foreach (var kv in Map(raw.GetValueOrDefault("vars"))) vars[kv.Key] = kv.Value;
                    foreach (var kv in combo) vars[kv.Key] = kv.Value;

                    var name = (string)Render(rawName, vars)!;
                    if (combo.Count > 0 && !rawName.Contains("${"))
                        name += "[" + string.Join(",", combo.Values.Select(v =>
                            v is Dictionary<string, object?> d && d.TryGetValue("name", out var n) ? Args.Str(n) : Args.Str(v))) + "]";
                    vars["scenario"] = name;

                    var steps = before.Concat(List(raw.GetValueOrDefault("steps"))).Concat(after)
                        .Select(s => ToStep(Render(s, vars)))
                        .ToList();
                    result.Add(new Scenario
                    {
                        Name = name,
                        Steps = steps,
                        Vars = vars,
                        Tags = List(raw.GetValueOrDefault("tags")).Select(t => t?.ToString() ?? "").ToList(),
                        Source = path,
                    });
                }
            }
        }
        return result;
    }

    public static List<Scenario> Filter(List<Scenario> scenarios, List<string> only, List<string> tags)
    {
        IEnumerable<Scenario> q = scenarios;
        if (only.Count > 0) q = q.Where(s => only.Any(p => Glob(p).IsMatch(s.Name)));
        if (tags.Count > 0) q = q.Where(s => s.Tags.Intersect(tags).Any());
        return q.ToList();
    }

    public static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    /// <summary>Resolve "a" or a dotted path "a.b" (matrix values may be mappings).</summary>
    public static bool TryLookup(IReadOnlyDictionary<string, object?> vars, string name, out object? value)
    {
        value = null;
        object? current = null;
        var first = true;
        foreach (var part in name.Split('.'))
        {
            if (first)
            {
                if (!vars.TryGetValue(part, out current)) return false;
                first = false;
            }
            else if (current is Dictionary<string, object?> d && d.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return false;
            }
        }
        value = current;
        return true;
    }

    private static string ToText(object? v) =>
        v is List<object?> l ? string.Join(", ", l.Select(ToText)) : Args.Str(v);

    /// <summary>
    /// Recursively substitute ${var} / ${var.key} in strings. A string that is exactly one variable keeps
    /// the value's type (e.g. a list of options). Unknown variables are left as is.
    /// </summary>
    public static object? Render(object? value, IReadOnlyDictionary<string, object?> vars) => value switch
    {
        string s when VarRegex().Match(s) is { Success: true } m && m.Length == s.Length && TryLookup(vars, m.Groups[1].Value, out var whole)
            => whole,
        string s => VarRegex().Replace(s, m => TryLookup(vars, m.Groups[1].Value, out var v) ? ToText(v) : m.Value),
        List<object?> list => list.Select(v => Render(v, vars)).ToList(),
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Render(kv.Value, vars)),
        _ => value,
    };

    /// <summary>Render a template into a string; <paramref name="text"/> converts each scalar value.</summary>
    public static string RenderString(string template, IReadOnlyDictionary<string, object?> vars, Func<string, string>? text = null)
    {
        text ??= t => t;
        object? Convert(object? v) => v switch
        {
            Dictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Convert(kv.Value)),
            List<object?> l => text(ToText(l)),
            _ => text(Args.Str(v)),
        };
        return Args.Str(Render(template, vars.ToDictionary(kv => kv.Key, kv => Convert(kv.Value))));
    }

    private static Step ToStep(object? raw)
    {
        if (raw is not Dictionary<string, object?> map || map.Count != 1)
            throw new StepException($"Each step must be a mapping with a single key, got: {Args.Str(raw)}");
        var (name, args) = map.First();
        return new Step(name, args);
    }

    private static List<Dictionary<string, object?>> ExpandMatrix(Dictionary<string, object?> matrix)
    {
        var combos = new List<Dictionary<string, object?>> { new() };
        foreach (var (key, values) in matrix)
        {
            var options = values is List<object?> l ? l : new List<object?> { values };
            combos = combos.SelectMany(c => options.Select(o => new Dictionary<string, object?>(c) { [key] = o })).ToList();
        }
        return combos;
    }

    /// <summary>YamlDotNet gives Dictionary&lt;object, object&gt;; convert to string keys.</summary>
    private static object? Normalize(object? o) => o switch
    {
        IDictionary<object, object?> d => d.ToDictionary(kv => kv.Key.ToString()!, kv => Normalize(kv.Value)),
        IList<object?> l => l.Select(Normalize).ToList(),
        _ => o,
    };

    private static Dictionary<string, object?> Map(object? o) =>
        o as Dictionary<string, object?> ?? new Dictionary<string, object?>();

    private static List<object?> List(object? o) => o as List<object?> ?? new List<object?>();
}
