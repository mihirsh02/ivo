using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Ivo;

// How Ivo connects, as browsers do ("happy eyeballs"): every address of the host is tried, IPv6 and IPv4 interleaved
// and a quarter second apart, and the first to connect wins. A network with a broken IPv6 route, or a broken IPv4 one,
// costs a moment instead of a 20-second timeout per address.
public static class Net
{
    public static SocketsHttpHandler Handler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // a network change gets fresh connections soon
        ConnectCallback = async (ctx, ct) => new NetworkStream(await Connect(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct), ownsSocket: true),
    };

    public static async Task<Socket> Connect(string host, int port, CancellationToken ct)
    {
        var all = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        var v6 = all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
        var v4 = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        var order = new List<IPAddress>();
        for (int i = 0; i < Math.Max(v6.Count, v4.Count); i++) { if (i < v6.Count) order.Add(v6[i]); if (i < v4.Count) order.Add(v4[i]); }
        if (order.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tries = new List<Task<Socket>>();
        Exception? last = null;
        async Task<Socket> Try(IPAddress a, int delay)
        {
            await Task.Delay(delay, race.Token);
            var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(a, port), race.Token); return s; }
            catch { s.Dispose(); throw; }
        }
        for (int i = 0; i < order.Count; i++) tries.Add(Try(order[i], i * 250));
        while (tries.Count > 0)
        {
            var done = await Task.WhenAny(tries);
            tries.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                race.Cancel();
                foreach (var other in tries) _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                return done.Result;
            }
            last = done.Exception?.InnerException ?? last;
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.TimedOut);
    }
}
