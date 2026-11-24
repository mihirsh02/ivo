using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Settings → Connections, laid out like API keys: pick the app, give the account a name if you like, paste its token
// (Spotify signs in through the browser instead). Ivo checks it with the service and keeps it encrypted. The same app
// can be added more than once, one account per name; the connected accounts are listed below.
public partial class MainWindow
{
    async Task<string> SignInSpotifyFromChat()
    {
        var service = Connections.All.First(s => s.Id == "spotify");
        var refresh = await Spotify.SignIn();
        var who = await Connections.Check(service, await Spotify.Access(refresh));
        S.Connections["spotify"] = Store.Protect(refresh);
        SaveSettings();
        UpdateCounts();
        MusicTimerOn();
        return who;
    }

    void BuildConnections()
    {
        ConnectionsTab.Children.Clear();
        ConnectionsTab.Children.Add(AddConnectionCard());
        ConnectionsTab.Children.Add(new TextBlock { Text = "CONNECTED", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 22, 0, 8) });
        var accounts = Connections.Accounts.ToList();
        if (accounts.Count == 0)
        {
            var none = new TextBlock { Text = "None yet. Add one above.", FontSize = 12 };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            ConnectionsTab.Children.Add(none);
        }
        foreach (var a in accounts) ConnectionsTab.Children.Add(AccountRow(a));
    }

    FrameworkElement AddConnectionCard()
    {
        var body = new StackPanel();
        var card = new Border { Style = (Style)FindResource("Group"), Child = body };
        body.Children.Add(new TextBlock { Text = "Add a connection", Style = (Style)FindResource("GroupTitle") });
        var pick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = Connections.All.Select(s => s.Name).ToList(), SelectedIndex = 0 };
        body.Children.Add(pick);
        body.Children.Add(new TextBlock { Text = "NAME  ·  optional, to tell two accounts of the same app apart (Work, Personal)", Style = (Style)FindResource("Label") });
        var name = new TextBox();
        body.Children.Add(name);
        var tokenLabel = new TextBlock { Text = "ACCESS TOKEN", Style = (Style)FindResource("Label") };
        body.Children.Add(tokenLabel);

        var row = new DockPanel();
        var connect = new Button { Style = (Style)FindResource("Shimmer"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), Content = "Connect" };
        DockPanel.SetDock(connect, Dock.Right);
        var token = new PasswordBox { VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(connect); row.Children.Add(token);
        body.Children.Add(row);
        var help = new TextBlock { Style = (Style)FindResource("Help"), Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(help);
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(status);

        Connections.Service Svc() => Connections.All[Math.Max(0, pick.SelectedIndex)];
        void Status(string text, string color) { status.Text = text; status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        void Show()   // the chosen app's help; Spotify has no token to paste
        {
            var svc = Svc();
            var browser = svc.Id == "spotify";
            tokenLabel.Visibility = token.Visibility = browser ? Visibility.Collapsed : Visibility.Visible;
            connect.Content = browser ? "Sign in with Spotify" : "Connect";
            help.Inlines.Clear(); help.Inlines.Add(svc.TokenHelp + " ");
            if (svc.TokenUrl != "")
            {
                var get = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Create one ↗")) { Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)) };
                get.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(svc.TokenUrl) { UseShellExecute = true }); } catch { } };
                help.Inlines.Add(get);
            }
            status.Text = "";
        }
        pick.SelectionChanged += (_, _) => Show();
        Show();

        async void Connect()
        {
            var svc = Svc();
            var key = svc.Id == "spotify" ? "spotify" : Connections.Key(svc, name.Text);   // one Spotify: it's the music remote
            if (S.Connections.ContainsKey(key)) { Status(name.Text.Trim() == "" ? $"{svc.Name} is already added. Give this one a name." : "That name is already used.", "#F85149"); return; }
            connect.IsEnabled = false;
            try
            {
                string who;
                if (svc.Id == "spotify")
                {
                    Status("Waiting for the browser…", "#8B8B98");
                    var refresh = await Spotify.SignIn();
                    S.Connections[key] = Store.Protect(refresh);
                    who = await Connections.Check(svc, await Spotify.Access(refresh));
                    MusicTimerOn();
                }
                else
                {
                    var t = token.Password.Trim();
                    if (t == "") { connect.IsEnabled = true; return; }
                    Status("Checking…", "#8B8B98");
                    who = await Connections.Check(svc, t);
                    S.Connections[key] = Store.Protect(t);
                }
                SaveSettings(); BuildConnections(); UpdateCounts();
                _ = Notice($"{svc.Name} connected", $"Signed in as {who}.");
            }
            catch (Exception ex) { Status(ex.Message.Length > 120 ? (svc.Id == "spotify" ? "Sign-in didn't finish." : "Token not accepted.") : ex.Message, "#F85149"); connect.IsEnabled = true; }
        }
        connect.Click += (_, _) => Connect();
        token.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Connect(); };
        return card;
    }

    FrameworkElement AccountRow(Connections.Account a)
    {
        var row = new DockPanel();
        var card = new Border { Style = (Style)FindResource("Group"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 10, 12, 10), Child = row };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        if (a.Svc.Id == "spotify")   // the remote: search, playlists, queue, devices
        {
            var open = new Button { Content = "Open Spotify", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, _) => OpenSpotify();
            buttons.Children.Add(open);
        }
        var off = new Button { Content = "Disconnect", Padding = new Thickness(12, 5, 12, 5) };
        off.Click += async (_, _) =>
        {
            if (!await Confirm($"Disconnect {a.Name}?", "Chats lose access to it.", "Disconnect", danger: true)) return;
            S.Connections.Remove(a.Key); if (a.Svc.Id == "spotify") Spotify.Forget(); SaveSettings(); BuildConnections(); UpdateCounts(); MusicTimerOn();
        };
        buttons.Children.Add(off);
        row.Children.Add(buttons);
        var title = new TextBlock { Text = a.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var dot = new TextBlock { Text = "  ● Connected", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, dot } });
        return card;
    }
}
