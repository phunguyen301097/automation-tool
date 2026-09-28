using Xunit;

namespace WeplanExport.Tests;

public class ConfigTests
{
    private static readonly DateLimits Limits = new(new DateOnly(2025, 9, 23), new DateOnly(2026, 9, 23));

    [Theory]
    [InlineData("2026-08-01", 2026, 8, 1)]
    [InlineData("01/08/2026", 2026, 8, 1)]
    [InlineData("max", 2026, 9, 23)]
    [InlineData("max-30d", 2026, 8, 24)]
    [InlineData("max-1m", 2026, 8, 23)]
    [InlineData("min+1w", 2025, 9, 30)]
    [InlineData("today-1d", 2026, 1, 9)]
    public void ParseDate(string input, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), DateParser.Parse(input, Limits, today: new DateOnly(2026, 1, 10)));

    [Fact]
    public void ParseDate_Invalid_Throws() =>
        Assert.Throws<StepException>(() => DateParser.Parse("next tuesday", Limits));

    [Fact]
    public void Matrix_Vars_Before_After_Skip_And_Filter()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            vars: {fmt: xlsx}
            before: [{goto: /x}]
            after: [{screenshot: end}]
            scenarios:
              - name: a_${c}
                tags: [t1]
                matrix: {c: [X, Y]}
                steps:
                  - select_country: ${c}
                  - download_table: {format: "${fmt}", filename: "${c}_${date_from}"}
              - name: skipped
                skip: true
                steps: []
              - name: plain
                matrix: {n: [1, 2]}
                steps: []
            """);
        var sc = ScenarioLoader.Load(new[] { file });
        File.Delete(file);

        Assert.Equal(new[] { "a_X", "a_Y", "plain[1]", "plain[2]" }, sc.Select(s => s.Name));
        var steps = sc[1].Steps;
        Assert.Equal(new[] { "goto", "select_country", "download_table", "screenshot" }, steps.Select(s => s.Name));
        Assert.Equal("Y", steps[1].Args);
        var dl = Assert.IsType<Dictionary<string, object?>>(steps[2].Args);
        Assert.Equal("xlsx", dl["format"]);
        Assert.Equal("Y_${date_from}", dl["filename"]);

        Assert.Equal(new[] { "a_X", "a_Y" }, ScenarioLoader.Filter(sc, new() { "a_*" }, new()).Select(s => s.Name));
        Assert.Equal(new[] { "a_X", "a_Y" }, ScenarioLoader.Filter(sc, new(), new() { "t1" }).Select(s => s.Name));
    }

    [Fact]
    public void Config_Partial_File_Keeps_Defaults()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            output_dir: out
            date: {input_format: dd/MM/yyyy}
            selectors: {download_button_text: Export}
            views: {custom: "#byX"}
            """);
        var cfg = AppConfig.Load(file);
        File.Delete(file);

        Assert.Equal("out", cfg.OutputDir);
        Assert.Equal("dd/MM/yyyy", cfg.Date.InputFormat);
        Assert.Equal(" - ", cfg.Date.RangeSeparator);
        Assert.Equal("Export", cfg.Selectors.DownloadButtonText);
        Assert.Equal("#tableProvinces", cfg.Selectors.TableContainer);
        Assert.Equal("#byCountry", cfg.Views["macro"]);
        Assert.Equal("#byX", cfg.Views["custom"]);
    }

    [Fact]
    public void MatrixValues_CanBeMappings()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            vars: {kpi: Coverage time}
            scenarios:
              - name: ${kpi}_${level.name}
                matrix:
                  level:
                    - {name: Net, opts: [4G, 3G]}
                steps:
                  - select_filter: {id: x, options: "${level.opts}"}
                  - download_table: {filename: "${kpi}_${level.name}_${year}"}
              - name: plain
                matrix: {t: [{name: 5G}]}
                steps: []
            """);
        var sc = ScenarioLoader.Load(new[] { file });
        File.Delete(file);

        Assert.Equal(new[] { "Coverage time_Net", "plain[5G]" }, sc.Select(s => s.Name));
        var select = Assert.IsType<Dictionary<string, object?>>(sc[0].Steps[0].Args);
        Assert.Equal(new[] { "4G", "3G" }, Args.StrList(select["options"]));
        var download = Assert.IsType<Dictionary<string, object?>>(sc[0].Steps[1].Args);
        // ${year} is only known at run time and stays for later.
        Assert.Equal("Coverage time_Net_${year}", download["filename"]);
    }

    [Fact]
    public void FileNames_KeepSpaces()
    {
        Assert.Equal("VTB_2026_T8_Coverage time_Net_All", Steps.SafeFileName("VTB_2026_T8_Coverage time_Net_All"));
        Assert.Equal("a_b_c", Steps.SafeFileName("a:b?c"));
    }

    [Fact]
    public void Markets_FromGlobalVars()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            scenarios:
              - name: ${market.code}_${t}
                matrix: {market: "${markets}", t: [a, b]}
                steps: [{select_country: "${market.country}"}]
            """);
        var markets = new List<object?>
        {
            new Dictionary<string, object?> { ["code"] = "VTC", ["country"] = "kh" },
            new Dictionary<string, object?> { ["code"] = "VTB", ["country"] = "bi" },
        };
        var sc = ScenarioLoader.Load(new[] { file }, new Dictionary<string, object?> { ["markets"] = markets });
        Assert.Equal(new[] { "VTC_a", "VTC_b", "VTB_a", "VTB_b" }, sc.Select(s => s.Name));
        Assert.Equal("bi", sc[2].Steps[0].Args);
        var reordered = ScenarioLoader.OrderByMarket(Enumerable.Reverse(sc).ToList(), markets);
        Assert.Equal(new[] { "VTC_b", "VTC_a", "VTB_b", "VTB_a" }, reordered.Select(s => s.Name));
        Assert.Throws<StepException>(() => ScenarioLoader.Load(new[] { file })); // no markets defined
        File.Delete(file);
    }

    [Fact]
    public void Config_ReadsMarkets()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            vars:
              markets:
                - {code: VTC, country: kh}
            """);
        var cfg = AppConfig.Load(file);
        File.Delete(file);
        var markets = Assert.IsType<List<object?>>(cfg.Vars["markets"]);
        var first = Assert.IsType<Dictionary<string, object?>>(markets[0]);
        Assert.Equal("kh", first["country"]);
    }

    [Fact]
    public void ExcludeFilter()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, """
            scenarios:
              - name: ${m}_${k}
                matrix: {m: [VTB, VTC], k: [Coverage time, Sample]}
                steps: []
            """);
        var sc = ScenarioLoader.Load(new[] { file });
        File.Delete(file);
        Assert.Equal(new[] { "VTB_Coverage time", "VTC_Coverage time", "VTC_Sample" },
            ScenarioLoader.Filter(sc, new(), new(), new() { "VTB_Sample" }).Select(s => s.Name));
        Assert.Equal(new[] { "VTC_Coverage time" },
            ScenarioLoader.Filter(sc, new() { "VTC_*" }, new(), new() { "*_Sample" }).Select(s => s.Name));
    }

    [Fact]
    public void DefaultOrder_IsPageThenMarket()
    {
        // Load order = file (page) by file, all markets of a page before the next page.
        var dir = Directory.CreateTempSubdirectory("weplan-order").FullName;
        var files = new List<string>();
        foreach (var kpi in new[] { "coverage", "sample" })
        {
            var f = Path.Combine(dir, kpi + ".yaml");
            File.WriteAllText(f, $$"""
                scenarios:
                  - name: ${market.code}_{{kpi}}_${lvl}
                    matrix: {market: "${markets}", lvl: [Net, Province]}
                    steps: []
                """);
            files.Add(f);
        }
        var markets = new List<object?>
        {
            new Dictionary<string, object?> { ["code"] = "VTC", ["country"] = "kh" },
            new Dictionary<string, object?> { ["code"] = "VTB", ["country"] = "bi" },
        };
        var names = ScenarioLoader.Load(files, new Dictionary<string, object?> { ["markets"] = markets }).Select(s => s.Name);
        Directory.Delete(dir, true);
        Assert.Equal(new[]
        {
            "VTC_coverage_Net", "VTC_coverage_Province", "VTB_coverage_Net", "VTB_coverage_Province",
            "VTC_sample_Net", "VTC_sample_Province", "VTB_sample_Net", "VTB_sample_Province",
        }, names);
    }

    [Fact]
    public void Throttle_Pause_Formats()
    {
        Assert.Equal((30d, 30d), ThrottleConfig.Range("30"));
        Assert.Equal((30d, 60d), ThrottleConfig.Range("30-60"));
        Assert.Equal((30d, 60d), ThrottleConfig.Range(new List<object?> { "30", "60" }));
        Assert.Equal((0d, 0d), ThrottleConfig.Range(null));
        Assert.Equal((45d, 45d), ThrottleConfig.Range(45));
        Assert.Throws<StepException>(() => ThrottleConfig.Range("abc"));
        Assert.Throws<StepException>(() => ThrottleConfig.Range("60-30"));

        var path = Path.GetTempFileName();
        File.WriteAllText(path, "throttle:\n  pause_between: [30, 60]\n  pause_after_market: 300-600\n  max_exports: 100\n");
        var cfg = AppConfig.Load(path);
        File.Delete(path);
        Assert.Equal((30d, 60d), ThrottleConfig.Range(cfg.Throttle.PauseBetween));
        Assert.Equal((300d, 600d), ThrottleConfig.Range(cfg.Throttle.PauseAfterMarket));
        Assert.Equal(100, cfg.Throttle.MaxExports);
    }
}
