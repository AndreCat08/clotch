using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Clotch.Core.Credentials;
using Clotch.Core.Diagnostics;
using Clotch.Core.Usage;

namespace Clotch.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Init() =>
        Log.UseDirectory(Path.Combine(Path.GetTempPath(), "clotch-test-logs", Guid.NewGuid().ToString("N")));
}

[Trait("Category", "Usage")]
public class UsageParserTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_real_response_shape()
    {
        var s = UsageParser.Parse(Fixtures.Read("usage_real.json"), At);

        Assert.NotNull(s.FiveHour);
        Assert.Equal(15.0, s.FiveHour!.Utilization);
        Assert.Equal("2026-10-20T17:40", s.FiveHour.ResetsAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm"));
        Assert.Equal(26.0, s.SevenDay!.Utilization);
        Assert.Equal(["five_hour", "seven_day", "iguana_necktie"], s.Windows.Select(w => w.Key));
        Assert.Equal(100, s.Windows[2].LimitDollars);
        Assert.Equal(2, s.Limits.Count);
        Assert.True(s.Limits[1].IsActive);
        Assert.False(s.Extra!.IsEnabled);
        Assert.Equal(4, s.Breakdown.Count);
        Assert.Equal(("Claude Code", 100d), (s.Breakdown[0].DisplayName, s.Breakdown[0].Percent));
    }

    [Fact]
    public void Minimal_object_yields_empty_snapshot()
    {
        var s = UsageParser.Parse("{}", At);
        Assert.Null(s.FiveHour);
        Assert.Null(s.SevenDay);
        Assert.Empty(s.Windows);
        Assert.Empty(s.Limits);
        Assert.Null(s.Extra);
    }

    [Fact]
    public void Tolerates_wrong_types_and_unknown_keys()
    {
        var s = UsageParser.Parse(Fixtures.Read("usage_weird.json"), At);

        Assert.Equal(42.5, s.FiveHour!.Utilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1775325600), s.FiveHour.ResetsAt);
        Assert.Null(s.SevenDay); // utilization null => not a window
        Assert.Null(s.Windows.Single(w => w.Key == "seven_day_sonnet").ResetsAt);
        Assert.Contains(s.Windows, w => w.Key == "brand_new_window" && w.DisplayName == "Brand new window");
        Assert.Empty(s.Limits);
        Assert.True(s.Extra!.IsEnabled);
        Assert.Equal(120.5, s.Extra.UsedCredits);
        Assert.Single(s.Breakdown);
    }

    [Fact]
    public void Non_object_root_is_empty()
    {
        Assert.Empty(UsageParser.Parse("[1,2,3]", At).Windows);
    }
}

[Trait("Category", "Usage")]
public class UsageClientTests
{
    private static readonly FakeTime Time = new(new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero));

    private static (UsageClient, StubHandler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var h = new StubHandler(respond);
        return (new UsageClient(new HttpClient(h), Time), h);
    }

    [Fact]
    public async Task Ok_sends_required_headers_and_parses()
    {
        var (c, h) = Make(_ => Fixtures.Json(HttpStatusCode.OK, Fixtures.Read("usage_real.json")));
        var r = await c.FetchAsync(Fixtures.Creds(Time.Now.AddHours(1)), CancellationToken.None);

        Assert.Equal(UsageFetchStatus.Ok, r.Status);
        Assert.Equal(15, r.Snapshot!.FiveHour!.Utilization);
        var req = Assert.Single(h.Requests);
        Assert.Equal(UsageClient.Endpoint, req.RequestUri);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("sk-ant-oat01-TESTTOKEN", req.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", req.Headers.GetValues("anthropic-beta").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, UsageFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageFetchStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UsageFetchStatus.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, UsageFetchStatus.ServerError)]
    public async Task Maps_status_codes(HttpStatusCode code, UsageFetchStatus expected)
    {
        var (c, _) = Make(_ => Fixtures.Json(code, "{\"error\":{}}"));
        var r = await c.FetchAsync(Fixtures.Creds(Time.Now.AddHours(1)), CancellationToken.None);
        Assert.Equal(expected, r.Status);
        Assert.Null(r.Snapshot);
    }

    [Fact]
    public async Task RateLimited_reads_retry_after()
    {
        var (c, _) = Make(_ =>
        {
            var m = Fixtures.Json(HttpStatusCode.TooManyRequests, "{}");
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(900));
            return m;
        });
        var r = await c.FetchAsync(Fixtures.Creds(Time.Now.AddHours(1)), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(900), r.RetryAfter);
    }

    [Fact]
    public async Task Network_failure_is_offline()
    {
        var (c, _) = Make(_ => throw new HttpRequestException("no route"));
        var r = await c.FetchAsync(Fixtures.Creds(Time.Now.AddHours(1)), CancellationToken.None);
        Assert.Equal(UsageFetchStatus.Offline, r.Status);
    }

    [Fact]
    public async Task Invalid_json_is_bad_response()
    {
        var (c, _) = Make(_ => Fixtures.Json(HttpStatusCode.OK, "<html>"));
        var r = await c.FetchAsync(Fixtures.Creds(Time.Now.AddHours(1)), CancellationToken.None);
        Assert.Equal(UsageFetchStatus.BadResponse, r.Status);
    }
}

[Trait("Category", "Usage")]
public class CredentialsTests
{
    [Fact]
    public void Reads_oauth_block()
    {
        using var d = new TempDir();
        var p = d.Write(".credentials.json", Fixtures.CredentialsJson(expiresAtMs: 1792520241022));
        var c = new CredentialsReader(p).Read();

        Assert.NotNull(c);
        Assert.Equal("pro", c!.SubscriptionType);
        Assert.Equal("default_claude_ai", c.RateLimitTier);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792520241022), c.ExpiresAt);
        Assert.True(c.HasPaidSubscription);
        Assert.Equal("[redacted]", c.AccessToken.ToString());
        Assert.DoesNotContain("TESTTOKEN", c.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"claudeAiOauth\":{}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"\"}}")]
    [InlineData("not json")]
    public void Missing_or_unusable_file_is_null(string? content)
    {
        using var d = new TempDir();
        var p = d.Combine(".credentials.json");
        if (content is not null) File.WriteAllText(p, content);
        Assert.Null(new CredentialsReader(p).Read());
    }

    [Fact]
    public void Free_plan_is_not_paid()
    {
        using var d = new TempDir();
        var p = d.Write(".credentials.json", Fixtures.CredentialsJson(subscription: "free"));
        Assert.False(new CredentialsReader(p).Read()!.HasPaidSubscription);
    }

    [Fact]
    public async Task Credentials_file_is_never_modified_by_polling()
    {
        using var d = new TempDir();
        var p = d.Write(".credentials.json", Fixtures.CredentialsJson());
        var mtime = File.GetLastWriteTimeUtc(p);
        var hash = SHA256.HashData(File.ReadAllBytes(p));

        var time = new FakeTime(DateTimeOffset.UtcNow);
        var handler = new StubHandler(_ => Fixtures.Json(HttpStatusCode.OK, Fixtures.Read("usage_real.json")));
        using var monitor = new UsageMonitor(new CredentialsReader(p), new UsageClient(new HttpClient(handler), time), time, new MonitorOptions());
        for (var i = 0; i < 3; i++)
        {
            await monitor.PollOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(p));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(p)));
    }
}

[Trait("Category", "Usage")]
public class RedactorTests
{
    [Fact]
    public void Removes_tokens_and_bearer()
    {
        var s = Redactor.Redact("Authorization: Bearer sk-ant-oat01-abc_DEF-123 and sk-ant-ort01-zzz {\"accessToken\":\"plain\"}");
        Assert.DoesNotContain("sk-ant-", s);
        Assert.DoesNotContain("Bearer ", s);
        Assert.DoesNotContain("plain", s);
    }
}
