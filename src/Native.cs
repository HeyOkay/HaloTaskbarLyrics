using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace TaskbarLyrics;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    /// <summary>WM_NCCALCSIZE: [0] новый прямоугольник окна → новая клиентская область; [1], [2] — куда и откуда копировать старую картинку.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS { public RECT R0, R1, R2; public IntPtr Pos; }
    public const int WM_NCCALCSIZE = 0x0083, WVR_VALIDRECTS = 0x0400;
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2), HWND_BOTTOM = new(1);
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const int GWLP_HWNDPARENT = -8;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    /// <summary>
    /// Делает окно «принадлежащим» панели задач: Windows сама держит такое окно над панелью,
    /// даже когда панель поднимается кликом по ней или по трею (поэтому виджет не моргает).
    /// </summary>
    public static void SetOwner(IntPtr h, IntPtr owner) => SetWindowLongPtr(h, GWLP_HWNDPARENT, owner);
    public static IntPtr GetOwner(IntPtr h) => GetWindowLongPtr(h, GWLP_HWNDPARENT);

    // ---------- Встраивание внутрь панели задач ----------

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    const int GWL_STYLE = -16;
    const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
    const uint GW_CHILD = 5;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    /// <summary>
    /// Делает окно дочерним окном панели задач. Тогда Windows показывает и прячет его вместе с панелью:
    /// оно не моргает при сворачивании окон и видно в «Представлении задач» (Win+Tab).
    /// </summary>
    public static bool Embed(IntPtr h, IntPtr taskbar)
    {
        int style = GetWindowLong(h, GWL_STYLE);
        SetWindowLong(h, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
        SetParent(h, taskbar);
        if (GetParent(h) == taskbar) return true;
        SetWindowLong(h, GWL_STYLE, style); // не получилось — возвращаем как было
        return false;
    }

    /// <summary>Снова обычное отдельное окно.</summary>
    public static void Unembed(IntPtr h)
    {
        SetParent(h, IntPtr.Zero);
        int style = GetWindowLong(h, GWL_STYLE);
        SetWindowLong(h, GWL_STYLE, (style & ~WS_CHILD) | WS_POPUP);
    }

    /// <summary>Мы — верхнее из дочерних окон панели (над её собственным содержимым).</summary>
    public static bool IsTopChild(IntPtr parent, IntPtr h) => GetWindow(parent, GW_CHILD) == h;

    public static void RaiseChild(IntPtr h) =>
        SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Экранные координаты → координаты внутри окна.</summary>
    public static (int X, int Y) ToClient(IntPtr h, int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        ScreenToClient(h, ref p);
        return (p.X, p.Y);
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const uint GW_HWNDPREV = 3;
    const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// Окно потеряло режим «поверх всех» или панель задач оказалась выше него.
    /// Так бывает после Win+D и сворачивания окон: Windows на миг снимает «поверх всех» с панели,
    /// а вместе с ней и с нас, а потом возвращает только панели.
    /// </summary>
    public static bool NeedsRaise(IntPtr self)
    {
        if ((GetWindowLong(self, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) return true;
        var tb = Taskbar();
        if (tb == IntPtr.Zero) return false;
        int guard = 0;
        for (var h = GetWindow(self, GW_HWNDPREV); h != IntPtr.Zero && guard++ < 1000; h = GetWindow(h, GW_HWNDPREV))
            if (h == tb) return true; // панель выше нас
        return false;
    }

    /// <summary>
    /// Сейчас активна системная часть Windows: сама панель задач и её меню, «Пуск», поиск,
    /// «Представление задач» (Win+Tab), скрытые значки трея и прочие окна Проводника,
    /// кроме обычных окон с папками. В это время Windows перестраивает трей, и его граница «врёт».
    /// </summary>
    /// <summary>Для отладки: класс и процесс активного окна.</summary>
    public static string ForegroundInfo()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "(none)";
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        GetWindowThreadProcessId(fg, out var pid);
        string name = "?";
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        return $"{sb} [{name}]";
    }

    /// <summary>Открыто окно скрытых значков трея (по клику на стрелку «^»).</summary>
    public static bool IsTrayOverflowForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        return sb.ToString() is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
    }

    public static bool IsShellOverlayForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls is "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "TaskSwitcherWnd"
            or "Windows.UI.Core.CoreWindow" or "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow"
            or "ForegroundStaging" or "#32768" or "Xaml_WindowedPopupClass") return true;
        if (cls is "CabinetWClass" or "ExploreWClass") return false; // обычное окно с папкой

        // Любое другое окно процесса Проводника (панель задач, её контекстное меню и т. п.)
        var tray = Taskbar();
        if (tray == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        GetWindowThreadProcessId(tray, out var shellPid);
        return fgPid != 0 && fgPid == shellPid;
    }

    /// <summary>Окно в режиме «поверх всех».</summary>
    public static bool IsTopmost(IntPtr h) => (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZEEND = 0x0017, WINEVENT_OUTOFCONTEXT = 0;
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    public static int WindowsBuild => Environment.OSVersion.Version.Build;

    /// <summary>
    /// Windows 11 look for a borderless window: blurred, see-through acrylic and rounded, anti-aliased
    /// corners drawn by Windows itself. The system backdrop (DWMWA_SYSTEMBACKDROP_TYPE) is not used:
    /// for a frameless WPF window it often stays opaque, so the acrylic comes from the accent policy,
    /// which works on every Windows 11 build.
    /// </summary>
    public static void ApplyWin11Backdrop(IntPtr h, bool dark, Color tint)
    {
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
        SetDarkTitleBar(h, dark);
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        EnableBlur(h, tint, 0);
    }

    /// <summary>Снова поверх всех окон (без перемещения и без фокуса).</summary>
    public static void BringToTop(IntPtr h) =>
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void MakeToolWindow(IntPtr h) =>
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

    static IntPtr Taskbar() => FindWindow("Shell_TrayWnd", null);
    public static IntPtr TaskbarHandle() => Taskbar();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Левая граница окна трея (Windows 10 и часть сборок Windows 11), иначе null.</summary>
    public static int? GetTrayNotifyLeft(IntPtr taskbar)
    {
        var h = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Width <= 0) return null;
        return r.Left;
    }

    /// <summary>Прямоугольник основной панели задач в физических пикселях.</summary>
    public static RECT? GetTaskbarRect()
    {
        var h = Taskbar();
        return h != IntPtr.Zero && GetWindowRect(h, out var r) ? r : null;
    }

    /// <summary>Активно полноэкранное окно на мониторе с панелью задач (игра, видео, презентация).</summary>
    public static bool IsForegroundFullscreen(IntPtr self)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self) return false;

        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "XamlExplorerHostIslandWindow") return false;
        if (!IsWindowVisible(fg)) return false;

        // Окна самого Проводника (трей, «скрытые значки», всплывающие панели) — не полноэкранные приложения
        var tray = Taskbar();
        if (tray != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out var fgPid);
            GetWindowThreadProcessId(tray, out var shellPid);
            if (fgPid != 0 && fgPid == shellPid) return false;
        }

        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var tb = Taskbar();
        if (tb != IntPtr.Zero && MonitorFromWindow(tb, MONITOR_DEFAULTTONEAREST) != mon) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out var r)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>
    /// Размытие того, что под окном (акрил в Windows 10 1803+ и Windows 11, иначе обычное размытие),
    /// и скруглённые углы.
    /// </summary>
    public static void EnableBlur(IntPtr h, Color tint, int cornerRadiusPx)
    {
        var accent = new AccentPolicy
        {
            AccentState = Environment.OSVersion.Version.Build >= 17134 ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND,
            AccentFlags = 2,
            // формат ABGR
            GradientColor = (uint)tint.A << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(h, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }

        // Размытие занимает всё окно целиком, поэтому обрезаем окно по скруглённому прямоугольнику
        if (cornerRadiusPx > 0 && GetWindowRect(h, out var r))
            SetWindowRgn(h, CreateRoundRectRgn(0, 0, r.Width + 1, r.Height + 1, cornerRadiusPx * 2, cornerRadiusPx * 2), true);
    }

    /// <summary>Окно видно только внутри скруглённого прямоугольника (x, y — от левого верхнего угла окна), физ. пиксели.</summary>
    public static void SetRoundRegion(IntPtr h, int x, int y, int w, int height, int radius) =>
        SetWindowRgn(h, CreateRoundRectRgn(x, y, x + w + 1, y + height + 1, radius * 2, radius * 2), true);

    /// <summary>Тёмный заголовок окна (Windows 10 20H1+ / 11).</summary>
    public static void SetDarkTitleBar(IntPtr h, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }
}
