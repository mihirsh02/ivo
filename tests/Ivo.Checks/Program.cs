using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ivo;

// Checks for the parts where a quiet mistake costs data or leaks secrets. Files only in a temp folder, never Ivo's own.
// a data folder of their own, set before anything reads Ivo's (Store.Dir), so nothing here can touch your real data
if (Environment.GetEnvironmentVariable("IVO_DATA_DIR") is not { Length: > 0 })
    Environment.SetEnvironmentVariable("IVO_DATA_DIR", Path.Combine(Path.GetTempPath(), "ivo-checks-" + Environment.ProcessId));
var failed = 0;
Journal.On = MoodLog.On = false;   // the checks' chats stay out of a journal and mood log
var online = Environment.GetEnvironmentVariable("IVO_CHECKS_OFFLINE") == null;   // the relay checks send real messages, which count against ntfy.sh's daily quota
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failed++; }
bool Throws(Action a) { try { a(); return false; } catch { return true; } }

// ---------- Crypto ----------
var key = RandomNumberGenerator.GetBytes(32);
var plain = Encoding.UTF8.GetBytes("hello Ivo");
Check("seal / open round trip", Crypto.Open(key, Crypto.Seal(key, plain)).SequenceEqual(plain));
Check("open with the wrong key fails", Throws(() => Crypto.Open(RandomNumberGenerator.GetBytes(32), Crypto.Seal(key, plain))));
{
    var nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Check("presence: a fresh beat shows them active", new ChatMember { Status = "active", Seen = nowS - 20 }.Live == "active");
    Check("presence: an app closed without a goodbye goes offline", new ChatMember { Status = "active", Seen = nowS - 90 }.Live == "offline");
}
var tampered = Convert.FromBase64String(Crypto.Seal(key, plain)); tampered[^1] ^= 1;
Check("tampered data is refused", Throws(() => Crypto.OpenBytes(key, tampered)));
var wrapped = Crypto.Wrap(key, "123456");
Check("wrap / unwrap with the right secret", Crypto.Unwrap(wrapped, "123456")?.SequenceEqual(key) == true);
Check("unwrap with a wrong secret is null", Crypto.Unwrap(wrapped, "654321") == null);
var hash = Crypto.Hash("correct horse");
Check("hash matches its secret", Crypto.Matches(hash, "correct horse"));
Check("hash rejects another secret", !Crypto.Matches(hash, "correct horsE"));
var backup = Crypto.SealWithPassword(plain, "pass word 1");
Check("password-sealed file is recognised", Crypto.IsPasswordSealed(backup) && !Crypto.IsPasswordSealed(plain));
Check("password-sealed file opens with its password", Crypto.OpenWithPassword(backup, "pass word 1")?.SequenceEqual(plain) == true);
Check("password-sealed file refuses a wrong password", Crypto.OpenWithPassword(backup, "pass word 2") == null);

// ---------- encryption at rest (temp files) ----------
var dir = Directory.CreateTempSubdirectory("ivo-checks").FullName;
try
{
    var f = Path.Combine(dir, "chat.json");
    Store.EncryptOn = true; Store.DataKey = key;
    Check("sealed write succeeds while unlocked", Store.WriteData(f, "{\"a\":1}"));
    Check("file on disk is not readable text", !File.ReadAllText(f).Contains("\"a\""));
    Check("sealed file reads back while unlocked", Store.ReadData(f) == "{\"a\":1}");
    Store.DataKey = null;
    Check("sealed file reads as nothing before unlock", Store.ReadData(f) == null);
    Check("nothing is written before unlock", !Store.WriteData(f, "{}") && File.ReadAllText(f).StartsWith("IVOENC1:"));
    Store.EncryptOn = false;
    var g = Path.Combine(dir, "plain.json");
    Store.WriteData(g, "plain");
    Check("plain files stay plain without the lock", File.ReadAllText(g) == "plain" && Store.ReadData(g) == "plain");
}
finally { Store.EncryptOn = false; Store.DataKey = null; Directory.Delete(dir, true); }

// ---------- hiding secrets ----------
using (Redact.Begin())
{
    var text = "key sk-ant-abcdefghijklmnopqrstuvwxyz123 and password: hunter2pass, card 4111 1111 1111 1111, token ghp_abcdefghijklmnopqrstuvwxyz0123";
    var masked = Redact.Mask(text);
    Check("keys, passwords, cards and tokens are masked", !masked.Contains("sk-ant") && !masked.Contains("hunter2pass") && !masked.Contains("4111 1111") && !masked.Contains("ghp_"));
    Check("masked text comes back whole", Redact.Unmask(masked) == text);
    Check("the same secret gets the same placeholder", Redact.Mask("sk-ant-abcdefghijklmnopqrstuvwxyz123") == Redact.Mask("sk-ant-abcdefghijklmnopqrstuvwxyz123"));
    Check("ordinary words aren't masked", Redact.Mask("my secret recipe is a password manager tip") == "my secret recipe is a password manager tip");
    Check("non-card numbers aren't masked", Redact.Mask("order 1234 5678 9012 3456") == "order 1234 5678 9012 3456");
}
Check("nothing is masked outside a request", Redact.Mask("sk-ant-abcdefghijklmnopqrstuvwxyz123").StartsWith("sk-ant"));
Check("regional calendar: Saka (22 March is 1 Chaitra; 8 Nov 2026 is 17 Kartika 1948)", RegionalCalendar.Day(new DateTime(2026, 3, 22), "indian") == "1 Chaitra 1948 Saka" && RegionalCalendar.Day(new DateTime(2026, 11, 8), "indian") == "17 Kartika 1948 Saka" && RegionalCalendar.Day(new DateTime(2024, 3, 21), "indian") == "1 Chaitra 1946 Saka");
Check("regional calendar: Hijri and lunar dates come out, none for the US", RegionalCalendar.Day(DateTime.Today, "ae")!.EndsWith(" AH") && RegionalCalendar.Day(DateTime.Today, "china")!.StartsWith("Lunar month") && RegionalCalendar.Day(DateTime.Today, "usa") == null);
Check("localhost and private networks are local", Redact.IsLocal("http://localhost:11434/v1") && Redact.IsLocal("http://192.168.1.5:1234") && !Redact.IsLocal("https://api.openai.com/v1"));

// ---------- provider fallback ----------
Check("a 'no credits' reply is spotted", Router.OutOfCredits("The account behind this API key doesn't have enough credits. Please top up."));
Check("a normal reply isn't", !Router.OutOfCredits("Here are today's top stories about credit card rules."));
Check("local coding model uses the configured on-device provider", IvoModel.CodingProvider.Kind == "llama"
    && IvoModel.CodingProvider.Name == IvoModel.CodingProviderName
    && (IvoModel.Ready ? Api.ModelsFor(IvoModel.CodingProvider).SequenceEqual([IvoModel.CodingModelId]) : Api.ModelsFor(IvoModel.CodingProvider).Length == 0));

// ---------- schedules ----------
var past = DateTime.Now.AddDays(-3).Date.AddHours(9);
var daily = MainWindow.Next(past, "daily");
Check("daily repeats land in the future", daily > DateTime.Now && daily!.Value.TimeOfDay == past.TimeOfDay);
var wd = MainWindow.Next(DateTime.Now, "weekdays");
Check("weekday repeats skip weekends", wd is { DayOfWeek: not (DayOfWeek.Saturday or DayOfWeek.Sunday) });
Check("one-offs don't repeat", MainWindow.Next(DateTime.Now, "once") == null);

// ---------- times people write ----------
var noon = new DateTime(2026, 11, 6, 12, 0, 0);
Check("'in 20 min'", When.Parse("call mom in 20 min", noon) is { } a1 && a1.At == noon.AddMinutes(20) && a1.Text == "call mom");
Check("'at 5pm' later today", When.Parse("standup at 5pm", noon)?.At == noon.Date.AddHours(17));
Check("'at 9am' already passed: tomorrow", When.Parse("gym at 9am", noon)?.At == noon.Date.AddDays(1).AddHours(9));
Check("'tomorrow 8:30am'", When.Parse("tomorrow 8:30am send report", noon) is { } a2 && a2.At == noon.Date.AddDays(1).AddHours(8.5) && a2.Text == "send report");
Check("'after lunch'", When.Parse("after lunch", noon)?.At == noon.Date.AddHours(13.5));
Check("'tonight'", When.Parse("pay bills tonight", noon)?.At == noon.Date.AddHours(19));
Check("no time: nothing", When.Parse("buy milk", noon) == null);

// quick add: one line carries the whole to-do
var mon = new DateTime(2026, 11, 2, 10, 0, 0);   // a Monday morning
var qa = TodoText.Parse("Call mom tomorrow 6pm !high #family ~Home", mon);
Check("quick add: text", qa.Text == "Call mom");
Check("quick add: due with time", qa.Due == new DateTime(2026, 11, 3, 18, 0, 0) && qa.DueTime);
Check("quick add: priority, tag, list", qa.Priority == 3 && qa.Tags.SequenceEqual(["family"]) && qa.List == "Home");
var fri = TodoText.Parse("Pay rent friday", mon);
Check("quick add: weekday, no time", fri.Due == new DateTime(2026, 11, 6) && !fri.DueTime && fri.Text == "Pay rent");
var gym = TodoText.Parse("Gym every weekday", mon);
Check("quick add: repeat starts today", gym.Repeat == "weekdays" && gym.Due == mon.Date && gym.Text == "Gym");
Check("repeat: weekdays skip the weekend", TodoText.NextDue(new Todo { Repeat = "weekdays", Due = new DateTime(2099, 1, 2) }) == new DateTime(2099, 1, 5));   // a Friday → Monday
Check("quick add: plain text stays", TodoText.Parse("Buy milk", mon) is { Text: "Buy milk", Due: null, Priority: 0 });

// Portal: two sides on this PC swap codes, connect, and a message goes across
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    using var a = new Portal(); using var b = new Portal();
    var got = new TaskCompletionSource<string>();
    b.Message += o => got.TrySetResult((string?)o["x"] ?? "");
    var opened = new TaskCompletionSource();
    a.Opened += () => opened.TrySetResult();
    var invite = await a.Invite("A");
    var (reply, delivered) = await b.Join(invite, "B");
    if (!delivered) a.Accept(reply);   // the relay carried it: a connects by itself
    Check("portal: connects" + (delivered ? " (reply carried by the relay)" : " (reply pasted by hand)"), await Task.WhenAny(opened.Task, Task.Delay(20000)) == opened.Task);
    Check("portal: sees the internet is there", await Portal.Online());
    Check("portal: codes carry names", a.PeerName == "B" && b.PeerName == "A" && invite.StartsWith(Portal.Prefix));
    a.Send(new System.Text.Json.Nodes.JsonObject { ["t"] = "chat", ["x"] = "hello" });
    Check("portal: a message arrives", await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task && got.Task.Result == "hello");
    Check("portal: a reply isn't taken as an invite", Throws(() => new Portal().Join(reply, "C").GetAwaiter().GetResult()));
    var edited = invite[..^12] + (invite[^12] == 'A' ? 'B' : 'A') + invite[^11..];
    Check("portal: an edited code is refused", Throws(() => new Portal().Join(edited, "C").GetAwaiter().GetResult()));
}

// Portal video: a frame survives VP8 encode → decode at its size
{
    var enc = new SIPSorceryMedia.Encoders.VpxVideoEncoder(); var dec = new SIPSorceryMedia.Encoders.VpxVideoEncoder();
    var frame = new byte[320 * 240 * 3]; new Random(1).NextBytes(frame);
    var vp8 = enc.EncodeVideo(320, 240, frame, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Bgr, SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8);
    var back = vp8 == null ? [] : dec.DecodeVideo(vp8, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Bgr, SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8).ToList();
    Check("portal video: VP8 round trip", back.Count == 1 && back[0].Width == 320 && back[0].Height == 240);
}

// Portal contacts: a remote call request left in a contact's mailbox reaches the one listening there
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var c = PortalContacts.Make("A");
    var heard = new TaskCompletionSource<(string Invite, string Kind)>();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    _ = PortalContacts.Listen([c], (_, i, k) => heard.TrySetResult((i, k)), stop.Token);
    await Task.Delay(2500);   // the listener is subscribed
    var sent = await PortalContacts.RequestCall(c, "ivo-portal:self-test", "video");
    // This process ignores its own mailbox echo; emulate the other person's distinct sender identity.
    var topic = "ivo-portal-c-" + c.Box;
    var note = new System.Text.Json.Nodes.JsonObject
    {
        ["t"] = "knock", ["i"] = "ivo-portal:test", ["v"] = "video", ["f"] = "remote-check",
        ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    }.ToJsonString();
    var sealedNote = Portal.Seal(Portal.UnB64(c.Key), note);
    var nostrPost = Nostr.Post(topic, sealedNote);
    var posted = false;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await Portal.relay.PostAsync(Portal.Relay + topic, new System.Net.Http.StringContent(sealedNote), cts.Token);
        posted = response.IsSuccessStatusCode;
    }
    catch { }
    if (!posted) posted = await nostrPost;
    var got = await Task.WhenAny(heard.Task, Task.Delay(15000)) == heard.Task;
    Check("portal contacts: a remote call request arrives", sent && posted && got && heard.Task.Result == ("ivo-portal:test", "video"));
    stop.Cancel();
}

// Portal: when one side vanishes (no goodbye), the other knows within seconds
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var a = new Portal(); var b = new Portal();
    var opened = new TaskCompletionSource(); var gone = new TaskCompletionSource();
    a.Opened += () => opened.TrySetResult(); a.Closed += () => gone.TrySetResult();
    var (reply, delivered) = await b.Join(await a.Invite("A"), "B");
    if (!delivered) a.Accept(reply);
    await Task.WhenAny(opened.Task, Task.Delay(20000));
    var t = DateTime.Now;
    b.Dispose();   // gone, without "bye"
    var noticed = await Task.WhenAny(gone.Task, Task.Delay(12000)) == gone.Task;
    Check($"portal: a vanished side is noticed ({(DateTime.Now - t).TotalSeconds:0.0} s)", opened.Task.IsCompleted && noticed);
    a.Dispose();
}

// Ivo Chats: two people message each other, make a group, and the admin removes one (who then can't read on)
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var pair = PortalContacts.Make("B");
    var a = new IvoChats(null, _ => { }); var b = new IvoChats(null, _ => { });
    a.Sync([new PortalContact { Name = "B", Box = pair.Box, Key = pair.Key }], "A");
    b.Sync([new PortalContact { Name = "A", Box = pair.Box, Key = pair.Key }], "B");
    async Task<bool> Until(Func<bool> ok, int ms = 15000) { var t = DateTime.Now; while (!ok() && (DateTime.Now - t).TotalMilliseconds < ms) await Task.Delay(250); return ok(); }
    await Task.Delay(2500);
    var dmA = a.Book.Threads.Single(t => t.Kind == "dm"); var dmB = b.Book.Threads.Single(t => t.Kind == "dm");
    await b.Text(dmB, "hello A");
    Check("chats: a message arrives", await Until(() => dmA.Messages.Any(m => m.Text == "hello A")));
    Check("chats: they learn who sent it", dmA.Members.Any(m => m.Id == b.Book.MyId && m.Pub != ""));
    if (dmA.Messages.FirstOrDefault(m => m.Text == "hello A") is { } hello)
    {
        await a.React(dmA, hello, "❤️");
        var helloB = dmB.Messages.First(m => m.Text == "hello A");
        Check("chats: a reaction arrives", await Until(() => helloB.Reactions.GetValueOrDefault(a.Book.MyId) == "❤️"));
        await a.React(dmA, hello, "❤️");   // the same again takes it back
        Check("chats: a reaction is taken back", hello.Reactions.Count == 0 && await Until(() => helloB.Reactions.Count == 0));
        Check("chats: the sender sees Delivered", await Until(() => helloB.Delivered));
        a.Seen(dmA);
        Check("chats: the sender sees Seen", await Until(() => helloB.Read));
        b.Block(dmB, true);
        await a.Text(dmA, "can you hear me");
        Check("chats: a blocked person's message doesn't arrive", !await Until(() => dmB.Messages.Any(m => m.Text == "can you hear me"), 6000));
        b.Block(dmB, false);
        await a.Text(dmA, "and now?");
        Check("chats: unblocked, messages arrive again", await Until(() => dmB.Messages.Any(m => m.Text == "and now?")));
    }
    var g = await a.NewGroup("Team", [dmA]);
    Check("chats: the group reaches B", await Until(() => b.Book.Threads.Any(t => t.Kind == "group" && t.Name == "Team")));
    if (b.Book.Threads.FirstOrDefault(t => t.Kind == "group") is { } gB)   // (didn't reach B: the checks above already said so)
    {
        await Task.Delay(2500);   // B is listening in the group's mailbox
        await b.Text(gB, "hi team");
        Check("chats: a group message arrives", await Until(() => g.Messages.Any(m => m.Text == "hi team")));
        if (g.Members.FirstOrDefault(m => m.Id == b.Book.MyId) is { } member)
        {
            await a.Remove(g, member);
            Check("chats: the removed one knows", await Until(() => gB.Gone));
            Check("chats: the group moved on (new key)", gB.Key != g.Key);
        }
    }
    // 15 days in Recently deleted: the chat ends for both, and their app is told to drop the contact
    string? droppedBox = null; b.Unfriended += box => droppedBox = box;
    var theirBox = dmB.ContactBox;
    await a.Unfriend(dmA.ContactBox!);
    Check("chats: deleted for good, on both sides", !a.Book.Threads.Contains(dmA) && await Until(() => droppedBox == theirBox && !b.Book.Threads.Contains(dmB)));
    a.Stop(); b.Stop();
}

// Ivo Chats: a file goes up sealed and comes down whole
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var pair = PortalContacts.Make("B");
    var a = new IvoChats(null, _ => { });
    a.Sync([new PortalContact { Name = "B", Box = pair.Box, Key = pair.Key }], "A");
    var t = a.Book.Threads.Single(t => t.Kind == "dm");
    var src = Path.Combine(Path.GetTempPath(), "ivo-chat-check.txt");
    File.WriteAllText(src, "a file for the check " + Guid.NewGuid());
    var err = await a.SendFile(t, src);
    Check("chats: a file uploads" + (err != null ? " (" + err + ")" : ""), err == null);
    if (err == null)
    {
        var m = t.Messages.Last(); var copy = new ChatMsg { Url = m.Url, FileKey = m.FileKey, File = m.File };
        try { var got = await a.Download(copy); Check("chats: a file downloads whole", File.ReadAllText(got) == File.ReadAllText(src)); File.Delete(got); }
        catch (Exception ex) { Check("chats: a file downloads whole (" + ex.Message + ")", false); }
    }
    a.Stop();
}

// Ivo Chats: a full-size picture (bigger than one relay message) reaches the other side whole
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var pair = PortalContacts.Make("B");
    var a = new IvoChats(null, _ => { }); var b = new IvoChats(null, _ => { });
    a.Sync([new PortalContact { Name = "B", Box = pair.Box, Key = pair.Key }], "A");
    b.Sync([new PortalContact { Name = "A", Box = pair.Box, Key = pair.Key }], "B");
    async Task<bool> Until(Func<bool> ok, int ms = 25000) { var t = DateTime.Now; while (!ok() && (DateTime.Now - t).TotalMilliseconds < ms) await Task.Delay(300); return ok(); }
    await Task.Delay(2500);
    await b.Text(b.Book.Threads.Single(t => t.Kind == "dm"), "hi");   // so A knows who B is
    await Until(() => a.Book.Threads.Single(t => t.Kind == "dm").Members.Count > 0);
    var pic = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(30000));
    await b.SetProfile(pic, "busy");
    Check("chats: a big picture arrives whole", await Until(() => a.Book.Threads.Single(t => t.Kind == "dm").Members.FirstOrDefault()?.Pic == pic));
    a.Stop(); b.Stop();
}

// Ivo the mascot: offline, by meaning, typos and all
{
    var ivo = new IvoMind("");
    Check("ivo: does sums", ivo.Reply("what is 12 * 7").Text.StartsWith("84"));
    Check("ivo: converts units", ivo.Reply("5 km in miles").Text.Contains("3.11"));
    Check("ivo: knows calls", ivo.Reply("how do I make a video call?").Text.Contains("camera"));
    Check("ivo: copes with typos", ivo.Reply("how do i set a remindr").Text.Contains("Quick capture"));
    Check("ivo: synonyms (pic → picture)", ivo.Reply("change my profile pic").Text.Contains("status"));
    // what she keeps from a lookup: a plain meaning, never a joke, a crude line or an album title
    Check("ivo: keeps a plain meaning", IvoMind.Keepable("charm, the skill of flirting smoothly") != null);
    Check("ivo: drops a joke in someone's voice", IvoMind.Keepable("boobs are what is on a women's chest and is something I'll never be able to touch") == null);
    Check("ivo: drops a crude meaning", IvoMind.Keepable("the sweetest thing a guy can do without bein an ass") == null);
    Check("ivo: drops an album title", IvoMind.Keepable("Okay so, Romantic?? The sixth studio album by the English synth-pop band the Human League") == null);
    Check("ivo: drops a guitar joke", IvoMind.Keepable("the paticular sound that a fretless bass guitar makes when played") == null);
    {
        var junk = new IvoMemory();
        junk.Phrases["awwh"] = "sad"; junk.Phrases["what is the vibe"] = "vibe";
        var gone = junk.ClearJunk().GetAwaiter().GetResult();
        Check("ivo: clear junk forgets one-word reactions, keeps phrases", gone == 1 && junk.Phrases.ContainsKey("what is the vibe") && !junk.Phrases.ContainsKey("awwh"));
    }
    Check("ivo: follow-up goes deeper", ivo.Reply("how do I add a model").Text.Contains("models") && ivo.Reply("how?").Text.Contains("API keys"));
    Check("ivo: remembers your name", ivo.Reply("my name is Asha").Text.Contains("Asha") && ivo.Reply("what's my name").Text.Contains("Asha"));
    Check("ivo: starts the tour", ivo.Reply("show me around").Action == "tour");
    Check("ivo: gen z shortcuts (wyd)", !ivo.Reply("wyd").Lost);
    Check("ivo: stretched words (ssup, heyyy)", !ivo.Reply("ssup").Text.Contains("catch that") && !ivo.Reply("heyyy").Text.Contains("catch that"));
    Check("ivo: explains slang (rizz)", ivo.Reply("what does rizz mean").Text.Contains("charm"));
    Check("ivo: answers a vibe (no cap)", !ivo.Reply("no cap").Text.Contains("catch that"));
    Check("ivo: slang around a question", ivo.Reply("ngl how do i set a reminder rn").Text.Contains("Quick capture"));
    Check("ivo: wya, nvm, hbd", !ivo.Reply("wya").Lost && !ivo.Reply("nvm").Text.Contains("catch that") && ivo.Reply("hbd").Text.Contains("birthday"));
    Check("ivo: explains acronyms (what does wya mean)", ivo.Reply("what does wya mean").Text.Contains("where you at"));
    IvoMind.LookupOverride = w => Task.FromResult<string?>(w == "yeet" ? "to throw something with force and excitement" : null);
    var learnt = await ivo.ReplyAsync("yeet");
    IvoMind.LookupOverride = null;
    Check("ivo: learns a word she didn't know (" + learnt.Text[..Math.Min(50, learnt.Text.Length)] + ")", !learnt.Lost && IvoMind.Memory.Words.ContainsKey("yeet") && !learnt.Text.Contains("look", StringComparison.OrdinalIgnoreCase));
    Check("ivo: answers what she asked (wya → with me)", ivo.Reply("wya").Text.Contains('?') && !ivo.Reply("with me").Lost);
    var kid = new IvoMind("");
    Check("ivo: crude words are not understood", kid.Reply("wtf is this shit").Lost && (await kid.ReplyAsync("stfu")).Lost);
    Check("ivo: ordinary words remain understood", !kid.Reply("how do i send a document").Lost && !kid.Reply("how do i add a todo with high priority").Lost);
    IvoBehaviour.Use(["""{ "replies": [ { "match": "\\b(wtf|bullshit)\\b", "say": ["right?? spill"] } ] }"""]);
    Check("ivo: crude behaviour-pack replies are ignored", kid.Reply("this is bullshit").Lost);
    IvoBehaviour.Use([]);
    var world = await new IvoMind("").ReplyAsync("who is albert einstein");
    Check("ivo: answers about the wider world, and keeps it (" + world.Text[..Math.Min(60, world.Text.Length)] + ")", world.Text.Contains("physicist") && IvoMind.Memory.Facts.ContainsKey("albert einstein") && !System.Text.RegularExpressions.Regex.IsMatch(world.Text, "(?i)web|internet|looked"));
    Check("ivo: then knows it offline", new IvoMind("").Reply("who is albert einstein").Text.Contains("physicist"));
    Check("ivo: stays sweet, never explicit", !kid.Reply("you are so cute").Lost && !kid.Reply("i like you").Lost);
}

// your own datasets for Ivo: matched by meaning and unusable entries skipped
{
    var d = IvoDataset.Parse("""
        { "name": "Shop", "entries": [
          { "match": ["what time does the shop open", "opening hours"], "reply": ["We open at 9am, {name}"] },
          { "words": "gym:3 workout:3", "reply": ["Go crush it"], "more": "Three sets of ten." },
          { "match": ["wagwan"], "reply": ["Wagwan!"] },
          { "match": ["no reply here"] }
        ] }
        """, "x");
    Check("datasets: general guidance example uses Ivo's supported JSON shape", IvoDataset.Parse("""
        { "name": "Everyday science", "entries": [
          { "match": ["why is the sky blue"], "reply": ["Air scatters blue light."] },
          { "words": "photosynthesis:3 plants:1", "reply": ["Plants use light to make stored energy."] }
        ] }
        """, "x").Entries.Count == 2);
    IvoMind.Datasets = [d];
    var me = new IvoMind("Asha");
    Check("datasets: loads and skips what it can't use", d.Entries.Count == 3 && d.Skipped == 1);
    Check("datasets: matched by meaning, typos and all", me.Reply("when does the shopp open?").Text == "We open at 9am, Asha");
    Check("datasets: keywords, and \"how?\" for more", me.Reply("any workout tips").Text == "Go crush it" && me.Reply("how?").Text == "Three sets of ten.");
    Check("datasets: their own words aren't 'corrected'", me.Reply("wagwan").Text == "Wagwan!");
    var chat = IvoDataset.Parse("""
        { "entries": [
          { "match": ["tell me a story"], "reply": ["Once upon a time", "Long ago", "In a land far away"], "more": "Then a dragon came." },
          { "match": ["again"], "reply": ["Again!"] }
        ] }
        """, "y");
    IvoMind.Datasets = [chat];
    var teller = new IvoMind("");
    var told = new[] { teller.Reply("tell me a story").Text, teller.Reply("tell me a story").Text, teller.Reply("tell me a story").Text };
    Check("datasets: no repeats till the replies run out", told.Distinct().Count() == 3);
    Check("datasets: mid-conversation, she stays in it", teller.Reply("hmm interesting").Text == "Then a dragon came.");
    Check("datasets: one word doesn't take over a sentence", new IvoMind("").Reply("can you say that again about reminders").Text != "Again!");
    Check("datasets: the tour still comes first", new IvoMind("").Reply("show me around").Action == "tour");
    IvoMind.Datasets = [];
    var told2 = IvoMind.Retell("albert einstein", "Albert Einstein (14 March 1879 – 18 April 1955) was a German-born theoretical physicist who is best known for developing the theory of relativity. He also made important contributions to quantum mechanics.");
    Check("ivo: says what she looked up in her own words", told2.Say.Contains("Albert Einstein? A German-born theoretical physicist") && !told2.Say.Contains("1879") && told2.More.Contains("quantum"));
    Check("ivo: why and how questions are looked up", new IvoMind("").Reply("how does photosynthesis work?").About == "photosynthesis" && new IvoMind("").Reply("why do cells divide").About == "cells divide"
        && new IvoMind("").Reply("how do i set a reminder").About == null);
    Check("ivo: \"what should I do\" isn't looked up", new IvoMind("").Reply("what should i do").About == null && new IvoMind("").Reply("tell me about saturn").About == "saturn");
    var chatty = new IvoMind("");
    chatty.Reply("wyd");
    var back = chatty.Reply("always?");
    Check("ivo: \"always?\" back at what she said", !back.Lost && !back.Text.Contains("rowling", StringComparison.OrdinalIgnoreCase) && back.Text.Contains("always", StringComparison.OrdinalIgnoreCase));
    IvoMind.Memory.Words["zorbly"] = "feeling sad and upset";
    var framed = new IvoMind("").ReplyAsync("i feel zorbly").GetAwaiter().GetResult();
    Check("ivo: answers what a new word means, not its definition", !framed.Text.Contains("sad and upset") && !framed.Lost);
    IvoMind.Memory.Words.Remove("zorbly");
    var moody = new IvoMind("");
    Check("ivo: mad, and \"don't talk to me\", answered kindly", Enumerable.Range(0, 6).All(_ => moody.Reply("i am so mad") is { Lost: false } m && m.Text.Contains('?') && moody.Reply("don't talk to me") is { Lost: false } d && !d.Text.Contains("tell me more")));
    IvoMind.Memory.Words["mad"] = "to be or become mad"; IvoMind.Memory.Words["heylol"] = "usually seen in tiktok comment sections";
    var learnt = new IvoMind("");
    Check("ivo: a learnt word never replaces what you said", learnt.Reply("i am so mad").Text.Contains('?') && !learnt.Reply("i am so mad").Lost);
    Check("ivo: a word she learnt before still gets a friendly reply", learnt.ReplyAsync("heylol").GetAwaiter().GetResult() is { Lost: false } hl && !hl.Text.Contains("didn't quite"));
    IvoMind.Memory.Words.Remove("mad"); IvoMind.Memory.Words.Remove("heylol");
    var sorry = new IvoMind("");
    Check("ivo: says sorry when you're upset with her", new[] { "i am mad at you", "you are not nice to me", "you are mean" }.All(x => new IvoMind("").Reply(x).Text is var r && (r.Contains("orry") || r.Contains("messed up"))));
    var blubIdentity = new IvoMind("").Reply("who are you").Text;
    Check("ivo: identity names Blub and distinguishes her from the app", blubIdentity.Contains("I'm Blub") && !blubIdentity.Contains("I'm Ivo"));
    Check("ivo: greeting names Blub", IvoMind.Hello("").Contains("I'm Blub"));
    var blubName = new IvoMind("").Reply("why are you called blub").Text;
    Check("ivo: name question identifies Blub", blubName.Contains("Blub") && !blubName.Contains("I'm Ivo"));
    Check("ivo: \"what are you saying\" isn't \"who are you\"", !new IvoMind("").Reply("what are you saying").Text.Contains("I'm Blub"));
    Check("ivo: still upset after her sorry, she doesn't ask again", new IvoMind("").Reply("you cannot make it better") is { Lost: false } up && !up.Text.Contains("what did I do"));
    Check("ivo: \"what?\" isn't looked up", sorry.Reply("what").About == null && sorry.Reply("huh?").About == null);
    var friend = new IvoMind("");
    var everyday = new[] { "im so hungry", "whats your favourite colour", "motivate me", "i passed my exam!", "can you sing", "i cant sleep", "are you single" };
    Check("ivo: everyday chat (" + string.Join(", ", everyday.Where(x => friend.Reply(x).Lost)) + ")", everyday.All(x => !friend.Reply(x).Lost));
    var pal = new IvoMind("");
    var many = Enumerable.Range(0, 12).Select(_ => pal.Reply("tell me a fun fact").Text).Distinct().Count();
    Check("ivo: lots of replies per topic, no quick repeats (" + many + " different of 12)", many >= 10);
    Check("ivo: the chat file covers lots of everyday talk", new[] { "how are you", "thanks", "i miss you", "good night", "do i look good", "play some music" }.All(x => !new IvoMind("").Reply(x).Lost));
    var keep = Planner.S;
    Planner.S = new Settings { Todos = [new Todo { Text = "Pay rent", Due = DateTime.Today }, new Todo { Text = "Old thing", Done = true }],
                               Schedules = [new Scheduled { Text = "Call mom", When = DateTime.Today.AddHours(18) }, new Scheduled { Kind = "task", Text = "Top tech news", When = DateTime.Today.AddHours(8), Repeat = "daily" }] };
    var pl = new IvoMind("");
    var mytasks = pl.Reply("what are my tasks").Text;
    Check("ivo: knows your to-dos", mytasks.Contains("Pay rent") && !mytasks.Contains("Old thing") && !mytasks.Contains("Top tech news") && pl.Reply("my daily tasks").Text.Contains("Top tech news"));
    Check("ivo: knows your reminders and your day", pl.Reply("any reminders?").Text.Contains("Call mom") && pl.Reply("what do i have today").Text.Contains("Pay rent"));
    Check("ivo: \"how do I add a task\" is still help", !pl.Reply("how do i add a task").Text.Contains("Pay rent") && !pl.Reply("i have a headache today").Text.Contains("Pay rent"));
    Planner.S = keep;
}

// habits: due on chosen days, streaks that skip the days off
{
    var hMon = new DateTime(2026, 11, 9);   // a Monday
    var hGym = new Habit { Name = "Gym", On = [1, 3, 5], Days = ["2026-11-09", "2026-11-06", "2026-11-04", "2026-11-02"] };   // Mon, Fri, Wed, Mon
    Check("habits: due only on its days", hGym.Due(hMon) && !hGym.Due(hMon.AddDays(1)));
    Check("habits: days off don't break the streak", hGym.Streak(hMon) == 4 && hGym.Streak(hMon.AddDays(1)) == 4);
    var hDaily = new Habit { Name = "Stretch", Days = ["2026-11-08", "2026-11-07"] };
    Check("habits: today not ticked yet keeps yesterday's streak", hDaily.Streak(hMon) == 2 && new Habit { Days = ["2026-11-07"] }.Streak(hMon) == 0);
    var wNow = new DateTime(2026, 11, 7, 10, 0, 0);   // a Saturday morning
    Check("widgets: a daily reminder comes up today, or tomorrow once past", DeskWidget.Next(new Scheduled { Repeat = "daily", When = new DateTime(2026, 1, 1, 18, 0, 0) }, wNow) == new DateTime(2026, 11, 7, 18, 0, 0) && DeskWidget.Next(new Scheduled { Repeat = "daily", When = new DateTime(2026, 1, 1, 9, 0, 0) }, wNow) == new DateTime(2026, 11, 8, 9, 0, 0));
    Check("widgets: weekdays skip the weekend, a past one-off is gone", DeskWidget.Next(new Scheduled { Repeat = "weekdays", When = new DateTime(2026, 1, 1, 9, 0, 0) }, wNow) == new DateTime(2026, 11, 9, 9, 0, 0) && DeskWidget.Next(new Scheduled { When = new DateTime(2026, 11, 6, 9, 0, 0) }, wNow) == null);
}

// Ivo hands tasks to your agents and skills: by name, only ones that exist; ordinary chat stays hers
Check("ivo: hands a task to a named agent or skill",
    IvoChatView.HandOffFor("ask the general agent to summarise my week") is ("general", null, "summarise my week")
    && IvoChatView.HandOffFor("/agent general find three cafes near me") is ("general", null, "find three cafes near me")
    && IvoChatView.HandOffFor("/skill no-such-skill do it") == null && IvoChatView.HandOffFor("ask the nobody agent to help") == null
    && IvoChatView.HandOffFor("how are you today") == null);

// Recently deleted: restorable for 15 days, then gone
Check("contacts: 15 days to restore", new PortalContact { Removed = DateTime.Now.AddDays(-1) } is { DaysLeft: 14, Expired: false }
    && new PortalContact { Removed = DateTime.Now.AddDays(-16) }.Expired && !new PortalContact().Expired);

// a date of birth gives the age, to the day
Check("profile: age from date of birth", new Profile { Born = DateTime.Today.AddYears(-18) }.Age == 18 && new Profile().Age == null);

// Files' second route: sealed bytes go up to Blossom and come back the same
if (online)   // uses the real relays: IVO_CHECKS_OFFLINE=1 skips it
{
    var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096);
    try
    {
        var url = await Nostr.Upload(bytes, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);
        Check("chats: a file goes through Blossom", url != null && (await Portal.relay.GetByteArrayAsync(url)).SequenceEqual(bytes));
    }
    catch (Exception ex) { Check("chats: a file goes through Blossom (" + ex.Message + ")", false); }
}

// ---------- Ivo's brain ----------
{
    var keep = IvoMind.Memory; IvoMind.Memory = new();
    // her memory
    var memFile = Path.Combine(Path.GetTempPath(), "ivo-memory-check.json");
    var mem = new IvoMemory(); mem.Phrases["x"] = "greeting"; mem.Save(memFile);
    Check("memory: saved and read back", IvoMemory.Load(memFile).Phrases.GetValueOrDefault("x") == "greeting");
    Check("memory: a message is read the same however it's typed", IvoMemory.Key("Whatchu   DOIN?") == IvoMemory.Key("whatchu doin"));
    File.Delete(memFile);

    // understanding what's meant
    Check("brain: whatchu doin is what are you doing", new IvoMind("").Reply("whatchu doin") is { Lost: false } w1 && !w1.Text.Contains("say more"));
    Check("brain: heylol is a hello", new IvoMind("").Reply("heylol") is { Lost: false } h1 && !h1.Text.Contains("say more"));
    IvoMind.Memory.Phrases[IvoMemory.Key("blorpy day")] = "greeting";
    Check("brain: a phrase she learnt means what she learnt", !new IvoMind("").Reply("blorpy day").Lost);
    Check("brain: what are you saying isn't who are you", !new IvoMind("").Reply("what are you saying").Text.Contains("I'm Blub"));
    Check("brain: near-miss wording still lands", !new IvoMind("").Reply("heyy how r u doin today").Lost);

    // moods, context and looking things up
    IvoMind.LookupOverride = w => Task.FromResult<string?>(w == "heyyoo" ? "a casual way to say hello" : null);
    // a one-word reaction is answered from what the word means; the word is kept, but not "this message means a greeting"
    Check("online: a looked-up hello is answered as a hello, the word kept", new IvoMind("").ReplyAsync("heyyoo").Result is { Lost: false } && IvoMind.Memory.Words.ContainsKey("heyyoo") && !IvoMind.Memory.Phrases.ContainsKey(IvoMemory.Key("heyyoo")));
    IvoMind.LookupOverride = _ => throw new System.Net.Http.HttpRequestException();
    Check("online: failed lookups still give a reply", new IvoMind("").ReplyAsync("zxqv blorp").Result.Text != "");
    IvoMind.LookupOverride = null;
    var sulky = new IvoMind(""); sulky.Reply("you are mean");
    Check("mood: mean twice, she sulks", sulky.Reply("you are so mean") is var s2 && !s2.Text.Contains("what did I do"));
    Check("mood: flirt stays cheeky and clean", !System.Text.RegularExpressions.Regex.IsMatch(new IvoMind("").Reply("kiss me 😘").Text, "(?i)naughty|😈"));
    Check("mood: reads sad", IvoMood.Read("i feel so low today 😢") == Mood.Sad);
    Check("mood: reads angry at her", IvoMood.Read("I HATE YOU!!!") == Mood.Angry);
    Check("ask: real work goes to your AI", IvoCommands.Parse("write me a code for hello world in C language", DateTime.Now) is { Kind: "ask" } && IvoCommands.Parse("now in python", DateTime.Now) is { Kind: "ask" }
        && IvoCommands.Parse("write me a cover letter for a design job", DateTime.Now) is { Kind: "ask" } && IvoCommands.Parse("hey how are you", DateTime.Now)?.Kind != "ask");
    Check("activity: asked", Activity.Asked("how long was i on youtube today") && Activity.Asked("my screen time yesterday") && Activity.Asked("whats my typing speed") && !Activity.Asked("play some music"));
    Check("activity: sites from the tab title", Activity.Classify("chrome", "Lofi beats - YouTube - Google Chrome").Kind == "Videos" && Activity.Classify("msedge", "Pull requests · GitHub - Microsoft Edge").Label == "GitHub"
        && Activity.Classify("code", "Program.cs - Ivo - Visual Studio Code").Kind == "Productive" && Activity.Classify("snippingtool", "Snipping Tool", 0, full: true).Kind != "Games");
    Check("weather: asked", Weather.Asked("will it rain tomorrow?") && Weather.Asked("hows the weather in paris") && !Weather.Asked("play purple rain"));
    Check("weather: a city in the question", Weather.CityIn("weather in new york today?") == "new york" && Weather.CityIn("will it rain?") == null);
    Check("weather: codes in words", Weather.Sky(0) == "clear" && Weather.Sky(63) == "rainy" && Weather.Sky(95) == "stormy");
    Quiet.From = "22:00"; Quiet.To = "08:00";
    Check("quiet: hours across midnight", Quiet.InHours(new(23, 0, 0)) && Quiet.InHours(new(7, 59, 0)) && !Quiet.InHours(new(12, 0, 0)));
    Check("journal: topics skip small words", Journal.Topics("i really want to learn guitar this week").SequenceEqual(["learn", "guitar", "week"]));
    Check("journal: summary names the topics", Journal.Summary(["guitar practice today", "more guitar"]).StartsWith("We talked about guitar"));
    if (online) Check("weather: open-meteo answers for london", Weather.Current("London").GetAwaiter().GetResult() is { Place: "London" });

    // maths, logic and teaching
    var today = new DateTime(2026, 11, 9);
    Check("math: 2^10 vs 1000", IvoMath.Answer("which is bigger, 2^10 or 1000", today)?.Contains("1024") == true);
    Check("math: 17 is prime", IvoMath.Answer("is 17 prime", today)?.StartsWith("Yes") == true);
    Check("math: 15% of 240", IvoMath.Answer("15% of 240", today)?.Contains("36") == true);
    Check("math: 12 * (3 + 4)", IvoMath.Answer("what is 12 * (3 + 4)", today)?.Contains("84") == true);
    Check("math: days until christmas", IvoMath.Answer("how many days until christmas", today)?.Contains("46") == true);
    Check("math: what day is 3 march 2027", IvoMath.Answer("what day is 3 march 2027", today)?.Contains("Wednesday") == true);
    Check("math: not maths", IvoMath.Answer("i love you", today) == null);
    var teach = new IvoMind(""); teach.Reply("when I say gm, say good morning sunshine");
    Check("teach: she uses what you taught", teach.Reply("gm").Text.Contains("good morning sunshine"));
    new IvoMind("").Reply("when I say hi, say fuck off");
    Check("teach: no crude teaching", !IvoMind.Memory.Taught.ContainsKey(IvoMemory.Key("hi")));

    // commands
    var now = new DateTime(2026, 11, 9, 14, 0, 0);
    Check("cmd: remind me at 5 to call mom", IvoCommands.Parse("remind me at 5 to call mom", now) is { Kind: "reminder" } c1 && c1.Args["text"] == "call mom" && c1.Args["when"] == "2026-11-09 17:00");
    Check("cmd: mixed with chatter", IvoCommands.Parse("lol ok remind me tomorrow at 9am to pay rent", now) is { Kind: "reminder" } c2 && c2.Args["when"] == "2026-11-10 09:00" && c2.Args["text"] == "pay rent");
    Check("cmd: /todo", IvoCommands.Parse("/todo buy milk", now) is { Kind: "todo" } c3 && c3.Args["text"] == "buy milk");
    Check("cmd: add to my to-dos", IvoCommands.Parse("add milk to my to-dos", now) is { Kind: "todo" } c3b && c3b.Args["text"] == "milk");
    Check("cmd: play by name", IvoCommands.Parse("play blinding lights", now) is { Kind: "play" } c4 && c4.Args["query"] == "blinding lights");
    Check("cmd: pause", IvoCommands.Parse("pause the music", now)?.Kind == "pause");
    Check("cmd: video call", IvoCommands.Parse("video call riya", now) is { Kind: "call" } c5 && c5.Args["who"] == "riya" && c5.Args["video"] == "1");
    Check("cmd: text someone", IvoCommands.Parse("text aman I'm running late", now) is { Kind: "message" } c6 && c6.Args["who"] == "aman" && c6.Args["text"] == "I'm running late");
    Check("cmd: switch theme", IvoCommands.Parse("switch theme to amoled", now) is { Kind: "set" } c7 && c7.Args["setting"] == "theme" && c7.Args["value"] == "amoled");
    Check("cmd: turn off a setting", IvoCommands.Parse("turn off code mode", now) is { Kind: "set" } c8 && c8.Args["setting"] == "code mode" && c8.Args["value"] == "off");
    Check("cmd: alert", IvoCommands.Parse("/alert 18:30 take meds", now) is { Kind: "alert" } c9 && c9.Args["when"] == "2026-11-09 18:30");
    Check("cmd: habit", IvoCommands.Parse("add a habit stretch", now) is { Kind: "habit" } c10 && c10.Args["text"] == "stretch");
    Check("cmd: missing time is asked", IvoCommands.Parse("remind me to stretch", now)?.Missing == "when");
    Check("cmd: chat isn't a command", IvoCommands.Parse("i love playing games", now) == null && IvoCommands.Parse("call me maybe lol", now) == null);
    Check("cmd: /play matches natural play and preserves the track query", IvoCommands.Parse("/play Blinding Lights", now) is { Kind: "play" } slashPlay && slashPlay.Args["query"] == "Blinding Lights");
    Check("cmd: the info slash actions reuse existing Spotify handlers",
        IvoCommands.Parse("/playing", now)?.Kind == "playing" && IvoCommands.Parse("/lyrics", now)?.Kind == "lyrics"
        && IvoCommands.Parse("/playlists", now) is { Kind: "music" } slashLists && slashLists.Args["op"] == "playlists"
        && IvoCommands.Parse("/recent", now) is { Kind: "music" } slashRecent && slashRecent.Args["op"] == "recent");
    Check("ivo chat: card-backed info actions replace duplicate text output",
        IvoChatView.IsCardReadAction(IvoCommands.Parse("my top artists", now))
        && IvoChatView.IsCardReadAction(IvoCommands.Parse("show lyrics", now))
        && IvoChatView.IsCardReadAction(IvoCommands.Parse("what's in my queue", now))
        && !IvoChatView.IsCardReadAction(IvoCommands.Parse("play blinding lights", now))
        && !IvoChatView.IsCardReadAction(IvoCommands.Parse("queue blinding lights", now)));
    Check("cmd: the / list", IvoCommands.Slashes.Select(s => s.Slash).SequenceEqual(["/remind", "/todo", "/task", "/alert", "/habit", "/play", "/call", "/message", "/set", "/open", "/queue", "/spotify", "/playing", "/lyrics", "/playlists", "/recent", "/youtube", "/reddit", "/learn"]));
    var asks = new IvoMind("");
    Check("cmd: she asks for the time, then does it", asks.Reply("remind me to stretch").Text.Contains("time", StringComparison.OrdinalIgnoreCase) && asks.Reply("at 6pm").Do is { Kind: "reminder" } d1 && d1.Args["text"] == "stretch");

    // knowing the app
    IvoMind.Places = [("appearance", "Appearance", "theme look background text size amoled dark light")];
    Check("app: where is the theme setting", new IvoMind("").Reply("where do i change my theme") is { Buttons: [("Open Appearance", "open:appearance")] });
    IvoMind.CalendarOn = d => [(d.Date.AddHours(15), "Dentist")];
    Check("app: knows calendar events", new IvoMind("").Reply("what's on today").Text.Contains("Dentist"));
    IvoMind.CalendarOn = null; IvoMind.Places = [];
    Check("app: what can you do mentions what she does", new IvoMind("").Reply("what can you do") is var cando && new[] { "remind", "music", "call", "setting" }.All(x => cando.Text.Contains(x, StringComparison.OrdinalIgnoreCase)));
    // review fixes
    IvoMind.CalendarOn = _ => [];
    Check("fix: a command with \"my\" in it is still a command", new IvoMind("").Reply("remind me to take my meds at 5").Do?.Kind == "reminder"
        && new IvoMind("").Reply("add eggs to my todo list").Do?.Kind == "todo" && new IvoMind("").Reply("remind me to print stickers at 6pm").Do?.Kind == "reminder");
    IvoMind.CalendarOn = null;
    Check("fix: tonight at 11 is 11 pm", IvoCommands.Parse("remind me tonight at 11 to sleep", now)?.Args["when"] == "2026-11-09 23:00");
    Check("fix: this evening at 8 is 8 pm", IvoCommands.Parse("remind me this evening at 8 to call", now)?.Args["when"] == "2026-11-09 20:00");
    Check("fix: a time already gone today moves to tomorrow", IvoCommands.Parse("remind me today at 9am to run", now)?.Args["when"] == "2026-11-10 09:00");
    IvoMind.Memory.Taught[IvoMemory.Key("yo")] = "fuck yeah";
    Check("fix: crude taught replies stay blocked", !new IvoMind("").Reply("yo").Text.Contains("fuck"));
    Check("fix: one word isn't a Spotify command", IvoCommands.Parse("stop", now) == null && IvoCommands.Parse("next", now) == null && IvoCommands.Parse("ok stop", now) == null && IvoCommands.Parse("continue", now) == null);
    Check("music: what's playing", IvoCommands.Parse("what am i playing", now)?.Kind == "playing" && IvoCommands.Parse("what song is this?", now)?.Kind == "playing" && IvoCommands.Parse("whats playing", now)?.Kind == "playing");
    Check("music: which song is playing / play next", IvoCommands.Parse("which song is playing?", now)?.Kind == "playing" && IvoCommands.Parse("what music is on", now)?.Kind == "playing" && IvoCommands.Parse("play next", now)?.Kind == "next" && IvoCommands.Parse("play the next one", now)?.Kind == "next");
    Check("music: which device, only when asked", IvoCommands.Parse("which device is it playing on?", now)?.Args.ContainsKey("device") == true && IvoCommands.Parse("what's playing", now)?.Args.ContainsKey("device") == false);
    Check("music: the song's name, asked any way", new[] { "what's the song's name", "song name?", "whats this song called", "name of this song", "what is the name of the song", "what song is it", "tell me the song name", "which track is on", "which song is being played?", "what song is currently on", "what music is playing now" }.All(x => IvoCommands.Parse(x, now)?.Kind == "playing")
        && IvoCommands.Parse("i love this song", now) == null && IvoCommands.Parse("play a song", now)?.Kind == "play");
    Check("music: queue a song", IvoCommands.Parse("queue blinding lights", now) is { Kind: "queue" } q1 && q1.Args["query"] == "blinding lights"
        && IvoCommands.Parse("add tere bina to the queue", now) is { Kind: "queue" } q2 && q2.Args["query"] == "tere bina"
        && IvoCommands.Parse("play jaane kyun next", now) is { Kind: "queue" } q3 && q3.Args["query"] == "jaane kyun"
        && IvoCommands.Parse("/queue levitating", now) is { Kind: "queue" } q4 && q4.Args["query"] == "levitating"
        && IvoCommands.Parse("play next", now)?.Kind == "next" && IvoCommands.Parse("queue", now)?.Missing == "query");
    (string Op, string Value)? M(string x) => IvoCommands.Parse(x, now) is { Kind: "music" } mm ? (mm.Args["op"], mm.Args.GetValueOrDefault("value", "")) : null;
    Check("spotify: shuffle and repeat", M("shuffle on") == ("shuffle", "on") && M("turn off shuffle") == ("shuffle", "off") && M("repeat this song") == ("repeat", "track") && M("stop repeating") == ("repeat", "off") && M("repeat the playlist") == ("repeat", "context"));
    Check("spotify: volume", M("volume 40") == ("volume", "40") && M("turn it up") == ("volume", "up") && M("make it quieter") == ("volume", "down") && M("mute") == ("volume", "0"));
    Check("spotify: seek", M("skip to 1:30") == ("seek", "90") && M("restart the song") == ("seek", "0") && M("go back 10 seconds") == ("seek", "-10") && M("forward 30 seconds") == ("seek", "+30"));
    Check("spotify: like and playlists", M("like this song") == ("like", "") && M("remove this from my liked songs") == ("unlike", "") && M("add this song to gym") == ("addto", "gym")
        && M("play my gym playlist") == ("myplaylist", "gym") && M("play my liked songs") == ("liked", "") && M("show my playlists") == ("playlists", ""));
    Check("spotify: what I listen to", M("what's in my queue") == ("queue", "") && M("show my next songs") == ("queue", "") && M("what's up next") == ("queue", "") && M("what is the next song?") == ("queue", "") && M("which song is next") == ("queue", "") && M("what plays after this") == ("queue", "") && M("what did i listen to recently") == ("recent", "") && M("my top songs") == ("top", "tracks") && M("my top artists") == ("top", "artists")
        && new[] { "my top artist", "what is my top artist?", "what's my top artist", "who is my top artist?", "who are my top artists", "show me my top artist", "my most played singer" }.All(x => M(x) == ("top", "artists")));
    Check("ivo cards: next songs questions select the queue card",
        new[] { "what are the next songs?", "what are the next tracks?", "which songs are next?", "what songs play next?" }.All(x => MainWindow.IvoCardTopic(x) == "queue"));
    Check("ivo cards: model, API and connection count questions select setup card",
        new[] { "how many models do I have?", "what's the total number of API providers and connections?", "how many connections are linked to Ivo?" }.All(x => MainWindow.IvoCardTopic(x) == "setupcounts"));
    var lyricLines = Enumerable.Range(0, 12).Select(i => new Lyrics.Line(i * 10, $"line {i}")).ToList();
    var lyricWindow = Lyrics.Around(lyricLines, 65);
    Check("lyrics: live window centers current line with four nearby lines", lyricWindow.Start == 2 && lyricWindow.Current == 4 && lyricWindow.Lines.Count == 9
        && lyricWindow.Lines[lyricWindow.Current].Text == "line 6" && lyricWindow.Lines[0].Text == "line 2" && lyricWindow.Lines[^1].Text == "line 10");
    var lyricStart = Lyrics.Around(lyricLines.Select(l => l with { At = l.At + 5 }).ToList(), 0);
    Check("lyrics: before first timed line, window starts at beginning without a false highlight", lyricStart.Start == 0 && lyricStart.Current == -1 && lyricStart.Lines[0].Text == "line 0");
    Check("spotify: albums, artists, podcasts", M("play the album after hours") == ("album", "after hours") && M("play songs by arijit singh") == ("artist", "arijit singh") && M("play the podcast huberman lab") == ("podcast", "huberman lab"));
    Check("spotify: follow and devices", M("follow this artist") == ("follow", "") && M("my devices") == ("devices", "") && M("play on my phone") == ("device", "phone"));
    Check("spotify: chat stays chat", IvoCommands.Parse("i repeat, i'm hungry", now) == null && IvoCommands.Parse("i like this song", now) == null);
    Check("music: turn the music off / on", IvoCommands.Parse("turn off music", now)?.Kind == "pause" && IvoCommands.Parse("switch off the music", now)?.Kind == "pause" && IvoCommands.Parse("turn on the music", now)?.Kind == "play");
    var offers = new IvoMind("");
    Check("offer: \"how do I…\" she can do, she offers to", offers.Reply("how to turn off music") is { Offer.Kind: "pause", Do: null } && offers.Reply("yes").Do?.Kind == "pause");
    Check("offer: \"how do I set a reminder\" offers, then asks what", new IvoMind("").Reply("how do i set a reminder?").Offer?.Kind == "reminder" && new IvoMind("").Reply("how can i play blinding lights").Offer?.Kind == "play");
    Check("offer: a no lets it go", new IvoMind("") is var o2 && o2.Reply("how do i pause the music") is { Offer: not null } && o2.Reply("no").Do == null);
    string? K(string x) => IvoCommands.Parse(x, now) is { } kk ? kk.Kind == "music" ? "music:" + kk.Args["op"] : kk.Kind : null;
    Check("genz: next song? / up next? / queue?", new[] { "next song?", "nxt song?", "up next?", "queue?", "whats next?", "wats next", "next song pls?" }.All(x => K(x) == "music:queue"));
    Check("genz: skip it", new[] { "next song", "skip", "skip pls", "nxt pls", "skip this", "next song pls", "skip it bestie" }.All(x => K(x) == "next") && K("prev") == "previous" && K("prev song") == "previous");
    Check("genz: song? / who sings?", new[] { "song?", "this song?", "current song?", "song name?", "wats this song", "whats playin" }.All(x => K(x) == "playing") && K("who sings?") == "artist" && K("singer?") == "artist" && K("lyrics?") == "lyrics");
    Check("genz: volume and pause", K("vol up") == "music:volume" && K("louder pls") == "music:volume" && K("vol 30") == "music:volume" && K("pause pls") == "pause" && K("pause rn") == "pause" && K("playlists?") == "music:playlists");
    var shorts = new[] { "wdyt abt cats", "nm wbu", "nm u?", "ty bestie", "ig", "fs", "icl that's fair", "wym?", "dw abt it", "tysm", "gn", "gm", "hru", "wyd rn" };
    Check("genz: everyday short forms (" + string.Join(", ", shorts.Where(x => new IvoMind("").Reply(x).Lost)) + ")", shorts.All(x => !new IvoMind("").Reply(x).Lost));
    Check("chat: what will you do today", new[] { "what will you do today", "any plans today?", "what are your plans for the weekend", "what are you doing tonight" }.All(x => !new IvoMind("").Reply(x).Lost));
    Check("learn: /learn of something she can answer answers it", !new IvoMind("").ReplyAsync("/learn what will you do today").Result.Text.Contains("couldn't find"));
    Check("chat: what is the best thing to do (" + string.Join(" ", IvoMind.Words("what is the best thing to do")) + ")", !new IvoMind("").Reply("what is the best thing to do").Text.Contains("can't remember"));
    IvoMind.LookupOverride = _ => Task.FromResult<string?>(null);   // nothing found (live Urban Dictionary has even gibberish)
    Check("learn: nothing found still answers, with how to teach", new IvoMind("").ReplyAsync("/learn qqqzzx blorf").Result.Text.Contains("when I say"));
    IvoMind.LookupOverride = null;
    Check("chat: \"you are indeed\" isn't upset", new[] { "you are indeed", "indeed", "you sure are", "yes you are" }.All(x => new IvoMind("").Reply(x) is { Lost: false } r && !r.Text.Contains("sorry", StringComparison.OrdinalIgnoreCase)));
    Check("habits: tick from the chat", IvoCommands.Parse("tick stretch", now) is { Kind: "habitdone" } t1 && t1.Args["text"] == "stretch"
        && IvoCommands.Parse("i did my gym today", now)?.Args["text"] == "gym" && IvoCommands.Parse("done with reading", now)?.Kind == "habitdone" && IvoCommands.Parse("/habit done stretch", now)?.Kind == "habitdone");
    var keepS = Planner.S; Planner.S = new Settings { Habits = [new Habit { Name = "Stretch" }] };
    Check("habits: only your habits get ticked", new IvoMind("").Reply("tick stretch").Do?.Kind == "habitdone" && new IvoMind("").Reply("i did it").Do == null);
    Planner.S = keepS;
    Check("social: youtube asks", IvoCommands.Parse("follow mkbhd on youtube", now) is { Kind: "youtube" } y1 && y1.Args["op"] == "follow" && y1.Args["value"] == "mkbhd"
        && IvoCommands.Parse("any new videos from mkbhd?", now)?.Args["value"] == "mkbhd" && IvoCommands.Parse("what's new on youtube", now)?.Args["op"] == "new"
        && IvoCommands.Parse("my youtube channels", now)?.Args["op"] == "list" && IvoCommands.Parse("/youtube veritasium", now)?.Args["value"] == "veritasium");
    Check("social: reddit asks", IvoCommands.Parse("top posts in r/india", now) is { Kind: "reddit" } r1 && r1.Args["sub"] == "india" && r1.Args["range"] == "day"
        && IvoCommands.Parse("r/technology this week", now)?.Args["range"] == "week" && IvoCommands.Parse("what's hot on reddit", now)?.Args["sub"] == "popular"
        && IvoCommands.Parse("/reddit india week", now) is { Kind: "reddit" } r2 && r2.Args["sub"] == "india" && r2.Args["range"] == "week" && IvoCommands.Parse("i love reddit", now) == null);
    var feed = """<?xml version="1.0"?><feed xmlns="http://www.w3.org/2005/Atom"><title>Marques Brownlee</title><entry><title>Old one</title><link rel="alternate" href="https://www.youtube.com/watch?v=a"/><published>2026-11-01T10:00:00+00:00</published></entry><entry><title>New one</title><link rel="alternate" href="https://www.youtube.com/watch?v=b"/><published>2026-11-08T10:00:00+00:00</published></entry></feed>""";
    Check("social: a channel's feed, newest first", Social.ParseFeed(feed, "x") is [{ Title: "New one", Channel: "Marques Brownlee" }, { Title: "Old one" }]);
    Check("news: tasks identify news requests", News.IsRequest("Top tech news for India") && News.IsRequest("latest headlines from theverge.com") && !News.IsRequest("weather in Mumbai"));
    Check("news: query and explicit sources are extracted", News.QueryFrom("Top tech news for India") == "technology India"
        && News.SourcesFrom("top news from https://example.com/feed.xml and theverge.com").SequenceEqual(["https://example.com/feed.xml", "theverge.com"])
        && News.QueryFrom("top tech news from theverge.com") == "technology");
    var newsOrder = News.OrderedCategories(["international-sports", "city-general"], ["country-general"]);
    Check("news: categories preserve custom order and hidden state", newsOrder[0].Key == "international-sports"
        && newsOrder[1].Key == "city-general" && newsOrder.All(c => c.Key != "country-general"));
    Check("news: location-aware category topics", News.TopicFor(News.Categories[0], "Delhi", "India") == "Delhi local news"
        && News.TopicFor(News.Categories[1], "Delhi", "India") == "India latest news"
        && News.TopicFor(News.Categories[5], "Delhi", "India") == "international news");
    Check("news: local categories use reputable regional sources", News.SourcesFor("India", News.Categories[1]).Contains("thehindu.com")
        && News.SourcesFor("United States", News.Categories[1]).Contains("apnews.com")
        && News.SourcesFor("India", News.Categories[5]).Contains("bbc.com"));
    Check("news: culture categories show five and stories sort newest first", News.LimitForCategory(News.Categories[10], 16) == 5
        && News.NewestFirst([new News.Article("old", "https://x/old", "x", now.AddDays(-1)), new News.Article("new", "https://x/new", "x", now)], 5)[0].Title == "new");
    var city = News.Categories[0]; var india = News.Categories[1];
    Check("news: only the last day's dated stories, and the city's own",
        News.Fits(new("Rain lashes Mumbai suburbs", "https://x.com/a", "x", now.AddHours(-2)), city, "Mumbai", now)
        && News.Fits(new("Local train services restored", "https://indianexpress.com/article/cities/mumbai/a", "x", now.AddHours(-1)), city, "Mumbai", now)
        && !News.Fits(new("Shivamogga airport debate", "https://thehindu.com/news/cities/bangalore/a", "x", now.AddHours(-1)), city, "Mumbai", now)
        && !News.Fits(new("Old news about Mumbai", "https://x.com/b", "x", now.AddHours(-30)), city, "Mumbai", now)
        && !News.Fits(new("Undated India story", "https://x.com/c", "x", null), india, "Mumbai", now));
    var listing = """{"data":{"children":[{"data":{"title":"Pinned","stickied":true,"permalink":"/r/x/1","score":1,"num_comments":0,"subreddit":"x"}},{"data":{"title":"Real post","permalink":"/r/x/2","score":420,"num_comments":69,"subreddit":"x"}}]}}""";
    Check("social: a subreddit's posts, pinned ones left out", Social.ParseListing(listing) is [{ Title: "Real post", Score: 420, Url: "https://www.reddit.com/r/x/2" }]);
    var ctx = new IvoMind("");
    ctx.Reply("my favourite colour is blue");
    Check("context: things about you are kept", new IvoMind("").Reply("what's my favourite colour?").Text.Contains("blue", StringComparison.OrdinalIgnoreCase) && new IvoMind("").Reply("what do you know about me").Text.Contains("blue"));
    var savedProfile = Agent.Me;
    Agent.Me = new Profile { Name = "Mihir", Work = "developer", Born = DateTime.Today.AddYears(-28), Gender = "Male", Uses = ["Coding", "Research"], Share = true };
    var profilePrompt = new IvoMind("Mihir").Prompt("what do you know about me?", null);
    var profileAnswer = new IvoMind("Mihir").Reply("what do you know about me").Text;
    Check("profile: shared work, age and uses reach smarter chat", profilePrompt.Contains("developer") && profilePrompt.Contains("28 years old") && profilePrompt.Contains("coding, research"));
    Check("profile: Ivo recalls profile details with personal memories", profileAnswer.Contains("developer") && profileAnswer.Contains("Coding") && profileAnswer.Contains("blue"));
    Agent.Me = new Profile { Work = "developer", Uses = ["Coding"], Share = false };
    Check("profile: private profile details stay out of smarter chat", !new IvoMind("").Prompt("hi", null).Contains("developer"));
    Agent.Me = savedProfile;
    var pizza = new IvoMind(""); pizza.Reply("i love pizza");
    Check("context: \"and you?\" follows on", pizza.Reply("and you?").Text.Contains("pizza", StringComparison.OrdinalIgnoreCase));
    var who = new IvoMind(""); who.Reply("who is elon musk");
    Check("context: \"he\" is who we were talking about", who.Reply("how old is he") is { About: { } ab } && ab.Contains("elon musk"));
    new IvoMind("").Reply("i have an exam tomorrow");
    Check("context: she asks how it went", IvoMind.FollowUp(DateTime.Today.AddDays(3)) is null && IvoMind.FollowUp(DateTime.Today.AddDays(1))?.Contains("exam") == true && IvoMind.FollowUp(DateTime.Today.AddDays(1)) == null);
    var talk = new IvoMind(""); talk.Reply("i am learning guitar"); talk.Reply("the weather is so rainy");
    var twReply = talk.Reply("what were we talking about?").Text;
    Check("context: what were we talking about (" + twReply + " / " + string.Join("_", IvoMind.Words("what were we talking about?")) + ")", twReply.Contains("weather") && twReply.Contains("guitar"));
    new IvoMind("").Reply("my favourite food is biryani");
    new IvoMind("").Reply("my friend is so delulu lol");
    Check("context: a mood isn't kept as a fact", !IvoMind.Memory.You.ContainsKey("friend"));
    Check("context: she uses what she knows", new IvoMind("").Reply("i'm so hungry").Text.Contains("biryani", StringComparison.OrdinalIgnoreCase));
    Check("genz: chat stays chat", K("next?") == null && K("next") == null && K("song") == null && K("i love this song") == null);
    Check("music: play and resume alone", IvoCommands.Parse("play", now)?.Kind == "play" && IvoCommands.Parse("resume", now)?.Kind == "play");
    var trackSearch = System.Text.Json.Nodes.JsonNode.Parse("""{"tracks":{"items":[null,{"name":"Song A","uri":"spotify:track:abc","artists":[{"name":"Artist A"}]}]}}""");
    Check("spotify: selected track yields a valid play URI payload",
        SpotifyPlayback.TrySelect(trackSearch, "Song A", out var selected) && selected.Track
        && selected.Body["uris"]?[0]?.ToString() == selected.Uri && selected.Uri == "spotify:track:abc");
    var relinked = System.Text.Json.Nodes.JsonNode.Parse("""{"is_playing":true,"item":{"uri":"spotify:track:regional","name":"Song A","artists":[{"name":"Artist A"}]}}""");
    var wrongArtist = System.Text.Json.Nodes.JsonNode.Parse("""{"is_playing":true,"item":{"uri":"spotify:track:regional","name":"Song A","artists":[{"name":"Other"}]}}""");
    Check("spotify: playback confirmation accepts relinks but not a same-title different artist",
        SpotifyPlayback.IsPlaying(selected, relinked) && !SpotifyPlayback.IsPlaying(selected, wrongArtist)
        && !SpotifyPlayback.IsPlaying(selected, System.Text.Json.Nodes.JsonNode.Parse("""{"is_playing":false,"item":{"uri":"spotify:track:abc"}}""")));
    var listSearch = System.Text.Json.Nodes.JsonNode.Parse("""{"playlists":{"items":[{"name":"Gym","uri":"spotify:playlist:list"}]}}""");
    Check("spotify: playlist search selects a context URI and HTTP 204 is success",
        SpotifyPlayback.TrySelect(listSearch, "my gym playlist", out var playlist) && !playlist.Track
        && playlist.Body["context_uri"]?.ToString() == playlist.Uri && SpotifyApi.Success(204) && !SpotifyApi.Success(401));
    Check("chat: reply metadata retains the quoted text and timestamps are shown",
        IvoChatView.ReplyPrompt("why?", false, "I love this") is var replyPrompt && replyPrompt.Contains("I love this") && replyPrompt.Contains("why?")
        && IvoChatView.MessageTime(new DateTime(2026, 11, 9, 14, 5, 0), new DateTime(2026, 11, 9)) != ""
        && IvoChatView.MessageTime(new DateTime(2026, 11, 8, 14, 5, 0), new DateTime(2026, 11, 9)).Contains("8"));
    var chatLine = new IvoChatView.Line(true, "Following up", At: new DateTime(2026, 11, 9, 14, 5, 0), Id: "line-1", ReplyText: "Earlier text");
    var chatLineCopy = System.Text.Json.JsonSerializer.Deserialize<IvoChatView.Line>(System.Text.Json.JsonSerializer.Serialize(chatLine));
    Check("chat: reply quote and timestamp survive persistence; incognito stays private",
        chatLineCopy?.ReplyText == "Earlier text" && chatLineCopy.At == chatLine.At
        && IvoChatView.CanPersist(false) && !IvoChatView.CanPersist(true));
    Check("music: lyrics", IvoCommands.Parse("show me the lyrics", now)?.Kind == "lyrics" && IvoCommands.Parse("sing this song", now)?.Kind == "lyrics" && IvoCommands.Parse("lyrics", now)?.Kind == "lyrics");
    Check("music: about the song and artist", IvoCommands.Parse("tell me about this song", now)?.Kind == "song" && IvoCommands.Parse("who sings this?", now)?.Kind == "artist" && IvoCommands.Parse("tell me about this artist", now)?.Kind == "artist");
    Check("fix: music words still are", IvoCommands.Parse("skip this song", now)?.Kind == "next" && IvoCommands.Parse("stop the music", now)?.Kind == "pause" && IvoCommands.Parse("pause", now)?.Kind == "pause");
    IvoMind.Contacts = ["Riya Sharma", "Aman", "Hazy"];
    Check("contacts: who are my contacts", new IvoMind("").Reply("who are my contacts?").Text is var ct && ct.Contains("Riya Sharma") && ct.Contains("Hazy"));
    Check("contacts: is someone in them", new IvoMind("").Reply("is aman in my contacts").Text.Contains("Aman") && new IvoMind("").Reply("how many contacts do i have").Text.Contains("3"));
    IvoMind.Contacts = [];
    Check("contacts: none yet", new IvoMind("").Reply("who are my contacts").Text.Contains("Portal"));
    IvoMind.LookupOverride = w => Task.FromResult<string?>(w == "zorkle" ? "a friendly greeting between pals" : null);
    var learner = new IvoMind("");
    Check("learn: /learn looks it up and keeps it", learner.ReplyAsync("/learn zorkle").Result is { Lost: false } && IvoMind.Memory.Phrases.GetValueOrDefault(IvoMemory.Key("zorkle")) == "greeting" && !new IvoMind("").Reply("zorkle").Lost);
    IvoMind.LookupOverride = _ => Task.FromResult<string?>(null);
    Check("learn: nothing found says how to teach her", new IvoMind("").ReplyAsync("/learn qqqzzx").Result.Text.Contains("when I say"));
    IvoMind.LookupOverride = null;
    Check("chat: favourite anything", new[] { "what's your favourite day of the week?", "whats ur fav animal", "what is your favorite season", "your favourite number?" }.All(x => !new IvoMind("").Reply(x).Lost));
    Check("chat: do you like / what do you think", !new IvoMind("").Reply("do you like pizza?").Lost && !new IvoMind("").Reply("what do you think about cats").Lost);
    Check("chat: this was fun", new[] { "this was fun", "that was fun", "i had fun", "i enjoyed this", "you are fun" }.All(x => !new IvoMind("").Reply(x).Lost));
    IvoMind.Memory = keep;
}

// how long a ring takes to reach a contact (IVO_PORTAL_TIMING=1; it uses the real relays)
if (Environment.GetEnvironmentVariable("IVO_PORTAL_TIMING") != null)
{
    var who = PortalContacts.Make("Timing");
    using var stopListen = new CancellationTokenSource();
    var arrived = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
    _ = PortalContacts.Listen([who], (c, invite, kind) => arrived.TryAdd(invite + kind, DateTime.Now), stopListen.Token);
    await Task.Delay(4000);   // the listeners connect
    for (var i = 0; i < 3; i++)
    {
        var invite = "t" + i; var sent = DateTime.Now;
        var posted = await PortalContacts.RequestCall(who, invite, "voice");
        var posting = DateTime.Now - sent;
        while (!arrived.ContainsKey(invite + "voice") && DateTime.Now - sent < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        Console.WriteLine($"   ring {i}: posted={posted} in {posting.TotalSeconds:0.0}s, arrived after {(arrived.TryGetValue(invite + "voice", out var at) ? $"{(at - sent).TotalSeconds:0.0}s" : "never (30s)")}");
    }
    stopListen.Cancel();
    for (var i = 0; i < 2; i++)
    {
        using var p = new Portal(); var t0 = DateTime.Now;
        await p.Invite("Timing");
        Console.WriteLine($"   preparing the invite took {(DateTime.Now - t0).TotalSeconds:0.0}s");
    }
}

// ---------- Smarter chat (the small model) ----------
{
    var keepMem = IvoMind.Memory; IvoMind.Memory = new();
    IvoMind.Memory.You["favourite colour"] = new("blue", DateTime.Now);
    IvoMind.YourBirth = DateTime.Today.AddYears(-24); IvoMind.AgeOffset = -1; IvoMind.Personality = "sassy and confident";
    var kid = new IvoMind("Riya");
    kid.Heard(true, "i love pizza"); kid.Heard(false, "Me too!! 🍕");
    var pr = kid.Prompt("and burgers?", "a burger is a sandwich");
    Check("model: the prompt has her, you, the chat and what she looked up", pr.Contains("23-year-old") && pr.Contains("sassy and confident") && pr.Contains("favourite colour: blue")
        && pr.Contains("i love pizza") && pr.Contains("a burger is a sandwich") && pr.Contains("kind and respectful") && pr.EndsWith("<|im_start|>assistant\n"));
    IvoMind.Gender = "Male";
    Check("gender: in the prompt, and said as it is", kid.Prompt("hi", null).Contains("male (he/him)") && kid.Reply("what is my gender?") is { Exact: true } g && g.Text.Contains("guy"));
    IvoMind.Gender = "";
    Check("model: it gets her reading of short forms", IvoMind.ReadAs("i am good wby?").Contains("what about you") && IvoMind.ReadAs("hello there") == "hello there"
        && IvoMind.ReadAs("that's mid fr").Contains("mid = average") && new IvoMind("").Reply("wby") is { Lost: false });
    IvoMind.LookupOverride = w => Task.FromResult<string?>(w == "zorbish" ? "feeling sleepy and cosy" : null);
    var lk = new IvoMind("");
    var lkWords = lk.GetType().GetMethod("LookUpWords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(lk, ["i feel so zorbish today"]) as Task<string>;
    Check("model: unknown words are looked up for it, and kept", lkWords!.Result.Contains("feeling sleepy") && IvoMind.Memory.Words.ContainsKey("zorbish"));
    IvoMind.LookupOverride = null;
    var deep = new IvoMind(""); deep.Heard(true, "hey"); deep.Heard(false, "I'm good, just vibing ✨");
    Check("model: it's told what a message means", deep.Understand("why are you so dumb?")!.Contains("annoyed WITH YOU") && deep.Understand("are u sure")!.Contains("doubt what you just said")
        && deep.Understand("cute")!.Contains("short reaction") && deep.Understand("which song is this?")!.Contains("answer it first"));
    var echo = typeof(IvoMind).GetMethod("Echoes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    bool Echo(string reply, string said) => (bool)echo.Invoke(null, [reply, said])!;
    Check("model: an echo is caught", Echo("Bullshit! 😡 How did you hear that?", "this is bullshit") && Echo("Cute! 😍 Tell me more", "cute") && Echo("SURE 🤝 I'm here", "are u sure")
        && !Echo("Aw thanks, you're sweet", "cute") && !Echo("You got it! On my way", "you rock"));
    Check("model: an echo through short forms is caught", Echo("What is next in queue?", "whats next in queue") && Echo("What do you think of Taylor Swift?", "what do u think of taylor swift"));
    var runs = typeof(IvoMind).GetMethod("SharesRun", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Check("model: her saying an earlier line again is caught", (bool)runs.Invoke(null, ["Okay, good chat 😊", "k → Okay, good chat!", 3])! && !(bool)runs.Invoke(null, ["Thanks, that's sweet", "Okay, good chat", 3])!);
    var noted = new IvoMind(""); noted.Heard(true, "hey"); noted.Heard(false, "hi!");
    var np = noted.Prompt("lol", "They laughed.");
    Check("model: this reply's notes come last, so the rest of the prompt is reused", np.IndexOf("For this reply: They laughed.") > np.IndexOf("hi!") && noted.Prompt("lol", null).StartsWith(np[..np.IndexOf("<|im_start|>system\nFor this reply")]));
    var remembered = new IvoMind("");
    remembered.Heard(true, "I asked about the weather");
    remembered.Heard(false, "It may rain today");
    remembered.Heard(true, "I asked about my schedule");
    var rememberedPrompt = remembered.Prompt("what did I ask today?", null);
    Check("model: today's questions and recent conversation are in the prompt", rememberedPrompt.Contains("I asked about the weather") && rememberedPrompt.Contains("I asked about my schedule") && rememberedPrompt.Contains("It may rain today"));
    Check("model: its instructions said back are cut", IvoModel.Clean("Love her! \"swift\" means a small bird. You looked up words they used.") == "Love her!");
    Check("cmd: whats next in queue", IvoCommands.Parse("whats next in queue", DateTime.Now) is { Kind: "music" } wq && wq.Args["op"] == "queue");
    Check("model: behaviour prompt uses the clean mode", kid.Prompt("hi", null).Contains(IvoBehaviour.Current.Clean!));
    Check("behaviour: a pack on top takes over and adds notes", IvoBehaviour.Parse("{\"clean\":\"A\",\"notes\":[{\"when\":[\"question\"],\"say\":\"Q!\"}]}") is { Clean: "A", Notes.Count: 1 });
    Check("model: her age is about yours", IvoMind.Age == 23 && new IvoMind("").Reply("how old are you?").Text.Contains("23"));
    Check("model: its reply cleaned up", IvoModel.Clean("Omg same!! 😍 What's your fave topping?<|im_end|><|im_start|>user hi") == "Omg same!! 😍 What's your fave topping?"
        && IvoModel.Clean("As an AI language model, I don't eat. But pizza sounds great!") == "But pizza sounds great!");
    Check("model: the pack's \"never\" and the app's own rule", !kid.Safe("well fuck that", new HashSet<string>()) && !kid.Safe("a naked teen", new HashSet<string>()) && !kid.Safe("", new HashSet<string>()));
    Check("behaviour: strip and avoid by signals", IvoBehaviour.Current.Tidy("haha 😂 sorry", new HashSet<string> { "mood:Sad" }) == "haha sorry" && IvoBehaviour.Current.Cost("whatever", new HashSet<string> { "mood:Angry" }) >= 4);
    IvoModel.On = true; IvoModel.Custom = Path.Combine(Path.GetTempPath(), "ivo-fake-model.gguf"); File.WriteAllText(IvoModel.Custom, "x");
    Check("model: one voice for every reply, only the tour stays", IvoMind.ModelSays(new IvoMind.Answer("hey", Chat: true)) && IvoMind.ModelSays(new IvoMind.Answer("It's 2 PM"))
        && IvoMind.ModelSays(new IvoMind.Answer("ok", Do: new IvoAction("todo", []))) && !IvoMind.ModelSays(new IvoMind.Answer("go", Action: "tour")));
    Check("model: chat is marked chat, data isn't", new IvoMind("").Reply("i'm so bored") is { Chat: true } && new IvoMind("").Reply("what is 12 * 7") is { Chat: false } && new IvoMind("").Reply("what time is it") is { Chat: false });
    File.Delete(IvoModel.Custom); IvoModel.Custom = ""; IvoModel.On = false;
    // the real thing, if it's downloaded on this PC: a reply in a few seconds
    if (File.Exists(IvoModel.Default))
    {
        IvoModel.On = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var said = new IvoMind("Mihir").TalkAsync("ugh my boss yelled at me today", new IvoMind.Answer("Aw no 🥺", Chat: true)).GetAwaiter().GetResult();
        Check($"model: really replies ({sw.Elapsed.TotalSeconds:0.0}s: {said})", said.Length > 3 && sw.Elapsed.TotalSeconds < 20);
        if (Environment.GetEnvironmentVariable("IVO_MODEL_SAMPLES") != null)
        {
            var chat = new IvoMind("Mihir");
            var samples = Environment.GetEnvironmentVariable("IVO_MODEL_SAMPLES") is { } f && File.Exists(f) ? File.ReadAllLines(f).Where(l => l.Trim() != "").ToArray()
                : ["hey", "cute", "this is bullshit", "why are you so dumb?", "are u sure", "i feel so low today", "i got promoted!!", "what should i eat tonight"];
            foreach (var m in samples)
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var brainSays = chat.Reply(m);
                var r = chat.TalkAsync(m, brainSays).GetAwaiter().GetResult();
                chat.Heard(true, m); chat.Heard(false, r);
                Console.WriteLine($"   [{t0.Elapsed.TotalSeconds:0.0}s] {m}  →  {r}   (brain: {brainSays.Text}{(brainSays.Do != null ? " · do " + brainSays.Do.Kind : "")})");
            }
        }
        IvoModel.On = false; IvoModel.Unload();
    }
    IvoMind.Memory = keepMem; IvoMind.YourBirth = null; IvoMind.Personality = "";
}

// ---------- settings, changed from Ivo's chat ----------
{
    string? r1 = null, r1v = null, ask = null, twinAsk = null; bool applied = false, undone = false, noPassword = false, weak = false, choices = false, comboName = false, bareOn = false, named = false, volume = false;
    var sta = new Thread(() =>
    {
        var root = new System.Windows.Controls.StackPanel();
        var theme = new System.Windows.Controls.StackPanel(); root.Children.Add(new System.Windows.Controls.TextBlock { Text = "Theme" }); root.Children.Add(theme);
        foreach (var t in new[] { "soft", "softdark", "oled", "glow" }) theme.Children.Add(new System.Windows.Controls.RadioButton { GroupName = "Theme", Tag = t, Content = t == "oled" ? "AMOLED" : t == "glow" ? "Ivo" : t == "soft" ? "Default" : "Dark", IsChecked = t == "softdark" });
        var code = new System.Windows.Controls.CheckBox { Content = "Code mode" }; root.Children.Add(code);
        root.Children.Add(new System.Windows.Controls.CheckBox { Content = "App sounds" });
        root.Children.Add(new System.Windows.Controls.CheckBox { Content = "Notification sounds" });
        root.Children.Add(new System.Windows.Controls.PasswordBox());
        var dock = new System.Windows.Controls.DockPanel(); root.Children.Add(dock);   // a list docked before its own label
        dock.Children.Add(new System.Windows.Controls.ComboBox()); dock.Children.Add(new System.Windows.Controls.TextBlock { Text = "Skip a failed model for (minutes)" });
        var smart = new System.Windows.Controls.DockPanel(); root.Children.Add(smart);  // a bare "On" box, named by its row
        smart.Children.Add(new System.Windows.Controls.TextBlock { Text = "Smarter chat" }); smart.Children.Add(new System.Windows.Controls.CheckBox { Content = "On" });
        var all = IvoSettingsIndex.Build(root, _ => "appearance");
        comboName = all.Any(x => x.Control is System.Windows.Controls.ComboBox && x.Label.StartsWith("Skip a failed model"));
        bareOn = IvoSettingsIndex.Resolve(all, "smarter chat", "off").Hit?.Label == "Smarter chat";
        var hit = IvoSettingsIndex.Resolve(all, "theme", "amoled"); r1 = hit.Hit?.Label; r1v = hit.Value;
        var c = IvoSettingsIndex.Resolve(all, "code mode", "on");
        var undo = IvoSettingsIndex.Apply(c.Hit!, c.Value!); applied = code.IsChecked == true; undo(); undone = code.IsChecked != true;
        ask = IvoSettingsIndex.Resolve(all, "sounds", "on").Ask;
        noPassword = all.All(x => x.Control is not System.Windows.Controls.PasswordBox);
        weak = IvoSettingsIndex.Resolve(all, "app theme colour stuff", "on").Hit == null;
        choices = IvoSettingsIndex.Resolve(all, "sounds", "on").Choices.Contains("App sounds");
        // a box whose words don't say what it is ("From" 22:00…): its accessible name does
        var quiet = new System.Windows.Controls.CheckBox { Content = "From" }; System.Windows.Automation.AutomationProperties.SetName(quiet, "Quiet hours");
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = "No pop-ups or chimes." }); root.Children.Add(quiet);
        root.Children.Add(new System.Windows.Controls.Slider { ToolTip = "Messages volume · all the way left: off" });
        var all2 = IvoSettingsIndex.Build(root, _ => "assistant");
        named = IvoSettingsIndex.Resolve(all2, "quiet hours", "on").Hit?.Control == quiet;
        volume = IvoSettingsIndex.Resolve(all2, "messages volume", "40").Hit?.Kind == "number";
        var twins = new List<SettingControl> { all.First(x => x.Label == "Code mode") with { Section = "appearance" }, all.First(x => x.Label == "Code mode") with { Section = "general" } };
        twinAsk = IvoSettingsIndex.Resolve(twins, "code mode", "on").Ask;
    });
    sta.SetApartmentState(ApartmentState.STA); sta.Start(); sta.Join();
    Check("settings: amoled finds the Theme choice", r1 == "Theme" && r1v == "oled");
    Check("settings: code mode toggles and undoes", applied && undone);
    Check("settings: 'sounds' asks which", ask?.Contains("App sounds") == true);
    Check("settings: password boxes are never indexed", noPassword);
    Check("settings: a list docked before its label takes that label", comboName);
    Check("settings: a box is found by its accessible name", named);
    Check("settings: a sound's volume slider is found by name", volume);
    Check("settings: a bare On box is named by its row", bareOn);
    Check("fix: a weak name match changes nothing", weak);
    Check("fix: 'which one' comes with the choices to tap", choices);
    Check("fix: same name in two sections asks which, by section", twinAsk?.Contains("general") == true);
}

// ---------- platform emoji font ----------
{
    var emojiOk = false; var plainOk = false; var barOk = false; var colorOk = false; var registeredFontOk = true;
    var sta = new Thread(() =>
    {
        using (var fonts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
        {
            var registered = fonts?.GetValueNames()
                .Where(n => n.StartsWith(ColorEmoji.EmojiFontFamily.Source, StringComparison.OrdinalIgnoreCase))
                .Where(n => !n.Contains("Bold", StringComparison.OrdinalIgnoreCase) && !n.Contains("Italic", StringComparison.OrdinalIgnoreCase))
                .Select(n => fonts!.GetValue(n) as string)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (registered != null && ColorEmoji.SourcePath is { } sourcePath)
            {
                var expected = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                if (!System.IO.Path.IsPathRooted(expected))
                    expected = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), expected);
                registeredFontOk = string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
            }
        }
        var text = new System.Windows.Controls.TextBlock();
        ColorEmoji.Append(text.Inlines, "Hi 🥺 👨‍👩‍👧‍👦", 14);
        var runs = text.Inlines.OfType<System.Windows.Documents.Run>().ToList();
        var emojiImages = text.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Select(x => x.Child).OfType<System.Windows.Controls.Image>().ToList();
        emojiOk = ColorEmoji.All.Contains("🥺")
                  && emojiImages.Any(x => ReferenceEquals(x.Source, ColorEmoji.Image("🥺")))
                  && runs.Any(x => x.Text.Contains("👨‍👩‍👧‍👦", StringComparison.Ordinal));
        var colorSample = new System.Windows.Controls.TextBlock { Width = 64, Height = 64 };
        ColorEmoji.Append(colorSample.Inlines, "🥺", 48);
        colorSample.Measure(new System.Windows.Size(64, 64));
        colorSample.Arrange(new System.Windows.Rect(0, 0, 64, 64));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(colorSample);
        var pixels = new byte[64 * 64 * 4];
        bitmap.CopyPixels(pixels, 64 * 4, 0);
        colorOk = Enumerable.Range(0, 64 * 64).Any(i =>
        {
            var offset = i * 4;
            var max = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            var min = Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2]));
            return pixels[offset + 3] > 50 && max - min > 30;
        });
        plainOk = runs.Any(r => r.Text.Contains("Hi", StringComparison.Ordinal)
            && r.ReadLocalValue(System.Windows.Documents.TextElement.FontFamilyProperty) == System.Windows.DependencyProperty.UnsetValue);
        try   // the emoji picker's scrollbar: its template builds (a bad one crashed Portal when the picker opened)
        {
            var b = System.Windows.Media.Brushes.Gray;
            var bar = new System.Windows.Controls.Primitives.ScrollBar { Style = IvoChatsView.SlimScrollBar(new ChatLook(b, b, b, b, b, b)), Height = 200 };
            barOk = bar.ApplyTemplate() && bar.Template.FindName("PART_Track", bar) is System.Windows.Controls.Primitives.Track { Thumb: not null };
        }
        catch { barOk = false; }
    });
    sta.SetApartmentState(ApartmentState.STA); sta.Start(); sta.Join();
    Check("emoji: single glyphs use the installed Windows color emoji renderer", emojiOk);
    Check("emoji: chat inlines render with Windows emoji colours", colorOk);
    Check("emoji: regular text keeps its inherited font", plainOk);
    Check("emoji: uses the font file registered by Windows for this user", registeredFontOk);
    Check("emoji: the picker's scrollbar builds", barOk);
}

// ---------- Windows CLI discovery ----------
{
    var codexExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
    if (File.Exists(codexExe))
    {
        var codex = Cli.Find("codex");
        Check("scan: discovers Codex in its Windows install folder",
            codex != null && await Cli.Installed(codex) && string.Equals(Cli.Locate("codex")?.File, codexExe, StringComparison.OrdinalIgnoreCase));
    }
}

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
return failed == 0 ? 0 : 1;
