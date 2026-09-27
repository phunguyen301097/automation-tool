using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeplanExport;

/// <summary>config.yaml. Any key missing from the file keeps the default below.</summary>
public class AppConfig
{
    public string BaseUrl { get; set; } = "https://dashboard.weplananalytics.com";
    public string StartPath { get; set; } = "/app/bi/coverage";
    public string OutputDir { get; set; } = "downloads";
    public AuthConfig Auth { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public TimeoutConfig Timeouts { get; set; } = new();
    public SelectorConfig Selectors { get; set; } = new();
    public DateConfig Date { get; set; } = new();

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
        return cfg;
    }
}

public class AuthConfig
{
    public bool Required { get; set; } = true;
    public string StorageState { get; set; } = ".auth/state.json";
    /// <summary>A URL containing one of these means the session expired.</summary>
    public List<string> LoginUrlMarkers { get; set; } = new() { "login", "signin", "auth" };
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
    /// <summary>input: type into the date input. calendar: click days in the popup.</summary>
    public string Mode { get; set; } = "input";
    /// <summary>.NET date format, e.g. MM/dd/yyyy or dd/MM/yyyy.</summary>
    public string InputFormat { get; set; } = "MM/dd/yyyy";
    public string RangeSeparator { get; set; } = " - ";
    public CalendarConfig Calendar { get; set; } = new();
}

public class CalendarConfig
{
    public string Title { get; set; } = ".p-datepicker-title, .p-datepicker-header";
    public string Prev { get; set; } = ".p-datepicker-prev, .p-datepicker-prev-button";
    public string Next { get; set; } = ".p-datepicker-next, .p-datepicker-next-button";
    public string Day { get; set; } =
        "td:not(.p-datepicker-other-month):not(.p-datepicker-day-cell-other-month) span";
    public string? Apply { get; set; }
}
