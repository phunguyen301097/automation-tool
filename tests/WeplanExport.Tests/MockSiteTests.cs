using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClosedXML.Excel;
using Xunit;

namespace WeplanExport.Tests;

/// <summary>Serves tests/mock_site/index.html for every page and generates files for /export.</summary>
public sealed class MockServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _html = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mock_site", "index.html"));
    public string BaseUrl { get; }

    public MockServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try
            {
                c = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            try
            {
                Handle(c);
            }
            catch
            {
                c.Response.StatusCode = 500;
            }
            c.Response.Close();
        }
    }

    private void Handle(HttpListenerContext c)
    {
        byte[] body;
        if (c.Request.Url!.AbsolutePath == "/export")
        {
            var q = c.Request.QueryString;
            var fmt = q["format"]!;
            var rows = q.AllKeys.Where(k => k != "format").Select(k => (k!, q[k] ?? "")).ToList();
            if (fmt == "xlsx")
            {
                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("table");
                ws.Cell(1, 1).Value = "key";
                ws.Cell(1, 2).Value = "value";
                for (var i = 0; i < rows.Count; i++)
                {
                    ws.Cell(i + 2, 1).Value = rows[i].Item1;
                    ws.Cell(i + 2, 2).Value = rows[i].Item2;
                }
                using var ms = new MemoryStream();
                wb.SaveAs(ms);
                body = ms.ToArray();
                c.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            }
            else
            {
                body = Encoding.UTF8.GetBytes("key,value\n" + string.Join("\n", rows.Select(r => $"{r.Item1},{r.Item2}")));
                c.Response.ContentType = "text/csv";
            }
            c.Response.AddHeader("Content-Disposition", $"attachment; filename=\"table_export.{fmt}\"");
        }
        else
        {
            body = _html;
            c.Response.ContentType = "text/html; charset=utf-8";
        }
        c.Response.ContentLength64 = body.Length;
        c.Response.OutputStream.Write(body);
    }

    public void Dispose() => _listener.Close();
}

public class MockSiteTests : IClassFixture<MockServer>, IDisposable
{
    private readonly MockServer _server;
    private readonly string _tmp = Directory.CreateTempSubdirectory("weplan-test").FullName;

    public MockSiteTests(MockServer server) => _server = server;

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private AppConfig Config() => new()
    {
        BaseUrl = _server.BaseUrl,
        OutputDir = Path.Combine(_tmp, "out"),
        Auth = new AuthConfig { Required = false },
        Timeouts = new TimeoutConfig { Default = 10_000, DataLoad = 20_000, Download = 20_000 },
    };

    private async Task<List<ScenarioResult>> RunYamlAsync(string yaml, AppConfig config, RunState? state = null)
    {
        var file = Path.Combine(_tmp, "s.yaml");
        await File.WriteAllTextAsync(file, yaml);
        return await Runner.RunAsync(ScenarioLoader.Load(new[] { file }), config, new RunOptions(), state);
    }

    private static Dictionary<string, string> ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheet(1).RowsUsed().Skip(1)
            .ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).GetString());
    }

    [Fact]
    public async Task FullFlow_Xlsx_WithMatrix()
    {
        var config = Config();
        var results = await RunYamlAsync("""
            vars: {from: max-30d, to: max}
            scenarios:
              - name: cov_${country}_${network}
                matrix:
                  country: [Cambodia]
                  network: [METFONE, SMART]
                steps:
                  - goto: /app/bi/coverage
                  - open_menu: ["Latency", "Latency Mobile (Cellular)"]
                  - select_country: ${country}
                  - set_date: {from: "${from}", to: "${to}"}
                  - select_filter: {id: carrier_filter, options: ["${network}"]}
                  - filters: {coverage_filter: ["4G"], origin_filter: [Weplan]}
                  - choose_view: macro
                  - wait_for_table: {}
                  - download_table: {format: xlsx, filename: "lat/${country}_${network}_${date_from}_${date_to}"}
            """, config);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(2, results.Count);
        foreach (var net in new[] { "METFONE", "SMART" })
        {
            var file = Path.Combine(config.OutputDir, "lat", $"Cambodia_{net}_2026-08-24_2026-09-23.xlsx");
            Assert.True(File.Exists(file), file);
            var data = ReadXlsx(file);
            Assert.Equal("kh", data["country"]);
            Assert.Equal("/app/bi/latencyMobile", data["kpi"]);
            Assert.Equal("2026-08-24..2026-09-23", data["date"]);
            Assert.Equal(net, data["carrier"]);
            Assert.Equal("4G", data["coverage"]);
            Assert.Equal("Weplan", data["origin"]);
            Assert.Equal("byCountry", data["view"]);
        }
    }

    [Fact]
    public async Task Csv_And_Requery_After_Filter_Change()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: csv_requery
                steps:
                  - goto: /app/bi/coverage
                  - set_date: {from: 2026-08-01, to: 2026-08-31}
                  - choose_view: {text: "By provinces"}
                  - wait_for_table: {}
                  - select_filter: {id: group_selector, options: ["Cellular network"]}
                  - apply: {}
                  - wait_for_table: {}
                  - download_table: {format: csv}
            """, Config());

        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Error);
        var file = Assert.Single(r.Downloads).File;
        Assert.EndsWith(".csv", file);
        var content = File.ReadLines(file).Skip(1).Select(l => l.Split(',', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("carrier", content["group"]);
        Assert.Equal("byRegions", content["view"]);
        Assert.Equal("2026-08-01..2026-08-31", content["date"]);
    }

    [Fact]
    public async Task Failure_Is_Reported_With_Artifacts()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: bad_network
                steps:
                  - goto: /app/bi/coverage
                  - select_filter: {id: carrier_filter, options: [NOPE], wait_options_ms: 1500}
            """, Config());

        var r = Assert.Single(results);
        Assert.False(r.Ok);
        Assert.Contains("NOPE", r.Error);
        Assert.Contains("ECONET", r.Error);
        Assert.Equal("2. select_filter", r.FailedStep);
        Assert.Contains(r.Artifacts, a => a.EndsWith(".png"));
    }

    [Fact]
    public async Task Dashboard_Error_Fails_Fast()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: no_date
                steps: [{goto: /app/bi/coverage}, {choose_view: macro}, {wait_for_table: {}}]
            """, Config());

        var r = Assert.Single(results);
        Assert.False(r.Ok);
        Assert.Contains("No date selected", r.Error);
    }

    [Fact]
    public async Task DateWidgetWithoutInput_UsesPopupInputs()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: popup_date
                steps:
                  - goto: /app/bi/coverage?dp=popup
                  - set_date: {from: 2026-08-01, to: 2026-08-31}
                  - choose_view: macro
                  - wait_for_table: {}
                  - download_table: {format: csv}
            """, Config());

        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("date,2026-08-01..2026-08-31", File.ReadAllText(r.Downloads[0].File));
    }

    [Fact]
    public async Task DateTextInputVariant_And_JsonExport()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: input_date
                steps:
                  - goto: /app/bi/coverage?dp=input
                  - set_date: {from: 2026-07-15, to: 2026-08-02}
                  - choose_view: macro
                  - wait_for_table: {}
                  - download_table: {format: json}
            """, Config());

        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Error);
        Assert.EndsWith(".json", r.Downloads[0].File);
        Assert.Contains("date,2026-07-15..2026-08-02", File.ReadAllText(r.Downloads[0].File));
    }

    [Fact]
    public async Task UnknownDateWidget_DumpsHtml()
    {
        var config = Config();
        var results = await RunYamlAsync("""
            scenarios:
              - name: unknown_date
                steps:
                  - goto: /app/bi/coverage?dp=none
                  - set_date: {from: 2026-08-01, to: 2026-08-31}
            """, config);

        var r = Assert.Single(results);
        Assert.False(r.Ok);
        var dump = Path.Combine(config.OutputDir, "_debug", "datepicker_unknown_date.html");
        Assert.Contains(dump, r.Error);
        Assert.Contains("reportrange-text", File.ReadAllText(dump));
    }

    [Fact]
    public async Task CalendarDayAfterMaxDate_FailsClearly()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: future_date
                steps:
                  - goto: /app/bi/coverage
                  - set_date: {from: 2026-09-01, to: 2026-09-25}
            """, Config());

        var r = Assert.Single(results);
        Assert.False(r.Ok);
        Assert.Contains("2026-09-25 is not selectable", r.Error);
    }

    [Fact]
    public async Task ManualDownload_IsKept()
    {
        var config = Config();
        using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await Runner.LaunchBrowserAsync(pw, config);
        var ctx = await Runner.NewContextAsync(browser, config, useState: false);
        Runner.KeepManualDownloads(ctx, config);
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(_server.BaseUrl + "/app/bi/coverage");
        var download = await page.RunAndWaitForDownloadAsync(() => page.EvaluateAsync(
            "u => { const a = document.createElement('a'); a.href = u; document.body.appendChild(a); a.click(); }",
            _server.BaseUrl + "/export?format=csv&x=1"));
        await download.PathAsync();
        await page.WaitForTimeoutAsync(500);
        await ctx.CloseAsync();
        Assert.True(File.Exists(Path.Combine(config.OutputDir, "manual", "table_export.csv")));
    }

    [Fact]
    public async Task VideoLikeCalendar_NavigatesMonths()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: nav_months
                steps:
                  - goto: /app/bi/coverage
                  - set_date: {from: 2026-05-31, to: 2026-07-01}
                  - choose_view: macro
                  - wait_for_table: {}
                  - download_table: {format: csv}
            """, Config());

        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("date,2026-05-31..2026-07-01", File.ReadAllText(r.Downloads[0].File));
    }

    [Fact]
    public async Task DaterangepickerVariant()
    {
        var results = await RunYamlAsync("""
            scenarios:
              - name: drp
                steps:
                  - goto: /app/bi/coverage?dp=drp
                  - set_date: {from: 2026-08-03, to: 2026-09-14}
                  - choose_view: macro
                  - wait_for_table: {}
                  - download_table: {format: csv}
            """, Config());

        var r = Assert.Single(results);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("date,2026-08-03..2026-09-14", File.ReadAllText(r.Downloads[0].File));
    }

    private const string TwoScenarios = """
        scenarios:
          - name: first
            steps:
              - goto: /app/bi/coverage
              - js: "() => sessionStorage.setItem('mark', '1')"
          - name: second
            steps:
              - open_menu: ["Coverage time"]
              - js: "() => { if (sessionStorage.getItem('mark') !== '1') throw new Error('not the same page'); }"
        """;

    [Fact]
    public async Task Scenarios_Share_One_Page_By_Default()
    {
        var results = await RunYamlAsync(TwoScenarios, Config());
        Assert.Equal(new[] { "PASS", "PASS" }, results.Select(r => r.Status));
    }

    [Fact]
    public async Task Isolated_Mode_Uses_Fresh_Page()
    {
        var config = Config();
        config.Browser.Isolated = true;
        var results = await RunYamlAsync(TwoScenarios, config);
        Assert.Equal(new[] { "PASS", "FAIL" }, results.Select(r => r.Status));
        Assert.Contains("not the same page", results[1].Error);
    }

    [Fact]
    public async Task Closing_The_Browser_Window_Stops_The_Run()
    {
        Steps.Registry["user_closes_window"] = new((ctx, _) => ctx.Page.CloseAsync(), "test only");
        var results = await RunYamlAsync("""
            scenarios:
              - {name: a, steps: [{goto: /app/bi/coverage}]}
              - {name: b, steps: [{goto: /app/bi/coverage}, {user_closes_window: null}, {wait: 100}]}
              - {name: c, steps: [{goto: /app/bi/coverage}]}
              - {name: d, steps: [{goto: /app/bi/coverage}]}
            """, Config());
        Assert.Equal(new[] { "PASS", "STOPPED", "NOT RUN", "NOT RUN" }, results.Select(r => r.Status));
        Assert.Contains("window was closed", results[1].Error);
    }

    [Fact]
    public async Task Ctrl_C_Stops_The_Run()
    {
        var state = new RunState();
        // Same as the Console.CancelKeyPress handler: record the stop, close the page.
        Steps.Registry["ctrl_c"] = new(async (ctx, _) =>
        {
            state.Stop("interrupted (Ctrl+C)");
            await ctx.Page.CloseAsync();
        }, "test only");
        var results = await RunYamlAsync("""
            scenarios:
              - {name: a, steps: [{goto: /app/bi/coverage}, {ctrl_c: null}, {wait: 100}]}
              - {name: b, steps: [{goto: /app/bi/coverage}]}
            """, Config(), state);
        Assert.Equal(new[] { "STOPPED", "NOT RUN" }, results.Select(r => r.Status));
        Assert.Contains("Ctrl+C", results[0].Error);
    }

    private static string AnnounceFlow(string announce) => $$"""
        scenarios:
          - name: with_popup
            steps:
              - goto: /app/bi/coverage?announce={{announce}}
              - wait: 1800
              - set_date: {from: 2026-08-03, to: 2026-09-14}
              - select_filter: {id: carrier_filter, options: [LUMITEL]}
              - choose_view: macro
              - wait_for_table: {}
              - download_table: {format: csv}
          - name: next_page_load
            steps:
              - open_menu: ["Coverage time"]
              - wait: 1800
              - choose_view: macro
        """;

    private async Task<(List<ScenarioResult> Results, string Log)> RunCapturedAsync(string yaml)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            return (await RunYamlAsync(yaml, Config()), writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task AnnouncementPopup_IsClosedAutomatically()
    {
        var (results, log) = await RunCapturedAsync(AnnounceFlow("1"));
        Assert.Equal(new[] { "PASS", "PASS" }, results.Select(r => r.Status));
        Assert.Contains("closed popup 'New Delta Analysis in Map View", log);
        // Closing it with its own button marks it as seen: it does not come back on the next page load.
        Assert.Single(Regex.Matches(log, "closed popup"));
    }

    [Fact]
    public async Task AnnouncementPopup_WithoutWorkingCloseButton_IsRemoved()
    {
        var (results, log) = await RunCapturedAsync(AnnounceFlow("stuck"));
        Assert.Equal(new[] { "PASS", "PASS" }, results.Select(r => r.Status));
        Assert.Contains("(removed)", log);
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public async Task CoverageTime_MonthlyScenarios()
    {
        // The real scenarios/coverage_time.yaml: 2 levels x 3 technologies, named files.
        var config = Config();
        var results = await Runner.RunAsync(ScenarioLoader.Load(new[] { RepoFile("scenarios/coverage_time.yaml") }),
            config, new RunOptions());
        Assert.True(results.All(r => r.Status == "PASS"), string.Join("; ", results.Select(r => $"{r.Scenario}: {r.Error}")));
        Assert.Equal(6, results.Count);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var last = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var first = new DateOnly(last.Year, last.Month, 1);
        var coverage = new Dictionary<string, string>
        {
            ["All"] = "5G_SA|5G_NSA_CONNECTED|5G_NSA_NOT_RESTRICTED|5G_NSA_RESTRICTED|4G|3G|2G",
            ["5G"] = "5G_SA|5G_NSA_CONNECTED|5G_NSA_NOT_RESTRICTED|5G_NSA_RESTRICTED",
            ["4G"] = "4G",
        };
        foreach (var (level, view) in new[] { ("Net", "byCountry"), ("Province", "byRegions") })
        {
            foreach (var (tech, cov) in coverage)
            {
                var file = Path.Combine(config.OutputDir, $"VTB_{last.Year}_T{last.Month}_Coverage time_{level}_{tech}.xlsx");
                Assert.True(File.Exists(file), file);
                var data = ReadXlsx(file);
                Assert.Equal("bi", data["country"]);
                Assert.Equal($"{first:yyyy-MM-dd}..{last:yyyy-MM-dd}", data["date"]);
                Assert.Equal("ECONET|LUMITEL|ONAMOB|SMART", data["carrier"]);
                Assert.Equal(cov, data["coverage"]);
                Assert.Equal(view, data["view"]);
            }
        }
    }

    [Theory]
    [InlineData("network_availability.yaml", "Network availability", "/app/bi/networkAvailabilityMobile", true)]
    [InlineData("sample.yaml", "Sample", "/app/bi/sample", false)]
    [InlineData("speed_test_throughput.yaml", "Speed test Throughput", "/app/bi/speedTestThruMobile", true)]
    [InlineData("web_performance_times.yaml", "Web performance times", "/app/bi/webPerformanceTimesMobile", true)]
    [InlineData("video_streaming_times.yaml", "Video Streaming times", "/app/bi/youtubeTimesMobile", true)]
    public async Task MonthlyPageScenarios(string fileName, string kpi, string path, bool split)
    {
        var config = Config();
        var results = await Runner.RunAsync(ScenarioLoader.Load(new[] { RepoFile($"scenarios/{fileName}") }),
            config, new RunOptions());
        var techs = split ? new[] { "All", "5G", "4G" } : new[] { "All" };
        Assert.True(results.All(r => r.Status == "PASS"), string.Join("; ", results.Select(r => $"{r.Scenario}: {r.Error}")));
        Assert.Equal(2 * techs.Length, results.Count);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var last = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        foreach (var (level, view) in new[] { ("Net", "byCountry"), ("Province", "byRegions") })
        {
            foreach (var tech in techs)
            {
                var file = Path.Combine(config.OutputDir, $"VTB_{last.Year}_T{last.Month}_{kpi}_{level}_{tech}.xlsx");
                Assert.True(File.Exists(file), file);
                var data = ReadXlsx(file);
                Assert.Equal(path, data["kpi"]);
                Assert.Equal(view, data["view"]);
                Assert.Equal("ECONET|LUMITEL|ONAMOB|SMART", data["carrier"]);
                if (tech == "4G") Assert.Equal("4G", data["coverage"]);
            }
        }
    }
}
