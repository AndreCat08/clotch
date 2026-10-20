using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using static Clotch.App.NativeMethods;

namespace Clotch.App;

/// <summary>
/// The notch: borderless, topmost, never activated, hidden from taskbar and Alt-Tab.
/// Positioning is done in physical pixels so it stays correct across monitors with different DPI.
/// The user-chosen anchor (centre X, top) is kept separately from the clamped on-screen position,
/// so expanding near a screen edge never permanently moves the notch.
/// </summary>
public partial class NotchWindow : Window
{
    private IntPtr _hwnd;
    private int _anchorCenterX;
    private int _anchorTop;
    private Point _downPoint;
    private bool _pressedOnHeader;
    private bool _dragging;

    public event Action? HideRequested;
    public event Func<bool>? RefreshRequested;
    public event Action? OpenUsageRequested;
    /// <summary>Raised after the user finished dragging: (centerX, top) in physical pixels.</summary>
    public event Action<int, int>? Moved;

    public NotchViewModel ViewModel { get; }

    public NotchWindow(NotchViewModel vm, int? centerX, int? top)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
            ResetAnchor(centerX, top);
            Place();
        };
        SizeChanged += (_, _) => Place();
        DpiChanged += (_, _) => Place();

        Header.MouseLeftButtonDown += OnHeaderDown;
        Root.MouseLeftButtonDown += OnRootDown;
        Root.MouseMove += OnRootMove;
        Root.MouseLeftButtonUp += OnRootUp;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotchViewModel.IsExpanded) && vm.IsExpanded) FadeInDetails();
        };
    }

    /// <summary>Uses the saved anchor if it lies on a connected monitor, else top-centre of the primary monitor.</summary>
    private void ResetAnchor(int? centerX, int? top)
    {
        if (centerX is { } cx && top is { } t &&
            MonitorFromPoint(new POINT(cx, t + 1), MONITOR_DEFAULTTONULL) != IntPtr.Zero)
        {
            _anchorCenterX = cx;
            _anchorTop = t;
            return;
        }
        var work = WorkArea(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY));
        _anchorCenterX = work is { } w ? (w.Left + w.Right) / 2 : 800;
        _anchorTop = work?.Top ?? 0;
    }

    public void ResetPosition()
    {
        ResetAnchor(null, null);
        _ = Place();
        Moved?.Invoke(_anchorCenterX, _anchorTop);
    }

    /// <summary>Puts the window at the anchor, clamped to the work area of the anchor's monitor.</summary>
    /// <returns>The clamped (centerX, top) actually used, or null when the window has no handle yet.</returns>
    private (int CenterX, int Top)? Place()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r)) return null;
        var work = WorkArea(MonitorFromPoint(new POINT(_anchorCenterX, _anchorTop + 1), MONITOR_DEFAULTTONEAREST));
        if (work is not { } wa) return null;

        // The transparent margin around Root (shadow room) must not count when clamping to the top edge.
        var scale = r.Width / Math.Max(1.0, ActualWidth);
        var marginBottom = (int)Math.Round(Root.Margin.Bottom * scale);

        var left = Math.Clamp(_anchorCenterX - r.Width / 2, wa.Left, Math.Max(wa.Left, wa.Right - r.Width));
        var top = Math.Clamp(_anchorTop, wa.Top, Math.Max(wa.Top, wa.Bottom - r.Height + marginBottom));
        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

        var docked = top <= wa.Top;
        Root.CornerRadius = docked ? new CornerRadius(0, 0, 16, 16) : new CornerRadius(16);
        Root.BorderThickness = docked ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        return (left + r.Width / 2, top);
    }

    /// <summary>Re-asserts topmost (other topmost windows such as the taskbar can cover it).</summary>
    public void BringToTop() => Place();


    // ── click vs drag ─────────────────────────────────────────

    private void OnHeaderDown(object sender, MouseButtonEventArgs e) => _pressedOnHeader = true;

    private void OnRootDown(object sender, MouseButtonEventArgs e)
    {
        _downPoint = e.GetPosition(this);
        _dragging = false;
        Root.CaptureMouse();
    }

    private void OnRootMove(object sender, MouseEventArgs e)
    {
        if (!Root.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _downPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _downPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragging = true;
        Root.ReleaseMouseCapture();
        try
        {
            DragMove(); // blocks until the button is released
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            _pressedOnHeader = false;
        }

        if (GetWindowRect(_hwnd, out var r))
        {
            _anchorCenterX = r.Left + r.Width / 2;
            _anchorTop = r.Top;
            // Persist the clamped position, so a notch dropped half off-screen is restored fully visible.
            if (Place() is { } placed) (_anchorCenterX, _anchorTop) = placed;
            Moved?.Invoke(_anchorCenterX, _anchorTop);
        }
    }

    private void OnRootUp(object sender, MouseButtonEventArgs e)
    {
        var wasHeader = _pressedOnHeader;
        _pressedOnHeader = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        if (_dragging) { _dragging = false; return; }
        if (wasHeader) ViewModel.IsExpanded = !ViewModel.IsExpanded;
    }

    private void FadeInDetails()
    {
        Details.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
    }

    // ── buttons ───────────────────────────────────────────────

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var accepted = RefreshRequested?.Invoke() ?? false;
        Toast.Text = accepted ? "Memperbarui…" : "Tunggu sebentar — refresh dibatasi 1× per menit.";
    }

    private void OpenUsage_Click(object sender, RoutedEventArgs e) => OpenUsageRequested?.Invoke();

    public void ClearToast() => Toast.Text = "";
}
