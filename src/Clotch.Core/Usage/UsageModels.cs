using System.Globalization;
using System.Text.Json;

namespace Clotch.Core.Usage;

public sealed record UsageWindow(
    string Key,
    string DisplayName,
    double Utilization,
    DateTimeOffset? ResetsAt,
    double? LimitDollars,
    double? UsedDollars);

public sealed record UsageLimit(
    string Kind,
    string? Group,
    double Percent,
    string? Severity,
    DateTimeOffset? ResetsAt,
    bool IsActive);

public sealed record ExtraUsage(
    bool IsEnabled,
    double? MonthlyLimit,
    double? UsedCredits,
    double? Utilization,
    string? Currency);

public sealed record BreakdownRow(string Key, string DisplayName, double Percent);

public sealed record UsageSnapshot(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    IReadOnlyList<UsageWindow> Windows,
    IReadOnlyList<UsageLimit> Limits,
    ExtraUsage? Extra,
    IReadOnlyList<BreakdownRow> Breakdown,
    DateTimeOffset FetchedAt);

/// <summary>
/// Tolerant parser for the undocumented /api/oauth/usage response. Every field is optional,
/// unknown keys are kept as generic windows, and wrong types are skipped instead of throwing.
/// </summary>
public static class UsageParser
{
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["five_hour"] = "Sesi 5 jam",
        ["seven_day"] = "Mingguan (semua model)",
        ["seven_day_sonnet"] = "Mingguan Sonnet",
        ["seven_day_opus"] = "Mingguan Opus",
        ["seven_day_oauth_apps"] = "Mingguan aplikasi OAuth",
        ["seven_day_cowork"] = "Mingguan Cowork",
        ["seven_day_omelette"] = "Mingguan Design",
    };

    public static UsageSnapshot Parse(string json, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new UsageSnapshot(null, null, [], [], null, [], fetchedAt);

        var windows = new List<UsageWindow>();
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            if (Num(p.Value, "utilization") is not { } util) continue;
            windows.Add(new UsageWindow(
                p.Name,
                DisplayNameFor(p.Name),
                util,
                Date(p.Value, "resets_at"),
                Num(p.Value, "limit_dollars"),
                Num(p.Value, "used_dollars")));
        }

        // Known windows first, in a stable order; unknown ones keep document order after them.
        string[] order = ["five_hour", "seven_day", "seven_day_sonnet", "seven_day_opus"];
        windows = windows
            .OrderBy(w => Array.IndexOf(order, w.Key) is var i and >= 0 ? i : order.Length)
            .ToList();

        var limits = new List<UsageLimit>();
        if (root.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in l.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (Str(item, "kind") is not { } kind || Num(item, "percent") is not { } pct) continue;
                limits.Add(new UsageLimit(kind, Str(item, "group"), pct, Str(item, "severity"),
                    Date(item, "resets_at"), Bool(item, "is_active") ?? false));
            }
        }

        ExtraUsage? extra = null;
        if (root.TryGetProperty("extra_usage", out var x) && x.ValueKind == JsonValueKind.Object)
        {
            extra = new ExtraUsage(Bool(x, "is_enabled") ?? false, Num(x, "monthly_limit"),
                Num(x, "used_credits"), Num(x, "utilization"), Str(x, "currency"));
        }

        var breakdown = new List<BreakdownRow>();
        if (root.TryGetProperty("seven_day_breakdown", out var b) && b.ValueKind == JsonValueKind.Object &&
            b.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (Str(r, "key") is not { } key || Num(r, "percent") is not { } pct) continue;
                breakdown.Add(new BreakdownRow(key, Str(r, "display_name") ?? Humanize(key), pct));
            }
        }

        return new UsageSnapshot(
            windows.FirstOrDefault(w => w.Key == "five_hour"),
            windows.FirstOrDefault(w => w.Key == "seven_day"),
            windows, limits, extra, breakdown, fetchedAt);
    }

    public static string DisplayNameFor(string key) =>
        KnownNames.TryGetValue(key, out var n) ? n : Humanize(key);

    private static string Humanize(string key)
    {
        var s = key.Replace('_', ' ').Trim();
        return s.Length == 0 ? key : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static double? Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

    private static DateTimeOffset? Date(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return d;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var secs))
            return DateTimeOffset.FromUnixTimeSeconds(secs);
        return null;
    }
}
