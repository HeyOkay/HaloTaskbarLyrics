using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TaskbarLyrics;

public enum ThemeMode { System, Light, Dark }

public sealed class Settings
{
    /// <summary>X окна в физических пикселях; null — у левого края панели.</summary>
    public int? X { get; set; }
    /// <summary>Ширина в DIP (масштабируется по DPI).</summary>
    public double Width { get; set; } = 420;
    public double FontSize { get; set; } = 15;
    /// <summary>Шрифт: null — по умолчанию, "user:Имя" — добавленный пользователем, иначе системный.</summary>
    public string? FontFamily { get; set; } = "Ink Free"; // есть в Windows 10/11; если нет — запасной шрифт
    public bool Bold { get; set; } = true;
    /// <summary>Shimmering glow under the sung part of the line.</summary>
    public bool Glow { get; set; } = true;
    /// <summary>Sung words take the same accent color as the glow and the visualizer.</summary>
    public bool AccentSung { get; set; } = true;
    /// <summary>Letters of a new line float in one by one at an angle.</summary>
    public bool LetterFx { get; set; } = true;
    /// <summary>The ring visualizer and the glow breathe with the music (loopback audio capture).</summary>
    public bool Visualizer { get; set; } = true;
    /// <summary>Text alignment: "left", "center" or "right".</summary>
    public string Align { get; set; } = "right";
    /// <summary>Vertical shift of the text inside the taskbar, DIP (negative — up).</summary>
    public double OffsetY { get; set; }
    /// <summary>Насколько раньше показывать следующую строку, мс.</summary>
    public int LineLeadMs { get; set; } = 300;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>"en" (default) or "ru".</summary>
    public string Language { get; set; } = "en";
    /// <summary>Widget position is locked (can't be dragged).</summary>
    public bool Locked { get; set; } = true;
    /// <summary>Right edge of the widget sticks to the system tray and follows it when icons appear or disappear.</summary>
    public bool DockToTray { get; set; } = true;
    /// <summary>Раз в сутки проверять, не вышла ли новая версия (GitHub Releases).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Журнал диагностики (diagnostics.log) — для разбора проблем, по умолчанию выключен.</summary>
    public bool Diagnostics { get; set; }
    /// <summary>Папка со своими .lrc-файлами; null — Документы\Lyrics.</summary>
    public string? LyricsFolder { get; set; }
    /// <summary>Ручной сдвиг синхронизации по трекам, мс.</summary>
    public Dictionary<string, int> TrackOffsets { get; set; } = new();

    [JsonIgnore]
    public string LyricsFolderResolved =>
        string.IsNullOrWhiteSpace(LyricsFolder) ? LocalLyrics.DefaultFolder : LyricsFolder;

    public static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaloTaskbarLyrics");

    /// <summary>Папка от старой версии (когда программа называлась TaskbarLyrics).</summary>
    static string OldAppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarLyrics");

    static string FilePath => Path.Combine(AppDir, "settings.json");

    /// <summary>
    /// Первый запуск под новым именем: переносим настройки, добавленные шрифты и кэш текстов
    /// из старой папки, чтобы ничего не пришлось настраивать заново.
    /// </summary>
    static void MigrateOldData()
    {
        try
        {
            if (Directory.Exists(AppDir) || !Directory.Exists(OldAppDir)) return;
            foreach (var file in Directory.EnumerateFiles(OldAppDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(AppDir, Path.GetRelativePath(OldAppDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }
        catch { /* не страшно: просто начнём с чистыми настройками */ }
    }

    public static Settings Load()
    {
        MigrateOldData();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "HaloTaskbarLyrics";
    const string OldName = "TaskbarLyrics";

    /// <summary>Автозапуск был включён у старой версии — переносим его на новое имя и новый exe.</summary>
    public static void Migrate()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(OldName) == null) return;
            k.DeleteValue(OldName, throwOnMissingValue: false);
            Set(true);
        }
        catch { }
    }

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) k.SetValue(Name, $"\"{exe}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
