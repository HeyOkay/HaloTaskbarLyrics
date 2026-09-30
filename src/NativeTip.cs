using System.Runtime.InteropServices;
using System.Windows.Media;

namespace TaskbarLyrics;

/// <summary>
/// Всплывающая подсказка виджета — стандартный системный элемент Windows (tooltips_class32 из
/// Common Controls 6). Внешний вид (скругление, тень, фон, шрифт) рисует сама Windows, как у подсказок
/// значков трея. Мы только задаём текст и место: по центру над кольцом, над панелью задач.
/// На Windows 11 под подсказку подкладывается акрил: фон, который рисует Windows, делаем прозрачным
/// (текст и рамка остаются), а под ним — размытие того, что за окном, как у меню программы.
/// </summary>
sealed class NativeTip : IDisposable
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int TTS_ALWAYSTIP = 0x01, TTS_NOPREFIX = 0x02;
    const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    const uint TTF_TRACK = 0x0020, TTF_ABSOLUTE = 0x0080;
    const uint TTM_TRACKACTIVATE = 0x0411, TTM_TRACKPOSITION = 0x0412, TTM_SETMAXTIPWIDTH = 0x0418,
               TTM_GETBUBBLESIZE = 0x041E, TTM_ADDTOOLW = 0x0432, TTM_UPDATETIPTEXTW = 0x0439;
    const int ICC_WIN95_CLASSES = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    struct INITCOMMONCONTROLSEX { public int dwSize; public int dwICC; }

    [StructLayout(LayoutKind.Sequential)]
    struct TOOLINFO
    {
        public int cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public IntPtr uId;
        public Native.RECT rect;
        public IntPtr hinst;
        public IntPtr lpszText;
        public IntPtr lParam;
        public IntPtr lpReserved;
    }

    [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string? name, int style, int x, int y, int w, int h,
                                        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, ref TOOLINFO l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string? app, string? idList);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, ref Native.RECT l);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr dc, string text, int len, ref Native.RECT r, uint format);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    const uint WM_GETFONT = 0x0031, TTM_ADJUSTRECT = 0x041F;
    const uint DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_CALCRECT = 0x400;

    // ---- Акрил (только Windows 11) ----
    static readonly bool Acrylic = Environment.OSVersion.Version.Build >= 22000;
    const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_PRINTCLIENT = 0x0318;
    const int PRF_CLIENT = 0x04, PRF_ERASEBKGND = 0x08, BPBF_TOPDOWNDIB = 2;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUNDSMALL = 3;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMSBT_TRANSIENTWINDOW = 3;                  // системный акрил всплывающих окон Windows 11
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE); // без рамки вокруг окна
    // Системный акрил DWM (DWMSBT_TRANSIENTWINDOW) проверен 29.09 — не сработал: подсказка никогда не бывает
    // активным окном, и Windows показывает сплошной фон. Поэтому акрил — через accent policy, оттенок по замерам
    static readonly bool SystemBackdrop = false;

    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public Native.RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    delegate IntPtr SubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Native.RECT r);
    [DllImport("uxtheme.dll")] static extern int BufferedPaintInit();
    [DllImport("uxtheme.dll")] static extern IntPtr BeginBufferedPaint(IntPtr hdcTarget, ref Native.RECT rc, int format, IntPtr param, out IntPtr hdc);
    [DllImport("uxtheme.dll")] static extern int GetBufferedPaintBits(IntPtr pb, out IntPtr bits, out int cxRow);
    [DllImport("uxtheme.dll")] static extern int EndBufferedPaint(IntPtr pb, bool update);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    SubclassProc? _proc; // держим ссылку, чтобы сборщик мусора не удалил делегат
    int _paints;

    readonly IntPtr _tip;
    readonly IntPtr _owner;
    bool _shown;
    bool? _dark;

    public NativeTip(IntPtr owner)
    {
        _owner = owner;
        var icc = new INITCOMMONCONTROLSEX { dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_WIN95_CLASSES };
        InitCommonControlsEx(ref icc);
        _tip = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "tooltips_class32", null,
                              WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_tip == IntPtr.Zero) return;
        SendMessage(_tip, TTM_SETMAXTIPWIDTH, IntPtr.Zero, (IntPtr)2000); // одна строка, без переносов
        var ti = Info(IntPtr.Zero);
        SendMessage(_tip, TTM_ADDTOOLW, IntPtr.Zero, ref ti);

        if (Acrylic)
        {
            try
            {
                BufferedPaintInit();
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(_tip, ref m);
                int corner = DWMWCP_ROUNDSMALL;
                DwmSetWindowAttribute(_tip, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                int noBorder = DWMWA_COLOR_NONE; // серую рамку по краю рисует Windows — у подсказок трея её нет
                DwmSetWindowAttribute(_tip, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
                if (SystemBackdrop)
                {
                    int backdrop = DWMSBT_TRANSIENTWINDOW;
                    DwmSetWindowAttribute(_tip, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                _proc = TipProc;
                SetWindowSubclass(_tip, _proc, (UIntPtr)1, UIntPtr.Zero);
            }
            catch (Exception ex) { App.Log(ex.ToString()); }
        }
    }

    // Оттенок акрила — тот же, что у панели с текстом (LyricsFlyout.Tint)
    static Color Tint(bool dark) => dark ? Color.FromArgb(0xC8, 0x22, 0x22, 0x26) : Color.FromArgb(0xC8, 0xF3, 0xF3, 0xF3);

    IntPtr TipProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_ERASEBKGND) return (IntPtr)1; // фон рисуем сами в WM_PAINT — без мигания
        if (msg != WM_PAINT) return DefSubclassProc(h, msg, w, l);
        try { PaintAcrylic(h); }
        catch (Exception ex) { App.Log(ex.ToString()); return DefSubclassProc(h, msg, w, l); }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Windows рисует подсказку как обычно, но в память. Потом каждый пиксель: чем сильнее он отличается
    /// от фона, тем он непрозрачнее (текст, рамка), а чистый фон становится полностью прозрачным —
    /// там виден акрил. Цвет текста остаётся системным (белый в тёмной теме, чёрный в светлой).
    /// </summary>
    void PaintAcrylic(IntPtr h)
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(h, out ps);
        try
        {
            GetClientRect(h, out var rc);
            int width = rc.Width, height = rc.Height;
            if (width <= 6 || height <= 6) return;
            var pb = BeginBufferedPaint(hdc, ref rc, BPBF_TOPDOWNDIB, IntPtr.Zero, out var mem);
            if (pb == IntPtr.Zero) return;
            try
            {
                DefSubclassProc(h, WM_PRINTCLIENT, mem, (IntPtr)(PRF_CLIENT | PRF_ERASEBKGND));
                if (GetBufferedPaintBits(pb, out var bits, out int row) != 0) return;
                var px = new int[row * height];
                Marshal.Copy(bits, px, 0, px.Length);

                // Цвет фона — у левого края по центру (там всегда поле, текста нет)
                int bg = px[height / 2 * row + 3];
                int bR = (bg >> 16) & 255, bG = (bg >> 8) & 255, bB = bg & 255;
                int fg = _dark == false ? 0 : 255;
                double span = Math.Abs(fg - bR) + Math.Abs(fg - bG) + Math.Abs(fg - bB);
                if (_paints++ < 3) Diag.Write($"Tip paint: {width}x{height}, bg #{bg & 0xFFFFFF:X6}, span {span}");
                if (span < 30) return;

                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * row + x, p = px[i];
                        // Серую рамку по краю, которую рисует тема, убираем — у системной подсказки её нет
                        if (x == 0 || y == 0 || x == width - 1 || y == height - 1) { px[i] = 0; continue; }
                        int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                        double t = Math.Min(1, (Math.Abs(r - bR) + Math.Abs(g - bG) + Math.Abs(b - bB)) / span);
                        uint a = (uint)Math.Round(t * 255), c = (uint)Math.Round(fg * t);
                        px[i] = unchecked((int)(a << 24 | c << 16 | c << 8 | c)); // premultiplied
                    }
                Marshal.Copy(px, 0, bits, px.Length);
            }
            finally { EndBufferedPaint(pb, true); }
        }
        finally { EndPaint(h, ref ps); }
    }

    public bool IsShown => _shown;

    TOOLINFO Info(IntPtr text) => new()
    {
        cbSize = Marshal.SizeOf<TOOLINFO>(),
        uFlags = TTF_TRACK | TTF_ABSOLUTE,
        hwnd = _owner,
        uId = (IntPtr)1,
        lpszText = text,
    };

    /// <summary>Показать (или обновить): по центру над centerX, нижний край — на bottomPx. Всё в физ. пикселях.</summary>
    public void Show(string text, int centerX, int bottomPx, int minX, int maxX)
    {
        if (_tip == IntPtr.Zero) return;
        // Тёмная или светлая тема — как у подсказок Проводника
        bool dark = !Theme.IsLight;
        if (_dark != dark)
        {
            SetWindowTheme(_tip, dark ? "DarkMode_Explorer" : "Explorer", null);
            _dark = dark;
            if (Acrylic)
            {
                // Тёмный или светлый вариант системного акрила
                int d = dark ? 1 : 0;
                DwmSetWindowAttribute(_tip, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
                if (!SystemBackdrop) Native.EnableBlur(_tip, Tint(dark), 0);
            }
        }

        var p = Marshal.StringToHGlobalUni(text);
        try
        {
            var ti = Info(p);
            SendMessage(_tip, TTM_UPDATETIPTEXTW, IntPtr.Zero, ref ti);
        }
        finally { Marshal.FreeHGlobal(p); }

        // Размер подсказки. До первого показа Windows сообщает 0×0 — тогда считаем сами по тексту и шрифту,
        // а сразу после показа уточняем и переставляем (до того, как окно успеет нарисоваться)
        var (w, h) = BubbleSize();
        if (w <= 0 || h <= 0) (w, h) = EstimateSize(text);
        Place(w, h, centerX, bottomPx, minX, maxX);
        if (!_shown)
        {
            var ti3 = Info(IntPtr.Zero);
            SendMessage(_tip, TTM_TRACKACTIVATE, (IntPtr)1, ref ti3);
            _shown = true;
            var (w2, h2) = BubbleSize();
            if (w2 > 0 && h2 > 0 && (w2 != w || h2 != h)) Place(w2, h2, centerX, bottomPx, minX, maxX);
            Diag.Write($"Tip: estimated {w}x{h}, real {w2}x{h2}, exstyle 0x{GetWindowLong(_tip, -20):X}, acrylic {Acrylic}, subclass {_proc != null}");
        }
    }

    void Place(int w, int h, int centerX, int bottomPx, int minX, int maxX)
    {
        int x = Math.Clamp(centerX - w / 2, minX, Math.Max(minX, maxX - w));
        int y = bottomPx - h;
        SendMessage(_tip, TTM_TRACKPOSITION, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
    }

    (int W, int H) BubbleSize()
    {
        var ti = Info(IntPtr.Zero);
        long size = SendMessage(_tip, TTM_GETBUBBLESIZE, IntPtr.Zero, ref ti).ToInt64();
        return ((int)(size & 0xFFFF), (int)((size >> 16) & 0xFFFF));
    }

    /// <summary>Размер по тексту: ширина строки шрифтом подсказки + поля, которые добавляет сама подсказка.</summary>
    (int W, int H) EstimateSize(string text)
    {
        var r = new Native.RECT();
        var dc = GetDC(_tip);
        try
        {
            var font = SendMessage(_tip, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
            var old = font != IntPtr.Zero ? SelectObject(dc, font) : IntPtr.Zero;
            DrawText(dc, text, -1, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
            if (old != IntPtr.Zero) SelectObject(dc, old);
        }
        finally { ReleaseDC(_tip, dc); }
        SendMessage(_tip, TTM_ADJUSTRECT, (IntPtr)1, ref r);
        return (r.Width, r.Height);
    }

    public void Hide()
    {
        if (_tip == IntPtr.Zero || !_shown) return;
        var ti = Info(IntPtr.Zero);
        SendMessage(_tip, TTM_TRACKACTIVATE, IntPtr.Zero, ref ti);
        _shown = false;
    }

    public void Dispose()
    {
        if (_tip != IntPtr.Zero) DestroyWindow(_tip);
    }
}
