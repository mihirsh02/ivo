using System.Text.RegularExpressions;

namespace Ivo;

public enum Mood { Neutral, Happy, Sad, Angry, Flirty, Bored, Stressed, Excited, Rude, Confused }

// How a message feels, from its words, emoji, shouting (ALL CAPS) and "!!!". Ivo answers in a mood of her own that
// fits it (IvoMind.HerMood): sorry when you're angry at her, caring when you're down, cheeky when
// you flirt.
public static class IvoMood
{
    static bool Has(string low, string pattern) => Regex.IsMatch(low, pattern);

    public static Mood Read(string raw)
    {
        var low = raw.ToLowerInvariant().Replace("'", "").Replace("’", "");
        var letters = raw.Where(char.IsLetter).ToList();
        var shouting = letters.Count >= 6 && letters.Count(char.IsUpper) > letters.Count * .7;
        var bang = raw.Contains("!!");
        if (Has(low, @"\b(hate you|mad at you|angry (at|with) you|upset with you|you are (so )?(mean|rude|annoying|useless|the worst)|you (suck|never listen))\b|😠|😡|🤬") || shouting && Has(low, @"\b(you|hate|stop|why|no)\b"))
            return Mood.Angry;
        if (IvoMind.Rude(raw)) return Mood.Rude;
        if (Has(low, @"\b(sad|low|down|depressed|crying|cry|lonely|alone|heartbroken|miss (him|her|them)|hurt)\b|😢|😭|💔|😞|😔|🥀")) return Mood.Sad;
        if (Has(low, @"\b(stressed|stress|anxious|anxiety|overwhelmed|worried|nervous|panic|deadline)\b|😰|😩|😫")) return Mood.Stressed;
        if (Has(low, @"\b(kiss|cute|hot|date me|flirt|crush|marry me|babe|baby|love you|ily)\b|😘|😍|🥵|😏|😉|💋|🥰")) return Mood.Flirty;
        if (Has(low, @"^(what|huh|wdym|what do you mean|come again|eh)\??$|\b(confused|makes no sense|what are you saying|i dont get it)\b|🤔|😕")) return Mood.Confused;
        if (Has(low, @"\b(bored|boring|nothing to do)\b|🥱|😴")) return Mood.Bored;
        if (bang || Has(low, @"\b(yay|omg|lets go|so excited|cant wait|woo+|yess+)\b|🎉|🥳|🤩|🔥")) return Mood.Excited;
        if (Has(low, @"\b(happy|great|good|awesome|amazing|love it|nice)\b|😊|😄|😁|💜|✨")) return Mood.Happy;
        return Mood.Neutral;
    }
}
