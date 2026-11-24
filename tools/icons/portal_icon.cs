#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
// Ivo Portal's app icon (Portal/portal.ico and portal.png): the same swirl as Portal's button in Ivo (the soft set's
// material-symbols:cyclone-rounded, from soft/mix.json), in the same violet (#A78BFA), on a clear background.
// Drawn from the original path, so it's smooth at every size. Run from tools\icons: dotnet run portal_icon.cs
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

var here = Directory.GetCurrentDirectory();
var root = Path.GetFullPath(Path.Combine(here, "..", ".."));
var body = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(here, "soft", "mix.json")))!["material-symbols:cyclone-rounded"]!["body"]!;
var d = Regex.Match(body, "d=\"([^\"]+)\"").Groups[1].Value;
var shape = Geometry.Parse(d);   // a 24-unit square
var violet = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));

byte[] Png(int px)
{
    var visual = new DrawingVisual();
    using (var g = visual.RenderOpen())
    {
        var k = px / 24.0;
        g.PushTransform(new ScaleTransform(k, k));
        g.DrawGeometry(violet, null, shape);
        g.Pop();
    }
    var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
}

// an .ico of PNG images, one per size
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = sizes.Select(Png).ToList();
using (var ico = new BinaryWriter(File.Create(Path.Combine(root, "Portal", "portal.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0); ico.Write((byte)0); ico.Write((short)1); ico.Write((short)32);
        ico.Write(images[i].Length); ico.Write(offset); offset += images[i].Length;
    }
    foreach (var img in images) ico.Write(img);
}
File.WriteAllBytes(Path.Combine(root, "Portal", "portal.png"), images[^1]);
Console.WriteLine("saved Portal/portal.ico and portal.png");
