using System.Net.Http;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Ivo;

// Today's articles for a daily task: search news feeds, group duplicate stories, and enrich them from the publishers.
public static partial class News
{
    public record Article(string Title, string Link, string Source, DateTime? Published, string Summary = "", int Coverage = 1, string ImageUrl = "", string Author = "", string Section = "", List<string>? Body = null);
    public sealed record Category(string Key, string Name, string Topic);
    public static readonly Category[] Categories =
    [
        new("city-general", "City general", "local news"),
        new("country-general", "Country general", "latest news"),
        new("country-technology", "Country technology", "technology"),
        new("country-foreign-affairs", "Country foreign affairs", "foreign affairs"),
        new("country-business", "Country business", "business"),
        new("international-general", "International general", "international news"),
        new("international-technology", "International technology", "international technology"),
        new("international-affairs", "International affairs", "international affairs"),
        new("international-business", "International business", "international business"),
        new("international-sports", "International sports", "international sports"),
        new("culture-bollywood", "Culture · Bollywood", "Bollywood films entertainment"),
        new("culture-hollywood", "Culture · Hollywood", "Hollywood films entertainment"),
        new("culture-music", "Culture · International music", "international music artists")
    ];
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static List<Category> OrderedCategories(IEnumerable<string>? order, IEnumerable<string>? hidden = null)
    {
        var known = Categories.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var ordered = (order ?? []).Where(known.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => known[key]).Concat(Categories.Where(c => !(order ?? []).Contains(c.Key, StringComparer.OrdinalIgnoreCase)));
        var excluded = new HashSet<string>(hidden ?? [], StringComparer.OrdinalIgnoreCase);
        return [.. ordered.Where(c => !excluded.Contains(c.Key))];
    }

    public static int LimitForCategory(Category category, int max) =>
        category.Key.StartsWith("culture-", StringComparison.Ordinal) ? Math.Min(max, 5) : max;

    public static List<Article> NewestFirst(IEnumerable<Article> articles, int max) =>
        [.. articles.OrderByDescending(a => a.Published ?? DateTime.MinValue).Take(max)];

    public static string TopicFor(Category category, string city, string country)
    {
        var topic = category.Topic;
        if (category.Key == "city-general") return $"{city} {topic}".Trim();
        if (category.Key.StartsWith("country-", StringComparison.Ordinal)) return $"{country} {topic}".Trim();
        return topic;
    }

    static readonly string[] InternationalSources = ["reuters.com", "apnews.com", "bbc.com", "theguardian.com", "aljazeera.com"];
    static readonly string[] IndiaSources = ["indianexpress.com", "thehindu.com", "hindustantimes.com", "timesofindia.indiatimes.com", "reuters.com"];   // not NDTV: it refuses apps ("Access Denied")
    static readonly string[] UnitedStatesSources = ["apnews.com", "reuters.com", "npr.org", "pbs.org", "bloomberg.com"];
    static readonly string[] UnitedKingdomSources = ["bbc.com", "reuters.com", "theguardian.com", "ft.com", "sky.com"];
    static readonly string[] CanadaSources = ["cbc.ca", "ctvnews.ca", "theglobeandmail.com", "reuters.com", "apnews.com"];
    static readonly string[] AustraliaSources = ["abc.net.au", "sbs.com.au", "theguardian.com", "reuters.com", "apnews.com"];

    public static IReadOnlyList<string> SourcesFor(string country, Category category)
    {
        if (category.Key.StartsWith("international-", StringComparison.Ordinal) || category.Key.StartsWith("culture-", StringComparison.Ordinal))
            return InternationalSources;
        if (country.Contains("India", StringComparison.OrdinalIgnoreCase)) return IndiaSources;
        if (country.Contains("United States", StringComparison.OrdinalIgnoreCase) || country.Equals("US", StringComparison.OrdinalIgnoreCase)) return UnitedStatesSources;
        if (country.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase) || country.Equals("UK", StringComparison.OrdinalIgnoreCase)) return UnitedKingdomSources;
        if (country.Contains("Canada", StringComparison.OrdinalIgnoreCase)) return CanadaSources;
        if (country.Contains("Australia", StringComparison.OrdinalIgnoreCase)) return AustraliaSources;
        return InternationalSources;
    }

    // Only stories from the reputed outlets listed in SourcesFor are kept.
    public static async Task<List<Article>> FetchCategory(Category category, string city, string country, int max = 20)
    {
        var candidates = LimitForCategory(category, max) + 8;
        var sources = SourcesFor(country, category);
        var articles = await Fetch(TopicFor(category, city, country), sources, candidates);
        return NewestFirst(articles.Where(a => IsFrom(a.Link, sources)), candidates);
    }

    static bool IsFrom(string link, IEnumerable<string> domains) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
        domains.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    static readonly Dictionary<string, (DateTime At, List<Article> Items)> cache = [];

    public static List<Article>? Cached(string key, TimeSpan maxAge)
    {
        lock (cache) return cache.TryGetValue(key, out var entry) && DateTime.Now - entry.At < maxAge ? entry.Items : null;
    }

    public static void Remember(string key, List<Article> items) { lock (cache) cache[key] = (DateTime.Now, items); }

    // The newspapers' own feeds, newest first and updated as they publish: fresher and much quicker than searching.
    // A city's feeds follow each paper's address pattern; a city a paper doesn't cover just gives nothing.
    static IEnumerable<string> DirectFeeds(Category category, string city, string country)
    {
        var slug = Regex.Replace(city.Trim().ToLowerInvariant(), @"\s+", "-");
        var india = country.Contains("India", StringComparison.OrdinalIgnoreCase);
        return category.Key switch
        {
            // the city's papers that publish round the clock (Times of India, Free Press Journal, Mid-day) and the national papers' city sections
            "city-general" when india && slug != "" => [.. TimesCity.TryGetValue(slug, out var toi) ? [$"https://timesofindia.indiatimes.com/rssfeeds/{toi}.cms"] : Array.Empty<string>(),
                                                        $"https://www.freepressjournal.in/stories.rss?section={slug}", .. slug == "mumbai" ? ["https://www.mid-day.com/Resources/midday/rss/mumbai-news.xml"] : Array.Empty<string>(),
                                                        $"https://indianexpress.com/section/cities/{slug}/feed/", $"https://www.hindustantimes.com/feeds/rss/cities/{slug}-news/rssfeed.xml",
                                                        $"https://www.thehindu.com/news/cities/{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug)}/feeder/default.rss"],
            "country-general" when india => ["https://timesofindia.indiatimes.com/rssfeedstopstories.cms", "https://indianexpress.com/feed/",
                                             "https://indianexpress.com/section/india/feed/", "https://www.thehindu.com/news/national/feeder/default.rss",
                                             "https://www.hindustantimes.com/feeds/rss/india-news/rssfeed.xml"],
            "country-technology" when india => ["https://indianexpress.com/section/technology/feed/", "https://www.thehindu.com/sci-tech/technology/feeder/default.rss"],
            "country-business" when india => ["https://www.hindustantimes.com/feeds/rss/business/rssfeed.xml", "https://www.thehindu.com/business/feeder/default.rss"],
            "international-general" or "international-affairs" => ["https://feeds.bbci.co.uk/news/world/rss.xml", "https://www.theguardian.com/world/rss", "https://www.aljazeera.com/xml/rss/all.xml"],
            "international-technology" => ["https://feeds.bbci.co.uk/news/technology/rss.xml"],
            "international-business" => ["https://feeds.bbci.co.uk/news/business/rss.xml"],
            "international-sports" => ["https://feeds.bbci.co.uk/sport/rss.xml"],
            _ => []
        };
    }

    // the Times of India's feed number for each big city
    static readonly Dictionary<string, string> TimesCity = new()
    {
        ["mumbai"] = "-2128838597", ["delhi"] = "-2128839596", ["new-delhi"] = "-2128839596", ["bengaluru"] = "-2128833038", ["bangalore"] = "-2128833038",
        ["kolkata"] = "-2128830821", ["chennai"] = "2950623", ["hyderabad"] = "-2128816011", ["pune"] = "-2128821991",
    };

    // a paper's own name, not its feed's title ("Mumbai News | The Indian Express")
    static readonly Dictionary<string, string> Papers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["indianexpress.com"] = "The Indian Express", ["timesofindia.indiatimes.com"] = "Times of India", ["freepressjournal.in"] = "Free Press Journal", ["mid-day.com"] = "Mid-day", ["thehindu.com"] = "The Hindu", ["hindustantimes.com"] = "Hindustan Times",
        ["bbc.co.uk"] = "BBC", ["bbc.com"] = "BBC", ["theguardian.com"] = "The Guardian", ["aljazeera.com"] = "Al Jazeera", ["reuters.com"] = "Reuters", ["apnews.com"] = "AP",
    };
    static Article Named(Article a) =>
        Uri.TryCreate(a.Link, UriKind.Absolute, out var u) && Papers.FirstOrDefault(p => u.Host.Equals(p.Key, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + p.Key, StringComparison.OrdinalIgnoreCase)) is { Value: { } name }
            ? a with { Source = name } : a;

    // recent (a date in the last day, none from the future) and, for the city, about the city: named, or from its own section
    public static bool Fits(Article a, Category category, string city, DateTime now)
    {
        if (a.Published is not { } at || at < now.AddHours(-24) || at > now.AddMinutes(15)) return false;
        if (category.Key != "city-general" || city.Trim() == "") return true;
        var slug = Regex.Replace(city.Trim().ToLowerInvariant(), @"\s+", "-");
        return (a.Title + " " + a.Summary).Contains(city.Trim(), StringComparison.OrdinalIgnoreCase) || a.Link.Contains("/" + slug, StringComparison.OrdinalIgnoreCase);
    }

    // The stories for a category, quickly: the papers' feeds first, a search only when they don't give enough. Each comes
    // with what its feed says (title, summary, photo, time); Details fills in the rest later, the full text only when opened.
    public static async Task<List<Article>> Latest(Category category, string city, string country, int max)
    {
        var limit = LimitForCategory(category, max);
        var now = DateTime.Now;
        var feeds = await Task.WhenAll(DirectFeeds(category, city, country).Select(Read));
        var found = feeds.SelectMany(x => x).Select(Named).Where(a => Fits(a, category, city, now)).ToList();
        if (found.Count < limit) found.AddRange((await FetchCategory(category, city, country, max)).Select(Named).Where(a => Fits(a, category, city, now)));
        // the same story from two papers (or twice in one feed): kept once, the fullest telling
        var kept = new List<Article>();
        foreach (var a in found.OrderByDescending(a => a.Published).ThenByDescending(a => a.ImageUrl != "").ThenByDescending(a => a.Summary.Length))
            if (!kept.Any(k => k.Link == a.Link || Similar(Words(k.Title), Words(a.Title)))) kept.Add(a with { Summary = Brief(a.Summary) });
        return NewestFirst(kept, limit);
    }

    // a card's missing photo or summary, from the publisher's page (only when the feed didn't have them)
    public static async Task<Article> Details(Article article) =>
        article.ImageUrl != "" && article.Summary.Length >= 60 ? article
        : await Download(article.Link, 6) is { } html ? Detail(article, html) : article;

    static string Bing(string q, string market) => $"https://www.bing.com/news/search?q={Uri.EscapeDataString(q)}&format=rss&mkt={market}&qft=interval%3d%227%22+sortbydate%3d%221%22";
    static string Google(string q, string country) => $"https://news.google.com/rss/search?q={Uri.EscapeDataString(q + " when:1d")}&hl=en-{country}&gl={country}&ceid={country}:en";

    public static bool IsRequest(string request) => Regex.IsMatch(request, @"\b(news|headlines?|current affairs)\b", RegexOptions.IgnoreCase);

    public static List<string> SourcesFrom(string request)
    {
        var sources = new List<string>();
        foreach (Match m in Regex.Matches(request, @"https?://[^\s,;]+|(?:www\.)?[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.(?:com|org|net|edu|gov|co\.[a-z]{2}|in|io|news)(?:/[^\s,;]*)?", RegexOptions.IgnoreCase))
        {
            var source = m.Value.TrimEnd('.', ')', ']', '}', '!', '?');
            if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase)) sources.Add(source);
        }
        return sources;
    }

    public static async Task<List<Article>> Fetch(string topic, IEnumerable<string> sources, int max = 20)
    {
        var feeds = new List<string>();
        var src = sources.Select(s => s.Trim()).Where(s => s != "").ToList();
        foreach (var s in src)
            feeds.Add(s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? s
                : Bing($"{topic} site:{s.Replace("https://", "").Replace("http://", "").TrimEnd('/')}".Trim(), "en-US"));
        if (topic.Trim() != "" && src.All(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            feeds.AddRange([Bing(topic, "en-US"), Bing(topic, "en-IN"), Google(topic, "US"), Google(topic, "IN")]);
        var lists = await Task.WhenAll(feeds.Select(Read));
        var since = DateTime.Now.AddHours(-36);
        var all = lists.SelectMany(x => x).Where(a => a.Published > since).ToList();   // undated: can't tell it's recent

        var clusters = new List<List<Article>>();
        foreach (var article in all)
        {
            var words = Words(article.Title);
            var home = clusters.FirstOrDefault(c => c.Any(other => Similar(words, Words(other.Title))));
            if (home != null) home.Add(article); else clusters.Add([article]);
        }
        var top = clusters.OrderByDescending(c => c.Max(a => a.Published ?? DateTime.MinValue))
                          .ThenByDescending(c => c.Select(a => a.Source.ToLowerInvariant()).Distinct().Count())
                          .Take(max + 5)
                          .Select(c => c.OrderBy(a => a.Link.Contains("news.google.com")).ThenByDescending(a => a.Summary.Length).First()
                                        with { Coverage = c.Select(a => a.Source.ToLowerInvariant()).Distinct().Count() })
                          .ToList();
        var resolved = await Task.WhenAll(top.Select(async article =>
        {
            if (!article.Link.Contains("news.google.com")) return article;
            foreach (var q in new[] { $"\"{article.Title}\"", article.Title })
                if ((await Read(Bing(q, "en-US"))).FirstOrDefault(candidate => Similar(Words(article.Title), Words(candidate.Title))) is { } found)
                    return article with { Link = found.Link, Summary = found.Summary != "" ? found.Summary : article.Summary };
            return article;
        }));
        return NewestFirst(resolved, max);
    }

    static readonly HashSet<string> Stop = ["the", "a", "an", "of", "to", "in", "on", "for", "and", "or", "is", "are", "with", "as", "at", "by", "from", "after", "its", "it", "be", "has", "have", "new", "says", "say"];
    static HashSet<string> Words(string title) =>
        [.. Regex.Matches(title.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).Where(w => w.Length > 1 && !Stop.Contains(w))];
    static bool Similar(HashSet<string> x, HashSet<string> y) =>
        x.Count > 0 && y.Count > 0 && x.Intersect(y).Count() / (double)Math.Min(x.Count, y.Count) >= 0.6;

    static async Task<string?> Download(string link, int seconds)
    {
        try
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.Host.EndsWith("news.google.com", StringComparison.OrdinalIgnoreCase)) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var res = await http.SendAsync(req, cts.Token);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(cts.Token) : null;
        }
        catch { return null; }
    }

    public static async Task<List<Article>> Enrich(List<Article> items) => [.. await Task.WhenAll(items.Select(async article =>
    {
        var html = await Download(article.Link, 8);
        return html == null ? article with { Summary = Brief(article.Summary) } : Detail(article, html);
    }))];

    public static async Task<List<string>> FullText(string link) =>
        await Download(link, 15) is { } html ? BodyFrom(html) : [];

    static List<string> BodyFrom(string html)
    {
        var fromJson = JsonLdBody(html);
        var fromHtml = HtmlParagraphs(html);
        var best = fromJson.Sum(p => p.Length) > fromHtml.Sum(p => p.Length) ? fromJson : fromHtml;
        return best.Sum(p => p.Length) >= 300 ? best : [];
    }

    // Reads the publisher's page details (description, image, author, section) into the article.
    static Article Detail(Article article, string html)
    {
        try
        {
            string Text(string value) => Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")), @"\s+", " ").Trim();
            string Meta(string name)
            {
                var tag = Regex.Match(html, $@"<meta\b(?=[^>]*(?:property|name)=[""']{Regex.Escape(name)}[""'])[^>]*>", RegexOptions.IgnoreCase);
                return tag.Success ? Regex.Match(tag.Value, @"content=[""']([^""']+)[""']", RegexOptions.IgnoreCase).Groups[1].Value : "";
            }
            var description = Meta("og:description");
            if (description == "") description = Meta("description");
            if (description == "") description = Meta("twitter:description");
            var imageUrl = Meta("og:image");
            if (imageUrl == "") imageUrl = Meta("twitter:image");
            var author = Meta("author");
            if (author == "") author = Meta("article:author");
            var section = Meta("article:section");
            var parts = new List<string>();
            if (description != "") parts.Add(Text(description));
            if (parts.Count == 0)
            {
                foreach (Match m in Regex.Matches(html, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var text = Text(m.Groups[1].Value);
                    if (text.Length < 80 || Regex.IsMatch(text, @"cookie|subscribe|sign up|newsletter|all rights reserved|javascript", RegexOptions.IgnoreCase)) continue;
                    parts.Add(text);
                    break;
                }
            }
            var summary = new[] { article.Summary }.Concat(parts)
                .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
                .Where(x => x.Length > 0)
                .OrderByDescending(x => x.Length)
                .FirstOrDefault() ?? "";
            imageUrl = imageUrl != "" ? System.Net.WebUtility.HtmlDecode(imageUrl) : article.ImageUrl;
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme is not ("http" or "https")) imageUrl = "";
            return article with { Summary = Brief(summary), ImageUrl = imageUrl, Author = Text(author), Section = Text(section) };
        }
        catch { return article with { Summary = Brief(article.Summary) }; }
    }

    static readonly Regex Boilerplate = new(@"cookie|subscribe|sign up|newsletter|all rights reserved|javascript|follow us|advertisement|also read|read more|click here|whatsapp|download the app|terms of use|privacy policy",
        RegexOptions.IgnoreCase);

    static string ParagraphText(string html) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", "")), @"\s+", " ").Trim();

    static List<string> HtmlParagraphs(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript|svg|nav|header|footer|aside|form|figure|iframe|template)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        static List<string> Extract(string scope)
        {
            var found = new List<string>();
            foreach (Match m in Regex.Matches(scope, @"<p\b[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var text = ParagraphText(m.Groups[1].Value);
                if (text.Length < 40 || (text.Length < 200 && Boilerplate.IsMatch(text)) || found.Contains(text)) continue;
                found.Add(text);
            }
            return found;
        }

        var article = Regex.Matches(html, @"<article\b[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => Extract(m.Groups[1].Value)).OrderByDescending(p => p.Sum(x => x.Length)).FirstOrDefault() ?? [];
        return article.Sum(p => p.Length) >= 400 ? article : Extract(html);
    }

    static List<string> JsonLdBody(string html)
    {
        foreach (Match m in Regex.Matches(html, @"<script\b[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                if (FindArticleBody(doc.RootElement) is { Length: > 0 } body) return SplitBody(body);
            }
            catch (JsonException) { }
        }
        return [];
    }

    static string? FindArticleBody(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("articleBody", out var body) && body.ValueKind == JsonValueKind.String) return body.GetString();
            foreach (var property in element.EnumerateObject())
                if (FindArticleBody(property.Value) is { Length: > 0 } inner) return inner;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindArticleBody(item) is { Length: > 0 } inner) return inner;
        }
        return null;
    }

    // Splits a plain body into paragraphs; very long blocks are broken at sentence ends so the reader stays easy on the eye.
    static List<string> SplitBody(string body)
    {
        body = Regex.Replace(body, @"</p>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        body = System.Net.WebUtility.HtmlDecode(Regex.Replace(body, "<[^>]+>", ""));
        var result = new List<string>();
        foreach (var block in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = Regex.Replace(block, @"\s+", " ");
            if (text.Length <= 700) { result.Add(text); continue; }
            var current = "";
            foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z""'“‘])"))
            {
                if (current != "" && current.Length + sentence.Length > 500) { result.Add(current); current = ""; }
                current = current == "" ? sentence : current + " " + sentence;
            }
            if (current != "") result.Add(current);
        }
        return result;
    }

    static string Brief(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= 360) return text;
        var cut = text.LastIndexOf(' ', 340);
        return text[..(cut > 160 ? cut : 340)] + "…";
    }

    public static string QueryFrom(string request, IEnumerable<string>? sources = null)
    {
        foreach (var source in sources ?? SourcesFrom(request)) request = request.Replace(source, " ", StringComparison.OrdinalIgnoreCase);
        var query = Regex.Replace(request,
            @"\b(give|me|show|get|the|top|latest|today'?s?|news|headlines?|stories|story|updates?|about|on|for|of|in|and|please|\d+|from|via|source|sources)\b", " ",
            RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"[^\p{L}\p{N} ]|\s+", " ").Trim();
        query = Regex.Replace(query, @"\btech\b", "technology", RegexOptions.IgnoreCase);
        return query == "" ? "top stories" : query;
    }

    static async Task<List<Article>> Read(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 Ivo");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            using var res = await http.SendAsync(req, cts.Token);
            var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var channel = ((string?)xml.Descendants("channel").FirstOrDefault()?.Element("title") ?? "").Trim();
            var rss = xml.Descendants("item").Select(i =>
            {
                var title = (string?)i.Element("title") ?? "";
                var source = (string?)i.Element("source") ?? (string?)i.Elements().FirstOrDefault(e => e.Name.LocalName == "Source") ?? "";
                if (source == "" && !url.Contains("bing.com/news") && !url.Contains("news.google.com")) source = channel;
                if (source == "" && title.LastIndexOf(" - ") is > 0 and var cut) { source = title[(cut + 3)..]; title = title[..cut]; }
                else if (source != "" && title.EndsWith(" - " + source)) title = title[..^(source.Length + 3)];
                var link = ((string?)i.Element("link") ?? "").Trim();
                if (link.Contains("bing.com/news/apiclick") && Uri.TryCreate(link, UriKind.Absolute, out var u) && System.Web.HttpUtility.ParseQueryString(u.Query)["url"] is { } real) link = real;
                var image = i.Elements().Where(e => e.Name.LocalName is "content" or "thumbnail" or "enclosure")
                    .Select(e => (string?)e.Attribute("url")).FirstOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var imageUri) && imageUri.Scheme is "http" or "https") ?? "";
                // "&amp;" in a headline reads "&"
                return new Article(System.Net.WebUtility.HtmlDecode(title).Trim(), link, source.Trim(), Date((string?)i.Element("pubDate")), Clean((string?)i.Element("description"), title), ImageUrl: image);
            });
            var entries = xml.Descendants(atom + "entry").Select(e => new Article(((string?)e.Element(atom + "title") ?? "").Trim(),
                (string?)e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href") ?? "",
                (string?)xml.Root?.Element(atom + "title") ?? new Uri(url).Host, Date((string?)e.Element(atom + "updated") ?? (string?)e.Element(atom + "published")),
                Clean((string?)e.Element(atom + "summary") ?? (string?)e.Element(atom + "content"), ""),
                ImageUrl: e.Elements().Where(x => x.Name.LocalName is "content" or "thumbnail")
                    .Select(x => (string?)x.Attribute("url") ?? (string?)x.Attribute("href")).FirstOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var imageUri) && imageUri.Scheme is "http" or "https") ?? ""));
            return rss.Concat(entries).Where(a => a.Title.Length > 18 && a.Link.StartsWith("http")
                                                  && !Regex.IsMatch(a.Title, @"^(log ?in|sign ?in|subscribe|home|newsletter)\b", RegexOptions.IgnoreCase)).ToList();
        }
        catch { return []; }
    }

    static DateTime? Date(string? s) => DateTimeOffset.TryParse(s, out var d) ? d.LocalDateTime : null;

    static string Clean(string? html, string title)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
        if (text.Length < 40 || title != "" && text.StartsWith(title[..Math.Min(title.Length, 30)], StringComparison.OrdinalIgnoreCase) && text.Length < title.Length + 60) return "";
        return text.Length > 500 ? text[..500] + "…" : text;
    }

    public static string ForPrompt(List<Article> items) =>
        string.Join("\n", items.Select(a => $"- [{a.Title}]({a.Link}) — {a.Source}{(a.Published is { } p ? $", {p:d MMM HH:mm}" : "")}{(a.Coverage > 1 ? $", reported by {a.Coverage} outlets" : "")}" +
                                            (a.Summary != "" ? $"\n  What the article says: {a.Summary}" : "\n  What the article says: (couldn't be read)")));
}
