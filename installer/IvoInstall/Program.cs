using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace IvoInstall;

// Ivo's own installer window: Ivo's look, no Inno or MSI. The launcher (installer\IvoLauncher, the .exe you download)
// unpacks the app and this window into one folder, sharing a .NET runtime, and starts it from there. The same window
// installs Ivo or Ivo Portal, whichever it was built for (IvoInstall.csproj, -p:App=Portal).
//   IvoInstaller.exe                   install (or update): copies the app from this folder into Program Files
//   "Uninstall <app>.exe" --uninstall  remove, asking whether to delete your data too
//   ... --uninstall --quiet            remove without asking, keeping your data (what the other installers call)
// Like the .exe and .msi, it installs to Program Files and replaces either of them first, so there's only one Ivo.
public static class Program
{
    // which app this installs (Ivo or Ivo Portal) and its words and IDs: set by IvoInstall.csproj, read from this assembly
    public static readonly string AppName = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyProductAttribute), false).Cast<System.Reflection.AssemblyProductAttribute>().First().Product;
    public static string Meta(string key) => typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;
    static readonly string InnoKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Meta("InnoAppId")}_is1";
    static readonly string OwnKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}";
    static readonly string MsiUpgradeCode = Meta("MsiUpgradeCode");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // this window's own files, next to Ivo's in the unpacked folder: they become the uninstaller, not part of Ivo
    static readonly string[] OwnFiles = ["IvoInstaller.exe", "IvoInstaller.dll", "IvoInstaller.deps.json", "IvoInstaller.runtimeconfig.json", "IvoInstaller.pdb", ".unpacked"];
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    static string StartMenuLink => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), $"{AppName}.lnk");
    static string DesktopLink => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");
    static string DataDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);


    [STAThread]
    public static int Main(string[] args)
    {
        // Program Files and the machine's app list need administrator rights: ask once (the launcher already has; Settings →
        // Apps may start the uninstaller without them)
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", args)) { Verb = "runas", UseShellExecute = true }); } catch { }   // declined: nothing happens
            return 0;
        }
        var uninstall = args.Contains("--uninstall");
        if (uninstall && args.Contains("--quiet")) { Uninstall(InstalledDir() ?? AppContext.BaseDirectory, deleteData: false); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new SetupWindow(uninstall));
        return 0;
    }

    public static string? InstalledDir() => Registry.LocalMachine.OpenSubKey(OwnKey)?.GetValue("InstallLocation") as string;
    public static string DefaultDir => InstalledDir() ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    // ---------- install ----------
    public static void Install(string dir, bool desktop, bool startup, Action<string, double> progress)
    {
        progress($"Closing {AppName}…", 0.02);
        StopIvo();
        RemoveOtherInstalls(progress);
        // just installed: on its next start the app asks whether to keep an account it finds from before (AccountChoice)
        Registry.CurrentUser.CreateSubKey($@"Software\Ivo\{AppName}")?.SetValue("JustInstalled", 1);
        var from = AppContext.BaseDirectory;
        if (!File.Exists(System.IO.Path.Combine(from, $"{AppName}.exe"))) throw new InvalidOperationException($"{AppName}'s files aren't next to the installer. Run the setup .exe you downloaded.");
        progress("Clearing the old version…", 0.1);
        if (Directory.Exists(dir)) foreach (var f in Directory.GetFileSystemEntries(dir)) { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); }
        Directory.CreateDirectory(dir);
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
        long size = 0;
        for (int i = 0; i < files.Length; i++)
        {
            var rel = System.IO.Path.GetRelativePath(from, files[i]);
            if (OwnFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            var to = System.IO.Path.Combine(dir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Copy(files[i], to, true);
            size += new FileInfo(to).Length;
            if (i % 25 == 0) progress($"Copying {AppName}… {i * 100 / files.Length}%", 0.12 + 0.76 * i / files.Length);
        }
        progress("Adding shortcuts…", 0.9);
        var exe = System.IO.Path.Combine(dir, $"{AppName}.exe");
        // the uninstaller: this window again, running on Ivo's runtime in the same folder
        var uninstaller = System.IO.Path.Combine(dir, $"Uninstall {AppName}.exe");
        File.Copy(System.IO.Path.Combine(from, "IvoInstaller.exe"), uninstaller, true);
        foreach (var f in new[] { "IvoInstaller.dll", "IvoInstaller.deps.json", "IvoInstaller.runtimeconfig.json" })
            File.Copy(System.IO.Path.Combine(from, f), System.IO.Path.Combine(dir, f), true);
        Shortcut(StartMenuLink, exe, dir);
        if (desktop) Shortcut(DesktopLink, exe, dir); else File.Delete(DesktopLink);
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))   // the same value as Settings → General → Start with Windows
            if (startup) run.SetValue(AppName, $"\"{exe}\" --tray"); else run.DeleteValue(AppName, false);
        progress($"Registering {AppName} with Windows…", 0.96);
        using (var key = Registry.LocalMachine.CreateSubKey(OwnKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "Ivo");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
            key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        progress("Done", 1);
    }

    // Opens Ivo as you, not as administrator (Explorer starts it from your normal desktop session).
    public static void OpenIvo(string dir) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{System.IO.Path.Combine(dir, AppName + ".exe")}\"") { UseShellExecute = false });

    // ---------- uninstall ----------
    public static void Uninstall(string dir, bool deleteData)
    {
        StopIvo();
        File.Delete(StartMenuLink);
        File.Delete(DesktopLink);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue(AppName, false);
        Registry.LocalMachine.DeleteSubKeyTree(OwnKey, false);
        // only a folder that's Ivo's; it holds this running uninstaller, so it's removed a moment after this exits
        if (Directory.Exists(dir) && File.Exists(System.IO.Path.Combine(dir, $"{AppName}.exe")))
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{dir.Replace("'", "''")}' -Recurse -Force\"") { CreateNoWindow = true, UseShellExecute = false });
        if (deleteData && Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
    }

    static void StopIvo()
    {
        foreach (var p in Process.GetProcessesByName(AppName)) try { p.Kill(true); p.WaitForExit(5000); } catch { }
    }

    // The .exe (Inno) and the .msi install the same Ivo: theirs go first, with no window of theirs showing. Inno's
    // "quiet" mode still shows its progress window, so its uninstaller runs fully hidden (/VERYSILENT); the .msi's
    // removal is /qn. Neither touches your data: Inno only asks about it in a manual uninstall.
    // which other installer has Ivo on this PC right now (shown before installing), or null
    public static string? OtherInstall() =>
        Registry.LocalMachine.OpenSubKey(InnoKey) != null || Registry.CurrentUser.OpenSubKey(InnoKey) != null ? $"the classic setup ({Meta("SetupName")} .exe)"
        : MsiEnumRelatedProducts(MsiUpgradeCode, 0, 0, new System.Text.StringBuilder(39)) == 0 ? "the .msi installer"
        : null;

    static void RemoveOtherInstalls(Action<string, double> progress)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            if (root.OpenSubKey(InnoKey)?.GetValue("UninstallString") is string cmd)
            {
                var exe = cmd.Trim().Trim('"');
                progress($"Removing the {AppName} installed by the classic setup (your chats and settings stay)…", 0.05);
                if (!File.Exists(exe))
                {
                    // endpoint protection removed its uninstaller: drop the dead Apps entry, this install replaces the files
                    root.DeleteSubKeyTree(InnoKey, false);
                    continue;
                }
                using var uninstall = Process.Start(new ProcessStartInfo(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { CreateNoWindow = true, UseShellExecute = false })
                    ?? throw new InvalidOperationException($"Couldn't start the existing {AppName} uninstaller ({exe}).");
                if (!uninstall.WaitForExit(120_000))
                    throw new InvalidOperationException($"The existing {AppName} uninstaller did not finish in time ({exe}). Close its window if one is open, then try again.");
                if (uninstall.ExitCode != 0)
                    throw new InvalidOperationException($"The existing {AppName} uninstaller failed (error {uninstall.ExitCode}). Ask your IT team to check its endpoint-protection history, then try again.");
            }
        var code = new System.Text.StringBuilder(39);
        while (MsiEnumRelatedProducts(MsiUpgradeCode, 0, 0, code) == 0)
        {
            progress($"Removing the {AppName} installed by the .msi installer (your chats and settings stay)…", 0.08);
            var p = Process.Start(new ProcessStartInfo("msiexec.exe", $"/x {code} /qn /norestart") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(180_000);
            if (p?.ExitCode is not 0) break;
        }
    }

    static void Shortcut(string link, string target, string dir)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic lnk = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [link])!;
        lnk.TargetPath = target; lnk.WorkingDirectory = dir; lnk.IconLocation = target + ",0"; lnk.Description = Meta("Blurb");
        lnk.Save();
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, System.Text.StringBuilder productCode);
}

// The window: Ivo's dark canvas, the crystal with its glow, and one card that changes from choices to progress to done.
public class SetupWindow : Window
{
    static readonly Brush Canvas = Hex("#0B0F12"), Card = Hex("#151C22"), Raised = Hex("#222C36"), Fg = Hex("#F8F9FA"), Dim = Hex("#A6ACB1"), Line = Hex("#2C3843");
    static readonly Brush Accent = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8579FF"), (Color)ColorConverter.ConvertFromString("#5A48F5"), 0);
    static SolidColorBrush Hex(string h) => new((Color)ColorConverter.ConvertFromString(h));

    readonly bool uninstall;
    readonly ContentControl page = new();
    string dir = Program.DefaultDir;

    public SetupWindow(bool uninstall)
    {
        this.uninstall = uninstall;
        Title = uninstall ? $"Uninstall {Program.AppName}" : $"Install {Program.AppName}";
        Width = 640; MinHeight = 460; SizeToContent = SizeToContent.Height;   // grows to fit the page: nothing is ever cut off or scrolled
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);   // smooth, true-shape text (Display mode bends letters onto whole pixels)

        // left: the crystal on a violet / cyan glow; right: the page
        var art = new Grid { Width = 230, ClipToBounds = true };
        art.Children.Add(new Rectangle { Fill = new RadialGradientBrush(Color.FromArgb(0xAA, 0x7C, 0x5C, 0xFF), Colors.Transparent) { Center = new Point(0.3, 0.25), GradientOrigin = new Point(0.3, 0.25), RadiusX = 0.9, RadiusY = 0.6 } });
        art.Children.Add(new Rectangle { Fill = new RadialGradientBrush(Color.FromArgb(0x66, 0x22, 0xD3, 0xEE), Colors.Transparent) { Center = new Point(0.8, 0.85), GradientOrigin = new Point(0.8, 0.85), RadiusX = 0.8, RadiusY = 0.5 } });
        var crystal = Crystal(70);
        crystal.VerticalAlignment = VerticalAlignment.Center; crystal.HorizontalAlignment = HorizontalAlignment.Center; crystal.Margin = new Thickness(0, 0, 0, 40);
        art.Children.Add(crystal);
        var name = new TextBlock { Text = Program.AppName, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 120, 0, 0) };
        art.Children.Add(name);
        art.Children.Add(new TextBlock { Text = $"Version {Program.Version}", FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 176, 0, 0) });
        // a gentle float, like the orb in the app
        var float_ = new TranslateTransform(); crystal.RenderTransform = float_;
        float_.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 4, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 42, 20, 0), Template = Bare(), Foreground = Dim, Cursor = Cursors.Hand };
        close.Click += (_, _) => Close();
        var right = new Grid { Children = { new Border { Child = page, Padding = new Thickness(30, 34, 30, 26) }, close } };
        var body = new DockPanel { Children = { art, right } };
        DockPanel.SetDock(art, Dock.Left);
        var inner = new Border { Child = body };
        // ClipToBounds is square; this clip follows the rounded corners, so the art can't poke out
        inner.SizeChanged += (_, e) => inner.Clip = new RectangleGeometry(new Rect(e.NewSize), 17, 17);
        Content = new Border { Background = Canvas, CornerRadius = new CornerRadius(18), Child = inner, BorderBrush = Line, BorderThickness = new Thickness(1) };
        // opened by the launcher's card: come to the front, not behind whatever had focus before
        ContentRendered += (_, _) => { Topmost = true; Activate(); Topmost = false; };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Show(uninstall ? UninstallChoices() : InstallChoices());
    }

    void Show(FrameworkElement p)
    {
        page.Content = p;
        p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        p.RenderTransform = new TranslateTransform(0, 8);
        p.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- pages ----------
    FrameworkElement InstallChoices()
    {
        var update = Program.InstalledDir() != null || Program.OtherInstall() != null;   // any install of it here: this updates it
        var col = new StackPanel();
        col.Children.Add(Heading(update ? $"Update {Program.AppName}" : $"Install {Program.AppName}"));
        col.Children.Add(Text(update ? $"This puts the newest {Program.AppName} in place. Your chats and settings stay as they are." : Program.Meta("Blurb"), 13.5, Dim, 0, 18));
        if (Program.OtherInstall() is { } other)   // said up front: that install is removed first
        {
            var note = new Border { Background = Raised, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, -6, 0, 14),
                                    Child = new TextBlock { Text = $"{Program.AppName} is already installed with {other}. It's removed first, then this one goes in. Your chats and settings stay.", FontSize = 12.5, Foreground = Hex("#FBBF24"), TextWrapping = TextWrapping.Wrap, LineHeight = 18 } };
            col.Children.Add(note);
        }
        var where = Text(dir, 12.5, Fg, 0, 0); where.TextTrimming = TextTrimming.CharacterEllipsis;
        var change = LinkButton("Change");
        change.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = $"Where should {Program.AppName} live?", InitialDirectory = System.IO.Path.GetDirectoryName(dir) };
            if (d.ShowDialog(this) == true) { dir = System.IO.Path.GetFileName(d.FolderName).Equals(Program.AppName, StringComparison.OrdinalIgnoreCase) ? d.FolderName : System.IO.Path.Combine(d.FolderName, Program.AppName); where.Text = dir; }
        };
        var loc = new DockPanel { Children = { change, where } };
        DockPanel.SetDock(change, Dock.Right);
        col.Children.Add(Text("FOLDER", 10.5, Dim, 0, 4));
        col.Children.Add(new Border { Background = Raised, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 8, 9), Child = loc, Margin = new Thickness(0, 0, 0, 16) });
        var desktop = Check("Desktop shortcut", !update || File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Program.AppName}.lnk")));
        var startup = Check("Start with Windows (it waits quietly in the tray)", Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue(Program.AppName) != null);
        col.Children.Add(desktop); col.Children.Add(startup);
        var go = Primary(update ? "Update" : "Install");
        go.Click += (_, _) => Run(desktop.IsChecked == true, startup.IsChecked == true, update);
        var cancel = Ghost("Cancel"); cancel.Click += (_, _) => Close();
        col.Children.Add(Buttons(cancel, go));
        return col;
    }

    async void Run(bool desktop, bool startup, bool update)
    {
        var status = Text("Getting ready…", 13, Dim, 0, 14);
        var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Raised, Child = bar };
        Show(new StackPanel { Children = { Heading(update ? $"Updating {Program.AppName}" : $"Installing {Program.AppName}"), Text("This takes a moment.", 13.5, Dim, 0, 26), track, status } });
        var width = 0.0;
        track.SizeChanged += (_, _) => width = track.ActualWidth;
        void Progress(string what, double f) => Dispatcher.Invoke(() =>
        {
            status.Text = what;
            bar.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(6, (width > 0 ? width : 330) * f), TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        });
        try
        {
            await Task.Run(() => Program.Install(dir, desktop, startup, Progress));
            await Task.Delay(300);
            Show(Done(update));
        }
        catch (Exception ex) { Show(Failed(ex.Message)); }
    }

    FrameworkElement Done(bool update)
    {
        var open = Primary($"Open {Program.AppName}");
        open.Click += (_, _) => { Program.OpenIvo(dir); Close(); };
        var close = Ghost("Close"); close.Click += (_, _) => Close();
        return new StackPanel
        {
            Children =
            {
                Heading(update ? $"{Program.AppName} is updated" : $"{Program.AppName} is ready"),
                Text(Program.Meta("ReadyText"), 13.5, Dim, 0, 18),
                Text("Uninstall it any time from Settings → Apps.", 12, Dim, 0, 0),
                Buttons(close, open),
            },
        };
    }

    FrameworkElement Failed(string why)
    {
        var close = Primary("Close"); close.Click += (_, _) => Close();
        return new StackPanel { Children = { Heading($"{Program.AppName} couldn't be installed"), Text(why, 13, Dim, 0, 10), Text("Nothing of your chats or settings was touched.", 12, Dim, 0, 0), Buttons(close) } };
    }

    FrameworkElement UninstallChoices()
    {
        var data = Check($"Remove my {Program.Meta("DataWords")} too (there's no cloud copy: it can't be brought back)", false);
        var go = Primary("Uninstall");
        go.Click += async (_, _) =>
        {
            var deleteData = data.IsChecked == true;
            Show(new StackPanel { Children = { Heading($"Removing {Program.AppName}"), Text("This takes a moment.", 13.5, Dim, 0, 0) } });
            try { await Task.Run(() => Program.Uninstall(Program.InstalledDir() ?? Program.DefaultDir, deleteData)); }
            catch (Exception ex) { Show(Failed(ex.Message)); return; }
            var close = Primary("Close"); close.Click += (_, _) => Close();
            Show(new StackPanel { Children = { Heading($"{Program.AppName} is removed"), Text(deleteData ? "Your data was deleted too." : $"Your chats and settings are still in your profile, ready if {Program.AppName} comes back.", 13.5, Dim, 0, 0), Buttons(close) } });
        };
        var cancel = Ghost("Cancel"); cancel.Click += (_, _) => Close();
        return new StackPanel { Children = { Heading($"Uninstall {Program.AppName}?"), Text($"{Program.AppName} is removed from this PC. Your data stays, so installing it again brings back everything exactly as it was.", 13.5, Dim, 0, 18), data, Buttons(cancel, go) } };
    }

    // ---------- pieces ----------
    // the app's mark: Ivo's crystal, or for Ivo Portal its own logo (the icon this installer carries, at full size)
    static FrameworkElement Crystal(double size)
    {
        if (Program.AppName == "Ivo Portal" && OwnIcon((int)size * 2) is { } logo)
            return new Image { Source = logo, Width = size, Height = size };
        var c = new Canvas { Width = 14, Height = 16 };
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L7,16 L0,8 Z"), Fill = Hex("#7C5CFF") });
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L14,8 L7,16 Z"), Fill = Hex("#22D3EE") });
        c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L14,8 L7,8 Z"), Fill = Brushes.White, Opacity = 0.22 });
        return new Viewbox { Width = size, Height = size * 16 / 14, Child = c };
    }
    static ImageSource? OwnIcon(int px)
    {
        var icons = new nint[1]; var ids = new int[1];
        if (PrivateExtractIcons(Environment.ProcessPath!, 0, px, px, icons, ids, 1, 0) == 0 || icons[0] == 0) return null;
        try { var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); src.Freeze(); return src; }
        finally { DestroyIcon(icons[0]); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, int[] ids, uint count, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);

    static TextBlock Heading(string t) => new() { Text = t, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Fg, Margin = new Thickness(0, 0, 0, 8) };
    static TextBlock Text(string t, double size, Brush b, double top, double bottom) => new() { Text = t, FontSize = size, Foreground = b, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5, Margin = new Thickness(0, top, 0, bottom) };

    static CheckBox Check(string label, bool on)
    {
        var box = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5), BorderBrush = Dim, Margin = new Thickness(0, 0, 10, 0) };
        var tick = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Child = tick;
        var cb = new CheckBox { IsChecked = on, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 10), Content = label };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { box, new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center } }, Background = Brushes.Transparent };
        void Draw() { var c = cb.IsChecked == true; box.Background = c ? Accent : Brushes.Transparent; box.BorderBrush = c ? Brushes.Transparent : Dim; tick.Visibility = c ? Visibility.Visible : Visibility.Hidden; }
        cb.Template = new ControlTemplate(typeof(CheckBox)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        cb.Content = row;
        cb.Checked += (_, _) => Draw(); cb.Unchecked += (_, _) => Draw();
        Draw();
        return cb;
    }

    static Button Primary(string label) => Pill(label, Accent, Brushes.White, FontWeights.SemiBold);
    static Button Ghost(string label) => Pill(label, Raised, Fg, FontWeights.Normal);
    static Button Pill(string label, Brush bg, Brush fg, FontWeight weight)
    {
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, bg);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bd.SetValue(Border.PaddingProperty, new Thickness(20, 9, 20, 10));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        bd.AppendChild(cp);
        var b = new Button { Content = label, Foreground = fg, FontSize = 13.5, FontWeight = weight, Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0), Template = new ControlTemplate(typeof(Button)) { VisualTree = bd } };
        b.MouseEnter += (_, _) => b.Opacity = 0.9; b.MouseLeave += (_, _) => b.Opacity = 1;
        return b;
    }
    static Button LinkButton(string label) => new() { Content = label, Foreground = Hex("#A78BFA"), FontSize = 12.5, Cursor = Cursors.Hand, Template = Bare(), Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    static ControlTemplate Bare() => new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    static FrameworkElement Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}
