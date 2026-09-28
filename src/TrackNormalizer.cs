using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarLyrics;

/// <summary>
/// Чистит метаданные (особенно из браузера: "Artist - Song (Official Video) [4K]", канал "Artist - Topic",
/// "Song - Remastered 2011") и придумывает варианты запросов для поиска текста.
/// </summary>
public static class TrackNormalizer
{
    /// <param name="Title">название без мусора</param>
    /// <param name="BareTitle">без "feat. …"</param>
    /// <param name="BaseTitle">без пометок версии: (Remix), (Live), (Acoustic)…</param>
    /// <param name="MainArtist">первый из исполнителей</param>
    public sealed record Normalized(string Artist, string Title, string BareTitle, string BaseTitle, string MainArtist);

    /// <param name="Free">свободный поиск (q=) вместо поиска по полям</param>
    /// <param name="MatchTitle">для свободного поиска: название найденного трека должно совпадать</param>
    /// <param name="StrictDuration">длительность должна совпадать почти точно</param>
    public sealed record Variant(string Artist, string Title, bool Free, string? MatchTitle, bool StrictDuration);

    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Regex Junk = new(
        @"\s*[\(\[【][^\)\]】]*?(official|video|audio|lyric|visuali[sz]er|\bmv\b|m/v|\bhd\b|\bhq\b|4k|клип|премьера|текст|караоке)[^\)\]】]*[\)\]】]",
        Opt);

    static readonly Regex TrailingVersion = new(
        @"\s+[-–—]\s+(\d{4}\s+)?(remaster(ed)?|live|mono|stereo|radio edit|single version|album version|edit|version|mix)\b.*$",
        Opt);

    static readonly Regex VersionTag = new(
        @"\s*[\(\[][^\)\]]*\b(remix|mix|edit|version|live|acoustic|remaster(ed)?|instrumental|sped up|slowed|reverb|demo|mono|stereo|ремикс|версия|акустика)\b[^\)\]]*[\)\]]",
        Opt);

    static readonly Regex Feat = new(@"\s*[\(\[]?\s*\b(feat\.?|ft\.?|featuring)\s+[^\)\]]*[\)\]]?", Opt);
    static readonly Regex ArtistSplit = new(@"\s*(,|&|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bи\b)\s*", Opt);
    static readonly string[] Dashes = [" - ", " – ", " — "];

    public static Normalized Clean(string artist, string title)
    {
        artist = Regex.Replace(artist.Trim(), @"\s*-\s*Topic$", "", Opt);
        artist = Regex.Replace(artist, @"VEVO$", "", Opt).Trim();

        title = Junk.Replace(title.Trim(), "").Trim();
        title = TrailingVersion.Replace(title, "").Trim();

        // YouTube и прочие сайты: в заголовке "Исполнитель - Песня", а в artist — название канала
        foreach (var d in Dashes)
        {
            int i = title.IndexOf(d, StringComparison.Ordinal);
            if (i <= 0) continue;
            var left = title[..i].Trim();
            var right = title[(i + d.Length)..].Trim();
            if (right.Length == 0) break;
            if (artist.Length == 0 ||
                left.Contains(artist, StringComparison.OrdinalIgnoreCase) ||
                artist.Contains(left, StringComparison.OrdinalIgnoreCase))
            {
                artist = left;
                title = right;
            }
            break;
        }

        var bare = Feat.Replace(title, "").Trim();
        if (bare.Length == 0) bare = title;
        var baseTitle = VersionTag.Replace(bare, "").Trim();
        if (baseTitle.Length == 0) baseTitle = bare;

        var main = ArtistSplit.Split(artist).FirstOrDefault(s => s.Trim().Length > 0)?.Trim() ?? artist;

        return new Normalized(artist, title, bare, baseTitle, main);
    }

    /// <summary>Варианты запросов от самого точного к самому свободному.</summary>
    public static IEnumerable<Variant> Variants(Normalized n)
    {
        if (n.Artist.Length > 0)
        {
            yield return new(n.Artist, n.Title, false, null, false);
            yield return new(n.MainArtist, n.BareTitle, false, null, false);
            yield return new(n.MainArtist, n.BaseTitle, false, null, false);
            // исполнитель и название перепутаны местами (частая беда YouTube)
            yield return new(n.BaseTitle, n.MainArtist, false, null, false);

            // транслитерация: "Kino" ↔ "Кино"
            var ta = Translit.Other(n.MainArtist);
            var tt = Translit.Other(n.BaseTitle);
            if (ta != n.MainArtist || tt != n.BaseTitle)
            {
                yield return new(ta, tt, false, null, false);
                yield return new(ta, n.BaseTitle, false, null, false);
                yield return new(n.MainArtist, tt, false, null, false);
            }

            yield return new("", $"{n.MainArtist} {n.BaseTitle}", true, n.BaseTitle, false);
        }

        // Последний шанс: только название, но длительность должна совпасть почти точно
        yield return new("", n.BaseTitle, true, n.BaseTitle, true);
    }

    /// <summary>Только буквы и цифры в нижнем регистре — для нестрогого сравнения названий.</summary>
    public static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c == 'ё' || c == 'Ё' ? 'е' : c));
        return sb.ToString();
    }
}

/// <summary>Простая транслитерация кириллица ↔ латиница для поиска.</summary>
public static class Translit
{
    static readonly Dictionary<char, string> CyrToLat = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya", ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g",
    };

    static readonly (string Lat, string Cyr)[] LatDigraphs =
    [
        ("shch", "щ"), ("sch", "щ"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"),
        ("yo", "ё"), ("yu", "ю"), ("ya", "я"), ("ye", "е"), ("ph", "ф"),
    ];

    static readonly Dictionary<char, string> LatToCyr = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф", ['g'] = "г", ['h'] = "х",
        ['i'] = "и", ['j'] = "дж", ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п",
        ['q'] = "к", ['r'] = "р", ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс",
        ['y'] = "ы", ['z'] = "з",
    };

    static bool HasCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    /// <summary>Кириллицу переводит в латиницу, латиницу — в кириллицу.</summary>
    public static string Other(string s) => HasCyrillic(s) ? ToLatin(s) : ToCyrillic(s);

    public static string ToLatin(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(CyrToLat.TryGetValue(ch, out var r) ? r : ch.ToString());
        return sb.ToString();
    }

    public static string ToCyrillic(string s)
    {
        var src = s.ToLowerInvariant();
        var sb = new StringBuilder();
        for (int i = 0; i < src.Length;)
        {
            var hit = LatDigraphs.FirstOrDefault(d => string.CompareOrdinal(src, i, d.Lat, 0, d.Lat.Length) == 0);
            if (hit.Lat != null) { sb.Append(hit.Cyr); i += hit.Lat.Length; continue; }
            sb.Append(LatToCyr.TryGetValue(src[i], out var r) ? r : src[i].ToString());
            i++;
        }
        return sb.ToString();
    }
}
