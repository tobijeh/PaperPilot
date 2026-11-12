using System.Windows;
using System.Windows.Media;

namespace PaperPilot.UI;

/// <summary>
/// The Paper Pilot plane as a simple silhouette (wing and body, as in the app icon), drawn in a
/// 24×24 box flying to the right, so the trail can stream out behind it on the left.
/// </summary>
public static class PlaneGlyph
{
    public static readonly Geometry Geometry = Build();

    private static Geometry Build()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            // Rounded, tapered strokes, like the icon's soft shapes.
            Capsule(c, 3.2, 13.4, 21.2, 9.9, 1.0, 1.55);   // body: thin tail, blunt nose (flying right)
            Capsule(c, 8.4, 20.3, 17.9, 3.6, 1.45, 1.25);  // main wing, swept across the body
            Capsule(c, 3.5, 12.6, 3.0, 8.6, 1.15, 0.95);   // tail fin
            Capsule(c, 4.6, 14.2, 2.0, 17.6, 0.95, 0.8);   // tail plane
        }
        g.FillRule = FillRule.Nonzero;
        g.Freeze();
        return g;
    }

    /// <summary>A tapered capsule from (x1,y1) with radius r1 to (x2,y2) with radius r2.</summary>
    private static void Capsule(StreamGeometryContext c, double x1, double y1, double x2, double y2, double r1, double r2)
    {
        double angle = Math.Atan2(y2 - y1, x2 - x1);
        var pts = new List<Point>();
        Arc(pts, x2, y2, r2, angle - Math.PI / 2, angle + Math.PI / 2);
        Arc(pts, x1, y1, r1, angle + Math.PI / 2, angle + 3 * Math.PI / 2);
        c.BeginFigure(pts[0], isFilled: true, isClosed: true);
        for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i], isStroked: true, isSmoothJoin: true);
    }

    private static void Arc(List<Point> pts, double cx, double cy, double r, double a0, double a1, int steps = 8)
    {
        for (int i = 0; i <= steps; i++)
        {
            double a = a0 + (a1 - a0) * i / steps;
            pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
    }

    /// <summary>Draws the plane filling <paramref name="box"/>.</summary>
    public static void Draw(DrawingContext dc, Brush fill, Rect box, Pen? outline = null)
    {
        dc.PushTransform(new TranslateTransform(box.X, box.Y));
        dc.PushTransform(new ScaleTransform(box.Width / 24.0, box.Height / 24.0));
        dc.DrawGeometry(fill, outline, Geometry);
        dc.Pop();
        dc.Pop();
    }
}
