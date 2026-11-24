using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// A time of day on phone-style wheels: hours, minutes and (12-hour clock) am/pm, each a drum the numbers roll around.
// Drag or scroll to spin; it coasts and settles on the nearest value; click a number to go to it.
// Follows Settings → Appearance → Clock (24-hour / 12-hour).
public class TimePicker : Grid
{
    readonly Wheel hour = new(loop: true), minute = new(loop: true), meridiem = new(loop: false);
    TimeSpan value = new(8, 0, 0);
    bool building;
    public event EventHandler? Changed;

    public TimePicker()
    {
        Height = Wheel.Rows * Wheel.RowHeight;
        // the band the chosen row sits in
        var band = new Border { Height = Wheel.RowHeight + 4, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        band.SetResourceReference(Border.BackgroundProperty, "Glass");
        band.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        band.BorderThickness = new Thickness(1);
        var colon = new TextBlock { Text = ":", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 3) };
        colon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        hour.Width = 52; minute.Width = 52; meridiem.Width = 52;
        row.Children.Add(hour); row.Children.Add(colon); row.Children.Add(minute); row.Children.Add(meridiem);
        Children.Add(band); Children.Add(row);
        band.HorizontalAlignment = HorizontalAlignment.Left;
        row.SizeChanged += (_, _) => band.Width = row.ActualWidth + 8;
        band.Margin = new Thickness(-4, 0, 0, 0);
        hour.Settled += (_, _) => Read(); minute.Settled += (_, _) => Read(); meridiem.Settled += (_, _) => Read();
        Loaded += (_, _) => Build();
    }

    public TimeSpan Value
    {
        get => value;
        set { this.value = new TimeSpan(value.Hours, value.Minutes, 0); Build(); }
    }

    // (re)draws for the current clock setting and value
    public void Build()
    {
        building = true;
        var twelve = When.TwelveHour;
        hour.Set(twelve ? Enumerable.Range(1, 12).Select(h => h.ToString()).ToList() : Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(),
                 twelve ? (value.Hours % 12 == 0 ? 11 : value.Hours % 12 - 1) : value.Hours);
        minute.Set(Enumerable.Range(0, 60).Select(m => m.ToString("00")).ToList(), value.Minutes);
        meridiem.Set(["am", "pm"], value.Hours < 12 ? 0 : 1);
        meridiem.Visibility = twelve ? Visibility.Visible : Visibility.Collapsed;
        building = false;
    }

    void Read()
    {
        if (building) return;
        var h = When.TwelveHour ? (hour.Index + 1) % 12 + (meridiem.Index == 1 ? 12 : 0) : hour.Index;
        var next = new TimeSpan(h, minute.Index, 0);
        if (next == value) return;
        value = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------- one drum ----------
    sealed class Wheel : Canvas
    {
        public const int Rows = 5;                 // visible rows: the chosen one and two either side
        public const double RowHeight = 30;
        const double StepAngle = 22;               // degrees between neighbouring rows on the drum
        readonly bool loop;
        List<string> items = [];
        readonly List<TextBlock> labels = [];
        double offset;                             // fractional index at the centre
        double velocity;                           // rows per frame while coasting
        bool dragging, coasting;
        Point last; DateTime lastTime; double dragMoved;
        public int Index => items.Count == 0 ? 0 : Mod((int)Math.Round(offset), items.Count);
        public event EventHandler? Settled;

        public Wheel(bool loop)
        {
            this.loop = loop;
            Height = Rows * RowHeight; ClipToBounds = true; Background = Brushes.Transparent; Cursor = Cursors.SizeNS;
            SizeChanged += (_, _) => Layout();
            MouseWheel += (_, e) => { e.Handled = true; StopCoast(); SnapTo(Math.Round(offset) + (e.Delta > 0 ? -1 : 1)); };
            MouseLeftButtonDown += (_, e) => { StopCoast(); dragging = true; dragMoved = 0; last = e.GetPosition(this); lastTime = DateTime.Now; velocity = 0; CaptureMouse(); e.Handled = true; };
            MouseMove += (_, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(this);
                var dy = p.Y - last.Y; dragMoved += Math.Abs(dy);
                var ms = Math.Max(1, (DateTime.Now - lastTime).TotalMilliseconds);
                offset = Clamp(offset - dy / RowHeight);
                velocity = -dy / RowHeight * (16.0 / ms);
                last = p; lastTime = DateTime.Now;
                Layout();
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!dragging) return;
                dragging = false; ReleaseMouseCapture();
                if (dragMoved < 3)   // a click: go to the row clicked
                {
                    var rows = (e.GetPosition(this).Y - Height / 2) / RowHeight;
                    SnapTo(Math.Round(offset + Math.Round(rows)));
                }
                else if (Math.Abs(velocity) > 0.05) { coasting = true; CompositionTarget.Rendering += Coast; }
                else SnapTo(Math.Round(offset));
            };
        }

        public void Set(List<string> list, int index)
        {
            StopCoast();
            if (!list.SequenceEqual(items))
            {
                items = list;
                Children.Clear(); labels.Clear();
                foreach (var t in items)
                {
                    var tb = new TextBlock { Text = t, FontSize = 17, TextAlignment = TextAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false };
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                    labels.Add(tb); Children.Add(tb);
                }
            }
            offset = index;
            Layout();
        }

        static int Mod(int a, int n) => ((a % n) + n) % n;
        double Clamp(double o) => loop || items.Count == 0 ? o : Math.Clamp(o, 0, items.Count - 1);

        // each label on the drum: its angle from the centre sets where it is, how squashed and how faint
        void Layout()
        {
            if (items.Count == 0 || ActualWidth == 0) return;
            var radius = Height / 2 - 4;
            for (int i = 0; i < labels.Count; i++)
            {
                double d = i - offset;
                if (loop) d -= Math.Round(d / items.Count) * items.Count;   // the nearest copy around the loop
                var angle = d * StepAngle;
                var tb = labels[i];
                if (Math.Abs(angle) >= 90) { tb.Visibility = Visibility.Hidden; continue; }
                var rad = angle * Math.PI / 180;
                var y = Height / 2 + Math.Sin(rad) * radius;
                var squash = Math.Cos(rad);
                tb.Visibility = Visibility.Visible;
                tb.Width = ActualWidth;
                tb.Measure(new Size(ActualWidth, double.PositiveInfinity));
                SetLeft(tb, 0); SetTop(tb, y - tb.DesiredSize.Height / 2);
                tb.RenderTransform = new ScaleTransform(0.82 + 0.18 * squash, squash);
                tb.Opacity = Math.Pow(squash, 2.2);
                tb.FontWeight = Math.Abs(d) < 0.5 ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        void Coast(object? s, EventArgs e)
        {
            offset = Clamp(offset + velocity);
            velocity *= 0.92;
            Layout();
            if (Math.Abs(velocity) < 0.02 || (!loop && (offset <= 0 || offset >= items.Count - 1))) { StopCoast(); SnapTo(Math.Round(offset)); }
        }

        void StopCoast() { if (!coasting) return; coasting = false; CompositionTarget.Rendering -= Coast; }

        // a short eased glide to a whole row, then report it
        void SnapTo(double target)
        {
            target = Clamp(target);
            var from = offset; var start = DateTime.Now; const double ms = 180;
            void Step(object? s, EventArgs e)
            {
                var t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / ms);
                var ease = 1 - Math.Pow(1 - t, 3);
                offset = from + (target - from) * ease;
                Layout();
                if (t < 1) return;
                CompositionTarget.Rendering -= Step;
                if (loop && items.Count > 0) offset = Mod((int)Math.Round(offset), items.Count);   // keep the number small
                Layout();
                Settled?.Invoke(this, EventArgs.Empty);
            }
            CompositionTarget.Rendering += Step;
        }
    }
}
