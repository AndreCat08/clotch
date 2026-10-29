using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Clotch.Core.Gating;
using Clotch.Core.Gauges;
using Clotch.Core.Local;
using Clotch.Core.Usage;

namespace Clotch.App;

public sealed record LimitRow(string Name, double Value, string Percent, string Detail, Brush Brush);
public sealed record ShareRow(string Name, double Value, string Percent);
public sealed record TokenRow(string Model, string Total, string Detail);

/// <summary>Presentation state for the notch. Rebuilt wholesale on every update (cheap, a handful of rows).</summary>
public sealed class NotchViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");
    private static readonly Brush Green = Frozen(0x4A, 0xDE, 0x80);
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush Red = Frozen(0xF8, 0x71, 0x71);
    private static readonly Brush Grey = Frozen(0x8A, 0x87, 0x80);
    private static readonly Brush Orange = Frozen(0xD9, 0x77, 0x57);

    private MonitorState _state = MonitorState.Initial;
    private LocalUsageSummary? _local;
    private bool _isExpanded;
    private CompactGaugeStyle _gaugeStyle = CompactGaugeStyle.Bar;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── compact ───────────────────────────────────────────────
    public double SessionValue { get; private set; }
    public string SessionPercent { get; private set; } = "–";
    public Brush SessionBrush { get; private set; } = Grey;
    public double WeeklyValue { get; private set; }
    public string WeeklyPercent { get; private set; } = "–";
    public Brush WeeklyBrush { get; private set; } = Grey;
    public string SessionReset { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = Grey;

    /// <summary>How the compact gauges are drawn; switching it re-renders the compact row immediately.</summary>
    public CompactGaugeStyle GaugeStyle
    {
        get => _gaugeStyle;
        set
        {
            if (_gaugeStyle == value) return;
            _gaugeStyle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GaugeStyle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRing)));
        }
    }
    public bool ShowBar => _gaugeStyle == CompactGaugeStyle.Bar;
    public bool ShowRing => _gaugeStyle == CompactGaugeStyle.Ring;

    // ── expanded ──────────────────────────────────────────────
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public string PlanText { get; private set; } = "Claude";
    public string StatusText { get; private set; } = "Memuat…";
    public string UpdatedText { get; private set; } = "";
    public IReadOnlyList<LimitRow> Limits { get; private set; } = [];
    public IReadOnlyList<ShareRow> Breakdown { get; private set; } = [];
    public bool HasBreakdown => Breakdown.Count > 0;
    public string ExtraUsageText { get; private set; } = "";
    public bool HasExtraUsage => ExtraUsageText.Length > 0;
    public IReadOnlyList<TokenRow> LocalToday { get; private set; } = [];
    public string LocalSummary { get; private set; } = "";
    public bool HasLocal => _local is not null;
    public string NextPollText { get; private set; } = "";

    public void SetState(MonitorState state) { _state = state; Rebuild(); }
    public void SetLocal(LocalUsageSummary local) { _local = local; Rebuild(); }

    /// <summary>Re-renders relative times ("2 mnt lalu", countdowns).</summary>
    public void Tick() => Rebuild();

    private void Rebuild()
    {
        var now = DateTimeOffset.Now;
        var s = _state;
        var snap = s.LastGood;

        (SessionValue, SessionPercent, SessionBrush) = Gauge(snap?.FiveHour);
        (WeeklyValue, WeeklyPercent, WeeklyBrush) = Gauge(snap?.SevenDay);
        SessionReset = snap?.FiveHour?.ResetsAt is { } r ? "↻ " + Format.Countdown(r - now) : "";
        StatusBrush = s.Status == MonitorStatus.Ok ? Green : s.IsStale ? Amber : Grey;

        PlanText = $"Claude {Format.Plan(s.SubscriptionType)}";
        StatusText = Format.StatusLabel(s);
        UpdatedText = s.LastSuccess is null ? "" : "diperbarui " + Format.Ago(s.LastSuccess, now);
        NextPollText = s.NextPoll is { } np ? "cek berikutnya " + np.ToLocalTime().ToString("HH':'mm", Id) : "";

        Limits = (snap?.Windows ?? []).Select(w =>
        {
            var (v, pct, brush) = Gauge(w);
            return new LimitRow(w.DisplayName, v, pct, Detail(w, now), brush);
        }).ToList();

        var total = snap?.Breakdown.Sum(b => b.Percent) ?? 0;
        Breakdown = total > 0
            ? snap!.Breakdown.Select(b => new ShareRow(b.DisplayName, b.Percent, Format.Percent(b.Percent))).ToList()
            : [];

        ExtraUsageText = snap?.Extra switch
        {
            null => "",
            { IsEnabled: false } => "Nonaktif",
            // monthly_limit/used_credits units are undocumented (observed both dollars and minor units),
            // so only the server-computed utilization is shown.
            { Utilization: { } u } => $"Aktif · {Format.Percent(u)} terpakai",
            _ => "Aktif",
        };

        if (_local is { } l)
        {
            LocalToday = l.Today.Select(m => new TokenRow(
                Format.Model(m.Model),
                Format.Tokens(m.Total),
                $"in {Format.Tokens(m.Input)} · out {Format.Tokens(m.Output)} · cache {Format.Tokens(m.CacheCreate + m.CacheRead)} · {m.Messages} pesan"))
                .ToList();
            LocalSummary = $"Hari ini {Format.Tokens(l.TodayTotal)} · window 5 jam {Format.Tokens(l.WindowTotal)}";
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static (double, string, Brush) Gauge(UsageWindow? w) => w is null
        ? (0, "–", Grey)
        : (Math.Clamp(w.Utilization, 0, 100), Format.Percent(w.Utilization), Format.Severity(w.Utilization) switch
        {
            2 => Red,
            1 => Amber,
            _ => w.Key == "seven_day" ? Orange : Green,
        });

    private static string Detail(UsageWindow w, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (w.UsedDollars is { } used && w.LimitDollars is { } lim)
            parts.Add($"{Money(used, "USD")} / {Money(lim, "USD")}");
        if (w.ResetsAt is { } r)
        {
            var local = r.ToLocalTime();
            var when = (r - now).TotalHours < 24 ? local.ToString("HH':'mm", Id) : local.ToString("ddd d MMM HH':'mm", Id);
            parts.Add($"reset {when} ({Format.Countdown(r - now)})");
        }
        return string.Join(" · ", parts);
    }

    private static string Money(double amount, string? currency) =>
        (currency ?? "USD").ToUpperInvariant() == "USD"
            ? "$" + amount.ToString("0.00", CultureInfo.InvariantCulture)
            : amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
