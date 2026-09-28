using System.Text.RegularExpressions;

namespace TaskbarLyrics;

/// <summary>
/// Свои .lrc-файлы в папке (по умолчанию Документы\Lyrics, включая подпапки).
/// Имя файла: "Исполнитель - Название.lrc" (или "Название - Исполнитель.lrc", или просто "Название.lrc").
/// Регистр, пробелы и знаки препинания в имени не важны.
/// </summary>
public static class LocalLyrics
{
    public const string SourceName = "local";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lyrics");

    static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    static readonly Regex ArTag = new(@"^\s*\[ar:(.*)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static LyricsResult? Find(string folder, TrackNormalizer.Normalized n)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            static string S(string s) => TrackNormalizer.Squash(s);

            // "исполнитель + название" в любом порядке
            var full = new HashSet<string>();
            if (n.Artist.Length > 0)
                foreach (var (a, t) in new[] { (n.Artist, n.Title), (n.MainArtist, n.BareTitle), (n.MainArtist, n.BaseTitle) })
                {
                    full.Add(S(a + t));
                    full.Add(S(t + a));
                }
            var titleOnly = new HashSet<string> { S(n.Title), S(n.BareTitle), S(n.BaseTitle) };
            titleOnly.Remove("");

            string? titleCandidate = null;
            foreach (var f in Directory.EnumerateFiles(folder, "*.lrc", Opts))
            {
                var k = S(Path.GetFileNameWithoutExtension(f));
                if (k.Length == 0) continue;
                if (full.Contains(k)) return Read(f);
                if (titleCandidate == null && titleOnly.Contains(k)) titleCandidate = f;
            }

            // Файл назван только по песне — проверим исполнителя по тегу [ar:], если он есть
            if (titleCandidate != null)
            {
                var text = File.ReadAllText(titleCandidate);
                var m = ArTag.Match(text);
                if (n.Artist.Length == 0 || !m.Success)
                    return new LyricsResult(text, null, false, SourceName);
                var ar = S(m.Groups[1].Value);
                var main = S(n.MainArtist);
                if (ar.Contains(main) || main.Contains(ar))
                    return new LyricsResult(text, null, false, SourceName);
            }
        }
        catch (Exception ex) { App.Log(ex.ToString()); }
        return null;
    }

    static LyricsResult Read(string path) => new(File.ReadAllText(path), null, false, SourceName);
}
