using Clotch.Core.Detection;
using Clotch.Core.Gating;
using Clotch.Core.Local;
using Clotch.Core.Usage;

namespace Clotch.Tests;

[Trait("Category", "Poller")]
public class UsageMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static UsageFetchResult Ok(DateTimeOffset at) => new(UsageFetchStatus.Ok, Fixtures.Snapshot(at), 200, null);
    private static UsageFetchResult RateLimited => UsageFetchResult.Fail(UsageFetchStatus.RateLimited, 429);

    [Fact]
    public async Task Backoff_doubles_from_5_to_30_minutes_and_resets_on_success()
    {
        var time = new FakeTime(T0);
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(Fixtures.Creds(T0.AddDays(1))), api, time, new MonitorOptions());

        api.Results.Enqueue(Ok(T0));
        await m.PollOnceAsync(default);
        var delays = new List<double> { m.NextDelay().TotalMinutes };

        for (var i = 0; i < 4; i++)
        {
            api.Results.Enqueue(RateLimited);
            await m.PollOnceAsync(default);
            delays.Add(m.NextDelay().TotalMinutes);
        }

        Assert.Equal([5d, 10d, 20d, 30d, 30d], delays);
        Assert.Equal(MonitorStatus.RateLimited, m.State.Status);
        Assert.True(m.State.IsStale);
        Assert.NotNull(m.State.LastGood); // cached numbers survive failures

        api.Results.Enqueue(Ok(T0));
        await m.PollOnceAsync(default);
        Assert.Equal(5d, m.NextDelay().TotalMinutes);
        Assert.False(m.State.IsStale);
    }

    [Fact]
    public async Task Retry_after_longer_than_backoff_is_respected_but_capped()
    {
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(Fixtures.Creds(T0.AddDays(1))), api, new FakeTime(T0), new MonitorOptions());
        api.Results.Enqueue(UsageFetchResult.Fail(UsageFetchStatus.RateLimited, 429, TimeSpan.FromMinutes(25)));
        await m.PollOnceAsync(default);
        Assert.Equal(25d, m.NextDelay().TotalMinutes);

        api.Results.Enqueue(UsageFetchResult.Fail(UsageFetchStatus.RateLimited, 429, TimeSpan.FromHours(3)));
        await m.PollOnceAsync(default);
        Assert.Equal(30d, m.NextDelay().TotalMinutes);
    }

    [Fact]
    public async Task Expired_token_makes_no_request()
    {
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(Fixtures.Creds(T0.AddMinutes(-1))), api, new FakeTime(T0), new MonitorOptions());
        await m.PollOnceAsync(default);

        Assert.Equal(0, api.Calls);
        Assert.Equal(MonitorStatus.TokenExpired, m.State.Status);
        Assert.Equal(TimeSpan.FromMinutes(1), m.NextDelay());
    }

    [Fact]
    public async Task Missing_credentials_makes_no_request()
    {
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(null), api, new FakeTime(T0), new MonitorOptions());
        await m.PollOnceAsync(default);

        Assert.Equal(0, api.Calls);
        Assert.Equal(MonitorStatus.NoCredentials, m.State.Status);
    }

    [Fact]
    public async Task Manual_refresh_is_throttled_to_once_per_minute()
    {
        var time = new FakeTime(T0);
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(Fixtures.Creds(T0.AddDays(1))), api, time, new MonitorOptions());
        api.Results.Enqueue(Ok(T0));
        await m.PollOnceAsync(default);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(m.RequestRefresh());
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(m.RequestRefresh());
    }

    [Fact]
    public void Poll_interval_never_below_two_minutes()
    {
        var o = new MonitorOptions { PollInterval = TimeSpan.FromSeconds(30) };
        Assert.Equal(TimeSpan.FromMinutes(2), o.EffectiveInterval);
    }

    [Fact]
    public async Task One_hour_of_real_cadence_stays_within_13_requests()
    {
        var time = new FakeTime(T0);
        var api = new FakeApi();
        using var m = new UsageMonitor(new FixedCredentials(Fixtures.Creds(T0.AddDays(1))), api, time, new MonitorOptions());
        var end = T0.AddHours(1);
        while (time.Now <= end)
        {
            api.Results.Enqueue(Ok(time.Now));
            await m.PollOnceAsync(default);
            time.Advance(m.NextDelay());
        }
        Assert.InRange(api.Calls, 1, 13);
    }
}

[Trait("Category", "Local")]
public class LocalUsageScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);

    private static string Line(string ts, string model, string msgId, string req, int input, int output, int cc = 0, int cr = 0) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = ts,
            requestId = req,
            uuid = Guid.NewGuid().ToString(),
            message = new
            {
                id = msgId,
                model,
                usage = new { input_tokens = input, output_tokens = output, cache_creation_input_tokens = cc, cache_read_input_tokens = cr },
            },
        });

    [Fact]
    public void Aggregates_today_and_window_with_dedupe_and_broken_lines()
    {
        using var d = new TempDir();
        var lines = new[]
        {
            Line("2026-10-20T13:00:00Z", "claude-opus-5-5", "m1", "r1", 10, 100, 5, 1000),
            Line("2026-10-20T13:00:00Z", "claude-opus-5-5", "m1", "r1", 10, 100, 5, 1000), // duplicate content block
            Line("2026-10-20T07:00:00Z", "claude-sonnet-5", "m2", "r2", 1, 2),             // today, outside 5h
            Line("2026-10-19T23:00:00Z", "claude-sonnet-5", "m3", "r3", 1000, 1000),       // yesterday
            Line("2026-10-20T13:30:00Z", "<synthetic>", "m4", "r4", 99, 99),
            "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
            "{not json \"type\":\"assistant\" \"usage\"",
        };
        d.Write(Path.Combine("proj-a", "s1.jsonl"), string.Join("\n", lines) + "\n");

        var scanner = new LocalUsageScanner(d.Path, new FakeTime(Now));
        var s = scanner.Scan(Now.AddHours(-5));

        Assert.Equal(2, s.Today.Count);
        var opus = s.Today.Single(m => m.Model == "claude-opus-5-5");
        Assert.Equal((10L, 100L, 5L, 1000L, 1), (opus.Input, opus.Output, opus.CacheCreate, opus.CacheRead, opus.Messages));
        Assert.Equal(3, s.Today.Single(m => m.Model == "claude-sonnet-5").Total);
        Assert.Equal(1115, s.WindowTotal);
        Assert.Single(s.Window);
    }

    [Fact]
    public void Reads_incrementally_and_ignores_partial_last_line()
    {
        using var d = new TempDir();
        var file = d.Write("p/s.jsonl", Line("2026-10-20T13:00:00Z", "m", "a", "1", 1, 1) + "\n" + Line("2026-10-20T13:01:00Z", "m", "b", "2", 5, 5)[..40]);
        var scanner = new LocalUsageScanner(d.Path, new FakeTime(Now));

        Assert.Equal(2, scanner.Scan().TodayTotal);

        // Finish the partial line and append another.
        File.WriteAllText(file, Line("2026-10-20T13:00:00Z", "m", "a", "1", 1, 1) + "\n" +
                                Line("2026-10-20T13:01:00Z", "m", "b", "2", 5, 5) + "\n");
        Assert.Equal(12, scanner.Scan().TodayTotal);

        File.AppendAllText(file, Line("2026-10-20T13:02:00Z", "m", "c", "3", 100, 0) + "\n");
        Assert.Equal(112, scanner.Scan().TodayTotal);
        Assert.Equal(112, scanner.Scan().TodayTotal); // re-scan without changes is stable
    }

    [Fact]
    public void Missing_projects_dir_is_empty()
    {
        var s = new LocalUsageScanner(@"Z:\does\not\exist", new FakeTime(Now)).Scan();
        Assert.Empty(s.Today);
        Assert.Equal(0, s.FilesTracked);
    }
}

[Trait("Category", "Detection")]
public class DetectionTests
{
    private static DetectionEnvironment Env(TempDir d, string? path = null, IReadOnlyList<string>? packages = null) =>
        new(d.Combine(".claude"), path, d.Combine("Roaming"), d.Combine("Local"), d.Combine("Home"), () => packages ?? []);

    [Fact]
    public void Nothing_installed()
    {
        using var d = new TempDir();
        var r = new DetectionService(Env(d)).Detect();
        Assert.False(r.ClaudeCode.Installed);
        Assert.False(r.ClaudeDesktop.Installed);
        Assert.False(r.AnyInstalled);
    }

    [Fact]
    public void Claude_code_via_npm_shim_with_version()
    {
        using var d = new TempDir();
        d.Write(Path.Combine("Roaming", "npm", "claude.cmd"), "@echo off");
        d.Write(Path.Combine("Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code", "package.json"), "{\"version\":\"2.1.295\"}");
        Directory.CreateDirectory(d.Combine(".claude"));

        var r = new DetectionService(Env(d)).Detect();
        Assert.True(r.ClaudeCode.Installed);
        Assert.Equal("2.1.295", r.ClaudeCode.Version);
        Assert.True(r.ClaudeCode.ConfigDirExists);
        Assert.False(r.ClaudeDesktop.Installed);
    }

    [Fact]
    public void Claude_code_native_exe_on_path()
    {
        using var d = new TempDir();
        d.Write(Path.Combine("bin", "claude.exe"), "");
        var r = new DetectionService(Env(d, path: d.Combine("bin") + ";C:\\nope")).Detect();
        Assert.True(r.ClaudeCode.Installed);
        Assert.EndsWith("claude.exe", r.ClaudeCode.ExecutablePath);
    }

    [Fact]
    public void Desktop_msix_from_package_registry_picks_highest_version()
    {
        using var d = new TempDir();
        var r = new DetectionService(Env(d, packages:
        [
            "Claude_1.7196.1.0_x64__pzs8sxrjxfjjc",
            "Claude_2.42304.2.0_x64__pzs8sxrjxfjjc",
            "Claude_9.0.0.0_x64__someoneelse",
            "Microsoft.Something_1.0.0.0_x64__8wekyb3d8bbwe",
        ])).Detect();

        Assert.True(r.ClaudeDesktop.Installed);
        Assert.Equal(DesktopInstallKind.Msix, r.ClaudeDesktop.Kind);
        Assert.Equal("2.42304.2.0", r.ClaudeDesktop.Version);
        Assert.False(r.ClaudeCode.Installed);
    }

    [Fact]
    public void Desktop_msix_from_package_data_folder()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Combine("Local", "Packages", "Claude_pzs8sxrjxfjjc"));
        var r = new DetectionService(Env(d)).Detect();
        Assert.Equal(DesktopInstallKind.Msix, r.ClaudeDesktop.Kind);
    }

    [Fact]
    public void Desktop_legacy_squirrel()
    {
        using var d = new TempDir();
        Directory.CreateDirectory(d.Combine("Local", "AnthropicClaude", "app-0.9.3"));
        Directory.CreateDirectory(d.Combine("Local", "AnthropicClaude", "app-0.10.1"));
        var r = new DetectionService(Env(d)).Detect();
        Assert.Equal(DesktopInstallKind.Squirrel, r.ClaudeDesktop.Kind);
        Assert.Equal("0.10.1", r.ClaudeDesktop.Version);
    }
}

[Trait("Category", "Gate")]
public class GateTests
{
    private static readonly DetectionResult Both = new(new(true, "c", "1", true), new(true, DesktopInstallKind.Msix, "2"));
    private static readonly DetectionResult DesktopOnly = new(new(false, null, null, false), new(true, DesktopInstallKind.Msix, "2"));
    private static readonly DetectionResult CodeOnly = new(new(true, "c", "1", true), new(false, DesktopInstallKind.None, null));
    private static readonly DetectionResult None = new(new(false, null, null, false), new(false, DesktopInstallKind.None, null));

    private static MonitorState St(MonitorStatus s, string? sub = "pro", bool cached = false) =>
        MonitorState.Initial with
        {
            Status = s,
            SubscriptionType = sub,
            LastGood = cached ? Fixtures.Snapshot(DateTimeOffset.UtcNow) : null,
        };

    [Fact]
    public void Not_installed_hides_notch() =>
        Assert.Equal((false, GateReason.NotInstalled), Pair(Gate.Evaluate(None, St(MonitorStatus.Ok))));

    [Fact]
    public void Desktop_only_without_code_login_hides_and_asks_for_login()
    {
        var g = Gate.Evaluate(DesktopOnly, St(MonitorStatus.NoCredentials, sub: null));
        Assert.Equal((false, GateReason.DesktopOnlyNeedsCodeLogin), Pair(g));
        Assert.Contains("Login Claude Code diperlukan", g.Message);
    }

    [Fact]
    public void Code_installed_but_not_logged_in() =>
        Assert.Equal((false, GateReason.NotLoggedIn), Pair(Gate.Evaluate(CodeOnly, St(MonitorStatus.NoCredentials, sub: null))));

    [Theory]
    [InlineData("free")]
    [InlineData(null)]
    public void Free_or_unknown_plan_hides(string? sub) =>
        Assert.Equal((false, GateReason.NoPaidSubscription), Pair(Gate.Evaluate(Both, St(MonitorStatus.Ok, sub))));

    [Fact]
    public void Unauthorized_without_cache_hides() =>
        Assert.Equal((false, GateReason.LoginInvalid), Pair(Gate.Evaluate(Both, St(MonitorStatus.Unauthorized))));

    [Theory]
    [InlineData(MonitorStatus.Ok, false)]
    [InlineData(MonitorStatus.RateLimited, true)]
    [InlineData(MonitorStatus.TokenExpired, false)]
    [InlineData(MonitorStatus.Unauthorized, true)]
    public void Active_paid_plan_shows(MonitorStatus s, bool cached) =>
        Assert.Equal((true, GateReason.Active), Pair(Gate.Evaluate(Both, St(s, "max", cached))));

    [Fact]
    public void Desktop_only_with_code_credentials_still_shows() =>
        Assert.True(Gate.Evaluate(DesktopOnly, St(MonitorStatus.Ok)).ShowNotch);

    private static (bool, GateReason) Pair(GateDecision g) => (g.ShowNotch, g.Reason);
}

[Trait("Category", "Gate")]
public class FormatTests
{
    [Theory]
    [InlineData(0, 0, 30, "<1m")]
    [InlineData(0, 12, 0, "12m")]
    [InlineData(2, 40, 0, "2j 40m")]
    [InlineData(76, 0, 0, "3h 4j")]
    public void Countdown(int h, int m, int s, string expected) =>
        Assert.Equal(expected, Format.Countdown(new TimeSpan(h, m, s)));

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("unknown", "Unknown")]
    public void Model_names(string raw, string expected) => Assert.Equal(expected, Format.Model(raw));

    [Fact]
    public void Tokens_compact() =>
        Assert.Equal(["999", "1.5K", "2.35M"], new[] { 999L, 1500L, 2_350_000L }.Select(Format.Tokens));
}

[Trait("Category", "Settings")]
public class SettingsTests
{
    [Fact]
    public void AutoStart_writes_and_removes_run_value()
    {
        var key = @"Software\Clotch.Tests\" + Guid.NewGuid().ToString("N");
        var a = new Clotch.Core.Settings.AutoStart(key, "Clotch", "\"C:\\x\\Clotch.exe\"");
        try
        {
            Assert.False(a.IsEnabled);
            a.Set(true);
            Assert.True(a.IsEnabled);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\x\\Clotch.exe\"", k!.GetValue("Clotch"));
            a.Set(false);
            Assert.False(a.IsEnabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Clotch.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Settings_roundtrip_and_clamp_interval()
    {
        using var d = new TempDir();
        var store = new Clotch.Core.Settings.SettingsStore(d.Combine("s", "settings.json"));
        Assert.False(store.Load().NotchHidden);
        store.Save(new Clotch.Core.Settings.AppSettings { NotchCenterX = 599, NotchTop = 268, NotchHidden = true, PollMinutes = 1 });
        var s = store.Load();
        Assert.Equal((599, 268, true, 2), (s.NotchCenterX, s.NotchTop, s.NotchHidden, s.PollMinutes));
    }
}
