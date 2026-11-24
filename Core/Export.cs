using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Markdig;

namespace Ivo;

// A chat saved as Markdown, a web page, or a PDF (Edge prints the web page; it ships with Windows).
public static class Export
{
    public static async Task Save(Chat c, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".md": await File.WriteAllTextAsync(path, Markdown(c)); break;
            case ".pdf": await Pdf(Html(c), path); break;
            default: await File.WriteAllTextAsync(path, Html(c)); break;
        }
    }

    public static string Markdown(Chat c)
    {
        var sb = new StringBuilder($"# {c.Title}\n\n_{When.Full(c.Updated)} · {c.Provider} · {c.Model}_\n\n");
        var results = c.Messages.Where(m => m.Role == "tool" && m.CallId != null).GroupBy(m => m.CallId!).ToDictionary(g => g.Key, g => g.Last().Text);
        foreach (var m in c.Messages.Where(m => m.Role != "tool"))
        {
            if (m.Role == "user")
            {
                sb.Append("### You\n\n").Append(m.Text);
                if (m.Images is { Count: > 0 } imgs) sb.Append($"\n\n_({imgs.Count} image{(imgs.Count == 1 ? "" : "s")} attached)_");
                sb.Append("\n\n");
                continue;
            }
            if (m.Text != "") sb.Append("### Ivo\n\n").Append(m.Text).Append("\n\n");
            foreach (var call in m.Calls ?? [])
            {
                var r = results.GetValueOrDefault(call.Id) ?? "";
                var how = r.StartsWith("Error") || r.StartsWith("Blocked") ? "failed" : r.StartsWith("The user denied") || r.StartsWith("Cancelled") ? "denied" : "done";
                sb.Append($"> {Agent.Describe(call).Label} · _{how}_\n\n");
            }
        }
        return sb.ToString();
    }

    static string Html(Chat c)
    {
        var body = Markdig.Markdown.ToHtml(Markdown(c), new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(c.Title)}}</title><style>
            body{font-family:"Segoe UI",system-ui,sans-serif;max-width:760px;margin:40px auto;padding:0 24px;color:#1c1c22;line-height:1.55}
            h1{font-size:24px;margin-bottom:4px}h3{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#7c5cff;margin:30px 0 6px}
            pre{background:#f4f4f8;border-radius:8px;padding:12px;white-space:pre-wrap;word-break:break-word}code{font-family:"Cascadia Code",Consolas,monospace;font-size:.9em}
            blockquote{margin:6px 0;padding:2px 12px;border-left:3px solid #c4b5fd;color:#555}table{border-collapse:collapse}td,th{border:1px solid #ddd;padding:4px 8px}
            img{max-width:100%}
            </style></head><body>{{body}}</body></html>
            """;
    }

    static async Task Pdf(string html, string path)
    {
        var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                       .Select(f => Path.Combine(Environment.GetFolderPath(f), @"Microsoft\Edge\Application\msedge.exe")).FirstOrDefault(File.Exists)
                   ?? throw new Exception("PDF export uses Microsoft Edge, which isn't installed. Export as a web page and print it to PDF instead.");
        var dir = Directory.CreateTempSubdirectory("ivo-export");
        try
        {
            var src = Path.Combine(dir.FullName, "chat.html");
            await File.WriteAllTextAsync(src, html);
            File.Delete(path);
            var psi = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--headless", "--disable-gpu", "--no-pdf-header-footer", "--user-data-dir=" + Path.Combine(dir.FullName, "profile"), "--print-to-pdf=" + path, new Uri(src).AbsoluteUri })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw new Exception("Edge took too long to make the PDF."); }
        }
        finally { try { dir.Delete(recursive: true); } catch { } }
        if (!File.Exists(path)) throw new Exception("Edge didn't produce the PDF.");
    }
}
