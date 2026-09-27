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

    private async Task<List<ScenarioResult>> RunYamlAsync(string yaml, AppConfig config)
    {
        var file = Path.Combine(_tmp, "s.yaml");
        await File.WriteAllTextAsync(file, yaml);
        return await Runner.RunAsync(ScenarioLoader.Load(new[] { file }), config, new RunOptions());
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
}
