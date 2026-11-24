namespace Ivo;

// Line diff between two answers of the same daily task (planner → History): which lines are new, which went away.
// Blank lines and leading/trailing spaces are ignored, so re-wrapping doesn't count as a change.
public static class LineDiff
{
    public enum Kind { Same, Added, Removed }

    // ponytail: O(n·m) LCS table; fine for task answers (tens of lines), Myers diff if it ever compares long files
    public static List<(Kind Kind, string Line)> Compare(string older, string newer)
    {
        static string[] Lines(string s) => [.. s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l != "")];
        var a = Lines(older); var b = Lines(newer);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<(Kind, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add((Kind.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add((Kind.Removed, a[x++]));
            else result.Add((Kind.Added, b[y++]));
        }
        while (x < a.Length) result.Add((Kind.Removed, a[x++]));
        while (y < b.Length) result.Add((Kind.Added, b[y++]));
        return result;
    }
}
