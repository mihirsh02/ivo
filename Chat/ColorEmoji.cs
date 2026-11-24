using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ivo;

// Reads the color layers in the Segoe UI Emoji font already installed by Windows.
static class ColorEmoji
{
    sealed class Font
    {
        public required byte[] B;
        public required int Colr;
        public required GlyphTypeface Face;
        public required double Em;
        public required Color[] Palette;
        public required string Path;
        public Dictionary<ushort, int> V1 = [];
        public int LayerList;
        public Dictionary<ushort, (int First, int Count)> V0 = [];
        public (ushort Glyph, ushort Color)[] V0Layers = [];
    }

    public static FontFamily EmojiFontFamily { get; } = new(SystemEmojiFamily());
    internal static string? SourcePath => font.Value?.Path;
    static readonly Lazy<Font?> font = new(Load);
    static readonly Dictionary<string, ImageSource?> cache = [];

    static string SystemEmojiFamily()
    {
        const string family = "Segoe UI Emoji";
        const string substitutes = @"Software\Microsoft\Windows NT\CurrentVersion\FontSubstitutes";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(substitutes);
                if (key?.GetValue(family) is string replacement && !string.IsNullOrWhiteSpace(replacement))
                    return replacement.Trim();
            }
            catch (System.Security.SecurityException) { }
            catch (IOException) { }
        }
        return family;
    }

    static string? RegisteredFontPath(string family)
    {
        const string fonts = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(fonts);
                if (key == null) continue;
                foreach (var name in key.GetValueNames()
                    .Where(n => n.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.Contains("Bold", StringComparison.OrdinalIgnoreCase)))
                {
                    if (key.GetValue(name) is not string value || string.IsNullOrWhiteSpace(value)) continue;
                    var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (!System.IO.Path.IsPathRooted(path))
                        path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), path);
                    if (File.Exists(path)) return System.IO.Path.GetFullPath(path);
                }
            }
            catch (System.Security.SecurityException) { }
            catch (IOException) { }
        }
        return null;
    }

    static string? ResolveFontPath()
    {
        if (RegisteredFontPath(EmojiFontFamily.Source) is { } registered) return registered;
        var typeface = new Typeface(EmojiFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (typeface.TryGetGlyphTypeface(out var glyph) && glyph.FontUri.IsFile && File.Exists(glyph.FontUri.LocalPath))
            return glyph.FontUri.LocalPath;
        var fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        return File.Exists(fallback) ? fallback : null;
    }

    static ushort U16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
    static short S16(byte[] b, int o) => (short)U16(b, o);
    static int U24(byte[] b, int o) => b[o] << 16 | b[o + 1] << 8 | b[o + 2];
    static int U32(byte[] b, int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
    static double F2Dot14(byte[] b, int o) => S16(b, o) / 16384.0;
    static double Fixed(byte[] b, int o) => U32(b, o) / 65536.0;

    static Font? Load()
    {
        try
        {
            var path = ResolveFontPath();
            if (path == null) return null;
            var b = File.ReadAllBytes(path);
            int colr = -1, cpal = -1, head = -1;
            for (int i = 0, n = U16(b, 4); i < n; i++)
            {
                var rec = 12 + 16 * i;
                switch (Encoding.ASCII.GetString(b, rec, 4)) { case "COLR": colr = U32(b, rec + 8); break; case "CPAL": cpal = U32(b, rec + 8); break; case "head": head = U32(b, rec + 8); break; }
            }
            if (colr < 0 || cpal < 0 || head < 0) return null;
            int entries = U16(b, cpal + 2), records = cpal + U32(b, cpal + 8), first = U16(b, cpal + 12);
            var palette = new Color[entries];
            for (var i = 0; i < entries; i++) { var o = records + 4 * (first + i); palette[i] = Color.FromArgb(b[o + 3], b[o + 2], b[o + 1], b[o]); }
            var f = new Font { B = b, Colr = colr, Face = new GlyphTypeface(new Uri(path)), Em = U16(b, head + 18), Palette = palette, Path = path };

            int bases = colr + U32(b, colr + 4), layers = colr + U32(b, colr + 8);
            for (int i = 0, n = U16(b, colr + 2); i < n; i++) f.V0[U16(b, bases + 6 * i)] = (U16(b, bases + 6 * i + 2), U16(b, bases + 6 * i + 4));
            f.V0Layers = new (ushort, ushort)[U16(b, colr + 12)];
            for (var i = 0; i < f.V0Layers.Length; i++) f.V0Layers[i] = (U16(b, layers + 4 * i), U16(b, layers + 4 * i + 2));
            if (U16(b, colr) >= 1)
            {
                var list = colr + U32(b, colr + 14);
                for (int i = 0, n = U32(b, list); i < n; i++) f.V1[U16(b, list + 4 + 6 * i)] = list + U32(b, list + 4 + 6 * i + 2);
                f.LayerList = U32(b, colr + 18) is var ll and > 0 ? colr + ll : 0;
            }
            return f;
        }
        catch { return null; }
    }

    public static IReadOnlyList<string> All => all.Value;
    static readonly Lazy<IReadOnlyList<string>> all = new(() =>
        font.Value is not { } f ? [] : f.Face.CharacterToGlyphMap
            .Where(x => (f.V1.ContainsKey(x.Value) || f.V0.ContainsKey(x.Value)) && (x.Key >= 0x1F000 || x.Key is >= 0x2000 and < 0x3300)
                        && x.Key is not (>= 0x1F1E6 and <= 0x1F1FF) and not (>= 0x1F3FB and <= 0x1F3FF) and not (>= 0xE0000))
            .OrderBy(x => x.Key).Select(x => char.ConvertFromUtf32(x.Key)).ToList());

    public static ImageSource? Image(string emoji)
    {
        var key = emoji.Replace("️", "");
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;
        var img = Build(key);
        lock (cache) cache[key] = img;
        return img;
    }

    static ImageSource? Build(string key)
    {
        ImageSource? img = null;
        if (font.Value is { } f && key.EnumerateRunes().ToList() is [var r] && (r.Value >= 0x1F000 || Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol)
            && f.Face.CharacterToGlyphMap.TryGetValue(r.Value, out var g) && (f.V1.ContainsKey(g) || f.V0.ContainsKey(g)))
        {
            var art = new DrawingGroup();
            art.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, -f.Face.Baseline * f.Em, f.Face.AdvanceWidths[g] * f.Em, f.Face.Height * f.Em))));
            try { art.Children.Add(f.V1.TryGetValue(g, out var paint) ? Draw(f, paint, 0) : Flat(f, g)); }
            catch { art.Children.Add(Flat(f, g)); }
            img = new DrawingImage(art);
            img.Freeze();
        }
        return img;
    }

    static readonly Dictionary<string, ImageSource> pictures = [];
    public static ImageSource? Picture(string emoji) { lock (pictures) return pictures.GetValueOrDefault(emoji.Replace("️", "")); }

    public static void Warm()
    {
        var t = new Thread(() =>
        {
            foreach (var e in All)
                try
                {
                    if (Build(e.Replace("️", "")) is not { } art) continue;
                    var v = new DrawingVisual();
                    using (var dc = v.RenderOpen()) dc.DrawImage(art, new Rect(0, 0, 48, 48));
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(v); bmp.Freeze();
                    lock (pictures) pictures[e.Replace("️", "")] = bmp;
                }
                catch { }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static Drawing Flat(Font f, ushort g)
    {
        var d = new DrawingGroup();
        if (!f.V0.TryGetValue(g, out var layers)) return d;
        for (var i = layers.First; i < layers.First + layers.Count; i++)
        {
            var (glyph, color) = f.V0Layers[i];
            d.Children.Add(new GeometryDrawing(Solid(f, color, 1), null, Outline(f, glyph)));
        }
        return d;
    }

    static Geometry Outline(Font f, ushort glyph) => f.Face.GetGlyphOutline(glyph, f.Em, f.Em);
    static Brush Solid(Font f, ushort index, double alpha) => new SolidColorBrush(Tint(f, index, alpha));
    static Color Tint(Font f, ushort index, double alpha)
    {
        var c = index < f.Palette.Length ? f.Palette[index] : Colors.Gray;
        return Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);
    }

    static Matrix Affine(double xx, double yx, double xy, double yy, double dx, double dy) => new(xx, -yx, -xy, yy, dx, -dy);

    static Matrix? TransformOf(Font f, int p)
    {
        var b = f.B;
        switch (b[p])
        {
            case 12: { var t = p + U24(b, p + 4); return Affine(Fixed(b, t), Fixed(b, t + 4), Fixed(b, t + 8), Fixed(b, t + 12), Fixed(b, t + 16), Fixed(b, t + 20)); }
            case 14: return Affine(1, 0, 0, 1, S16(b, p + 4), S16(b, p + 6));
            case 16: return new Matrix(F2Dot14(b, p + 4), 0, 0, F2Dot14(b, p + 6), 0, 0);
            case 18:
            {
                double sx = F2Dot14(b, p + 4), sy = F2Dot14(b, p + 6), cx = S16(b, p + 8), cy = -S16(b, p + 10);
                return new Matrix(sx, 0, 0, sy, cx - sx * cx, cy - sy * cy);
            }
            default: return null;
        }
    }

    static Drawing Draw(Font f, int p, int depth)
    {
        if (depth > 32) return new DrawingGroup();
        var b = f.B;
        switch (b[p])
        {
            case 1:
            {
                var g = new DrawingGroup();
                int count = b[p + 1], first = U32(b, p + 2);
                for (var i = 0; i < count; i++) g.Children.Add(Draw(f, f.LayerList + U32(b, f.LayerList + 4 + 4 * (first + i)), depth + 1));
                return g;
            }
            case 10: return new GeometryDrawing(Fill(f, p + U24(b, p + 1), depth + 1), null, Outline(f, U16(b, p + 4)));
            case 11: return f.V1.TryGetValue(U16(b, p + 1), out var other) ? Draw(f, other, depth + 1) : new DrawingGroup();
            case 12 or 14 or 16 or 18: return new DrawingGroup { Transform = new MatrixTransform(TransformOf(f, p)!.Value), Children = { Draw(f, p + U24(b, p + 1), depth + 1) } };
            case 32: return new DrawingGroup { Children = { Draw(f, p + U24(b, p + 5), depth + 1), Draw(f, p + U24(b, p + 1), depth + 1) } };
            default: return new DrawingGroup();
        }
    }

    static Brush Fill(Font f, int p, int depth)
    {
        var b = f.B;
        switch (b[p])
        {
            case 2: return Solid(f, U16(b, p + 1), F2Dot14(b, p + 3));
            case 4:
            {
                var stops = Stops(f, p + U24(b, p + 1));
                Point p0 = new(S16(b, p + 4), -S16(b, p + 6)), p1 = new(S16(b, p + 8), -S16(b, p + 10)), p2 = new(S16(b, p + 12), -S16(b, p + 14));
                var d = p2 - p0; var n = new Vector(d.Y, -d.X);
                var end = n.LengthSquared < 1e-9 ? p1 : p0 + n * (Vector.Multiply(p1 - p0, n) / n.LengthSquared);
                return new LinearGradientBrush(stops) { MappingMode = BrushMappingMode.Absolute, StartPoint = p0, EndPoint = end, SpreadMethod = GradientSpreadMethod.Pad };
            }
            case 6:
            {
                var stops = Stops(f, p + U24(b, p + 1));
                Point c0 = new(S16(b, p + 4), -S16(b, p + 6)), c1 = new(S16(b, p + 10), -S16(b, p + 12));
                double r0 = U16(b, p + 8), r1 = Math.Max(U16(b, p + 14), 1e-3);
                if (r0 > 0) foreach (var s in stops) s.Offset = (r0 + s.Offset * (r1 - r0)) / r1;
                return new RadialGradientBrush(stops) { MappingMode = BrushMappingMode.Absolute, Center = c1, GradientOrigin = c0, RadiusX = r1, RadiusY = r1, SpreadMethod = GradientSpreadMethod.Pad };
            }
            case 12 or 14 or 16 or 18:
            {
                var inner = Fill(f, p + U24(b, p + 1), depth + 1).Clone();
                var m = inner.Transform?.Value ?? Matrix.Identity;
                m.Append(TransformOf(f, p)!.Value);
                inner.Transform = new MatrixTransform(m);
                return inner;
            }
            default: return Brushes.Transparent;
        }
    }

    static GradientStopCollection Stops(Font f, int line)
    {
        var b = f.B;
        var stops = new GradientStopCollection();
        for (int i = 0, n = U16(b, line + 1); i < n; i++)
        {
            var s = line + 3 + 6 * i;
            stops.Add(new GradientStop(Tint(f, U16(b, s + 2), F2Dot14(b, s + 4)), F2Dot14(b, s)));
        }
        return stops;
    }

    public static void Append(InlineCollection into, string text, double size)
    {
        var plain = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var el = e.GetTextElement();
            if (Image(el) is not { } img) { plain.Append(el); continue; }
            if (plain.Length > 0) { into.Add(new Run(plain.ToString())); plain.Clear(); }
            into.Add(new InlineUIContainer(new System.Windows.Controls.Image { Source = img, Width = size * 1.25, Height = size * 1.25 }) { BaselineAlignment = BaselineAlignment.Center });
        }
        if (plain.Length > 0) into.Add(new Run(plain.ToString()));
    }
}
