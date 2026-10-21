using Clotch.Core.Detection;
using Clotch.Core.Usage;

namespace Clotch.Core.Gating;

public enum GateReason
{
    Active,
    NotInstalled,
    DesktopOnlyNeedsCodeLogin,
    NotLoggedIn,
    NoPaidSubscription,
    LoginInvalid,
    Checking,
}

public sealed record GateDecision(bool ShowNotch, GateReason Reason, string Message);

/// <summary>Decides whether the notch may appear. Pure function so every state is unit-testable.</summary>
public static class Gate
{
    public static GateDecision Evaluate(DetectionResult detection, MonitorState state)
    {
        if (!detection.AnyInstalled)
            return new(false, GateReason.NotInstalled, "Claude Code / Claude Desktop tidak terdeteksi di mesin ini.");

        if (state.Status == MonitorStatus.Starting)
            return new(false, GateReason.Checking, "Memeriksa login Claude…");

        if (state.Status == MonitorStatus.NoCredentials)
        {
            return detection.ClaudeDesktop.Installed && !detection.ClaudeCode.Installed
                ? new(false, GateReason.DesktopOnlyNeedsCodeLogin,
                    "Claude Desktop terdeteksi. Login Claude Code diperlukan untuk membaca usage.")
                : new(false, GateReason.NotLoggedIn,
                    "Belum login Claude Code. Jalankan `claude` lalu /login.");
        }

        if (string.Equals(state.SubscriptionType, "free", StringComparison.OrdinalIgnoreCase))
            return new(false, GateReason.NoPaidSubscription, "Tidak ada subscription Claude aktif (plan: free).");

        // Plan label missing (token minted without it): subscription usage windows from the API are proof enough.
        if (string.IsNullOrWhiteSpace(state.SubscriptionType) && !HasSubscriptionUsage(state.LastGood))
        {
            return state.Status is MonitorStatus.Ok
                ? new(false, GateReason.NoPaidSubscription, "Tidak ada subscription Claude aktif (plan: tidak diketahui).")
                : new(false, GateReason.Checking, "Memeriksa subscription Claude…");
        }

        if (state.Status == MonitorStatus.Unauthorized && state.LastGood is null)
            return new(false, GateReason.LoginInvalid, "Sesi Claude Code tidak valid. Login ulang di Claude Code.");

        return new(true, GateReason.Active, $"Claude {Format.Plan(state.SubscriptionType)} aktif.");
    }

    private static bool HasSubscriptionUsage(UsageSnapshot? s) => s is not null && (s.FiveHour is not null || s.SevenDay is not null);
}

public static class Format
{
    public static string Plan(string? subscriptionType) => subscriptionType?.ToLowerInvariant() switch
    {
        null or "" => "Subscription",
        "pro" => "Pro",
        "max" => "Max",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var s => char.ToUpperInvariant(s[0]) + s[1..],
    };

    public static string Percent(double v) => FormattableString.Invariant($"{Math.Round(v):0}%");

    /// <summary>"2j 40m", "3h 4j", "12m", "<1m".</summary>
    public static string Countdown(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "sekarang";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}h {span.Hours}j";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}j {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return "<1m";
    }

    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null) return "belum pernah";
        var d = now - when.Value;
        if (d.TotalSeconds < 60) return "baru saja";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} mnt lalu";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} jam lalu";
        return $"{(int)d.TotalDays} hari lalu";
    }

    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{n / 1_000_000_000d:0.##}B"),
        >= 1_000_000 => FormattableString.Invariant($"{n / 1_000_000d:0.##}M"),
        >= 1_000 => FormattableString.Invariant($"{n / 1_000d:0.#}K"),
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static string StatusLabel(MonitorState s) => s.Status switch
    {
        MonitorStatus.Ok => "Live",
        MonitorStatus.RateLimited => "Rate limited — data cache",
        MonitorStatus.Offline => "Offline — data cache",
        MonitorStatus.ServerError => "Server error — data cache",
        MonitorStatus.TokenExpired => "Token kedaluwarsa — buka Claude Code untuk refresh",
        MonitorStatus.Unauthorized => "Login tidak valid — login ulang di Claude Code",
        MonitorStatus.NoCredentials => "Belum login Claude Code",
        _ => "Memuat…",
    };

    /// <summary>"claude-opus-5-5" -> "Opus 5.5", "claude-haiku-4-5-20251001" -> "Haiku 4.5".</summary>
    public static string Model(string model)
    {
        var s = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"-\d{8}$", "");
        var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return model;
        var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var version = string.Join('.', parts.Skip(1));
        return version.Length == 0 ? name : $"{name} {version}";
    }

    /// <summary>0 = normal, 1 = warning, 2 = critical.</summary>
    public static int Severity(double percent) => percent >= 80 ? 2 : percent >= 50 ? 1 : 0;
}
