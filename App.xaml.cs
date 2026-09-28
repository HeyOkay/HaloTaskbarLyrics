using System.Windows;

namespace TaskbarLyrics;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _rescue;

    /// <summary>Пользователь выбрал «Выход» (а не окно закрылось само — например, при перезапуске Проводника).</summary>
    public static bool Exiting { get; private set; }

    public static void Quit()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Только один экземпляр
        _mutex = new Mutex(true, "HaloTaskbarLyrics.SingleInstance", out bool created);
        // Запуск после обновления: старая версия ещё закрывается — ждём, пока она отпустит мьютекс
        if (!created && e.Args.Contains(Updater.AfterUpdateArg))
        {
            try { created = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created)
        {
            // Программа уже запущена. Повторный запуск — «спасательный круг»: если встроенный виджет
            // почему-то не виден, запущенная копия выключит встраивание и станет обычным окном
            try { EventWaitHandle.OpenExisting("HaloTaskbarLyrics.Rescue").Set(); } catch { }
            Shutdown();
            return;
        }
        _rescue = new EventWaitHandle(false, EventResetMode.AutoReset, "HaloTaskbarLyrics.Rescue");
        var rescue = _rescue;
        new Thread(() =>
        {
            while (rescue.WaitOne())
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as OverlayWindow)?.Rescue()));
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, a) =>
        {
            Log(a.Exception.ToString());
            a.Handled = true;
        };

        Autostart.Migrate();
        Updater.CleanupOld();
        base.OnStartup(e);
        ShowOverlay();
    }

    static void ShowOverlay()
    {
        var w = new OverlayWindow();
        Current.MainWindow = w;
        w.Show();
    }

    /// <summary>
    /// Встроенный виджет уничтожается вместе с панелью задач, когда перезапускается Проводник.
    /// Ждём новую панель и создаём виджет заново.
    /// </summary>
    public static void RecreateOverlay()
    {
        if (Exiting) return;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (Native.TaskbarHandle() == IntPtr.Zero) return;
            t.Stop();
            try { ShowOverlay(); } catch (Exception ex) { Log(ex.ToString()); }
        };
        t.Start();
    }

    public static void Log(string text)
    {
        Diag.Write("ERROR: " + text);
        try
        {
            Directory.CreateDirectory(Settings.AppDir);
            File.AppendAllText(Path.Combine(Settings.AppDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
