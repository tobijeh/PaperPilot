using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperPilot.Core;

namespace PaperPilot.UI;

/// <summary>
/// The bubble's look: a rounded capsule with the plane at the front and the voice waveform
/// streaming out behind it like a cloud trail, plus a brief word when the state changes.
/// Used by the floating bubble and by the preview on the Bubble settings page.
/// </summary>
public sealed class BubbleView : Border
{
    private readonly TrailAndPlane _art;
    private readonly TextBlock _word;
    private readonly Border _wordClip;
    private readonly DispatcherTimer _wordTimer = new();
    private PilotState _state = PilotState.Holding;

    public BubbleView()
    {
        _art = new TrailAndPlane();
        _word = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.FindResource("AppFont"),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _word.SetResourceReference(TextBlock.ForegroundProperty, "BubbleText");
        _wordClip = new Border { Child = _word, Width = 0, ClipToBounds = true, Opacity = 0 };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_wordClip);
        row.Children.Add(_art);
        Child = row;

        SetResourceReference(BackgroundProperty, "BubbleBg");
        SetResourceReference(BorderBrushProperty, "BubbleEdge");
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        SnapsToDevicePixels = true;

        _wordTimer.Tick += (_, _) =>
        {
            _wordTimer.Stop();
            AnimateWord(show: false);
        };
        ThemeManager.Changed += () => _art.InvalidateVisual();
        ApplySize(PillSize.Small);
    }

    public PilotState State => _state;

    public void ApplySize(PillSize size)
    {
        double h = size == PillSize.Medium ? 34 : 28;
        Height = h;
        CornerRadius = new CornerRadius(h / 2);
        Padding = new Thickness(h * 0.36, 0, h * 0.28, 0);
        _art.Height = h - 6;
        _art.Width = (h - 6) * 2.9;
        _word.FontSize = size == PillSize.Medium ? 14 : 12;
    }

    /// <summary>Feed a microphone level (0..1); drawn as the trail behind the plane.</summary>
    public void PushLevel(double level) => _art.PushLevel(level);

    public void SetState(PilotState state, string? word, bool showWord)
    {
        _state = state;
        _art.SetState(state);
        bool dashed = state == PilotState.Problem;
        BorderThickness = new Thickness(dashed ? 1.25 : 1);
        // A dashed edge can't be drawn by Border; the art draws it instead.
        _art.DashedEdge = dashed;
        if (dashed) BorderBrush = Brushes.Transparent;
        else SetResourceReference(BorderBrushProperty, "BubbleEdge");

        if (showWord && !string.IsNullOrEmpty(word)) ShowWord(word, TimeSpan.FromMilliseconds(1600));
    }

    public void ShowWord(string word, TimeSpan hold)
    {
        _word.Text = word;
        AnimateWord(show: true);
        _wordTimer.Stop();
        _wordTimer.Interval = hold;
        _wordTimer.Start();
    }

    private void AnimateWord(bool show)
    {
        _word.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double target = show ? _word.DesiredSize.Width : 0;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(show ? 260 : 360);
        _wordClip.BeginAnimation(WidthProperty, new DoubleAnimation(target, dur) { EasingFunction = ease });
        _wordClip.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, dur) { EasingFunction = ease });
    }

    /// <summary>The trail and the plane, drawn together.</summary>
    private sealed class TrailAndPlane : FrameworkElement
    {
        private readonly TrailBuffer _trail = new(28);
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        private PilotState _state = PilotState.Holding;

        public TrailAndPlane()
        {
            _timer.Tick += (_, _) =>
            {
                _trail.Tick();
                InvalidateVisual();
                if (_trail.IsSilent) _timer.Stop();
            };
            IsVisibleChanged += (_, _) => { if (!IsVisible) { _timer.Stop(); _trail.Clear(); } };
        }

        public bool DashedEdge { get; set; }

        public void PushLevel(double level)
        {
            if (!Listening) return;
            _trail.Push(level);
            if (!_timer.IsEnabled && IsVisible) _timer.Start();
        }

        private bool Listening => _state is PilotState.Following or PilotState.Holding;

        public void SetState(PilotState state)
        {
            _state = state;
            if (!Listening) _trail.Clear();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double planeSize = h;
            double planeX = w - planeSize;
            double mid = h * 0.56; // the tail sits a little below centre
            string planeKey = _state switch
            {
                PilotState.Following => "PlaneFollowing",
                PilotState.Holding => "PlaneHolding",
                _ => "PlaneInactive",
            };
            var planeColor = ThemeManager.Get(planeKey);

            // ---- Cloud trail -------------------------------------------------------------
            double trailRight = planeX + planeSize * 0.18; // tucks under the tail
            double trailWidth = trailRight;
            var trailColor = ThemeManager.Get(_state == PilotState.Following ? "TrailFollowing" : "TrailHolding");
            if (!Listening) trailColor = ThemeManager.Get("PlaneInactive");

            var fade = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, trailColor.R, trailColor.G, trailColor.B), 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(Listening ? (byte)200 : (byte)90, trailColor.R, trailColor.G, trailColor.B), 1));

            int n = _trail.Length;
            double step = trailWidth / (n - 1);
            double maxAmp = h * 0.42;
            var top = new Point[n];
            var bottom = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double x = trailRight - i * step;
                double taper = 1 - Math.Pow(i / (double)(n - 1), 1.6) * 0.55; // older = thinner
                double amp = 0.7 + _trail[i] * maxAmp * taper;              // always a hairline
                top[i] = new Point(x, mid - amp);
                bottom[i] = new Point(x, mid + amp);
            }
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(top[0], true, true);
                c.PolyBezierTo(Smooth(top), true, true);
                c.LineTo(bottom[n - 1], true, true);
                var rev = bottom.Reverse().ToArray();
                c.PolyBezierTo(Smooth(rev), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(fade, null, geo);

            // ---- Plane ---------------------------------------------------------------------
            PlaneGlyph.Draw(dc, new SolidColorBrush(planeColor), new Rect(planeX, 0, planeSize, planeSize));

            // ---- Dashed edge for Problem (drawn around the whole bubble by the parent's size) --
            if (DashedEdge && VisualTreeHelper.GetParent(this) is FrameworkElement row
                && VisualTreeHelper.GetParent(row) is BubbleView bubble)
            {
                var pen = new Pen(new SolidColorBrush(ThemeManager.Get("BubbleEdge")), 1.25) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
                var origin = TranslatePoint(new Point(0, 0), bubble);
                var r = new Rect(-origin.X + 0.6, -origin.Y + 0.6, bubble.ActualWidth - 1.2, bubble.ActualHeight - 1.2);
                dc.DrawRoundedRectangle(null, pen, r, r.Height / 2, r.Height / 2);
            }
        }

        /// <summary>Catmull-Rom → Bézier control points through the given points (skipping the first).</summary>
        private static List<Point> Smooth(Point[] p)
        {
            var result = new List<Point>((p.Length - 1) * 3);
            for (int i = 0; i < p.Length - 1; i++)
            {
                var p0 = p[Math.Max(0, i - 1)];
                var p1 = p[i];
                var p2 = p[i + 1];
                var p3 = p[Math.Min(p.Length - 1, i + 2)];
                result.Add(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6));
                result.Add(new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6));
                result.Add(p2);
            }
            return result;
        }
    }
}
