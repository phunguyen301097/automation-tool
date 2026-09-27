using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WeplanExport;

public class ScenarioResult
{
    public required string Scenario { get; init; }
    public bool Ok { get; set; }
    /// <summary>PASS | FAIL | STOPPED | NOT RUN</summary>
    public string Status { get; set; } = "";
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
    /// <summary>Fresh browser context per scenario instead of one shared page (null: config).</summary>
    public bool? Isolated { get; init; }
}

/// <summary>Tracks whether the user closed the browser / pressed Ctrl+C, to stop the whole run.</summary>
public class RunState
{
    public string? StopReason { get; private set; }
    /// <summary>True while the tool itself closes pages/contexts.</summary>
    public bool Closing { get; set; }
    public IPage? CurrentPage { get; set; }

    public void Stop(string reason)
    {
        if (!Closing) StopReason ??= reason;
    }

    public void WatchPage(IPage page) => page.Close += (_, _) => Stop("the browser window was closed");

    public void WatchBrowser(IBrowser browser) => browser.Disconnected += (_, _) => Stop("the browser was closed");
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
        KeepManualDownloads(ctx, config);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(config.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Log("Log in in the opened browser window, wait until the dashboard is shown,");
        Console.Write("then press ENTER here to save the session... ");
        Console.ReadLine();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.Auth.StorageState))!);
        await ctx.StorageStateAsync(new() { Path = config.Auth.StorageState });
        Log($"Session saved to {config.Auth.StorageState}");
    }

    /// <summary>
    /// Playwright stores downloads under a temp GUID name and deletes them when the browser
    /// closes. Save files downloaded by hand in a tool-opened window instead.
    /// </summary>
    public static void KeepManualDownloads(IBrowserContext ctx, AppConfig config)
    {
        var target = Path.Combine(config.OutputDir, "manual");

        void OnPage(IPage page) => page.Download += async (_, download) =>
        {
            Directory.CreateDirectory(target);
            var path = Path.Combine(target, download.SuggestedFilename);
            await download.SaveAsAsync(path);
            Log($"Saved manual download -> {path}");
        };

        foreach (var p in ctx.Pages) OnPage(p);
        ctx.Page += (_, p) => OnPage(p);
    }

    /// <summary>Dump the rendered DOM of the date widget and the result table to help tune selectors.</summary>
    public static async Task<string> InspectAsync(AppConfig config, bool headed, string view)
    {
        var outDir = Path.Combine(config.OutputDir, "_inspect", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);
        var sel = config.Selectors;

        using var pw = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(pw, config, headed);
        var ctx = await NewContextAsync(browser, config);
        KeepManualDownloads(ctx, config);
        var page = await ctx.NewPageAsync();
        await Steps.InstallPopupHandlerAsync(page, config, Log);

        async Task Dump(string name, string? css = null)
        {
            var html = css is null
                ? await page.ContentAsync()
                : await page.EvaluateAsync<string>(
                    "(s) => Array.from(document.querySelectorAll(s)).map(e => e.outerHTML).join('\\n\\n') || 'NOT FOUND'", css);
            await File.WriteAllTextAsync(Path.Combine(outDir, name + ".html"), html);
            await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, name + ".png"), FullPage = true });
            Log($"  wrote {Path.Combine(outDir, name)}.html/.png");
        }

        if (config.Auth.Required) await EnsureSessionAsync(page, config);
        else await page.GotoAsync(config.BaseUrl.TrimEnd('/') + config.StartPath);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
        }
        await page.WaitForTimeoutAsync(3000);
        await Dump("1_page");
        await Dump("2_datepicker", sel.Datepicker);
        // Open the date widget and capture whatever popup appears.
        try
        {
            var root = page.Locator(sel.Datepicker).First;
            var clickable = root.Locator("input, button, [role=button], .form-control, div, span");
            await (await clickable.CountAsync() > 0 ? clickable.First : root).ClickAsync();
            await page.WaitForTimeoutAsync(1500);
            await Dump("3_page_datepicker_open");
            await page.Keyboard.PressAsync("Escape");
        }
        catch (Exception e)
        {
            Log($"  could not open date widget: {e.Message}");
        }
        // Load the result table and capture the area around "Download table".
        try
        {
            var sctx = new StepContext(page, config, new Scenario { Name = "inspect", Steps = new() }, outDir, Log);
            await Steps.ChooseViewAsync(sctx, view);
            await Steps.WaitForTableAsync(sctx, null);
            await Dump("4_results", sel.Results);
            var btn = page.Locator("button, a, [role=button]").Filter(new() { HasText = sel.DownloadButtonText }).First;
            if (await btn.CountAsync() > 0)
            {
                await btn.ClickAsync();
                await page.WaitForTimeoutAsync(1500);
                await Dump("5_download_menu_open");
            }
            else
            {
                Log($"  button '{sel.DownloadButtonText}' not found");
            }
        }
        catch (Exception e)
        {
            Log($"  could not load results: {e.Message}");
            await Dump("4_page_error");
        }
        Log($"Done. Send the folder {outDir} (zip) to adjust selectors.");
        return outDir;
    }

    private static readonly HashSet<string> FirstNavSteps = new() { "goto", "open_menu", "menu" };

    /// <summary>
    /// Run scenarios one after another. By default all scenarios share one browser page (one
    /// window with --headed, a single login check); Isolated gives each a fresh context.
    /// Closing the browser window or pressing Ctrl+C stops the whole run; remaining scenarios
    /// are reported as NOT RUN.
    /// </summary>
    public static async Task<List<ScenarioResult>> RunAsync(List<Scenario> scenarios, AppConfig config, RunOptions opts,
                                                            RunState? state = null)
    {
        state ??= new RunState();
        var isolated = opts.Isolated ?? config.Browser.Isolated;
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outRoot = config.OutputDir;
        var artifactsDir = Path.Combine(outRoot, "_runs", runId);
        var results = new List<ScenarioResult>();

        // Ctrl+C: record the stop and close the page so the running step ends right away.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            state.Stop("interrupted (Ctrl+C)");
            _ = state.CurrentPage?.CloseAsync();
        };
        Console.CancelKeyPress += onCancel;
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync();
            browser = await LaunchBrowserAsync(pw, config, opts.Headed, opts.SlowMo);
            state.WatchBrowser(browser);
            IBrowserContext? ctx = null;
            IPage? page = null;
            if (!isolated)
            {
                ctx = await NewContextAsync(browser, config);
                if (opts.Trace) await ctx.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
                page = await ctx.NewPageAsync();
                state.WatchPage(page);
                state.CurrentPage = page;
                await Steps.InstallPopupHandlerAsync(page, config, Log);
            }
            var sessionChecked = false;
            var previousFailed = false;

            for (var idx = 0; idx < scenarios.Count; idx++)
            {
                var sc = scenarios[idx];
                if (state.StopReason is not null)
                {
                    results.Add(new ScenarioResult { Scenario = sc.Name, Status = "NOT RUN" });
                    continue;
                }
                Log($"=== [{idx + 1}/{scenarios.Count}] {sc.Name}");
                if (isolated)
                {
                    ctx = await NewContextAsync(browser, config);
                    if (opts.Trace) await ctx.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
                    page = await ctx.NewPageAsync();
                    state.WatchPage(page);
                    state.CurrentPage = page;
                    await Steps.InstallPopupHandlerAsync(page, config, Log);
                    sessionChecked = false;
                }
                else if (opts.Trace)
                {
                    await ctx!.Tracing.StartChunkAsync();
                }
                var sctx = new StepContext(page!, config, sc, outRoot, Log);
                var res = new ScenarioResult { Scenario = sc.Name };
                var sw = Stopwatch.StartNew();
                string? current = null;
                try
                {
                    var first = sc.Steps.Count > 0 ? sc.Steps[0].Name : null;
                    if (config.Auth.Required && !sessionChecked)
                    {
                        await EnsureSessionAsync(page!, config);
                        sessionChecked = true;
                    }
                    else if (previousFailed || page!.Url == "about:blank" || first is null || !FirstNavSteps.Contains(first))
                    {
                        // Start from a clean page when the previous scenario broke off midway,
                        // is still blank, or does not navigate by itself.
                        await page!.GotoAsync(config.BaseUrl.TrimEnd('/') + config.StartPath,
                            new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    }
                    for (var i = 0; i < sc.Steps.Count; i++)
                    {
                        var step = sc.Steps[i];
                        current = $"{i + 1}. {step.Name}";
                        if (!Steps.Registry.TryGetValue(step.Name, out var def))
                            throw new StepException($"Unknown step '{step.Name}'. Available: {string.Join(", ", Steps.Registry.Keys.Order())}");
                        Log($"- {current}");
                        if (config.Popups.AutoDismiss && step.Name is not ("dismiss_popups" or "close_popups"))
                            await Steps.DismissPopupsAsync(page!, config, Log);
                        await def.Run(sctx, step.Args);
                    }
                    res.Ok = true;
                    res.Status = "PASS";
                }
                catch (Exception e)
                {
                    res.FailedStep = current;
                    if (state.StopReason is not null)
                    {
                        res.Status = "STOPPED";
                        res.Error = $"Stopped: {state.StopReason}";
                    }
                    else
                    {
                        res.Status = "FAIL";
                        res.Error = $"{e.GetType().Name}: {e.Message}";
                        Log($"FAILED at step {current}: {res.Error}");
                        if (e is not StepException) Console.Error.WriteLine(e);
                        Directory.CreateDirectory(artifactsDir);
                        var baseName = Path.Combine(artifactsDir, Safe(sc.Name));
                        try
                        {
                            await page!.ScreenshotAsync(new() { Path = baseName + ".png", FullPage = true });
                            await File.WriteAllTextAsync(baseName + ".html", await page.ContentAsync());
                            res.Artifacts.Add(baseName + ".png");
                            res.Artifacts.Add(baseName + ".html");
                        }
                        catch (Exception)
                        {
                            // page may be closed/crashed; nothing more to capture
                        }
                    }
                }
                finally
                {
                    res.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    res.Downloads = sctx.Downloads;
                    if (state.StopReason is null)
                    {
                        if (opts.Trace)
                        {
                            Directory.CreateDirectory(artifactsDir);
                            var tracePath = Path.Combine(artifactsDir, Safe(sc.Name) + "_trace.zip");
                            try
                            {
                                if (isolated) await ctx!.Tracing.StopAsync(new() { Path = tracePath });
                                else await ctx!.Tracing.StopChunkAsync(new() { Path = tracePath });
                                res.Artifacts.Add(tracePath);
                            }
                            catch (PlaywrightException)
                            {
                            }
                        }
                        if (isolated)
                        {
                            state.Closing = true;
                            await ctx!.CloseAsync();
                            state.Closing = false;
                        }
                    }
                }
                results.Add(res);
                previousFailed = !res.Ok;
                Log(state.StopReason is not null
                    ? $"=== STOPPED {sc.Name}: {state.StopReason} -> stopping the run"
                    : $"=== {res.Status} {sc.Name} ({res.Seconds}s)");
                if (opts.StopOnFail && !res.Ok) state.Stop("--stop-on-fail");
            }
            // Keep the refreshed session (cookies, "announcement seen" flags) for the next run.
            if (!isolated && page is not null && state.StopReason is null) await SaveSessionAsync(page, config);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            var done = results.Select(r => r.Scenario).ToHashSet();
            results.AddRange(scenarios.Where(s => !done.Contains(s.Name))
                .Select(s => new ScenarioResult { Scenario = s.Name, Status = "NOT RUN" }));
            state.Closing = true;
            try
            {
                if (browser is not null) await browser.CloseAsync();
            }
            catch (Exception)
            {
            }
            pw?.Dispose();
        }

        WriteReport(results, artifactsDir, state.StopReason);
        return results;
    }

    private static async Task SaveSessionAsync(IPage page, AppConfig config)
    {
        if (!config.Auth.Required) return;
        try
        {
            if (!page.IsClosed && await IsLoggedInAsync(page, config))
                await page.Context.StorageStateAsync(new() { Path = config.Auth.StorageState });
        }
        catch (PlaywrightException)
        {
        }
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

    private static void WriteReport(List<ScenarioResult> results, string artifactsDir, string? stopReason)
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
        Console.WriteLine($"{"RESULT",-8} {"TIME",7}  SCENARIO / FILE");
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Status,-8} {r.Seconds,6}s  {r.Scenario}");
            foreach (var d in r.Downloads) Console.WriteLine($"{"",18}-> {d.File} ({d.DataRows} rows)");
            if (r.Status != "FAIL") continue;
            Console.WriteLine($"{"",18}!! {r.FailedStep}: {r.Error}");
            foreach (var a in r.Artifacts) Console.WriteLine($"{"",18}   {a}");
        }
        if (stopReason is not null) Console.WriteLine($"\nRun stopped: {stopReason}.");
        Console.WriteLine($"\n{results.Count(r => r.Ok)}/{results.Count} passed. Report: {reportPath}");
    }
}
