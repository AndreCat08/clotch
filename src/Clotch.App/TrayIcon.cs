using System;
using System.Drawing;
using System.Reflection;
using Clotch.Core.Gauges;
using WinForms = System.Windows.Forms;

namespace Clotch.App;

/// <summary>System tray icon and its context menu. All events are raised on the UI thread.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _status;
    private readonly WinForms.ToolStripMenuItem _toggleNotch;
    private readonly WinForms.ToolStripMenuItem _autoStart;
    private readonly WinForms.ToolStripMenuItem _autoHide;
    private readonly WinForms.ToolStripMenuItem _interval;
    private readonly WinForms.ToolStripMenuItem _gaugeStyle;
    private bool _suppress;

    public event Action? ToggleNotchClicked;
    public event Action? RefreshClicked;
    public event Action? OpenUsageClicked;
    public event Action? ResetPositionClicked;
    public event Action<bool>? AutoStartChanged;
    public event Action<bool>? AutoHideChanged;
    public event Action<int>? IntervalChanged;
    public event Action<CompactGaugeStyle>? GaugeStyleChanged;
    public event Action? OpenLogsClicked;
    public event Action? ExitClicked;
    public event Action? IconClicked;

    public TrayIcon()
    {
        _status = new WinForms.ToolStripMenuItem("Clotch — memuat…") { Enabled = false };
        _toggleNotch = new WinForms.ToolStripMenuItem("Sembunyikan notch", null, (_, _) => ToggleNotchClicked?.Invoke());
        _autoStart = new WinForms.ToolStripMenuItem("Jalankan saat Windows start") { CheckOnClick = true };
        _autoStart.CheckedChanged += (_, _) => { if (!_suppress) AutoStartChanged?.Invoke(_autoStart.Checked); };
        _autoHide = new WinForms.ToolStripMenuItem("Sembunyikan saat aplikasi fullscreen") { CheckOnClick = true };
        _autoHide.CheckedChanged += (_, _) => { if (!_suppress) AutoHideChanged?.Invoke(_autoHide.Checked); };

        _interval = new WinForms.ToolStripMenuItem("Interval refresh");
        foreach (var m in new[] { 2, 5, 10, 15, 30 })
        {
            var item = new WinForms.ToolStripMenuItem($"{m} menit") { Tag = m };
            item.Click += (_, _) => IntervalChanged?.Invoke(m);
            _interval.DropDownItems.Add(item);
        }

        _gaugeStyle = new WinForms.ToolStripMenuItem("Gaya indikator");
        foreach (var (style, label) in new[]
                 {
                     (CompactGaugeStyle.Bar, "Progress bar"),
                     (CompactGaugeStyle.Ring, "Lingkaran"),
                     (CompactGaugeStyle.Percent, "Persentase saja"),
                 })
        {
            var item = new WinForms.ToolStripMenuItem(label) { Tag = style };
            item.Click += (_, _) => GaugeStyleChanged?.Invoke(style);
            _gaugeStyle.DropDownItems.Add(item);
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([
            _status,
            new WinForms.ToolStripSeparator(),
            _toggleNotch,
            new WinForms.ToolStripMenuItem("Refresh sekarang", null, (_, _) => RefreshClicked?.Invoke()),
            new WinForms.ToolStripMenuItem("Buka halaman usage claude.ai", null, (_, _) => OpenUsageClicked?.Invoke()),
            new WinForms.ToolStripMenuItem("Kembalikan posisi notch", null, (_, _) => ResetPositionClicked?.Invoke()),
            new WinForms.ToolStripSeparator(),
            _gaugeStyle,
            _interval,
            _autoHide,
            _autoStart,
            new WinForms.ToolStripMenuItem("Buka folder log", null, (_, _) => OpenLogsClicked?.Invoke()),
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Keluar", null, (_, _) => ExitClicked?.Invoke()),
        ]);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Clotch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) IconClicked?.Invoke();
        };
    }

    private static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Clotch.App.clotch.ico");
        return s is null ? SystemIcons.Application : new Icon(s, WinForms.SystemInformation.SmallIconSize);
    }

    public void SetState(string tooltip, string statusLine, bool notchAvailable, bool notchHidden)
    {
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..124] + "…" : tooltip;
        _status.Text = statusLine;
        _toggleNotch.Enabled = notchAvailable;
        _toggleNotch.Text = notchHidden ? "Tampilkan notch" : "Sembunyikan notch";
    }

    public void SetOptions(bool autoStart, bool autoHide, int intervalMinutes, CompactGaugeStyle gaugeStyle)
    {
        _suppress = true;
        try
        {
            _autoStart.Checked = autoStart;
            _autoHide.Checked = autoHide;
            foreach (WinForms.ToolStripMenuItem item in _interval.DropDownItems)
                item.Checked = item.Tag is int m && m == intervalMinutes;
            foreach (WinForms.ToolStripMenuItem item in _gaugeStyle.DropDownItems)
                item.Checked = item.Tag is CompactGaugeStyle g && g == gaugeStyle;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Balloon(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(5000, title, text, icon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
