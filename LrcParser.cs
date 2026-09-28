using System.Text.RegularExpressions;

namespace TaskbarLyrics;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Разбор LRC: [mm:ss.xx]текст, несколько меток на строку, тег [offset:], word-level метки &lt;mm:ss.xx&gt;.</summary>
public static class LrcParser
{
    static readonly Regex Stamp = new(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
    static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase);
    static readonly Regex WordStamp = new(@"<\d+:\d+(?:[.:]\d+)?>");

    public static List<LyricLine> Parse(string lrc)
    {
        var result = new List<LyricLine>();
        int offsetMs = 0;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();

            var om = OffsetTag.Match(line);
            if (om.Success) { offsetMs = int.Parse(om.Groups[1].Value); continue; }

            var times = new List<TimeSpan>();
            Match m;
            while ((m = Stamp.Match(line)).Success)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3, '0')) : 0;
                times.Add(new TimeSpan(0, 0, min, sec, ms));
                line = line[m.Length..];
            }
            if (times.Count == 0) continue;

            var text = WordStamp.Replace(line, "").Trim();
            // Положительный offset по стандарту LRC сдвигает текст раньше
            foreach (var t in times)
                result.Add(new LyricLine(t - TimeSpan.FromMilliseconds(offsetMs), text));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>Индекс последней строки, чьё время ≤ pos; -1, если ещё идёт вступление.</summary>
    public static int IndexAt(List<LyricLine> lines, TimeSpan pos)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= pos) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}
