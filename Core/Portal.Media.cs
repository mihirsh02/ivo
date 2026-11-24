using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Windows;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace Ivo;

// Portal's calls: voice, camera and screen, over the same encrypted connection (SRTP). The audio and video lines are agreed
// when the two connect, so a call starts and stops later with no new codes: only "ring / accept / hang up" messages on the
// chat channel. Sound: Opus at 48 kHz, the microphone and speakers you picked. Video: VP8, the camera (Windows' own capture, up to 1920×1080 at
// 30 fps, its bitrate from its size and frame rate) or the screen (its own size up to 2560 wide at 15 fps, 8 Mbps, sharp enough
// to read text; with the camera beside it, up to 1920 + 768 wide at 11 Mbps).
public sealed partial class Portal
{
    static readonly VideoFormat Vp8 = new(VideoCodecsEnum.VP8, 96);
    MediaStreamTrack? audioTrack, videoTrack;
    WindowsAudioEndPoint? sound;
    AudioFormat? audioFormat;
    VpxVideoEncoder encoder = new();
    readonly VpxVideoEncoder decoder = new();
    MediaCapture? camera;
    MediaFrameReader? cameraFrames;
    int encoding;
    System.Threading.Timer? screenTimer;
    readonly object codec = new(), decoding = new();   // sending and receiving each on its own: a big frame going out never holds up one coming in

    // "" · camera · screen (with the camera on too, it goes beside the screen in the same picture); the other side is told
    // which, so it can label the tile ("presenting") and lay it out
    public string Sending { get => sending; private set { if (sending == value) return; sending = value; Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "kind", ["v"] = value }); } }
    string sending = "";
    // your own picture split the same way ("split"): what you send, shown back to you as two tiles
    public (double Screen, double Camera) MySplit { get; private set; }
    public bool CameraOn => camera != null;
    // the camera while the screen is shared: its newest frame, small, sent beside the screen (one stream; the other side splits it into two tiles,
    // told where by a "split" message)
    (byte[] Bgr, int W, int H)? pip;

    // decoded frames, BGR 24-bit: theirs (to show) and yours (the little preview)
    public event Action<byte[], int, int>? TheirFrame, MyFrame;
    public event Action<string>? SharingStopped;   // the shared window went away (minimised, closed): why, for a notice
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(nint h);

    // sound: Opus at 48 kHz (full-band, what video meetings use)
    static AudioEncoder Voice() => new(new AudioEncoder(true, true).SupportedFormats.Where(f => f.FormatName == "OPUS").ToArray());

    void AddMedia()
    {
        audioTrack = new MediaStreamTrack(Voice().SupportedFormats, MediaStreamStatusEnum.SendRecv);
        videoTrack = new MediaStreamTrack(Vp8, MediaStreamStatusEnum.SendRecv);
        pc.addTrack(audioTrack); pc.addTrack(videoTrack);
        pc.OnAudioFormatsNegotiated += f => { audioFormat = f.First(); if (sound != null) { sound.SetAudioSourceFormat(audioFormat.Value); sound.SetAudioSinkFormat(audioFormat.Value); } };
        pc.OnAudioFrameReceived += f => sound?.GotEncodedMediaFrame(f);
        pc.OnVideoFrameReceived += (_, _, frame, _) =>
        {
            if (TheirFrame == null) return;
            List<VideoSample> got;
            try { lock (decoding) got = decoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8).ToList(); }
            catch { return; }   // a damaged frame: skipped, the next key frame puts the picture right
            foreach (var s in got) TheirFrame?.Invoke(s.Sample, (int)s.Width, (int)s.Height);
        };
    }

    // ---------- sound: both sides start it when a call is accepted ----------
    public async Task StartSound()
    {
        if (sound != null) return;
        sound = new WindowsAudioEndPoint(Voice(), PortalDevices.Speaker, PortalDevices.Mic);   // the speakers and microphone you picked (or Windows' own)
        if (audioFormat is { } f) { sound.SetAudioSourceFormat(f); sound.SetAudioSinkFormat(f); }
        sound.OnAudioSourceEncodedSample += pc.SendAudio;
        await sound.StartAudio();
        await sound.StartAudioSink();
        if (Muted) await sound.PauseAudio();   // joined with the mic off
    }

    // muting tells their side, so their view shows your mic off (as Meet does)
    public void Mute(bool muted)
    {
        Muted = muted;
        Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "mic", ["on"] = !muted });
        if (sound == null) return;
        if (muted) _ = sound.PauseAudio(); else _ = sound.ResumeAudio();
    }
    public bool Muted { get; private set; }
    public bool TheirMuted { get; private set; }
    public event Action? TheirMic;

    // before joining: the camera shows only here (the "Ready to join?" preview), nothing is sent until Preview goes off
    public bool Preview { get; set; }

    public async Task EndCall()
    {
        await StopVideo();
        if (sound is { } s) { sound = null; s.OnAudioSourceEncodedSample -= pc.SendAudio; try { await s.CloseAudio(); } catch { } }
    }

    // ---------- video: the camera or the screen ----------
    public async Task StartCamera()
    {
        await StopCamera();
        // the camera you picked (or the first one), its colour stream, its largest size up to 1920 wide at its best frame rate
        var found = (await MediaFrameSourceGroup.FindAllAsync()).SelectMany(g => g.SourceInfos.Select(i => (Group: g, Info: i)))
            .Where(x => x.Info.SourceKind == MediaFrameSourceKind.Color)
            .OrderByDescending(x => !string.IsNullOrEmpty(PortalDevices.Camera) && x.Info.DeviceInformation?.Id == PortalDevices.Camera).FirstOrDefault();
        if (found.Group == null) throw new InvalidOperationException("No camera found.");
        var mc = new MediaCapture();
        await mc.InitializeAsync(new MediaCaptureInitializationSettings { SourceGroup = found.Group, StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu, SharingMode = MediaCaptureSharingMode.ExclusiveControl });
        var source = mc.FrameSources[found.Info.Id];
        var best = source.SupportedFormats.Where(f => f.VideoFormat.Width <= 1920 && f.Subtype is "NV12" or "YUY2" or "MJPG")
                .OrderByDescending(f => f.VideoFormat.Width).ThenByDescending(f => (double)f.FrameRate.Numerator / Math.Max(1, f.FrameRate.Denominator)).ThenBy(f => f.Subtype == "MJPG").FirstOrDefault();
            if (best != null) await source.SetFormatAsync(best);
        // a steady frame rate over brighter frames: cameras that offer it would otherwise drop to ~13 fps in dim light
        try { if (mc.VideoDeviceController.ExposurePriorityVideoControl is { Supported: true } ep) ep.Enabled = false; } catch { }
        var reader = await mc.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;   // always the newest frame, never a backlog
        var last = DateTime.UtcNow;
        reader.FrameArrived += (r, _) =>
        {
            // the camera never waits on the encoder: while one frame is being sent, newer ones are skipped (a slower PC sends fewer, never late)
            if (Interlocked.Exchange(ref encoding, 1) == 1) return;
            using var frame = r.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } got || Sending == "") { encoding = 0; return; }
            var corner = Sending == "screen";
            using var bgra = got.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(got, BitmapPixelFormat.Bgra8);
            var use = bgra ?? got;
            int w = use.PixelWidth & ~1, h = use.PixelHeight & ~1, stride = use.PixelWidth;
            // the same buffers frame after frame (two for the picture, as the window may still be drawing the last one): no
            // megabytes of garbage a frame for the collector to stop the call over
            var raw = camRaw.Length == use.PixelWidth * use.PixelHeight * 4 ? camRaw : camRaw = new byte[use.PixelWidth * use.PixelHeight * 4];
            use.CopyToBuffer(raw.AsBuffer());
            camFlip = !camFlip;
            var n = w * h * 3;
            var bgr = camFlip ? (camBgrA.Length == n ? camBgrA : camBgrA = new byte[n]) : (camBgrB.Length == n ? camBgrB : camBgrB = new byte[n]);
            Task.Run(() =>
            {
                try
                {
                    for (int y = 0; y < h; y++) for (int x = 0, si = y * stride * 4, di = y * w * 3; x < w; x++, si += 4, di += 3) { bgr[di] = raw[si]; bgr[di + 1] = raw[si + 1]; bgr[di + 2] = raw[si + 2]; }
                    if (corner) { pip = Shrink(bgr, w, h, CamTile); return; }   // sharing the screen: it goes beside it instead
                    var now = DateTime.UtcNow; var ms = (uint)Math.Clamp((now - last).TotalMilliseconds, 1, 1000); last = now;
                    SendFrame(ms, w, h, bgr, VideoPixelFormatsEnum.Bgr, "camera");
                }
                finally { encoding = 0; }
            });
        };
        camRaw = []; camBgrA = []; camBgrB = [];
        // what the camera gives, fully used: about 0.12 bits a pixel (1080p30 ≈ 7.5 Mbps, 720p30 ≈ 3.3), never under 2.5
        var fps = best == null ? 30 : (double)best.FrameRate.Numerator / Math.Max(1, best.FrameRate.Denominator);
        cameraKbps = best == null ? 3500u : (uint)Math.Clamp(best.VideoFormat.Width * best.VideoFormat.Height * Math.Min(fps, 30) * 0.12 / 1000, 2500, 8000);
        if (Sending != "screen") { Fresh(cameraKbps, 44); Sending = "camera"; }
        camera = mc; cameraFrames = reader;
        await reader.StartAsync();
    }

    byte[] camRaw = [], camBgrA = [], camBgrB = [];
    bool camFlip;

    public ShareTarget? Sharing { get; private set; }

    // a screen, or one app's window (picked in ScreenPicker)
    public Task StartScreen(ShareTarget what)
    {
        screenTimer?.Dispose(); screenTimer = null;   // the camera, if on, stays: it moves into the corner
        Fresh(camera != null ? BesideKbps : 8000, 30);   // text stays sharp: a much finer worst quality than the camera's
        Sending = "screen"; Sharing = what; sentSize = default;
        var busy = 0;
        screenTimer = new System.Threading.Timer(_ =>   // 15 frames a second; a slow one is skipped, never queued
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                // one app's window, minimised or closed: Windows can't picture it, so the sharing stops and says why
                if (Sending == "screen" && what.Window != 0 && (!IsWindow(what.Window) || IsIconic(what.Window)))
                {
                    var why = IsWindow(what.Window) ? $"{what.Name} was minimised" : $"{what.Name} was closed";
                    _ = StopScreen(); SharingStopped?.Invoke(why);
                    return;
                }
                if (Sending == "screen" && ScreenFrame(what, camera != null ? 1920 : 2560) is { } f)   // with the camera too, a smaller screen: one picture the PC can encode at full speed
                {
                    var (bgr, w, h, s, c) = camera != null ? Beside(f.Bgr, f.W, f.H, pip) : (f.Bgr, f.W, f.H, 0.0, 0.0);
                    if ((w, h) != sentSize)   // the camera came or went: a new size, a fresh encoder, and where the line is
                    {
                        if (sentSize != default) Fresh(camera != null ? BesideKbps : 8000u, 30);
                        sentSize = (w, h); MySplit = (s, c);
                        Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "split", ["s"] = s, ["c"] = c });
                    }
                    SendFrame(66, w, h, bgr, VideoPixelFormatsEnum.Bgr, "screen");
                }
            }
            finally { busy = 0; }
        }, null, 0, 66);
        return Task.CompletedTask;
    }

    uint cameraKbps = 3500;
    // screen and camera in one picture: the screen up to 1920 wide, the camera beside it at up to 768, with the bits for both
    const int CamTile = 768;
    const uint BesideKbps = 11000;

    // just the camera off: sharing goes on, without the corner
    public async Task StopCamera()
    {
        if (cameraFrames is { } r) { cameraFrames = null; try { await r.StopAsync(); } catch { } r.Dispose(); }
        if (camera is { } c) { camera = null; c.Dispose(); }
        pip = null;
        if (Sending == "camera") Sending = "";
    }

    // just the sharing off: the camera, if on, is the picture again
    public Task StopScreen()
    {
        screenTimer?.Dispose(); screenTimer = null; Sharing = null; Unsplit();
        if (Sending == "screen") { Sending = camera != null ? "camera" : ""; if (camera != null) Fresh(cameraKbps, 44); }
        return Task.CompletedTask;
    }

    // a BGR picture made smaller, each pixel the average of the ones it covers (nearest pixel turns a face jagged)
    static (byte[] Bgr, int W, int H) Shrink(byte[] bgr, int w, int h, int toW)
    {
        if (w <= toW) return ((byte[])bgr.Clone(), w, h);   // its own copy: the camera's buffer is used again
        int nw = toW & ~1, nh = Math.Max(2, (int)((long)h * nw / w) & ~1);
        var o = new byte[nw * nh * 3];
        for (int y = 0; y < nh; y++)
        {
            int y0 = y * h / nh, y1 = Math.Max(y0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * w / nw, x1 = Math.Max(x0 + 1, (x + 1) * w / nw), b = 0, g = 0, rr = 0;
                for (int sy = y0; sy < y1; sy++) for (int sx = x0, si = (sy * w + x0) * 3; sx < x1; sx++, si += 3) { b += bgr[si]; g += bgr[si + 1]; rr += bgr[si + 2]; }
                int n = (y1 - y0) * (x1 - x0), di = (y * nw + x) * 3;
                o[di] = (byte)(b / n); o[di + 1] = (byte)(g / n); o[di + 2] = (byte)(rr / n);
            }
        }
        return (o, nw, nh);
    }

    (int W, int H) sentSize;

    // ---------- a group call: one camera and one screen, sent to everyone ----------
    // In a group call one unconnected Portal (Preview on) captures the camera and the screen, and each person's Portal
    // sends on what it captured: the same picture, its own encoder. What it is and where it splits go along too.
    (int W, int H) forwardSize;
    public void Forward(byte[] bgr, int w, int h, string kind, (double Screen, double Camera) split)
    {
        if (Sending != kind) Sending = kind;
        if ((w, h) != forwardSize)
        {
            if (forwardSize != default) Fresh(kind == "screen" ? 8000u : 3500u, kind == "screen" ? 30u : 44u);
            forwardSize = (w, h); MySplit = split;
            Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "split", ["s"] = split.Screen, ["c"] = split.Camera });
        }
        SendFrame(66, w, h, bgr, VideoPixelFormatsEnum.Bgr, kind);
    }
    // nothing sent any more (camera and screen off): their side shows your face
    public void ForwardOff() { Sending = ""; forwardSize = default; MySplit = default; Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "video", ["on"] = false }); }
    void Unsplit() { MySplit = default; if (sentSize != default) { sentSize = default; Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "split", ["s"] = 0.0, ["c"] = 0.0 }); } }

    // the screen with the camera in a column to its right (up to 768 wide, at the top; black until the
    // camera's first frame), and where the screen ends (s) and the camera's bottom (c), as fractions of the whole
    static (byte[] Bgr, int W, int H, double S, double C) Beside(byte[] screen, int w, int h, (byte[] Bgr, int W, int H)? cam)
    {
        int cw = Math.Max(2, Math.Min(CamTile, w * 2 / 5) & ~1), W = w + cw;   // the camera at up to 768 wide: sharp in its own tile
        int ch = cam is { } c0 ? (int)((long)cw * c0.H / c0.W) & ~1 : (cw * 9 / 16) & ~1;
        var H = Math.Max(h, ch);   // a short screen never crops the camera: the picture is as tall as the taller of the two
        var o = new byte[W * H * 3];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(screen, y * w * 3, o, y * W * 3, w * 3);
        if (cam is { } c)
            for (int y = 0; y < ch; y++)
            {
                var sy = y * c.H / ch;
                for (int x = 0; x < cw; x++) { var si = (sy * c.W + x * c.W / cw) * 3; var di = (y * W + w + x) * 3; o[di] = c.Bgr[si]; o[di + 1] = c.Bgr[si + 1]; o[di + 2] = c.Bgr[si + 2]; }
            }
        return (o, W, H, (double)w / W, (double)ch / H);
    }

    public async Task StopVideo()
    {
        Sending = ""; Sharing = null; pip = null; Unsplit();
        screenTimer?.Dispose(); screenTimer = null;
        if (cameraFrames is { } r) { cameraFrames = null; try { await r.StopAsync(); } catch { } r.Dispose(); }
        if (camera is { } c) { camera = null; c.Dispose(); }
    }

    // a fresh encoder for each start: its own size and bitrate, and a key frame first so their side shows it at once.
    // The library starts VP8 at 256 kbps with the coarsest quality allowed (quantizer up to 63) on one thread, and ignores
    // TargetKbps; so once it has started, its live settings are replaced: our bitrate, a quality floor, several threads.
    uint wantKbps, worstQ; bool tuned;
    void Fresh(uint kbps, uint maxQuantizer = 50) { lock (codec) { encoder.Dispose(); encoder = new VpxVideoEncoder(); wantKbps = kbps; worstQ = maxQuantizer; tuned = false; } }

    static readonly System.Reflection.FieldInfo vp8Field = typeof(VpxVideoEncoder).GetField("_vp8Encoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    static readonly System.Reflection.FieldInfo ctxField = typeof(SIPSorceryMedia.Encoders.Codecs.Vp8Codec).GetField("_vpxEncodeCtx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    void Tune()
    {
        tuned = true;
        try
        {
            if (vp8Field.GetValue(encoder) is not { } vp8 || ctxField.GetValue(vp8) is not vpxmd.VpxCodecCtx ctx) return;
            var cfg = ctx.config.Enc;
            cfg.RcTargetBitrate = wantKbps; cfg.RcMinQuantizer = 2; cfg.RcMaxQuantizer = worstQ;
            cfg.RcEndUsage = vpxmd.VpxRcMode.VPX_VBR; cfg.RcOvershootPct = 25; cfg.RcUndershootPct = 50;
            cfg.RcBufSz = 1000; cfg.RcBufInitialSz = 500; cfg.RcBufOptimalSz = 600;
            cfg.GThreads = (uint)Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            // a lost packet spoils the picture only until the next key frame: at most 30 frames away (1–2 s), and frames that don't lean on lost ones
            cfg.GErrorResilient = 1; cfg.KfMode = vpxmd.VpxKfMode.VPX_KF_AUTO; cfg.KfMinDist = 0; cfg.KfMaxDist = 30;
            cfg.RcDropframeThresh = 0;   // never skip frames to save bits: send every one at the quality allowed
            vpxmd.vpx_encoder.VpxCodecEncConfigSet(ctx, cfg);
        }
        catch { }   // a library that changed inside: its own settings stay
    }

    // One encoder, one picture size. Camera and screen frames are made on their own threads, so one from what was
    // showing a moment ago can still arrive after a switch (sharing started, camera turned off): it's dropped, and a
    // new size always gets a new encoder. Fed a frame of another size, the native encoder reads past the buffer
    // (Portal crashed) or sends garbage (their side went grey and scrambled).
    (int W, int H) encodedSize;
    void SendFrame(uint ms, int w, int h, byte[] bgr, VideoPixelFormatsEnum fmt, string kind)
    {
        if (bgr.Length < w * h * 3 || w < 2 || h < 2) return;   // never hand the native encoder less than it will read
        if (Preview) { MyFrame?.Invoke(bgr, w, h); return; }
        if (!Open) return;
        byte[]? encoded;
        lock (codec)
        {
            if (Sending != kind) return;   // a late frame from what was being sent before
            if (tuned && (w, h) != encodedSize) { encoder.Dispose(); encoder = new VpxVideoEncoder(); tuned = false; }
            // the library starts its encoder on the first frame: start it, set our settings, then send a key frame made with them
            if (!tuned) { encoder.EncodeVideo(w, h, bgr, fmt, VideoCodecsEnum.VP8); Tune(); encoder.ForceKeyFrame(); encodedSize = (w, h); }
            encoded = encoder.EncodeVideo(w, h, bgr, fmt, VideoCodecsEnum.VP8);
        }
        if (encoded != null) pc.SendVideo(ms * 90, encoded);   // RTP time: 90 kHz
        MyFrame?.Invoke(bgr, w, h);
    }

    // what's shared, at its own size up to 2560 wide, as BGR 24-bit rows
    static (byte[] Bgr, int W, int H)? ScreenFrame(ShareTarget what, int maxW = 2560)
    {
        try
        {
            using var full = ScreenPicker.Capture(what.Screen, what.Window);
            if (full == null) return null;
            var b = new System.Drawing.Rectangle(0, 0, full.Width, full.Height);
            var k = Math.Min(1.0, (double)maxW / b.Width);   // your screen as it is, up to 2560 wide (1600 with the camera beside it)
            int w = Math.Max(2, (int)(b.Width * k) & ~1), h = Math.Max(2, (int)(b.Height * k) & ~1);
            using var small = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(small)) { g.InterpolationMode = k < 1 ? System.Drawing.Drawing2D.InterpolationMode.Bilinear : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed; g.DrawImage(full, 0, 0, w, h); }   // fast scaling: 15 times a second on the CPU
            var data = small.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var bgr = new byte[w * h * 3];
                for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgr, y * w * 3, w * 3);
                return (bgr, w, h);
            }
            finally { small.UnlockBits(data); }
        }
        catch { return null; }
    }
}
