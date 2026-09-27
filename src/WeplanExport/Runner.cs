using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WeplanExport;

public class ScenarioResult
{
    public required string Scenario { get; init; }
    public bool Ok { get; set; }
    public double Seconds { get; set; }
    public List<DownloadInfo> Downloads { get; set; } = new();
    public string? Error { get; set; }
    public string? FailedStep { get; set; }
    public List<string> Artifacts { get; } = new();
}

public class RunOptions
{
    public bool? Headed { get; init; }
    public int? SlowMo { get; init; }
    public bool Trace { get; init; }
    public bool StopOnFail { get; init; }
}

/// <summary>Browser session management and scenario execution.</summary>
public static class Runner
{
    public static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public static async Task<IBrowser> LaunchBrowserAsync(IPlaywright pw, AppConfig config, bool? headed = null, int? slowMo = null)
    {
        var b = config.Browser;
        var options = new BrowserTypeLaunchOptions
        {
            Headless = headed is { } h ? !h : b.Headless,
            SlowMo = slowMo ?? b.SlowMo,
        };
        var exe = b.ExecutablePath ?? Environment.GetEnvironmentVariable("BROWSER_EXECUTABLE");
        if (!string.IsNullOrEmpty(b.Channel)) options.Channel = b.Channel;
        else if (!string.IsNullOrEmpty(exe)) options.ExecutablePath = exe;
        try
        {
            return await pw.Chromium.LaunchAsync(options);
        }
        catch (PlaywrightException e) when (e.Message.Contains("Executable doesn't exist") && options.Channel is null)
        {
            // Fall back to a system Chrome/Edge if Playwright's bundled browser is not installed.
            foreach (var channel in new[] { "chrome", "msedge" })
            {
                try
                {
                    Log($"Bundled Chromium missing, trying installed '{channel}'");
                    options.Channel = channel;
                    return await pw.Chromium.LaunchAsync(options);
                }
                catch (PlaywrightException)
                {
                }
            }
            throw;
        }
    }

    public static async Task<IBrowserContext> NewContextAsync(IBrowser browser, AppConfig config, bool useState = true)
    {
        var options = new BrowserNewContextOptions
        {
            AcceptDownloads = true,
            ViewportSize = new ViewportSize { Width = config.Browser.Viewport.Width, Height = config.Browser.Viewport.Height },
            Locale = config.Browser.Locale,
        };
        if (useState && File.Exists(config.Auth.StorageState)) options.StorageStatePath = config.Auth.StorageState;
        var ctx = await browser.NewContextAsync(options);
        ctx.SetDefaultTimeout(config.Timeouts.Default);
        ctx.SetDefaultNavigationTimeout(config.Timeouts.Navigation);
        return ctx;
    }

    private static async Task<bool> IsLoggedInAsync(IPage page, AppConfig config)
    {
        var url = page.Url.ToLowerInvariant();
        if (config.Auth.LoginUrlMarkers.Any(url.Contains)) return false;
        return await page.Locator(config.Selectors.LoggedInMarker).CountAsync() > 0;
    }

    /// <summary>Log in with WEPLAN_USERNAME / WEPLAN_PASSWORD env vars, if set.</summary>
    private static async Task<bool> AutoLoginAsync(IPage page, AppConfig config)
    {
        var a = config.Auth;
        var user = Environment.GetEnvironmentVariable(a.UsernameEnv);
        var pwd = Environment.GetEnvironmentVariable(a.PasswordEnv);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pwd)) return false;
        Log("Session not valid -> logging in with credentials from environment");
        await page.GotoAsync(config.BaseUrl.TrimEnd('/') + a.LoginPath, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(a.UsernameSelector).First.FillAsync(user);
        await page.Locator(a.PasswordSelector).First.FillAsync(pwd);
        await page.Locator(a.SubmitSelector).First.ClickAsync();
        try
        {
            await page.Locator(config.Selectors.LoggedInMarker).First.WaitForAsync(new() { Timeout = config.Timeouts.Navigation });
        }
        catch (TimeoutException)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(a.StorageState))!);
        await page.Context.StorageStateAsync(new() { Path = a.StorageState });
        return true;
    }

    /// <summary>Open a visible browser, let the user log in, then save the session.</summary>
    public static async Task InteractiveLoginAsync(AppConfig config)
    {
        using var pw = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(pw, config, headed: true);
        var ctx = await NewContextAsync(browser, config, useState: false);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(config.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Log("Log in in the opened browser window, wait until the dashboard is shown,");
        Console.Write("then press ENTER here to save the session... ");
        Console.ReadLine();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.Auth.StorageState))!);
        await ctx.StorageStateAsync(new() { Path = config.Auth.StorageState });
        Log($"Session saved to {config.Auth.StorageState}");
    }

    public static async Task<List<ScenarioResult>> RunAsync(List<Scenario> scenarios, AppConfig config, RunOptions opts)
    {
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outRoot = config.OutputDir;
        var artifactsDir = Path.Combine(outRoot, "_runs", runId);
        var results = new List<ScenarioResult>();

        using var pw = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(pw, config, opts.Headed, opts.SlowMo);
        for (var idx = 0; idx < scenarios.Count; idx++)
        {
            var sc = scenarios[idx];
            Log($"=== [{idx + 1}/{scenarios.Count}] {sc.Name}");
            var ctx = await NewContextAsync(browser, config);
            if (opts.Trace) await ctx.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
            var page = await ctx.NewPageAsync();
            var sctx = new StepContext(page, config, sc, outRoot, Log);
            var res = new ScenarioResult { Scenario = sc.Name };
            var sw = Stopwatch.StartNew();
            string? current = null;
            try
            {
                if (config.Auth.Required) await EnsureSessionAsync(page, config);
                for (var i = 0; i < sc.Steps.Count; i++)
                {
                    var step = sc.Steps[i];
                    current = $"{i + 1}. {step.Name}";
                    if (!Steps.Registry.TryGetValue(step.Name, out var def))
                        throw new StepException($"Unknown step '{step.Name}'. Available: {string.Join(", ", Steps.Registry.Keys.Order())}");
                    Log($"- {current}");
                    await def.Run(sctx, step.Args);
                }
                res.Ok = true;
            }
            catch (Exception e)
            {
                res.Error = $"{e.GetType().Name}: {e.Message}";
                res.FailedStep = current;
                Log($"FAILED at step {current}: {res.Error}");
                if (e is not StepException) Console.Error.WriteLine(e);
                Directory.CreateDirectory(artifactsDir);
                var baseName = Path.Combine(artifactsDir, Safe(sc.Name));
                try
                {
                    await page.ScreenshotAsync(new() { Path = baseName + ".png", FullPage = true });
                    await File.WriteAllTextAsync(baseName + ".html", await page.ContentAsync());
                    res.Artifacts.Add(baseName + ".png");
                    res.Artifacts.Add(baseName + ".html");
                }
                catch (Exception)
                {
                    // page may be closed/crashed; nothing more to capture
                }
            }
            finally
            {
                if (opts.Trace)
                {
                    Directory.CreateDirectory(artifactsDir);
                    var tracePath = Path.Combine(artifactsDir, Safe(sc.Name) + "_trace.zip");
                    await ctx.Tracing.StopAsync(new() { Path = tracePath });
                    res.Artifacts.Add(tracePath);
                }
                res.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
                res.Downloads = sctx.Downloads;
                await ctx.CloseAsync();
            }
            results.Add(res);
            Log($"=== {(res.Ok ? "PASS" : "FAIL")} {sc.Name} ({res.Seconds}s)");
            if (opts.StopOnFail && !res.Ok) break;
        }

        WriteReport(results, artifactsDir);
        return results;
    }

    private static async Task EnsureSessionAsync(IPage page, AppConfig config)
    {
        await page.GotoAsync(config.BaseUrl.TrimEnd('/') + config.StartPath, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        if (await IsLoggedInAsync(page, config)) return;
        if (await AutoLoginAsync(page, config)) return;
        throw new StepException("Not logged in. Run `weplan-export login` first " +
                                $"(or set {config.Auth.UsernameEnv}/{config.Auth.PasswordEnv}).");
    }

    private static string Safe(string s) => Regex.Replace(s, @"[^\w.\-]+", "_").Trim('_');

    private static void WriteReport(List<ScenarioResult> results, string artifactsDir)
    {
        Directory.CreateDirectory(artifactsDir);
        var reportPath = Path.Combine(artifactsDir, "report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine();
        Console.WriteLine($"{"RESULT",-6} {"TIME",7}  SCENARIO / FILE");
        foreach (var r in results)
        {
            Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL"),-6} {r.Seconds,6}s  {r.Scenario}");
            foreach (var d in r.Downloads) Console.WriteLine($"{"",16}-> {d.File} ({d.DataRows} rows)");
            if (r.Error is null) continue;
            Console.WriteLine($"{"",16}!! {r.FailedStep}: {r.Error}");
            foreach (var a in r.Artifacts) Console.WriteLine($"{"",16}   {a}");
        }
        Console.WriteLine($"\n{results.Count(r => r.Ok)}/{results.Count} passed. Report: {reportPath}");
    }
}
