using Clotch.Core.Layout;
using Clotch.Core.Settings;

namespace Clotch.Tests;

public class NotchPlacementTests
{
    // 1920x1080 screen with a 60 px taskbar at the bottom.
    private static readonly PxRect Work = new(0, 0, 1920, 1020);
    private static readonly PxSize Bar = new(460, 56);
    private static readonly PxSize BarExpanded = new(470, 680);
    private static readonly PxSize Strip = new(80, 300);
    private static readonly PxSize StripExpanded = new(500, 680);

    [Fact]
    public void Top_dock_expands_down_and_keeps_bar_in_place()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Top, 960, 0, 0), Bar, Work);
        Assert.Equal(new PxRect(730, 0, 460, 56), compact);

        var l = NotchPlacement.Expanded(NotchEdge.Top, compact, BarExpanded, Work);
        Assert.Equal(ExpandDirection.Down, l.Direction);
        Assert.Equal(new PxRect(725, 0, 470, 680), l.Window);
    }

    [Fact]
    public void Floating_near_bottom_expands_up_and_bar_stays()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Float, 960, 940, 0), Bar, Work);
        Assert.Equal(940, compact.Top);

        var l = NotchPlacement.Expanded(NotchEdge.Float, compact, BarExpanded, Work);
        Assert.Equal(ExpandDirection.Up, l.Direction);
        Assert.Equal(compact.Bottom, l.Window.Bottom);
        Assert.True(l.Window.Top >= Work.Top);

        // Collapsing returns to exactly the same compact rectangle (no drift).
        var back = NotchPlacement.CompactWithin(NotchEdge.Float, l.Direction, l.Window, Bar);
        Assert.Equal(compact, back);
    }

    [Fact]
    public void Floating_in_upper_half_expands_down()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Float, 960, 200, 0), Bar, Work);
        var l = NotchPlacement.Expanded(NotchEdge.Float, compact, BarExpanded, Work);
        Assert.Equal(ExpandDirection.Down, l.Direction);
        Assert.Equal(200, l.Window.Top);
        Assert.Equal(compact, NotchPlacement.CompactWithin(NotchEdge.Float, l.Direction, l.Window, Bar));
    }

    [Fact]
    public void Fits_neither_way_picks_larger_side_and_stays_inside_work_area()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Float, 960, 400, 0), Bar, Work);
        var l = NotchPlacement.Expanded(NotchEdge.Float, compact, new PxSize(470, 900), Work);
        Assert.Equal(ExpandDirection.Down, l.Direction); // 620 px below vs 456 above
        Assert.Equal(Work.Bottom, l.Window.Bottom);
        Assert.True(l.Window.Top >= Work.Top);
    }

    [Fact]
    public void Taller_than_work_area_is_clamped_to_it()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Float, 960, 500, 0), Bar, Work);
        var l = NotchPlacement.Expanded(NotchEdge.Float, compact, new PxSize(470, 1500), Work);
        Assert.Equal(Work.Height, l.Window.Height);
        Assert.Equal(Work.Top, l.Window.Top);
    }

    [Fact]
    public void Expanded_bar_near_right_edge_is_clamped_horizontally()
    {
        var compact = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Top, 1900, 0, 0), Bar, Work);
        Assert.Equal(Work.Right, compact.Right);
        var l = NotchPlacement.Expanded(NotchEdge.Top, compact, BarExpanded, Work);
        Assert.Equal(Work.Right, l.Window.Right);
    }

    [Fact]
    public void Compact_offscreen_float_is_clamped()
    {
        var c = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Float, -500, 5000, 0), Bar, Work);
        Assert.Equal(new PxRect(0, Work.Bottom - Bar.Height, Bar.Width, Bar.Height), c);
    }

    [Fact]
    public void Side_strips_are_flush_with_their_edge_and_expand_inward()
    {
        var left = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Left, 0, 0, 400), Strip, Work);
        Assert.Equal(new PxRect(0, 250, 80, 300), left);
        var ll = NotchPlacement.Expanded(NotchEdge.Left, left, StripExpanded, Work);
        Assert.Equal(Work.Left, ll.Window.Left);
        Assert.Equal(ExpandDirection.Down, ll.Direction);
        Assert.Equal(left.Top, ll.Window.Top);

        var right = NotchPlacement.Compact(new NotchAnchor(NotchEdge.Right, 0, 0, 900), Strip, Work);
        Assert.Equal(Work.Right, right.Right);
        Assert.Equal(Work.Bottom - Strip.Height, right.Top); // clamped
        var rl = NotchPlacement.Expanded(NotchEdge.Right, right, StripExpanded, Work);
        Assert.Equal(Work.Right, rl.Window.Right);
        Assert.True(rl.Window.Left < right.Left); // details open toward the screen centre
        Assert.Equal(ExpandDirection.Up, rl.Direction);
        Assert.Equal(right.Bottom, rl.Window.Bottom);
        Assert.Equal(right, NotchPlacement.CompactWithin(NotchEdge.Right, rl.Direction, rl.Window, Strip));
    }

    [Theory]
    [InlineData(10, 500, 200, NotchEdge.Left)]
    [InlineData(1915, 500, 200, NotchEdge.Right)]
    [InlineData(5, 5, 0, NotchEdge.Top)]          // top-left corner: top wins
    [InlineData(1915, 10, 0, NotchEdge.Top)]      // top-right corner: top wins
    [InlineData(900, 500, 470, NotchEdge.Float)]
    [InlineData(900, 30, 10, NotchEdge.Top)]      // window within snap distance of the top
    public void Resolves_edge_from_drop(int cx, int cy, int windowTop, NotchEdge expected)
    {
        var dropped = new PxRect(cx - 200, windowTop, 400, 56);
        Assert.Equal(expected, NotchPlacement.ResolveEdge(new PxPoint(cx, cy), dropped, Work, 30));
    }

    [Fact]
    public void Orientation_change_centres_new_shape_on_cursor()
    {
        var bar = new PxRect(0, 400, 460, 56);
        var a = NotchPlacement.AnchorAfterDrop(NotchEdge.Float, NotchEdge.Left, bar, new PxPoint(8, 430));
        Assert.Equal(NotchEdge.Left, a.Edge);
        Assert.Equal(430, a.CenterY);

        var strip = new PxRect(0, 300, 80, 300);
        var b = NotchPlacement.AnchorAfterDrop(NotchEdge.Left, NotchEdge.Float, strip, new PxPoint(700, 320));
        Assert.Equal(700, b.CenterX);
        Assert.Equal(300, b.Top);

        // With the bar size known, the new bar is centred vertically on the cursor.
        var b2 = NotchPlacement.AnchorAfterDrop(NotchEdge.Left, NotchEdge.Float, strip, new PxPoint(700, 320), Bar);
        Assert.Equal(320 - Bar.Height / 2, b2.Top);

        var c = NotchPlacement.AnchorAfterDrop(NotchEdge.Float, NotchEdge.Float, bar, new PxPoint(100, 420));
        Assert.Equal(bar.CenterX, c.CenterX);
        Assert.Equal(400, c.Top);
    }

    [Fact]
    public void Legacy_settings_map_to_top_or_float()
    {
        Assert.Equal(NotchEdge.Top, NotchPlacement.LegacyEdge(0, Work));
        Assert.Equal(NotchEdge.Float, NotchPlacement.LegacyEdge(804, Work));
    }

    [Fact]
    public void Settings_round_trip_edge_and_load_old_files()
    {
        using var dir = new TempDir();
        var oldFile = dir.Write("old.json", """{ "NotchCenterX": 1266, "NotchTop": 0, "NotchHidden": false }""");
        var old = new SettingsStore(oldFile).Load();
        Assert.Null(old.NotchEdge);
        Assert.Null(old.NotchCenterY);
        Assert.Equal(1266, old.NotchCenterX);

        var store = new SettingsStore(dir.Combine("new.json"));
        store.Save(new AppSettings { NotchEdge = NotchEdge.Right, NotchCenterY = 420 });
        Assert.Contains("\"Right\"", File.ReadAllText(store.Path));
        var loaded = store.Load();
        Assert.Equal(NotchEdge.Right, loaded.NotchEdge);
        Assert.Equal(420, loaded.NotchCenterY);
    }
}
