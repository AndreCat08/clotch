using Clotch.Core.Gauges;
using Clotch.Core.Layout;
using Clotch.Core.Settings;

namespace Clotch.Tests;

public class RingGeometryTests
{
    private const double C = 10, R = 8;

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Empty_values_draw_track_only(double pct)
    {
        var a = RingGeometry.Arc(pct, C, C, R);
        Assert.True(a.IsEmpty);
        Assert.False(a.IsFull);
        Assert.Equal(0, a.Fraction);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(99.99)]
    public void Full_values_draw_a_closed_circle(double pct)
    {
        var a = RingGeometry.Arc(pct, C, C, R);
        Assert.True(a.IsFull);
        Assert.False(a.IsEmpty);
        Assert.Equal(1, a.Fraction);
    }

    [Theory]
    // pct, endX, endY, largeArc — clockwise from 12 o'clock
    [InlineData(25, 18, 10, false)]
    [InlineData(50, 10, 18, false)]
    [InlineData(75, 2, 10, true)]
    public void Partial_values_end_on_the_expected_point(double pct, double x, double y, bool large)
    {
        var a = RingGeometry.Arc(pct, C, C, R);
        Assert.False(a.IsEmpty || a.IsFull);
        Assert.Equal((10.0, 2.0), (a.StartX, a.StartY));
        Assert.Equal(x, a.EndX, 6);
        Assert.Equal(y, a.EndY, 6);
        Assert.Equal(large, a.IsLargeArc);
    }

    [Fact]
    public void Nearly_full_is_still_an_arc()
    {
        var a = RingGeometry.Arc(99.9, C, C, R);
        Assert.False(a.IsFull);
        Assert.True(a.IsLargeArc);
        Assert.True(a.EndX < C); // just left of the start point
    }
}

public class GaugeStyleSettingsTests
{
    [Fact]
    public void Missing_field_defaults_to_bar()
    {
        using var d = new TempDir();
        var p = d.Write("settings.json", """{ "NotchCenterX": 100, "NotchTop": 0, "PollMinutes": 10 }""");
        var s = new SettingsStore(p).Load();
        Assert.Equal(CompactGaugeStyle.Bar, s.CompactGaugeStyle);
        Assert.Equal(10, s.PollMinutes);
    }

    [Theory]
    [InlineData(CompactGaugeStyle.Ring)]
    [InlineData(CompactGaugeStyle.Percent)]
    [InlineData(CompactGaugeStyle.Bar)]
    public void Style_roundtrips_as_a_name(CompactGaugeStyle style)
    {
        using var d = new TempDir();
        var store = new SettingsStore(d.Combine("settings.json"));
        store.Save(new AppSettings { CompactGaugeStyle = style });
        Assert.Contains($"\"{style}\"", File.ReadAllText(store.Path));
        Assert.Equal(style, store.Load().CompactGaugeStyle);
    }

    [Theory]
    [InlineData("\"Foo\"")]
    [InlineData("\"7\"")]
    [InlineData("3")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Unknown_value_falls_back_to_bar_and_keeps_other_settings(string raw)
    {
        using var d = new TempDir();
        var p = d.Write("settings.json",
            $$"""{ "NotchEdge": "Left", "NotchCenterX": 5, "NotchTop": 6, "NotchCenterY": 300, "PollMinutes": 15, "CompactGaugeStyle": {{raw}} }""");
        var s = new SettingsStore(p).Load();
        Assert.Equal(CompactGaugeStyle.Bar, s.CompactGaugeStyle);
        Assert.Equal((NotchEdge.Left, 15, 300), (s.NotchEdge, s.PollMinutes, s.NotchCenterY));
    }

    [Fact]
    public void Style_name_is_case_insensitive()
    {
        using var d = new TempDir();
        var p = d.Write("settings.json", """{ "CompactGaugeStyle": "ring" }""");
        Assert.Equal(CompactGaugeStyle.Ring, new SettingsStore(p).Load().CompactGaugeStyle);
    }
}
