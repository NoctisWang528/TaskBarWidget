using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace TaskBarWidgets.UI;

// Colored, layered weather symbols drawn as vector geometry. Every shape lives in a
// 128x128 design space and is scaled by a Viewbox, so the same artwork stays crisp at
// the 42 DIP hero size and the 28 DIP forecast size. Colors come from a light or dark
// palette because the cards sit on translucent Mica surfaces in both themes.
internal static class WeatherIconArt
{
    private const double DesignSize = 128;

    private sealed class Palette
    {
        internal Color Sun { get; init; }
        internal Color Cloud { get; init; }
        internal Color CloudShade { get; init; }
        internal Color Rain { get; init; }
        internal Color Snow { get; init; }
        internal Color Bolt { get; init; }
        internal Color Fog { get; init; }
        internal Color Dust { get; init; }
        internal Color Moon { get; init; }
        internal Color Star { get; init; }

        internal static readonly Palette Light = new()
        {
            Sun = Rgb(0xFF, 0xC2, 0x47),
            Cloud = Rgb(0xF6, 0xFB, 0xFF),
            CloudShade = Rgb(0xA9, 0xC2, 0xD8),
            Rain = Rgb(0x3E, 0x97, 0xDC),
            Snow = Rgb(0xCF, 0xE4, 0xF6),
            Bolt = Rgb(0xFF, 0xC5, 0x3D),
            Fog = Rgb(0xB7, 0xC7, 0xD6),
            Dust = Rgb(0xD9, 0xB9, 0x8A),
            Moon = Rgb(0xF7, 0xE7, 0xB4),
            Star = Rgb(0xFF, 0xD1, 0x6B)
        };

        internal static readonly Palette Dark = new()
        {
            Sun = Rgb(0xFF, 0xCB, 0x5C),
            Cloud = Rgb(0xE9, 0xF2, 0xFC),
            CloudShade = Rgb(0x6C, 0x7F, 0x92),
            Rain = Rgb(0x59, 0xAB, 0xF0),
            Snow = Rgb(0xE6, 0xF1, 0xFB),
            Bolt = Rgb(0xFF, 0xD1, 0x66),
            Fog = Rgb(0x8B, 0xA0, 0xB2),
            Dust = Rgb(0xC9, 0xA8, 0x7A),
            Moon = Rgb(0xFB, 0xEE, 0xC2),
            Star = Rgb(0xFF, 0xE0, 0x8C)
        };
    }

    internal static Color Rgb(byte red, byte green, byte blue) => Color.FromArgb(0xFF, red, green, blue);

    internal static FrameworkElement Build(string kind, bool night, double size, bool lightTheme)
    {
        var palette = lightTheme ? Palette.Light : Palette.Dark;
        var canvas = new Canvas { Width = DesignSize, Height = DesignSize };
        try
        {
            Compose(canvas, palette, Normalize(kind), night);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A symbol failure must never take the whole card down; fall back to a cloud.
            canvas.Children.Clear();
            AddCloud(canvas, palette, 0, 0);
        }
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    internal static string Normalize(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "clear" or "sunny" => "clear",
        "partly-cloudy" or "cloudy" or "few-clouds" => "partly-cloudy",
        "overcast" => "overcast",
        "rain" or "shower" => "rain",
        "drizzle" => "drizzle",
        "snow" => "snow",
        "sleet" => "sleet",
        "fog" or "mist" => "fog",
        "haze" or "smog" => "haze",
        "dust" or "sand" => "dust",
        "thunder" or "thunderstorm" => "thunder",
        "wind" or "windy" => "wind",
        _ => "unknown"
    };

    private static void Compose(Canvas canvas, Palette palette, string kind, bool night)
    {
        switch (kind)
        {
            case "clear":
                if (night)
                {
                    AddMoon(canvas, palette, new Point(62, 60), 30);
                    AddStar(canvas, palette, 98, 38, 7);
                    AddStar(canvas, palette, 108, 70, 5.5);
                    AddStar(canvas, palette, 86, 100, 4.5);
                }
                else
                {
                    AddSun(canvas, palette, new Point(64, 62), 26, 37, 48, 7);
                }
                break;
            case "partly-cloudy":
                if (night) AddMoon(canvas, palette, new Point(46, 40), 22);
                else AddSun(canvas, palette, new Point(46, 40), 16, 25, 33, 5.5);
                AddCloud(canvas, palette, 0, 0);
                break;
            case "overcast":
                AddCloudLayer(canvas, palette.CloudShade, -10, -26, 0.9);
                AddCloud(canvas, palette, 0, 0);
                break;
            case "rain":
                AddCloud(canvas, palette, 0, -10);
                AddDrop(canvas, palette.Rain, 60, 94, 4.2);
                AddDrop(canvas, palette.Rain, 84, 104, 4.2);
                AddDrop(canvas, palette.Rain, 108, 94, 4.2);
                break;
            case "drizzle":
                AddCloud(canvas, palette, 0, -10);
                AddDrop(canvas, palette.Rain, 68, 98, 3.2);
                AddDrop(canvas, palette.Rain, 96, 106, 3.2);
                break;
            case "snow":
                AddCloud(canvas, palette, 0, -10);
                AddFlake(canvas, palette.Snow, 60, 102, 5.5);
                AddFlake(canvas, palette.Snow, 84, 112, 5.5);
                AddFlake(canvas, palette.Snow, 108, 102, 5.5);
                break;
            case "sleet":
                AddCloud(canvas, palette, 0, -10);
                AddDrop(canvas, palette.Rain, 62, 98, 3.8);
                AddFlake(canvas, palette.Snow, 88, 108, 5);
                AddDrop(canvas, palette.Rain, 110, 98, 3.8);
                break;
            case "fog":
                AddCloud(canvas, palette, 0, -20);
                AddBar(canvas, palette.Fog, 22, 84, 84);
                AddBar(canvas, palette.Fog, 34, 100, 72);
                AddBar(canvas, palette.Fog, 46, 116, 42);
                break;
            case "haze":
                AddSun(canvas, palette, new Point(64, 42), 22, 33, 42, 6);
                AddBar(canvas, palette.Fog, 22, 92, 84);
                AddBar(canvas, palette.Fog, 36, 108, 70);
                break;
            case "dust":
                AddWind(canvas, palette.Dust, 6);
                AddDot(canvas, palette.Dust, 30, 108, 3.4);
                AddDot(canvas, palette.Dust, 62, 118, 2.6);
                AddDot(canvas, palette.Dust, 96, 108, 3);
                break;
            case "thunder":
                AddCloud(canvas, palette, 0, -14);
                AddBolt(canvas, palette.Bolt);
                break;
            case "wind":
                AddWind(canvas, palette.Fog, 6);
                break;
            default:
                AddCloud(canvas, palette, 0, 0);
                break;
        }
    }

    private static void AddSun(Canvas canvas, Palette palette, Point center, double radius, double rayInner, double rayOuter, double rayWidth)
    {
        var rays = new PathGeometry();
        for (var index = 0; index < 8; index++)
        {
            var angle = Math.PI * index / 4;
            var figure = new PathFigure
            {
                StartPoint = new Point(center.X + rayInner * Math.Cos(angle), center.Y + rayInner * Math.Sin(angle)),
                IsClosed = false
            };
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(center.X + rayOuter * Math.Cos(angle), center.Y + rayOuter * Math.Sin(angle))
            });
            rays.Figures.Add(figure);
        }
        canvas.Children.Add(new Path
        {
            Data = rays,
            Stroke = new SolidColorBrush(palette.Sun),
            StrokeThickness = rayWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        Place(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = new SolidColorBrush(palette.Sun) },
            center.X - radius, center.Y - radius, canvas);
    }

    private static void AddMoon(Canvas canvas, Palette palette, Point center, double radius)
    {
        var crescent = new GeometryGroup { FillRule = FillRule.EvenOdd };
        crescent.Children.Add(new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius });
        crescent.Children.Add(new EllipseGeometry
        {
            Center = new Point(center.X + radius * 0.42, center.Y - radius * 0.34),
            RadiusX = radius * 0.88,
            RadiusY = radius * 0.88
        });
        canvas.Children.Add(new Path { Data = crescent, Fill = new SolidColorBrush(palette.Moon) });
    }

    private static void AddCloud(Canvas canvas, Palette palette, double offsetX, double offsetY)
    {
        // The shade layer sits behind and slightly lower, which keeps the cloud readable
        // on a near-white card without needing an outline.
        AddCloudLayer(canvas, palette.CloudShade, offsetX + 4, offsetY + 6, 1);
        AddCloudLayer(canvas, palette.Cloud, offsetX, offsetY, 1);
    }

    private static void AddCloudLayer(Canvas canvas, Color color, double offsetX, double offsetY, double scale)
    {
        var brush = new SolidColorBrush(color);
        var layer = new Canvas { Width = DesignSize, Height = DesignSize };
        Place(new Ellipse { Width = 30, Height = 30, Fill = brush }, 45, 69, layer);
        Place(new Ellipse { Width = 46, Height = 46, Fill = brush }, 61, 51, layer);
        Place(new Ellipse { Width = 32, Height = 32, Fill = brush }, 90, 68, layer);
        Place(new Rectangle { Width = 46, Height = 17, RadiusX = 8.5, RadiusY = 8.5, Fill = brush }, 60, 83, layer);
        layer.RenderTransform = new CompositeTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            TranslateX = offsetX,
            TranslateY = offsetY
        };
        canvas.Children.Add(layer);
    }

    private static void Place(FrameworkElement element, double left, double top, Canvas parent)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        parent.Children.Add(element);
    }

    private static void AddDrop(Canvas canvas, Color color, double x, double y, double radius)
    {
        var figure = new PathFigure { StartPoint = new Point(x, y), IsClosed = true };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(x + radius * 0.72, y + radius * 0.94),
            Point2 = new Point(x + radius, y + radius * 1.36),
            Point3 = new Point(x + radius, y + radius * 1.83)
        });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x - radius, y + radius * 1.83),
            Size = new Size(radius, radius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(x - radius, y + radius * 1.36),
            Point2 = new Point(x - radius * 0.72, y + radius * 0.94),
            Point3 = new Point(x, y)
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        canvas.Children.Add(new Path { Data = geometry, Fill = new SolidColorBrush(color) });
    }

    private static void AddFlake(Canvas canvas, Color color, double x, double y, double radius)
    {
        var geometry = new PathGeometry();
        for (var index = 0; index < 3; index++)
        {
            var angle = Math.PI * index / 3;
            var dx = radius * Math.Cos(angle);
            var dy = radius * Math.Sin(angle);
            var figure = new PathFigure { StartPoint = new Point(x - dx, y - dy), IsClosed = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x + dx, y + dy) });
            geometry.Figures.Add(figure);
        }
        canvas.Children.Add(new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    private static void AddBar(Canvas canvas, Color color, double x, double y, double width) =>
        Place(new Rectangle { Width = width, Height = 9, RadiusX = 4.5, RadiusY = 4.5, Fill = new SolidColorBrush(color) }, x, y, canvas);

    private static void AddDot(Canvas canvas, Color color, double x, double y, double radius) =>
        Place(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = new SolidColorBrush(color) }, x - radius, y - radius, canvas);

    private static void AddStar(Canvas canvas, Palette palette, double x, double y, double radius)
    {
        var figure = new PathFigure { StartPoint = new Point(x, y - radius), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(x + radius * 0.34, y) });
        figure.Segments.Add(new LineSegment { Point = new Point(x, y + radius) });
        figure.Segments.Add(new LineSegment { Point = new Point(x - radius * 0.34, y) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        canvas.Children.Add(new Path { Data = geometry, Fill = new SolidColorBrush(palette.Star) });
    }

    private static void AddBolt(Canvas canvas, Color color)
    {
        var figure = new PathFigure { StartPoint = new Point(70, 90), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(56, 112) });
        figure.Segments.Add(new LineSegment { Point = new Point(68, 112) });
        figure.Segments.Add(new LineSegment { Point = new Point(62, 126) });
        figure.Segments.Add(new LineSegment { Point = new Point(84, 102) });
        figure.Segments.Add(new LineSegment { Point = new Point(71, 102) });
        figure.Segments.Add(new LineSegment { Point = new Point(80, 90) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        canvas.Children.Add(new Path { Data = geometry, Fill = new SolidColorBrush(color) });
    }

    private static void AddWind(Canvas canvas, Color color, double thickness)
    {
        var geometry = new PathGeometry();
        AddWindFigure(geometry, new Point(16, 48), 78, new Point(87, 30));
        AddWindFigure(geometry, new Point(30, 72), 92, new Point(101, 90));
        var shortFigure = new PathFigure { StartPoint = new Point(42, 96), IsClosed = false };
        shortFigure.Segments.Add(new LineSegment { Point = new Point(72, 96) });
        geometry.Figures.Add(shortFigure);
        canvas.Children.Add(new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private static void AddWindFigure(PathGeometry geometry, Point start, double turnX, Point end)
    {
        var radius = Math.Abs(end.Y - start.Y) / 2;
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new LineSegment { Point = new Point(turnX, start.Y) });
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        geometry.Figures.Add(figure);
    }
}
