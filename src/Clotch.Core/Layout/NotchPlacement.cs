namespace Clotch.Core.Layout;

/// <summary>Where the notch is attached. Top/Float use the horizontal bar, Left/Right the vertical strip.</summary>
public enum NotchEdge { Top, Float, Left, Right }

/// <summary>Which way the details grow vertically from the compact header.</summary>
public enum ExpandDirection { Down, Up }

public readonly record struct PxPoint(int X, int Y);

public readonly record struct PxSize(int Width, int Height);

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PxRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
    public static PxRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
}

/// <summary>
/// The user's chosen notch position, independent of its current size.
/// Horizontal edges use (CenterX, Top) of the compact bar; side edges use CenterY of the compact strip.
/// </summary>
public readonly record struct NotchAnchor(NotchEdge Edge, int CenterX, int Top, int CenterY)
{
    public bool IsVertical => Edge is NotchEdge.Left or NotchEdge.Right;
}

/// <summary>Result of placing the expanded notch: the window rectangle and where the details open.</summary>
public readonly record struct NotchLayout(PxRect Window, ExpandDirection Direction);

/// <summary>
/// Pure placement math for the notch window, all in physical pixels.
/// The window never leaves the work area; the compact part stays where the user put it whenever the
/// details fit above or below it.
/// </summary>
public static class NotchPlacement
{
    public static bool IsVertical(NotchEdge edge) => edge is NotchEdge.Left or NotchEdge.Right;

    /// <summary>Settings written before docking edges existed only hold (centerX, top): docked iff at the top of the work area.</summary>
    public static NotchEdge LegacyEdge(int top, PxRect work) => top <= work.Top ? NotchEdge.Top : NotchEdge.Float;

    /// <summary>Decides the edge for a drop. Top wins at corners, so the existing top dock keeps working.</summary>
    /// <param name="cursor">Cursor position when the mouse was released.</param>
    /// <param name="dropped">Window rectangle where the drag left it (may be partly off-screen).</param>
    /// <param name="work">Work area of the monitor under the cursor.</param>
    /// <param name="snapPx">Distance from an edge that still counts as touching it.</param>
    public static NotchEdge ResolveEdge(PxPoint cursor, PxRect dropped, PxRect work, int snapPx)
    {
        if (dropped.Top <= work.Top + snapPx || cursor.Y <= work.Top + snapPx) return NotchEdge.Top;
        if (cursor.X <= work.Left + snapPx) return NotchEdge.Left;
        if (cursor.X >= work.Right - 1 - snapPx) return NotchEdge.Right;
        return NotchEdge.Float;
    }

    /// <summary>
    /// Anchor for a drop. When the orientation changes (bar ↔ strip) the new shape is centred on the cursor,
    /// so it appears under the pointer instead of where the old shape's centre was.
    /// </summary>
    /// <param name="compact">Rectangle of the compact part at the drop position, in the old orientation.</param>
    /// <param name="newCompactSize">Compact size of the new shape when already measured; centres a new bar on the cursor.</param>
    public static NotchAnchor AnchorAfterDrop(NotchEdge oldEdge, NotchEdge newEdge, PxRect compact, PxPoint cursor, PxSize? newCompactSize = null)
    {
        var sameOrientation = IsVertical(oldEdge) == IsVertical(newEdge);
        if (IsVertical(newEdge))
            return sameOrientation
                ? new NotchAnchor(newEdge, compact.CenterX, compact.Top, compact.CenterY)
                : new NotchAnchor(newEdge, cursor.X, compact.Top, cursor.Y);
        if (sameOrientation) return new NotchAnchor(newEdge, compact.CenterX, compact.Top, compact.CenterY);
        var top = newCompactSize is { } s ? cursor.Y - s.Height / 2 : compact.Top;
        return new NotchAnchor(newEdge, cursor.X, top, cursor.Y);
    }

    /// <summary>Rectangle of the compact notch (details closed), clamped to the work area.</summary>
    public static PxRect Compact(NotchAnchor anchor, PxSize size, PxRect work)
    {
        int ClampX(int x) => Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        int ClampY(int y) => Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        return anchor.Edge switch
        {
            NotchEdge.Top => new PxRect(ClampX(anchor.CenterX - size.Width / 2), work.Top, size.Width, size.Height),
            NotchEdge.Float => new PxRect(ClampX(anchor.CenterX - size.Width / 2), ClampY(anchor.Top), size.Width, size.Height),
            NotchEdge.Left => new PxRect(work.Left, ClampY(anchor.CenterY - size.Height / 2), size.Width, size.Height),
            NotchEdge.Right => new PxRect(Math.Max(work.Left, work.Right - size.Width), ClampY(anchor.CenterY - size.Height / 2), size.Width, size.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
        };
    }

    /// <summary>
    /// Places the expanded notch. Details open below the compact part when they fit, otherwise above it;
    /// when neither fits, toward the side with more room and the window is clamped into the work area.
    /// Bar: centred on the compact bar. Strip: flush with its screen edge, details open toward the screen centre.
    /// </summary>
    public static NotchLayout Expanded(NotchEdge edge, PxRect compact, PxSize size, PxRect work)
    {
        var height = Math.Min(size.Height, work.Height);
        var width = Math.Min(size.Width, work.Width);

        var left = edge switch
        {
            NotchEdge.Left => work.Left,
            NotchEdge.Right => work.Right - width,
            _ => Math.Clamp(compact.CenterX - width / 2, work.Left, work.Right - width),
        };

        ExpandDirection dir;
        int top;
        if (edge == NotchEdge.Top || compact.Top + height <= work.Bottom)
        {
            dir = ExpandDirection.Down;
            top = compact.Top;
        }
        else if (compact.Bottom - height >= work.Top)
        {
            dir = ExpandDirection.Up;
            top = compact.Bottom - height;
        }
        else
        {
            var below = work.Bottom - compact.Top;
            var above = compact.Bottom - work.Top;
            dir = below >= above ? ExpandDirection.Down : ExpandDirection.Up;
            top = dir == ExpandDirection.Down ? compact.Top : compact.Bottom - height;
        }
        top = Math.Clamp(top, work.Top, work.Bottom - height);
        return new NotchLayout(new PxRect(left, top, width, height), dir);
    }

    /// <summary>Recovers the compact rectangle from an expanded window rectangle (used after dragging while expanded).</summary>
    public static PxRect CompactWithin(NotchEdge edge, ExpandDirection dir, PxRect window, PxSize compact)
    {
        var left = edge switch
        {
            NotchEdge.Left => window.Left,
            NotchEdge.Right => window.Right - compact.Width,
            _ => window.CenterX - compact.Width / 2,
        };
        var top = dir == ExpandDirection.Down ? window.Top : window.Bottom - compact.Height;
        return new PxRect(left, top, compact.Width, compact.Height);
    }
}
