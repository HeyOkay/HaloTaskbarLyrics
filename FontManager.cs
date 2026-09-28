using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace TaskbarLyrics;

/// <summary>
/// Шрифты: системные и добавленные пользователем (.ttf/.otf/.ttc копируются в %LOCALAPPDATA%\HaloTaskbarLyrics\fonts,
/// устанавливать их в Windows не нужно).
/// </summary>
public static class FontManager
{
    public const string UserPrefix = "user:";
    const string Fallback = "Segoe UI"; // для символов, которых нет в выбранном шрифте (♪, ∅ и т. п.)

    public static string Dir => Path.Combine(Settings.AppDir, "fonts");

    public static FontFamily Default { get; } = new("Segoe UI Variable Display, Segoe UI");

    static readonly string[] Extensions = [".ttf", ".otf", ".ttc"];

    public static FontFamily Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Default;
        try
        {
            if (key.StartsWith(UserPrefix, StringComparison.Ordinal))
            {
                var name = key[UserPrefix.Length..];
                return new FontFamily(new Uri(Dir + Path.DirectorySeparatorChar), $"./#{name}, {Fallback}");
            }
            return new FontFamily($"{key}, {Fallback}");
        }
        catch { return Default; }
    }

    public static string NameOf(FontFamily f)
    {
        if (f.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out var n) && !string.IsNullOrWhiteSpace(n)) return n;
        var s = f.Source;
        int i = s.LastIndexOf('#');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    /// <summary>Шрифты, которые добавил пользователь: (ключ для настроек, название).</summary>
    public static List<(string Key, string Name)> UserFonts()
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(Dir)) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Dir))
        {
            if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            try
            {
                foreach (var fam in Fonts.GetFontFamilies(file))
                {
                    var name = NameOf(fam);
                    if (seen.Add(name)) list.Add((UserPrefix + name, name));
                }
            }
            catch (Exception ex) { App.Log($"Шрифт {file}: {ex.Message}"); }
        }
        list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>Копирует файл шрифта к себе. Возвращает ключи добавленных семейств.</summary>
    public static List<string> Install(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidOperationException(L.S("errNotFont"));

        Directory.CreateDirectory(Dir);
        var dest = Path.Combine(Dir, Path.GetFileName(path));
        if (!File.Exists(dest)) File.Copy(path, dest);

        var keys = Fonts.GetFontFamilies(dest).Select(f => UserPrefix + NameOf(f)).Distinct().ToList();
        if (keys.Count == 0) throw new InvalidOperationException(L.S("errNoFamilies"));
        return keys;
    }

    /// <summary>
    /// Жирность для строки. Если у шрифта нет настоящего полужирного/жирного начертания,
    /// Windows «раздувает» буквы сама и выходит грязно — тогда лучше обычное начертание.
    /// </summary>
    public static FontWeight BestWeight(FontFamily family, bool bold)
    {
        if (!bold) return FontWeights.Normal;
        foreach (var w in new[] { FontWeights.SemiBold, FontWeights.Bold })
        {
            try
            {
                var tf = new Typeface(family, FontStyles.Normal, w, FontStretches.Normal);
                if (!tf.IsBoldSimulated) return w;
            }
            catch { }
        }
        return FontWeights.Normal;
    }

    /// <summary>Все системные шрифты по алфавиту.</summary>
    public static List<string> SystemFonts() =>
        Fonts.SystemFontFamilies.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
}
