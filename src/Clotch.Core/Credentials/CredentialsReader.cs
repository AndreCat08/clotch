using System.Text.Json;

namespace Clotch.Core.Credentials;

/// <summary>Bearer token wrapper whose ToString never reveals the value.</summary>
public sealed class SecretToken(string value)
{
    internal string Value { get; } = value;
    public override string ToString() => "[redacted]";
}

public sealed record OAuthCredentials(
    SecretToken AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier,
    IReadOnlyList<string> Scopes)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now.AddSeconds(30);

    /// <summary>True when the stored plan is a paid Claude subscription (pro, max, team, enterprise...).</summary>
    public bool HasPaidSubscription =>
        !string.IsNullOrWhiteSpace(SubscriptionType) &&
        !string.Equals(SubscriptionType, "free", StringComparison.OrdinalIgnoreCase);
}

public interface ICredentialsSource
{
    /// <summary>Returns null when no usable Claude Code OAuth login exists.</summary>
    OAuthCredentials? Read();
}

/// <summary>
/// Reads Claude Code's ~/.claude/.credentials.json strictly read-only.
/// Clotch never writes this file and never refreshes the token: Claude Code owns the token lifecycle,
/// and rotating the refresh token from here could log the user out of Claude Code.
/// </summary>
public sealed class CredentialsReader(string credentialsPath) : ICredentialsSource
{
    public string Path { get; } = credentialsPath;

    public OAuthCredentials? Read()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(Path)) return null;
                using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                return Parse(doc.RootElement);
            }
            catch (IOException)
            {
                // Claude Code may be rewriting the file right now.
                Thread.Sleep(100);
            }
            catch (JsonException)
            {
                // Half-written file: retry once or twice, then give up.
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    internal static OAuthCredentials? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("claudeAiOauth", out var o) ||
            o.ValueKind != JsonValueKind.Object)
            return null;

        var token = o.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token)) return null;

        DateTimeOffset? expires = null;
        if (o.TryGetProperty("expiresAt", out var e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms))
                expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            else if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), out var dt))
                expires = dt;
        }

        var scopes = o.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

        return new OAuthCredentials(
            new SecretToken(token),
            expires,
            Str(o, "subscriptionType"),
            Str(o, "rateLimitTier"),
            scopes);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
