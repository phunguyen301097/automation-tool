using WeplanExport;
using YamlDotNet.Serialization;

const string Usage = """
Usage: weplan-export [-c config.yaml] <command> [options]

Commands:
  login                         open a browser, log in manually and save the session
  install-browser               download Playwright's Chromium (not needed with browser.channel: chrome)
  run [files...]                run scenarios (default: scenarios/*.yaml)
      -k, --only <glob>         only scenarios whose name matches (repeatable)
      -t, --tag <tag>           only scenarios with this tag (repeatable)
      --headed                  show the browser
      --slow-mo <ms>            slow down each action
      --trace                   record a Playwright trace per scenario
      -x, --stop-on-fail        stop at the first failed scenario
      --dry-run                 only print the expanded scenarios
  list [files...]               list scenarios
  steps                         list available step types
""";

var configPath = "config.yaml";
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "-c" or "--config" && i + 1 < args.Length) configPath = args[++i];
    else rest.Add(args[i]);
}
if (rest.Count == 0 || rest[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return rest.Count == 0 ? 2 : 0;
}

var command = rest[0];
var files = new List<string>();
var only = new List<string>();
var tags = new List<string>();
bool headed = false, trace = false, stopOnFail = false, dryRun = false;
int? slowMo = null;
for (var i = 1; i < rest.Count; i++)
{
    switch (rest[i])
    {
        case "-k" or "--only": only.Add(rest[++i]); break;
        case "-t" or "--tag": tags.Add(rest[++i]); break;
        case "--headed": headed = true; break;
        case "--slow-mo": slowMo = int.Parse(rest[++i]); break;
        case "--trace": trace = true; break;
        case "-x" or "--stop-on-fail": stopOnFail = true; break;
        case "--dry-run": dryRun = true; break;
        default: files.Add(rest[i]); break;
    }
}

var config = AppConfig.Load(configPath);

try
{
    switch (command)
    {
        case "login":
            await Runner.InteractiveLoginAsync(config);
            return 0;

        case "install-browser":
            return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

        case "steps":
            foreach (var (name, def) in Steps.Registry.OrderBy(kv => kv.Key))
                Console.WriteLine($"{name,-16} {def.Help}");
            return 0;

        case "list":
            foreach (var s in ScenarioLoader.Load(ScenarioFiles(files)))
                Console.WriteLine($"{s.Name,-50} {string.Join(",", s.Tags),-20} {s.Source}");
            return 0;

        case "run":
            var scenarios = ScenarioLoader.Filter(ScenarioLoader.Load(ScenarioFiles(files)), only, tags);
            if (scenarios.Count == 0)
            {
                Console.Error.WriteLine("No scenario matched");
                return 1;
            }
            if (dryRun)
            {
                var serializer = new SerializerBuilder().Build();
                foreach (var s in scenarios)
                {
                    Console.WriteLine($"# {s.Name}  ({s.Source})");
                    Console.WriteLine(serializer.Serialize(s.Steps.Select(st => new Dictionary<string, object?> { [st.Name] = st.Args }).ToList()));
                }
                return 0;
            }
            var results = await Runner.RunAsync(scenarios, config, new RunOptions
            {
                Headed = headed ? true : null,
                SlowMo = slowMo,
                Trace = trace,
                StopOnFail = stopOnFail,
            });
            return results.All(r => r.Ok) ? 0 : 1;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'\n\n{Usage}");
            return 2;
    }
}
catch (StepException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static List<string> ScenarioFiles(List<string> patterns)
{
    if (patterns.Count == 0) patterns = new() { "scenarios/*.yaml" };
    var result = new List<string>();
    foreach (var p in patterns)
    {
        if (!p.Contains('*') && !p.Contains('?'))
        {
            result.Add(p);
            continue;
        }
        var dir = Path.GetDirectoryName(p) is { Length: > 0 } d ? d : ".";
        if (Directory.Exists(dir))
            result.AddRange(Directory.GetFiles(dir, Path.GetFileName(p)).Order());
    }
    if (result.Count == 0) throw new StepException("No scenario files found");
    return result;
}
