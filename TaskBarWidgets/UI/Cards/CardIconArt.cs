using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace TaskBarWidgets.UI.Cards;

internal enum CardIconKind
{
    Previous,
    Play,
    Pause,
    Next,
    Speaker,
    SpeakerMuted,
    Microphone,
    MicrophoneMuted,
    Settings,
    Refresh
}

internal sealed class CardIconHost : Grid
{
    internal CardIconHost(CardIconKind kind, double iconSize)
    {
        Width = Height = CardIconArt.HostSize;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Kind = kind;
        Icon = new PathIcon
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = CardIconArt.Build(kind)
        };
        AutomationProperties.SetAutomationId(Icon, "card-icon-" + kind);
        Children.Add(Icon);
    }

    internal CardIconKind Kind { get; private set; }

    internal PathIcon Icon { get; }

    internal void SetKind(CardIconKind kind)
    {
        if (Kind == kind) return;
        Kind = kind;
        Icon.Data = CardIconArt.Build(kind);
        AutomationProperties.SetAutomationId(Icon, "card-icon-" + kind);
    }
}

// Card controls deliberately avoid Segoe Fluent Icons. The symbol font can be
// absent or replaced by the system, which previously left square fallback
// glyphs for mute and speaker states.
internal static class CardIconArt
{
    internal const double HostSize = 20;

    internal static Button CreateButton(CardIconKind kind, string automationName, double diameter = 32)
    {
        var button = new Button
        {
            Width = diameter,
            Height = diameter,
            MinWidth = 0,
            MinHeight = 0,
            UseLayoutRounding = false,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(diameter / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new CardIconHost(kind, diameter > 32 ? 20 : 16)
        };
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    internal static void SetKind(Button button, CardIconKind kind)
    {
        if (button.Content is CardIconHost host) host.SetKind(kind);
    }

    internal static Geometry Build(CardIconKind kind) => kind switch
    {
        CardIconKind.Previous => Group(
            Rect(3.4, 3.5, 2.2, 13),
            Polygon(new Point(16.8, 3.4), new Point(16.8, 16.6), new Point(7.2, 10))),
        CardIconKind.Play => Polygon(new Point(6.3, 3.3), new Point(16.7, 10), new Point(6.3, 16.7)),
        CardIconKind.Pause => Group(
            RoundedRect(5.1, 3.4, 3.6, 13.2, 1.4),
            RoundedRect(11.3, 3.4, 3.6, 13.2, 1.4)),
        CardIconKind.Next => Group(
            Polygon(new Point(3.2, 3.4), new Point(12.8, 10), new Point(3.2, 16.6)),
            Rect(14.4, 3.5, 2.2, 13)),
        CardIconKind.Speaker => Group(
            Polygon(new Point(1.8, 7.2), new Point(5.8, 7.2), new Point(10.8, 3.1),
                new Point(10.8, 16.9), new Point(5.8, 12.8), new Point(1.8, 12.8)),
            RingSegment(new Point(10.4, 10), 5.7, 4.7, -52, 52),
            RingSegment(new Point(10.4, 10), 8.1, 7.1, -48, 48)),
        CardIconKind.SpeakerMuted => Group(
            Polygon(new Point(1.8, 7.2), new Point(5.8, 7.2), new Point(10.8, 3.1),
                new Point(10.8, 16.9), new Point(5.8, 12.8), new Point(1.8, 12.8)),
            Polygon(new Point(13.2, 6.2), new Point(15.2, 4.2), new Point(18.2, 7.2),
                new Point(17.2, 8.2), new Point(18.2, 9.2), new Point(15.2, 12.2),
                new Point(13.2, 10.2), new Point(14.2, 9.2), new Point(13.2, 8.2),
                new Point(14.2, 7.2))),
        CardIconKind.Microphone => Group(
            RoundedRect(7.3, 2.1, 5.4, 8.8, 2.7),
            RingSegment(new Point(10, 10.1), 4.7, 3.8, 28, 152),
            Rect(9.3, 13.8, 1.4, 3.3),
            RoundedRect(6.9, 16.2, 6.2, 1.7, .85)),
        CardIconKind.MicrophoneMuted => Group(
            RoundedRect(7.3, 2.1, 5.4, 8.8, 2.7),
            RingSegment(new Point(10, 10.1), 4.7, 3.8, 28, 152),
            Rect(9.3, 13.8, 1.4, 3.3),
            RoundedRect(6.9, 16.2, 6.2, 1.7, .85),
            Polygon(new Point(3.2, 2.7), new Point(4.8, 2.7), new Point(17.2, 17.3),
                new Point(15.6, 17.3))),
        CardIconKind.Settings => SettingsGear(),
        CardIconKind.Refresh => Group(
            RingSegment(new Point(10, 10), 8.4, 7.1, 35, 310),
            Polygon(new Point(12.5, 1.4), new Point(12.5, 7.5), new Point(18.4, 4.45))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static Geometry SettingsGear()
    {
        const int teeth = 8;
        const double center = 10;
        const double outerRadius = 9.3;
        const double rootRadius = 7.2;
        var points = new List<Point>(teeth * 4);
        for (var index = 0; index < teeth; index++)
        {
            var angle = index * 45d;
            points.Add(PointAt(center, center, rootRadius, angle - 22));
            points.Add(PointAt(center, center, outerRadius, angle - 13));
            points.Add(PointAt(center, center, outerRadius, angle + 13));
            points.Add(PointAt(center, center, rootRadius, angle + 22));
        }
        return Group(FillRule.EvenOdd, Polygon(points), Circle(10, 10, 3.15));
    }

    private static GeometryGroup Group(params Geometry[] geometries) =>
        Group(FillRule.Nonzero, geometries);

    private static GeometryGroup Group(FillRule rule, params Geometry[] geometries)
    {
        var group = new GeometryGroup { FillRule = rule };
        foreach (var geometry in geometries) group.Children.Add(geometry);
        return group;
    }

    private static Geometry Polygon(params Point[] points) => Polygon((IEnumerable<Point>)points);

    private static Geometry Polygon(IEnumerable<Point> points)
    {
        var figure = new PathFigure
        {
            StartPoint = points.First(),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new PolyLineSegment { Points = new PointCollection() });
        var line = (PolyLineSegment)figure.Segments[0];
        foreach (var point in points.Skip(1)) line.Points.Add(point);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static RectangleGeometry Rect(double x, double y, double width, double height) =>
        new() { Rect = new Rect(x, y, width, height) };

    private static EllipseGeometry Circle(double x, double y, double radius) =>
        new() { Center = new Point(x, y), RadiusX = radius, RadiusY = radius };

    private static Geometry RoundedRect(double x, double y, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        var figure = new PathFigure
        {
            StartPoint = new Point(x + radius, y),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment { Point = new Point(x + width - radius, y) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + width, y + radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(x + width, y + height - radius) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + width - radius, y + height),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(x + radius, y + height) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x, y + height - radius),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(x, y + radius) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x + radius, y),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Geometry RingSegment(Point center, double outerRadius, double innerRadius, double startAngle, double endAngle)
    {
        var largeArc = endAngle - startAngle > 180;
        var figure = new PathFigure
        {
            StartPoint = PointAt(center.X, center.Y, outerRadius, startAngle),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(center.X, center.Y, outerRadius, endAngle),
            Size = new Size(outerRadius, outerRadius),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment
        {
            Point = PointAt(center.X, center.Y, innerRadius, endAngle)
        });
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(center.X, center.Y, innerRadius, startAngle),
            Size = new Size(innerRadius, innerRadius),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Counterclockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointAt(double centerX, double centerY, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(centerX + radius * Math.Cos(radians), centerY + radius * Math.Sin(radians));
    }
}
