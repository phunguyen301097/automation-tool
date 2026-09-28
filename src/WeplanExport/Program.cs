using WeplanExport;
using YamlDotNet.Serialization;

const string Usage = """
Usage: weplan-export [-c config.yaml] <command> [options]

Commands:
  login                         open a browser, log in manually and save the session
  install-browser               download Playwright's Chromium (not needed with browser.channel: chrome)
  inspect [--headed] [--view macro]
                                save rendered HTML + screenshots of the date widget and result table
  run [files...]                run scenarios (default: scenarios/*.yaml)
      -k, --only <glob>         only scenarios whose name matches (repeatable)
      -e, --exclude <glob>      skip scenarios whose name matches (repeatable)
      -t, --tag <tag>           only scenarios with this tag (repeatable)
      --headed                  show the browser
      --slow-mo <ms>            slow down each action
      --trace                   record a Playwright trace per scenario
      -x, --stop-on-fail        stop at the first failed scenario
      --isolated                fresh browser context per scenario (default: one shared page)
      --resume [run]            skip scenarios finished in the latest run (or downloads/_runs/<run>), run the rest
      --order page|market       page: each page for all markets, then the next page (default);
                                market: all pages of one market, then the next market
      --pause <sec|min-max>     rest between exports, e.g. 30-60 (config: throttle.pause_between)
      --market-pause <sec|min-max>
                                rest after finishing a market, e.g. 300-600 (config: throttle.pause_after_market)
      --max-exports <n>         stop after n files; continue later with --resume (config: throttle.max_exports)
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
var exclude = new List<string>();
var tags = new List<string>();
bool headed = false, trace = false, stopOnFail = false, dryRun = false, isolated = false;
int? slowMo = null;
var view = "macro";
string? order = null;
string? resume = null;
string? pause = null, marketPause = null;
int? maxExports = null;
for (var i = 1; i < rest.Count; i++)
{
    switch (rest[i])
    {
        case "-k" or "--only": only.Add(rest[++i]); break;
        case "-e" or "--exclude": exclude.Add(rest[++i]); break;
        case "-t" or "--tag": tags.Add(rest[++i]); break;
        case "--headed": headed = true; break;
        case "--slow-mo": slowMo = int.Parse(rest[++i]); break;
        case "--trace": trace = true; break;
        case "-x" or "--stop-on-fail": stopOnFail = true; break;
        case "--dry-run": dryRun = true; break;
        case "--resume":
            resume = i + 1 < rest.Count && !rest[i + 1].StartsWith('-') && !rest[i + 1].EndsWith(".yaml") ? rest[++i] : "latest";
            break;
        case "--isolated": isolated = true; break;
        case "--view": view = rest[++i]; break;
        case "--order": order = rest[++i]; break;
        case "--pause": pause = rest[++i]; break;
        case "--market-pause": marketPause = rest[++i]; break;
        case "--max-exports": maxExports = int.Parse(rest[++i]); break;
        default: files.Add(rest[i]); break;
    }
}

var config = AppConfig.Load(configPath);
if (pause is not null) config.Throttle.PauseBetween = pause;
if (marketPause is not null) config.Throttle.PauseAfterMarket = marketPause;
if (maxExports is not null) config.Throttle.MaxExports = maxExports.Value;

try
{
    switch (command)
    {
        case "login":
            await Runner.InteractiveLoginAsync(config);
            return 0;

        case "inspect":
            await Runner.InspectAsync(config, headed, view);
            return 0;

        case "install-browser":
            return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

        case "steps":
            foreach (var (name, def) in Steps.Registry.OrderBy(kv => kv.Key))
                Console.WriteLine($"{name,-16} {def.Help}");
            return 0;

        case "list":
            foreach (var s in LoadOrdered())
                Console.WriteLine($"{s.Name,-50} {string.Join(",", s.Tags),-20} {s.Source}");
            return 0;

        case "run":
            ThrottleConfig.Range(config.Throttle.PauseBetween); // fail early on an invalid pause
            ThrottleConfig.Range(config.Throttle.PauseAfterMarket);
            var scenarios = ScenarioLoader.Filter(LoadOrdered(), only, tags, exclude);
            if (scenarios.Count == 0)
            {
                Console.Error.WriteLine("No scenario matched");
                return 1;
            }
            var done = new List<string>();
            if (resume is not null)
            {
                var (report, finished) = Runner.CompletedInLastRun(config.OutputDir, resume);
                if (report is null)
                {
                    Console.Error.WriteLine($"--resume: no earlier run report found in {config.OutputDir}/_runs");
                    return 1;
                }
                done = scenarios.Where(s => finished.Contains(s.Name)).Select(s => s.Name).ToList();
                scenarios = scenarios.Where(s => !finished.Contains(s.Name)).ToList();
                Console.WriteLine($"Resuming from {report}: {done.Count} scenario(s) already done, {scenarios.Count} left.");
                if (scenarios.Count == 0)
                {
                    Console.WriteLine("Nothing left to run.");
                    return 0;
                }
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
            Console.WriteLine($"Running {scenarios.Count} scenario(s):");
            var t = config.Throttle;
            if (ThrottleConfig.Range(t.PauseBetween).Max > 0 || ThrottleConfig.Range(t.PauseAfterMarket).Max > 0 || t.MaxExports > 0)
                Console.WriteLine($"Throttle: pause {Show(t.PauseBetween)}s between exports, {Show(t.PauseAfterMarket)}s after each market, " +
                                  $"max {(t.MaxExports > 0 ? t.MaxExports.ToString() : "unlimited")} file(s) this run");
            foreach (var s in scenarios) Console.WriteLine($"  - {s.Name}");
            Console.WriteLine();
            var results = await Runner.RunAsync(scenarios, config, new RunOptions
            {
                Headed = headed ? true : null,
                SlowMo = slowMo,
                Trace = trace,
                StopOnFail = stopOnFail,
                Isolated = isolated ? true : null,
                Done = done,
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

static string Show(object? pause) => ThrottleConfig.Range(pause) is var (lo, hi) && lo != hi ? $"{lo}-{hi}" : $"{hi}";

List<Scenario> LoadOrdered()
{
    var loaded = ScenarioLoader.Load(ScenarioFiles(files), config.Vars);
    return (order ?? config.RunOrder) == "market"
        ? ScenarioLoader.OrderByMarket(loaded, config.Vars.GetValueOrDefault("markets"))
        : loaded;
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
