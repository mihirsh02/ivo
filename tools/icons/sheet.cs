#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
// Renders every glyph of the icon font onto a PNG (off-screen), labelled with its codepoint.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var font = args[0]; var outFile = args[1];
var gt = new GlyphTypeface(new Uri(font));
var cps = gt.CharacterToGlyphMap.Keys.OrderBy(c => c).ToList();
int cols = 12, cell = 90, rows = (cps.Count + cols - 1) / cols;
var dv = new DrawingVisual();
using (var dc = dv.RenderOpen())
{
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 30)), null, new Rect(0, 0, cols * cell, rows * cell));
    for (int i = 0; i < cps.Count; i++)
    {
        var x = i % cols * cell; var y = i / cols * cell;
        var run = new GlyphRun(gt, 0, false, 40, 1, new[] { gt.CharacterToGlyphMap[cps[i]] }, new Point(x + 25, y + 52), new[] { 40.0 }, null, null, null, null, null, null);
        dc.DrawGlyphRun(Brushes.White, run);
        dc.DrawText(new FormattedText(cps[i].ToString("X4"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Gray, 1), new Point(x + 28, y + 68));
    }
}
var bmp = new RenderTargetBitmap(cols * cell, rows * cell, 96, 96, PixelFormats.Pbgra32);
bmp.Render(dv);
var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
using var fs = File.Create(outFile); enc.Save(fs);
Console.WriteLine($"{cps.Count} glyphs");
