using System.Net;
using System.Net.Http.Headers;
using Clotch.Core.Credentials;
using Clotch.Core.Diagnostics;

namespace Clotch.Core.Usage;

public enum UsageFetchStatus { Ok, Unauthorized, RateLimited, ServerError, Offline, BadResponse }

public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot, int? HttpStatus, TimeSpan? RetryAfter)
{
    public static UsageFetchResult Fail(UsageFetchStatus s, int? code = null, TimeSpan? retry = null) => new(s, null, code, retry);
}

public interface IUsageApi
{
    Task<UsageFetchResult> FetchAsync(OAuthCredentials credentials, CancellationToken ct);
}

/// <summary>Client for the endpoint behind Claude Code's /usage command.</summary>
public sealed class UsageClient(HttpClient http, TimeProvider time) : IUsageApi
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    public const string BetaHeader = "oauth-2025-04-20";

    public static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<UsageFetchResult> FetchAsync(OAuthCredentials credentials, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken.Value);
        req.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("clotch", "0.1"));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"usage request failed: {ex.GetType().Name}");
            return UsageFetchResult.Fail(UsageFetchStatus.Offline);
        }

        using (resp)
        {
            var code = (int)resp.StatusCode;
            Log.Info($"usage request -> HTTP {code}");
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try
                    {
                        return new UsageFetchResult(UsageFetchStatus.Ok, UsageParser.Parse(body, time.GetUtcNow()), code, null);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        Log.Warn("usage response was not valid JSON");
                        return UsageFetchResult.Fail(UsageFetchStatus.BadResponse, code);
                    }
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return UsageFetchResult.Fail(UsageFetchStatus.Unauthorized, code);
                case HttpStatusCode.TooManyRequests:
                    return UsageFetchResult.Fail(UsageFetchStatus.RateLimited, code, RetryAfter(resp));
                default:
                    return UsageFetchResult.Fail(UsageFetchStatus.ServerError, code, RetryAfter(resp));
            }
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date - time.GetUtcNow();
        return null;
    }
}
