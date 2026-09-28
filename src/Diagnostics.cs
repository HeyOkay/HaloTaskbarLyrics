namespace TaskbarLyrics;

/// <summary>
/// Журнал диагностики для пользователей: включается в меню «Диагностика», по умолчанию выключен.
/// Пишет только технические события: версии, панель задач и трей, положение виджета, встраивание,
/// какой плеер и трек играет и откуда взят текст. Сами тексты песен в журнал не попадают.
/// Файл — diagnostics.log рядом с настройками; больше ~1 МБ — старый переименовывается в diagnostics.old.log.
/// </summary>
public static class Diag
{
    const long MaxBytes = 1_000_000;
    static readonly object Gate = new();

    public static bool Enabled { get; private set; }

    public static string FilePath => Path.Combine(Settings.AppDir, "diagnostics.log");
    static string OldPath => Path.Combine(Settings.AppDir, "diagnostics.old.log");

    public static void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        if (on)
        {
            Enabled = true;
            var v = typeof(Diag).Assembly.GetName().Version;
            Write($"==== Diagnostics on. HaloTaskbarLyrics {v}, Windows {Environment.OSVersion.Version} (build {Native.WindowsBuild}), " +
                  $"{(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}, .NET {Environment.Version}, language {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        }
        else
        {
            Write("==== Diagnostics off");
            Enabled = false;
        }
    }

    public static void Write(string text)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.AppDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(FilePath, OldPath, true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
            }
        }
        catch { /* журнал не должен ронять программу */ }
    }
}
