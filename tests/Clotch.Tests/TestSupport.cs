using System.Net;
using Clotch.Core.Credentials;
using Clotch.Core.Usage;

namespace Clotch.Tests;

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clotch-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string content)
    {
        var p = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class FakeApi : IUsageApi
{
    public Queue<UsageFetchResult> Results { get; } = new();
    public int Calls { get; private set; }

    public Task<UsageFetchResult> FetchAsync(OAuthCredentials credentials, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : UsageFetchResult.Fail(UsageFetchStatus.ServerError, 500));
    }
}

internal sealed class FixedCredentials(OAuthCredentials? creds) : ICredentialsSource
{
    public OAuthCredentials? Value { get; set; } = creds;
    public OAuthCredentials? Read() => Value;
}

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string CredentialsJson(string token = "sk-ant-oat01-TESTTOKEN", long? expiresAtMs = null, string? subscription = "pro") =>
        $$$"""
        {"claudeAiOauth":{"accessToken":"{{{token}}}","refreshToken":"sk-ant-ort01-REFRESH","expiresAt":{{{expiresAtMs ?? DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds()}}},"scopes":["user:inference","user:profile"],"subscriptionType":{{{(subscription is null ? "null" : $"\"{subscription}\"")}}},"rateLimitTier":"default_claude_ai"}}
        """;

    public static OAuthCredentials Creds(DateTimeOffset expires, string? sub = "pro") =>
        new(new SecretToken("sk-ant-oat01-TESTTOKEN"), expires, sub, "default_claude_ai", []);

    public static UsageSnapshot Snapshot(DateTimeOffset at) => UsageParser.Parse(Read("usage_real.json"), at);

    public static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
