using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clotch.Core.Layout;
using static Clotch.App.NativeMethods;
using ExpandDirection = Clotch.Core.Layout.ExpandDirection;

namespace Clotch.App;

/// <summary>
/// The notch: borderless, topmost, never activated, hidden from taskbar and Alt-Tab.
/// Positioning is done in physical pixels so it stays correct across monitors with different DPI.
/// The user-chosen anchor (edge + position of the compact notch) is kept separately from the on-screen
/// rectangle, so expanding near a screen edge never permanently moves the notch. The placement math
/// lives in <see cref="NotchPlacement"/>; this class only measures, applies the layout and moves the HWND.
/// </summary>
public partial class NotchWindow : Window
{
    /// <summary>Distance (DIP) from a screen edge at which a drop docks to that edge.</summary>
    private const double SnapDip = 24;

    private IntPtr _hwnd;
    private NotchAnchor _anchor;
    private Point _downPoint;
    private bool _pressedOnHeader;
    private bool _dragging;
    private bool _placing;

    // Applied visual state; _appliedEdge is null until the first Place() so everything is set once.
    private NotchEdge? _appliedEdge;
    private ExpandDirection _appliedDir;
    private bool _appliedExpanded;

    /// <summary>Last measured compact size per orientation (key: vertical).</summary>
    private readonly Dictionary<bool, PxSize> _compactSize = [];
    /// <summary>Current expanded layout; null while compact.</summary>
    private NotchLayout? _layout;

    public event Action? HideRequested;
    public event Func<bool>? RefreshRequested;
    public event Action? OpenUsageRequested;
    /// <summary>Raised after the user finished dragging, with the (clamped) anchor to persist.</summary>
    public event Action<NotchAnchor>? Moved;

    public NotchViewModel ViewModel { get; }

    public NotchWindow(NotchViewModel vm, NotchEdge? edge, int? centerX, int? top, int? centerY)
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
            ResetAnchor(edge, centerX, top, centerY);
            Place();
        };
        SizeChanged += (_, _) => Place();
        DpiChanged += (_, _) => Place();

        Header.MouseLeftButtonDown += OnHeaderDown;
        HeaderV.MouseLeftButtonDown += OnHeaderDown;
        Root.MouseLeftButtonDown += OnRootDown;
        Root.MouseMove += OnRootMove;
        Root.MouseLeftButtonUp += OnRootUp;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotchViewModel.IsExpanded) && vm.IsExpanded) FadeInDetails();
        };
    }

    /// <summary>Uses the saved anchor if it lies on a connected monitor, else top-centre of the primary monitor.</summary>
    private void ResetAnchor(NotchEdge? edge, int? centerX, int? top, int? centerY)
    {
        if (centerX is { } cx && top is { } t)
        {
            var vertical = edge is { } e && NotchPlacement.IsVertical(e);
            var probe = vertical && centerY is { } cy ? new POINT(cx, cy) : new POINT(cx, t + 1);
            var monitor = MonitorFromPoint(probe, MONITOR_DEFAULTTONULL);
            if (monitor != IntPtr.Zero && WorkArea(monitor) is { } wa)
            {
                var resolved = edge ?? NotchPlacement.LegacyEdge(t, ToPx(wa));
                if (NotchPlacement.IsVertical(resolved) && centerY is null) resolved = NotchEdge.Float;
                _anchor = new NotchAnchor(resolved, cx, t, centerY ?? t);
                return;
            }
        }
        var work = WorkArea(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY));
        var x = work is { } w ? (w.Left + w.Right) / 2 : 800;
        var y = work?.Top ?? 0;
        _anchor = new NotchAnchor(NotchEdge.Top, x, y, y);
    }

    public void ResetPosition()
    {
        if (_anchor.IsVertical) ViewModel.IsExpanded = false; // the bar's compact size may not be measured yet
        ResetAnchor(null, null, null, null);
        Place();
        Moved?.Invoke(_anchor);
    }

    private static PxRect ToPx(RECT r) => PxRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);

    private PxRect? WorkAreaForAnchor()
    {
        var probe = _anchor.IsVertical ? new POINT(_anchor.CenterX, _anchor.CenterY) : new POINT(_anchor.CenterX, _anchor.Top + 1);
        return WorkArea(MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST)) is { } wa ? ToPx(wa) : null;
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleY;

    /// <summary>
    /// Lays the notch out for its edge and expand state, then moves it into place.
    /// Re-entrant calls (SizeChanged raised by our own UpdateLayout) are ignored.
    /// </summary>
    /// <returns>The compact rectangle actually used, or null when the window has no handle yet.</returns>
    private PxRect? Place()
    {
        if (_hwnd == IntPtr.Zero || _placing) return null;
        _placing = true;
        try
        {
            if (WorkAreaForAnchor() is not { } work) return null;
            var vertical = _anchor.IsVertical;
            var expanded = ViewModel.IsExpanded;

            // Never taller than the work area; the details scroll instead.
            var maxHeight = work.Height / DpiScale;
            if (double.IsInfinity(MaxHeight) || Math.Abs(MaxHeight - maxHeight) > 0.5) MaxHeight = maxHeight;

            ApplyVisuals(_anchor.Edge, _layout?.Direction ?? ExpandDirection.Down, expanded);
            UpdateLayout();
            if (!GetWindowRect(_hwnd, out var r)) return null;
            var size = new PxSize(r.Width, r.Height);

            if (!expanded)
            {
                _compactSize[vertical] = size;
                _layout = null;
                var c = NotchPlacement.Compact(_anchor, size, work);
                MoveTo(c.Left, c.Top);
                return c;
            }

            var compactSize = _compactSize.TryGetValue(vertical, out var cs) ? cs : size;
            var compact = NotchPlacement.Compact(_anchor, compactSize, work);
            var layout = NotchPlacement.Expanded(_anchor.Edge, compact, size, work);
            if (layout.Direction != _appliedDir)
            {
                ApplyVisuals(_anchor.Edge, layout.Direction, expanded); // reorders children; size is unchanged
                UpdateLayout();
            }
            _layout = layout;
            MoveTo(layout.Window.Left, layout.Window.Top);
            return compact;
        }
        finally
        {
            _placing = false;
        }
    }

    private void MoveTo(int left, int top) =>
        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Switches bar/strip, where the details sit, and the docked look. Only touches what changed.</summary>
    private void ApplyVisuals(NotchEdge edge, ExpandDirection dir, bool expanded)
    {
        if (_appliedEdge == edge && _appliedDir == dir && _appliedExpanded == expanded) return;
        _appliedEdge = edge;
        _appliedDir = dir;
        _appliedExpanded = expanded;

        var vertical = NotchPlacement.IsVertical(edge);
        var up = dir == ExpandDirection.Up;
        Header.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        HeaderV.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        Root.MinWidth = vertical ? 0 : 300;

        // Shadow room is left on every side except the one touching the screen edge.
        switch (edge)
        {
            case NotchEdge.Top:
                Root.Margin = new Thickness(10, 0, 10, 12);
                Root.CornerRadius = new CornerRadius(0, 0, 16, 16);
                Root.BorderThickness = new Thickness(1, 0, 1, 1);
                break;
            case NotchEdge.Float:
                Root.Margin = new Thickness(10, 0, 10, 12);
                Root.CornerRadius = new CornerRadius(16);
                Root.BorderThickness = new Thickness(1);
                break;
            case NotchEdge.Left:
                Root.Margin = new Thickness(0, 4, 12, 12);
                Root.CornerRadius = new CornerRadius(0, 16, 16, 0);
                Root.BorderThickness = new Thickness(0, 1, 1, 1);
                break;
            case NotchEdge.Right:
                Root.Margin = new Thickness(12, 4, 0, 12);
                Root.CornerRadius = new CornerRadius(16, 0, 0, 16);
                Root.BorderThickness = new Thickness(1, 1, 0, 1);
                break;
        }

        if (vertical)
        {
            var left = edge == NotchEdge.Left;
            Root.Padding = !expanded ? new Thickness(6, 10, 6, 8) : left ? new Thickness(6, 10, 10, 8) : new Thickness(10, 10, 6, 8);
            DockPanel.SetDock(HeaderV, left ? Dock.Left : Dock.Right);
            HeaderV.VerticalAlignment = up ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            Details.Margin = left ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0);
            SeparatorTop.Visibility = Visibility.Collapsed;
            SeparatorBottom.Visibility = Visibility.Collapsed;
        }
        else
        {
            Root.Padding = new Thickness(14, 7, 10, 8);
            DockPanel.SetDock(Header, up ? Dock.Bottom : Dock.Top);
            Details.Margin = up ? new Thickness(0, 0, 0, 4) : new Thickness(0, 4, 0, 0);
            SeparatorTop.Visibility = up ? Visibility.Collapsed : Visibility.Visible;
            SeparatorBottom.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
        }
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
        OnDropped();
    }

    /// <summary>Docks to the edge under the cursor and persists the clamped anchor.</summary>
    private void OnDropped()
    {
        if (!GetWindowRect(_hwnd, out var r) || !GetCursorPos(out var pt)) return;
        if (WorkArea(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST)) is not { } wa) return;

        var oldEdge = _anchor.Edge;
        var dropped = ToPx(r);
        var compact = _layout is { } l && _compactSize.TryGetValue(_anchor.IsVertical, out var cs)
            ? NotchPlacement.CompactWithin(oldEdge, l.Direction, dropped, cs)
            : dropped;
        var cursor = new PxPoint(pt.X, pt.Y);
        var snapPx = (int)Math.Round(SnapDip * DpiScale);
        var newEdge = NotchPlacement.ResolveEdge(cursor, compact, ToPx(wa), snapPx);
        PxSize? newCompact = _compactSize.TryGetValue(NotchPlacement.IsVertical(newEdge), out var ns) ? ns : null;
        _anchor = NotchPlacement.AnchorAfterDrop(oldEdge, newEdge, compact, cursor, newCompact);
        Clotch.Core.Diagnostics.Log.Info($"notch drop: cursor=({pt.X},{pt.Y}) window=({r.Left},{r.Top},{r.Width}x{r.Height}) {oldEdge}->{newEdge}");

        // Bar <-> strip: close the details so the new shape is measured compact first.
        if (NotchPlacement.IsVertical(oldEdge) != NotchPlacement.IsVertical(newEdge)) ViewModel.IsExpanded = false;

        // Persist the clamped position, so a notch dropped half off-screen is restored fully visible.
        if (Place() is { } placed)
            _anchor = _anchor with { CenterX = placed.CenterX, Top = placed.Top, CenterY = placed.CenterY };
        Moved?.Invoke(_anchor);
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
