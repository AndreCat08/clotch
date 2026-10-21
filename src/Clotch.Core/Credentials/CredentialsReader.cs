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
public sealed class CredentialsReader(string credentialsPath, string? accountConfigPath = null) : ICredentialsSource
{
    public string Path { get; } = credentialsPath;

    /// <summary>Claude Code's global config (~/.claude.json). Its oauthAccount block is the plan fallback.</summary>
    public string? AccountConfigPath { get; } = accountConfigPath;

    public OAuthCredentials? Read()
    {
        var creds = ReadCredentialsFile();
        if (creds is null || AccountConfigPath is null) return creds;
        if (!string.IsNullOrWhiteSpace(creds.SubscriptionType) && !string.IsNullOrWhiteSpace(creds.RateLimitTier))
            return creds;

        // Tokens minted by a refresh or by another OAuth client (setup-token, third-party agents) are written
        // without subscriptionType/rateLimitTier. Claude Code still caches the plan in ~/.claude.json.
        var account = ReadAccountPlan(AccountConfigPath);
        if (account is null) return creds;
        return creds with
        {
            SubscriptionType = string.IsNullOrWhiteSpace(creds.SubscriptionType) ? account.Value.Plan : creds.SubscriptionType,
            RateLimitTier = string.IsNullOrWhiteSpace(creds.RateLimitTier) ? account.Value.Tier : creds.RateLimitTier,
        };
    }

    internal static (string? Plan, string? Tier)? ReadAccountPlan(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return ParseAccountPlan(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static (string? Plan, string? Tier)? ParseAccountPlan(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("oauthAccount", out var a) ||
            a.ValueKind != JsonValueKind.Object)
            return null;

        var plan = NormalizePlan(Str(a, "organizationType"));
        var tier = Str(a, "userRateLimitTier") is { Length: > 0 } u ? u : Str(a, "organizationRateLimitTier");
        return plan is null && tier is null ? null : (plan, tier);
    }

    /// <summary>"claude_pro" -> "pro", "claude_max" -> "max"; anything else is lower-cased as-is.</summary>
    internal static string? NormalizePlan(string? organizationType)
    {
        if (string.IsNullOrWhiteSpace(organizationType)) return null;
        var s = organizationType.Trim().ToLowerInvariant();
        return s.StartsWith("claude_", StringComparison.Ordinal) ? s[7..] : s;
    }

    private OAuthCredentials? ReadCredentialsFile()
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
