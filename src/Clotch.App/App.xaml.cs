using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Clotch.Core.Credentials;
using Clotch.Core.Detection;
using Clotch.Core.Diagnostics;
using Clotch.Core.Gating;
using Clotch.Core.Local;
using Clotch.Core.Settings;
using Clotch.Core.Usage;

namespace Clotch.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Clotch.SingleInstance";
    private const string ShowEventName = @"Local\Clotch.ShowNotch";
    private const string UsagePageUrl = "https://claude.ai/settings/usage";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private DetectionService _detector = null!;
    private DetectionResult _detection = null!;
    private HttpClient _http = null!;
    private UsageMonitor _monitor = null!;
    private LocalUsageScanner _scanner = null!;
    private NotchViewModel _vm = null!;
    private NotchWindow _notch = null!;
    private TrayIcon _tray = null!;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _fullscreenTimer;

    private readonly AutoStart _autoStart = AutoStart.ForCurrentProcess();
    private GateReason? _lastReason;
    private bool _fullscreen;
    private int _scanning;
    private int _tickCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Another Clotch is running: ask it to show its notch, then quit.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(OnSecondInstance), null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("unhandled UI exception", ev.Exception);
            ev.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("unobserved task exception", ev.Exception);
            ev.SetObserved();
        };

        Log.Info($"Clotch starting (pid {Environment.ProcessId})");

        _store = new SettingsStore(SettingsStore.DefaultPath);
        _settings = _store.Load();

        var env = DetectionEnvironment.Current();
        _detector = new DetectionService(env);
        _detection = _detector.Detect();
        Log.Info($"detected: code={_detection.ClaudeCode.Installed} ({_detection.ClaudeCode.Version}), " +
                 $"desktop={_detection.ClaudeDesktop.Installed} ({_detection.ClaudeDesktop.Kind} {_detection.ClaudeDesktop.Version})");

        _vm = new NotchViewModel { GaugeStyle = _settings.CompactGaugeStyle };
        _notch = new NotchWindow(_vm, _settings.NotchEdge, _settings.NotchCenterX, _settings.NotchTop, _settings.NotchCenterY);
        _notch.HideRequested += () => SetHidden(true);
        _notch.RefreshRequested += Refresh;
        _notch.OpenUsageRequested += OpenUsagePage;
        _notch.Moved += anchor =>
        {
            _settings.NotchEdge = anchor.Edge;
            _settings.NotchCenterX = anchor.CenterX;
            _settings.NotchTop = anchor.Top;
            _settings.NotchCenterY = anchor.CenterY;
            _store.Save(_settings);
            Log.Info($"notch moved: edge={anchor.Edge}");
        };

        _tray = new TrayIcon();
        _tray.SetOptions(SafeAutoStartEnabled(), _settings.AutoHideFullscreen, _settings.PollMinutes, _settings.CompactGaugeStyle);
        _tray.ToggleNotchClicked += () => SetHidden(!_settings.NotchHidden);
        _tray.IconClicked += OnTrayIconClicked;
        _tray.RefreshClicked += () =>
        {
            if (!Refresh()) _tray.Balloon("Clotch", "Refresh dibatasi 1× per menit agar tidak kena rate limit.");
        };
        _tray.OpenUsageClicked += OpenUsagePage;
        _tray.ResetPositionClicked += () => { SetHidden(false); _notch.ResetPosition(); };
        _tray.AutoStartChanged += on =>
        {
            try { _autoStart.Set(on); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Error("autostart toggle failed", ex);
                _tray.Balloon("Clotch", "Gagal mengubah pengaturan autostart.", System.Windows.Forms.ToolTipIcon.Warning);
            }
        };
        _tray.AutoHideChanged += on =>
        {
            _settings.AutoHideFullscreen = on;
            _store.Save(_settings);
            Apply();
        };
        _tray.IntervalChanged += m =>
        {
            _settings.PollMinutes = m;
            _store.Save(_settings);
            _monitor.UpdateOptions(_monitor.Options with { PollInterval = TimeSpan.FromMinutes(m) });
            _tray.SetOptions(SafeAutoStartEnabled(), _settings.AutoHideFullscreen, m, _settings.CompactGaugeStyle);
        };
        _tray.GaugeStyleChanged += style =>
        {
            _settings.CompactGaugeStyle = style;
            _store.Save(_settings);
            _vm.GaugeStyle = style;
            _tray.SetOptions(SafeAutoStartEnabled(), _settings.AutoHideFullscreen, _settings.PollMinutes, style);
            Log.Info($"gauge style: {style}");
        };
        _tray.OpenLogsClicked += () =>
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Directory}\"") { UseShellExecute = true });
        };
        _tray.ExitClicked += () => Shutdown();

        _http = UsageClient.CreateHttpClient();
        _monitor = new UsageMonitor(
            new CredentialsReader(ClaudePaths.CredentialsFile(env.ConfigDir), ClaudePaths.AccountConfigFile()),
            new UsageClient(_http, TimeProvider.System),
            TimeProvider.System,
            new MonitorOptions { PollInterval = TimeSpan.FromMinutes(_settings.PollMinutes) });
        _monitor.StateChanged += s => Dispatcher.BeginInvoke(() => OnMonitorState(s));

        _scanner = new LocalUsageScanner(ClaudePaths.ProjectsDir(env.ConfigDir), TimeProvider.System);

        WatchCredentials(env.ConfigDir);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fullscreenTimer.Tick += (_, _) => OnFullscreenCheck();
        _fullscreenTimer.Start();

        Apply();
        _monitor.Start();
        ScanLocal();
    }

    private void OnMonitorState(MonitorState s)
    {
        Log.Info($"monitor: {s.Status}, failures={s.ConsecutiveFailures}, next={s.NextPoll?.ToLocalTime():HH:mm:ss}");
        _vm.SetState(s);
        _notch.ClearToast();
        Apply();
    }

    /// <summary>Single place that decides notch visibility and tray text.</summary>
    private void Apply()
    {
        var gate = Gate.Evaluate(_detection, _monitor?.State ?? MonitorState.Initial);
        var show = gate.ShowNotch && !_settings.NotchHidden && !(_settings.AutoHideFullscreen && _fullscreen);

        if (show && !_notch.IsVisible) _notch.Show();
        else if (!show && _notch.IsVisible) _notch.Hide();

        if (gate.ShowNotch)
        {
            _tray.SetState(
                $"Clotch · 5 jam {_vm.SessionPercent} · Mingguan {_vm.WeeklyPercent}",
                $"{_vm.PlanText} · {_vm.StatusText}",
                notchAvailable: true,
                notchHidden: _settings.NotchHidden);
        }
        else
        {
            _tray.SetState($"Clotch · {gate.Message}", gate.Message, notchAvailable: false, notchHidden: _settings.NotchHidden);
        }

        if (_lastReason != gate.Reason)
        {
            Log.Info($"gate: {gate.Reason} (show={gate.ShowNotch})");
            if (!gate.ShowNotch && gate.Reason != GateReason.Checking)
                _tray.Balloon("Clotch", gate.Message, System.Windows.Forms.ToolTipIcon.Warning);
            _lastReason = gate.Reason;
        }
    }

    private void SetHidden(bool hidden)
    {
        if (_settings.NotchHidden == hidden) return;
        _settings.NotchHidden = hidden;
        _store.Save(_settings);
        Apply();
        if (hidden) _tray.Balloon("Clotch", "Notch disembunyikan. Klik icon Clotch di tray untuk menampilkannya lagi.");
    }

    private void OnTrayIconClicked()
    {
        var gate = Gate.Evaluate(_detection, _monitor.State);
        if (gate.ShowNotch) SetHidden(!_settings.NotchHidden);
        else _tray.Balloon("Clotch", gate.Message, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnSecondInstance()
    {
        SetHidden(false);
        _tray.Balloon("Clotch", "Clotch sudah berjalan di system tray.");
    }

    private bool Refresh()
    {
        _detection = _detector.Detect();
        var accepted = _monitor.RequestRefresh();
        if (accepted) ScanLocal();
        return accepted;
    }

    private void OnTick()
    {
        _tickCount++;
        _detection = _detector.Detect();
        _vm.Tick();
        Apply();
        if (_tickCount % 4 == 0) ScanLocal(); // every 2 minutes
    }

    private void OnFullscreenCheck()
    {
        var fs = NativeMethods.IsFullscreenAppActive();
        if (fs != _fullscreen)
        {
            _fullscreen = fs;
            Apply();
        }
        else if (_notch.IsVisible && !_notch.IsMouseOver)
        {
            _notch.BringToTop();
        }
    }

    private async void ScanLocal()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var windowStart = _monitor.State.LastGood?.FiveHour?.ResetsAt?.AddHours(-5);
            var summary = await Task.Run(() => _scanner.Scan(windowStart));
            _vm.SetLocal(summary);
        }
        catch (Exception ex)
        {
            Log.Error("local scan failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private void WatchCredentials(string configDir)
    {
        if (!Directory.Exists(configDir)) return; // the 1-minute local re-check covers a later login
        try
        {
            _watcher = new FileSystemWatcher(configDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler onChange = (_, ev) =>
            {
                if (string.Equals(ev.Name, ".credentials.json", StringComparison.OrdinalIgnoreCase))
                    _monitor.NotifyCredentialsChanged();
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (s, ev) => onChange(s, ev);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn($"credentials watcher unavailable: {ex.GetType().Name}");
        }
    }

    private static void OpenUsagePage()
    {
        try { Process.Start(new ProcessStartInfo(UsagePageUrl) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error("open browser failed", ex); }
    }

    private bool SafeAutoStartEnabled()
    {
        try { return _autoStart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            Log.Info("Clotch exiting");
            _tick?.Stop();
            _fullscreenTimer?.Stop();
            _watcher?.Dispose();
            _monitor?.Dispose();
            _http?.Dispose();
            _tray?.Dispose();
            _showWait?.Unregister(null);
            _showSignal?.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
