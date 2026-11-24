using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Ivo;

public enum IvoAvatarState
{
    Idle,
    Happy,
    Thinking,
    Listening,
    Excited,
    Sleep,
    Loading,
    Error,
    Celebrate
}

// Ivo's shared avatar. It uses one flat mascot render and only compositor-friendly transforms, so every app surface
// gets the same image and animation behavior.
public sealed class IvoAvatar : Viewbox
{
    const double W = 200, H = 200;
    static readonly BitmapImage? Portrait = LoadPortrait();

    static BitmapImage? LoadPortrait()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "blub.png");
        if (!System.IO.File.Exists(path)) return null;
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(path);
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.EndInit();
        b.Freeze();
        return b;
    }

    internal static List<byte[]> ReadFrames(string path)
    {
        var all = new List<byte[]>();
        if (!System.IO.File.Exists(path)) return all;
        var bytes = System.IO.File.ReadAllBytes(path);
        for (var at = 0; at + 4 <= bytes.Length;)
        {
            var n = BitConverter.ToInt32(bytes, at);
            at += 4;
            if (n <= 0 || at + n > bytes.Length) break;
            all.Add(bytes[at..(at + n)]);
            at += n;
        }
        return all;
    }

    internal static BitmapImage Frame(byte[] jpeg)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.StreamSource = new System.IO.MemoryStream(jpeg);
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.EndInit();
        b.Freeze();
        return b;
    }

    public bool Alive { get; init; } = true;
    public static bool ReducedMotion { get; set; }

    readonly Image front = new();
    readonly ScaleTransform drift = new(1, 1, W / 2, H * .35);
    readonly TranslateTransform breathe = new();
    readonly RotateTransform sway = new(0, W / 2, H);
    readonly ScaleTransform blink = new(1, 1, W / 2, H * .48);
    readonly DispatcherTimer blinkTimer = new(System.Windows.Threading.DispatcherPriority.Background);
    readonly Random random = new();
    readonly Canvas sparkles = new();
    IvoAvatarState state = IvoAvatarState.Idle;
    static readonly List<IvoAvatar> instances = [];
    public static event Action<IvoAvatarState>? StateChanged;
    bool happy;

    public IvoAvatar()
    {
        Width = W;
        Height = H;
        Stretch = Stretch.Uniform;
        front.Width = W;
        front.Height = H;
        front.Stretch = Stretch.Uniform;
        front.RenderTransform = blink;
        RenderOptions.SetBitmapScalingMode(front, BitmapScalingMode.HighQuality);
        front.Source = Portrait;
        var art = new Grid
        {
            Width = W,
            Height = H,
            RenderTransform = new TransformGroup { Children = { drift, sway, breathe } },
            Children = { front, sparkles }
        };
        sparkles.IsHitTestVisible = false;
        Child = art;
        art.RenderTransform = new TransformGroup { Children = { drift, sway, breathe } };
        blinkTimer.Tick += (_, _) => { Blink(); blinkTimer.Interval = TimeSpan.FromSeconds(random.Next(3, 9)); };
        MouseLeftButtonUp += (_, _) => React(IvoAvatarState.Excited);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => { StateChanged -= OnStateChanged; instances.Remove(this); Stop(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else Stop(); };
        instances.Add(this);
        StateChanged += OnStateChanged;
    }

    public IvoAvatarState State
    {
        get => state;
        set => SetState(value);
    }

    public static void SetIvoState(IvoAvatarState state) => StateChanged?.Invoke(state);
    public static void SetIvoState(string state) => SetIvoState(state?.Trim().ToLowerInvariant() switch
    {
        "happy" or "excited" => IvoAvatarState.Excited,
        "celebrate" => IvoAvatarState.Celebrate,
        "thinking" or "listening" => IvoAvatarState.Thinking,
        "sleep" or "sleepy" => IvoAvatarState.Sleep,
        "loading" or "processing" => IvoAvatarState.Loading,
        "error" => IvoAvatarState.Error,
        _ => IvoAvatarState.Idle
    });
    void OnStateChanged(IvoAvatarState next) => SetState(next);

    public string Mood
    {
        get => happy ? "happy" : "";
        set
        {
            var now = value == "happy";
            if (now == happy) return;
            happy = now;
            front.BeginAnimation(OpacityProperty, new DoubleAnimation(happy ? 0.72 : 1, happy ? 1 : 0.72, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new SineEase()
            });
        }
    }

    static DoubleAnimation Loop(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

    public void Start()
    {
        if (!Alive || !IsVisible) return;
        if (ReducedMotion)
        {
            front.Opacity = .92;
            front.BeginAnimation(OpacityProperty, new DoubleAnimation(.92, 1, TimeSpan.FromSeconds(3))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            return;
        }
        drift.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1.035, 4.8));
        drift.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, 1.035, 4.8));
        breathe.BeginAnimation(TranslateTransform.YProperty, Loop(0, state == IvoAvatarState.Sleep ? -3 : -8, state == IvoAvatarState.Sleep ? 5 : 3));
        sway.BeginAnimation(RotateTransform.AngleProperty, Loop(-2, 2, state == IvoAvatarState.Sleep ? 5 : 3.4));
        blinkTimer.Interval = TimeSpan.FromSeconds(random.Next(3, 9));
        blinkTimer.Start();
    }

    public void Wave()
    {
        if (!Alive) return;
        Mood = "happy";
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new BounceEase { Bounces = 1 }));
        breathe.BeginAnimation(TranslateTransform.YProperty, hop);
        var after = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        after.Tick += (_, _) => { after.Stop(); Mood = ""; Start(); };
        after.Start();
    }

    public void Stop()
    {
        drift.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        drift.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        breathe.BeginAnimation(TranslateTransform.YProperty, null);
        sway.BeginAnimation(RotateTransform.AngleProperty, null);
        blinkTimer.Stop();
    }

    void SetState(IvoAvatarState next)
    {
        state = next;
        switch (next)
        {
            case IvoAvatarState.Thinking:
            case IvoAvatarState.Listening:
                sway.BeginAnimation(RotateTransform.AngleProperty, Loop(-3, 3, 2));
                break;
            case IvoAvatarState.Sleep:
                front.BeginAnimation(OpacityProperty, new DoubleAnimation(.58, TimeSpan.FromMilliseconds(400)));
                Start();
                break;
            case IvoAvatarState.Loading:
                front.BeginAnimation(OpacityProperty, new DoubleAnimation(.6, 1, TimeSpan.FromMilliseconds(500))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                break;
            case IvoAvatarState.Error:
                React(IvoAvatarState.Error);
                break;
            case IvoAvatarState.Celebrate:
                Celebrate();
                break;
            default:
                front.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
                break;
        }
    }

    void Blink()
    {
        if (state is IvoAvatarState.Sleep or IvoAvatarState.Loading) return;
        var close = new DoubleAnimationUsingKeyFrames();
        close.KeyFrames.Add(new LinearDoubleKeyFrame(.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        close.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), new SineEase()));
        blink.BeginAnimation(ScaleTransform.ScaleYProperty, close);
    }

    public void React(IvoAvatarState reaction)
    {
        SetState(reaction);
        if (reaction is IvoAvatarState.Error)
        {
            var shake = new DoubleAnimationUsingKeyFrames();
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            sway.BeginAnimation(RotateTransform.AngleProperty, shake);
            return;
        }
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new BounceEase { Bounces = 1 }));
        breathe.BeginAnimation(TranslateTransform.YProperty, hop);
        sparkleBurst();
    }

    public void Lean(double fromLeft, double fromRight)
    {
        if (ReducedMotion) return;
        var lean = fromRight > fromLeft ? 5 : fromLeft > fromRight ? -5 : 0;
        sway.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(lean, TimeSpan.FromMilliseconds(220)));
    }

    void Celebrate()
    {
        var hop = new DoubleAnimationUsingKeyFrames();
        for (var i = 0; i < 2; i++)
        {
            var start = i * 280;
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(start + 120)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(start + 260)), new BounceEase { Bounces = 1 }));
        }
        breathe.BeginAnimation(TranslateTransform.YProperty, hop);
        sparkleBurst();
    }

    void sparkleBurst()
    {
        sparkles.Children.Clear();
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White, Opacity = .9 };
            Canvas.SetLeft(dot, W / 2); Canvas.SetTop(dot, H / 2);
            sparkles.Children.Add(dot);
            var dx = new DoubleAnimation(0, Math.Cos(angle) * 44, TimeSpan.FromMilliseconds(550)) { EasingFunction = new QuadraticEase() };
            var dy = new DoubleAnimation(0, Math.Sin(angle) * 44, TimeSpan.FromMilliseconds(550)) { EasingFunction = new QuadraticEase() };
            var tx = new TranslateTransform(); dot.RenderTransform = tx;
            tx.BeginAnimation(TranslateTransform.XProperty, dx);
            tx.BeginAnimation(TranslateTransform.YProperty, dy);
        }
        var clear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        clear.Tick += (_, _) => { clear.Stop(); sparkles.Children.Clear(); };
        clear.Start();
    }
}
