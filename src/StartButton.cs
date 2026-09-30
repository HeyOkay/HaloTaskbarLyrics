using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskbarLyrics;

/// <summary>
/// Своя кнопка «Пуск» в стиле кольца-визуализатора: квадрат 15 DIP из четырёх клеток, обводка 1,5,
/// акцентный цвет, свечение, «дышит» под музыку, при нажатии сжимается в залитый квадратик.
/// Окно встроено в панель задач (дочернее Shell_TrayWnd) и лежит ровно над настоящей кнопкой «Пуск».
/// Настоящий значок закрывает подложка цвета панели: цвет берём с экрана по краям кнопки раз в секунду.
/// Левый клик — меню «Пуск» (как клавиша Win), правый — меню Win+X.
/// </summary>
internal sealed class StartButton : Window
{
    const double GlyphSize = 15, StrokeWidth = 1.5;
    const double PlateMax = 32;   // подложка, закрывающая настоящий значок, DIP

    readonly System.Windows.Shapes.Path _glyph = new();
    readonly SolidColorBrush _stroke = new(Colors.White);
    readonly SolidColorBrush _fill = new(Colors.Transparent) { Opacity = 0 };
    readonly SolidColorBrush _plateBrush = new(Colors.Transparent);
    readonly Border _plate = new() { CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed };
    readonly ScaleTransform _scale = new(1, 1);
    readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.8, RenderingBias = RenderingBias.Performance };
    readonly CancellationTokenSource _stop = new();

    IntPtr _hwnd;
    bool _embedded, _shown;
    IntPtr _host;                 // родительское окно — панель задач
    // Копия «поверх всех»: пока активна панель задач или открыт «Пуск», Проводник десятки раз в секунду
    // кладёт содержимое панели поверх встроенной кнопки, и мелькала старая. Копия — отдельное окно,
    // принадлежащее панели: его Проводник перекрыть не может. Положение и цвет подложки берёт у встроенной
    readonly bool _cover;
    Native.RECT? _button;         // настоящая кнопка «Пуск», физ. пиксели (null — не нашли)
    Native.RECT _placed;
    double _fade, _press;
    bool _pressed, _startOpen, _startWasOpen;
    Native.WinEventProc? _reorder; // держим ссылку, иначе сборщик мусора удалит обработчик
    IntPtr _reorderHook, _foregroundHook;
    int _raises;                  // сколько раз пришлось подниматься над содержимым панели (для журнала)
    DateTime _raiseLogged;
    int _lost;                    // сколько проверок подряд Windows не отдала положение кнопки
    bool _lostLogged;
    AutomationElement? _uia;      // Windows 11: элемент StartButton в XAML панели задач
    DateTime _uiaFound;           // когда последний раз искали элемент заново
    string? _lookupError;         // почему не нашли (для журнала диагностики)
    Native.RECT? _taskbarAtFound; // панель задач, на которой кнопку нашли в последний раз

    public StartButton(bool cover = false)
    {
        _cover = cover;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = cover;
        Width = Height = 1;
        Left = Top = -10000;
        Title = "HaloTaskbarLyrics Start";

        var rect = new RectangleGeometry(new Rect(StrokeWidth / 2, StrokeWidth / 2, GlyphSize - StrokeWidth, GlyphSize - StrokeWidth), 1.5, 1.5);
        var mid = GlyphSize / 2;
        var geo = new GeometryGroup();
        geo.Children.Add(rect);
        geo.Children.Add(new LineGeometry(new Point(mid, StrokeWidth / 2), new Point(mid, GlyphSize - StrokeWidth / 2)));
        geo.Children.Add(new LineGeometry(new Point(StrokeWidth / 2, mid), new Point(GlyphSize - StrokeWidth / 2, mid)));
        geo.Freeze();

        _glyph.Data = geo;
        _glyph.Width = _glyph.Height = GlyphSize;
        _glyph.Stroke = _stroke;
        _glyph.StrokeThickness = StrokeWidth;
        _glyph.Fill = _fill;
        _glyph.HorizontalAlignment = HorizontalAlignment.Center;
        _glyph.VerticalAlignment = VerticalAlignment.Center;
        _glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        _glyph.RenderTransform = _scale;
        _glyph.Effect = _glow;
        _glyph.Opacity = 0;
        _glyph.IsHitTestVisible = false;

        _plate.Background = _plateBrush;
        _plate.HorizontalAlignment = HorizontalAlignment.Center;
        _plate.VerticalAlignment = VerticalAlignment.Center;
        _plate.IsHitTestVisible = false;
        _plate.Width = _plate.Height = PlateMax;

        // Почти прозрачный фон: клики по всей площади кнопки ловим мы, а не настоящая кнопка под нами
        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        root.Children.Add(_plate);
        root.Children.Add(_glyph);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            var taskbar = Native.TaskbarHandle();
            if (_cover)
            {
                // Владелец — панель задач: такое окно всегда выше неё и всего, что внутри неё
                Native.SetOwner(_hwnd, taskbar);
                Native.ShowWindow(_hwnd, Native.SW_HIDE);
                return;
            }
            _host = taskbar;
            _embedded = _host != IntPtr.Zero && Native.Embed(_hwnd, _host);
            if (_embedded)
            {
                // Проводник переложил окна внутри панели или сменилось активное окно (открылся «Пуск») —
                // сразу проверяем, не оказались ли под содержимым панели, не дожидаясь следующего кадра
                _reorder = (_, _, _, _, _, _, _) => KeepOnTop();
                _reorderHook = Native.SetWinEventHook(Native.EVENT_OBJECT_REORDER, Native.EVENT_OBJECT_REORDER,
                    IntPtr.Zero, _reorder, Native.ProcessOf(taskbar), 0, Native.WINEVENT_OUTOFCONTEXT);
                _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _reorder, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
                Native.OwnChildren.Add(_hwnd);
                Native.ShowWindow(_hwnd, Native.SW_HIDE); // покажемся, когда найдём настоящую кнопку
                Diag.Write($"Start button: embedded into {Native.ClassOf(_host)}");
            }
            else Diag.Write("Start button: embed failed — not shown");
        };
        Loaded += (_, _) =>
        {
            if (_cover) { if (!_shown) Native.ShowWindow(_hwnd, Native.SW_HIDE); }
            else if (_embedded) _ = WatchAsync(_stop.Token);
            else Close();
        };
        Closed += (_, _) =>
        {
            _stop.Cancel();
            if (_reorderHook != IntPtr.Zero) Native.UnhookWinEvent(_reorderHook);
            if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
            Native.OwnChildren.Remove(_hwnd);
        };
    }

    // ---------------- Цвета ----------------

    /// <summary>Акцентный цвет (тот же, что у кольца) и светлая ли панель задач.</summary>
    internal void ApplyTheme(Color accent, bool lightTaskbar)
    {
        _stroke.Color = accent;
        _fill.Color = accent;
        _glow.Color = accent;
    }

    // ---------------- Каждый кадр (из OverlayWindow.AnimateRing) ----------------

    /// <param name="s">Сглаженный уровень музыки 0…1 — тот же, которым дышит кольцо.</param>
    internal void Animate(double s)
    {
        if (_hwnd == IntPtr.Zero || !_shown || !(_embedded || _cover)) return;

        if (_cover) { if (Native.NeedsRaise(_hwnd)) Native.BringToTop(_hwnd); }
        else KeepOnTop();

        _startOpen = Native.IsStartMenuForeground();
        _fade += (1 - _fade) * 0.06;

        // Как у кольца: нажали — быстро сжимается в залитый квадратик, отпустили — пружинит обратно
        bool dot = _pressed;
        _press += ((dot ? 1 : 0) - _press) * (dot ? 0.35 : 0.2);
        if (_press < 0.002) _press = 0;
        double pr = _press;
        _glyph.Opacity = _fade * (0.45 + 0.40 * s + (0.55 - 0.40 * s) * pr);
        _scale.ScaleX = _scale.ScaleY = (1 + 0.12 * s) * (1 - 0.55 * pr);
        _glow.BlurRadius = 4 + 6 * s;
        if (Math.Abs(_fill.Opacity - pr) > 0.002) _fill.Opacity = pr;
    }

    // ---------------- Копия «поверх всех» ----------------

    /// <summary>Где сейчас стоит встроенная кнопка (null — спрятана). Для копии.</summary>
    internal Native.RECT? VisibleRect => _shown ? _button : null;
    internal Color PlateColor => _plateBrush.Color;
    internal bool PlateShown => _plate.Visibility == Visibility.Visible;

    /// <summary>Копия: встать ровно над встроенной кнопкой (rect — экранные физ. пиксели) или спрятаться (null).</summary>
    internal void Follow(Native.RECT? rect, Color plate, bool plateShown)
    {
        if (!_cover || _hwnd == IntPtr.Zero) return;
        if (rect is not { } b)
        {
            if (_shown) { Native.ShowWindow(_hwnd, Native.SW_HIDE); _shown = false; }
            return;
        }
        _plateBrush.Color = plate;
        _plate.Visibility = plateShown ? Visibility.Visible : Visibility.Collapsed;
        if (b.Left != _placed.Left || b.Top != _placed.Top || b.Right != _placed.Right || b.Bottom != _placed.Bottom || !_shown)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE);
            _placed = b;
            SizePlate(b);
        }
        if (!_shown)
        {
            _fade = 1; // копия появляется сразу, без плавного проявления, — иначе было бы видно подмену
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.BringToTop(_hwnd);
            _shown = true;
        }
    }

    void SizePlate(Native.RECT b)
    {
        // Подложка не больше самой кнопки
        var dpi = VisualTreeHelper.GetDpi(this);
        double side = Math.Min(b.Width / dpi.DpiScaleX, b.Height / dpi.DpiScaleY);
        _plate.Width = _plate.Height = Math.Min(PlateMax, side - 6);
    }

    /// <summary>Панель задач переложила своё содержимое поверх нас — поднимаемся обратно.</summary>
    void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || !_shown) return;
        var parent = Native.GetParent(_hwnd);
        if (parent == IntPtr.Zero || Native.IsTopChild(parent, _hwnd)) return;
        var cover = Diag.Enabled ? Native.ForeignAbove(parent, _hwnd) : IntPtr.Zero;
        Native.RaiseChild(_hwnd);
        _raises++;
        if (Diag.Enabled && (DateTime.Now - _raiseLogged).TotalSeconds >= 5)
        {
            Diag.Write($"Start button: covered by {Native.ClassOf(cover)}, raised back ({_raises} times so far), parent {Native.ClassOf(parent)}, fg {Native.ForegroundInfo()}");
            _raiseLogged = DateTime.Now;
        }
    }

    // ---------------- Где настоящая кнопка ----------------

    /// <summary>
    /// 10 раз в секунду узнаём, где настоящая кнопка «Пуск» (на панели по центру она сдвигается,
    /// когда открываются и закрываются приложения), раз в секунду — цвет панели вокруг неё.
    /// </summary>
    async Task WatchAsync(CancellationToken ct)
    {
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var found = await Task.Run(() => FindButton());
                if (ct.IsCancellationRequested) break;
                // Windows иногда не отдаёт положение кнопки (например, пока открыт «Пуск» или поиск).
                // Тогда остаёмся на прежнем месте — пока сама панель задач не сдвинулась и не изменилась
                var bar = Native.GetTaskbarRect();
                if (found != null) { _lost = 0; _taskbarAtFound = bar; }
                else if (_button != null && SameRect(bar, _taskbarAtFound))
                {
                    found = _button;
                    if (++_lost == 1) Diag.Write($"Start button: lookup failed ({_lookupError}), keeping the last position");
                }
                if (found != null && _lost == 0 && _button != null && _lostLogged) { Diag.Write("Start button: found again"); }
                _lostLogged = _lost > 0;
                var r = found;
                if ((r == null) != (_button == null)) Diag.Write(r is { } f ? $"Start button: found {f.Left},{f.Top}-{f.Right},{f.Bottom}" : $"Start button: not found ({_lookupError}) — hidden");
                _button = r;
                Place();
                // Цвет подложки: не во время нажатия и не при открытом «Пуске»
                if (r is { } rr && (n++ % 10 == 0 || _plateBrush.Color.A == 0) && _press == 0 && !_startOpen)
                {
                    var c = await Task.Run(() => SampleColor(rr));
                    if (c is { } col && !ct.IsCancellationRequested)
                    {
                        _plateBrush.Color = col;
                        _plate.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex) { App.Log("Start button: " + ex.Message); }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Прямоугольник настоящей кнопки «Пуск», физ. пиксели. Вызывается из фонового потока.</summary>
    Native.RECT? FindButton()
    {
        var taskbar = Native.TaskbarHandle();
        if (taskbar == IntPtr.Zero) return null;
        // Windows 10: кнопка — обычное окно "Start" внутри панели
        if (Native.WindowsBuild < 22000) return Native.GetStartWindowRect(taskbar);

        // Windows 11: кнопка нарисована на XAML — находим её через UI Automation один раз, а дальше
        // только читаем её прямоугольник (это дёшево). Найденный элемент не выбрасываем, пока он работает:
        // повторный поиск, пока открыт «Пуск» или поиск, Windows иногда отвечает «не найдено»
        if (_uia != null)
        {
            try
            {
                if (ToRect(_uia.Current.BoundingRectangle) is { } r) return r;
            }
            catch (Exception ex) { _lookupError = ex.Message; }
            _uia = null; // элемент устарел (например, Проводник перестроил панель) — найдём заново
        }

        // Искать заново — не чаще раза в секунду: полный поиск по панели стоит десятки миллисекунд
        if ((DateTime.Now - _uiaFound).TotalSeconds < 1) return null;
        _uiaFound = DateTime.Now;
        try
        {
            var el = AutomationElement.FromHandle(taskbar).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (el != null && ToRect(el.Current.BoundingRectangle) is { } r)
            {
                _uia = el;
                return r;
            }
            _lookupError = el == null ? "StartButton element not found" : "empty rectangle";
        }
        catch (Exception ex) { _lookupError = ex.Message; }
        return Native.GetStartWindowRect(taskbar);
    }

    static Native.RECT? ToRect(Rect r) =>
        r.IsEmpty || r.Width < 8 || r.Height < 8 ? null : new Native.RECT
        {
            Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top),
            Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom),
        };

    static bool SameRect(Native.RECT? a, Native.RECT? b) =>
        a is { } x && b is { } y && x.Left == y.Left && x.Top == y.Top && x.Right == y.Right && x.Bottom == y.Bottom;

    /// <summary>Встаём ровно над настоящей кнопкой (или прячемся, если её не нашли).</summary>
    void Place()
    {
        if (_button is not { } b || b.Width <= 0 || _host == IntPtr.Zero || Native.GetParent(_hwnd) != _host)
        {
            if (_shown) { Native.ShowWindow(_hwnd, Native.SW_HIDE); _shown = false; }
            return;
        }
        if (b.Left != _placed.Left || b.Top != _placed.Top || b.Right != _placed.Right || b.Bottom != _placed.Bottom || !_shown)
        {
            var (cx, cy) = Native.ToClient(_host, b.Left, b.Top);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, cx, cy, b.Width, b.Height, Native.SWP_NOACTIVATE);
            _placed = b;
            SizePlate(b);
        }
        if (!_shown) { Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE); _shown = true; }
    }

    /// <summary>Цвет панели задач по краям кнопки (там нет ни значка, ни нашего свечения). Из фонового потока.</summary>
    static Color? SampleColor(Native.RECT r)
    {
        int y1 = r.Top + r.Height / 4, y2 = r.Top + r.Height * 3 / 4;
        return Native.SampleScreen(new[] { (r.Left + 1, y1), (r.Left + 1, y2), (r.Right - 2, y1), (r.Right - 2, y2) });
    }

    // ---------------- Мышь ----------------

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) _pressed = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        MenuWindow.CloseOpen();
        // Был ли открыт «Пуск» до клика (значение прошлого кадра: сам клик мог его уже закрыть)
        _startWasOpen = _startOpen;
        _pressed = true;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        bool wasPressed = _pressed;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!wasPressed || !CursorInside()) return; // увели мышь с кнопки — как у обычной кнопки, ничего не делаем
        // Меню «Пуск» было открыто: клик его закрывает. Если Windows не закрыла его сама — закрываем клавишей Win
        if (_startWasOpen)
        {
            if (Native.IsStartMenuForeground()) Native.PressKeys(Native.VK_LWIN);
        }
        else Native.PressKeys(Native.VK_LWIN);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        MenuWindow.CloseOpen();
        Native.PressKeys(Native.VK_LWIN, Native.VK_X); // системное меню Win+X, как у настоящей кнопки
    }

    bool CursorInside() =>
        Native.GetCursorPos(out var p) && Native.GetWindowRect(_hwnd, out var r)
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
