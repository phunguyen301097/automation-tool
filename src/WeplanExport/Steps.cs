using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;

namespace WeplanExport;

public class StepContext(IPage page, AppConfig config, Scenario scenario, string outputDir, Action<string> log)
{
    public IPage Page { get; } = page;
    public AppConfig Config { get; } = config;
    public Scenario Scenario { get; } = scenario;
    public string OutputDir { get; } = outputDir;
    public Action<string> Log { get; } = log;
    /// <summary>
    /// Report month defaults to the previous calendar month; set_date overrides it with the month
    /// actually selected (e.g. Topology Stock has no date filter).
    /// </summary>
    public Dictionary<string, object?> Vars { get; } = DefaultVars(scenario.Vars);

    private static Dictionary<string, object?> DefaultVars(Dictionary<string, object?> scenarioVars)
    {
        var today = DateTime.Today;
        var last = new DateTime(today.Year, today.Month, 1).AddDays(-1);
        var vars = new Dictionary<string, object?>
        {
            ["year"] = last.Year.ToString(),
            ["month"] = last.Month.ToString(),
            ["month2"] = last.Month.ToString("00"),
        };
        foreach (var (k, v) in scenarioVars) vars[k] = v;
        return vars;
    }
    public List<DownloadInfo> Downloads { get; } = new();
    public SelectorConfig Sel => Config.Selectors;
    public TimeoutConfig Timeouts => Config.Timeouts;
}

public record DownloadInfo(string File, long Bytes, string Suggested, int DataRows);

/// <summary>All scenario step types. Each step receives the context and the raw YAML argument.</summary>
public static class Steps
{
    public delegate Task StepFn(StepContext ctx, object? args);

    public record StepDef(StepFn Run, string Help);

    public static readonly Dictionary<string, StepDef> Registry = new();

    static Steps()
    {
        Register(GotoAsync, "Open a path or URL: /app/bi/coverage", "goto");
        Register(OpenMenuAsync, "Navigate the sidebar: \"Coverage time\" | [\"Latency\", \"Latency Mobile (Cellular)\"] | /app/bi/signal", "open_menu", "menu");
        Register(SelectCountryAsync, "Select country by name (Cambodia) or code (kh)", "select_country", "country");
        Register(SelectFilterAsync, "{id: carrier_filter | label: \"Technology\", options: [ECONET], clear: true, mode: js|ui, all: true}", "select_filter", "select", "filter");
        Register(FiltersAsync, "Several filters at once: {carrier_filter: [ECONET], coverage_filter: [\"4G\"]}", "filters");
        Register(SetDateAsync, "{from: 2026-08-01, to: 2026-08-31, mode: auto|input|calendar|preset|skip, preset: \"Last 30 days\"}", "set_date", "date");
        Register(ApplyAsync, "Click \"Parameters changed...\" if it is visible", "apply", "run_query");
        Register(ChooseViewAsync, "Click a visualization card: macro | population_range | admin_1 | '#byCountry' | {text: 'Macro data'}", "choose_view", "view");
        Register(WaitForTableAsync, "Wait until results are shown and the table has rows: {min_rows: 1, timeout: 300000}", "wait_for_table", "wait_data");
        Register(DownloadTableAsync, "Click \"Download table\", pick file type, save: {format: xlsx, filename: \"${country}_${date_from}\"}", "download_table", "download");
        Register(ClickAsync, "\"#css\" | {selector: .x} | {text: \"Macro data\"} | {role: button, name: OK}", "click");
        Register(FillAsync, "{selector: \"#x\", value: abc}", "fill");
        Register(PressAsync, "Press a key: Escape", "press");
        Register(WaitAsync, "Wait N milliseconds", "wait");
        Register(WaitForAsync, "Wait for a selector: \"#results\" | {selector, state: visible|hidden|attached}", "wait_for");
        Register(ScreenshotAsync, "Full-page screenshot into downloads/screenshots", "screenshot");
        Register(EvaluateAsync, "Run JavaScript on the page", "js", "evaluate");
        Register(PauseAsync, "Open Playwright Inspector (needs --headed)", "pause");
        Register(SetVarAsync, "Set variables: {name: value}", "set_var");
        Register(DismissPopupsStepAsync, "Close announcement popups (runs automatically before each step)", "dismiss_popups", "close_popups");
    }

    private static void Register(StepFn fn, string help, params string[] names)
    {
        foreach (var n in names) Registry[n] = new StepDef(fn, help);
    }

    // ------------------------------------------------------------------ helpers

    private static string Url(StepContext ctx, string path) =>
        Regex.IsMatch(path, "^(https?|file):", RegexOptions.IgnoreCase)
            ? path
            : ctx.Config.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static async Task WaitPageReadyAsync(StepContext ctx)
    {
        await ctx.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = ctx.Timeouts.Navigation });
        try
        {
            await ctx.Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            // Dashboards may keep long-polling; not fatal.
        }
    }

    public static Regex TextRegex(string text, bool exact = false) =>
        new(exact ? $@"^\s*{Regex.Escape(text.Trim())}\s*$" : Regex.Escape(text.Trim()), RegexOptions.IgnoreCase);

    private static async Task<bool> ClickUpdateIfVisibleAsync(StepContext ctx)
    {
        var btn = ctx.Page.Locator(ctx.Sel.UpdateButton).First;
        if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
        {
            ctx.Log("  'Parameters changed' button visible -> clicking it");
            await btn.ClickAsync();
            return true;
        }
        return false;
    }

    private static async Task CheckErrorAsync(StepContext ctx)
    {
        var err = ctx.Page.Locator(ctx.Sel.ErrorMessage).First;
        if (await err.CountAsync() > 0 && await err.IsVisibleAsync())
        {
            var text = (await err.InnerTextAsync()).Trim();
            if (text.Length > 0) throw new StepException($"Dashboard shows an error: {text}");
        }
    }

    private static string Safe(string s) => Regex.Replace(s, @"[^\w.\-]+", "_").Trim('_');

    /// <summary>Keep spaces and letters; drop only characters Windows does not allow in file names.</summary>
    public static string SafeFileName(string s) => Regex.Replace(s, @"[<>:""/\\|?*\x00-\x1f]+", "_").Trim().TrimEnd('.');

    // ------------------------------------------------------------------ popups

    public static ILocator PopupLocator(IPage page, AppConfig config)
    {
        var ignore = string.Concat(config.Popups.Ignore.Select(i => $":not({i})"));
        var parts = config.Popups.Selector.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);
        return page.Locator(string.Join(", ", parts.Select(p => p + ignore)));
    }

    /// <summary>Pages currently closing a popup (the click would re-trigger the handler).</summary>
    private static readonly HashSet<IPage> Dismissing = new();

    /// <summary>Close visible announcement popups. Returns how many were closed.</summary>
    public static async Task<int> DismissPopupsAsync(IPage page, AppConfig config, Action<string> log)
    {
        lock (Dismissing)
        {
            if (!Dismissing.Add(page)) return 0;
        }
        try
        {
            var loc = PopupLocator(page, config);
            var closed = 0;
            for (var i = 0; i < 5; i++) // a popup may be followed by another one
            {
                var popups = await VisibleAsync(loc);
                if (popups.Count == 0) break;
                var el = popups[0];
                string title;
                try
                {
                    title = Regex.Replace(await el.InnerTextAsync(new() { Timeout = 1000 }), @"\s+", " ").Trim();
                    if (title.Length > 70) title = title[..70];
                }
                catch (PlaywrightException)
                {
                    title = "?";
                }
                var how = await ClosePopupAsync(page, el, config.Popups);
                log($"  closed popup '{title}' ({how})");
                closed++;
            }
            return closed;
        }
        finally
        {
            lock (Dismissing) Dismissing.Remove(page);
        }
    }

    private static async Task<string> ClosePopupAsync(IPage page, ILocator el, PopupConfig pc)
    {
        async Task<bool> Gone()
        {
            try
            {
                await el.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 2000 });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        var buttons = await VisibleAsync(el.Locator(pc.CloseSelector));
        if (buttons.Count == 0)
        {
            var names = string.Join("|", pc.CloseTexts.Select(Regex.Escape));
            buttons = await VisibleAsync(el.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex($@"^\s*(?:{names})\s*$", RegexOptions.IgnoreCase) }));
        }
        if (buttons.Count > 0)
        {
            try
            {
                await buttons[0].ClickAsync(new() { Timeout = 3000 });
                if (await Gone()) return "close button";
            }
            catch (Exception)
            {
            }
        }
        await page.Keyboard.PressAsync("Escape");
        if (await Gone()) return "Escape";
        // Last resort: remove the dialog and any backdrop that still blocks the page.
        await el.EvaluateAsync(@"(el) => {
            (el.closest('.modal, .p-dialog-mask, .swal2-container') || el).remove();
            document.querySelectorAll('.modal-backdrop, .p-dialog-mask, .swal2-container').forEach(b => b.remove());
            document.body.classList.remove('modal-open', 'swal2-shown');
            document.body.style.removeProperty('overflow');
            document.body.style.removeProperty('padding-right');
        }");
        return "removed";
    }

    /// <summary>Let Playwright close popups automatically whenever one blocks an action.</summary>
    public static async Task InstallPopupHandlerAsync(IPage page, AppConfig config, Action<string> log)
    {
        if (!config.Popups.AutoDismiss) return;
        // NoWaitAfter: when the tool itself is clicking the popup's close button the handler does
        // nothing, and Playwright must not wait for the popup to disappear first.
        await page.AddLocatorHandlerAsync(PopupLocator(page, config),
            async _ => await DismissPopupsAsync(page, config, log), new() { NoWaitAfter = true });
    }

    public static async Task DismissPopupsStepAsync(StepContext ctx, object? args)
    {
        if (await DismissPopupsAsync(ctx.Page, ctx.Config, ctx.Log) == 0) ctx.Log("  no popup");
    }

    // ------------------------------------------------------------------ navigation

    public static async Task GotoAsync(StepContext ctx, object? args)
    {
        var path = Args.Map(args, "url").Get("url") ?? throw new StepException("goto needs a path");
        ctx.Log($"  goto {Url(ctx, path)}");
        await ctx.Page.GotoAsync(Url(ctx, path), new() { Timeout = ctx.Timeouts.Navigation, WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitPageReadyAsync(ctx);
    }

    public static async Task OpenMenuAsync(StepContext ctx, object? args)
    {
        var items = Args.StrList(args);
        if (items.Count == 1 && items[0].StartsWith('/'))
        {
            await GotoAsync(ctx, items[0]);
            return;
        }
        var page = ctx.Page;
        ILocator scope = page.Locator(ctx.Sel.Sidebar).First;
        for (var i = 0; i < items.Count; i++)
        {
            var last = i == items.Count - 1;
            var link = scope.Locator("a").Filter(new() { HasTextRegex = TextRegex(items[i], exact: true) }).First;
            if (await link.CountAsync() == 0) throw new StepException($"Menu item '{items[i]}' not found in sidebar");
            var href = await link.GetAttributeAsync("href") ?? "#";
            ctx.Log($"  menu -> {items[i]}" + (href == "#" ? "" : $" ({href})"));
            if (last && href is not ("#" or ""))
            {
                var navigated = new TaskCompletionSource();
                void OnNav(object? _, IFrame f)
                {
                    if (f == page.MainFrame) navigated.TrySetResult();
                }
                page.FrameNavigated += OnNav;
                try
                {
                    await link.ClickAsync();
                    await navigated.Task.WaitAsync(TimeSpan.FromMilliseconds(ctx.Timeouts.Navigation));
                }
                catch (TimeoutException)
                {
                    throw new StepException($"Clicking menu '{items[i]}' did not navigate to {href}");
                }
                finally
                {
                    page.FrameNavigated -= OnNav;
                }
                await WaitPageReadyAsync(ctx);
            }
            else
            {
                await link.ClickAsync();
                await page.WaitForTimeoutAsync(400); // metisMenu expand animation
                scope = link.Locator("xpath=.."); // next level lives inside this item's submenu
            }
        }
    }

    public static async Task SelectCountryAsync(StepContext ctx, object? args)
    {
        var wanted = Args.Map(args, "name").Get("name") ?? throw new StepException("select_country needs a name");
        var page = ctx.Page;
        var sel = ctx.Sel.CountrySelect;
        await page.WaitForSelectorAsync(sel, new() { State = WaitForSelectorState.Attached, Timeout = ctx.Timeouts.Default });
        var info = await page.EvaluateAsync<JsonElement>(@"([sel, wanted]) => {
            const el = document.querySelector(sel);
            const opts = Array.from(el.options);
            const w = String(wanted).trim().toLowerCase();
            const o = opts.find(o => o.value.toLowerCase() === w || o.text.trim().toLowerCase() === w);
            return {current: el.value, match: o ? o.value : null, label: o ? o.text.trim() : null,
                    available: opts.map(o => o.text.trim())};
        }", new object[] { sel, wanted });
        var match = info.GetProperty("match").GetString();
        if (match is null)
            throw new StepException($"Country '{wanted}' not available. Options: {info.GetProperty("available")}");
        var label = info.GetProperty("label").GetString();
        ctx.Vars["country"] = label;
        ctx.Vars["country_code"] = match;
        if (info.GetProperty("current").GetString() == match)
        {
            ctx.Log($"  country already {label}");
            return;
        }
        ctx.Log($"  country -> {label}");
        var sw = Stopwatch.StartNew();
        // Mark the current document: after the dashboard reloads, the mark is gone.
        await page.EvaluateAsync("() => { window.__weplanCountryMark = true; }");
        try
        {
            await SetSelectValuesAsync(ctx, sel, new() { match }, clear: true, waitOptionsMs: null);
        }
        catch (PlaywrightException)
        {
            // the reload may interrupt the script that changed the value
        }
        await WaitCountryAppliedAsync(ctx, sel, match!);
        ctx.Log($"  country is {label} ({sw.Elapsed.TotalSeconds:0.0}s)");
    }

    private const string CountryStateJs = @"(sel) => {
        const el = document.querySelector(sel);
        return {reloaded: !window.__weplanCountryMark, value: el ? el.value : null, ready: document.readyState};
    }";

    /// <summary>
    /// Wait until the page reloaded after a country switch and shows the new country. The dashboard may
    /// navigate more than once (an aborted request, then a reload): navigation errors are expected meanwhile.
    /// </summary>
    private static async Task WaitCountryAppliedAsync(StepContext ctx, string sel, string code, int noReloadMs = 15_000)
    {
        var page = ctx.Page;
        var sw = Stopwatch.StartNew();
        JsonElement? state = null;
        while (true)
        {
            try
            {
                state = await page.EvaluateAsync<JsonElement>(CountryStateJs, sel);
            }
            catch (PlaywrightException)
            {
                state = null; // navigating
            }
            if (state is { } st && st.GetProperty("ready").GetString() == "complete"
                                && st.GetProperty("value").GetString() == code
                                && (st.GetProperty("reloaded").GetBoolean() || sw.ElapsedMilliseconds > noReloadMs))
                break;
            if (sw.ElapsedMilliseconds > ctx.Timeouts.Navigation)
                throw new StepException($"Country did not switch to '{code}' (page state: {state})");
            try
            {
                await page.WaitForTimeoutAsync(300);
            }
            catch (PlaywrightException)
            {
            }
        }
        try
        {
            await WaitPageReadyAsync(ctx);
        }
        catch (PlaywrightException)
        {
        }
    }

    // ------------------------------------------------------------------ filters

    private const string SetSelectJs = @"([sel, wanted, clear]) => {
        const el = document.querySelector(sel);
        if (!el) return {error: 'not found'};
        const opts = Array.from(el.options);
        const norm = s => String(s).trim().toLowerCase();
        const want = wanted.map(norm);
        const hit = o => want.includes(norm(o.value)) || want.includes(norm(o.text));
        const missing = wanted.filter(w => !opts.some(o => norm(o.value) === norm(w) || norm(o.text) === norm(w)));
        if (missing.length) return {missing, available: opts.map(o => o.text.trim())};
        if (el.multiple) {
            opts.forEach(o => { if (hit(o)) o.selected = true; else if (clear) o.selected = false; });
        } else {
            const o = opts.find(hit); if (o) el.value = o.value;
        }
        const $ = window.jQuery;
        if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
        el.dispatchEvent(new Event('input', {bubbles: true}));
        el.dispatchEvent(new Event('change', {bubbles: true}));
        if ($) { try { $(el).trigger('changed.bs.select'); } catch (e) {} }
        return {selected: opts.filter(o => o.selected).map(o => o.text.trim())};
    }";

    private static async Task<List<string>> SetSelectValuesAsync(StepContext ctx, string css, List<string> values, bool clear, int? waitOptionsMs)
    {
        var sw = Stopwatch.StartNew();
        var limit = waitOptionsMs ?? ctx.Timeouts.Default;
        while (true)
        {
            var res = await ctx.Page.EvaluateAsync<JsonElement>(SetSelectJs, new object[] { css, values, clear });
            if (res.TryGetProperty("error", out _)) throw new StepException($"Select '{css}' not found");
            if (!res.TryGetProperty("missing", out var missing))
                return res.GetProperty("selected").EnumerateArray().Select(e => e.GetString()!).ToList();
            // Options are often loaded asynchronously (e.g. geography); retry until timeout.
            if (sw.ElapsedMilliseconds > limit)
            {
                var available = res.GetProperty("available").EnumerateArray().Take(50).Select(e => e.GetString());
                throw new StepException($"Options {missing} not found in '{css}'. Available: [{string.Join(", ", available)}]");
            }
            await ctx.Page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>Drive bootstrap-select through clicks, like a user would.</summary>
    private static async Task<List<string>> SelectUiAsync(StepContext ctx, string css, List<string> values, bool clear)
    {
        var page = ctx.Page;
        var wrapper = page.Locator($".bootstrap-select:has({css})").First;
        await wrapper.Locator(".dropdown-toggle").First.ClickAsync();
        var menu = wrapper.Locator(".dropdown-menu").First;
        await menu.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = ctx.Timeouts.Default });
        var deselect = menu.Locator(".bs-deselect-all").First;
        if (clear && await deselect.CountAsync() > 0 && await deselect.IsVisibleAsync()) await deselect.ClickAsync();
        var search = menu.Locator(".bs-searchbox input").First;
        foreach (var v in values)
        {
            var hasSearch = await search.CountAsync() > 0 && await search.IsVisibleAsync();
            if (hasSearch) await search.FillAsync(v);
            await menu.Locator("li a, li .dropdown-item").Filter(new() { HasTextRegex = TextRegex(v, exact: true) }).First.ClickAsync();
            if (hasSearch) await search.FillAsync("");
        }
        await page.Keyboard.PressAsync("Escape");
        return (await page.EvaluateAsync<string[]>(
            "(sel) => Array.from(document.querySelector(sel).selectedOptions).map(o => o.text.trim())", css)).ToList();
    }

    private const string SelectByLabelJs = @"(wanted) => {
        const norm = s => s.replace(/\(.*?\)/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
        const w = norm(wanted);
        const labels = Array.from(document.querySelectorAll('label'))
            .filter(l => !l.closest('.modal') && norm(l.textContent) === w);
        for (const l of labels) {
            let sel = l.htmlFor ? document.getElementById(l.htmlFor) : null;
            if (!sel || sel.tagName !== 'SELECT') {
                const box = l.closest('.form-group, .form-group-sm, .col, div');
                sel = box ? box.querySelector('select') : null;
            }
            if (sel) {
                if (!sel.id) sel.setAttribute('data-weplan-label', wanted);
                return sel.id ? '#' + CSS.escape(sel.id) : `select[data-weplan-label=""${wanted}""]`;
            }
        }
        return null;
    }";

    /// <summary>CSS selector of the select whose label reads <paramref name="label"/> (counters like "(1 active)" ignored).</summary>
    private static async Task<string> SelectByLabelAsync(StepContext ctx, string label)
    {
        var sw = Stopwatch.StartNew();
        string? css;
        while ((css = await ctx.Page.EvaluateAsync<string?>(SelectByLabelJs, label)) is null)
        {
            if (sw.ElapsedMilliseconds > ctx.Timeouts.Default) throw new StepException($"No filter labelled '{label}' found on the page");
            await ctx.Page.WaitForTimeoutAsync(500);
        }
        ctx.Log($"  filter '{label}' -> {css}");
        return css;
    }

    public static async Task SelectFilterAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "id");
        var css = a.Get("selector")
                  ?? (a.Get("id") is { } id ? "#" + id
                      : a.Get("label") is { } label ? await SelectByLabelAsync(ctx, label)
                      : throw new StepException("select_filter needs id, label or selector"));
        var values = Args.StrList(a.GetValueOrDefault("options") ?? a.GetValueOrDefault("value"));
        var clear = a.GetBool("clear", true);
        await ctx.Page.WaitForSelectorAsync(css, new() { State = WaitForSelectorState.Attached, Timeout = ctx.Timeouts.Default });
        if (a.GetBool("all", false))
        {
            await SelectAllAsync(ctx, css, a.Get("mode") ?? "js");
            return;
        }
        if (values.Count == 0)
        {
            ctx.Log($"  clear {css}");
            await ctx.Page.EvaluateAsync(@"(sel) => { const el = document.querySelector(sel);
                Array.from(el.options).forEach(o => o.selected = false);
                const $ = window.jQuery; if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
                el.dispatchEvent(new Event('change', {bubbles: true})); }", css);
            return;
        }
        var selected = a.Get("mode") == "ui"
            ? await SelectUiAsync(ctx, css, values, clear)
            : await SetSelectValuesAsync(ctx, css, values, clear, a.ContainsKey("wait_options_ms") ? a.GetInt("wait_options_ms", 0) : null);
        ctx.Log($"  {css} = [{string.Join(", ", selected)}]");
    }

    private const string SelectAllJs = @"(sel) => {
        const el = document.querySelector(sel);
        const opts = Array.from(el.options).filter(o => !o.disabled && o.value !== '');
        if (!opts.length) return null;
        opts.forEach(o => o.selected = true);
        const $ = window.jQuery;
        if ($ && $.fn && $.fn.selectpicker) { try { $(el).selectpicker('refresh'); } catch (e) {} }
        el.dispatchEvent(new Event('input', {bubbles: true}));
        el.dispatchEvent(new Event('change', {bubbles: true}));
        if ($) { try { $(el).trigger('changed.bs.select'); } catch (e) {} }
        return opts.map(o => o.text.trim());
    }";

    /// <summary>Select every option ("Select All"). Waits for options that load asynchronously.</summary>
    private static async Task SelectAllAsync(StepContext ctx, string css, string mode)
    {
        var page = ctx.Page;
        string[]? selected;
        if (mode == "ui")
        {
            var wrapper = page.Locator($".bootstrap-select:has({css})").First;
            await wrapper.Locator(".dropdown-toggle").First.ClickAsync();
            await wrapper.Locator(".bs-select-all").First.ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            selected = await page.EvaluateAsync<string[]>(
                "(sel) => Array.from(document.querySelector(sel).selectedOptions).map(o => o.text.trim())", css);
        }
        else
        {
            var sw = Stopwatch.StartNew();
            while ((selected = await page.EvaluateAsync<string[]?>(SelectAllJs, css)) is null)
            {
                if (sw.ElapsedMilliseconds > ctx.Timeouts.Default) throw new StepException($"'{css}' has no options to select");
                await page.WaitForTimeoutAsync(500);
            }
        }
        ctx.Log($"  {css} = all {selected.Length}: [{string.Join(", ", selected)}]");
    }

    public static async Task FiltersAsync(StepContext ctx, object? args)
    {
        foreach (var (id, values) in Args.Map(args, "_"))
            await SelectFilterAsync(ctx, new Dictionary<string, object?> { ["id"] = id, ["options"] = values });
    }

    // ------------------------------------------------------------------ dates

    public static async Task SetDateAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "from");
        var page = ctx.Page;
        var dcfg = ctx.Config.Date;
        var cal = dcfg.Calendar;
        var mode = a.Get("mode") ?? (a.ContainsKey("preset") ? "preset" : dcfg.Mode);
        if (mode == "skip")
        {
            ctx.Log("  date: skipped, using the dashboard's current range");
            return;
        }
        await page.WaitForSelectorAsync(ctx.Sel.Datepicker, new() { State = WaitForSelectorState.Attached, Timeout = ctx.Timeouts.Default });

        if (mode == "preset")
        {
            ctx.Log($"  date preset -> {a.Get("preset")}");
            var before = await DateWidgetTextAsync(ctx);
            await OpenDatepickerAsync(ctx);
            var items = await VisibleAsync(page.GetByText(TextRegex(a.Get("preset")!, exact: true)));
            if (items.Count == 0) throw new StepException($"Preset '{a.Get("preset")}' not found in the date picker");
            await items[0].ClickAsync();
            await ClickApplyIfVisibleAsync(ctx);
            await page.Keyboard.PressAsync("Escape");
            await ReadPresetRangeAsync(ctx, a.Get("input_format") ?? dcfg.InputFormat, before);
            return;
        }

        var limits = await DateParser.GetLimitsAsync(page);
        var from = DateParser.Parse(a.Get("from") ?? throw new StepException("set_date needs from"), limits);
        var to = DateParser.Parse(a.Get("to") ?? a.Get("from")!, limits);
        if (from > to) throw new StepException($"Date from {from:yyyy-MM-dd} is after to {to:yyyy-MM-dd}");
        if (limits.Min is { } min && from < min) ctx.Log($"  WARNING: {from:yyyy-MM-dd} < dashboard minDate {min:yyyy-MM-dd}");
        if (limits.Max is { } max && to > max) ctx.Log($"  WARNING: {to:yyyy-MM-dd} > dashboard maxDate {max:yyyy-MM-dd}");
        SetDateVars(ctx, from, to);

        var fmt = a.Get("input_format") ?? dcfg.InputFormat;
        var sep = a.Get("range_separator") ?? dcfg.RangeSeparator;
        string F(DateOnly d) => d.ToString(fmt, CultureInfo.InvariantCulture);
        var expected = F(from) + sep + F(to);
        if (mode is not ("auto" or "input" or "calendar")) throw new StepException($"Unknown date mode '{mode}'");

        var (kind, inputs) = await DetectDateWidgetAsync(ctx, allowCalendar: mode != "input", preferCalendar: mode == "calendar");
        if (kind == "inputs")
        {
            if (inputs!.Count >= 2)
            {
                // Separate start / end inputs.
                ctx.Log($"  date -> start '{F(from)}', end '{F(to)}'");
                await TypeIntoAsync(inputs[0], F(from));
                await TypeIntoAsync(inputs[1], F(to));
            }
            else
            {
                ctx.Log($"  date -> '{expected}'");
                await TypeIntoAsync(inputs[0], expected);
            }
        }
        else if (kind == "css_calendar")
        {
            ctx.Log($"  date (calendar) -> {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}");
            await CalendarPickAsync(ctx, from);
            await CalendarPickAsync(ctx, to);
        }
        else
        {
            ctx.Log($"  date (calendar, by text) -> {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}");
            await TextCalendarPickAsync(ctx, from);
            await TextCalendarPickAsync(ctx, to);
        }
        await ClickApplyIfVisibleAsync(ctx);
        await page.Keyboard.PressAsync("Escape");
        await VerifyDateShownAsync(ctx, expected);
    }

    /// <summary>Variables for file names: ${date_from} ${date_to} ${year} ${month} (8) ${month2} (08).</summary>
    private static void SetDateVars(StepContext ctx, DateOnly from, DateOnly to)
    {
        ctx.Vars["date_from"] = from.ToString("yyyy-MM-dd");
        ctx.Vars["date_to"] = to.ToString("yyyy-MM-dd");
        ctx.Vars["year"] = from.Year.ToString();
        ctx.Vars["month"] = from.Month.ToString();
        ctx.Vars["month2"] = from.Month.ToString("00");
    }

    /// <summary>After clicking a preset, read the range the widget shows and set the date variables.</summary>
    private static async Task ReadPresetRangeAsync(StepContext ctx, string fmt, string before)
    {
        var shown = "";
        for (var i = 0; i < 16; i++)
        {
            shown = await DateWidgetTextAsync(ctx);
            if (shown != before || i >= 4)
            {
                var dates = Regex.Matches(shown, @"\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}")
                    .Select(m => DateOnly.TryParseExact(m.Value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
                    .Where(d => d is not null).Select(d => d!.Value).ToList();
                if (dates.Count >= 2)
                {
                    SetDateVars(ctx, dates[0], dates[1]);
                    ctx.Log($"  date widget shows: '{shown}' -> {dates[0]:yyyy-MM-dd} .. {dates[1]:yyyy-MM-dd}");
                    return;
                }
            }
            await ctx.Page.WaitForTimeoutAsync(250);
        }
        throw new StepException($"Could not read the date range after the preset (widget shows '{shown}'). Check date.input_format");
    }

    private static readonly string CalendarJs = LoadResource("calendar.js");

    private static string LoadResource(string name)
    {
        using var stream = typeof(Steps).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Task<JsonElement> TextCalendarAsync(StepContext ctx, DateOnly d) =>
        ctx.Page.EvaluateAsync<JsonElement>(CalendarJs, new[] { d.Year, d.Month, d.Day });

    private static string Shown(JsonElement res) =>
        res.TryGetProperty("shown", out var s) ? string.Join(", ", s.EnumerateArray().Select(e => e.GetString())) : "";

    /// <summary>Click day d in the open calendar popup, navigating months with its arrows.</summary>
    private static async Task TextCalendarPickAsync(StepContext ctx, DateOnly d)
    {
        var page = ctx.Page;
        for (var i = 0; i < 40; i++)
        {
            var res = await TextCalendarAsync(ctx, d);
            if (res.TryGetProperty("error", out var err))
            {
                var path = await DumpDatepickerAsync(ctx);
                throw new StepException($"Calendar: cannot find {d:yyyy-MM-dd} ({err.GetString()}, months shown: {Shown(res)}). " +
                                        $"Widget HTML saved to {path}");
            }
            var x = res.GetProperty("x").GetDouble();
            var y = res.GetProperty("y").GetDouble();
            if (res.TryGetProperty("nav", out _))
            {
                // Mouse clicks bypass the locator handler.
                if (ctx.Config.Popups.AutoDismiss) await DismissPopupsAsync(page, ctx.Config, ctx.Log);
                await page.Mouse.ClickAsync((float)x, (float)y);
                await page.WaitForTimeoutAsync(300);
                continue;
            }
            if (res.GetProperty("disabled").GetBoolean())
                throw new StepException($"Day {d:yyyy-MM-dd} is not selectable in the calendar (outside the dashboard's date limits?)");
            if (ctx.Config.Popups.AutoDismiss && await DismissPopupsAsync(page, ctx.Config, ctx.Log) > 0)
                continue; // a popup covered the calendar; locate the day again
            await page.Mouse.ClickAsync((float)x, (float)y);
            await page.WaitForTimeoutAsync(300);
            return;
        }
        throw new StepException($"Could not navigate the calendar to {d:MMMM yyyy}");
    }

    private static async Task<bool> AnyCalendarOpenAsync(StepContext ctx) =>
        await CalendarOpenAsync(ctx) ||
        !((await TextCalendarAsync(ctx, DateOnly.FromDateTime(DateTime.Today))).TryGetProperty("error", out var e) && e.GetString() == "no-calendar");

    private static async Task ClickApplyIfVisibleAsync(StepContext ctx)
    {
        var page = ctx.Page;
        if (ctx.Config.Date.Calendar.Apply is { } apply) await ClickIfVisibleAsync(page.Locator(apply).First);
        // Generic Apply/OK only while a calendar popup is still open (the dashboard's closes itself).
        if (!await AnyCalendarOpenAsync(ctx)) return;
        var buttons = await VisibleAsync(page.GetByRole(AriaRole.Button,
            new() { NameRegex = new Regex(@"^\s*(apply|ok|aplicar|done|select)\s*$", RegexOptions.IgnoreCase) }));
        if (buttons.Count > 0) await buttons[0].ClickAsync();
    }

    /// <summary>Fail if the widget ends up showing a different range (avoids exporting the wrong period).</summary>
    private static async Task VerifyDateShownAsync(StepContext ctx, string expected)
    {
        var want = Regex.Replace(expected, @"\s+", "");
        var shown = "";
        for (var i = 0; i < 12; i++)
        {
            shown = await DateWidgetTextAsync(ctx);
            if (Regex.Replace(shown, @"\s+", "").Contains(want))
            {
                ctx.Log($"  date widget shows: '{shown}'");
                return;
            }
            await ctx.Page.WaitForTimeoutAsync(250);
        }
        var m = Regex.Match(shown, @"\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}\s*-\s*\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}");
        if (m.Success)
            throw new StepException($"Date widget shows '{m.Value}' instead of '{expected}'. Check date.input_format / the calendar selection");
        ctx.Log($"  WARNING: could not read the selected range from the widget (shows '{(shown.Length > 80 ? shown[..80] : shown)}')");
    }

    private static async Task<string> DateWidgetTextAsync(StepContext ctx)
    {
        try
        {
            return await ctx.Page.EvaluateAsync<string>(@"(sel) => { const el = document.querySelector(sel); if (!el) return '';
                const vals = Array.from(el.querySelectorAll('input')).map(i => i.value).join(' ');
                return (el.innerText + ' ' + vals).replace(/\s+/g, ' ').trim(); }", ctx.Sel.Datepicker);
        }
        catch (PlaywrightException)
        {
            return "";
        }
    }

    private static async Task<List<ILocator>> VisibleAsync(ILocator loc)
    {
        var result = new List<ILocator>();
        var n = await loc.CountAsync();
        for (var i = 0; i < n; i++)
        {
            try
            {
                if (await loc.Nth(i).IsVisibleAsync()) result.Add(loc.Nth(i));
            }
            catch (PlaywrightException)
            {
            }
        }
        return result;
    }

    private static async Task ClickIfVisibleAsync(ILocator loc)
    {
        try
        {
            if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) await loc.ClickAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task TypeIntoAsync(ILocator input, string text)
    {
        await input.ClickAsync();
        await input.PressAsync("Control+A");
        await input.PressAsync("Delete");
        await input.PressSequentiallyAsync(text, new() { Delay = 20 });
        await input.PressAsync("Enter");
    }

    /// <summary>Click whatever the date widget renders (input, button or text).</summary>
    private static async Task OpenDatepickerAsync(StepContext ctx)
    {
        var page = ctx.Page;
        var inside = await VisibleAsync(page.Locator(ctx.Sel.DatepickerInput));
        if (inside.Count > 0)
        {
            await inside[0].ClickAsync();
        }
        else
        {
            var root = page.Locator(ctx.Sel.Datepicker).First;
            var clickable = await VisibleAsync(root.Locator("input, button, [role=button], [tabindex], .form-control, div, span"));
            await (clickable.Count > 0 ? clickable[0] : root).ClickAsync();
        }
        await page.WaitForTimeoutAsync(800);
    }

    private static async Task<bool> CalendarOpenAsync(StepContext ctx) =>
        (await VisibleAsync(ctx.Page.Locator(ctx.Config.Date.Calendar.Title))).Count > 0;

    /// <summary>Returns ("inputs", locators) | ("css_calendar", null) | ("text_calendar", null).</summary>
    private static async Task<(string Kind, List<ILocator>? Inputs)> DetectDateWidgetAsync(StepContext ctx, bool allowCalendar, bool preferCalendar)
    {
        var page = ctx.Page;
        if (!preferCalendar)
        {
            if (await page.Locator(ctx.Sel.DatepickerInput).CountAsync() > 0)
            {
                try
                {
                    await page.Locator(ctx.Sel.DatepickerInput).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                }
                catch (TimeoutException)
                {
                }
            }
            var inputs = await VisibleAsync(page.Locator(ctx.Sel.DatepickerInput));
            if (inputs.Count > 0) return ("inputs", inputs);
            ctx.Log("  no <input> inside the date widget -> clicking it to open the picker");
        }
        await OpenDatepickerAsync(ctx);
        if (!preferCalendar)
        {
            var inputs = await VisibleAsync(page.Locator(ctx.Config.Date.PopupInputs));
            if (inputs.Count > 0) return ("inputs", inputs);
        }
        if (allowCalendar)
        {
            if (await CalendarOpenAsync(ctx)) return ("css_calendar", null);
            var probe = await TextCalendarAsync(ctx, DateOnly.FromDateTime(DateTime.Today));
            if (!(probe.TryGetProperty("error", out var e) && e.GetString() == "no-calendar"))
            {
                ctx.Log($"  calendar popup found (months shown: {Shown(probe)})");
                return ("text_calendar", null);
            }
        }
        var path = await DumpDatepickerAsync(ctx);
        throw new StepException("Could not find a date input or calendar. The date widget's HTML was saved to " +
                                $"{path}. Send that file to adjust selectors, or use `set_date: {{mode: skip}}` " +
                                "to keep the dashboard's default range.");
    }

    private static async Task<string> DumpDatepickerAsync(StepContext ctx)
    {
        var html = await ctx.Page.EvaluateAsync<string>(@"(sel) => {
            const parts = [];
            const root = document.querySelector(sel);
            parts.push('<!-- ' + sel + ' -->\n' + (root ? root.outerHTML : 'NOT FOUND'));
            // Popups are often appended to <body>; keep the visible ones.
            for (const el of document.body.children) {
                const r = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                if (r.width > 0 && r.height > 0 && style.display !== 'none' && style.visibility !== 'hidden'
                    && /picker|calendar|popover|dropdown|dialog|overlay|menu/i.test(el.className + ' ' + el.id)) {
                    parts.push('<!-- body > ' + el.tagName + '.' + el.className + ' -->\n' + el.outerHTML);
                }
            }
            return parts.join('\n\n');
        }", ctx.Sel.Datepicker);
        var path = Path.Combine(ctx.OutputDir, "_debug", $"datepicker_{Safe(ctx.Scenario.Name)}.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, html);
        await ctx.Page.ScreenshotAsync(new() { Path = Path.ChangeExtension(path, ".png"), FullPage = true });
        return path;
    }

    private static DateOnly? ParseMonthTitle(string title)
    {
        title = Regex.Replace(title, @"\s+", " ").Trim();
        return DateOnly.TryParseExact(title, new[] { "MMMM yyyy", "MMM yyyy", "MM/yyyy", "yyyy-MM" }, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var d) ? new DateOnly(d.Year, d.Month, 1) : null;
    }

    private static async Task CalendarPickAsync(StepContext ctx, DateOnly d)
    {
        var cal = ctx.Config.Date.Calendar;
        var page = ctx.Page;
        var target = new DateOnly(d.Year, d.Month, 1);
        for (var i = 0; ; i++)
        {
            if (i >= 40) throw new StepException($"Could not navigate calendar to {target:MMMM yyyy}");
            var titles = await VisibleAsync(page.Locator(cal.Title));
            if (titles.Count == 0) throw new StepException("Calendar popup is not open (date.calendar.title matched nothing visible)");
            var raw = await titles[0].InnerTextAsync();
            var shown = ParseMonthTitle(raw) ?? throw new StepException($"Cannot read calendar month from '{raw.Trim()}'");
            if (shown == target) break;
            var nav = await VisibleAsync(page.Locator(shown < target ? cal.Next : cal.Prev));
            if (nav.Count == 0)
                throw new StepException($"Cannot move calendar from {shown:MMM yyyy} to {target:MMM yyyy} (outside date limits?)");
            await nav[0].ClickAsync();
            await page.WaitForTimeoutAsync(200);
        }
        var days = await VisibleAsync(page.Locator(cal.Day).Filter(new() { HasTextRegex = TextRegex(d.Day.ToString(), exact: true) }));
        if (days.Count == 0)
            throw new StepException($"Day {d:yyyy-MM-dd} is not selectable in the calendar (outside the dashboard's date limits?)");
        await days[0].ClickAsync();
        await page.WaitForTimeoutAsync(200);
    }

    // ------------------------------------------------------------------ query / results

    public static async Task ApplyAsync(StepContext ctx, object? args)
    {
        if (!await ClickUpdateIfVisibleAsync(ctx)) ctx.Log("  nothing to apply");
    }

    public static async Task ChooseViewAsync(StepContext ctx, object? args)
    {
        var page = ctx.Page;
        await ClickUpdateIfVisibleAsync(ctx);
        var a = Args.Map(args, "view");
        ILocator loc;
        string label;
        if (a.Get("text") is { } text)
        {
            loc = page.Locator(".chartModeSelector").Filter(new() { HasTextRegex = TextRegex(text) }).First;
            label = text;
        }
        else
        {
            var key = a.Get("view") ?? throw new StepException("choose_view needs a view");
            var css = ctx.Config.Views.GetValueOrDefault(key, key);
            loc = css.StartsWith('#') || css.StartsWith('.') || css.StartsWith('[')
                ? page.Locator(css).First
                : page.Locator(".chartModeSelector").Filter(new() { HasTextRegex = TextRegex(key) }).First;
            label = key;
        }
        try
        {
            await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            // The dashboard may already show results (e.g. restored last query): the cards are hidden then.
            if (await page.Locator(ctx.Sel.Results).First.IsVisibleAsync())
            {
                ctx.Log($"  view cards hidden, results already shown -> keeping current view (wanted {label})");
                return;
            }
            await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = ctx.Timeouts.DataLoad });
        }
        ctx.Log($"  view -> {label}");
        await loc.ClickAsync();
        if (a.Get("municipality") is { } muni)
            await SelectFilterAsync(ctx, new Dictionary<string, object?> { ["id"] = "municipalitySelect", ["options"] = muni });
    }

    public static async Task WaitForTableAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "min_rows");
        var page = ctx.Page;
        var timeout = a.GetInt("timeout", ctx.Timeouts.DataLoad);
        var minRows = a.GetInt("min_rows", 1);
        var rowsSel = a.Get("rows") ?? ctx.Sel.TableRows;
        // Results shown, nothing loading and still no rows after this long -> the table is empty
        // (e.g. no 5G measurements in a market). 0 disables it.
        var emptyAfter = a.GetInt("empty_after_ms", 20_000);
        var sw = Stopwatch.StartNew();
        Stopwatch? emptyFor = null;
        while (true)
        {
            await CheckErrorAsync(ctx);
            var resultsVisible = await page.Locator(ctx.Sel.Results).First.IsVisibleAsync();
            var rows = resultsVisible ? await page.Locator(rowsSel).CountAsync() : 0;
            var loading = !string.IsNullOrEmpty(ctx.Sel.Loading) && await page.Locator(ctx.Sel.Loading).CountAsync() > 0;
            if (resultsVisible && rows >= minRows && !loading) break;
            if (resultsVisible && rows == 0 && !loading && emptyAfter > 0)
            {
                emptyFor ??= Stopwatch.StartNew();
                if (emptyFor.ElapsedMilliseconds >= emptyAfter)
                {
                    ctx.Vars["rows"] = 0;
                    ctx.Log($"  WARNING: table is empty (no data for these filters) after {sw.Elapsed.TotalSeconds:0.0}s");
                    return;
                }
            }
            else
            {
                emptyFor = null;
            }
            if (sw.ElapsedMilliseconds > timeout)
                throw new StepException($"Timed out after {timeout / 1000}s waiting for table " +
                                        $"(results visible={resultsVisible}, rows={rows}, loading={loading})");
            await page.WaitForTimeoutAsync(1000);
        }
        await page.WaitForTimeoutAsync(a.GetInt("settle_ms", 1500)); // let the table finish rendering
        var count = await page.Locator(rowsSel).CountAsync();
        ctx.Vars["rows"] = count;
        ctx.Log($"  table ready: {count} rows ({sw.Elapsed.TotalSeconds:0.0}s)");
    }

    // ------------------------------------------------------------------ download

    /// <summary>Menu items of "Download table": As XLSX / As JSON / As CSV / As PDF / As TXT / As PNG.</summary>
    private static readonly Dictionary<string, string> FormatText = new()
    {
        ["xlsx"] = @"^\s*As XLSX\s*$|excel|xlsx",
        ["xls"] = @"^\s*As XLS\s*$|excel",
        ["csv"] = @"^\s*As CSV\s*$",
        ["json"] = @"^\s*As JSON\s*$",
        ["pdf"] = @"^\s*As PDF\s*$",
        ["txt"] = @"^\s*As TXT\s*$",
        ["png"] = @"^\s*As PNG\s*$",
    };

    public static async Task DownloadTableAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "format");
        var page = ctx.Page;
        var fmt = (a.Get("format") ?? "").ToLowerInvariant();
        var buttonText = a.Get("button_text") ?? ctx.Sel.DownloadButtonText;

        if (a.GetValueOrDefault("format_select") is Dictionary<string, object?> fs)
            await SetSelectValuesAsync(ctx, fs.Get("selector")!, new() { fs.Get("option")! }, true, null);

        await CheckCountryAsync(ctx);

        var scope = page.Locator(a.Get("container") ?? ctx.Sel.TableContainer).First;
        var btn = scope.Locator("button, a, [role=button]").Filter(new() { HasTextRegex = TextRegex(buttonText) }).First;
        if (await btn.CountAsync() == 0) // button may live in a toolbar outside the container
            btn = page.Locator("button, a, [role=button]").Filter(new() { HasTextRegex = TextRegex(buttonText) }).First;
        await btn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = ctx.Timeouts.Default });
        await btn.ScrollIntoViewIfNeededAsync();

        Regex? optionRe = a.Get("format_text") is { } ft ? TextRegex(ft)
            : fmt != "" ? new Regex(FormatText.GetValueOrDefault(fmt, Regex.Escape(fmt)), RegexOptions.IgnoreCase)
            : null;

        ctx.Log($"  click '{buttonText}'" + (optionRe is null ? "" : $" -> {optionRe}"));
        var download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            await btn.ClickAsync();
            if (optionRe is not null) await ClickFormatOptionAsync(ctx, btn, optionRe);
        }, new() { Timeout = ctx.Timeouts.Download });

        var suggested = download.SuggestedFilename ?? "table";
        var ext = Path.GetExtension(suggested);
        if (ext == "" && fmt != "") ext = "." + fmt;
        ctx.Vars.TryAdd("date_from", "");
        ctx.Vars.TryAdd("date_to", "");
        ctx.Vars["timestamp"] = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = a.Get("filename") is { } template
            ? string.Join("/", ScenarioLoader.RenderString(template, ctx.Vars, SafeFileName).Split('/').Select(SafeFileName))
            : $"{Safe(ctx.Scenario.Name)}_{ctx.Vars["timestamp"]}";
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;

        var target = Path.Combine(ctx.OutputDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await download.SaveAsAsync(target);
        if (await download.FailureAsync() is { } failure) throw new StepException($"Download failed: {failure}");
        var size = new FileInfo(target).Length;
        ctx.Log($"  saved {target} ({size:N0} bytes, server name '{suggested}')");
        var tableEmpty = ctx.Vars.TryGetValue("rows", out var r) && Args.Str(r) == "0";
        var rows = a.GetBool("verify", true) && !tableEmpty ? VerifyFile(target) : -1;
        if (rows >= 0) ctx.Log($"  verified: {rows} data rows");
        ctx.Downloads.Add(new DownloadInfo(target, size, suggested, rows));
    }

    /// <summary>Refuse to export if the dashboard no longer shows the country chosen by select_country.</summary>
    private static async Task CheckCountryAsync(StepContext ctx)
    {
        if (!ctx.Vars.TryGetValue("country_code", out var want) || want is null) return;
        var shown = await ctx.Page.EvaluateAsync<string?>(
            "(sel) => { const el = document.querySelector(sel); return el ? el.value : null; }", ctx.Sel.CountrySelect);
        if (shown is not null && shown != Args.Str(want))
            throw new StepException($"Dashboard shows country '{shown}' but this scenario is for '{Args.Str(want)}': not exporting");
    }

    /// <summary>After clicking the download button, pick the file-type item if a menu/modal appears.</summary>
    private static async Task ClickFormatOptionAsync(StepContext ctx, ILocator btn, Regex optionRe)
    {
        var candidates = ctx.Page.Locator(
            ".dropdown-menu.show a, .dropdown-menu.show button, .dropdown-menu.show li, " +
            ".p-menu a, .p-menuitem-link, .p-tieredmenu a, .modal.show button, .modal.show a, " +
            "[role=menuitem], [role=option], button, a, label").Filter(new() { HasTextRegex = optionRe });
        var btnText = (await btn.InnerTextAsync()).Trim();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            var n = await candidates.CountAsync();
            for (var i = 0; i < n; i++)
            {
                var c = candidates.Nth(i);
                try
                {
                    if (await c.IsVisibleAsync() && !string.Equals((await c.InnerTextAsync()).Trim(), btnText, StringComparison.OrdinalIgnoreCase))
                    {
                        await c.ClickAsync();
                        return;
                    }
                }
                catch (PlaywrightException)
                {
                    // element went away between count and click; retry
                }
            }
            await ctx.Page.WaitForTimeoutAsync(250);
        }
        ctx.Log("  (no file-type menu appeared; assuming the button downloads directly)");
    }

    private static int VerifyFile(string path)
    {
        if (new FileInfo(path).Length == 0) throw new StepException($"Downloaded file {path} is empty");
        int rows;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx" or ".xlsm":
                using (var wb = new XLWorkbook(path))
                    rows = wb.Worksheets.Sum(ws => Math.Max((ws.LastRowUsed()?.RowNumber() ?? 0) - 1, 0));
                break;
            case ".csv" or ".txt":
                rows = Math.Max(File.ReadLines(path).Count(l => l.Trim().Length > 0) - 1, 0);
                break;
            default:
                return -1;
        }
        if (rows <= 0) throw new StepException($"Downloaded file {path} has no data rows");
        return rows;
    }

    // ------------------------------------------------------------------ generic steps

    public static async Task ClickAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "selector");
        var page = ctx.Page;
        var loc = a.Get("text") is { } text ? page.GetByText(text, new() { Exact = a.GetBool("exact", false) })
            : a.Get("role") is { } role ? page.GetByRole(Enum.Parse<AriaRole>(role, ignoreCase: true), new() { Name = a.Get("name") })
            : page.Locator(a.Get("selector") ?? throw new StepException("click needs selector, text or role"));
        ctx.Log($"  click {Args.Str(args)}");
        await loc.Nth(a.GetInt("index", 0)).ClickAsync(new() { Timeout = a.GetInt("timeout", ctx.Timeouts.Default) });
    }

    public static Task FillAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "selector");
        return ctx.Page.Locator(a.Get("selector")!).First.FillAsync(a.Get("value") ?? "");
    }

    public static Task PressAsync(StepContext ctx, object? args) =>
        ctx.Page.Keyboard.PressAsync(Args.Map(args, "key").Get("key")!);

    public static Task WaitAsync(StepContext ctx, object? args) =>
        ctx.Page.WaitForTimeoutAsync(Args.Map(args, "ms").GetInt("ms", 1000));

    public static Task WaitForAsync(StepContext ctx, object? args)
    {
        var a = Args.Map(args, "selector");
        var state = Enum.Parse<WaitForSelectorState>(a.Get("state") ?? "visible", ignoreCase: true);
        return ctx.Page.Locator(a.Get("selector")!).First.WaitForAsync(new() { State = state, Timeout = a.GetInt("timeout", ctx.Timeouts.DataLoad) });
    }

    public static async Task ScreenshotAsync(StepContext ctx, object? args)
    {
        var name = Args.Map(args, "name").Get("name") ?? "screenshot";
        var path = Path.Combine(ctx.OutputDir, "screenshots", $"{Safe(ctx.Scenario.Name)}_{Safe(name)}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await ctx.Page.ScreenshotAsync(new() { Path = path, FullPage = true });
        ctx.Log($"  screenshot {path}");
    }

    public static async Task EvaluateAsync(StepContext ctx, object? args)
    {
        var result = await ctx.Page.EvaluateAsync<JsonElement?>(Args.Map(args, "script").Get("script")!);
        ctx.Log($"  js -> {result}");
    }

    public static Task PauseAsync(StepContext ctx, object? args) => ctx.Page.PauseAsync();

    public static Task SetVarAsync(StepContext ctx, object? args)
    {
        foreach (var (k, v) in Args.Map(args, "_")) ctx.Vars[k] = v;
        return Task.CompletedTask;
    }
}
