namespace Clotch.Core.Gauges;

/// <summary>How the 5-hour and weekly gauges are drawn in the collapsed notch.</summary>
public enum CompactGaugeStyle { Bar, Ring, Percent }

/// <summary>
/// One progress arc on a circle, starting at 12 o'clock and running clockwise.
/// <see cref="IsEmpty"/> means draw only the track; <see cref="IsFull"/> means draw a closed circle
/// (a single arc segment cannot describe 360°).
/// </summary>
public readonly record struct RingArc(double Fraction, double StartX, double StartY, double EndX, double EndY, bool IsLargeArc, bool IsEmpty, bool IsFull);

public static class RingGeometry
{
    /// <summary>Fractions this close to 1 are drawn as a full circle (start and end would coincide).</summary>
    private const double FullThreshold = 0.9995;

    /// <param name="percent">Utilization 0..100; out-of-range values are clamped, NaN counts as 0.</param>
    public static RingArc Arc(double percent, double centerX, double centerY, double radius)
    {
        var fraction = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100) / 100.0;
        var startX = centerX;
        var startY = centerY - radius;
        if (fraction <= 0) return new RingArc(0, startX, startY, startX, startY, false, IsEmpty: true, IsFull: false);
        if (fraction >= FullThreshold) return new RingArc(1, startX, startY, startX, startY, true, IsEmpty: false, IsFull: true);

        var angle = fraction * 2 * Math.PI;
        var endX = centerX + radius * Math.Sin(angle);
        var endY = centerY - radius * Math.Cos(angle);
        return new RingArc(fraction, startX, startY, endX, endY, fraction > 0.5, IsEmpty: false, IsFull: false);
    }
}
