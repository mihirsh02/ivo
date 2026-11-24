#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false
// Reads mix.json (Iconify bodies), turns every stroke into a filled outline with WPF's geometry engine,
// unions everything per icon and writes outlines.json: name -> SVG path data in a 1000-unit em (y down).
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

var dir = args[0];
var src = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "mix.json")))!.AsObject();
var result = new JsonObject();
var inv = CultureInfo.InvariantCulture;
double D(string? s, double def = 0) => s == null ? def : double.Parse(s.Replace("px", ""), inv);

foreach (var (name, node) in src)
{
    var w = (double)node!["w"]!; var h = (double)node["h"]!; var left = (double)node["left"]!; var top = (double)node["top"]!;
    var xml = XElement.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\">{node["body"]}</svg>");
    var parts = new List<Geometry>();
    void Walk(XElement e, Dictionary<string, string> inherited)
    {
        var a = new Dictionary<string, string>(inherited);
        foreach (var at in e.Attributes()) a[at.Name.LocalName] = at.Value;
        if (a.TryGetValue("opacity", out var op) && D(op) < 0.5) return;   // duotone backgrounds would fill solid in a font
        if (a.TryGetValue("fill-opacity", out var fo) && D(fo) < 0.5) return;
        Geometry? g = e.Name.LocalName switch
        {
            "path" => Geometry.Parse((a.GetValueOrDefault("fill-rule") == "evenodd" ? "F0 " : "F1 ") + a["d"]),
            "circle" => new EllipseGeometry(new Point(D(a.GetValueOrDefault("cx")), D(a.GetValueOrDefault("cy"))), D(a["r"]), D(a["r"])),
            "ellipse" => new EllipseGeometry(new Point(D(a.GetValueOrDefault("cx")), D(a.GetValueOrDefault("cy"))), D(a["rx"]), D(a["ry"])),
            "rect" => new RectangleGeometry(new Rect(D(a.GetValueOrDefault("x")), D(a.GetValueOrDefault("y")), D(a["width"]), D(a["height"])), D(a.GetValueOrDefault("rx"), D(a.GetValueOrDefault("ry"))), D(a.GetValueOrDefault("ry"), D(a.GetValueOrDefault("rx")))),
            "line" => new LineGeometry(new Point(D(a.GetValueOrDefault("x1")), D(a.GetValueOrDefault("y1"))), new Point(D(a.GetValueOrDefault("x2")), D(a.GetValueOrDefault("y2")))),
            "polyline" or "polygon" => Geometry.Parse("M" + a["points"] + (e.Name.LocalName == "polygon" ? "Z" : "")),
            _ => null,
        };
        if (g != null)
        {
            if (a.TryGetValue("transform", out var tf)) g.Transform = ParseTransform(tf);
            var fill = a.GetValueOrDefault("fill", "currentColor");
            var stroke = a.GetValueOrDefault("stroke", "none");
            if (fill != "none" && e.Name.LocalName is not "line" and not "polyline") parts.Add(g.GetOutlinedPathGeometry());
            if (stroke != "none")
            {
                var cap = a.GetValueOrDefault("stroke-linecap") switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat };
                var join = a.GetValueOrDefault("stroke-linejoin") switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter };
                var pen = new Pen(Brushes.Black, D(a.GetValueOrDefault("stroke-width"), 1)) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
                parts.Add(g.GetWidenedPathGeometry(pen).GetOutlinedPathGeometry());
            }
        }
        foreach (var c in e.Elements()) Walk(c, a);
    }
    Walk(xml, new());
    Geometry all = Geometry.Empty;
    foreach (var p in parts) all = Geometry.Combine(all, p, GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);
    // embolden: these sets use ~1.5/24 lines, under a pixel at the app's 12-13px icon size (blurry); grow every edge by
    // old grid units so lines land near 1px, like Segoe Fluent's
    var bold = Math.Max(w, h) / 24 * 0.26;
    all = Geometry.Combine(all, all.GetWidenedPathGeometry(new Pen(Brushes.Black, bold) { LineJoin = PenLineJoin.Round }), GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);
    // to font units: 1000 em. The sets' own grids carry ~10% padding, so the grid spans 1150 to match Segoe Fluent's ink size.
    var s = 1150 / Math.Max(w, h) * (name.StartsWith("streamline") ? 0.86 : 1);   // Streamline's grid has less padding
    var tg = new TransformGroup();
    tg.Children.Add(new TranslateTransform(-left - w / 2, -top - h / 2));
    tg.Children.Add(new ScaleTransform(s, s));
    tg.Children.Add(new TranslateTransform(500, 500));
    var path = PathGeometry.CreateFromGeometry(all);
    path.Transform = tg;
    var flat = PathGeometry.CreateFromGeometry(path.GetOutlinedPathGeometry(0.05, ToleranceType.Absolute));
    result[name] = ToSvg(flat);
}
File.WriteAllText(Path.Combine(dir, "outlines.json"), result.ToJsonString());
Console.WriteLine($"outlined {result.Count}");

Transform ParseTransform(string t)
{
    var g = new TransformGroup();
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"(\w+)\(([^)]*)\)"))
    {
        var n = m.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, inv)).ToArray();
        g.Children.Add(m.Groups[1].Value switch
        {
            "translate" => new TranslateTransform(n[0], n.Length > 1 ? n[1] : 0),
            "scale" => new ScaleTransform(n[0], n.Length > 1 ? n[1] : n[0]),
            "rotate" => n.Length == 3 ? new RotateTransform(n[0], n[1], n[2]) : new RotateTransform(n[0]),
            "matrix" => new MatrixTransform(n[0], n[1], n[2], n[3], n[4], n[5]),
            _ => Transform.Identity,
        });
    }
    return g;
}

string ToSvg(PathGeometry g)
{
    var sb = new System.Text.StringBuilder();
    string P(Point p) => $"{p.X.ToString("0.##", inv)} {p.Y.ToString("0.##", inv)} ";
    foreach (var f in g.Figures)
    {
        sb.Append("M").Append(P(f.StartPoint));
        foreach (var seg in f.Segments)
            switch (seg)
            {
                case LineSegment l: sb.Append("L").Append(P(l.Point)); break;
                case PolyLineSegment pl: foreach (var p in pl.Points) sb.Append("L").Append(P(p)); break;
                case BezierSegment b: sb.Append("C").Append(P(b.Point1)).Append(P(b.Point2)).Append(P(b.Point3)); break;
                case PolyBezierSegment pb: for (int i = 0; i + 2 < pb.Points.Count; i += 3) sb.Append("C").Append(P(pb.Points[i])).Append(P(pb.Points[i + 1])).Append(P(pb.Points[i + 2])); break;
                case QuadraticBezierSegment q: sb.Append("Q").Append(P(q.Point1)).Append(P(q.Point2)); break;
                case PolyQuadraticBezierSegment pq: for (int i = 0; i + 1 < pq.Points.Count; i += 2) sb.Append("Q").Append(P(pq.Points[i])).Append(P(pq.Points[i + 1])); break;
                case ArcSegment: throw new Exception("arc survived outlining");
            }
        if (f.IsClosed) sb.Append("Z");
    }
    return sb.ToString();
}






