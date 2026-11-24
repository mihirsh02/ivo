using System.Globalization;
using System.Text.RegularExpressions;

namespace Ivo;

// Something you asked Ivo to do. Kind: reminder | task | alert | todo | habit | play | pause | next | previous | call |
// message | set | open. Args: text, when ("yyyy-MM-dd HH:mm"), repeat (once|daily|weekdays|weekly), who, video ("1"),
// query, setting, value, target. Missing: the one thing she still has to ask for (when | text | who | value).
public sealed record IvoAction(string Kind, Dictionary<string, string> Args, string? Missing = null);

// What you ask her to do, in plain words ("remind me at 5 to call mom", "play blinding lights", "switch theme to
// amoled") or with a / command. Chatter around it ("lol ok …", "hey ivo can you …") is ignored. Null: not a command.
public static class IvoCommands
{
    // the / list: the command, what goes after it, what it does, and its emoji
    public static IReadOnlyList<(string Slash, string Args, string Hint, string Icon)> Slashes { get; } =
    [
        ("/remind", "when  what", "Set a reminder · /remind tomorrow 9am pay rent", "⏰"),
        ("/todo", "what", "Add to your to-dos · /todo buy milk", "✅"),
        ("/task", "when  prompt", "A daily task, run every day · /task 8am give me a word of the day", "🗓️"),
        ("/alert", "when  what", "An alert · /alert 18:30 take meds", "🔔"),
        ("/habit", "name", "Track a habit in Focus · /habit stretch", "🔥"),
        ("/play", "song, artist or playlist", "Play on Spotify · /play blinding lights", "🎵"),
        ("/call", "contact  [video]", "Call a Portal contact · /call Riya video", "📞"),
        ("/message", "contact  text", "Message a Portal contact · /message Aman running late", "💬"),
        ("/set", "setting  value", "Change a setting · /set theme amoled", "⚙️"),
        ("/open", "section or panel", "Open a settings section or panel · /open notifications", "↗️"),
        ("/queue", "song", "Add a song to your Spotify queue · /queue tere bina", "📋"),
        ("/spotify", "anything", "Anything Spotify: shuffle on, volume 40, like this song, play my gym playlist, my top songs…", "🎧"),
        ("/playing", "", "See what's playing on Spotify · /playing", "🎶"),
        ("/lyrics", "", "Show lyrics for the song playing now · /lyrics", "🎤"),
        ("/playlists", "", "List your Spotify playlists · /playlists", "📚"),
        ("/recent", "", "See what you played recently · /recent", "🕘"),
        ("/youtube", "channel", "New videos, no account · /youtube mkbhd · follow mkbhd on youtube", "📺"),
        ("/reddit", "subreddit  [week]", "Top posts, no account · /reddit india week", "🟠"),
        ("/learn", "what you said", "Ivo looks it up online and learns how to answer it · /learn heylol", "🔎"),
    ];

    static readonly RegexOptions I = RegexOptions.IgnoreCase;
    const string Chatter = @"^(?:(?:lol|lmao|haha|ok|okay|okie|kk|hey|hi|yo|so|um|uh|and|also|pls|please|plz|ivo|bestie|babe|now|quick|quickly|can you|could you|would you|will you|can u|go ahead and|i want you to|i need you to)\b[\s,!.]*)+";

    public static IvoAction? Parse(string said, DateTime now)
    {
        var s = Regex.Replace(said.Trim(), Chatter, "", I).Trim().TrimEnd('.', '!');
        s = Short(s);
        if (s.StartsWith('/')) return Slash(s, now);
        Match m;
        // real work (code, an essay, a plan…) goes to your AI models in a chat of its own: she hands it over
        if (Regex.IsMatch(s, @"^(?:can you |could you |pls |please )?(?:write|make|create|build|generate|give me|show me|code|fix|debug|explain|refactor)\b.{0,40}\b(?:code|program|script|function|class|app|website|algorithm|sql|query|regex|essay|cover letter|resume|email)\b|\b(?:in|using) (?:python|c\+\+|c#|c|java|javascript|typescript|rust|go|kotlin|swift|php|ruby|sql|html|css)\s*$", I))
            return new("ask", new() { ["text"] = said.Trim() });

        if ((m = Regex.Match(s, @"^(?:remind me|set (?:a |me a )?reminder|add (?:a )?reminder|new reminder|reminder)\b[:\s]*(.*)$", I)).Success)
            return Timed("reminder", m.Groups[1].Value, now);
        if ((m = Regex.Match(s, @"^(?:set (?:an |me an )?alert|add (?:an )?alert|new alert|alert me|alert)\b[:\s]*(.*)$", I)).Success)
            return Timed("alert", m.Groups[1].Value, now);
        if ((m = Regex.Match(s, @"^(?:add|create|set up|schedule|new) (?:a )?(?:daily|scheduled) task\b[:\s]*(.*)$", I)).Success)
            return Timed("task", m.Groups[1].Value, now, repeat: "daily");
        // a habit done today: "tick stretch", "i did my stretch", "done with gym" (her chat checks it's one of yours)
        if ((m = Regex.Match(s, @"^(?:tick|check off|check|mark)(?: off)? (?:my )?(.+?)(?: as done| done| off| for today)?$|^i (?:did|finished|completed|have done|went to|went for) (?:my |the )?(.+?)(?: today)?$|^done (?:with )?(?:my )?(.+?)(?: today)?$", I)).Success)
            return new("habitdone", new() { ["text"] = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim().ToLowerInvariant() });
        if ((m = Regex.Match(s, @"^(?:add|put|create|start|track|new) (?:a |new )?habit\b[:\s]*(.+)$", I)).Success)
            return new("habit", new() { ["text"] = m.Groups[1].Value.Trim() });
        if ((m = Regex.Match(s, @"^(?:add|put) (.+?) (?:to|on|in) (?:my |the )?(?:to-?dos?|todo list|to do list|list|planner)$", I)).Success
            || (m = Regex.Match(s, @"^(?:add|create|new) (?:a )?(?:to-?do|to do|task)\b[:\s]*(.+)$", I)).Success
            || (m = Regex.Match(s, @"^(?:to-?do|todo)\b[:\s]+(.+)$", I)).Success)
            return new("todo", new() { ["text"] = m.Groups[1].Value.Trim() });

        // music: said with a music word, so "stop", "next" or "continue" on their own stay chat
        if (Regex.IsMatch(s, @"^(?:turn|switch|shut) off(?: the| my)? (?:music|song|spotify)$|^(?:turn|switch) (?:the |my )?(?:music|song|spotify) off$|^pause(?: the| my)?(?: music| song| track| spotify)?$|^stop(?: the| my)? (?:music|song|track|spotify|playing)$", I)) return new("pause", []);
        if (Regex.IsMatch(s, @"^(?:next|skip)(?: (?:this|the))? (?:song|track)$|^(?:play )?(?:the )?next (?:song|track|one)$|^play next$", I)) return new("next", []);
        if (Regex.IsMatch(s, @"^(?:previous|prev|go back|back|last)(?: song| track)$|^play (?:the )?previous(?: song| track| one)?$", I)) return new("previous", []);
        if (Regex.IsMatch(s, @"^(?:resume|unpause|continue)(?: the| my)? (?:music|song|spotify)$|^(?:unpause|play|resume)$|^(?:turn|switch) on(?: the| my)? (?:music|spotify)$|^(?:turn|switch) (?:the |my )?(?:music|spotify) on$|^play(?: some| my)? (?:music|songs|spotify)$", I)) return new("play", new() { ["query"] = "" });
        if (Regex.IsMatch(s, @"^(?:(?:on )?which device|where)(?: is)?(?: (?:the|my))? (?:music|song|spotify)(?: is)?(?: playing| on)?\??$|^(?:which|what) device is (?:it|the music|spotify|this) (?:playing )?on\??$", I)) return new("playing", new() { ["device"] = "1" });
        if (Regex.IsMatch(s, @"^(?:(?:show|give|get|tell)(?: me)? )?(?:the )?lyrics(?: (?:to|of|for) (?:this|the) song)?\??$|^(?:what are the|whats the) lyrics\??$|^sing (?:this|the|me this|me the) song$|^sing along$", I)) return new("lyrics", []);
        if (Regex.IsMatch(s, @"^(?:tell me (?:about|something about)|what do you know about|talk about) (?:this|the) (?:song|track)\??$|^(?:what is|whats) this song about\??$", I)) return new("song", []);
        if (Regex.IsMatch(s, @"^(?:who sings this(?: song)?|who is (?:this|the) (?:artist|singer)|tell me (?:about|something about) (?:this|the) (?:artist|singer|band))\??$", I)) return new("artist", []);
        // the song's name, asked any way: a short question about "the song" or "this track" with name / called / which / what
        if (s.Split(' ').Length <= 9 && Regex.IsMatch(s, @"\b(?:song|songs|track|music)\b", I) && !Regex.IsMatch(s, @"\b(?:play|plays|lyrics|sing|about|love|like|hate|send|add|recommend|suggest|next|upcoming|after|queue|queued|previous|last)\b", I)
            && (Regex.IsMatch(s, @"\b(?:name|called|title)\b", I) || Regex.IsMatch(s, @"^(?:what|which|whats|what's)\b", I) && Regex.IsMatch(s, @"\b(?:this|the|that|current|currently|it|on|playing|played|being|now)\b", I)))
            return new("playing", []);
        if (Regex.IsMatch(s, @"^(?:what(?:'?s| is)(?: this)?(?: song)? playing|what am i (?:playing|listening to)|what(?:'?s| is) this song|(?:what|which) (?:song|track|music) is (?:this|playing|on|that)(?: now| right now)?|(?:what|which) (?:song|track) (?:are you playing|am i listening to)|whats on spotify|now playing|which song is this)\??$", I)) return new("playing", []);
        // everything else Spotify does: shuffle, repeat, volume, seek, liking, playlists, albums, artists, podcasts, your queue,
        // recent and top songs, following, devices. Kind "music", Args op (+ value).
        if (Music(s) is { } music) return music;

        // YouTube (public feeds) and Reddit (public listings), no account
        if (Social(s) is { } social) return social;

        // the queue: "queue X", "add X to the queue", "play X next" (but "play next" is the next song)
        if ((m = Regex.Match(s, @"^(?:queue|add|put)(?: up)? (.+?) (?:to|in|on|into) (?:the |my )?(?:queue|up next)$|^queue(?: up)? (.+)$|^play (.+?) (?:next|after this(?: one)?)$", I)).Success
            && !Regex.IsMatch(m.Value, @"^play (?:the )?next\b", I))
            return new("queue", new() { ["query"] = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim() });
        if (Regex.IsMatch(s, @"^queue(?: up)?$", I)) return new("queue", new() { ["query"] = "" }, "query");
        if ((m = Regex.Match(s, @"^play (.+?)(?: on spotify)?$", I)).Success && !Regex.IsMatch(m.Groups[1].Value, @"^(?:with|a game|games|it|that|this|along|around|outside|fair|nice|dead)\b", I))
            return new("play", new() { ["query"] = m.Groups[1].Value.Trim() });

        if ((m = Regex.Match(s, @"^(video call|voice call|facetime|call|ring|phone)\s+([\p{L}][\p{L}\-' ]{0,30}?)(?:\s+on video|\s+(?:a )?video)?$", I)).Success
            && !Regex.IsMatch(m.Groups[2].Value, @"^(?:me|you|it|that|this|him|her|them|back|now|later)\b", I))
            return new("call", new() { ["who"] = m.Groups[2].Value.Trim().ToLowerInvariant(), ["video"] = Regex.IsMatch(m.Value, @"video|facetime", I) ? "1" : "0" });

        if ((m = Regex.Match(s, @"^(?:text|message|msg|dm|send (?:a )?(?:message|text|msg) to)\s+([\p{L}][\p{L}\-']{0,30})\s*[:,]?\s*(?:saying |that |to say )?(.*)$", I)).Success
            && !Regex.IsMatch(m.Groups[1].Value, @"^(?:me|you|it|that|this)$", I))
        {
            var text = m.Groups[2].Value.Trim();
            return new("message", new() { ["who"] = m.Groups[1].Value.ToLowerInvariant(), ["text"] = text }, text == "" ? "text" : null);
        }

        if ((m = Regex.Match(s, @"^turn (on|off) (?:the |my )?(.+)$", I)).Success) return Set(m.Groups[2].Value, m.Groups[1].Value);
        if ((m = Regex.Match(s, @"^turn (?:the |my )?(.+?) (on|off)$", I)).Success) return Set(m.Groups[1].Value, m.Groups[2].Value);
        if ((m = Regex.Match(s, @"^(enable|disable|switch on|switch off) (?:the |my )?(.+)$", I)).Success) return Set(m.Groups[2].Value, m.Groups[1].Value.EndsWith("off") || m.Groups[1].Value == "disable" ? "off" : "on");
        if ((m = Regex.Match(s, @"^(?:switch|change|set|make|put) (?:the |my )?(.+?) (?:to|into|as) (.+)$", I)).Success) return Set(m.Groups[1].Value, m.Groups[2].Value);
        if ((m = Regex.Match(s, @"^make (?:the |my )?(.+?) (bigger|smaller|larger|louder|quieter|brighter|darker|faster|slower)$", I)).Success) return Set(m.Groups[1].Value, m.Groups[2].Value);

        if ((m = Regex.Match(s, @"^(?:open|go to|take me to|show me) (?:the |my )?(.+?)(?: settings?| page| panel| tab)?$", I)).Success
            && m.Groups[1].Value.Split(' ').Length <= 3 && !Regex.IsMatch(m.Groups[1].Value, @"^(?:around|everything|how|what|a |something)", I))
            return new("open", new() { ["target"] = m.Groups[1].Value.Trim().ToLowerInvariant() });
        return null;
    }

    // short and Gen Z ways of asking, read as the long way: "nxt pls", "wats playin", "vol up", "skip it bestie"
    static readonly (string From, string To)[] Shorthand =
    [
        (@"\bnxt\b", "next"), (@"\bprev\b", "previous"), (@"\bvol\b", "volume"), (@"\bwats\b|\bwhats\b|\bwuts\b", "what's"), (@"\bwat\b|\bwut\b", "what"),
        (@"\bplayin\b", "playing"), (@"\bsongz\b", "songs"), (@"\bu\b", "you"), (@"\bur\b", "your"), (@"\btmrw\b|\btmr\b", "tomorrow"),
    ];
    static string Short(string s)
    {
        foreach (var (from, to) in Shorthand) s = Regex.Replace(s, from, to, I);
        var polite = Regex.Replace(s, @"(?:[\s,]+(?:pls|plz|please|rn|now|fr|bestie|babe|ivo|thx|thanks|ty))+\s*(\??)$", "$1", I).Trim();   // the politeness at the end
        if (polite != s && polite.Equals("next", StringComparison.OrdinalIgnoreCase)) return "skip this song";   // "nxt pls": a request, not a word
        s = polite;
        // a short question about music: "next song?", "song?", "who sings?"
        return s.ToLowerInvariant() switch
        {
            "next song?" or "next track?" or "next one?" or "up next?" or "queue?" or "the queue?" or "what's next?" or "what's next" or "whats next" => "what's in my queue",
            "song?" or "this song?" or "current song?" or "the song?" or "song name?" or "track?" or "what's this song" or "what's this song?" or "what's playing" or "what's playing?" => "what's playing",
            "who sings?" or "singer?" or "artist?" or "who sings this?" or "who's this?" or "who's singing?" => "who sings this",
            "lyrics?" or "lyrics pls" => "lyrics",
            "playlists?" or "my playlists?" => "show my playlists",
            "skip" or "skip it" or "skip this" or "skip this one" or "next song" or "next track" or "next one" => "skip this song",
            "previous" or "previous song" or "go back" or "last song" => "previous song",
            "pause" or "pause it" or "pause music" or "stop music" => "pause",
            "louder" or "volume up" or "turn it up" => "turn it up",
            "quieter" or "softer" or "volume down" or "turn it down" => "turn it down",
            _ => s,
        };
    }

    static IvoAction? Social(string s)
    {
        IvoAction Yt(string op, string value = "") => new("youtube", new() { ["op"] = op, ["value"] = value });
        IvoAction Rd(string sub, string sort = "top", string range = "day") => new("reddit", new() { ["sub"] = sub, ["sort"] = sort, ["range"] = range });
        Match m;
        if ((m = Regex.Match(s, @"^(?:follow|subscribe to|add) (.+?) (?:on|to) youtube$|^subscribe to (.+)$", I)).Success) return Yt("follow", (m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value.Trim());
        if ((m = Regex.Match(s, @"^(?:unfollow|unsubscribe from|remove) (.+?) (?:on|from) youtube$", I)).Success) return Yt("unfollow", m.Groups[1].Value.Trim());
        if (Regex.IsMatch(s, @"^(?:show |list )?(?:my )?(?:youtube channels|channels i follow|youtube subscriptions)\??$", I)) return Yt("list");
        if (Regex.IsMatch(s, @"^(?:any |what'?s |show me )?(?:new|latest|anything new)(?: videos)? on (?:my )?youtube\??$|^(?:my )?youtube(?: feed)?\??$|^any new videos\??$", I)) return Yt("new");
        if ((m = Regex.Match(s, @"^(?:any |show me |what are the )?(?:new|latest|recent) (?:videos?|uploads?) (?:from|by|on) (.+?)\??$|^what did (.+?) (?:upload|post)(?: on youtube)?\??$|^(.+?)'?s? (?:latest|new) videos?\??$", I)).Success)
            return Yt("new", (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim());
        static string Range(string w) => w.ToLowerInvariant() switch { "today" or "" => "day", "this week" or "week" => "week", "this month" or "month" => "month", "this year" or "year" => "year", "all time" or "ever" => "all", _ => "day" };
        if ((m = Regex.Match(s, @"^(?:what'?s |whats )?(top|hot|best|new|trending|latest)? ?(?:posts? )?(?:in|on|from) r/(\w+)(?: (today|this week|this month|this year|all time))?\??$|^r/(\w+)(?: (today|this week|this month|this year|all time|week|month|year))?$", I)).Success)
        {
            var sub = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
            var word = m.Groups[1].Value.ToLowerInvariant();
            return Rd(sub, word is "hot" or "trending" ? "hot" : word is "new" or "latest" ? "new" : "top", Range(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[5].Value));
        }
        if ((m = Regex.Match(s, @"^(?:top|hot|best|trending) (?:posts )?(?:in|on) (\w+) subreddit(?: (today|this week|this month))?\??$", I)).Success) return Rd(m.Groups[1].Value, "top", Range(m.Groups[2].Value));
        if (Regex.IsMatch(s, @"^(?:what'?s |whats )?(?:hot|trending|popular|new) on reddit(?: today)?\??$|^reddit$", I)) return Rd("popular", "hot");
        return null;
    }

    static IvoAction? Music(string s)
    {
        IvoAction Op(string op, string value = "") => new("music", new() { ["op"] = op, ["value"] = value });
        Match m;
        if ((m = Regex.Match(s, @"^(?:turn |switch )?shuffle (on|off)$|^turn (on|off) shuffle$|^(?:stop|no) shuffl(?:e|ing)$|^shuffle(?: (?:it|my music|the playlist))?$", I)).Success)
            return Op("shuffle", m.Groups[1].Success ? m.Groups[1].Value.ToLowerInvariant() : m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : m.Value.StartsWith("stop") || m.Value.StartsWith("no") ? "off" : "on");
        if (Regex.IsMatch(s, @"^(?:put )?(?:on )?repeat (?:this|the) (?:song|track|one)$|^loop (?:this|the) (?:song|track)$|^repeat one$", I)) return Op("repeat", "track");
        if (Regex.IsMatch(s, @"^repeat (?:the |this )?(?:playlist|album)$|^repeat all$|^(?:turn )?repeat on$", I)) return Op("repeat", "context");
        if (Regex.IsMatch(s, @"^(?:stop repeat(?:ing)?|repeat off|turn off repeat|no repeat|stop looping)$", I)) return Op("repeat", "off");
        if ((m = Regex.Match(s, @"^(?:set )?(?:the )?volume (?:to )?(\d{1,3})%?$|^(?:turn it|turn the volume|volume) (up|down)$|^make it (louder|quieter|softer)$|^(louder|quieter)$|^(mute|unmute)(?: (?:it|the music|spotify))?$", I)).Success)
        {
            var v = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value + m.Groups[5].Value).ToLowerInvariant() switch
            { "up" or "louder" => "up", "down" or "quieter" or "softer" => "down", "mute" => "0", "unmute" => "50", var x => x };
            return Op("volume", v);
        }
        if ((m = Regex.Match(s, @"^(?:skip|jump|go|seek)(?: ahead)? to (\d{1,2}):(\d{2})$", I)).Success) return Op("seek", (int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value)).ToString());
        if (Regex.IsMatch(s, @"^(?:restart|replay)(?: (?:the|this) (?:song|track))?$|^(?:start|play) (?:the|this) song (?:again|from the (?:start|beginning))$", I)) return Op("seek", "0");
        if ((m = Regex.Match(s, @"^(?:go )?(back|rewind|forward|skip ahead|fast forward)(?: by)? (\d{1,3}) ?(?:seconds|secs|s)$", I)).Success)
            return Op("seek", (m.Groups[1].Value.ToLowerInvariant() is "back" or "rewind" ? "-" : "+") + m.Groups[2].Value);
        if (Regex.IsMatch(s, @"^(?:like|save|heart|love) (?:this|the) (?:song|track)$|^add (?:this|it|the song) to (?:my )?liked(?: songs)?$|^save (?:this|it)$", I)) return Op("like");
        if (Regex.IsMatch(s, @"^(?:unlike|unsave) (?:this|the|it)(?: song| track)?$|^remove (?:this|it|the song)(?: song)? from (?:my )?liked(?: songs)?$", I)) return Op("unlike");
        if ((m = Regex.Match(s, @"^add (?:this|it|the song|this song|this track) to (?:my )?(?:playlist )?(.+?)(?: playlist)?$", I)).Success && !Regex.IsMatch(m.Groups[1].Value, @"^(?:the )?(?:queue|liked|liked songs|my liked songs)$", I))
            return Op("addto", m.Groups[1].Value.Trim().ToLowerInvariant());
        if (Regex.IsMatch(s, @"^play (?:my )?liked songs$|^play (?:my )?(?:favourites|favorites|saved songs)$", I)) return Op("liked");
        if ((m = Regex.Match(s, @"^play my (.+?) playlist$|^play (?:my )?playlist (.+)$", I)).Success) return Op("myplaylist", (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().ToLowerInvariant());
        if (Regex.IsMatch(s, @"^(?:show|list|what are)(?: me)? my playlists\??$|^my playlists$", I)) return Op("playlists");
        if (Regex.IsMatch(s, @"^(?:what'?s|what is|show(?: me)?)(?: in)? (?:my |the )?queue\??$|^(?:what'?s|what is) (?:up )?next\??$|^my queue$|^(?:show|list|what are)(?: me)? (?:my |the )?(?:next|upcoming|coming) (?:songs|tracks)\??$|^(?:what'?s|what is) (?:coming|playing) (?:up )?next\??$|^up next\??$|^(?:what'?s|what is) (?:up )?next (?:in|on) (?:my |the )?queue\??$|^(?:what'?s|what is|which is|whats) (?:the )?next (?:song|track|one)\??$|^(?:which|what) (?:song|track) (?:is|comes|plays) next\??$|^what (?:plays|comes|is playing) (?:after this|next)(?: one)?\??$", I)) return Op("queue");
        if (Regex.IsMatch(s, @"^(?:what (?:did|have) i (?:listen(?:ed)? to|play(?:ed)?)(?: recently| lately| today)?|recently played|my recent(?:ly played)? songs)\??$", I)) return Op("recent");
        if ((m = Regex.Match(s, @"^(?:(?:what(?:'s| is| are)|who(?:'s| is| are)|tell me|show(?: me)?)\s+)?(?:my\s+)?(?:top|most played)\s+(songs?|tracks?|artists?|singers?|bands?)\??$|^who (?:do i listen to|are my top artists) (?:the )?most\??$", I)).Success)
            return Op("top", m.Groups[1].Success && Regex.IsMatch(m.Groups[1].Value, @"^(?:songs?|tracks?)$", I) ? "tracks" : "artists");
        if ((m = Regex.Match(s, @"^play (?:the )?album (.+)$|^play (.+?) (?:the )?album$", I)).Success) return Op("album", (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().ToLowerInvariant());
        if ((m = Regex.Match(s, @"^play (?:songs|music|something) by (.+)$|^play (?:the )?artist (.+)$", I)).Success) return Op("artist", (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().ToLowerInvariant());
        if ((m = Regex.Match(s, @"^play (?:the |a )?(?:podcast|episode|show) (.+)$|^play (.+?) podcast$", I)).Success) return Op("podcast", (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().ToLowerInvariant());
        if (Regex.IsMatch(s, @"^follow (?:this|the) (?:artist|singer|band)$", I)) return Op("follow");
        if (Regex.IsMatch(s, @"^(?:my|show(?: me)?(?: my)?|list(?: my)?) (?:spotify )?devices\??$|^(?:what|which) devices (?:do i have|are there)\??$", I)) return Op("devices");
        if ((m = Regex.Match(s, @"^(?:play|move (?:it|the music|playback)|switch|continue) (?:on|to) (?:my |the )?(.+)$", I)).Success && m.Groups[1].Value.Split(' ').Length <= 3) return Op("device", m.Groups[1].Value.Trim().ToLowerInvariant());
        return null;
    }

    // what she's about to do, in a line ("⏰ Reminder · call mom · today 5:00 PM")
    public static string Describe(IvoAction a)
    {
        string Get(string k) => a.Args.GetValueOrDefault(k, "");
        string At()
        {
            if (!DateTime.TryParseExact(Get("when"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "";
            var day = d.Date == DateTime.Today ? "today" : d.Date == DateTime.Today.AddDays(1) ? "tomorrow" : d.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            var every = Get("repeat") is "once" or "" ? "" : $" · {Get("repeat")}";
            return $" · {day} {d.ToString("h:mm tt", CultureInfo.CurrentCulture)}{every}";
        }
        return a.Kind switch
        {
            "reminder" => $"⏰ Reminder · {Get("text")}{At()}",
            "alert" => $"🔔 Alert · {Get("text")}{At()}",
            "task" => $"🗓️ Daily task · {Get("text")}{At()}",
            "todo" => $"✅ To-do · {Get("text")}",
            "habit" => $"🔥 Habit · {Get("text")}",
            "habitdone" => $"✅ Tick · {Get("text")}",
            "ask" => $"💻 Ask your AI: {Get("text")}",
            "youtube" => Get("op") switch { "follow" => $"📺 Follow {Get("value")} on YouTube", "unfollow" => $"📺 Unfollow {Get("value")}", "list" => "📺 Your YouTube channels", _ => Get("value") == "" ? "📺 New on your YouTube" : $"📺 New from {Get("value")}" },
            "reddit" => Get("sub") == "popular" ? "🟠 Hot on Reddit" : $"🟠 r/{Get("sub")} · {(Get("sort") == "top" ? "top " + Get("range") switch { "day" => "today", "all" => "of all time", var r => "this " + r } : Get("sort"))}",
            "url" => "↗️ Opening…",
            "play" => Get("query") == "" ? "▶️ Play music" : $"🎵 Play · {Get("query")}",
            "pause" => "⏸️ Pause music",
            "queue" => $"📋 Queue · {Get("query")}",
            "music" => Get("op") switch
            {
                "shuffle" => $"🔀 Shuffle {Get("value")}", "repeat" => Get("value") switch { "track" => "🔂 Repeat this song", "context" => "🔁 Repeat the playlist", _ => "➡️ Repeat off" },
                "volume" => Get("value") switch { "up" => "🔊 Louder", "down" => "🔉 Quieter", "0" => "🔇 Mute", var v => $"🔊 Volume {v}%" },
                "seek" => Get("value") switch { "0" => "⏪ From the start", var v when v.StartsWith('-') => $"⏪ Back {v[1..]}s", var v when v.StartsWith('+') => $"⏩ Forward {v[1..]}s", var v => $"⏩ To {int.Parse(v) / 60}:{int.Parse(v) % 60:00}" },
                "like" => "💚 Save to Liked Songs", "unlike" => "💔 Remove from Liked Songs", "addto" => $"➕ Add to {Get("value")}",
                "liked" => "💚 Play your Liked Songs", "myplaylist" => $"🎶 Play your {Get("value")} playlist", "playlists" => "🎶 Your playlists",
                "queue" => "📋 Up next", "recent" => "🕘 Recently played", "top" => Get("value") == "artists" ? "🏆 Your top artists" : "🏆 Your top songs",
                "album" => $"💿 Play the album {Get("value")}", "artist" => $"🎤 Play {Get("value")}", "podcast" => $"🎙️ Play {Get("value")}",
                "follow" => "➕ Follow this artist", "devices" => "📱 Your devices", "device" => $"📱 Play on {Get("value")}", _ => "🎧 Spotify",
            },
            "next" => "⏭️ Next song",
            "previous" => "⏮️ Previous song",
            "playing" => a.Args.ContainsKey("device") ? "🎧 Let me check…" : "🎧 Here's what's on",
            "lyrics" => "🎤 Getting the lyrics…",
            "song" => "🎶 About this song…",
            "artist" => "🎙️ About this artist…",
            "call" => $"{(Get("video") == "1" ? "📹 Video call" : "📞 Call")} · {Get("who")}",
            "message" => $"💬 Message {Get("who")} · {Get("text")}",
            "set" => $"⚙️ {Get("setting")} → {Get("value")}",
            "open" => $"↗️ Open {Get("target")}",
            _ => a.Kind,
        };
    }

    static IvoAction? Set(string setting, string value)
    {
        setting = setting.Trim().ToLowerInvariant(); value = value.Trim().ToLowerInvariant();
        if (setting == "" || setting.Split(' ').Length > 4) return null;
        value = value switch { "enable" or "enabled" or "yes" or "true" or "switch on" => "on", "disable" or "disabled" or "no" or "false" or "switch off" => "off", _ => value };
        return new("set", new() { ["setting"] = setting, ["value"] = value }, value == "" ? "value" : null);
    }

    static IvoAction? Slash(string s, DateTime now)
    {
        var m = Regex.Match(s, @"^/(\w+)\s*(.*)$");
        if (!m.Success) return null;
        var rest = m.Groups[2].Value.Trim();
        switch (m.Groups[1].Value.ToLowerInvariant())
        {
            case "remind" or "reminder": return Timed("reminder", rest, now);
            case "task": return Timed("task", rest, now, repeat: "daily");
            case "alert": return Timed("alert", rest, now);
            case "todo": return new("todo", new() { ["text"] = rest }, rest == "" ? "text" : null);
            case "habit" when Regex.Match(rest, @"^(?:done|tick|did)\s+(.+)$", I) is { Success: true } did: return new("habitdone", new() { ["text"] = did.Groups[1].Value.Trim().ToLowerInvariant() });
            case "habit": return new("habit", new() { ["text"] = rest }, rest == "" ? "text" : null);
            case "play": return new("play", new() { ["query"] = rest });
            case "queue": return new("queue", new() { ["query"] = rest }, rest == "" ? "query" : null);
            case "youtube" or "yt": return rest == "" ? new("youtube", new() { ["op"] = "new", ["value"] = "" }) : Social(rest) ?? Social("new videos from " + rest);
            case "reddit": { var p = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries); return p.Length == 0 ? Social("what's hot on reddit") : Social("r/" + p[0].TrimStart('r', '/') + (p.Length > 1 ? " " + p[1] : "")) ?? Social("r/" + p[0]); }
            case "spotify" or "music": return rest == "" ? new("playing", []) : Music(rest) ?? new("play", new() { ["query"] = rest });
            case "playing" or "nowplaying": return new("playing", []);
            case "lyrics": return new("lyrics", []);
            case "playlists": return new("music", new() { ["op"] = "playlists" });
            case "recent": return new("music", new() { ["op"] = "recent" });
            case "pause": return new("pause", []);
            case "next": return new("next", []);
            case "call":
            {
                var video = Regex.IsMatch(rest, @"\bvideo\b", I);
                var who = Regex.Replace(rest, @"\b(video|voice)\b", "", I).Trim().ToLowerInvariant();
                return new("call", new() { ["who"] = who, ["video"] = video ? "1" : "0" }, who == "" ? "who" : null);
            }
            case "message" or "msg" or "text":
            {
                var p = Regex.Match(rest, @"^(\S+)\s*(.*)$");
                return new("message", new() { ["who"] = p.Groups[1].Value.ToLowerInvariant(), ["text"] = p.Groups[2].Value.Trim() }, rest == "" ? "who" : p.Groups[2].Value.Trim() == "" ? "text" : null);
            }
            case "set":
            {
                var p = Regex.Match(rest, @"^(.+?)\s+(?:to\s+)?(\S+)$");
                return p.Success ? Set(p.Groups[1].Value, p.Groups[2].Value) : new("set", new() { ["setting"] = rest.ToLowerInvariant(), ["value"] = "" }, "value");
            }
            case "open": return new("open", new() { ["target"] = rest.ToLowerInvariant() });
            default: return null;
        }
    }

    // something at a time: its words, when, and how often
    static IvoAction Timed(string kind, string rest, DateTime now, string repeat = "once")
    {
        var args = new Dictionary<string, string>();
        var text = rest;
        foreach (var (pattern, r) in new[] { (@"\b(?:every ?day|daily|each day)\b", "daily"), (@"\b(?:on )?weekdays\b|\bevery weekday\b", "weekdays"), (@"\b(?:every ?week|weekly)\b", "weekly") })
            if (Regex.IsMatch(text, pattern, I)) { repeat = r; text = Regex.Replace(text, pattern, " ", I); }
        args["repeat"] = repeat;
        if (When(text, now, out var left) is { } at) args["when"] = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        text = Regex.Replace(left, @"\s+", " ").Trim(' ', ',', '.');
        text = Regex.Replace(text, @"^(?:to|that|about|for|of|i need to|i have to)\s+", "", I).Trim();
        args["text"] = text;
        return new(kind, args, !args.ContainsKey("when") ? "when" : text == "" ? "text" : null);
    }

    // a time in a sentence ("at 5", "tomorrow 9am", "18:30", "in 20 minutes", "tonight", "on friday"), and the
    // sentence without it. A bare hour picks the next one still ahead: "at 5" at 2 pm is 5 pm.
    public static DateTime? When(string text, DateTime now, out string rest)
    {
        rest = text;
        Match m;
        if ((m = Regex.Match(rest, @"\bin (\d+|an?|half an) (min(?:ute)?s?|hours?|hrs?|days?)\b", I)).Success)
        {
            var n = m.Groups[1].Value.ToLowerInvariant() switch { "a" or "an" => 1.0, "half an" => 0.5, var x => double.Parse(x, CultureInfo.InvariantCulture) };
            rest = rest.Remove(m.Index, m.Length);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            var at = unit.StartsWith("min") ? now.AddMinutes(n) : unit.StartsWith('h') ? now.AddMinutes(n * 60) : now.AddDays(n);
            return new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0);
        }

        DateTime? day = null; var defaultHour = 9;
        if ((m = Regex.Match(rest, @"\b(day after tomorrow|tomorrow|tmrw|tmr|today|tonight|this evening|this morning|this afternoon)\b", I)).Success)
        {
            var w = m.Value.ToLowerInvariant();
            day = w.StartsWith("day after") ? now.Date.AddDays(2) : w is "tomorrow" or "tmrw" or "tmr" ? now.Date.AddDays(1) : now.Date;
            defaultHour = w switch { "tonight" => 20, "this evening" => 18, "this afternoon" => 15, "this morning" => 9, _ => 9 };
            rest = rest.Remove(m.Index, m.Length);
        }
        else if ((m = Regex.Match(rest, @"\b(?:on |this |next )?(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", I)).Success)
        {
            var target = Enum.Parse<DayOfWeek>(m.Groups[1].Value, true);
            var ahead = ((int)target - (int)now.DayOfWeek + 7) % 7;
            day = now.Date.AddDays(ahead == 0 ? 7 : ahead);
            rest = rest.Remove(m.Index, m.Length);
        }

        int? hour = null, minute = null; var exact = false;
        if ((m = Regex.Match(rest, @"(?:\b(?:at|@|by|around)\s*)?\b(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)\b", I)).Success)
        {
            hour = int.Parse(m.Groups[1].Value) % 12 + (m.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0; exact = true;
        }
        else if ((m = Regex.Match(rest, @"(?:\b(?:at|@|by|around)\s*)?\b([01]?\d|2[0-3]):([0-5]\d)\b", I)).Success)
        {
            hour = int.Parse(m.Groups[1].Value); minute = int.Parse(m.Groups[2].Value); exact = hour > 12 || m.Groups[1].Value.StartsWith('0');
        }
        else if ((m = Regex.Match(rest, @"\b(?:at|@|by|around)\s+(\d{1,2})\b(?!\s*(?:min|hour|hr|day|%))", I)).Success && int.Parse(m.Groups[1].Value) is >= 1 and <= 12)
        {
            hour = int.Parse(m.Groups[1].Value); minute = 0;
        }
        else if ((m = Regex.Match(rest, @"\b(?:at )?(noon|midday|midnight)\b", I)).Success)
        {
            hour = m.Groups[1].Value.ToLowerInvariant() == "midnight" ? 0 : 12; minute = 0; exact = true;
        }
        if (hour != null) rest = rest.Remove(m.Index, m.Length);
        if (hour == null && day == null) return null;

        var date = day ?? now.Date;
        if (hour == null) return date.AddHours(defaultHour);
        static DateTime Ahead(DateTime t, DateTime now) => t <= now ? t.AddDays(1) : t;   // gone already today: the same time tomorrow
        if (exact) return Ahead(date.AddHours(hour.Value).AddMinutes(minute!.Value), now);
        // a bare hour: the next one ahead (5 → 5:00 or 17:00); on another day, daytime hours
        var h = hour.Value % 12;
        if (day == null)
        {
            foreach (var c in new[] { h, h + 12, h + 24 }) { var t = date.AddHours(c).AddMinutes(minute!.Value); if (t > now) return t; }
        }
        var evening = defaultHour >= 15;   // "tonight at 11", "this evening at 8": the evening's
        return Ahead(date.AddHours(evening || h < 7 ? h + 12 : h).AddMinutes(minute!.Value), now);
    }
}
