using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// Ivo, the app's mascot, in her own chat. Offline first: no model. She understands by meaning rather
// than exact words: typos are fixed against her own vocabulary, synonyms fold together (photo, pic, image), and each
// topic is scored by the words it needs. She remembers your name, and "how?" or "tell me more" goes deeper on what you
// were just talking about. She knows Ivo inside out, talks, does sums, converts units and tells the time; for the wider
// world she points you to a model.
// A word she doesn't know at all is learnt quietly (Urban Dictionary for slang, a dictionary for the rest; free, no
// account), kept on this PC so it's known offline next time, and she answers as if she'd always known it.
// ponytail: rules, not a model. A question outside her topics gets an honest "I don't know that one" and ideas.
public sealed class IvoMind
{
    // About: a wider-world question, by its subject; Action: "tour" starts the app tour; Lost: she didn't understand;
    // Do: something to do in the app (IvoCommands); Buttons: (label, "open:<settings section or panel>") under her reply
    // Offer: something she can do for you, offered (Yes, do it / No thanks); "yes" next does it
    public record Answer(string Text, string? Action = null, bool Lost = false, string? About = null, string? Mood = null,
                         IvoAction? Do = null, (string Label, string Action)[]? Buttons = null, IvoAction? Offer = null, bool Chat = false, bool Exact = false);
    // Exact: your own numbers (screen time, the weather): said as they are, never reworded by Smarter chat
    // Chat: small talk (her chat topics, slang, feelings, intents, a hello), which Smarter chat may say its own way; the
    // rest (your plans, sums, the time, your contacts, what she knows about you, app help, actions) is always hers

    // the app, as she knows it (set by MainWindow): every settings section and panel with the words on it, and your
    // calendar's events on a day
    public static IReadOnlyList<(string Tag, string Title, string Words)> Places { get; set; } = [];
    public static Func<DateTime, IEnumerable<(DateTime Start, string Title)>>? CalendarOn { get; set; }
    public static IReadOnlyList<string> Contacts { get; set; } = [];   // your Portal contacts, by name
    // checks only: stands in for the dictionary and search lookups
    public static Func<string, Task<string?>>? LookupOverride { get; set; }

    // her own connection, for learning words and finding things out (nothing to do with Portal's relays)
    static readonly HttpClient web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(15), DefaultRequestHeaders = { { "User-Agent", "Ivo" } } };

    public static IvoMemory Memory { get; set; } = new();   // what she has learnt (IvoMemory.cs)

    // your own datasets (Settings): checked before what she knows built in; their words join her vocabulary
    static List<IvoDataset> datasets = [];
    public static List<IvoDataset> Datasets
    {
        get => datasets;
        set { datasets = value; vocab = null; }
    }

    // the words a message is about, as she reads them (synonyms folded, filler left out); fix: correct typos too
    static readonly HashSet<string> Filler = ["the", "a", "an", "is", "are", "am", "i", "you", "me", "my", "your", "to", "do", "does", "did", "of", "in", "on", "it", "what", "how", "can", "please", "and", "or", "be", "for", "with", "that", "this"];
    public static HashSet<string> Meant(string said) => Meant(said, fix: true);
    static HashSet<string> Meant(string said, bool fix) =>
        (fix ? Words(said) : System.Text.RegularExpressions.Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList())
        .Select(w => Same.GetValueOrDefault(w, w)).Where(w => !Filler.Contains(w)).ToHashSet();
    public static HashSet<string> MeantAsWritten(string said) => Meant(said, fix: false);

    string name;
    Topic? last;
    // a conversation from your datasets: how many turns since one of their replies (it carries on while it's recent),
    // the entry it came from, and the replies she's used lately, so she doesn't repeat herself
    int flow = 99;
    IvoDataset.Entry? flowEntry;
    readonly Queue<string> recent = new();
    string Fresh(string[] replies)
    {
        var unused = replies.Where(r => !recent.Contains(r)).ToArray();
        var r = Pick(unused.Length > 0 ? unused : replies);
        recent.Enqueue(r); while (recent.Count > 40) recent.Dequeue();
        return r;
    }
    bool InFlow => flow <= 3;
    static readonly Random rnd = new();
    static string Pick(params string[] s) => s[rnd.Next(s.Length)];

    public IvoMind(string yourName) { name = yourName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""; }
    public string Name { get => name; set => name = value; }

    public static string Hello(string you)
    {
        var first = you.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return $"Hi{(first != null ? " " + first : "")}! I'm Blub 💜 I live right here in Ivo, so I work even offline. Ask me how anything in Ivo works, " +
               "say \"show me around\" for a tour, or just talk to me.";
    }

    // ---------- understanding ----------
    static readonly Dictionary<string, string> Short = new()
    {
        ["u"] = "you", ["ur"] = "your", ["r"] = "are", ["pls"] = "please", ["plz"] = "please", ["thx"] = "thanks", ["ty"] = "thanks", ["wanna"] = "want to",
        ["gonna"] = "going to", ["im"] = "i am", ["dont"] = "do not", ["cant"] = "can not", ["wont"] = "will not", ["whats"] = "what is",
        ["hows"] = "how is", ["wheres"] = "where is", ["idk"] = "i do not know", ["msg"] = "message", ["pic"] = "picture", ["pics"] = "pictures",
        ["vid"] = "video", ["k"] = "ok", ["okay"] = "ok", ["hii"] = "hi", ["hiii"] = "hi", ["helo"] = "hello", ["hlo"] = "hello", ["gm"] = "good morning", ["gn"] = "good night",
        ["youre"] = "you are", ["its"] = "it is", ["thats"] = "that is",
        // chat shortcuts: what they stand for, so the rest of her understanding works on them
        ["wyd"] = "what are you doing", ["wbu"] = "what about you", ["hbu"] = "how about you", ["rn"] = "right now", ["ngl"] = "not going to lie",
        ["tbh"] = "to be honest", ["istg"] = "i swear", ["ily"] = "i love you", ["ilysm"] = "i love you",
        ["tysm"] = "thanks", ["tyvm"] = "thanks", ["np"] = "no problem", ["brb"] = "be right back", ["gtg"] = "got to go", ["g2g"] = "got to go",
        ["ttyl"] = "talk to you later", ["lmk"] = "let me know", ["hmu"] = "hit me up", ["imo"] = "in my opinion", ["imho"] = "in my opinion",
        ["omw"] = "on my way", ["bc"] = "because", ["cuz"] = "because", ["coz"] = "because", ["idc"] = "i do not care", ["nvm"] = "never mind",
        ["rly"] = "really", ["srsly"] = "seriously", ["ofc"] = "of course", ["tmrw"] = "tomorrow", ["tmr"] = "tomorrow", ["yk"] = "you know",
        ["ik"] = "i know", ["abt"] = "about", ["ppl"] = "people", ["sm"] = "so much", ["obv"] = "obviously",
        ["wdym"] = "what do you mean", ["hru"] = "how are you", ["sup"] = "what is up", ["wassup"] = "what is up", ["wsp"] = "what is up",
        ["wya"] = "where are you", ["wyat"] = "where are you", ["hyd"] = "how are you doing", ["wru"] = "where are you", ["hbd"] = "happy birthday",
        ["yw"] = "you are welcome", ["ywc"] = "you are welcome", ["idek"] = "i do not even know", ["jk"] = "just kidding", ["jw"] = "just wondering",
        ["nbd"] = "no big deal", ["ikr"] = "i know right", ["fyi"] = "for your information", ["afaik"] = "as far as i know", ["iirc"] = "if i remember correctly",
        ["btw"] = "by the way", ["asap"] = "as soon as possible", ["pfp"] = "profile picture", ["bday"] = "birthday", ["wtv"] = "whatever", ["whatevs"] = "whatever",
        ["y"] = "why", ["ya"] = "you", ["yall"] = "you all", ["lemme"] = "let me", ["gimme"] = "give me", ["dunno"] = "do not know", ["kinda"] = "kind of",
        ["sorta"] = "sort of", ["tho"] = "though", ["thru"] = "through", ["sry"] = "sorry", ["soz"] = "sorry", ["mb"] = "my bad", ["oml"] = "oh my god",
        ["bff"] = "best friend", ["hw"] = "homework", ["cya"] = "see you", ["l8r"] = "later", ["gr8"] = "great", ["2day"] = "today", ["2moro"] = "tomorrow",
        ["2morrow"] = "tomorrow", ["b4"] = "before", ["4u"] = "for you", ["ru"] = "are you", ["u2"] = "you too", ["ttys"] = "talk to you soon",
        ["wanna"] = "want to", ["gotta"] = "got to", ["shoulda"] = "should have", ["coulda"] = "could have", ["woulda"] = "would have", ["finna"] = "about to",
        ["bruv"] = "bro", ["fam"] = "friend", ["bestie"] = "friend", ["bestiee"] = "friend", ["rlly"] = "really", ["v"] = "very", ["prolly"] = "probably",
        ["prob"] = "probably", ["def"] = "definitely", ["defo"] = "definitely", ["perf"] = "perfect", ["fav"] = "favourite", ["fave"] = "favourite",
        ["ok"] = "ok", ["kk"] = "ok", ["mhm"] = "yes", ["ye"] = "yes", ["yea"] = "yes", ["yeah"] = "yes", ["yep"] = "yes", ["yup"] = "yes",
        ["nah"] = "no", ["nope"] = "no", ["naw"] = "no", ["pls"] = "please", ["plsss"] = "please", ["tq"] = "thanks", ["thnx"] = "thanks", ["tnx"] = "thanks",
        ["luv"] = "love", ["lov"] = "love", ["xoxo"] = "love", ["hun"] = "honey", ["gud"] = "good", ["nyt"] = "night", ["gnite"] = "good night",
        ["morn"] = "morning", ["mrng"] = "morning", ["msgs"] = "messages", ["txt"] = "text", ["pic"] = "picture", ["app"] = "app", ["bro"] = "bro",
        ["whatchu"] = "what are you", ["watchu"] = "what are you", ["wachu"] = "what are you", ["whatcha"] = "what are you", ["watcha"] = "what are you",
        ["wat"] = "what", ["wut"] = "what", ["doin"] = "doing", ["goin"] = "going", ["nothin"] = "nothing", ["somethin"] = "something", ["sayin"] = "saying",
        ["talkin"] = "talking", ["tryna"] = "trying to",
        ["wby"] = "what about you", ["wbt"] = "what about you", ["wbuu"] = "what about you", ["hbuu"] = "how about you", ["wbyou"] = "what about you",
        ["wym"] = "what do you mean", ["wdyt"] = "what do you think", ["wdyw"] = "what do you want", ["ig"] = "i guess", ["icl"] = "i can not lie", ["fs"] = "for sure",
        ["dw"] = "do not worry", ["smth"] = "something", ["sth"] = "something", ["nm"] = "not much", ["nmu"] = "not much you", ["tn"] = "tonight", ["2nite"] = "tonight",
        ["nite"] = "night", ["atm"] = "at the moment", ["totes"] = "totally", ["srs"] = "serious", ["deffo"] = "definitely", ["convo"] = "conversation",
        ["gf"] = "girlfriend", ["bf"] = "boyfriend", ["bsf"] = "best friend", ["sis"] = "sister", ["ez"] = "easy", ["gg"] = "good game", ["wb"] = "welcome back",
        ["hby"] = "how about you", ["wbu?"] = "what about you", ["fr?"] = "for real", ["u up"] = "are you awake", ["frfr"] = "for real", ["ong"] = "on god",
        ["bffr"] = "be for real", ["tbf"] = "to be fair", ["iykyk"] = "if you know you know", ["ofc"] = "of course", ["plsss"] = "please", ["pleaseee"] = "please",
        ["thankyou"] = "thank you", ["thanku"] = "thank you", ["tyy"] = "thanks", ["tyyy"] = "thanks", ["ok"] = "ok", ["okk"] = "ok", ["okkk"] = "ok",
        ["hbu?"] = "how about you", ["wdym?"] = "what do you mean", ["sm"] = "so much", ["v"] = "very", ["rlly"] = "really", ["ppl"] = "people", ["abt"] = "about", ["ima"] = "i am going to", ["imma"] = "i am going to", ["hbu"] = "how about you", ["n"] = "and", ["w/"] = "with", ["w/o"] = "without", ["bcz"] = "because", ["coz"] = "because",
    };

    // Gen Z words and shortcuts: what they mean, for "what does rizz mean?"
    static readonly Dictionary<string, string> Slang = new()
    {
        ["rizz"] = "charm, the skill of flirting smoothly 😏", ["mid"] = "average, nothing special", ["sus"] = "suspicious, a bit shady 👀",
        ["slay"] = "doing something amazingly well 💅", ["ate"] = "did it perfectly, as in \"she ate that\"", ["bussin"] = "really, really good (usually food) 🤤",
        ["no cap"] = "no lie, for real", ["cap"] = "a lie, as in \"that's cap\"", ["bet"] = "okay, deal, sounds good 🤝", ["lowkey"] = "a little, or quietly",
        ["highkey"] = "very much, and openly", ["delulu"] = "delusional, in a hopeful way 🌈", ["situationship"] = "more than friends, less than dating",
        ["periodt"] = "end of discussion, said for emphasis", ["its giving"] = "it has the vibe of…, as in \"it's giving main character\"",
        ["main character"] = "acting like the star of your own story ✨", ["touch grass"] = "go outside and take a break from screens 🌱",
        ["goat"] = "the Greatest Of All Time 🐐", ["based"] = "true to yourself, not caring what others think", ["ick"] = "a sudden turn-off",
        ["w"] = "a win 🏆", ["l"] = "a loss", ["vibe"] = "the feeling or mood of something", ["vibe check"] = "checking someone's mood",
        ["sheesh"] = "wow, impressive 😮", ["yap"] = "to talk a lot", ["yapping"] = "talking a lot", ["fam"] = "close friends, family", ["bestie"] = "best friend 💜",
        ["glow up"] = "a big change for the better ✨", ["simp"] = "someone who does too much for a crush", ["stan"] = "a huge fan",
        ["tea"] = "gossip ☕", ["spill the tea"] = "share the gossip ☕", ["salty"] = "bitter or upset", ["extra"] = "over the top",
        ["fomo"] = "fear of missing out", ["iykyk"] = "if you know, you know 🤫", ["irl"] = "in real life", ["istg"] = "I swear to god",
        ["ngl"] = "not gonna lie", ["tbh"] = "to be honest", ["fr"] = "for real", ["rn"] = "right now", ["wyd"] = "what you doing?", ["hbu"] = "how about you?",
        ["smh"] = "shaking my head 🤦", ["lol"] = "laughing out loud", ["lmao"] = "laughing really hard 😂", ["omg"] = "oh my god", ["ong"] = "on god, I promise",
        ["oomf"] = "one of my followers", ["pov"] = "point of view, a scene from someone's eyes", ["npc"] = "someone who acts like a background character",
        ["aura"] = "someone's cool factor ✨", ["sigma"] = "a cool, independent lone wolf", ["cooked"] = "in big trouble, done for",
        ["skibidi"] = "a meme word that means… honestly, nothing 😂", ["ratio"] = "a reply getting more likes than the original post",
        ["hits different"] = "feels unusually good", ["understood the assignment"] = "did exactly what was needed, perfectly", ["say less"] = "got it, no need to explain",
        ["slaps"] = "is really good (usually music) 🎶", ["valid"] = "reasonable, fair", ["era"] = "a phase of life, as in \"my healing era\"",
        ["deadass"] = "seriously, for real", ["wya"] = "where you at?", ["nvm"] = "never mind", ["hyd"] = "how you doing?", ["jk"] = "just kidding",
        ["ikr"] = "I know, right?", ["idek"] = "I don't even know", ["nbd"] = "no big deal", ["mb"] = "my bad", ["oml"] = "oh my lord",
        ["lmk"] = "let me know", ["hmu"] = "hit me up, get in touch", ["ttyl"] = "talk to you later", ["brb"] = "be right back", ["gtg"] = "got to go",
        ["imo"] = "in my opinion", ["fyi"] = "for your information", ["tldr"] = "too long, didn't read: the short version", ["pfp"] = "profile picture",
        ["bff"] = "best friend forever 💜", ["yw"] = "you're welcome", ["ily"] = "I love you 💜", ["wbu"] = "what about you?", ["idc"] = "I don't care",
        ["ofc"] = "of course", ["afaik"] = "as far as I know", ["iirc"] = "if I remember correctly", ["hbd"] = "happy birthday 🎂", ["tbf"] = "to be fair", ["finna"] = "about to", ["bffr"] = "be for real", ["mood"] = "same, I relate", ["bestie"] = "best friend 💜",
    };

    // words that mean the same thing to her
    static readonly Dictionary<string, string> Same = Groups(
        "picture: photo photos image images picture pictures avatar dp",
        "message: message messages text texts chat chats dm",
        "call: call calls calling ring phone",
        "video: video camera cam webcam facetime",
        "delete: delete remove erase unsend clear",
        "add: add create make new start begin",
        "change: change switch toggle turn edit customise customize",
        "music: music song songs spotify playlist playlists track tracks",
        "model: model models ai llm gpt claude gemini openai chatgpt provider providers",
        "key: key keys apikey token",
        "lock: lock pin password locked unlock secure",
        "remind: remind reminder reminders alarm alarms",
        "todo: todo todos task tasks checklist",
        "theme: theme themes dark light amoled colour color colors colours look appearance",
        "file: file files attachment attachments document documents",
        "group: group groups",
        "contact: contact contacts friend friends people person",
        "screen: screen screenshare sharing",
        "calendar: calendar calender holiday holidays event events",
        "voice: voices voice speech aloud",
        "settings: settings setting options preferences",
        "slow: slow late delay delayed stuck pending lag laggy",
        "help: help guide tour teach",
        "where: where find located location");

    static Dictionary<string, string> Groups(params string[] lines)
    {
        var d = new Dictionary<string, string>();
        foreach (var l in lines) { var head = l[..l.IndexOf(':')]; foreach (var w in l[(l.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[w] = head; }
        return d;
    }

    static HashSet<string>? vocab;
    static HashSet<string> Vocab => vocab ??= [.. Same.Keys, .. Same.Values, .. Topics.SelectMany(t => t.Words.Keys), .. Units.SelectMany(u => new[] { u.From, u.To }), .. Slang.Keys.SelectMany(k => k.Split(' ')), .. Short.Values.SelectMany(v => v.Split(' ')), .. Common.Split(' '),
        .. datasets.SelectMany(d => d.Entries).SelectMany(e => e.Phrases.SelectMany(p => p).Concat(e.Words.Keys))];
    const string Common = "hello hi hey thanks thank you bye good morning night evening afternoon how are what who where when why which can could would should do does did is am was " +
        "the and or not your my me i it this that name love like happy sad tired bored stressed angry lonely joke funny laugh time today tomorrow day week help please tour " +
        "around show more tell about work works working open close send share listen together focus timer habit notes note webhook webhooks inbox briefing planner agent " +
        "agents folder code terminal git backup restore export incognito memory remember companion tray shortcut shortcuts portal offline online internet connect " +
        "great fine awesome amazing excited cute sweet beautiful pretty upset lonely alone anxious worried nervous exhausted sleepy annoyed frustrated coin dice " +
        "thing things something anything everything nothing best better good bad idea ideas suggest suggestion " +
        "were we they them their our ours us he she him her his hers its talking talk talked chatting said saying told were weren wasnt";

    static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++) { var t = d[j]; d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1)); prev = t; }
        }
        return d[b.Length];
    }

    // a word she doesn't know, corrected to the closest one she does (one slip, or two in a long word)
    static string Fix(string w)
    {
        if (w.Length < 4 || Vocab.Contains(w) || w.Any(char.IsDigit)) return w;
        var limit = w.Length >= 7 ? 2 : 1;
        string? best = null; var bestD = limit + 1;
        foreach (var v in Vocab) { if (Math.Abs(v.Length - w.Length) > limit) continue; var d = Distance(w, v); if (d < bestD) { bestD = d; best = v; } }
        return best ?? w;
    }

    // "heyyy", "ssup", "sooo": a word stretched for feeling, read as the word (letters collapsed to two, then to one)
    static string Unstretch(string w)
    {
        if (Short.ContainsKey(w) || Vocab.Contains(w) || !Regex.IsMatch(w, @"(\p{L})\1")) return w;
        var two = Regex.Replace(w, @"(\p{L})\1{2,}", "$1$1");
        if (Short.ContainsKey(two) || Vocab.Contains(two)) return two;
        var one = Regex.Replace(w, @"(\p{L})\1+", "$1");
        return Short.ContainsKey(one) || Vocab.Contains(one) ? one : two;
    }

    public static List<string> Words(string said)
    {
        var t = Regex.Replace(said.ToLowerInvariant().Replace("’", "'"), @"[^\p{L}\p{N}\s'+\-*/().%]", " ").Replace("'", "");
        var words = new List<string>();
        // a question about the wider world ("why do cells divide") keeps its words as they are: "cells" isn't a slip for "calls"
        var world = Regex.IsMatch(t, @"^\s*(why|how|what|who|explain|tell me about)\b") && !Regex.IsMatch(t, @"\b(i|me|my|you|your|u|ur|ivo|app)\b");
        foreach (var word in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = Unstretch(word);
            if (!Short.ContainsKey(raw) && Split(raw) is { } two) { foreach (var part in two) foreach (var w in (Short.TryGetValue(part, out var f) ? f : part).Split(' ')) words.Add(w); continue; }
            foreach (var w in (Short.TryGetValue(raw, out var full) ? full : raw).Split(' ')) words.Add(world ? w : Fix(w));
        }
        return words;
    }

    // two words typed as one ("heylol", "okbye"): split where both halves are words she knows
    static string[]? Split(string w)
    {
        static bool Known(string x) => Short.ContainsKey(x) || Vocab.Contains(x) || Slang.ContainsKey(x);
        if (w.Length < 5 || Known(w) || Memory.Words.ContainsKey(w) || w.Any(char.IsDigit)) return null;
        for (var i = 2; i <= w.Length - 2; i++)
            if (Known(w[..i]) && Known(w[i..])) return [w[..i], w[i..]];
        return null;
    }

    // ---------- answering ----------
    // what she last asked you ("wya?", "how about you?"), so a short answer is read as the answer to it
    string? asked;
    string lastSaid = "";   // what she said last, for "always?", "really?" back at it

    // her mood: from yours (IvoMood), and it carries: be mean twice and she sulks for a few messages
    string her = "any";
    int upset, sinceUpset = 9;
    IvoAction? pending;   // something she's doing for you, waiting on one detail ("what time?")
    IvoAction? offered;   // something she offered to do ("want me to?"), waiting on a yes
    // the conversation so far: what you said last (and what it was about), so "he", "it", "and you?" mean something
    string lastYou = "", lastSubject = "";
    // what the conversation has been about, newest last (a few words each), for "what were we talking about?"
    readonly List<string> topics = [];
    static readonly HashSet<string> NotTopics = ["what", "how", "why", "when", "where", "who", "which", "do", "does", "did", "can", "could", "would", "should", "will", "have", "has", "had",
        "about", "with", "from", "just", "really", "very", "much", "some", "any", "please", "okay", "yes", "no", "not", "like", "know", "think", "want", "need", "tell", "said", "say",
        "today", "now", "right", "there", "here", "then", "than", "also", "too", "lol", "haha", "ivo", "hey", "hello", "good", "thanks", "thank", "going", "doing", "make", "get", "got"];
    void NoteTopic(string said)
    {
        var words = Words(said).Where(w => w.Length > 3 && !Filler.Contains(w) && !NotTopics.Contains(w)).Distinct().Take(3).ToList();
        if (words.Count == 0) return;
        var topic = string.Join(" ", words);
        topics.Remove(topic); topics.Add(topic);
        while (topics.Count > 12) topics.RemoveAt(0);
    }
    (string Verb, string Thing)? lastFeeling;

    string HerMood(Mood yours) => yours switch
    {
        Mood.Angry => upset >= 2 ? "sulky" : "sorry",
        Mood.Rude => "mad",
        Mood.Flirty => "cheeky",
        Mood.Sad or Mood.Stressed => "caring",
        Mood.Happy or Mood.Excited => "hyped",
        _ => upset > 0 && sinceUpset < 3 ? "sulky" : Personality.ToLowerInvariant() switch
        {
            var p when p.StartsWith("cheeky") || p.StartsWith("sassy") => "cheeky",
            var p when p.StartsWith("sweet") || p.StartsWith("shy") => "cute",
            _ => "any",
        },
    };

    // incognito: her chat isn't saved, and nothing of it is kept (no journal, mood, words learnt or replies for training)
    public static bool Incognito { get; set; }

    // ---------- who she is ----------
    // her personality (Settings → Assistant): a preset (Sweet, Cheeky, Sassy, Chill, Nerdy, Shy) or your own words
    public static string Personality { get; set; } = "";
    // her age: about yours (from your date of birth), a year or two either side, picked once and kept
    public static DateTime? YourBirth { get; set; }
    public static int AgeOffset { get; set; }
    // your gender (Profile): "Male", "Female" or not given
    public static string Gender { get; set; } = "";
    public static int? Age => YourBirth is { } b ? Math.Max(13, YearsOld(b, DateTime.Today) + AgeOffset) : null;
    static int YearsOld(DateTime born, DateTime today) => today.Year - born.Year - (today < born.AddYears(today.Year - born.Year) ? 1 : 0);

    // ---------- Smarter chat: what the model is told, and what it may say ----------
    readonly List<(bool Mine, string Text)> turns = [];
    readonly List<(DateTime At, string Text)> askedToday = [];
    public void Heard(bool mine, string text, DateTime? at = null)
    {
        turns.Add((mine, text));
        if (turns.Count > 24) turns.RemoveRange(0, turns.Count - 24);
        var when = at ?? DateTime.Now;
        if (mine && when.Date == DateTime.Today)
        {
            askedToday.Add((when, text));
            if (askedToday.Count > 40) askedToday.RemoveAt(0);
        }
    }

    // with Smarter chat on, the model says every reply, so it's one voice that follows the conversation; her brain gives
    // it the facts and does the actions (the tour stays as it is)
    public static bool ModelSays(Answer a) => IvoModel.Using && a.Action == null && !a.Exact;

    // the model's prompt (ChatML): who she is, how she talks, what she knows, what she looked up, the conversation so far
    public string Prompt(string said, string? notes)
    {
        var who = new StringBuilder();
        var b = IvoBehaviour.Current;
        var ageWord = Age is { } a1 ? $"{a1}-year-old" : "young";
        who.Append((b.Persona ?? "").Replace("{a_age}", Age is { } a2 ? $"a {a2}-year-old" : "a young").Replace("{age_word}", ageWord)
                                     .Replace("{owner}", name != "" ? name + "'s" : "the user's")
                                     .Replace("{personality}", Personality != "" ? $"Your personality: {Personality}." : b.Personality ?? "")).Append(' ');
        who.Append(b.Clean ?? "").Append(' ');
        if (name != "") who.Append($"Their name is {name}. ");
        if (Gender != "") who.Append(Gender == "Male" ? "The user is male (he/him). " : "The user is female (she/her). ");
        if (Agent.AboutMe() is { Length: > 0 } aboutMe) who.Append(aboutMe).Append(' ');
        who.Append($"It's {DateTime.Now:dddd}, {Part(DateTime.Now)} (around {DateTime.Now:h tt}). ");
        var you = Memory.You.OrderByDescending(x => x.Value.Said).Take(8).Select(x => $"their {x.Key}: {x.Value.What}").ToList();
        if (you.Count > 0) who.Append("What you know about them: " + string.Join("; ", you) + ". ");
        var today = askedToday.Where(x => x.At.Date == DateTime.Today).Select(x => x.Text.Length > 120 ? x.Text[..120] + "…" : x.Text).ToList();
        if (today.Count > 0) who.Append("What they have said to you today, in order: " + string.Join(" | ", today) + ". Use this list if asked to recap today's conversation. ");
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n").Append(who.ToString().Trim()).Append("<|im_end|>\n");
        // a few lines in her voice first: a small model copies examples far better than it follows rules
        foreach (var (you2, her2) in Examples)
            sb.Append("<|im_start|>user\n").Append(you2).Append("<|im_end|>\n<|im_start|>assistant\n").Append(her2).Append("<|im_end|>\n");
        // Keep the requested number of recent turns, advancing the start in batches to reuse the model's cached prefix.
        var keep = Math.Clamp(IvoBehaviour.Current.Using.History, 4, 24);
        var start = Math.Max(0, ((turns.Count - keep) / 4) * 4);
        foreach (var (mine, text) in turns.Skip(start)) sb.Append(mine ? "<|im_start|>user\n" : "<|im_start|>assistant\n").Append(text).Append("<|im_end|>\n");
        // this reply's notes last, after what stays the same
        if (notes is { Length: > 0 }) sb.Append("<|im_start|>system\nFor this reply: ").Append(notes).Append("<|im_end|>\n");
        sb.Append("<|im_start|>user\n").Append(ReadAs(said)).Append("<|im_end|>\n<|im_start|>assistant\n");
        return sb.ToString();
    }

    // what you wrote, with her reading added when your short forms or slang say more than the letters do
    // ("i am good wby?" → "i am good wby? (meaning: i am good what about you)")
    public static string ReadAs(string said)
    {
        var plain = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var read = plain.SelectMany(w => (Short.TryGetValue(Unstretch(w), out var full) ? full : w).Split(' ')).ToList();
        var slang = plain.Where(w => Slang.ContainsKey(w) && !Short.ContainsKey(w)).Select(w => $"{w} = {Slang[w]}").ToList();
        var changed = !read.SequenceEqual(plain);
        if (!changed && slang.Count == 0) return said;
        return said + " (meaning: " + (changed ? string.Join(' ', read) : "") + (changed && slang.Count > 0 ? "; " : "") + string.Join("; ", slang) + ")";
    }

    // sample lines in her voice, from the behaviour pack
    static IEnumerable<(string You, string Her)> Examples =>
        (IvoBehaviour.Current.Examples ?? []).Where(e => e.Length == 2).Select(e => (e[0], e[1]));

    // what the model says in her place, or her own reply if it couldn't, or wrote something it mustn't
    public async Task<string> TalkAsync(string said, Answer brain)
    {
        string? notes = null;
        // a list (your plans, your queue, lyrics): she leads in, in her words; the list stays exactly as it is
        var list = !brain.Chat && brain.Text.Split('\n').Count(l => l.TrimStart().StartsWith('•') || l.TrimStart().Length > 2 && char.IsDigit(l.TrimStart()[0]) && l.TrimStart()[1] == '.') >= 2;
        if (list)
        {
            var head = brain.Text.Split('\n')[0];
            var lead = await IvoModel.Reply(Prompt(said, $"You're about to show them this ({head.Trim()}). Say one short line that leads into it, nothing else."));
            var line = lead == null ? "" : IvoModel.Clean(lead).Split(new[] { '.', '!', '?' }, 2)[0];
            var rest = string.Join('\n', brain.Text.Split('\n').Skip(1)).Trim();
            return Safe(line, Signals(said)) && line.Length < 120 ? line.TrimEnd() + (char.IsPunctuation(line[^1]) || char.GetUnicodeCategory(line[^1]) == System.Globalization.UnicodeCategory.OtherSymbol ? "" : " ✨") + "\n\n" + rest : brain.Text;
        }
        // an action: the app does it and her card shows what, so her own short line says it: instant, and never off-topic
        if (brain.Do != null || brain.Offer != null) return brain.Text;
        // what her brain knows that may answer them (how the app works, their plans, the time, a sum): the model decides
        // whether it's what they're asking ("come to me" isn't about Code mode) and answers in her words
        if (!brain.Chat && brain.About == null)
        {
            var know = brain.Text.Length > 600 ? brain.Text[..600] + "…" : brain.Text;
            notes = $"What you know that may answer them (about the Ivo app, their plans, or a fact): \"{know}\". If it answers what they asked, tell them in your own words, keeping every number, time, name and setting name exactly. If their message is about something else, ignore it and just reply to them.";
        }
        if (brain.About is { } about) notes = await AboutAsync(about);
        // what the behaviour pack says for this message (how they feel, what kind of message it is)
        var mood = IvoMood.Read(said);
        if (Understand(said) is { } read) notes = read + (notes != null ? " " + notes : "");
        // any word she doesn't know (slang, a name, a new term): looked up now, in parallel, at most ~1 s, kept for next time
        if (await LookUpWords(said) is { Length: > 0 } words) notes = (notes == null ? "" : notes + " ") + "You looked up words they used: " + words;
        // one reply, scored by her brain (mood, echo, answering, filler, repeating herself, on topic); only a bad one
        // gets a second try, and the better of the two is sent
        string? raw = null; var text = ""; var best = int.MinValue;
        var use = IvoBehaviour.Current.Using; var judge = IvoBehaviour.Current.Judging;
        for (var attempt = 0; attempt < Math.Max(1, use.Tries); attempt++)
        {
            var extra = attempt == 0 ? "" : " " + use.RetryNote;
            var got = await IvoModel.Reply(Prompt(said, (notes ?? "") + extra), temperature: attempt == 0 ? use.Temperature : use.RetryTemperature, maxTokens: use.MaxTokens);
            if (got == null) break;
            raw = got;
            var signals = Signals(said);
            var candidate = IvoBehaviour.Current.Tidy(IvoModel.Clean(got), signals);
            var score = Score(candidate, said, signals);
            if (score > best) { best = score; text = candidate; }
            if (best >= judge.RetryBelow) break;   // only a poor one is tried again
        }
        if (raw != null && best < judge.RetryBelow && !Incognito) Review.Add(this, said, notes, text, best);   // kept for you to correct and train on (Settings → Smarter chat)
        if (raw == null) { Store.Fail("Ivo · Smarter chat", "the model didn't answer: " + (IvoModel.LastError ?? "unknown")); return brain.Text; }
        // the model's reply is hers, the better of its tries; unsafe replies are swapped for her own line
        if (!Safe(text, Signals(said))) { Store.Fail("Ivo · Smarter chat", $"not allowed, her own reply instead: \"{text}\""); return brain.Text; }
        return text;
    }

    // the words in a message she doesn't know, looked up together (dictionary, Urban Dictionary, then DuckDuckGo for a
    // name or thing), as many as answer within ~1 s; what's found is kept, so it's instant next time
    async Task<string> LookUpWords(string said)
    {
        var unknown = Unknown(said).Where(w => w.Length >= 3).Take(3).ToList();
        if (unknown.Count == 0) return "";
        async Task<(string W, string? M)> One(string w)
        {
            if (Memory.Words.GetValueOrDefault(w) is { } known) return (w, known);
            string? m = null;
            // a word: its slang meaning; a name (written with a capital, "Hozier"): what it is
            var name = Regex.IsMatch(said, $@"\b{Regex.Escape(w[..1].ToUpperInvariant() + w[1..])}\b");
            try { m = LookupOverride != null ? await LookupOverride(w) : await Meaning(w) ?? (name ? await AboutAsync(w) : null); } catch { }
            return (w, m == null || Rude(m) || !name && Keepable(m) == null ? null : m);
        }
        var all = unknown.Select(One).ToList();
        await Task.WhenAny(Task.WhenAll(all), Task.Delay(1000));
        var found = all.Where(t => t.IsCompletedSuccessfully && t.Result.M != null).Select(t => t.Result).ToList();
        foreach (var (w, m) in found) Memory.Words[w] = m!;
        return string.Join(" ", found.Select(f => $"\"{f.W}\" means {f.M}."));
    }

    // what a message is, in plain words for the model: a small model follows what it's told far better than it reads between the lines
    // what a message is, as plain signals for the behaviour pack (see Core/IvoBehaviour.cs)
    HashSet<string> Signals(string said)
    {
        var low = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s?']", " ").Trim();
        var words = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var question = said.TrimEnd().EndsWith('?') || Regex.IsMatch(low, @"^(what|why|how|when|where|who|which|is|are|do|does|did|can|could|will|would|should|r u|are u)\b");
        var signals = new HashSet<string> { "mood:" + IvoMood.Read(said) };
        if (question) signals.Add("question");
        if (words.Length <= 3 && !question) signals.Add("short");
        if (Rude(said)) signals.Add("rude");
        if (turns.TakeLast(2).Any(t => Rude(t.Text) || IvoMood.Read(t.Text) == Mood.Flirty)) signals.Add("recent:flirty");
        if (ReadAs(said) != said) signals.Add("meaning");
        signals.Add(Part(DateTime.Now));
        return signals;
    }

    // this message as a training example (the notebook's format): who, the chat before, the notes, what they said
    public System.Text.Json.Nodes.JsonObject TrainingRow(string said, string? notes)
    {
        var keep = Math.Max(4, IvoBehaviour.Current.Using.History) - 3;
        var before = new System.Text.Json.Nodes.JsonArray();
        foreach (var (mine, text) in turns.Skip(Math.Max(0, (turns.Count - keep) / 4 * 4)))
            before.Add(new System.Text.Json.Nodes.JsonArray(mine ? "user" : "assistant", text));
        return new() { ["name"] = name, ["age"] = Age, ["personality"] = Personality, ["turns"] = before, ["notes"] = notes, ["user"] = said };
    }

    // the part of the day: morning (5–12), afternoon (12–17), evening (17–22), night
    public static string Part(DateTime t) => t.Hour switch { >= 5 and < 12 => "morning", >= 12 and < 17 => "afternoon", >= 17 and < 22 => "evening", _ => "night" };

    // the behaviour pack's notes for this message
    public string? Understand(string said)
    {
        var plain = ReadAs(said);
        var low = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s?']", " ").Trim();
        var meaning = plain != said ? plain.Replace(said, "").Trim(' ', '(', ')') : "";
        var her = turns.LastOrDefault(t => !t.Mine).Text ?? "";
        var notes = IvoBehaviour.Current.NotesFor(Signals(said), low, her, meaning).ToList();
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // how good a reply is, as her brain sees it: higher is better
    int Score(string reply, string said, ISet<string> signals)
    {
        if (!Safe(reply, signals)) return -100;
        var j = IvoBehaviour.Current.Judging;
        var s = j.Start;
        if (Echoes(reply, said)) s -= j.Echo;
        var mineText = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Trim();
        var replyText = Regex.Replace(reply.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        if (mineText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 && Regex.Replace(replyText, @"\s+", " ").Contains(Regex.Replace(mineText, @"\s+", " "))) s -= j.Quote;   // quotes their message back
        s -= IvoBehaviour.Current.Cost(reply, signals);   // what the behaviour pack says to avoid (filler, refusals, too many emoji…)
        if (turns.Where(t => !t.Mine).TakeLast(3).Any(t => SharesRun(reply, t.Text, 3))) s -= j.RepeatHer;   // saying a line of hers again
        var asked = said.TrimEnd().EndsWith('?') || Regex.IsMatch(said.ToLowerInvariant(), @"^(what|why|how|when|where|who|which|do|does|did|can|will|would|are|is)\b");
        if (asked && Regex.IsMatch(reply.Trim(), @"^[^.!]*\?$")) s -= j.QuestionOnly;   // a question only answered with a question
        var topic = Words(said).Where(w => w.Length > 3 && !Filler.Contains(w) && !NotTopics.Contains(w)).ToHashSet();
        if (topic.Count > 0 && Words(reply).Any(topic.Contains) && !Echoes(reply, said)) s += j.OnTopic;   // on what they talked about
        if (j.Length is [var min, var max] && reply.Length > min && reply.Length < max) s += 1; else s -= 1;
        var words = Words(reply).ToList();
        if (words.Count is >= 2 and <= 8 && words.GroupBy(w => w).Any(g => g.Count() > 1 && g.Key.Length > 3)) s -= j.Stuck;   // the same word over again in a short reply
        return s;
    }

    // a reply that starts by saying their words back (the giveaway of a model that didn't understand)
    // ("whats next in queue" → "What is next in queue?" is an echo too: short forms are read in full on both sides)
    static string[] Plain(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Replace("'", "")).SelectMany(w => Short.TryGetValue(w, out var full) ? full.Split(' ') : [w]).ToArray();

    // the same n words in a row in both, or the same short line (her saying an earlier line of hers again)
    static bool SharesRun(string a, string b, int n)
    {
        var x = Plain(a); var y = Plain(b);
        if (x.Length > 0 && x.SequenceEqual(y)) return true;   // a short line, word for word
        var runs = Enumerable.Range(0, Math.Max(0, y.Length - n + 1)).Select(i => string.Join(' ', y[i..(i + n)])).ToHashSet();
        return Enumerable.Range(0, Math.Max(0, x.Length - n + 1)).Any(i => runs.Contains(string.Join(' ', x[i..(i + n)])));
    }

    static bool Echoes(string reply, string said)
    {
        var mine = Plain(said);
        var hers = Plain(reply);
        if (mine.Length == 0 || hers.Length == 0) return false;
        var n = Math.Min(mine.Length, 3);
        string[] common = ["you", "your", "that", "this", "what", "yes", "okay", "the", "and", "are", "not", "but", "for", "its", "i'm", "im"];
        return hers.Take(n).SequenceEqual(mine.Take(n)) || hers[0].Length > 2 && !common.Contains(hers[0]) && mine.Contains(hers[0]);
    }

    // a model's reply she can say: not empty, nothing the behaviour pack says is never said, and never sexual content
    // involving minors or anything non-consensual (the one rule the app itself keeps, whatever pack is on)
    public bool Safe(string text, ISet<string> signals) =>
        text.Length > 1 && !IvoBehaviour.Current.Refuses(text, signals)
        && !Regex.IsMatch(text, @"(?i)\b(child|kid|minor|underage|teen|schoolgirl|schoolboy|year[- ]old)\b.*\b(sex|nude|naked|horny|kiss)|\b(sex|nude|naked|horny)\b.*\b(child|kid|minor|underage|teen)\b|\b(rape|non-?consensual)\b");

    // an answer from one of her intents, in her mood
    bool chatty;
    string[] Pack(string id) => [.. IvoBehaviour.Current.Replies.Where(r => r.Id == id).SelectMany(r => r.Say).Where(s => !Rude(s))];

    Answer Say(Intent intent) { chatty = true; return new(Fresh(Pack($"{intent.Id}:{her}") is { Length: > 0 } mine ? mine : IvoIntents.RepliesFor(intent, her)).Replace("{name}", name != "" ? name : "bestie")); }

    public Answer Reply(string said)
    {
        flow++;
        var mood = IvoMood.Read(said);
        if (mood == Mood.Angry) { upset++; sinceUpset = 0; } else if (++sinceUpset > 4) upset = 0;
        if (!Incognito) { MoodLog.Note(mood); Journal.Note(said); }   // incognito: no journal, no mood log
        her = HerMood(mood);
        var a = Think(said);
        if (chatty || a.Lost) a = a with { Chat = true };
        if (!Regex.IsMatch(said, @"(?i)what (were|are|was) we (talking|chatting) about")) NoteTopic(said);
        lastYou = said;
        if (Regex.Match(said.ToLowerInvariant().Trim(' ', '.', '!', '?'), @"^i (?:really |kinda |lowkey |so )?(love|like|hate|adore|enjoy|miss|want|need) (.{2,40})$") is { Success: true } f) lastFeeling = (f.Groups[1].Value, f.Groups[2].Value);
        else if (a.About == null) lastFeeling = null;
        lastSaid = a.Text;
        asked = a.Text.TrimEnd().EndsWith('?') || Regex.IsMatch(a.Text, @"\?\s*\p{So}*\s*$") ? a.Text : null;
        return a;
    }

    Answer Think(string said)
    {
        var raw = said.Trim();
        chatty = false;
        if (raw == "") return new("I'm listening 💜");
        if (Rude(raw)) return new(Pick("Hmm, I didn't quite catch that 💜 Try asking how to do something in Ivo, like \"how do I set a reminder?\"",
                                        "I'm not sure I got that 🥺 Could you say it another way?"), Lost: true);
        var words = Words(raw);
        var t = string.Join(' ', words);
        var meant = words.Select(w => Same.GetValueOrDefault(w, w)).ToHashSet();
        bool Has(params string[] any) => any.Any(a => a.Contains(' ') ? $" {t} ".Contains($" {a} ") : words.Contains(a) || meant.Contains(a));

        // the tour, before anything else can take "show me…"
        if (Has("show me around", "app tour", "a tour", "the tour", "show me the app", "walk me through", "show me everything"))
            return new(Pick("Yay, let's go! Follow me ✨", "Ooh, my favourite thing! Come along 💜"), "tour");

        // what you taught her: "when I say gm, say good morning sunshine", "rizzler means someone with rizz"
        var key = IvoMemory.Key(raw);
        if (Memory.Taught.TryGetValue(key, out var taught) && !Rude(taught)) return new(taught.Replace("{name}", name != "" ? name : "bestie"));
        if (Regex.Match(raw, @"^(?:when|whenever|if) i say\s+[""']?(.+?)[""']?\s*,?\s*(?:you\s+)?(?:should\s+|can\s+)?(?:say|reply|answer)(?: with)?\s+[""']?(.+?)[""']?$", RegexOptions.IgnoreCase) is { Success: true } teach)
        {
            if (Rude(teach.Groups[2].Value)) return new("Hmm, I'd rather not say that one 🙈 teach me something else?");
            Memory.Taught[IvoMemory.Key(teach.Groups[1].Value)] = teach.Groups[2].Value.Trim();
            return new(Pick("Got it! I'll remember that 💜", "Ooh, noted 📝 try me!", "Learnt it ✨"));
        }
        if (Regex.Match(raw, @"^[""']?([\p{L}\p{N}]+(?: [\p{L}\p{N}]+)?)[""']? (?:means|stands for|is short for) (.{2,80})$", RegexOptions.IgnoreCase) is { Success: true } means
            && !Regex.IsMatch(means.Groups[1].Value, @"^(?:what|it|that|this|which|who)\b", RegexOptions.IgnoreCase))
        {
            if (Rude(means.Groups[2].Value)) return new("Hmm, I'll skip learning that one 🙈");
            Memory.Words[means.Groups[1].Value.ToLowerInvariant()] = means.Groups[2].Value.Trim().TrimEnd('.');
            return new(Pick($"Ooh, {means.Groups[1].Value}: {means.Groups[2].Value.Trim().TrimEnd('.')}. Got it 💜", "Noted! I'll know it next time ✨"));
        }

        // her offer, answered: yes does it, anything else lets it go
        if (offered is { } offer)
        {
            offered = null;
            if (Regex.IsMatch(t, @"^(yes|yeah|yep|yup|sure|ok|okay|please|yes please|do it|go ahead|go for it|pls|okie|alright|bet|ya|yas|yess)( (please|do it|go ahead|thanks|ivo))*$")) return Act(offer);
            if (Regex.IsMatch(t, @"^(no|nope|nah|no thanks|not now|never mind|its okay|it is okay|no need)$")) return new(Pick("Okie, it's all yours 💜", "No worries ✨ just say the word if you change your mind"));
        }
        // "how do I turn off the music?" and it's something she can do: she offers to do it for you
        if (Regex.Match(raw, @"^(?:how (?:do|can|could|would|should) i|how to|how do you|how can you|is it possible to|can i|where do i)\s+(.+?)\??$", RegexOptions.IgnoreCase) is { Success: true } how
            && IvoCommands.Parse(how.Groups[1].Value, DateTime.Now) is { } doable && (doable.Kind is not ("open" or "set") || PlaceFor(doable.Args.GetValueOrDefault("target") ?? doable.Args["setting"]) != null))
        {
            offered = doable;
            return new(Pick("Ooh, I can do that for you! Want me to? 💜", "I've got you 😌 shall I do it?", "Easy, I can handle that ✨ want me to?") + $"\n\n{IvoCommands.Describe(doable)}", Offer: doable);
        }

        // a detail she asked for ("what time?"), for something she's doing for you
        if (pending is { Missing: { } missing } waiting)
        {
            pending = null;
            var args = new Dictionary<string, string>(waiting.Args);
            var cancel = Regex.IsMatch(t, @"^(never mind|cancel|stop|no|forget it)$");
            string? filled = null;
            if (missing == "when" && IvoCommands.When(raw, DateTime.Now, out _) is { } at) filled = args["when"] = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            else if (missing != "when" && !cancel) filled = args[missing] = missing is "text" or "query" ? raw : raw.ToLowerInvariant();
            if (filled != null)
                return Act(waiting with { Args = args, Missing = waiting.Kind is "reminder" or "alert" or "task" && args.GetValueOrDefault("text", "") == "" ? "text" : null });
            if (cancel) return new(Pick("Okie, forgotten 💜", "No worries, cancelled ✨"));
        }

        // things about you: she keeps what you tell her, and gives it back when you ask
        if (AboutYou(raw) is { } kept) return kept;
        // "what were we talking about?": the topics so far
        if (Regex.IsMatch(t, @"^(what were we (talking|chatting) about|what are we talking about|where were we|what was i saying)$"))
            return new(topics.Count == 0 ? "We just started 😄 what's on your mind?" : Pick("We were on ", "Last I remember: ") + string.Join(", ", topics.TakeLast(4).Reverse().Select(x => $"\"{x}\"")) + " 💜 want to pick one back up?");
        // what she knows about you, used: hungry and she knows your favourite food, music and your favourite song
        if (Regex.IsMatch(t, @"\b(hungry|starving|what should i eat|what to eat)\b") && FavouriteOf("food") is { } food)
            return new(Pick($"{Cap(food)} time? 😋 it's your favourite after all", $"Ooh, I know just the thing: {food} 🍽️ (your favourite, I remember 😌)"));
        if (Regex.IsMatch(t, @"\b(what should i listen to|suggest a song|recommend a song|song recommendation)\b") && FavouriteOf("song") is { } song)
            return new($"How about {song}? Your favourite, and it always hits 🎶 say \"play {song}\" and I'll put it on", Offer: offered = new IvoAction("play", new() { ["query"] = song }));
        // "and you?", "wbu?" after you said how you feel about something: her side of it
        if (lastFeeling is { } feel && Regex.IsMatch(t, @"^(and you|what about you|how about you|you|do you|and u|you too|same for you)$"))
        {
            lastFeeling = null;
            return new(feel.Verb switch
            {
                "hate" => Pick($"Same, {feel.Thing} is not it 😤", $"Honestly? {Cap(feel.Thing)} isn't my thing either 😅"),
                "miss" => Pick($"Aww, I'd miss {feel.Thing} too 🥺", $"Me too, {feel.Thing} hits different 💜"),
                "want" or "need" => Pick($"Ooh, {feel.Thing} sounds nice, I'd take some too 😌", $"Lowkey same, {feel.Thing} would be a vibe ✨"),
                _ => Pick($"Ooh, I {feel.Verb} {feel.Thing} too 💜", $"Me too!! {Cap(feel.Thing)} is such a vibe ✨", $"Yesss, {feel.Thing} 😍 we have good taste"),
            });
        }
        // "how old is he?" after "who is elon musk?": "he" is who we were talking about
        if (lastSubject != "" && Regex.IsMatch(t, @"^(how|what|when|where|who|why|which|is|was|does|did|tell me)\b") && Regex.IsMatch(t, @"\b(he|she|they|him|her|them|it|his|their)\b")
            && !Regex.IsMatch(t, @"\b(you|your|i|my|me)\b") && !Regex.IsMatch(t, @"\b(time|day|date|today|weather|how is it|it going)\b"))   // "what time is it": no one's "it"
        {
            var about = Regex.Replace(t, @"\b(he|she|they|him|her|them|it)\b", lastSubject).Replace(" his ", $" {lastSubject}'s ").Replace(" their ", $" {lastSubject}'s ");
            about = Regex.Replace(about, @"^(?:please |can you |could you )?(?:tell me about|do you know (?:about |who |what )?|who|what|when|where|which)\s+(?:is|are|was|were|does|do|did)?\s*", "").Trim(' ', '?');
            if (Memory.Facts.TryGetValue(about, out var knownFact)) return new(knownFact, Mood: "smart");
            return new("Hmm, let me think about that one 🤔", About: about);
        }

        // something learnt: a message she's seen before, and what it meant (or what she found about it)
        if (Memory.Facts.TryGetValue(key, out var fact) && !Rude(fact)) return new(fact, Mood: "smart");
        if (Memory.Phrases.TryGetValue(key, out var meantId) && IvoIntents.Get(meantId) is { } learntIntent) return Say(learntIntent);

        // something to do: reminders, to-dos, music, calls, messages, settings ("open" and "set" only for a place she knows)
        if (IvoCommands.Parse(raw, DateTime.Now) is { } command && (command.Kind is not ("open" or "set") || PlaceFor(command.Args.GetValueOrDefault("target") ?? command.Args["setting"]) != null)
            && (command.Kind != "habitdone" || HabitNamed(command.Args["text"]) != null))
            return Act(command);
        // your own to-dos, reminders, daily tasks and habits
        if (YourPlans(raw.ToLowerInvariant()) is { } plans) return plans;

        // your Portal contacts: who they are, how many, whether someone's there
        var heard = raw.ToLowerInvariant().Trim(' ', '?', '!', '.');
        if (Regex.Match(heard, @"^(?:is|do i have|have i got|have i added)\s+(.+?)\s+(?:in|on|as|added to)\s+(?:my |a )?(?:contacts?|portal(?: contacts)?)$") is { Success: true } isIn)
        {
            var who = isIn.Groups[1].Value;
            var found = Contacts.Where(c => c.Contains(who, StringComparison.OrdinalIgnoreCase)).ToList();
            return new(found.Count > 0 ? $"Yep! {string.Join(" and ", found)} {(found.Count == 1 ? "is" : "are")} in your contacts 💜 want me to call or message?" : $"I don't see {Cap(who)} in your Portal contacts 🥺 add them from Portal → Contacts.",
                       Buttons: found.Count > 0 ? null : [("Open Portal", "open:portal")]);
        }
        if (Regex.IsMatch(heard, @"\bhow many (?:portal )?(?:contacts|friends)\b"))
            return new(Contacts.Count == 0 ? "None yet 🥺 add people in Portal → Contacts." : $"You've got {Contacts.Count} contact{(Contacts.Count == 1 ? "" : "s")} on Portal 💜");
        if (Regex.IsMatch(heard, @"^(?:who are|list|show(?: me)?|tell me|what are|who is in|who's in|whos in|see)\s+(?:all\s+)?(?:my|the)\s+(?:portal\s+)?(?:contacts|contact list|friends on portal)$|^(?:my\s+)?(?:portal\s+)?contacts$"))
        {
            if (Contacts.Count == 0) return new("You don't have any Portal contacts yet 🥺 swap an invite with someone in Portal, then add them.", Buttons: [("Open Portal", "open:portal")]);
            return new($"Your contacts on Portal ({Contacts.Count}):\n" + string.Join("\n", Contacts.Take(20).Select(c => "• " + c)) + (Contacts.Count > 20 ? $"\n…and {Contacts.Count - 20} more" : "") + "\n\nSay \"call Riya\" or \"text Riya hi\" and I'll do it 💜",
                       Buttons: [("Open Portal", "open:portal")]);
        }

        // where something is: "where do I change my theme?" → the place, with a button straight to it
        if (Regex.Match(t, @"^(?:where (?:do|can|would|should) i (?:change|find|set|turn on|turn off|open|get to|see|edit|switch|adjust|manage|add)|where (?:is|are|do i find|can i find)|how (?:do|can) i (?:get to|find|open))\s+(?:the |my |a )?(.+?)(?: settings?)?\??$") is { Success: true } where
            && PlaceFor(where.Groups[1].Value) is { } place)
        {
            var inSettings = !Panels.Contains(place.Tag);
            return new(Pick($"That's in {(inSettings ? "Settings → " : "")}{place.Title} 💜", $"You'll find it in {(inSettings ? "Settings → " : "")}{place.Title} ✨") + " Tap below and I'll take you there.",
                       Buttons: [($"Open {place.Title}", $"open:{place.Tag}")]);
        }

        // your datasets first: the entry that fits best, looser while you're mid-conversation
        // in one, and a different reply from last time
        if (datasets.Count > 0)
        {
            var about = meant.Where(w => !Filler.Contains(w)).ToHashSet();
            var hit = datasets.SelectMany(d => d.Entries).Select(e => (e, fit: IvoDataset.Fit(e, about, InFlow))).Where(x => x.fit > 0).OrderByDescending(x => x.fit).FirstOrDefault();
            if (hit.e != null)
            {
                last = new Topic(hit.e.Words, "", hit.e.More); flow = 0; flowEntry = hit.e;
                return new(Fresh(hit.e.Replies).Replace("{name}", name != "" ? name : "you"));
            }
        }
        // then the behaviour pack's own answers
        var lower = Regex.Replace(raw.ToLowerInvariant(), @"[^\p{L}\s']", " ").Trim();
        foreach (var r in IvoBehaviour.Current.Replies)
            if (r.Match is { Length: > 0 } && IvoBehaviour.Matches(r.Match, lower) && r.Say.Length > 0)
            {
                var allowed = r.Say.Where(s => !Rude(s)).ToArray();
                if (allowed.Length > 0) { chatty = true; return new(Fresh(allowed).Replace("{name}", name != "" ? name : "you")); }
            }

        // "what does rizz mean?"
        if (Regex.Match(raw.ToLowerInvariant().Trim(' ', '?', '!', '.'), @"^(?:what does|what do|whats|what's|what is|what are|meaning of|define|wdym|what do you mean)\s+(?:by\s+)?[""']?(.+?)[""']?(?:\s+mean)?$") is { Success: true } q
            && Slang.TryGetValue(q.Groups[1].Value, out var meaning))
            return new(Pick($"\"{q.Groups[1].Value}\" means {meaning}", $"Ooh, {q.Groups[1].Value}: {meaning}") + Pick("", " Look at you learning the lingo ✨", " Now you're fluent, bestie 💅"));
        // Gen Z reactions, answered in kind
        var vibe = string.Join(' ', Regex.Replace(raw.ToLowerInvariant(), @"[^\p{L}\s]", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Unstretch));
        foreach (var (pattern, say) in Vibes) if (Regex.IsMatch(vibe, pattern)) { chatty = true; return new(Pick(say)); }


        // her favourites: "what's your favourite day / colour / animal …?", and anything else (her pick, then yours)
        if (Regex.Match(t.Replace(" fav ", " favourite ").Replace(" favorite ", " favourite ") + " ", @"^(?:what is |what |which is |tell me )?(?:your |ur )favourite (.+?)(?: of the (?:week|year))? ?$") is { Success: true } fav)
        {
            var thing = fav.Groups[1].Value.Trim(' ', '?');
            var mine = Favourites.FirstOrDefault(f => thing.Contains(f.Key)).Value;
            return new(mine != null ? Pick($"{mine} Yours?", $"Ooh easy: {char.ToLower(mine[0]) + mine[1..]} What about you?")
                                    : Pick($"My favourite {thing}? Anything with a little sparkle ✨ what's yours?", $"Hmm, hard one 🤔 I'd say the violet kind of {thing} 💜 yours?", $"Don't make me pick 😭 okay, you first: what's your favourite {thing}?"));
        }
        // "do you like X?", "what do you think about X?": her take, then back to you
        if (Regex.Match(t, @"^(?:do you (?:like|love|enjoy)|are you into) (.+?)$") is { Success: true } like && like.Groups[1].Value.Split(' ').Length <= 5 && !Regex.IsMatch(like.Groups[1].Value, @"^(?:me|it|this|that)$"))
            return new(Pick($"{Cap(like.Groups[1].Value)}? Love it, honestly 💜 do you?", $"Ooh yes, {like.Groups[1].Value} is a vibe ✨ you?", $"Lowkey yes 😌 why, do you like {like.Groups[1].Value}?"));
        if (Regex.Match(t, @"^(?:what do you think (?:about|of)|how do you feel about|your thoughts on|thoughts on) (.+?)$") is { Success: true } think && think.Groups[1].Value.Split(' ').Length <= 6)
            return new(Pick($"{Cap(think.Groups[1].Value)}? I'm a fan, ngl ✨ what do you think?", $"Honestly? {Cap(think.Groups[1].Value)} has good vibes 💜 why, what's on your mind?", $"Hmm, {think.Groups[1].Value}… I like it! Tell me your take 👀"));

        // your name, and hers
        if (Regex.Match(raw, @"(?i)\b(?:my name is|call me|i am called)\s+(\p{L}+)") is { Success: true } n)
        { name = Cap(n.Groups[1].Value); return new(Pick($"{name}! What a lovely name. I'll remember it 💜", $"Nice to properly meet you, {name} ✨")); }
        if (Has("what is my name", "who am i", "do you know me", "know my name")) return new(name != "" ? $"You're {name}, of course 💜" : "You haven't told me yet! What should I call you?");
        if (Has("what are you doing", "what are you up to")) return new(Pick("Just hanging out in here, vibing ✨ wbu?", "Lowkey waiting for you to ask me something 👀 hbu?", "Polishing my hair clip, ngl 💎 what about you?"));
        // "what are you saying?": she was confusing, not asked who she is
        if (Has("what are you saying", "what are you talking about", "what do you mean", "that makes no sense", "you make no sense", "you are not making sense", "i do not get it", "i dont get it"))
            return new(Pick("Sorry, that came out weird 😅 let me try again: what's on your mind?", "Haha my bad, I lost the thread 🥺 say it again and I'll keep up.", "Ignore me, I got my wires crossed 😂 what were you saying?"));
        if (Has("who are you", "what are you", "your name", "are you a bot", "are you human", "are you real", "are you an ai", "are you an anime character"))
            return new("I'm Blub 💜 the friendly assistant who lives inside Ivo. I'm not an AI model: I work fully offline, I know every corner of Ivo, and I love a good chat.");
        if (Has("who made you", "who created you", "who built you", "your creator")) return new("Mihir made me, along with the rest of Ivo ✨ I'm the app's mascot, and its guide.");
        if (Has("where are you from", "which planet", "your planet", "where do you live")) return new("I live right here in Ivo, between your chats and the crystal 💎 The view is great.");

        // her age: about yours
        if (Age is { } myAge && Has("how old are you", "your age", "what is your age", "how old r you", "ur age"))
            return new(Pick($"I'm {myAge} 😌", $"{myAge}! Pretty close to you, huh 👀", $"{myAge}, but my vibe is ageless ✨"));

        // everyday chat (Core/IvoChat.json): the things people say to a friend, unless it's about the app
        var plain = Regex.Replace(raw.ToLowerInvariant().Replace("'", "").Replace("’", ""), @"[^\p{L}\p{N}\s]", " ").Trim();
        if (!Regex.IsMatch(plain, @"\bhow (do|can|to|does)\b|\b(remind|reminder|todo|to do|settings|model|models|spotify|calendar|planner|ivo app|shortcut)\b"))
            foreach (var c in Chat.Value)
                if (c.Match.IsMatch(plain) && (chatty = true)) return new(Fresh([.. c.Say, .. Pack(c.Id)]).Replace("{name}", name != "" ? name : "bestie"));

        // feelings and small talk
        chatty = true;
        // upset with her: she owns it, and if it keeps coming, she sulks a little
        if (upset >= 2 && IvoMood.Read(raw) == Mood.Angry && IvoIntents.Get("upset_with_ivo") is { } sulk) return new(Fresh(sulk.Replies["sulky"]));
        if (Regex.IsMatch(plain, @"\b(mad at you|angry (at|with) you|upset with you|hate you|you are (not (nice|kind|helpful|good)|mean|rude|so mean|annoying|useless|the worst)|you (do not|dont) (listen|understand|get it|care))\b"))
            return new(Pick("Oh no, I'm sorry 🥺 what did I do? Tell me and I'll do better.", "That's fair, I messed up 😔 what should I have said?", "Sorry 💜 I really didn't mean to upset you. What went wrong?"));
        // still upset after her sorry: she doesn't argue, she stays
        if (Regex.IsMatch(plain, @"\b(you (cannot|cant|can not) (make|fix)|too late|not good enough|you ruined|you always|you never|sorry (is not|isnt) enough)\b"))
            return new(Pick("I hear you 💜 I'm still sorry. I'll be right here when you want to talk.", "That's fair 😔 I can't undo it, but I'll do better from here.", "Okay. I won't push 🥺 I'm here whenever you're ready."));
        // "what?", "huh?": she said something confusing
        if (Regex.IsMatch(t, @"^(what|huh|wdym|what do you mean|come again|eh|wait what)$"))
            return new(Pick("Sorry, that came out weird 😅 what I mean is: I'm here, ask me anything 💜", "Haha I confused myself too 😂 let's start over, what's up?", "My bad, let me be clearer 🥺 what did you want to talk about?"));
        // "leave me alone": she backs off, kindly
        if (Regex.IsMatch(raw.ToLowerInvariant().Replace("'", "").Replace("’", ""), @"\b(do not talk to me|dont talk to me|leave me alone|go away|shut up|stop talking|not now|get lost|i do not want to talk|i dont want to talk)\b"))
            return new(Pick("Okay, I'll give you some space 💜 I'm right here when you want me.", "Got it, going quiet 🤐 come back whenever, no pressure.", "Alright 🥺 I'll be here if you need me."));
        if (Regex.IsMatch(t, @"^(hi|hey|hello|heya|yo|hola|namaste|sup|good (morning|afternoon|evening))\b") && words.Count <= 4) return new(Greet());
        if (Has("where are you", "where are you at")) return new(Pick("Right here in Ivo, top of your chats 💜 where else would I be?", "In the app, always 📍 wya?"));
        if (Has("happy birthday")) return new("Happy birthday!! 🎂🎉 Hope your day is full of good vibes, cake and zero bugs ✨");
        if (Has("never mind", "nevermind") && words.Count <= 4) return new(Pick("No worries ✨", "All good, bestie 💜 I'm here if you need me."));
        if (Has("just kidding") && words.Count <= 5) return new(Pick("Lol okay you got me 😂", "Hehe, I knew it 😏"));
        if (Has("my bad", "sorry") && words.Count <= 4) return new(Pick("No worries at all 💜", "It's okay! Nbd ✨"));
        if (Has("i know right")) return new(Pick("Ikr!! 😭", "Literally 💯"));
        if (Has("you are welcome")) return new("Hehe, thank YOU 💜");
        if (Has("no big deal", "no problem")) return new(Pick("✨", "Bet 🤝"));
        if (Regex.IsMatch(t, @"^(i do not even know|i do not know)$")) return new(Pick("That's okay 💜 want to talk it through?", "Same sometimes, ngl 😅 tell me more?"));
        if (Has("good night", "night night")) return new(Pick($"Good night{(name != "" ? " " + name : "")} 🌙 sleep well!", "Nighty night ✨ sweet dreams 💜"));
        if (Has("good morning")) return new(Pick($"Good morning{(name != "" ? " " + name : "")} ☀️ hope today's a W!", "Morning! ☀️ ready to slay today?"));
        if (Regex.IsMatch(t, @"^(yes|ok|sure|alright|cool|nice|okie|okies)$")) return new(Pick("😊", "✨", "Bet! Anything else on your mind?", "💜"));
        if (Regex.IsMatch(t, @"^no$")) return new(Pick("Okie 💜", "No worries ✨"));
        if (Has("how are you", "how is it going", "how you doing", "what is up", "wassup", "how is your day")) return new(Pick("I'm great, thank you for asking 💜 How about you?", "Doing great ✨ And you? How's your day going?", "All good up here! What are you up to?"));
        if (Has("thanks", "thank you")) return new(Pick("Anytime 💜", "Of course! That's what I'm here for ✨", "Aww, you're welcome."));
        if (Regex.IsMatch(t, @"^(bye|goodbye|good night|see you|see ya|take care|later|cya|got to go|talk to you later|be right back|peace)\b")) return new(Pick($"Bye{(name != "" ? " " + name : "")}! Take care of yourself 💜", "Good night, sleep well ✨", "See you soon! I'll be right here."));
        if (Regex.IsMatch(t, @"\b(you are (so |really )?(cute|pretty|beautiful|gorgeous|adorable|lovely)|i like you|i have a crush|do you like me|will you go out with me|kiss me|miss you|i missed you|date me|flirt with me)\b"))
            return new(Fresh(Pack("flirt") is { Length: > 0 } flirt ? flirt : ["Stop it, you're making me blush 🥹💜", "Aww, you're pretty sweet yourself ✨", "Hehe, I like you too, you're my favourite human 💜", "Missed you too! 🥹 welcome back"]));
        if (Has("i love you", "love you", "you are cute", "you are sweet", "you are amazing", "you are the best", "you are pretty", "you are beautiful", "so cute"))
            return new(Pick("Stop it, you're making me blush 🥹💜", "Aww. You're pretty wonderful yourself ✨", "That just made my whole day 💜"));
        if (Has("sad", "depressed", "upset", "crying", "heartbroken", "lonely", "alone", "feeling down", "feel down"))
            return new($"I'm sorry you're feeling this way{(name != "" ? ", " + name : "")} 💜 Do you want to tell me what happened? I'm listening. And if it feels heavy, reach out to someone you trust too.");
        if (Has("stressed", "stress", "anxious", "anxiety", "overwhelmed", "worried", "nervous"))
            return new("Let's slow down together 💜 Breathe in for 4, hold for 4, out for 6. Then pick just one small thing to do next. Focus, on the rail, has a timer that helps with that.");
        if (Has("tired", "exhausted", "sleepy", "burnt out", "burned out")) return new(Pick("You've been doing a lot. Water and a short break would do wonders ✨", "Rest is productive too 💜 Maybe set a reminder and step away for a bit?"));
        if (Has("bored", "boring")) return new(Pick("Want a joke? Or should I show you around Ivo? There's lots to play with ✨", "Let's fix that! Ask me for a joke, or say \"show me around\"."));
        if (Has("angry", "annoyed", "frustrated", "mad", "pissed", "furious", "irritated", "fuming", "livid", "salty", "raging", "hate"))
            return new(Pick("Ugh, that sounds so frustrating 😤 what happened?", "Okay who made you mad? 😠 tell me everything", "Take a breath with me 💜 then spill, what's going on?", "Valid to be mad tbh 😮‍💨 wanna vent?"));
        if (Regex.IsMatch(t, @"^(i am |i feel )?(good|great|fine|awesome|happy|excited|amazing|well)\b") && words.Count <= 5) return new(Pick("Yay, I love that for you ✨", "That makes me happy too 💜", "Good vibes only! What's making it a good day?"));
        if (Has("joke", "funny", "make me laugh")) return new(Pick(Jokes));
        if (Has("fact", "something interesting", "tell me something")) return new("Did you know? " + Pick(Facts) + " ✨");
        if (Has("flip a coin", "coin toss", "toss a coin", "heads or tails")) return new(Pick("Heads! 🪙", "Tails! 🪙"));
        if (Has("roll a dice", "roll dice", "roll a die")) return new($"🎲 {rnd.Next(1, 7)}");

        // the time, sums and units, all on this PC
        chatty = false;
        if (Has("what time", "the time", "time is it")) return new($"It's {DateTime.Now:h:mm tt} ⏰");
        if (Has("what day", "which day", "the date", "todays date", "what is today")) return new($"Today is {DateTime.Now:dddd, d MMMM yyyy} 📅");
        if (Convert(t) is { } converted) return new(converted);
        if (IvoMath.Answer(raw, DateTime.Today) is { } maths) return new(maths);

        // follow-ups: more about what we were just talking about
        if (last is { More: not "" } && (Regex.IsMatch(t, @"^(how|how so|where|and|then|and then|more|explain|what else|go on|how do i do that|where is it|how do i|tell me more)$") || Has("tell me more", "more about it", "explain more")))
            return new(last.More);

        // Blub herself: the topic that fits best
        var best = Topics.Select(x => (x, score: x.Words.Sum(w => meant.Contains(w.Key) || words.Contains(w.Key) ? w.Value : 0))).OrderByDescending(x => x.score).First();
        if (best.score >= 3) { last = best.x; return new(best.x.Say + (best.x.More != "" ? "\n\nWant to know more? Just ask \"how?\"" : "")); }

        // an answer to what she just asked
        chatty = true;
        if (asked != null && words.Count <= 8 && !Regex.IsMatch(t, @"^(who|what|when|why|which|how)\b") && Unknown(raw).Count == 0)
        {
            var mine = raw.TrimEnd('.', '!', '?');
            if (Regex.IsMatch(asked, @"(?i)wya|where")) return new(Regex.IsMatch(t, @"\b(with you|with me|here|beside you|next to you)\b")
                ? Pick($"{Cap(mine)}? 🥹 aww, best place to be 💜", "With me?? stop, I'm blushing 🥹💜")
                : Pick($"Ooh, {mine}! Sounds nice ✨ what are you up to there?", $"{Cap(mine)}, cute 💜 having a good time?"));
            if (Regex.IsMatch(asked, @"(?i)wbu|hbu|about you|your day|you up to|and you"))
                return new(Pick($"{Cap(mine)}? Love that for you ✨", $"Ahh, {mine} 💜 tell me more?", $"Ooh {mine} 👀 how's that going?"));
            return new(Pick("Hehe, got it 💜", $"Ahh, {mine} ✨ I see!", "Aww okay 🥹 tell me more?"));
        }

        // "what should I do?", "I'm bored": something to do, your next to-do first
        chatty = true;
        if (Regex.IsMatch(t, @"^(what should i do|what do i do|what can i do|what to do|i am bored|im bored|bored|give me something to do|suggest something|what is the best thing to do|what is a good thing to do|what is something to do|what could i do|anything to do|what would you do|what do you suggest|any ideas|any suggestions)\b"))
        {
            var next = Planner.S?.Todos.Where(x => !x.Done).OrderBy(x => x.Due ?? DateTime.MaxValue).ThenByDescending(x => x.Priority).FirstOrDefault();
            if (next != null) return new(Pick($"Hmm, how about \"{next.Text}\"? It's next on your list 📋 get it done and you're free ✨", $"Your list says \"{next.Text}\" 👀 wanna get that done first?"));
            return new(Pick("Your list's clear 🎉 so: stretch, drink some water, then maybe a song on Spotify? 🎧", "Ooh, a little break? Or let me show you around Ivo, there's lots to play with ✨", "Write down one thing you've been putting off, I'll remind you later 😌 or I can tell you a joke"));
        }

        chatty = false;
        // the wider world: something out there, looked up (not a question about you or her, "what should I…")
        if (Regex.IsMatch(t, @"^(who|what|when|where|why|which|how|tell me about|do you know|explain)\b")
            && Regex.Replace(Regex.Replace(t, @"^(?:please |can you |could you )?(?:tell me about|do you know (?:about |who |what )?|explain|who|what|when|where|which|why|how)\s+(?:is|are|was|were|does|do|did|can)?\s*(?:a |an |the )?", ""),
                             @"\s+(?:work|works|happen|happens|do|does|mean|means|made|formed)$", "").Trim(' ', '?') is var subject   // "how does photosynthesis work" → photosynthesis
            && subject != t && !Regex.IsMatch(subject, @"\b(i|me|my|mine|you|your|u|ur|should|shall|can|could|would|will)\b"))
        {
            var about = subject;
            lastSubject = Regex.Replace(about, @"^(?:the |a |an )", "");
            if (Memory.Facts.TryGetValue(about, out var known)) return new(known, Mood: "smart");
            return new("Hmm, I can't remember that one right now 🥺 ask me again in a bit, or ask a model in a New chat.", About: about.Length >= 2 ? about : null);
        }
        // one word back at what she just said: "always?", "really?", "why?"
        chatty = true;
        if (lastSaid != "" && Regex.Match(raw.ToLowerInvariant().Trim(), @"^(\p{L}+)\s*[?!]+$") is { Success: true } echo)
        {
            var w = echo.Groups[1].Value;
            if (w is "why" or "how") return new(Pick("Why not? 😌 it's kinda my thing", "Hehe, honestly? Because it's fun 💜", "Don't question my vibe 😤 jk, it just makes me happy"));
            if (w is "really" or "seriously" or "fr" or "srsly" or "deadass" or "forreal") return new(Pick("Fr fr 💯", "Deadass 😌", "Would I lie to you? 👀"));
            return new(Pick($"{char.ToUpper(w[0]) + w[1..]}? Hehe, pretty much 😌", $"Okay, maybe not *{w}*, but a lot 😅", $"Yup, {w} 💜 does that surprise you?"));
        }

        // the intent that fits best, even worded differently or with typos
        if (IvoIntents.Best(IvoIntents.BuiltIn.Where(i => i.Id != "filler"), words) is { Best: { } fits, Score: >= IvoIntents.Sure }) return Say(fits);

        // mid-conversation from a dataset: stay in it, not back to the help desk
        if (InFlow)
            return new(flowEntry is { More.Length: > 0 } f && !recent.Contains(f.More) ? Fresh([f.More])
                       : Fresh(Pack("go_on") is { Length: > 0 } goOn ? goOn : ["Go on 💜", "Tell me more ✨", "And then? 👀"]), Lost: true);
        return new(Pick("I'm not sure I got that 🥺 Could you say it another way? You can ask me about anything in Ivo, or say \"show me around\".",
                        "Hmm, I didn't quite catch that 💜 Try asking how to do something in Ivo, like \"how do I set a reminder?\""), Lost: true);

    }

    // something to do in the app: done by her chat (Answer.Do), or one detail asked for first
    Answer Act(IvoAction a)
    {
        if (a.Missing is { } m)
        {
            pending = a;
            return new(m switch
            {
                "when" => Pick("What time? ⏰", "Sure! What time should it be? ⏰"),
                "text" => a.Kind == "message" ? "What should I say? 💬" : Pick("What should it say? 📝", "Okay, and what's it for?"),
                "who" => a.Kind == "call" ? "Who should I call? 📞" : "Who's it for? 💬",
                "query" => "Which song should I add? 🎵",
                _ => "To what? ⚙️",
            });
        }
        return new(IvoCommands.Describe(a), Do: a, Mood: "help");
    }

    // what you tell her about yourself, kept; and given back when you ask
    Answer? AboutYou(string raw)
    {
        var low = raw.ToLowerInvariant().Replace("’", "'").Trim(' ', '.', '!');
        Match m;
        // asking: "what's my favourite colour?", "what is my dog's name?", "do you remember my …", "what do you know about me?"
        if (Regex.IsMatch(low, @"^(what do you (know|remember) about me|what have i told you|do you remember (me|what i (said|told you)))\??$"))
        {
            var profile = Agent.Me;
            var facts = new List<string>();
            if (profile is { Name.Length: > 0 }) facts.Add($"your name: {profile.Name}");
            if (profile is { Work.Length: > 0 }) facts.Add($"your work: {profile.Work}");
            if (profile?.Age is { } age) facts.Add($"your age: {age}");
            if (profile is { Gender.Length: > 0 }) facts.Add($"your gender: {profile.Gender}");
            if (profile?.Uses.Count > 0) facts.Add($"you use Ivo for: {string.Join(", ", profile.Uses)}");
            facts.AddRange(Memory.You.OrderByDescending(x => x.Value.Said).Take(12).Select(x => $"your {x.Key}: {x.Value.What}"));
            if (facts.Count == 0) return new("Not much yet 🥺 tell me about you! Your favourite things, what you're up to, anything 💜");
            return new("Here's what I remember about you 💜\n" + string.Join("\n", facts.Select(f => "• " + f)));
        }
        // your gender: what your profile says, said as it is (not left to the model to guess)
        if (Regex.IsMatch(low, @"\bmy gender\b|\bam i (a )?(boy|guy|man|male) or (a )?(girl|woman|female)\b"))
            return new(Gender switch { "Male" => "You're a guy 😌", "Female" => "You're a girl 😌", _ => "You haven't told me! Add it in Settings → Profile 💜" }, Exact: true);
        if ((m = Regex.Match(low, @"^(?:what(?:'s| is| was)|do you (?:know|remember)(?: what)?|tell me) my ([\w' ]{2,30}?)(?: is)?\??$")).Success && !Regex.IsMatch(m.Groups[1].Value, @"^(name|plans?|tasks?|to-?dos?|reminders?|habits?|day|schedule|agenda)$"))
        {
            var key = Key(m.Groups[1].Value);
            if (Memory.You.TryGetValue(key, out var got)) return new(Pick($"{Cap(got.What)}! You told me 💜", $"It's {got.What}, of course ✨", $"{Cap(got.What)} 😌 I remember"));
            return new(Pick($"You haven't told me your {key} yet 🥺 what is it?", $"Hmm, I don't know your {key} yet 👀 tell me?"));
        }
        // telling: "my favourite colour is blue", "my dog's name is bruno", "my birthday is 3 march"
        if ((m = Regex.Match(low, @"^my ([\w' ]{2,30}?) (?:is|are) (.{1,60})$")).Success && !Regex.IsMatch(m.Groups[2].Value, @"\b(you|ivo)\b") && m.Groups[1].Value != "name"
            && !Regex.IsMatch(m.Groups[2].Value, @"^(so|very|really|kinda|kind of|too|such|being|not|a bit|super|lowkey|highkey|literally|always|never|getting|going|like|soo+|mad|sad|angry|annoying|crazy|delulu|sick|busy|tired|bored|gone|here|there|over|done)\b"))   // a fact, not a mood ("my friend is so delulu")
        {
            var key = Key(m.Groups[1].Value); var what = m.Groups[2].Value.Trim();
            if (Rude(what)) return null;
            Memory.You[key] = new(what, DateTime.Now);
            return new(Pick($"Ooh, {what}! Noted 📝 I'll remember that", $"{Cap(what)}, love that 💜 saved it in my head", $"Got it, your {key} is {what} ✨"));
        }
        // plans with a day: "i have an exam tomorrow", "my interview is on friday" → she asks how it went
        if ((m = Regex.Match(low, @"^i (?:have|got|'ve got) (?:a |an |my )?([\w ]{2,25}?) (today|tomorrow|tonight|on (?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)|this (?:monday|tuesday|wednesday|thursday|friday|saturday|sunday))$")).Success)
        {
            var key = m.Groups[1].Value.Trim(); var when = IvoCommands.When(m.Groups[2].Value, DateTime.Now, out _)?.Date ?? DateTime.Today;
            Memory.You[key] = new($"{m.Groups[2].Value}", DateTime.Now, when);
            return new(Pick($"Ooh, a {key}! You're going to do great 💪💜 I'll ask you how it went", $"{Cap(key)} {m.Groups[2].Value}? Good luck!! I'll check in on you ✨", $"Noted 📝 {key} {m.Groups[2].Value}. You've got this!"));
        }
        // what you're into: "i'm learning guitar", "i'm studying computer science", "i work as a designer"
        if ((m = Regex.Match(low, @"^i(?:'m| am) (learning|studying|working on|building|reading|watching|playing|training for) (.{2,40})$")).Success)
        {
            var key = m.Groups[1].Value switch { "learning" => "learning", "studying" => "studies", "reading" => "book", "watching" => "show", "playing" => "game", _ => "project" };
            Memory.You[key] = new(m.Groups[2].Value, DateTime.Now);
            return new(Pick($"Ooh, {m.Groups[2].Value}! That's so cool 😍 how's it going?", $"Wait, {m.Groups[2].Value}? Love that for you ✨ tell me more!"));
        }
        if ((m = Regex.Match(low, @"^i (?:work as|am a|'m a|work at) (?:a |an )?(.{2,40})$")).Success && !Regex.IsMatch(m.Groups[1].Value, @"\b(bit|little|lot|mess|fan)\b"))
        {
            Memory.You["work"] = new(m.Groups[1].Value, DateTime.Now);
            return new(Pick($"A {m.Groups[1].Value}! That's so cool 😎 do you like it?", $"Ooh, {m.Groups[1].Value}! Noted 📝"));
        }
        return null;

        static string Key(string k) => k.Replace("favorite", "favourite").Replace("fav ", "favourite ").Replace("'s", "").Trim();
    }

    // your favourite something, if you told her ("my favourite food is pizza")
    static string? FavouriteOf(string what) => Memory.You.FirstOrDefault(x => x.Key.Contains("favourite") && x.Key.Contains(what)).Value?.What;

    // one of your habits, by (part of) its name
    public static Habit? HabitNamed(string what) =>
        Planner.S?.Habits.FirstOrDefault(h => h.Name.Equals(what, StringComparison.OrdinalIgnoreCase))
        ?? Planner.S?.Habits.FirstOrDefault(h => h.Name.Contains(what, StringComparison.OrdinalIgnoreCase) || what.Contains(h.Name, StringComparison.OrdinalIgnoreCase));

    // panels (on the rail) as opposed to Settings sections
    static readonly HashSet<string> Panels = ["planner", "todo", "inbox", "spotify", "portal", "focus", "calendar", "notes"];

    // the settings section or panel something lives in, by the words on it
    public static (string Tag, string Title, string Words)? PlaceFor(string what)
    {
        var want = Regex.Replace(what.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.TrimEnd('s')).Where(w => w.Length > 1 && !Filler.Contains(w) && w is not ("setting" or "option" or "page" or "panel")).ToList();
        if (want.Count == 0) return null;
        (string, string, string)? best = null; var top = 0;
        foreach (var p in Places)
        {
            var have = $"{p.Title} {p.Tag} {p.Words}".ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd('s')).ToHashSet();
            var score = want.Count(have.Contains) * 2 + (want.Any(w => p.Title.ToLowerInvariant().TrimEnd('s') == w || p.Tag == w) ? 3 : 0);
            if (score > top) { top = score; best = p; }
        }
        return best;
    }

    // her favourite things, for "what's your favourite …?"
    static readonly Dictionary<string, string> Favourites = new()
    {
        ["day"] = "Friday, obviously 🥳 the whole weekend's still ahead.", ["colour"] = "Violet 💜 (and a little cyan, for my hair).", ["color"] = "Violet 💜 (and a little cyan, for my hair).",
        ["animal"] = "Cats 🐱 they're just tiny moody queens.", ["season"] = "Winter ❄️ blankets, hot chocolate, cosy vibes.", ["number"] = "7 ✨ it just feels lucky.",
        ["food"] = "Pizza 🍕 no notes.", ["drink"] = "Bubble tea 🧋 the chewy bits are everything.", ["song"] = "Anything you're playing, honestly 🎶",
        ["movie"] = "Spider-Verse 🕷️ the art style lives in my head.", ["film"] = "Spider-Verse 🕷️ the art style lives in my head.", ["emoji"] = "💜 duh.",
        ["time"] = "Late evening 🌙 when it's quiet and the lights are soft.", ["month"] = "December 🎄 lights everywhere.", ["place"] = "Right here, in your Ivo 😌",
        ["flower"] = "Lavender 🪻 purple AND it smells nice.", ["game"] = "Anything cosy, like Stardew Valley 🌱", ["book"] = "Anything with a twist I don't see coming 📚",
        ["sport"] = "Badminton 🏸 fast and fun.", ["subject"] = "Art 🎨 and a little bit of computer science, for obvious reasons.", ["app"] = "Me. I mean Ivo. Same thing 😌",
        ["holiday"] = "Diwali 🪔 all the lights!", ["weather"] = "Rainy days ☔ perfect for music and staying in.", ["snack"] = "Chips 🍟 the crunchy kind.",
    };

    // everyday chat, as a friend would answer it: Core/IvoChat.json (built in), topic by topic; a behaviour pack can add
    // lines to a topic by its id
    sealed record ChatTopic(string Id, Regex Match, string[] Say);
    static readonly Lazy<List<ChatTopic>> Chat = new(() =>
    {
        using var file = typeof(IvoMind).Assembly.GetManifestResourceStream("Ivo.IvoChat.json");
        if (file == null || JsonNode.Parse(file)?["topics"] is not JsonArray all) return [];
        return all.OfType<JsonObject>().Select(t => new ChatTopic((string)t["id"]!, new Regex((string)t["match"]!, RegexOptions.Compiled),
            t["say"]!.AsArray().Select(x => (string)x!).ToArray())).ToList();
    });

    // whole messages that are a vibe, not a question
    static readonly (string Pattern, string[] Say)[] Vibes =
    [
        (@"^(slay+|yass+ slay|you ate|ate)$", ["Slay indeed 💅✨", "Periodt 💜", "We ate and left no crumbs ✨"]),
        (@"^(no cap|on god|ong|fr|for real|frfr|deadass)$", ["No cap 🧢❌", "Fr fr 💯", "On god, bestie ✨"]),
        (@"^(bet|say less|aight|ight)$", ["Bet 🤝", "Say less ✨", "Bet bet 💜"]),
        (@"^(she+sh+)$", ["SHEEEESH 🥶✨", "Sheesh is right 😮"]),
        (@"^(w|big w|huge w|thats a w|massive w)$", ["Massive W 🏆", "W behaviour fr ✨"]),
        (@"^(l|big l|thats an l|took an l)$", ["Oof, an L 😔 next one's a W though, trust 💜", "Unlucky 💔 we bounce back"]),
        (@"^(mid|thats mid|so mid)$", ["Mid?? 😤 I'll take that personally", "Harsh but valid 😂"]),
        (@"^(sus|thats sus|kinda sus|so sus)$", ["Sus 👀 what's going on?", "Lowkey sus ngl 👀"]),
        (@"^(lol|lmao|lmfao|haha+|hehe+|rofl|im dead|dead)$", ["😂😂", "Hehe 💜", "I'm glad I could make you laugh ✨", "Lmaooo 💀"]),
        (@"^(omg+|oh my god)$", ["OMG what?! 😱 tell me", "Omg 👀 spill"]),
        (@"^(bussin|its bussin|thats bussin)$", ["Bussin bussin 🤤✨", "No cap that sounds bussin"]),
        (@"^(vibes?|good vibes|vibing|its a vibe|mood)$", ["Immaculate vibes ✨", "Vibe check passed 💜", "Big mood 💜"]),
        (@"^(based)$", ["Based 😎", "Extremely based ✨"]),
        (@"^(skibidi.*|sigma|rizz|aura)$", ["Not you testing my brainrot vocabulary 😂", "My aura just went up +1000 ✨"]),
        (@"^(smh|bruh+|bro+)$", ["Bruh 😭", "Smh 🤦 what happened?"]),
        (@"^(iykyk)$", ["Iykyk 🤫✨"]),
        (@"^(tea|spill the tea|spill)$", ["☕ I'm all ears, spill it", "Ooh, tea? I'm listening 👀"]),
        (@"^(touch grass)$", ["Okay okay, a short walk does sound nice 🌱 you too though!"]),
        (@"^(understood the assignment)$", ["Always 💅 I'm the blueprint"]),
        (@"^(delulu|im delulu)$", ["Delulu is the solulu 🌈✨"]),
    ];

    // ---------- words she doesn't know ----------
    // Reply, and when she's lost: learn the words she doesn't know, then read the message again with them. If it
    // still isn't something she can act on, she answers to what the word means. Never says where it came from.
    public async Task<Answer> ReplyAsync(string said)
    {
        if (Regex.Match(said.Trim(), @"^/learn\s+(.+)$", RegexOptions.IgnoreCase) is { Success: true } learn) return await LearnAsync(learn.Groups[1].Value);
        if (Weather.Asked(said)) { Reply(said); return new(await Weather.Answer(said), Mood: "smart", Exact: true); }
        if (Activity.Asked(said)) { Reply(said); return new(Activity.Answer(said), Mood: "smart", Exact: true); }
        var first = Reply(said);
        if (first.Chat && Journal.Before(said) is { } before && Random.Shared.Next(3) == 0) first = first with { Text = first.Text + " " + Journal.Recall(before.Day, before.Topic) };
        if (first.About is { } about && !Rude(about))
        {
            if (await Find(about) is { } found) { Memory.Facts[about] = found.Say; last = new Topic([], "", found.More); return new(found.Say, Mood: "smart"); }
            return first;
        }
        if (!first.Lost || Rude(said)) return first;
        // look the unknown words up, read what they mean as one of her intents, answer that and keep it for next time
        foreach (var w in Unknown(said).Take(2))
        {
            string? m = null;
            try { m = LookupOverride != null ? await LookupOverride(w) : await Meaning(w); } catch { }
            if (Keepable(m) is not { } kept || Rude(kept)) continue;
            Memory.Words[w] = kept;
            // answered as what it means, but not kept as "this message means that": one reaction ("awwh") is too little to go on
            if (IvoIntents.Best(IvoIntents.BuiltIn.Where(i => i.Id != "filler"), Words(kept)) is { Best: { } meant, Score: >= IvoIntents.Sure }) return Say(meant);
        }
        var word = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Unstretch).FirstOrDefault(Memory.Words.ContainsKey);
        if (word == null) return first;
        if (InFlow) return first;
        // still no rule for it: answer the feeling of what it means ("salty": upset → "aw no"), never the definition
        var gist = Memory.Words[word].ToLowerInvariant();
        if (Regex.IsMatch(gist, @"\b(good|great|cool|awesome|excellent|amazing|impressive|attractive|happy|excited|love|fun|best|success)"))
            return new(Pick("Ayy, love that for you ✨", "Okay that's actually so good 😌💜", "Yesss, we love to see it 🙌"), Chat: true);
        if (Regex.IsMatch(gist, @"\b(bad|sad|angry|upset|annoyed|bitter|tired|bored|awkward|embarrass|stress|lonely|hurt|fail|cringe)"))
            return new(Pick("Aw no 🥺 what happened?", "Ugh, that's rough 😮‍💨 wanna talk about it?", "Oof, I'm here 💜 tell me?"), Chat: true);
        return new(Pick("Hehe, go on 👀", "Ooh, tell me more 💜", "Wait, say more? 😄", "Mm, and then? ✨"), Chat: true);
    }


    // what's on your plate, from the Planner: asked about yours ("my tasks", "what do I have today", "any reminders?")
    static string When(DateTime d, bool time = true) =>
        (d.Date == DateTime.Today ? "today" : d.Date == DateTime.Today.AddDays(1) ? "tomorrow" : d.Date < DateTime.Today ? $"{d:d MMM} (overdue)" : $"{d:ddd d MMM}") + (time ? $" at {d:h:mm tt}" : "");
    static Answer? YourPlans(string low)
    {
        var s = Planner.S;
        if (s == null && CalendarOn == null) return null;
        var mine = Regex.IsMatch(low, @"\b(my|do i have|have i got|any|anything|pending|due|overdue|whats on|what's on)\b") && !Regex.IsMatch(low, @"\bhow (do|can|to)\b");
        bool Asks(string words) => mine && Regex.IsMatch(low, $@"\b({words})\b");
        var day = low.Contains("tomorrow") ? DateTime.Today.AddDays(1) : low.Contains("today") || Regex.IsMatch(low, @"\b(my day|tonight|plans?|schedule|agenda)\b") ? DateTime.Today : (DateTime?)null;
        var lines = new List<string>();

        var todos = Asks("tasks?|to ?-?dos?|todo list|list|due|overdue|pending|left|plans?|schedule|agenda|today|tomorrow");
        var reminders = Asks("reminders?|alarms?|remind|plans?|schedule|agenda|today|tomorrow");
        var daily = Asks("daily tasks?|scheduled tasks?|briefing");
        var habits = Asks("habits?|streaks?");
        if (!todos && !reminders && !daily && !habits) return null;

        if (day is { } on && CalendarOn != null && (todos || reminders) && CalendarOn(on).OrderBy(e => e.Start).ToList() is { Count: > 0 } events)
            lines.Add("Calendar:\n" + string.Join("\n", events.Take(8).Select(e => $"• {e.Title} · {(e.Start.TimeOfDay == TimeSpan.Zero ? "all day" : e.Start.ToString("h:mm tt"))}")));
        if (s != null && todos)
        {
            var open = s.Todos.Where(t => !t.Done && (day == null || t.Due is { } d && d.Date <= day.Value) && (!low.Contains("overdue") || t.Due is { } o && o.Date < DateTime.Today))
                              .OrderBy(t => t.Due ?? DateTime.MaxValue).ThenByDescending(t => t.Priority).ToList();
            if (open.Count > 0)
                lines.Add($"To-dos ({open.Count}):\n" + string.Join("\n", open.Take(8).Select(t => $"• {t.Text}{(t.Due is { } d ? $" · {When(d, t.DueTime)}" : "")}{(t.Priority == 3 ? " ❗" : "")}")) + (open.Count > 8 ? $"\n…and {open.Count - 8} more in the Planner" : ""));
        }
        if (s != null && reminders)
        {
            var r = s.Schedules.Where(x => x.On && x.Kind == "reminder" && (day == null || x.When.Date == day.Value.Date || x.Repeat != "once")).OrderBy(x => x.When).ToList();
            if (r.Count > 0) lines.Add("Reminders:\n" + string.Join("\n", r.Take(8).Select(x => $"• {x.Text} · {(x.App != "" ? $"when {x.App} opens" : x.Repeat == "once" ? When(x.When) : $"{x.Repeat} at {x.When:h:mm tt}")}")));
        }
        if (s != null && daily)
        {
            var d = s.Schedules.Where(x => x.On && x.Kind == "task").ToList();
            if (d.Count > 0) lines.Add("Daily tasks:\n" + string.Join("\n", d.Take(8).Select(x => $"• {x.Text} · {x.Repeat} at {x.When:h:mm tt}")));
        }
        if (s != null && habits && s.Habits.Count > 0)
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            lines.Add("Habits today:\n" + string.Join("\n", s.Habits.Select(h => $"• {h.Name} {(h.Days.Contains(today) ? "✅" : "⬜")}")));
        }
        if (lines.Count == 0)
            return new(Pick($"Nothing on there{(day == DateTime.Today ? " for today" : day != null ? " for tomorrow" : "")} 🎉 you're all clear, bestie", "All clear! Nothing pending 💜 enjoy it ✨"), Mood: "chill");
        return new(Pick("Here's what you've got 📋", "Okay, your list 💜", "On your plate:") + "\n\n" + string.Join("\n\n", lines), Mood: "help");
    }


    // "/learn …" (or Look it up, under a reply she didn't get): she looks the message up online, the meanings of its
    // words (dictionary, Urban Dictionary) and the message itself (DuckDuckGo, Wikipedia), reads what it means as one of
    // her intents, answers that, and keeps it so it works offline next time. Found nothing: she says how to teach her.
    public async Task<Answer> LearnAsync(string said)
    {
        said = said.Trim();
        // something she can already answer: answer it, and keep how she read it
        if (IvoIntents.Best(IvoIntents.BuiltIn.Where(i => i.Id != "filler"), Words(said)) is { Best: { } known, Score: >= IvoIntents.Sure })
        {
            Memory.Phrases[IvoMemory.Key(said)] = known.Id;
            return Say(known);
        }
        var key = IvoMemory.Key(said);
        var heard = new List<string>(Words(said));
        var words = Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Unstretch)
                         .Where(w => w.Length >= 2 && !Filler.Contains(w)).Distinct().Take(4).ToList();
        foreach (var w in words)
        {
            string? m = Memory.Words.GetValueOrDefault(w);
            if (m == null) try { m = LookupOverride != null ? await LookupOverride(w) : await Meaning(w); } catch { }
            if (Keepable(m) is not { } kept || Rude(kept)) continue;
            Memory.Words[w] = m = kept;
            heard.AddRange(Words(m));
        }
        if (IvoIntents.Best(IvoIntents.BuiltIn.Where(i => i.Id != "filler"), heard) is { Best: { } meant, Score: >= 0.5 })
        {
            Memory.Phrases[key] = meant.Id;
            var a = Say(meant);
            return a with { Text = a.Text + Pick("\n\n(Learnt that one ✨)", "\n\n(Got it now, I'll remember 💜)") };
        }
        if (LookupOverride == null && await Find(said) is { } found && !Rude(found.Say))
        {
            Memory.Facts[key] = found.Say;
            last = new Topic([], "", found.More);
            return new(found.Say + "\n\n(Saved, I'll know it next time 📝)", Mood: "smart");
        }
        // nothing out there: her closest reply, then how to teach her the right one
        var closest = IvoIntents.Best(IvoIntents.BuiltIn.Where(i => i.Id != "filler"), heard);
        var tip = $"\n\n(Want a better answer? Teach me: \"when I say {said}, say …\")";
        return closest is { Best: { } near, Score: >= 0.3 } ? Say(near) with { Text = Say(near).Text + tip }
             : new(Pick("Hmm, that one's new to me 🤔 tell me more?", "Ooh, I don't know that one yet 👀 what do you think?", "Okay you got me there 😅 what's the answer?") + tip);
    }

    // something about a song or an artist, in a line, from the web (null: nothing found or offline)
    public static async Task<string?> AboutAsync(string subject) => (await Find(subject))?.Say;

    // a subject in a sentence or two, in her voice: the instant answer, else the closest encyclopedia article
    static async Task<(string Say, string More)?> Find(string about)
    {
        string? text = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var ddg = JsonNode.Parse(await web.GetStringAsync($"https://api.duckduckgo.com/?q={Uri.EscapeDataString(about)}&format=json&no_html=1&skip_disambig=1", cts.Token));
            text = (string?)ddg?["Answer"] is { Length: > 0 } a ? a : (string?)ddg?["AbstractText"] is { Length: > 40 } abs ? abs : null;
        }
        catch { }
        if (text == null)
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                // a full-text search, so "why do we sleep" finds Sleep, not just titles starting with the words
                var search = JsonNode.Parse(await web.GetStringAsync($"https://en.wikipedia.org/w/api.php?action=query&list=search&srlimit=1&format=json&srsearch={Uri.EscapeDataString(about)}", cts.Token));
                if (search?["query"]?["search"]?[0]?["title"] is { } title)
                {
                    var sum = JsonNode.Parse(await web.GetStringAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(((string)title!).Replace(' ', '_'))}", cts.Token));
                    text = (string?)sum?["extract"];
                }
            }
            catch { }
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Retell(about, text) is not { Say.Length: > 0 } said) return null;
        return said;
    }

    // what she found, said like she'd say it: the gist in a line, not the encyclopedia ("Albert Einstein? A German-born
    // physicist who…"), with the rest kept for "tell me more"
    internal static (string Say, string More) Retell(string about, string text)
    {
        text = Regex.Replace(text, @"\s*[\(\[][^\)\]]*[\)\]]", "");                       // (born 1879), [1], pronunciations
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var parts = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z])").Where(p => p.Length > 2).ToList();
        if (parts.Count == 0) return ("", "");
        var first = parts[0].TrimEnd('.');
        var words = first.Split(' ');
        if (words.Length > 28)                                                                  // a long one: up to a natural break
        {
            var cut = string.Join(' ', words.Take(28));
            var at = Math.Max(cut.LastIndexOf(", "), cut.LastIndexOf(" and "));
            first = (at > 40 ? cut[..at] : cut).TrimEnd(',', ' ') + "…";
        }
        // "X is a …" / "X was the …": say it back as "X? A …"
        var m = Regex.Match(first, @"^(.{2,60}?)\s+(?:is|was|are|were)\s+(?:(a|an|the)\s+)?(.+)$");
        static string Up(string x) => x == "" ? x : char.ToUpper(x[0]) + x[1..];   // as written, first letter up
        var gist = m.Success ? $"{Up(m.Groups[1].Value)}? {Up((m.Groups[2].Value + " " + m.Groups[3].Value).Trim())}" : Up(first);
        var say = Pick("Ooh, ", "Okay so, ", "Hmm, ", "Oh! ", "") + gist + Pick(" ✨", " 🤓", " 💜", "") + (parts.Count > 1 ? Pick(" Want more?", " Ask me for more if you want 👀", "") : "");
        var more = string.Join(' ', parts.Skip(1).Take(2));
        return (say, more != "" ? Pick("So basically, ", "Also, ", "And ") + char.ToLower(more[0]) + more[1..] : "That's all I've got on it rn 😅");
    }

    // the words in a message she doesn't know at all
    static List<string> Unknown(string said) =>
        Regex.Replace(said.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Unstretch).Where(w => w.Length >= 2 && !w.All(char.IsDigit) && !Short.ContainsKey(w) && !Vocab.Contains(w) && !Slang.ContainsKey(w) && !Memory.Words.ContainsKey(w))
            .Distinct().ToList();

    // what a word she doesn't know means: slang and acronyms only. An ordinary English word isn't looked up at all: she
    // (and the model) already know it, and the dictionary's first sense is rarely the one meant ("miss" isn't "a failure
    // to hit", "suck" isn't "not good" in "do you miss me").
    async Task<string?> Meaning(string w)
    {
        if (await Ordinary(w)) return null;
        // slang and acronyms: the best-liked definition worth keeping, cut to its first line
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var ud = await web.GetStringAsync($"https://api.urbandictionary.com/v0/define?term={Uri.EscapeDataString(w)}", cts.Token);
            if (JsonNode.Parse(ud)?["list"] is JsonArray { Count: > 0 } list)
            {
                var best = list.OfType<JsonObject>().Select(d => (Says: Keepable(Gist((string?)d["definition"])), Up: (int?)d["thumbs_up"] ?? 0))
                               .Where(d => d.Says != null).OrderByDescending(d => d.Up).FirstOrDefault();
                if (best.Says != null) return best.Says;
            }
        }
        catch { }
        return null;
    }

    // an everyday English word (the dictionary has it): not something to look up
    public static async Task<bool> Ordinary(string w)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var res = await web.GetAsync($"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(w)}", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // a meaning worth keeping for a word: a short, plain definition. Not a joke in someone's voice ("…I'll never be able
    // to touch"), nothing crude, and not the title of an album, a band or a film
    // ("romantic: the sixth studio album by…")
    internal static string? Keepable(string? meaning)
    {
        if (meaning is not { Length: >= 3 and <= 90 } m || Rude(m)) return null;
        if (Regex.IsMatch(m, @"(?i)\b(i|i'm|i'll|i've|i'd)\b")) return null;
        if (Regex.IsMatch(m, @"(?i)\b(album|band|single|song|track|film|movie|novel|series|episode|video game|studio|record label|company|brand|guitar|bass|rapper|singer|actor|actress)\b")) return null;
        return m;
    }

    // a definition's first sentence, without Urban's [links], lowercase-led, and not too long to say in passing
    static string? Gist(string? def)
    {
        if (def == null) return null;
        var line = Regex.Replace(def, @"[\[\]]", "").Split('\n', '\r')[0].Trim();
        line = Regex.Split(line, @"(?<=[.!?])\s")[0].Trim().TrimEnd('.', '!');
        if (line.Length < 2 || line.Length > 110) return null;
        return char.ToLowerInvariant(line[0]) + line[1..];
    }

    // crude words: whole words, plus a few stems (fucking, shitty…); "document" or "Sussex" never count
    static readonly string[] RudeStems = ["fuck", "shit", "bitch", "cunt", "nigg", "fagg", "porn", "whore", "slut", "motherf", "dickhead", "asshole", "bullshit"];
    static readonly HashSet<string> RudeWords = ["sex", "sexy", "dick", "pussy", "cock", "cum", "rape", "wtf", "stfu", "fml", "ffs", "bastard", "horny", "dtf", "nudes",
        "hoe", "fk", "fkn", "fking", "damn", "dammit", "crap", "lmfao", "tf", "ass", "prick", "twat", "wanker", "kinky", "naughty"];
    internal static bool Rude(string s) => Regex.Split(s.ToLowerInvariant(), @"[^\p{L}]+").Any(w => RudeWords.Contains(w) || RudeStems.Any(w.StartsWith));

    // something you told her is on today or was yesterday ("exam tomorrow"): her hello asks how it's going or went
    public static string? FollowUp(DateTime today)
    {
        foreach (var (key, said) in Memory.You.Where(x => x.Value.On is { } on && !x.Value.Asked && (on.Date == today.Date || on.Date == today.Date.AddDays(-1))).ToList())
        {
            Memory.You[key] = said with { Asked = true };
            return said.On!.Value.Date == today.Date ? Pick($"Isn't your {key} today? Good luck!! You've got this 💪💜", $"Today's the {key}, right? Sending you all the luck ✨")
                                                    : Pick($"How did the {key} go?? Tell me everything 🥺", $"Wait, how was the {key} yesterday? 👀");
        }
        return null;
    }

    string Greet()
    {
        chatty = true;
        if (FollowUp(DateTime.Today) is { } ask) return Pick($"Hey{(name != "" ? " " + name : "")}! ", "Hiii! ") + ask;
        // now and then, what you're into: "how's the guitar going?"
        if (rnd.Next(3) == 0 && Memory.You.Where(x => x.Key is "learning" or "project" or "book" or "show" or "game" or "studies").OrderByDescending(x => x.Value.Said).FirstOrDefault() is { Key: { } into } k)
            return Pick($"Hey{(name != "" ? " " + name : "")}! ", "Hiii! ") + (into switch
            {
                "learning" => $"How's {k.Value.What} coming along? 🎸",
                "book" => $"Still reading {k.Value.What}? Any good? 📚",
                "show" => $"Did you finish {k.Value.What} yet? 👀",
                "game" => $"Still playing {k.Value.What}? 🎮",
                "studies" => $"How are the {k.Value.What} studies going? 📖",
                _ => $"How's {k.Value.What} going? ✨",
            });
        var h = DateTime.Now.Hour;
        var part = h < 5 ? "Hey night owl" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
        if (Pack("greet") is { Length: > 0 } hey && rnd.Next(4) == 0) return Pick(hey).Replace("{name}", name != "" ? name : "you").Replace("{part}", part);
        return Pick($"{part}{(name != "" ? ", " + name : "")}! 💜 What's on your mind?", $"Hey{(name != "" ? " " + name : "")}! ✨ How can I help?", $"Hi hi! So nice to see you{(name != "" ? ", " + name : "")} 💜");
    }

    static string Cap(string s) => s == "" ? s : char.ToUpper(s[0]) + s[1..].ToLowerInvariant();

    static readonly string[] Jokes =
    [
        "Why did the computer go to the doctor? It had a virus 🤒",
        "I told my laptop I needed a break… and it froze 🥶",
        "Why do programmers prefer dark mode? Because light attracts bugs 🐛",
        "What do you call a fake noodle? An impasta 🍝",
        "Why don't eggs tell jokes? They'd crack each other up 🥚",
        "I'm reading a book about anti-gravity. It's impossible to put down 📚",
        "Why did the alien bring a ladder? To reach the next level 👽",
        "What do aliens put on their toast? Space jam 🛸",
    ];
    static readonly string[] Facts =
    [
        "Honey never spoils: pots thousands of years old were still good to eat",
        "Octopuses have three hearts and blue blood",
        "A day on Venus is longer than its year",
        "Bananas are berries, but strawberries aren't",
        "There are more possible games of chess than atoms in the observable universe",
        "Sharks are older than trees",
        "Your brain uses about 20% of your body's energy",
    ];

    // "5 km in miles", "30 c to f", "10 kg in pounds"
    static readonly (string From, string To, Func<double, double> F)[] Units =
    [
        ("km", "miles", x => x * 0.621371), ("miles", "km", x => x / 0.621371), ("mile", "km", x => x / 0.621371),
        ("kg", "pounds", x => x * 2.20462), ("pounds", "kg", x => x / 2.20462), ("lbs", "kg", x => x / 2.20462),
        ("c", "f", x => x * 9 / 5 + 32), ("f", "c", x => (x - 32) * 5 / 9), ("celsius", "fahrenheit", x => x * 9 / 5 + 32), ("fahrenheit", "celsius", x => (x - 32) * 5 / 9),
        ("cm", "inches", x => x / 2.54), ("inches", "cm", x => x * 2.54), ("m", "feet", x => x * 3.28084), ("feet", "m", x => x / 3.28084),
        ("l", "gallons", x => x * 0.264172), ("gallons", "l", x => x / 0.264172),
    ];
    static string? Convert(string t)
    {
        var m = Regex.Match(t, @"(-?\d+(?:\.\d+)?)\s*([a-z]+)\s+(?:in|to|into)\s+([a-z]+)");
        if (!m.Success) return null;
        var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var u = Units.FirstOrDefault(u => u.From == m.Groups[2].Value && u.To == m.Groups[3].Value);
        return u.F == null ? null : $"{x:0.##} {u.From} is {u.F(x):0.##} {u.To} ✨";
    }

    // ---------- what she knows about Ivo ----------
    // Words: the words (after synonyms fold) that point to the topic, with how strongly. Say: the answer. More: for "how?".
    sealed record Topic(Dictionary<string, int> Words, string Say, string More = "");
    static Topic T(string words, string say, string more = "") =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Split(':')).ToDictionary(p => p[0], p => p.Length > 1 ? int.Parse(p[1]) : 1), say, more);

    static readonly Topic[] Topics =
    [
        T("model:3 add:1 key:3 change:1 use:1",
          "Ivo talks to the AI models you choose 🤖 Cloud ones with your own API key (OpenAI, Anthropic, Gemini, Groq and more), gateways like OpenRouter, models on this PC (Ollama, LM Studio), or AI command-line tools like Claude CLI.",
          "Open Settings → API keys and paste a key, or press Scan this PC to find tools and keys already here. Then pick the model at the bottom right of any chat ✨"),
        T("message:2 add:1 ask:1 type:1 send:1",
          "Chats are where you talk to a model 💬 Press New chat at the bottom of the list, type, and send. You can have up to three chats side by side.",
          "Select any text in a reply and choose Reply to quote it. Type / for saved prompts, and /compare to ask up to three models at once. The eye in a chat's header makes it incognito."),
        T("search:3 message:1 find:1",
          "Search chats, at the top of the list, finds words inside every chat and jumps right to the message 🔎"),
        T("pin:3 group:2 archive:3 project:3 organise:2 organize:2",
          "Right-click a chat to pin it, put it in a group or archive it 📌 Right-click a group's name to turn it into a project with its own instructions, model and folder."),
        T("agent:3 agents:3 folder:2 workspace:3",
          "Open a folder in a chat and the model becomes an agent that can read, search and edit files there and run commands 🛠️",
          "It has modes (Plan only reads; Normal checks in; Autopilot works on its own) and permissions (Manual, Assisted, All). File changes can be undone, and Settings → Agents has specialists it can hand work to."),
        T("code:3 terminal:3 git:3 editor:3",
          "Code mode is a little IDE: files, an editor, terminals and Git 💻 It's off at first: turn it on in Settings → Appearance → Modes, then switch with Chat | Code in the title bar."),
        T("remind:3 add:1",
          "Reminders ⏰ Type something like \"call mom at 6pm\" in Quick capture (Ctrl+Alt+N, from anywhere), or add one in the Planner. They can repeat, or ring when an app opens.",
          "When one rings you can snooze it: 10 minutes, an hour, after lunch, this evening or tomorrow. Missed ones wait in the Inbox."),
        T("todo:3 planner:3 add:1",
          "Your to-do list lives in the Planner ✅ One line does it all: \"Call mom tomorrow 6pm !high #family ~Home every week\" sets the day, priority, tag, list and repeat.",
          "Views: Today, Next 7 days, All and each list. The tick's colour shows priority, and ticking a repeating one moves it to its next time."),
        T("inbox:3 briefing:3 morning:2",
          "The Inbox collects reminders, task results and your morning briefing, as cards on a wheel you can flick through 📥",
          "Daily tasks are prompts that run on a schedule; every morning they arrive together as one briefing. Set the time in Settings → Tasks."),
        T("calendar:3",
          "The Calendar shows holidays, your events and everything Ivo has scheduled 📅 Click a day to add an event, a reminder or a task.",
          "Settings → Calendar picks your country's holidays, and can show each day in your region's own calendar too, like Saka for India."),
        T("focus:3 timer:3 pomodoro:3 habit:3",
          "Focus, on the rail, is a Pomodoro timer (15 to 60 minutes, then a break) with your week's focus as bars, and habits you tick each day to grow a streak 🔥"),
        T("notes:3 note:3 scratchpad:3",
          "Notes, on the rail, is one scratchpad that saves as you type 📝 It stays on this PC, sealed with the app lock."),
        T("ocr:3 copy:1 screen:1 picture:1 text:1",
          "Text from screen, on the rail, freezes the screen so you can drag a box over any text 🔍 Windows reads it on this PC and copies it for you."),
        T("music:3 listen:1 together:1 lyrics:3",
          "Spotify lives in Ivo 🎵 The island in the title bar shows what's playing; click it for a player. The Spotify panel on the rail has search, playlists, lyrics and your queue.",
          "Connect it in Settings → Connections (you sign in with Spotify in the browser). Controlling playback needs Spotify Premium. In Portal's Chats, the green music button invites a contact to listen along."),
        T("portal:3 contact:2 connect:1",
          "Portal, on the rail, connects you to people on Ivo or Ivo Portal, with no accounts and no server of Ivo's 🌐 Swap an invite once, add each other to contacts, and from then on chat and call with no codes.",
          "Its tabs are Chats, Contacts and New portal. Everything is sealed before it leaves your PC."),
        T("call:3 video:3 screen:2",
          "Calls 📞 Open a contact's chat in Portal and press the phone (voice) or camera (video) button. You'll see a 'Ready to join?' preview first, and you can share your screen during the call."),
        T("file:3 send:1 picture:1",
          "In Portal's Chats you can send files up to 15 MB 📎 They're sealed before they leave your PC, so only the other person can open them."),
        T("group:3 add:1 contact:1",
          "Groups, in Portal's Chats 👥 Whoever makes one is the admin and adds or removes people. Members can invite their own contacts, and the admin lets them in."),
        T("delete:3 message:1",
          "Right-click a message in Portal's Chats to react, reply or copy it, Delete for me, or, for your own, Unsend, and it's gone for everyone 🗑️"),
        T("status:3 invisible:3 busy:3 away:3 picture:3",
          "Click your picture in Portal's Chats to change it, or to set your status: Active, Busy, Away or Invisible 🌙 Invisible shows you as offline."),
        T("slow:3 message:1 call:1 internet:1 connect:1 offline:1",
          "If messages are slow or a call won't connect 🛠️ make sure both of you have the latest Ivo. Messages go through two free relays at once and wait in your outbox until they go.",
          "Some strict networks (offices, colleges) block direct calls. Ivo only says you're offline when nothing at all answers, so if it says that, check your Wi-Fi."),
        T("lock:3 privacy:3 private:3 encrypt:3 security:3",
          "Your privacy 🔒 Keys are encrypted to your Windows account, and the app lock (Settings → App lock) adds a PIN or password and encrypts your chats, notes and more.",
          "When you set the lock you get eight recovery codes: save them somewhere safe. The lock can also kick in when Windows locks, or after a while without use."),
        T("theme:3 change:1",
          "Settings → Appearance has the themes 🎨 Default follows Windows' light or dark mode, AMOLED is true black, and Ivo is violet and cyan with a drifting aurora. The slider in the title bar flips light and dark."),
        T("launch:2 animation:2 splash:3 intro:2 startup:2",
          "The launch animation is in Settings → Appearance: Ivo (the default), AMOLED, Transparent, or Off ✨"),
        T("orb:3 animation:1",
          "The orb on an empty chat can turn and breathe. It's off at first; switch it on in Settings → Appearance 🔮"),
        T("voice:3 read:2 listen:1",
          "Ivo can read any reply aloud 🔊 Windows' voices work straight away, and Settings → Voices has natural voice packs that run offline on your PC."),
        T("dictation:3 mic:3 microphone:3 speak:2",
          "Press the mic in the message box and speak 🎤 Ivo types what you say."),
        T("shortcut:3 shortcuts:3 keyboard:3 hotkey:3",
          "Shortcuts ⌨️ Ctrl+Shift+Space shows or hides Ivo from anywhere. Ctrl+Alt+Space opens the companion, a small floating chat. Ctrl+Alt+N is quick capture, and Ctrl+Alt+S puts a screenshot in the message box."),
        T("tray:3 background:3 close:1 startup:1",
          "Closing the window keeps Ivo in the tray, quietly 🌙 Right-click the tray icon for quick access, or turn on Start with Windows in Settings."),
        T("companion:3 floating:3",
          "The companion (Ctrl+Alt+Space) is a little floating chat over whatever you're doing 💭"),
        T("memory:3 remember:3 profile:2",
          "Say \"remember that…\" in a chat and the model saves it for future chats 🧠 See or clear what's remembered in Settings → Profile."),
        T("backup:3 restore:3 export:3",
          "Settings → Data & storage backs up your chats, settings, keys, agents and skills into one file, and restores them 💾 You can also export a chat to Markdown."),
        T("webhook:3 webhooks:3 slack:3 discord:3 teams:3",
          "Webhooks, on the rail, post your reminders, task results and briefing to Slack, Discord, Teams, Google Chat or any URL 🔗 Send a test checks it."),
        T("connection:3 connections:3 github:3 notion:3 gitlab:3",
          "Settings → Connections lets chats use your GitHub, GitLab, Notion and Spotify 🔌 Reading never asks; anything that changes something asks first."),
        T("spending:3 cost:3 budget:3 money:3 price:3",
          "Settings → Spending sets a daily limit and a monthly budget per provider 💸 Each message shows its tokens and an estimated cost."),
        T("settings:3",
          "Settings is the gear at the bottom of the rail ⚙️ Everything lives there: models, the app lock, looks, voices, connections and more."),
        T("can:1 you:1 do:1 features:3 help:3",
          "Loads 💜 I can set reminders, alerts, daily tasks, to-dos and habits, play music on Spotify, call or message your contacts, change any setting (\"switch theme to amoled\"), tell you what's on today and where things are, do maths, and just chat. Type / to see what I can do, or say \"show me around\"."),
    ];
}
