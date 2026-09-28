using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using RepeatMode = global::Windows.Media.MediaPlaybackAutoRepeatMode;

namespace TaskbarLyrics;

/// <summary>
/// Панель с текстом песни — открывается кликом по кольцу. Сверху весь текст: текущая строка по центру
/// с заливкой пропетого и свечением, выше прошедшие, ниже следующие (чем дальше — тем тусклее, края растворяются).
/// Смена строки анимируется так же, как строка на панели задач (буквы всплывают, размытие тает, свечение переливается).
/// Колесом мыши текст можно прокрутить; через несколько секунд он сам возвращается к текущей строке.
/// Снизу — обложка, название, «Поделиться» (ссылка на трек), полоса времени с перемоткой и кнопки плеера.
/// Shuffle и Repeat показываются, только если плеер сообщает Windows, что умеет их.
/// Пока панель открыта, строка на панели задач спрятана (остаётся кольцо).
/// Закрывается повторным кликом по кольцу, кликом мимо панели или клавишей Esc.
/// </summary>
public sealed class LyricsFlyout : Window
{
    static LyricsFlyout? _open;
    static DateTime _closedAt;

    public static bool IsOpen => _open != null;
    /// <summary>Панель закрылась только что: тот же клик по кольцу, которым её закрыли, не должен открыть её снова.</summary>
    public static bool JustClosed => DateTime.Now - _closedAt < TimeSpan.FromMilliseconds(400);
    public static void CloseOpen() => _open?.CloseFlyout();

    public static void Open(OverlayWindow host)
    {
        CloseOpen();
        var f = new LyricsFlyout(host);
        _open = f;
        f.ShowNearRing();
    }

    // Размеры, DIP
    const double PanelWidth = 380;
    const double Box = 356;             // область с текстом (квадрат)
    const double LineHeight = 46;       // высота строки в одну строчку; длинная строка переносится и занимает больше
    const double CurrentSize = 21;      // текущая строка
    const double OtherSize = 18;        // остальные строки
    const double Small = OtherSize / CurrentSize;
    const double TextMaxWidth = 332;
    const int ActiveSlot = 2;           // текущая строка стоит на месте третьей сверху (0 — первая)
    const double FadeEdge = 64;         // высота растворяющихся краёв сверху и снизу
    const double EdgeBlurZone = 56;     // у краёв строки размываются: чем ближе к краю, тем сильнее (как в iOS)
    const double EdgeBlurMax = 8;
    const double ThumbWidth = 1.5;      // полоса прокрутки — как линия кольца-визуализатора
    const double KnobSize = 10;         // кружок на полосе перемотки

    // Анимации — как у строки на панели задач (OverlayWindow)
    static readonly TimeSpan InDuration = TimeSpan.FromMilliseconds(420);
    static readonly TimeSpan OutDuration = TimeSpan.FromMilliseconds(300);
    const double BlurMax = 12;
    // Пропетая строка выцветает: через SungFadeDelay после конца заливки цвет акцента медленно
    // (за SungFadeSec) переходит в обычный цвет текста, свечение так же медленно гаснет
    const double SungFadeDelay = 1.5;
    const double SungFadeSec = 3;

    readonly OverlayWindow _host;
    readonly bool _win11 = Native.WindowsBuild >= 22000;
    readonly bool _light = Theme.IsLight;
    readonly Color _fg, _dimFg, _accent, _hover, _sung;
    IntPtr _hwnd;
    Grid _panelGrid = null!;            // сама панель (прижата к низу окна)
    // Всё, что под текстом (обложка, название, полоса, кнопки), — в отдельном прозрачном окне поверх панели.
    // Оно никогда не двигается и не меняет размер, поэтому не дрожит, пока панель растёт или сжимается
    Window _controlsWin = null!;
    Grid _controlsRoot = null!;
    Border _spacer = null!;             // место под нижние элементы в самой панели (там только фон)
    IntPtr _ctrlHwnd;
    int _panelBottomPx = int.MinValue;
    bool _sized;                        // окно поставлено на место — дальше высоту меняем сами, каждый кадр
    int _leftPx, _widthPx, _topPx, _heightPx; // окно в физических пикселях
    int? _bottomPx;                     // панель задач внизу: низ окна стоит на месте, окно растёт вверх
    bool _settled;                      // первый кадр: всё сразу на своих местах, без анимации
    double _boxH = Box;                 // текущая высота блока с текстом (плавно идёт к нужной)
    double _boxTarget = Box;            // к какой высоте блок шёл в прошлом кадре
    DateTime _holdSince;                // с какого момента блок «ждёт» (между треками), держа прежний размер
    double _lyricsAlpha;  // прозрачность текста песни (плавная смена)
    bool _closing;
    DateTime _lastFrame;

    // Текст
    sealed class LineRow(Border row, Grid grid, TextBlock text, ScaleTransform scale)
    {
        public Border Row { get; } = row;
        public Grid Grid { get; } = grid;         // текст и (у текущей строки) слои свечения под ним
        public TextBlock Text { get; } = text;
        public ScaleTransform Scale { get; } = scale;
        public LinearGradientBrush? Fill { get; set; }
        public double Opacity { get; set; } = 0.3;
        public BlurEffect? EdgeBlur { get; set; }
        /// <summary>Строка перенесена на несколько строчек: у каждой строчки своя заливка и своё свечение.</summary>
        public List<LineFill>? Lines { get; set; }
    }
    /// <summary>Строчка перенесённой строки: её заливка, свечение и доля всей ширины (Start, Width — от 0 до 1).</summary>
    sealed record LineFill(LinearGradientBrush Fill, LinearGradientBrush Glow, double Start, double Width);

    readonly List<LineRow> _rows = new();
    double[] _tops = Array.Empty<double>();
    List<LyricLine>? _builtFor;
    bool _built;
    int _current = int.MinValue;
    int _seekLine = -1;                 // строка, по которой кликнули (пока плеер не подтвердил перемотку)
    DateTime _seekLineUntil;
    LineRow? _currentRow;
    double _listY = double.NaN;
    double _scroll;                     // сдвиг прокрутки колесом, DIP (плюс — к началу песни)
    double _lastBaseY = double.NaN;     // положение списка без прокрутки в прошлом кадре
    DateTime _lastScroll;
    FontFamily _lyricsFont = FontManager.Default;
    FontWeight _lyricsWeight = FontWeights.SemiBold;
    string? _fontKey;
    bool _fontBold;
    bool _accentSung;

    // Свечение пропетого — два слоя, как на панели задач; переезжают к текущей строке
    readonly LinearGradientBrush _glowBrush = OverlayWindow.NewGlowBrush();
    TextBlock _glowNear = null!, _glowFar = null!;
    bool _glowOn;
    Color _sungBase, _sungNow;          // цвет пропетого у текущей строки: исходный и нынешний (выцветающий)
    double _sungFade;                   // насколько текущая строка уже выцвела (0…1)

    Canvas _viewport = null!;
    StackPanel _list = null!;
    readonly TranslateTransform _listMove = new();
    Border _thumb = null!;
    readonly TranslateTransform _thumbMove = new();
    DropShadowEffect _thumbGlow = null!;
    TextBlock _status = null!;
    Grid _box = null!;
    string? _coverHere;                 // обложка, которая сейчас стоит в панели (её отпечаток)

    // Большая обложка: по клику на обложку она вырастает из своего места в квадрат на месте текста,
    // повторный клик — обратно. Живёт в самой панели (маленькая — в слое с кнопками) и на время
    // развёрнутости маленькую прячет
    Canvas _coverOverlay = null!;
    Grid _bigCover = null!;             // слои картинок: новая обложка плавно проявляется поверх старой
    bool _coverExpanded;
    double _coverP;                     // 0 — маленькая, 1 — развёрнута (линейно по времени)
    double _coverE;                     // то же со сглаживанием — им двигаем обложку и гасим текст
    BlurEffect? _lyricsBlur;
    LinearGradientBrush _mask = null!;  // растворение краёв блока с текстом (верхний край — только когда текст прокрутился)
    StackPanel _mainStack = null!;
    const double CoverMorphSec = 0.42;

    // Трек
    string? _trackKey;
    int _coverRequest;
    Border _cover = null!;
    TextBlock _title = null!, _artist = null!;

    // Время и перемотка
    TextBlock _elapsed = null!, _total = null!;
    Grid _progressBar = null!;
    Border _progressFill = null!, _progressAhead = null!;
    readonly TranslateTransform _aheadMove = new();
    Ellipse _knob = null!;
    DropShadowEffect _progressGlow = null!;
    readonly TranslateTransform _knobMove = new();
    string _elapsedText = "", _totalText = "";
    bool _seeking;                      // кнопку мыши держат на полосе
    double _seekFrac;                   // куда перематываем (доля трека)
    double? _heldFrac;                  // перемотали — до ответа плеера показываем новую позицию
    DateTime _heldUntil;

    // Кнопки
    Border _shuffleBtn = null!, _prevBtn = null!, _playBtn = null!, _nextBtn = null!, _repeatBtn = null!;
    TextBlock _shuffleGlyph = null!, _repeatGlyph = null!, _playGlyph = null!;
    Border _shuffleDot = null!, _repeatDot = null!;
    Ellipse _playRing = null!;
    readonly TranslateTransform _buttonsMove = new(); // точная подгонка ряда кнопок под центр кольца (целые пиксели)
    readonly ScaleTransform _playScale = new(1, 1);
    DropShadowEffect _playGlow = null!;
    bool _playHover;
    bool _thumbHover;                   // курсор на полосе прокрутки
    bool _progressHover;                // курсор на полосе перемотки
    Border[] _progressLines = Array.Empty<Border>();
    // Плеер сообщает новое состояние не сразу — до ~2 с показываем то, что нажали
    bool? _wantShuffle, _wantPlaying;
    RepeatMode? _wantRepeat;
    DateTime _wantUntil;

    // Уведомление «Ссылка скопирована»
    Border _toast = null!;
    TextBlock _toastText = null!;
    DispatcherTimer? _toastTimer;

    // Значки Segoe Fluent Icons / Segoe MDL2 Assets
    const string GlyphShuffle = "", GlyphPrev = "", GlyphNext = "";
    const string GlyphPlay = "", GlyphPause = "";
    const string GlyphRepeatAll = "", GlyphRepeatOne = "";
    const string GlyphShare = "", GlyphMusic = "";

    LyricsFlyout(OverlayWindow host)
    {
        _host = host;

        Title = "HaloTaskbarLyrics";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        // Windows 11: обычное окно + системный акрил, скруглённые углы и тень. Windows 10: прозрачное окно + размытие.
        AllowsTransparency = !_win11;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        // Ideal — чтобы строки плавно меняли размер без «дрожания» букв по пикселям
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Left = -32000;
        Top = -32000;

        _fg = _light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF2, 0xF4);
        _dimFg = _light ? Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A) : Color.FromRgb(0xA9, 0xA9, 0xB0);
        _accent = _light ? Theme.Accent : Mix(Theme.Accent, Colors.White, 0.45);
        _hover = _light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        // Пропетые буквы — чуть светлее акцента, чем остальные элементы: на акриле панели они иначе тускловаты
        _sung = _light ? Theme.Accent : Mix(Theme.Accent, Colors.White, 0.58);
        Foreground = Paint(_fg);

        ApplyFont();
        _glowFar = MakeGlowLayer(16);
        _glowNear = MakeGlowLayer(5);
        Content = BuildContent();

        // Слой с нижними элементами: прозрачное окно поверх панели, не забирает фокус (панель остаётся активной)
        _controlsWin = new Window
        {
            Title = "HaloTaskbarLyrics",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            UseLayoutRounding = true,
            Width = PanelWidth,
            SizeToContent = SizeToContent.Height,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Foreground,
            Left = -32000,
            Top = -32000,
            Content = _controlsRoot,
        };
        TextOptions.SetTextFormattingMode(_controlsWin, TextFormattingMode.Ideal);
        _controlsWin.SourceInitialized += (_, _) =>
        {
            _ctrlHwnd = new WindowInteropHelper(_controlsWin).Handle;
            Native.MakeToolWindow(_ctrlHwnd); // не в Alt+Tab и не забирает фокус у панели
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            _hwnd = h;
            HwndSource.FromHwnd(h)?.AddHook(WndProc);
            if (_win11)
            {
                if (HwndSource.FromHwnd(h) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
                Native.ApplyWin11Backdrop(h, dark: !_light, Tint);
            }
        };
        // Windows 10: размытие обрезается по скруглённому прямоугольнику — пересчитываем при смене размера
        SizeChanged += (_, _) =>
        {
            if (_win11 || _hwnd == IntPtr.Zero) return;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Native.EnableBlur(_hwnd, Tint, (int)Math.Round(12 * scale));
        };

        // Закрываемся по клику мимо: когда не активна ни сама панель, ни её слой с кнопками
        // (клик по кнопке может сделать активным слой — это не «мимо»)
        void CloseIfOutside() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsActive && !_controlsWin.IsActive) CloseFlyout();
        }));
        Deactivated += (_, _) => CloseIfOutside();
        _controlsWin.Deactivated += (_, _) => CloseIfOutside();

        KeyEventHandler keys = (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseFlyout(); e.Handled = true; }
            else if (e.Key == Key.Space) { PlayPause(); e.Handled = true; }
        };
        PreviewKeyDown += keys;
        _controlsWin.PreviewKeyDown += keys;
        Closed += (_, _) =>
        {
            _closing = true;
            CompositionTarget.Rendering -= OnFrame;
            _toastTimer?.Stop();
            try { _controlsWin.Close(); } catch { }
            if (_open == this) _open = null;
            _closedAt = DateTime.Now;
        };

        CompositionTarget.Rendering += OnFrame;
    }

    Color Tint => _light ? Color.FromArgb(0xC8, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xC8, 0x22, 0x22, 0x26);

    void CloseFlyout()
    {
        if (_closing) return;
        _closing = true;
        _closedAt = DateTime.Now;
        Close();
    }

    // ---------------- Построение ----------------

    FrameworkElement BuildContent()
    {
        _bigCover = new Grid { Cursor = Cursors.Hand, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        _bigCover.MouseLeftButtonUp += (_, e) => { ToggleCover(); e.Handled = true; };
        _coverOverlay = new Canvas();
        _coverOverlay.Children.Add(_bigCover);

        var stack = _mainStack = new StackPanel { Margin = new Thickness(12, 12, 12, 0) };
        stack.Children.Add(BuildLyricsBox());
        _spacer = new Border(); // высоту задаём по нижним элементам (ShowNearRing)
        stack.Children.Add(_spacer);

        // Нижние элементы — отдельный слой (своё прозрачное окно). Почти прозрачная заливка —
        // чтобы клики между кнопками не проходили насквозь
        var controls = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        controls.Children.Add(BuildTrackRow());
        controls.Children.Add(BuildProgress());
        controls.Children.Add(BuildButtons());
        _controlsRoot = new Grid { Width = PanelWidth, Background = Paint(Color.FromArgb(0x01, 0, 0, 0)) };
        _controlsRoot.Children.Add(controls);

        // Рамка панели (Windows 10; в Windows 11 рамку, углы и тень рисует сама система).
        // Почти прозрачная заливка — чтобы клики по пустым местам панели не проходили насквозь
        var frame = new Border { Background = Paint(Color.FromArgb(0x01, 0, 0, 0)) };
        if (!_win11)
        {
            frame.CornerRadius = new CornerRadius(12);
            frame.BorderThickness = new Thickness(1);
            frame.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
        }

        _panelGrid = new Grid
        {
            Width = PanelWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            // Прижата к низу окна: кнопки и обложка стоят на месте, пока блок с текстом растёт или сжимается
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _panelGrid.Children.Add(frame);
        _panelGrid.Children.Add(stack);
        _panelGrid.Children.Add(_coverOverlay);
        _panelGrid.Children.Add(BuildToast());

        // Лёгкое появление: панель чуть поднимается и проявляется
        var move = new TranslateTransform(0, 8);
        _panelGrid.RenderTransform = move;
        _panelGrid.Opacity = 0;
        var moveControls = new TranslateTransform(0, 8);
        _controlsRoot.RenderTransform = moveControls;
        _controlsRoot.Opacity = 0;
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var el in new UIElement[] { _panelGrid, _controlsRoot })
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            foreach (var t in new[] { move, moveControls })
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        };

        var root = new Grid();
        root.Children.Add(_panelGrid);
        return root;
    }

    FrameworkElement BuildLyricsBox()
    {
        _list = new StackPanel { Width = Box, RenderTransform = _listMove };
        _viewport = new Canvas { Width = Box, Height = Box, ClipToBounds = true, Background = Brushes.Transparent };
        _viewport.Children.Add(_list);
        // Края сверху и снизу растворяются
        double f = FadeEdge / Box;
        // Absolute: маска считается от самой области (0…Box), а не от всего списка строк, который выходит
        // далеко за её пределы, — иначе края не растворялись и была видна линия обрезки текста
        _viewport.OpacityMask = _mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, Box),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, f),
                new GradientStop(Colors.Black, 1 - f),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        _status = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 18, 28, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Foreground = Paint(_dimFg),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        // Полоса прокрутки — как линия кольца-визуализатора: та же толщина 1,5, цвет акцента, то же свечение,
        // так же дышит под музыку. Видна, только пока крутят колесо
        _thumbGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.8, Color = _accent, RenderingBias = RenderingBias.Performance };
        _thumb = new Border
        {
            Width = ThumbWidth,
            CornerRadius = new CornerRadius(ThumbWidth / 2),
            Background = Paint(_accent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 2, 0),
            Effect = _thumbGlow,
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = _thumbMove,
        };

        // Без рамки и фона: текст лежит прямо на панели. Нет текста — блок сжимается до надписи
        _box = new Grid { Width = Box, Height = Box, Background = Brushes.Transparent, ClipToBounds = true };
        _box.Children.Add(_viewport);
        _box.Children.Add(_status);
        _box.Children.Add(_thumb);
        _box.MouseWheel += OnWheel;
        // Наведение на полосу прокрутки (у правого края, пока она видна) — она ярче, как кольцо кнопки play
        _box.MouseMove += (_, e) => _thumbHover = _thumb.Opacity > 0.05 && e.GetPosition(_box).X >= Box - 16;
        _box.MouseLeave += (_, _) => _thumbHover = false;
        return _box;
    }

    FrameworkElement BuildTrackRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _cover = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand };
        _cover.MouseLeftButtonUp += (_, e) => { ToggleCover(); e.Handled = true; };
        SetCoverPlaceholder();

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        _title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Paint(_fg) };
        _artist = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Paint(_dimFg), Margin = new Thickness(0, 1, 0, 0) };
        texts.Children.Add(_title);
        texts.Children.Add(_artist);

        // Значок «Поделиться» тусклее остальных, при наведении — обычной яркости
        var shareGlyph = Glyph(GlyphShare, 16);
        shareGlyph.Foreground = Paint(_dimFg);
        var share = MakeButton(shareGlyph, Share, L.S("share"));
        share.MouseEnter += (_, _) => shareGlyph.Foreground = Paint(_fg);
        share.MouseLeave += (_, _) => shareGlyph.Foreground = Paint(_dimFg);
        share.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(texts, 1);
        Grid.SetColumn(share, 2);
        grid.Children.Add(_cover);
        grid.Children.Add(texts);
        grid.Children.Add(share);
        return grid;
    }

    FrameworkElement BuildProgress()
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        // Время стоит посередине между краем панели и полосой: отрицательные отступы расширяют место
        // надписи на поле панели (12) и отступ полосы (6), а текст — по центру этого места
        _elapsed = new TextBlock
        {
            FontSize = 11, Foreground = Paint(_dimFg), VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(-12, 0, -6, 0),
        };
        _total = new TextBlock
        {
            FontSize = 11, Foreground = Paint(_dimFg), VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(-6, 0, -12, 0),
        };

        // Полоса перемотки — как полоса прокрутки и линия кольца: толщина 1,5, цвет акцента, свечение, дышит под музыку
        Border Bar(Color c) => new()
        {
            Height = ThumbWidth,
            CornerRadius = new CornerRadius(ThumbWidth / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Paint(c),
        };
        var track = Bar(Color.FromArgb(0x24, _fg.R, _fg.G, _fg.B));
        track.HorizontalAlignment = HorizontalAlignment.Stretch;
        _progressFill = Bar(_accent);
        _progressFill.Width = 0;
        _progressGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.8, Color = _accent, RenderingBias = RenderingBias.Performance };
        _progressFill.Effect = _progressGlow;
        // Отрезок, который перематываем, — тусклым акцентом
        _progressAhead = Bar(Color.FromArgb(0x66, _accent.R, _accent.G, _accent.B));
        _progressAhead.Width = 0;
        _progressAhead.RenderTransform = _aheadMove;
        _progressLines = new[] { track, _progressFill, _progressAhead };

        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            Fill = Paint(_accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = _knobMove,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6, Color = _accent, RenderingBias = RenderingBias.Performance },
        };

        // Полоса выше, чем видно (16 DIP), — чтобы по ней было легко попасть мышью
        _progressBar = new Grid { Height = 16, Background = Brushes.Transparent, Margin = new Thickness(6, 0, 6, 0) };
        _progressBar.Children.Add(track);
        _progressBar.Children.Add(_progressAhead);
        _progressBar.Children.Add(_progressFill);
        _progressBar.Children.Add(_knob);

        // Нажали — появляется кружок, его можно тащить; отпустили — перемотка
        _progressBar.MouseLeftButtonDown += (_, e) =>
        {
            if (!CanSeekNow()) return;
            _seeking = true;
            _seekFrac = FracAt(e);
            _progressBar.CaptureMouse();
            e.Handled = true;
        };
        _progressBar.MouseMove += (_, e) => { if (_seeking) _seekFrac = FracAt(e); };
        _progressBar.MouseLeftButtonUp += (_, e) =>
        {
            if (!_seeking) return;
            _seekFrac = FracAt(e);
            _seeking = false;
            _progressBar.ReleaseMouseCapture();
            _heldFrac = _seekFrac;
            _heldUntil = DateTime.Now.AddSeconds(2.5);
            SeekTo(_seekFrac);
            e.Handled = true;
        };
        _progressBar.LostMouseCapture += (_, _) => _seeking = false;
        // Наведение — полоса ярче, сильнее светится и чуть толще (как полоса прокрутки и кольцо кнопки play)
        _progressBar.MouseEnter += (_, _) => _progressHover = true;
        _progressBar.MouseLeave += (_, _) => _progressHover = false;

        Grid.SetColumn(_progressBar, 1);
        Grid.SetColumn(_total, 2);
        grid.Children.Add(_elapsed);
        grid.Children.Add(_progressBar);
        grid.Children.Add(_total);
        return grid;
    }

    double FracAt(MouseEventArgs e) => Math.Clamp(e.GetPosition(_progressBar).X / Math.Max(1, _progressBar.ActualWidth), 0, 1);

    bool CanSeekNow() => _host.CurrentSnapshot is { HasTimeline: true } s && s.Controls.CanSeek && s.Track.Duration > TimeSpan.Zero;

    FrameworkElement BuildButtons()
    {
        // Пять мест одинаковой ширины по краям: play всегда ровно по центру панели (под кольцом),
        // даже если плеер не умеет shuffle/repeat (их место тогда просто пустое)
        var row = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), RenderTransform = _buttonsMove };
        foreach (var w in new[] { 38.0, 38.0, 52.0, 38.0, 38.0 })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        (_shuffleBtn, _shuffleGlyph, _shuffleDot) = MakeToggle(GlyphShuffle, ToggleShuffle, L.S("shuffle"));
        _prevBtn = MakeButton(Glyph(GlyphPrev, 16), () => Command(m => m.PreviousAsync()), L.S("previous"));
        _nextBtn = MakeButton(Glyph(GlyphNext, 16), () => Command(m => m.NextAsync()), L.S("next"));
        (_repeatBtn, _repeatGlyph, _repeatDot) = MakeToggle(GlyphRepeatAll, CycleRepeat, L.S("repeatOff"));

        // Play/pause — кольцо, как визуализатор на панели задач: цвет акцента, лёгкое свечение, дышит под музыку
        _playGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.8, Color = _accent, RenderingBias = RenderingBias.Performance };
        _playRing = new Ellipse
        {
            Width = 32,
            Height = 32,
            StrokeThickness = 1.5,
            Stroke = Paint(_accent),
            Effect = _playGlow,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _playScale,
            IsHitTestVisible = false,
        };
        _playGlyph = Glyph(GlyphPlay, 13);
        _playGlyph.Foreground = Paint(_accent);
        var content = new Grid();
        content.Children.Add(_playRing);
        content.Children.Add(_playGlyph);
        _playBtn = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(6, 0, 6, 0),
            Background = Brushes.Transparent,
            Child = content,
            ToolTip = L.S("play"),
        };
        _playBtn.MouseEnter += (_, _) => _playHover = true;
        _playBtn.MouseLeave += (_, _) => _playHover = false;
        _playBtn.MouseLeftButtonUp += (_, e) => { PlayPause(); e.Handled = true; };
        AddPress(_playBtn, background: false);

        int col = 0;
        foreach (var b in new[] { _shuffleBtn, _prevBtn, _playBtn, _nextBtn, _repeatBtn })
        {
            b.Margin = new Thickness(0);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(b, col++);
            row.Children.Add(b);
        }
        return row;
    }

    FrameworkElement BuildToast()
    {
        _toastText = new TextBlock { FontSize = 12, Foreground = Paint(_fg), TextTrimming = TextTrimming.CharacterEllipsis };
        _toast = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 150),
            MaxWidth = 340,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Paint(_light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Background = Paint(_light ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xEB, 0x14, 0x14, 0x18)),
            Child = _toastText,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        return _toast;
    }

    TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Paint(_fg),
    };

    /// <summary>Кнопка 32×32 с подсветкой при наведении.</summary>
    Border MakeButton(UIElement child, Action action, string tooltip)
    {
        var b = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Child = child,
            ToolTip = tooltip,
        };
        b.MouseEnter += (_, _) => b.Background = Paint(_hover);
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        AddPress(b);
        return b;
    }

    /// <summary>
    /// Эффект «прожатия»: пока кнопку мыши держат, кнопка чуть уменьшается (и темнеет фон),
    /// отпустили или увели мышь — мягко пружинит обратно.
    /// </summary>
    void AddPress(Border b, bool background = true)
    {
        var st = new ScaleTransform(1, 1);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = st;
        var pressedBg = Paint(_light ? Color.FromArgb(0x1F, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        void To(double v, double ms, IEasingFunction ease)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }
        void Release() => To(1, 240, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        b.PreviewMouseLeftButtonDown += (_, _) =>
        {
            To(0.86, 70, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            if (background) b.Background = pressedBg;
        };
        b.PreviewMouseLeftButtonUp += (_, _) =>
        {
            Release();
            if (background) b.Background = b.IsMouseOver ? Paint(_hover) : Brushes.Transparent;
        };
        b.MouseLeave += (_, _) => Release();
    }

    /// <summary>Кнопка-переключатель: включённая — цвет акцента и точка снизу.</summary>
    (Border, TextBlock, Border) MakeToggle(string glyph, Action action, string tooltip)
    {
        var icon = Glyph(glyph, 16);
        var dot = new Border
        {
            Width = 3,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            Background = Paint(_accent),
            Opacity = 0,
        };
        var grid = new Grid();
        grid.Children.Add(icon);
        grid.Children.Add(dot);
        return (MakeButton(grid, action, tooltip), icon, dot);
    }

    TextBlock MakeGlowLayer(double radius) => new()
    {
        Foreground = _glowBrush,
        Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        Opacity = 0,
        IsHitTestVisible = false,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------- Каждый кадр ----------------

    void OnFrame(object? sender, EventArgs e)
    {
        if (_closing) return;
        var now = DateTime.Now;
        double dt = _lastFrame == default ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        if (now > _wantUntil) { _wantShuffle = null; _wantPlaying = null; _wantRepeat = null; }

        try
        {
            var snap = _host.CurrentSnapshot;
            var prefs = _host.Prefs;
            if (prefs.FontFamily != _fontKey || prefs.Bold != _fontBold || prefs.AccentSung != _accentSung)
            {
                ApplyFont();
                _built = false; // перестроить строки новым шрифтом
            }

            UpdateTrack(_host.CurrentTrack);
            // Сменился текст (другой трек): старый сначала растворяется, потом появляется новый
            var lines = _host.CurrentLines;
            bool swap = !_built || !ReferenceEquals(lines, _builtFor);
            if (swap && (_rows.Count == 0 || _lyricsAlpha < 0.02)) { BuildRows(lines); swap = false; }
            AnimateCover(dt);
            UpdateLyrics(dt, snap, now, swap);
            UpdateProgress(snap, dt, now);
            UpdateButtons(snap);
            AnimatePlayRing();
            FitWindowHeight();
            _settled = true;
        }
        catch (Exception ex)
        {
            App.Log("Flyout: " + ex);
            CloseFlyout();
        }
    }

    void ApplyFont()
    {
        var prefs = _host.Prefs;
        _fontKey = prefs.FontFamily;
        _fontBold = prefs.Bold;
        _accentSung = prefs.AccentSung;
        _lyricsFont = FontManager.Resolve(prefs.FontFamily);
        _lyricsWeight = FontManager.BestWeight(_lyricsFont, prefs.Bold);
    }

    // ---------------- Текст ----------------

    void BuildRows(List<LyricLine>? lines)
    {
        DetachGlow();
        _built = true;
        _builtFor = lines;
        _list.Children.Clear();
        _rows.Clear();
        _current = int.MinValue;
        _currentRow = null;
        _seekLine = -1;
        _scroll = 0;
        _lastBaseY = double.NaN;
        _listY = double.NaN;
        if (lines == null) return;

        foreach (var line in lines)
        {
            var scale = new ScaleTransform(Small, Small);
            // Длинная строка переносится на следующую строчку (по центру), а не обрезается
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontFamily = _lyricsFont,
                FontWeight = _lyricsWeight,
                FontSize = CurrentSize,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = TextMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Paint(_fg),
            };
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            grid.Children.Add(tb);
            var row = new Border { MinHeight = LineHeight, Padding = new Thickness(0, 5, 0, 5), Child = grid, Opacity = 0.3 };
            // Клик по строке — перемотка на её начало. Прозрачный фон — чтобы клик ловился по всей ширине строки
            int index = _rows.Count;
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) => { SeekToLine(index); e.Handled = true; };
            _list.Children.Add(row);
            _rows.Add(new LineRow(row, grid, tb, scale));
        }
    }

    double RowHeight(int i) => _rows[i].Row.ActualHeight > 0 ? _rows[i].Row.ActualHeight : LineHeight;

    static double Approach(double from, double to, double k) => Math.Abs(to - from) < 0.002 ? to : from + (to - from) * k;

    void UpdateLyrics(double dt, MediaSnapshot? snap, DateTime now, bool swapping)
    {
        bool has = _rows.Count > 0 && _builtFor != null && snap is { HasTimeline: true };
        double fade = _settled ? 1 - Math.Exp(-dt / 0.09) : 1;

        // Надписей о состоянии («текст не найден», «ищу текст…») в панели нет: нет текста — блок просто сжимается
        if (_status.Opacity != 0) _status.Opacity = 0;
        double hide = 1 - _coverE; // развёрнутая обложка закрывает текст — он тает

        // Текст песни: при смене трека старый растворяется, новый проявляется
        _lyricsAlpha = Approach(_lyricsAlpha, has && !swapping ? 1 : 0, fade);
        if (_viewport.Opacity != _lyricsAlpha * hide) _viewport.Opacity = _lyricsAlpha * hide;

        // Высота блока плавно меняется: квадрат, когда есть текст (или развёрнута обложка), иначе блок исчезает
        // совсем — остаются обложка, название, полоса и кнопки
        double boxTarget = has || swapping || _coverExpanded || _coverE > 0 ? Box : 0;
        // Между треками блок держит прежний размер, а не сворачивается и разворачивается:
        // • браузер на полсекунды-секунду вообще не сообщает, что что-то играет (трека «нет»);
        // • текст для нового трека ещё ищется (даже из кэша — доли секунды);
        // • плеер ещё не сообщил позицию нового трека.
        // Если так держится дольше 3 с (музыку выключили, текст не нашёлся) — тогда уже сворачивается
        bool waiting = !has && (_host.CurrentTrack == null || _host.LyricsStatusKey == "searching" || snap is not { HasTimeline: true });
        if (waiting)
        {
            if (_holdSince == default) _holdSince = now;
            if ((now - _holdSince).TotalSeconds < 3) boxTarget = _boxTarget;
        }
        else _holdSince = default;
        _boxTarget = boxTarget;
        _boxH = Approach(_boxH, boxTarget, _settled ? 1 - Math.Exp(-dt / 0.12) : 1);
        if (Math.Abs(_box.Height - _boxH) > 0.05) _box.Height = _boxH;
        // Отступ над блоком сжимается вместе с ним: без текста сверху такой же отступ, как по бокам
        double topGap = Math.Min(12, _boxH);
        if (Math.Abs(_mainStack.Margin.Top - topGap) > 0.05) _mainStack.Margin = new Thickness(12, topGap, 12, 0);

        if (!has)
        {
            _thumb.Opacity = 0;
            return;
        }

        var lines = _builtFor!;
        var pos = _host.PlaybackPosition;
        // Как и на панели задач: следующая строка становится текущей чуть раньше своего времени
        int idx = LrcParser.IndexAt(lines, pos + TimeSpan.FromMilliseconds(_host.Prefs.LineLeadMs));
        // Кликнули по строке — она сразу текущая, не дожидаясь, пока плеер сообщит новую позицию
        // (иначе список сначала дёргался к старой строке, а потом ехал к новой)
        if (_seekLine >= 0)
        {
            if (idx == _seekLine || now > _seekLineUntil || _seekLine >= _rows.Count) _seekLine = -1;
            else idx = _seekLine;
        }
        bool pending = _seekLine >= 0;
        if (idx != _current) SetCurrent(idx);
        int focus = Math.Max(0, idx);
        int n = _rows.Count;

        // Где какая строка по высоте (длинные строки выше обычных)
        if (_tops.Length != n + 1) _tops = new double[n + 1];
        for (int i = 0; i < n; i++) _tops[i + 1] = _tops[i] + RowHeight(i);
        double Center(int i) => _tops[i] + RowHeight(i) / 2;

        // Прокрутили колесом — через несколько секунд тишины возвращаемся к текущей строке
        double returnSec = Math.Clamp(_host.Prefs.FlyoutReturnSec, 0.5, 30);
        if (_thumbHover && _scroll != 0) _lastScroll = now; // пока курсор на полосе — не прячем и не возвращаемся
        bool scrolling = _scroll != 0 && (now - _lastScroll).TotalSeconds < returnSec;
        if (!scrolling) _scroll = 0;

        // В начале трека текст стоит от самого верха блока, и первые строки закрашиваются на месте.
        // Когда текущей становится третья строка, текст начинает прокручиваться: текущая строка
        // дальше всегда там, где была третья, а остальные уезжают вверх
        int slot = Math.Min(ActiveSlot, n - 1);
        double baseY = focus <= slot ? 0 : Center(slot) - Center(focus);
        double minY = Math.Min(0, Center(slot) - Center(n - 1)); // дальше последней строки не прокручиваем
        // Пока текст прокручен колесом, он стоит на месте: когда песня переходит к следующей строке,
        // прокрутку поправляем ровно на столько, на сколько сдвинулась бы строка
        if (scrolling && !double.IsNaN(_lastBaseY)) _scroll += _lastBaseY - baseY;
        _lastBaseY = baseY;
        _scroll = Math.Clamp(_scroll, minY - baseY, -baseY);
        double target = baseY + _scroll;
        if (double.IsNaN(_listY)) _listY = target;
        // Плавный сдвиг (≈0,5 с до места); при прокрутке колесом — быстрее
        _listY += (target - _listY) * (1 - Math.Exp(-dt / (scrolling ? 0.06 : 0.12)));
        if (Math.Abs(target - _listY) < 0.05) _listY = target;
        _listMove.Y = _listY;

        // Верхний край растворяется, только когда текст уехал вверх (в начале трека — чёткий)
        double topFade = Math.Clamp(-_listY / FadeEdge, 0, 1);
        var topColor = Color.FromArgb((byte)Math.Round(255 * (1 - topFade)), 0, 0, 0);
        if (_mask.GradientStops[0].Color != topColor) _mask.GradientStops[0].Color = topColor;

        // Чем дальше строка от центра, тем она тусклее; прошедшие — ещё чуть тусклее
        double view = Center(focus) - _scroll;
        double k = 1 - Math.Exp(-dt / 0.12);
        for (int i = 0; i < n; i++)
        {
            var r = _rows[i];
            double d = Math.Abs(Center(i) - view) / LineHeight;
            double op = i == idx ? 1 : Math.Max(0.16, 0.62 - 0.14 * d) * (i < focus ? 0.85 : 1);
            double next = r.Opacity + (op - r.Opacity) * k;
            if (Math.Abs(next - r.Opacity) > 0.001 || Math.Abs(r.Row.Opacity - next) > 0.001)
            {
                r.Opacity = next;
                r.Row.Opacity = next;
            }

            // Размытие у краёв: чем ближе строка к верхнему или нижнему краю, тем сильнее
            double h = RowHeight(i);
            double c = _listY + _tops[i] + h / 2;
            // Линейно: на границе зоны — 0, у самого края — максимум. Верхний край — только когда текст
            // уже прокрутился (topFade), иначе первая строка в начале трека была бы размыта
            double blur = c < -h || c > Box + h ? 0
                : Math.Max(EdgeBlurMax * Math.Clamp(1 - c / EdgeBlurZone, 0, 1) * topFade,
                           EdgeBlurMax * Math.Clamp(1 - (Box - c) / EdgeBlurZone, 0, 1));
            SetEdgeBlur(r, blur);
        }

        // Заливка пропетого и свечение текущей строки
        if (idx >= 0 && idx < n && (_rows[idx].Fill != null || _rows[idx].Lines != null))
        {
            var cur = _rows[idx];
            double p = pending ? 0 : _host.LineFill(idx, pos);

            // Строка допета, и прошло время — цвет пропетого медленно выцветает (перемотали назад — возвращается)
            double since = pending ? -1 : (pos - _host.LineEndAt(idx)).TotalSeconds - SungFadeDelay;
            double ft = Math.Clamp(since / SungFadeSec, 0, 1);
            ft = ft * ft * (3 - 2 * ft); // мягко в начале и в конце
            if (Math.Abs(ft - _sungFade) > 0.002)
            {
                bool started = _sungFade <= 0 && ft > 0, back = _sungFade > 0 && ft <= 0;
                _sungFade = ft;
                _sungNow = Mix(_sungBase, _fg, ft);
                // Выцветание идёт слева направо (у перенесённой строки — строчка за строчкой)
                if (cur.Lines is { } fadeParts)
                    foreach (var part in fadeParts)
                        SetFade(part.Fill, part.Width <= 0 ? (ft >= part.Start ? 1 : 0) : Math.Clamp((ft - part.Start) / part.Width, 0, 1));
                else if (cur.Fill is { } fadeFill) SetFade(fadeFill, ft);
                if (started) FadeGlow(SungFadeSec * (1 - ft));
                if (back) ShowGlow();
            }
            bool glow = _glowOn && _host.Prefs.Glow;
            double lvl = glow ? _host.GlowLevel : 0;
            if (glow)
            {
                if (_glowFar.Effect is BlurEffect far) far.Radius = 11 + 13 * lvl;
                if (_glowNear.Effect is BlurEffect near) near.Radius = 4 + 4 * lvl;
            }
            if (cur.Lines is { } parts)
            {
                // Перенесённая строка: строчки закрашиваются по очереди
                foreach (var part in parts)
                {
                    double q = part.Width <= 0 ? (p >= part.Start ? 1 : 0) : Math.Clamp((p - part.Start) / part.Width, 0, 1);
                    SetFill(part.Fill, q);
                    if (glow) _host.PaintGlow(part.Glow, q, lvl);
                }
            }
            else if (cur.Fill is { } fill)
            {
                SetFill(fill, p);
                if (glow) _host.PaintGlow(_glowBrush, p, lvl);
            }
        }

        // Полоса прокрутки
        double contentH = _tops[n];
        double thumbH = Math.Max(28, Box * Box / (contentH + Box));
        double span = Math.Max(1, Center(n - 1) - Center(0));
        double pp = (view - Center(0)) / span;
        if (double.IsNaN(_thumb.Height) || Math.Abs(_thumb.Height - thumbH) > 0.5) _thumb.Height = thumbH;
        _thumbMove.Y = 6 + Math.Clamp(pp, 0, 1) * (Box - 12 - thumbH);
        double level = _host.MusicLevel;
        double to = scrolling ? Math.Min(1, 0.45 + 0.40 * level + (_thumbHover ? 0.25 : 0)) : 0; // как у кольца на панели задач
        if (_coverE > 0) to = 0;
        _thumb.Opacity += (to - _thumb.Opacity) * (1 - Math.Exp(-dt / 0.1));
        _thumbGlow.BlurRadius = 4 + 6 * level + (_thumbHover ? 3 : 0);
        double tw = _thumbHover ? 2.5 : ThumbWidth; // при наведении чуть толще
        if (_thumb.Width != tw) { _thumb.Width = tw; _thumb.CornerRadius = new CornerRadius(tw / 2); }
    }

    static void SetEdgeBlur(LineRow r, double radius)
    {
        if (radius < 0.3)
        {
            if (r.Row.Effect != null) r.Row.Effect = null;
            return;
        }
        r.EdgeBlur ??= new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        if (Math.Abs(r.EdgeBlur.Radius - radius) > 0.1) r.EdgeBlur.Radius = radius;
        if (r.Row.Effect != r.EdgeBlur) r.Row.Effect = r.EdgeBlur;
    }

    void SetCurrent(int idx)
    {
        if (_currentRow != null) MakeNormal(_currentRow);
        _currentRow = null;
        _current = idx;
        if (idx >= 0 && idx < _rows.Count) MakeCurrent(_rows[idx]);
    }

    /// <summary>
    /// Строка становится текущей — как новая строка на панели задач: буквы всплывают по одной под углом,
    /// строка размыта и проясняется, потом проявляется свечение пропетого.
    /// </summary>
    void MakeCurrent(LineRow r)
    {
        _currentRow = r;
        var sung = _accentSung ? _sung : _fg;
        _sungBase = _sungNow = sung;
        _sungFade = 0;
        var dim = Color.FromArgb(0x61, _fg.R, _fg.G, _fg.B);
        var brush = FillBrush(sung, dim);
        r.Fill = brush;
        r.Lines = null;
        var tb = r.Text;
        tb.Foreground = brush;
        AttachGlow(r);

        bool letters = _host.Prefs.LetterFx;
        var blur = new BlurEffect
        {
            Radius = letters ? OverlayWindow.LetterBlur : BlurMax,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
        tb.Effect = blur;
        if (letters)
        {
            try { _host.AnimateLetters(tb, dim); }
            catch (Exception ex) { App.Log("Letters: " + ex.Message); tb.TextEffects = null; }
        }
        else tb.TextEffects = null;
        SplitFill(r, sung, dim);

        var clear = letters
            ? new DoubleAnimation(0, OverlayWindow.LetterBlurDuration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(0, InDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        clear.Completed += (_, _) =>
        {
            if (tb.Effect != blur) return; // строка уже сменилась
            tb.Effect = null;
            if (_currentRow == r) ShowGlow();
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, clear);
        AnimateScale(r, 1, InDuration, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>Строка перестала быть текущей: свечение гаснет, строка становится обычной и чуть меньше.</summary>
    void MakeNormal(LineRow r)
    {
        HideGlow();
        // Строка уже прошла: пропетое не гаснет сразу, а медленно выцветает слева направо —
        // продолжая с того места, до которого успело выцвести, пока строка была текущей
        if (_sungBase == _fg || (r.Fill == null && r.Lines == null))
        {
            r.Fill = null;
            r.Lines = null;
            r.Text.TextEffects = null;
            r.Text.Foreground = Paint(_fg);
        }
        else
        {
            double ft = _sungFade;
            double total = SungFadeSec * (1 - ft);               // сколько ещё выцветать
            double delay = ft > 0 ? 0 : SungFadeDelay;
            if (r.Lines is { } parts)
            {
                foreach (var part in parts)
                {
                    SetFill(part.Fill, 1); // строка допета целиком
                    double w = Math.Max(1e-6, part.Width);
                    double q0 = Math.Clamp((ft - part.Start) / w, 0, 1);
                    // Когда край выцветания дойдёт до начала этой строчки и до её конца
                    double t0 = Math.Max(0, (part.Start - ft) / Math.Max(1e-6, 1 - ft)) * total;
                    double t1 = Math.Max(t0, (part.Start + part.Width - ft) / Math.Max(1e-6, 1 - ft) * total);
                    AnimateFade(part.Fill, q0, t1 - t0, delay + t0);
                }
            }
            else if (r.Fill is { } fill)
            {
                SetFill(fill, 1);
                AnimateFade(fill, ft, total, delay);
            }
        }
        // Короткое лёгкое размытие при уходе — как уход строки на панели задач, но строка остаётся в списке
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        r.Text.Effect = blur;
        var pulse = new DoubleAnimation(0, 5, TimeSpan.FromMilliseconds(OutDuration.TotalMilliseconds / 2))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        pulse.Completed += (_, _) => { if (r.Text.Effect == blur) r.Text.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, pulse);
        AnimateScale(r, Small, OutDuration, new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    static void AnimateScale(LineRow r, double to, TimeSpan dur, IEasingFunction ease)
    {
        r.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
        r.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
    }

    // Кисть строки: [выцвело][мягкий край][пропето][мягкий край][ещё не пропето].
    // Сначала слева направо идёт заливка пропетого, потом — так же слева направо — выцветание
    const double FadeEdge2 = 0.35; // ширина мягкого края выцветания — доля строки

    static void SetFill(LinearGradientBrush b, double p)
    {
        const double edge = 0.06;
        double x = -edge + p * (1 + edge);
        b.GradientStops[3].Offset = Math.Clamp(x, 0, 1);
        b.GradientStops[4].Offset = Math.Clamp(x + edge, 0, 1);
    }

    /// <summary>Выцветание пропетого: q — какая доля строки слева уже выцвела.</summary>
    static void SetFade(LinearGradientBrush b, double q)
    {
        double y = -FadeEdge2 + q * (1 + FadeEdge2);
        b.GradientStops[1].Offset = y;
        b.GradientStops[2].Offset = y + FadeEdge2;
    }

    /// <summary>Плавное выцветание прошедшей строки: край выцветания доезжает до конца за seconds.</summary>
    static void AnimateFade(LinearGradientBrush b, double fromQ, double seconds, double delay)
    {
        double y0 = -FadeEdge2 + fromQ * (1 + FadeEdge2), y1 = 1;
        var dur = TimeSpan.FromSeconds(Math.Max(0.05, seconds));
        var begin = TimeSpan.FromSeconds(Math.Max(0, delay));
        b.GradientStops[1].BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(y0, y1, dur) { BeginTime = begin });
        b.GradientStops[2].BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(y0 + FadeEdge2, y1 + FadeEdge2, dur) { BeginTime = begin });
    }

    // ---- Свечение: те же два слоя, что у строки на панели задач ----

    /// <summary>Заливка пропетого: [пропето][мягкий край][ещё не пропето] — как на панели задач.</summary>
    LinearGradientBrush FillBrush(Color sung, Color dim) => new()
    {
        StartPoint = new Point(0, 0.5),
        EndPoint = new Point(1, 0.5),
        GradientStops =
        {
            new GradientStop(_fg, -1),                 // выцветшее (обычный цвет текста)
            new GradientStop(_fg, -FadeEdge2),         // край выцветания — пока за левым краем строки
            new GradientStop(sung, 0),
            new GradientStop(sung, 0),                 // край заливки пропетого
            new GradientStop(dim, 0),
            new GradientStop(dim, 1),
        },
    };

    /// <summary>
    /// Строка перенесена на несколько строчек — у каждой строчки своя заливка, и они закрашиваются
    /// по очереди: вторая начинает, когда закрасится первая. Ширина строчки — её доля во времени строки.
    /// Заливка задаётся «эффектами текста» на диапазон букв строчки (кисть растягивается по этой строчке).
    /// </summary>
    void SplitFill(LineRow r, Color sung, Color dim)
    {
        var tb = r.Text;
        List<(int Start, int Length, double Width)> parts;
        try { parts = VisualLines(tb); }
        catch (Exception ex) { App.Log("Wrap: " + ex.Message); return; }
        if (parts.Count < 2) return;

        double total = parts.Sum(p => p.Width);
        if (total <= 0) return;
        tb.Foreground = Paint(dim); // пробелы на месте переноса
        var effects = tb.TextEffects ?? new TextEffectCollection();
        tb.TextEffects = effects;
        var farFx = new TextEffectCollection();
        var nearFx = new TextEffectCollection();
        var lines = new List<LineFill>();
        double cum = 0;
        int k = 0;
        foreach (var (start, length, width) in parts)
        {
            var fill = FillBrush(sung, dim);
            var glow = OverlayWindow.NewGlowBrush();
            // В начало списка: пока буквы всплывают, их собственный цвет важнее
            effects.Insert(k++, new TextEffect { PositionStart = start, PositionCount = length, Foreground = fill });
            farFx.Add(new TextEffect { PositionStart = start, PositionCount = length, Foreground = glow });
            nearFx.Add(new TextEffect { PositionStart = start, PositionCount = length, Foreground = glow });
            lines.Add(new LineFill(fill, glow, cum / total, width / total));
            cum += width;
        }
        r.Fill = null;
        r.Lines = lines;
        if (_glowFar.Parent != null)
        {
            _glowFar.Foreground = Brushes.Transparent;
            _glowNear.Foreground = Brushes.Transparent;
            _glowFar.TextEffects = farFx;
            _glowNear.TextEffects = nearFx;
        }
    }

    /// <summary>На какие строчки переносится текст (начало, длина в буквах, ширина). Перенос — как у самой строки.</summary>
    List<(int Start, int Length, double Width)> VisualLines(TextBlock tb)
    {
        var result = new List<(int, int, double)>();
        var text = tb.Text;
        if (string.IsNullOrEmpty(text)) return result;
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White, null,
            TextFormattingMode.Ideal, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = tb.MaxWidth,
        };
        int start = 0;
        double top = double.NaN, minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i])) continue;
            int count = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var b = ft.BuildHighlightGeometry(new Point(0, 0), i, count)?.Bounds ?? Rect.Empty;
            if (b.IsEmpty) continue;
            if (double.IsNaN(top)) top = b.Top;
            else if (b.Top > top + tb.FontSize * 0.5) // новая строчка
            {
                result.Add((start, i - start, maxX > minX ? maxX - minX : 0));
                start = i;
                top = b.Top;
                minX = double.PositiveInfinity;
                maxX = double.NegativeInfinity;
            }
            if (!char.IsWhiteSpace(text[i]))
            {
                minX = Math.Min(minX, b.Left);
                maxX = Math.Max(maxX, b.Right);
            }
        }
        result.Add((start, text.Length - start, maxX > minX ? maxX - minX : 0));
        return result;
    }

    void AttachGlow(LineRow r)
    {
        DetachGlow();
        foreach (var g in new[] { _glowFar, _glowNear })
        {
            g.Foreground = _glowBrush;
            g.TextEffects = null;
            g.Text = r.Text.Text;
            g.FontFamily = r.Text.FontFamily;
            g.FontSize = r.Text.FontSize;
            g.FontWeight = r.Text.FontWeight;
            g.MaxWidth = r.Text.MaxWidth;
        }
        // Снизу вверх: дальний ореол, ближнее свечение, сам текст
        r.Grid.Children.Insert(0, _glowNear);
        r.Grid.Children.Insert(0, _glowFar);
    }

    void DetachGlow()
    {
        _glowOn = false;
        foreach (var g in new[] { _glowFar, _glowNear })
        {
            g.BeginAnimation(OpacityProperty, null);
            g.Opacity = 0;
            (g.Parent as Panel)?.Children.Remove(g);
        }
    }

    void ShowGlow()
    {
        if (!_host.Prefs.Glow || _glowFar.Parent == null) return;
        _glowOn = true;
        foreach (var g in new[] { _glowFar, _glowNear })
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(OverlayWindow.GlowOpacity, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Свечение медленно гаснет (строка выцветает).</summary>
    void FadeGlow(double seconds)
    {
        foreach (var g in new[] { _glowFar, _glowNear })
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(Math.Max(0.3, seconds)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    void HideGlow()
    {
        _glowOn = false;
        foreach (var g in new[] { _glowFar, _glowNear })
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_rows.Count == 0 || _coverExpanded) return;
        // Колесо вверх — к началу песни (текст едет вниз)
        _scroll += e.Delta > 0 ? LineHeight : -LineHeight;
        _lastScroll = DateTime.Now;
    }

    string StatusText(MediaSnapshot? snap)
    {
        if (_host.CurrentTrack == null) return L.S("flyoutNothing");
        if (_rows.Count > 0 && snap is { HasTimeline: false }) return Cap(L.S("noTimeline"));
        return Cap(L.S(_host.LyricsStatusKey ?? "searching"));
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], L.Culture) + s[1..];

    // ---------------- Трек ----------------

    void UpdateTrack(TrackInfo? t)
    {
        var key = t == null ? null : t.Key + "\u001f" + t.Album;
        if (key == _trackKey) return;
        _trackKey = key;
        _heldFrac = null;
        _title.Text = t?.Title ?? "";
        // Новое название и исполнитель мягко проявляются
        foreach (var tb in new[] { _title, _artist })
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            });
        var player = t == null ? "" : PlayerName(t.Source);
        var artist = t?.Artist ?? "";
        _artist.Text = artist.Length > 0 && player.Length > 0 ? $"{artist} · {player}" : artist + player;

        // Развёрнутая обложка не мигает заглушкой: старая стоит, пока не найдётся новая (или не выяснится, что её нет)
        SetCoverPlaceholder(toBig: !_coverExpanded);
        _coverHere = null;
        var request = ++_coverRequest;
        if (t != null) LoadCover(request, t);
    }

    /// <summary>
    /// Обложку плеер отдаёт через Windows. Некоторые плееры (например, Spotify) обновляют её с задержкой —
    /// поэтому спрашиваем ещё раз через 1,5 и 4 с. Если у нового трека обложки нет, часть плееров продолжает
    /// отдавать обложку прошлого трека: такую (та же картинка, а альбом другой) не показываем — остаётся заглушка.
    /// </summary>
    async void LoadCover(int request, TrackInfo t)
    {
        try
        {
            bool own = false, web = false;
            string? ownShown = null;         // какая обложка плеера уже учтена (чтобы не менять её снова)
            int[] delays = { 0, 1500, 2500 };
            foreach (var delay in delays)
            {
                if (delay > 0) await Task.Delay(delay);
                if (request != _coverRequest || _closing) return;
                // Обложка чужого трека (плеер не обновил картинку) сюда не попадает — см. MediaWatcher.GetCoverAsync
                var cover = await _host.Media.GetCoverAsync(t);
                if (request != _coverRequest || _closing) return;
                if (cover != null)
                {
                    own = true;
                    if (cover.Hash == ownShown) continue;
                    ownShown = cover.Hash;
                    SetCover(cover.Bytes, cover.Hash);

                    // Плеер дал маленькую картинку (Chrome — 150×150): для большой обложки берём ту же
                    // из Deezer в высоком разрешении, но только если она «на глаз» такая же
                    if (PixelWidthOf(cover.Bytes) < 400)
                    {
                        var hi = await CoverService.FindAsync(t);
                        if (request != _coverRequest || _closing) return;
                        if (hi != null && MediaWatcher.LookOf(cover.Bytes).Look is ulong a && MediaWatcher.LookOf(hi).Look is ulong b)
                        {
                            int diff = MediaWatcher.Distance(a, b);
                            Diag.Write($"Cover \"{t.Title}\": Deezer version differs by {diff} of 64 — {(diff <= 12 ? "used in high resolution" : "different picture, not used")}");
                            if (diff <= 12) SetCover(hi, "hi:" + cover.Hash);
                        }
                    }
                    continue;
                }

                // Своей обложки плеер не дал — ищем в Deezer (один раз). Если потом плеер всё же пришлёт
                // свою обложку, она заменит найденную
                if (!own && !web)
                {
                    web = true;
                    var bytes = await CoverService.FindAsync(t);
                    if (request != _coverRequest || _closing) return;
                    if (!own && bytes != null) SetCover(bytes, "deezer");
                }
            }
        }
        catch (Exception ex) { Diag.Write("Cover: " + ex.Message); }
        // Обложки так и не нашлось — теперь и большая показывает заглушку
        if (request == _coverRequest && !_closing && _coverHere == null) SetBigCover(PlaceholderBrush(), note: true);
    }

    /// <summary>Ставит обложку: миниатюру (96 px) и большую (в полном разрешении картинки, до 1024 px).</summary>
    void SetCover(byte[] bytes, string id)
    {
        if (MakeImage(bytes, 96) is not { } small) return;
        _cover.Background = new ImageBrush(small) { Stretch = Stretch.UniformToFill };
        _cover.Child = null;
        _coverHere = id;
        var big = MakeImage(bytes, 1024) ?? small;
        SetBigCover(new ImageBrush(big) { Stretch = Stretch.UniformToFill }, note: false);
    }

    /// <summary>Ширина картинки в пикселях (0 — не удалось прочитать).</summary>
    static int PixelWidthOf(byte[] bytes)
    {
        try
        {
            return BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
        }
        catch { return 0; }
    }

    /// <summary>Новая картинка большой обложки: ложится сверху и плавно проявляется, старые слои потом убираются.</summary>
    void SetBigCover(Brush background, bool note)
    {
        if (_bigCover == null) return;
        var layer = new Border { Background = background, Opacity = 0 };
        if (note)
        {
            var glyph = Glyph(GlyphMusic, 18);
            glyph.Foreground = Paint(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            glyph.FontSize = 18 * BigScale();
            layer.Child = glyph;
        }
        _bigCover.Children.Add(layer);
        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(_coverE > 0 ? 450 : 1));
        fade.Completed += (_, _) =>
        {
            while (_bigCover.Children.Count > 1 && _bigCover.Children[0] != layer) _bigCover.Children.RemoveAt(0);
        };
        layer.BeginAnimation(OpacityProperty, fade);
    }

    double BigScale() => double.IsNaN(_bigCover.Width) || _bigCover.Width <= 0 ? 1 : _bigCover.Width / 44;

    void ToggleCover()
    {
        _coverExpanded = !_coverExpanded;
        if (_coverExpanded) _scroll = 0;
    }

    /// <summary>
    /// Каждый кадр: большая обложка между своим местом у названия (44 px) и квадратом на месте текста.
    /// Плавное ускорение и торможение; при повторном клике на полпути разворачивается без рывка.
    /// </summary>
    void AnimateCover(double dt)
    {
        double target = _coverExpanded ? 1 : 0;
        if (_coverP != target)
        {
            double step = dt / CoverMorphSec;
            _coverP = target > _coverP ? Math.Min(target, _coverP + step) : Math.Max(target, _coverP - step);
        }
        double p = _settled ? _coverP : target;
        _coverE = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

        bool visible = _coverE > 0;
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_bigCover.Visibility != v) _bigCover.Visibility = v;
        double smallOp = visible ? 0 : 1; // пока большая на экране (или летит) — маленькая спрятана
        if (_cover.Opacity != smallOp) _cover.Opacity = smallOp;

        // Текст под обложкой уходит в размытие
        if (_coverE > 0)
        {
            _lyricsBlur ??= new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            _lyricsBlur.Radius = 10 * _coverE;
            if (_viewport.Effect != _lyricsBlur) _viewport.Effect = _lyricsBlur;
        }
        else if (_viewport.Effect != null) _viewport.Effect = null;
        if (!visible) return;

        // Откуда и куда (в координатах панели): место маленькой обложки и квадрат блока с текстом
        // Маленькая обложка: у левого края слоя с кнопками (отступ 12 уже в координатах), на 12 ниже его верха
        var small = _spacer.TranslatePoint(new Point(0, 12), _panelGrid);
        var box = _box.TranslatePoint(new Point(0, 0), _panelGrid);
        double bigH = _box.ActualHeight > 0 ? _box.ActualHeight : Box;
        double x = small.X + (box.X - small.X) * _coverE;
        double y = small.Y + (box.Y - small.Y) * _coverE;
        double w = 44 + (Box - 44) * _coverE;
        double h = 44 + (bigH - 44) * _coverE;
        double r = 6 + 4 * _coverE;
        Canvas.SetLeft(_bigCover, x);
        Canvas.SetTop(_bigCover, y);
        _bigCover.Width = w;
        _bigCover.Height = h;
        _bigCover.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        foreach (var child in _bigCover.Children)
            if (child is Border { Child: TextBlock glyph }) glyph.FontSize = 18 * w / 44;
    }

    /// <summary>Картинка из байтов; maxWidth — уменьшить до этой ширины, если она больше (память и скорость).</summary>
    static BitmapImage? MakeImage(byte[] bytes, int maxWidth)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            int w = PixelWidthOf(bytes);
            if (w == 0 || w > maxWidth) bi.DecodePixelWidth = maxWidth;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Обложки нет — градиент в цветах акцента и значок ноты.</summary>
    void SetCoverPlaceholder(bool toBig = true)
    {
        _cover.Background = PlaceholderBrush();
        var note = Glyph(GlyphMusic, 18);
        note.Foreground = Paint(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        _cover.Child = note;
        if (toBig) SetBigCover(PlaceholderBrush(), note: true);
    }

    LinearGradientBrush PlaceholderBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop(_accent, 0),
            new GradientStop(Color.FromRgb(0xB9, 0x8B, 0xFF), 0.55),
            new GradientStop(Color.FromRgb(0xFF, 0x9E, 0xC7), 1),
        },
    };

    /// <summary>Понятное имя плеера из его идентификатора в Windows.</summary>
    static string PlayerName(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Contains("spotify")) return "Spotify";
        if (s.Contains("yandex")) return L.Lang == "ru" ? "Яндекс Музыка" : "Yandex Music";
        if (s.Contains("zunemusic") || s.Contains("mediaplayer")) return "Media Player";
        if (s.Contains("applemusic")) return "Apple Music";
        if (s.Contains("msedge")) return "Edge";
        if (s.Contains("chrome")) return "Chrome";
        if (s.Contains("firefox")) return "Firefox";
        if (s.Contains("opera")) return "Opera";
        if (s.Contains("vivaldi")) return "Vivaldi";
        if (s.Contains("brave")) return "Brave";
        if (s.Contains("foobar")) return "foobar2000";
        if (s.Contains("aimp")) return "AIMP";
        if (s.Contains("vlc")) return "VLC";
        if (s.Contains("itunes")) return "iTunes";
        if (s.Contains("tidal")) return "TIDAL";
        if (s.Contains("deezer")) return "Deezer";
        if (s.Contains("youtube")) return "YouTube Music";
        // Иначе — имя без «.exe» и служебных хвостов
        var name = id;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int us = name.IndexOf('_');
        if (us > 0) name = name[..us];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }

    // ---------------- Время и перемотка ----------------

    void UpdateProgress(MediaSnapshot? snap, double dt, DateTime now)
    {
        var dur = snap?.Track.Duration ?? TimeSpan.Zero;
        bool timeline = snap is { HasTimeline: true } && dur > TimeSpan.Zero;
        var pos = timeline ? _host.RawPosition : TimeSpan.Zero;
        if (pos > dur) pos = dur;
        double frac = timeline ? Math.Clamp(pos / dur, 0, 1) : 0;

        // Только что перемотали: пока плеер не сообщил новую позицию, показываем ту, куда перемотали
        if (_heldFrac is double held)
        {
            if (now >= _heldUntil || Math.Abs(frac - held) < 0.01) _heldFrac = null;
            else frac = held;
        }
        if (_seeking && !timeline) _seeking = false;

        // Время слева: пока тащим кружок — то, куда перемотаем
        var shown = timeline ? dur * (_seeking ? _seekFrac : frac) : TimeSpan.Zero;
        var e = timeline ? Fmt(shown) : "";
        var t = timeline ? Fmt(dur) : "";
        if (e != _elapsedText) _elapsed.Text = _elapsedText = e;
        if (t != _totalText) _total.Text = _totalText = t;

        // Сыграно — акцентом; отрезок между текущим местом и местом перемотки — тусклым акцентом
        double w = _progressBar.ActualWidth;
        double lo = frac, hi = frac;
        if (_seeking) { lo = Math.Min(frac, _seekFrac); hi = Math.Max(frac, _seekFrac); }
        SetWidth(_progressFill, Math.Round(w * lo, 1));
        SetWidth(_progressAhead, Math.Round(w * (hi - lo), 1));
        _aheadMove.X = Math.Round(w * lo, 1);

        // Кружок — только пока держат кнопку мыши на полосе
        _knobMove.X = w * (_seeking ? _seekFrac : frac) - KnobSize / 2;
        double knobTo = _seeking ? 1 : 0;
        if (Math.Abs(_knob.Opacity - knobTo) > 0.01) _knob.Opacity += (knobTo - _knob.Opacity) * (1 - Math.Exp(-dt / 0.06));
        else if (_knob.Opacity != knobTo) _knob.Opacity = knobTo;

        // Дышит под музыку, как кольцо на панели задач
        double level = _host.MusicLevel;
        bool hot = _progressHover || _seeking;
        double fillOp = Math.Min(1, 0.45 + 0.40 * level + (hot ? 0.25 : 0));
        double lineH = hot ? 2.5 : ThumbWidth;
        foreach (var line in _progressLines)
            if (line.Height != lineH) { line.Height = lineH; line.CornerRadius = new CornerRadius(lineH / 2); }
        if (Math.Abs(_progressFill.Opacity - fillOp) > 0.005) _progressFill.Opacity = fillOp;
        _progressGlow.BlurRadius = 4 + 6 * level + (hot ? 3 : 0);

        var cursor = CanSeekNow() ? Cursors.Hand : null;
        if (_progressBar.Cursor != cursor) _progressBar.Cursor = cursor;
    }

    static void SetWidth(FrameworkElement el, double w)
    {
        if (double.IsNaN(el.Width) || Math.Abs(el.Width - w) > 0.05) el.Width = Math.Max(0, w);
    }

    static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Перемотка на начало строки i (с учётом ручного сдвига синхронизации).</summary>
    void SeekToLine(int i)
    {
        if (_builtFor == null || i < 0 || i >= _builtFor.Count || !CanSeekNow() || _coverExpanded) return;
        var target = _builtFor[i].Time - _host.TrackOffset;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        var dur = _host.CurrentSnapshot!.Track.Duration;
        if (target > dur) return;
        _scroll = 0; // список одним плавным движением едет от того места, где он сейчас, к этой строке
        _seekLine = i;
        _seekLineUntil = DateTime.Now.AddSeconds(2.5);
        _heldFrac = Math.Clamp(target / dur, 0, 1);
        _heldUntil = DateTime.Now.AddSeconds(2.5);
        Command(m => m.SeekAsync(target));
    }

    void SeekTo(double fraction)
    {
        if (!CanSeekNow()) return;
        var target = _host.CurrentSnapshot!.Track.Duration * Math.Clamp(fraction, 0, 1);
        Command(m => m.SeekAsync(target));
    }

    // ---------------- Кнопки ----------------

    void UpdateButtons(MediaSnapshot? snap)
    {
        var c = snap?.Controls ?? MediaControlsInfo.None;

        SetShown(_shuffleBtn, c.CanShuffle);
        SetShown(_repeatBtn, c.CanRepeat);
        SetEnabled(_prevBtn, c.CanPrevious);
        SetEnabled(_nextBtn, c.CanNext);
        SetEnabled(_playBtn, c.CanPlayPause);

        bool playing = _wantPlaying ?? snap?.Playing == true;
        var glyph = playing ? GlyphPause : GlyphPlay;
        if (_playGlyph.Text != glyph)
        {
            _playGlyph.Text = glyph;
            // Треугольник визуально левее центра — чуть сдвигаем
            _playGlyph.Margin = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
            _playBtn.ToolTip = L.S(playing ? "pause" : "play");
        }

        bool shuffle = _wantShuffle ?? c.Shuffle == true;
        SetActive(_shuffleGlyph, _shuffleDot, shuffle);

        var repeat = _wantRepeat ?? c.Repeat ?? RepeatMode.None;
        SetActive(_repeatGlyph, _repeatDot, repeat != RepeatMode.None);
        var rg = repeat == RepeatMode.Track ? GlyphRepeatOne : GlyphRepeatAll;
        if (_repeatGlyph.Text != rg) _repeatGlyph.Text = rg;
        var tip = L.S(repeat switch { RepeatMode.List => "repeatAll", RepeatMode.Track => "repeatOne", _ => "repeatOff" });
        if (!Equals(_repeatBtn.ToolTip, tip)) _repeatBtn.ToolTip = tip;
    }

    /// <summary>Кольцо кнопки play дышит под музыку, как кольцо на панели задач; при наведении — ярче.</summary>
    void AnimatePlayRing()
    {
        double s = _host.MusicLevel;
        double hover = _playHover ? 0.25 : 0;
        _playRing.Opacity = Math.Min(1, 0.6 + 0.35 * s + hover);
        _playScale.ScaleX = _playScale.ScaleY = 1 + 0.08 * s + (_playHover ? 0.04 : 0);
        _playGlow.BlurRadius = 4 + 6 * s + (_playHover ? 3 : 0);
    }

    static void SetShown(UIElement b, bool shown)
    {
        var v = shown ? Visibility.Visible : Visibility.Hidden; // место остаётся — кнопки не съезжают
        if (b.Visibility != v) b.Visibility = v;
    }

    static void SetEnabled(UIElement b, bool enabled)
    {
        double op = enabled ? 1 : 0.35;
        if (b.Opacity != op) b.Opacity = op;
        if (b.IsHitTestVisible != enabled) b.IsHitTestVisible = enabled;
    }

    void SetActive(TextBlock glyph, Border dot, bool active)
    {
        var color = active ? _accent : _fg;
        if (glyph.Foreground is not SolidColorBrush sb || sb.Color != color) glyph.Foreground = Paint(color);
        double op = active ? 1 : 0;
        if (dot.Opacity != op) dot.Opacity = op;
    }

    void PlayPause()
    {
        var snap = _host.CurrentSnapshot;
        if (snap == null || !snap.Controls.CanPlayPause) return;
        bool playing = _wantPlaying ?? snap.Playing;
        _wantPlaying = !playing;
        _wantUntil = DateTime.Now.AddSeconds(2);
        Command(m => m.PlayPauseAsync(playing));
    }

    void ToggleShuffle()
    {
        var c = _host.CurrentSnapshot?.Controls;
        if (c is not { CanShuffle: true }) return;
        bool on = !(_wantShuffle ?? c.Shuffle == true);
        _wantShuffle = on;
        _wantUntil = DateTime.Now.AddSeconds(2);
        Command(m => m.SetShuffleAsync(on));
    }

    /// <summary>Повтор по кругу: выкл → весь список → этот трек → выкл.</summary>
    void CycleRepeat()
    {
        var c = _host.CurrentSnapshot?.Controls;
        if (c is not { CanRepeat: true }) return;
        var cur = _wantRepeat ?? c.Repeat ?? RepeatMode.None;
        var next = cur switch { RepeatMode.None => RepeatMode.List, RepeatMode.List => RepeatMode.Track, _ => RepeatMode.None };
        _wantRepeat = next;
        _wantUntil = DateTime.Now.AddSeconds(2);
        Command(m => m.SetRepeatAsync(next));
    }

    /// <summary>Команда плееру, потом сразу перечитываем его состояние (не дожидаясь секундного опроса).</summary>
    async void Command(Func<MediaWatcher, Task<bool>> command)
    {
        try
        {
            bool ok = await command(_host.Media);
            if (!ok) { _seekLine = -1; _wantPlaying = null; _wantShuffle = null; _wantRepeat = null; _heldFrac = null; }
            await Task.Delay(250);
            await _host.RefreshMediaAsync();
        }
        catch (Exception ex) { App.Log("Flyout command: " + ex.Message); }
    }

    // ---------------- Поделиться ----------------

    /// <summary>
    /// Копирует ссылку на трек в сервисе, где он играет (Spotify, Яндекс Музыка, Apple Music, Deezer, TIDAL).
    /// Точной ссылки на трек Windows не сообщает, поэтому ссылка ведёт на поиск этого трека в сервисе;
    /// для браузеров и остальных плееров — поиск на YouTube.
    /// </summary>
    void Share()
    {
        var t = _host.CurrentTrack;
        if (t == null) return;
        try
        {
            Clipboard.SetText(ShareLink(t));
            ShowToast(L.S("linkCopied"));
        }
        catch (Exception ex)
        {
            App.Log("Share: " + ex.Message);
            ShowToast(L.S("copyFailed"));
        }
    }

    static string ShareLink(TrackInfo t)
    {
        var n = TrackNormalizer.Clean(t.Artist, t.Title);
        var text = string.IsNullOrWhiteSpace(n.Artist) ? n.Title : $"{n.Artist} {n.Title}";
        var q = Uri.EscapeDataString(text.Trim());
        var s = t.Source.ToLowerInvariant();
        if (s.Contains("spotify")) return $"https://open.spotify.com/search/{q}";
        if (s.Contains("yandex")) return $"https://music.yandex.ru/search?text={q}";
        if (s.Contains("applemusic") || s.Contains("itunes")) return $"https://music.apple.com/search?term={q}";
        if (s.Contains("deezer")) return $"https://www.deezer.com/search/{q}";
        if (s.Contains("tidal")) return $"https://tidal.com/search?q={q}";
        return $"https://www.youtube.com/results?search_query={q}";
    }

    void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };
        _toastTimer.Start();
    }

    // ---------------- Положение ----------------

    /// <summary>Над панелью задач, по центру над кольцом (не выходя за край экрана).</summary>
    void ShowNearRing()
    {
        const double Gap = 12; // зазор между панелью задач и панелью с текстом, DIP
        // Место под нижние элементы в панели — ровно их высота
        _controlsRoot.Measure(new Size(PanelWidth, double.PositiveInfinity));
        _spacer.Height = Math.Ceiling(_controlsRoot.DesiredSize.Height);
        OnFrame(this, EventArgs.Empty); // сразу правильная высота блока (текст или надпись), без анимации при открытии
        Show();
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        var ring = _host.RingScreenCenter();
        double x = ring.X / sx, y = ring.Y / sy;
        var wa = SystemParameters.WorkArea;

        // По центру кольца
        double left = x - w / 2, top = y - h - Gap;
        bool horizontal = true, bottomBar = true;
        if (Native.GetTaskbarRect() is { } tb)
        {
            double tl = tb.Left / sx, tt = tb.Top / sy, tr = tb.Right / sx, tbm = tb.Bottom / sy;
            horizontal = tb.Width >= tb.Height;
            bottomBar = horizontal && tt > wa.Top;
            if (bottomBar) top = tt - h - Gap;                                      // панель задач внизу
            else if (horizontal) top = tbm + Gap;                                   // панель задач вверху
            else
            {
                top = y - h / 2;
                left = tl > wa.Left ? tl - w - Gap : tr + Gap;                      // справа / слева
            }
            if (!bottomBar) _panelGrid.VerticalAlignment = VerticalAlignment.Top;   // растём вниз
        }

        Left = Math.Max(wa.Left + Gap, Math.Min(left, wa.Right - w - Gap));
        Top = Math.Max(wa.Top + Gap, Math.Min(top, wa.Bottom - h));
        UpdateLayout();

        // Проверка по настоящим пикселям: центр окна ровно над центром кольца. Если разошлось
        // (округления, другой масштаб) — сдвигаем, но не за край экрана
        if (horizontal && _hwnd != IntPtr.Zero && Native.GetWindowRect(_hwnd, out var r))
        {
            double delta = ring.X - (r.Left + r.Right) / 2.0;
            if (Math.Abs(delta) >= 1) Left = Math.Max(wa.Left + Gap, Math.Min(Left + delta / sx, wa.Right - w - Gap));
            if (Diag.Enabled) Diag.Write($"Flyout: ring {ring.X:0}, window {r.Left}-{r.Right}, shift {delta:0.#} px, dpi {sx:0.##}");
        }
        // Дальше высоту окна меняем сами (FitWindowHeight) — в целых пикселях, от неподвижного края
        SizeToContent = SizeToContent.Manual;
        UpdateLayout();
        if (_hwnd != IntPtr.Zero && Native.GetWindowRect(_hwnd, out var wr))
        {
            _leftPx = wr.Left;
            _widthPx = wr.Width;
            _topPx = wr.Top;
            _heightPx = wr.Height;
            _bottomPx = bottomBar ? wr.Bottom : null;
            _sized = true;
            _panelBottomPx = int.MinValue;

            // Слой с нижними элементами — над низом панели
            _controlsWin.Owner = this;
            _controlsWin.Show();
            PlaceControls(wr.Bottom);
            _controlsWin.UpdateLayout();

            // Кнопка play — ровно над центром кольца. Колонки с кнопками при масштабе 125%, 150% и т. п.
            // округляются до целых пикселей, и play съезжала на 1–2 px. Меряем, где она оказалась на экране,
            // и сдвигаем ряд кнопок на целое число пикселей (так значки остаются чёткими)
            if (horizontal)
            {
                var pc = _playBtn.PointToScreen(new Point(_playBtn.ActualWidth / 2, _playBtn.ActualHeight / 2));
                double d = Math.Round(ring.X - pc.X);
                if (d != 0) _buttonsMove.X = d / sx;
                if (Diag.Enabled) Diag.Write($"Flyout: play button at {pc.X:0.#}, moved {d} px");
            }
        }
        Activate();
    }

    /// <summary>Ставит слой с нижними элементами так, чтобы его низ совпал с низом панели (физ. пиксели).</summary>
    void PlaceControls(int panelBottomPx)
    {
        if (_ctrlHwnd == IntPtr.Zero || panelBottomPx == _panelBottomPx) return;
        _panelBottomPx = panelBottomPx;
        double scale = VisualTreeHelper.GetDpi(_controlsWin).DpiScaleY;
        int h = (int)Math.Round(_spacer.Height * scale);
        Native.SetWindowPos(_ctrlHwnd, IntPtr.Zero, _leftPx, panelBottomPx - h, _widthPx, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
    }

    /// <summary>
    /// Окно растёт или сжимается вверх (низ на месте). Обычно Windows при смене размера оставляет старую
    /// картинку окна прижатой к ВЕРХУ — и на один кадр, пока панель перерисовывается под новый размер,
    /// обложка, полоса и кнопки прыгали. Просим сохранять старую картинку прижатой к НИЗУ: тогда всё,
    /// что под текстом, стоит на месте, а меняется только верхний край панели.
    /// </summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_NCCALCSIZE || wParam == IntPtr.Zero || lParam == IntPtr.Zero || _bottomPx == null) return IntPtr.Zero;
        var before = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(lParam);
        var oldClient = before.R2;
        Native.DefWindowProc(hwnd, msg, wParam, lParam); // новая клиентская область — как обычно
        var p = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(lParam);
        var client = p.R0;
        int h = Math.Min(client.Height, oldClient.Height), w = Math.Min(client.Width, oldClient.Width);
        handled = true;
        if (h <= 0 || w <= 0) return IntPtr.Zero;
        p.R1 = new Native.RECT { Left = client.Left, Top = client.Bottom - h, Right = client.Left + w, Bottom = client.Bottom };
        p.R2 = new Native.RECT { Left = oldClient.Left, Top = oldClient.Bottom - h, Right = oldClient.Left + w, Bottom = oldClient.Bottom };
        System.Runtime.InteropServices.Marshal.StructureToPtr(p, lParam, false);
        return new IntPtr(Native.WVR_VALIDRECTS);
    }

    /// <summary>
    /// Каждый кадр: окно ровно по высоте панели. Считаем в целых физических пикселях от неподвижного края
    /// (низа, если панель задач внизу), а панель прижата к этому краю — поэтому кнопки, обложка и полоса
    /// не сдвигаются ни на пиксель, пока блок с текстом плавно растёт или сжимается.
    /// </summary>
    void FitWindowHeight()
    {
        if (!_sized || _hwnd == IntPtr.Zero) return;
        _panelGrid.Measure(new Size(PanelWidth, double.PositiveInfinity));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int hPx = (int)Math.Ceiling(_panelGrid.DesiredSize.Height * scale - 0.01);
        if (hPx <= 0 || hPx == _heightPx) return;
        _heightPx = hPx;
        int topPx = _bottomPx is int bottom ? bottom - hPx : _topPx;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _leftPx, topPx, _widthPx, hPx, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        // Панель задач внизу — низ панели не двигается, и слой с кнопками тоже (PlaceControls ничего не делает).
        // Панель задач сверху/сбоку — панель растёт вниз, слой едет вместе с её низом
        PlaceControls(topPx + hPx);
    }

    // ---------------- Мелочи ----------------

    static SolidColorBrush Paint(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Color Mix(Color a, Color b, double k) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k),
        (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}
