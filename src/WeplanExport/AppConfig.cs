using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeplanExport;

/// <summary>config.yaml. Any key missing from the file keeps the default below.</summary>
public class AppConfig
{
    public string BaseUrl { get; set; } = "https://dashboard.weplananalytics.com";
    public string StartPath { get; set; } = "/app/bi/coverage";
    public string OutputDir { get; set; } = "downloads";
    /// <summary>
    /// page: one page (KPI file) for every market, then the next page (file order).
    /// market: every page for one market, then the next market.
    /// </summary>
    public string RunOrder { get; set; } = "page";
    public AuthConfig Auth { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public TimeoutConfig Timeouts { get; set; } = new();
    public SelectorConfig Selectors { get; set; } = new();
    public DateConfig Date { get; set; } = new();
    public PopupConfig Popups { get; set; } = new();
    public ThrottleConfig Throttle { get; set; } = new();

    /// <summary>Variables available to every scenario file (e.g. <c>markets</c>).</summary>
    public Dictionary<string, object?> Vars { get; set; } = new();

    /// <summary>Aliases for the visualization cards ("Select a visualization mode").</summary>
    public Dictionary<string, string> Views { get; set; } = new()
    {
        ["macro"] = "#byCountry",
        ["population_range"] = "#byRanges",
        ["admin_1"] = "#byRegions",
        ["admin_2"] = "#byProvinces",
        ["admin_3"] = "#byMunicipality",
    };

    public static AppConfig Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new AppConfig();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var cfg = deserializer.Deserialize<AppConfig?>(File.ReadAllText(path)) ?? new AppConfig();
        // Merge view aliases instead of replacing them.
        foreach (var kv in new AppConfig().Views) cfg.Views.TryAdd(kv.Key, kv.Value);
        cfg.Vars = cfg.Vars.ToDictionary(kv => kv.Key, kv => ScenarioLoader.Normalize(kv.Value));
        return cfg;
    }
}

public class AuthConfig
{
    public bool Required { get; set; } = true;
    public string StorageState { get; set; } = ".auth/state.json";
    /// <summary>A URL containing one of these means the session expired.</summary>
    public List<string> LoginUrlMarkers { get; set; } = new() { "login", "signin", "auth" };
    /// <summary>Text of a page saying the account is blocked: the run stops at once (no retry).</summary>
    public List<string> BlockedTexts { get; set; } = new()
    {
        "temporarily suspended", "account has been suspended", "account is suspended",
        "account has been blocked", "account is blocked", "account has been disabled",
    };
    public string UsernameEnv { get; set; } = "WEPLAN_USERNAME";
    public string PasswordEnv { get; set; } = "WEPLAN_PASSWORD";
    public string LoginPath { get; set; } = "/";
    public string UsernameSelector { get; set; } = "input[type=email], input[name=email], input[name=username]";
    public string PasswordSelector { get; set; } = "input[type=password]";
    public string SubmitSelector { get; set; } = "button[type=submit], input[type=submit]";
}

public class BrowserConfig
{
    public bool Headless { get; set; } = true;
    /// <summary>false: all scenarios share one page. true: fresh context per scenario (--isolated).</summary>
    public bool Isolated { get; set; }
    /// <summary>"chrome" / "msedge" to use an installed browser.</summary>
    public string? Channel { get; set; }
    public string? ExecutablePath { get; set; }
    public int SlowMo { get; set; }
    public ViewportConfig Viewport { get; set; } = new();
    public string Locale { get; set; } = "en-US";
}

public class ViewportConfig
{
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 1000;
}

public class TimeoutConfig
{
    public int Default { get; set; } = 30_000;
    public int Navigation { get; set; } = 90_000;
    public int DataLoad { get; set; } = 300_000;
    public int Download { get; set; } = 180_000;
}

public class SelectorConfig
{
    public string LoggedInMarker { get; set; } = "aside.dash-sidebar, #dash";
    public string Sidebar { get; set; } = "aside.dash-sidebar";
    public string CountrySelect { get; set; } = "#dropdownCountryChooser";
    public string Datepicker { get; set; } = "#datepicker";
    public string DatepickerInput { get; set; } = "#datepicker input";
    public string Results { get; set; } = "#results";
    public string TableContainer { get; set; } = "#tableProvinces";
    public string TableRows { get; set; } = "#tableProvinces table tbody tr";
    public string Loading { get; set; } =
        ".panelLoader:visible, .graphChooserLoader:visible, .p-datatable-loading-overlay, .dash-table-loading";
    public string UpdateButton { get; set; } = "#updateButton";
    public string ErrorMessage { get; set; } = "#errors_row #error_message";
    public string DownloadButtonText { get; set; } = "Download table";
}

public class DateConfig
{
    /// <summary>
    /// auto: type into an input if present, else inputs in the popup, else click the calendar.
    /// input: type into the date input. calendar: click start/end days in the popup.
    /// </summary>
    public string Mode { get; set; } = "auto";
    /// <summary>.NET date format. The dashboard shows 01-09-2026 - 25-09-2026.</summary>
    public string InputFormat { get; set; } = "dd-MM-yyyy";
    public string RangeSeparator { get; set; } = " - ";
    /// <summary>Inputs looked for in the popup when #datepicker itself has no input.</summary>
    public string PopupInputs { get; set; } =
        ".daterangepicker input, .p-datepicker input, .dp__menu input, .mx-datepicker-main input, " +
        ".vc-popover-content input, .flatpickr-calendar input, [role=dialog] input, .dropdown-menu.show input";
    public CalendarConfig Calendar { get; set; } = new();
}

/// <summary>Defaults cover daterangepicker / vue2-daterange-picker (two months + Apply) and PrimeVue.</summary>
public class CalendarConfig
{
    public string Title { get; set; } =
        ".daterangepicker .drp-calendar.left .month, .daterangepicker .calendar.left .month, .p-datepicker-title, .p-datepicker-header";
    public string Prev { get; set; } = ".daterangepicker .prev.available, .p-datepicker-prev, .p-datepicker-prev-button";
    public string Next { get; set; } = ".daterangepicker .next.available, .p-datepicker-next, .p-datepicker-next-button";
    public string Day { get; set; } =
        ".daterangepicker .drp-calendar.left td.available:not(.off), .daterangepicker .calendar.left td.available:not(.off), " +
        "td:not(.p-datepicker-other-month):not(.p-datepicker-day-cell-other-month) > span";
    public string? Apply { get; set; } = ".daterangepicker .applyBtn, .drp-buttons .applyBtn";
}

/// <summary>
/// Announcement popups (e.g. "What's New: New Delta Analysis in Map View") that block the page
/// are closed automatically before actions and before every step.
/// </summary>
public class PopupConfig
{
    public bool AutoDismiss { get; set; } = true;
    public string Selector { get; set; } =
        "#changelogAnnouncer .modal.show, #changelogAnnouncer [role=dialog], #changelogAnnouncer .p-dialog, " +
        ".modal.show, [role=dialog][aria-modal=true], .p-dialog-mask .p-dialog, .swal2-popup";
    /// <summary>Dialogs a scenario may open on purpose; never auto-closed.</summary>
    public List<string> Ignore { get; set; } = new() { "#locationSourceModal", "#user_preferences_modal", "#user_account_modal" };
    public string CloseSelector { get; set; } =
        ".btn-close, [aria-label='Close' i], [data-bs-dismiss=modal], .p-dialog-header-close, .swal2-close, .close";
    public List<string> CloseTexts { get; set; } = new()
    {
        "Close", "Got it", "OK", "Okay", "Dismiss", "Skip", "Later", "Not now", "Understood", "Continue",
        "Cerrar", "Entendido", "Aceptar",
    };
}

/// <summary>
/// Rest between exports so the dashboard is not hit continuously. A pause is a number of seconds,
/// [min, max] (random in between) or "min-max"; 0 = no pause.
/// </summary>
public class ThrottleConfig
{
    /// <summary>Between two scenarios.</summary>
    public object? PauseBetween { get; set; }
    /// <summary>Between two steps of one scenario (e.g. [2, 5]).</summary>
    public object? PauseBetweenSteps { get; set; }
    /// <summary>Instead of PauseBetween when the next scenario is another market.</summary>
    public object? PauseAfterMarket { get; set; }
    /// <summary>Stop after this many files in one run (0 = no limit); --resume continues.</summary>
    public int MaxExports { get; set; }

    /// <summary>30 -> (30, 30); [30, 60] or "30-60" -> (30, 60).</summary>
    public static (double Min, double Max) Range(object? value)
    {
        var parts = value switch
        {
            null => new List<string>(),
            string s => System.Text.RegularExpressions.Regex.Split(s.Trim(), @"\s*[-,]\s*").Where(x => x.Length > 0).ToList(),
            System.Collections.IEnumerable e => e.Cast<object?>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList(),
            _ => new List<string> { Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" },
        };
        if (parts.Count == 0) return (0, 0);
        var nums = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                throw new StepException($"Invalid pause '{value}': use seconds, [min, max] or \"min-max\"");
            nums.Add(d);
        }
        var (lo, hi) = (nums[0], nums[^1]);
        if (lo < 0 || hi < lo) throw new StepException($"Invalid pause '{value}': use seconds, [min, max] or \"min-max\"");
        return (lo, hi);
    }
}
