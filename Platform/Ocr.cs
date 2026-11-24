using System.IO;
using System.Windows.Media.Imaging;

namespace Ivo;

// Text from a picture, with the OCR engine built into Windows: on this PC, offline, in the languages Windows has installed.
public static class Ocr
{
    public static async Task<string> Read(BitmapSource img)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream(); enc.Save(ms); ms.Position = 0;
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var bmp = await decoder.GetSoftwareBitmapAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("Windows has no text recognition for your languages. Add one in Settings → Time & language → Language.");
        var result = await engine.RecognizeAsync(bmp);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }
}
