using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskbarLyrics;

public partial class OverlayWindow : Window
{
    readonly MediaWatcher _media = new();
    readonly Settings _settings = Settings.Load();
    readonly LyricsService _lyrics;

    readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(1) }; // опрос плеера, позиция окна
    readonly DispatcherTimer _trayTimer = new() { Interval = TimeSpan.FromMilliseconds(400) }; // граница трея
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(30) }; // первая проверка через 30 с, дальше раз в сутки
    Updater.Release? _update;   // найденная новая версия
    bool _updateBusy;
    readonly Stopwatch _sincePoll = new();

    IntPtr _hwnd;
    MediaSnapshot? _snap;
    TrackInfo? _track;
    List<LyricLine>? _lines;
    string _source = "";
    int _shownIndex = int.MinValue;
    CancellationTokenSource? _cts;
    bool _polling, _dragging, _hiddenForFullscreen;
    bool _embedded;             // виджет живёт внутри окна панели задач
    int _dragStartCursorX, _dragStartWindowX;
    DateTime _holdUntil;
    bool _dissolving;           // строка медленно растворяется в паузе
    bool _activeHidden;         // активная строка уже растворена (невидима)

    // Караоке-сцена: активная строка и строка, которая сейчас уходит
    TextBlock _active, _idle;
    bool _karaoke;              // активная строка — строка текста (заливка идёт)
    Color _hi, _dim, _glow;
    Color _sung;                // цвет пропетой части: обычный или акцентный

    // Свечение пропетого текста
    LinearGradientBrush _glowBrush = new();
    Color _glowA, _glowB;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    const int GlowStops = 8;

    // Кольцо-визуализатор (видно всегда) и звук для «дыхания»
    readonly AudioLevel _audio = new();
    DateTime _audioRetry;       // когда можно снова попробовать включить захват звука
    double _ringFade;           // плавное появление кольца при запуске
    int? _trayLeft;             // левая граница трея, физ. пиксели
    int _trayButton;            // ширина кнопки трея, физ. пиксели (0 — неизвестна)
    bool _trayBusy;
    bool _embedOff;             // встраивание в панель не удалось или выключено «спасательным кругом» (до перезапуска)
    int _buttonOddHits;         // сколько проверок подряд стрелка трея «необычной» ширины
    int _fullscreenHits;        // сколько проверок подряд поверх панели полноэкранное окно
    Native.WinEventProc? _winEvent; // держим ссылку, иначе сборщик мусора удалит обработчик
    IntPtr _winEventHook;

    bool _titleGlow;            // текст не найден — свечение на весь заголовок
    bool _glowOn;               // свечение пропетого текста сейчас видно (тоже «дышит» от музыки)
    double _noteLevel;
    double _glowFast, _glowSlow;  // уровень для свечения: быстрый (вспышки) и медленный (фон)
    const double GlowOpacity = 0.5;

    const int NoTimelineIndex = -1000;

    // Значки состояний вместо надписей (подробности — во всплывающей подсказке при наведении)
    const string SymWaiting = "♪";       // ждём музыку
    const string SymSearching = "⋯";     // ищу текст
    const string SymNotFound = "∅";      // текст не найден
    const string SymInstrumental = "♪";  // инструментал / проигрыш
    const string SymUntimed = "≡";       // текст есть, но без таймингов
    const string SymError = "⚠";         // ошибка сети
    const string SymNoTimeline = "◌";    // плеер не сообщает позицию

    // Параметры анимации
    const double BlurMax = 12;       // насколько размыта строка в начале/конце перехода
    const double Travel = 9;         // сдвиг по вертикали, DIP
    const double ScaleFrom = 0.94;
    static readonly TimeSpan InDuration = TimeSpan.FromMilliseconds(420);
    static readonly TimeSpan OutDuration = TimeSpan.FromMilliseconds(300);

    // Растворение в паузах: если после строки тишина дольше MinPause, строка тает в размытии,
    // и чем пауза длиннее, тем медленнее (доля паузы, но в пределах DissolveMin…DissolveMax)
    static readonly TimeSpan MinPause = TimeSpan.FromSeconds(4);
    static readonly TimeSpan DissolveDelay = TimeSpan.FromSeconds(3);
    static readonly TimeSpan DissolveMin = TimeSpan.FromMilliseconds(800);
    static readonly TimeSpan DissolveMax = TimeSpan.FromSeconds(4);
    const double DissolveShare = 0.6;
    const double DissolveBlur = 18;

    public OverlayWindow()
    {
        InitializeComponent();
        L.Set(_settings.Language);
        Diag.SetEnabled(_settings.Diagnostics);
        if (_settings.Locked) Root.Cursor = Cursors.Arrow;
        ApplyOffsetY();
        ApplyAlignment();
        _lyrics = new LyricsService(() => _settings.LyricsFolderResolved);

        ApplyTheme();
        PrepareLine(LineA);
        PrepareLine(LineB);
        PrepareGlow();
        _active = LineA;
        _idle = LineB;
        ApplyFont(_settings.FontFamily);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd); // не в Alt+Tab и не забирает фокус
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Attach();
            PlaceWindow();
            DiagState("Started");
            // Смена активного окна, сворачивание/разворачивание, Win+D — сразу проверяем, не оказались ли под панелью
            _winEvent = (_, _, _, _, _, _, _) => EnsureOnTop();
            _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEvent, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        };

        Loaded += async (_, _) =>
        {
            ShowStatus(SymWaiting, L.S("waiting"));
            try { await _media.StartAsync(); }
            catch (Exception ex) { App.Log(ex.ToString()); ShowStatus(SymError, L.S("noSmtc")); }
            _slow.Start();
            _trayTimer.Start();
            if (Updater.Configured && _settings.AutoUpdate) _updateTimer.Start();
            CompositionTarget.Rendering += OnRender; // ~60 кадров/с для плавной заливки
            await PollAsync();
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            _slow.Stop();
            _trayTimer.Stop();
            _updateTimer.Stop();
            _audio.Dispose();
            if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
            // Встроенный виджет закрывается сам, когда перезапускается Проводник, — создаём его заново
            if (!App.Exiting) Diag.Write("Widget closed (Explorer restarted?) — waiting for a new taskbar");
            App.RecreateOverlay();
        };

        _slow.Tick += async (_, _) => { KeepOnTaskbar(); await PollAsync(); };
        // Трей проверяем чаще: когда появляется новый значок, виджет должен уступить место сразу
        _trayTimer.Tick += (_, _) => RefreshTray();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            await CheckUpdatesAsync(manual: false);
        };

        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.Invoke(ApplyTheme);

    }

    // ---------------- Трек и текст ----------------

    async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await _media.PollAsync();
            _snap = snap;
            _sincePoll.Restart();

            var t = snap?.Track;
            if (t?.Key != _track?.Key)
            {
                Diag.Write(t == null ? "Track: none" : $"Track: \"{t.Artist}\" — \"{t.Title}\", album \"{t.Album}\", {t.Duration:m\\:ss}, player {t.Source}");
                _track = t;
                OnTrackChanged();
            }
        }
        catch (Exception ex) { App.Log(ex.ToString()); }
        finally { _polling = false; }
    }

    void OnTrackChanged(bool force = false)
    {
        _cts?.Cancel();
        _lines = null;
        _shownIndex = int.MinValue;

        var t = _track;
        if (t == null) { ShowStatus("", null); return; }

        // Без значка в самой строке: когда текст загрузится, заголовок уже тот же и не перерисовывается
        ShowStatus(t.DisplayName, $"{t.DisplayName}\n{SymSearching}  {L.S("searching")}");
        var cts = _cts = new CancellationTokenSource();
        _ = LoadAsync(t, force, cts.Token);
    }

    async Task LoadAsync(TrackInfo t, bool force, CancellationToken ct)
    {
        try
        {
            var r = await _lyrics.GetAsync(t, force, ct);
            if (ct.IsCancellationRequested) return;

            Diag.Write(r == null ? "Lyrics: not found"
                : $"Lyrics: source {r.Source}, synced {(!string.IsNullOrWhiteSpace(r.Synced) ? "yes" : "no")}, plain {(!string.IsNullOrWhiteSpace(r.Plain) ? "yes" : "no")}, instrumental {r.Instrumental}");
            if (r == null)
            {
                ShowTrackStatus(t, SymNotFound, L.S("notFound"));
            }
            else if (r.Instrumental)
            {
                ShowTrackStatus(t, SymInstrumental, L.S("instrumental"));
            }
            else if (!string.IsNullOrWhiteSpace(r.Synced) && LrcParser.Parse(r.Synced) is { Count: > 0 } lines)
            {
                _lines = lines;
                _source = r.Source;
                _shownIndex = int.MinValue; // OnRender сам покажет нужную строку
            }
            else
            {
                ShowTrackStatus(t, SymUntimed, L.S("untimed"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            if (!ct.IsCancellationRequested)
                ShowTrackStatus(t, SymError, L.S("netError"));
        }
    }

    TimeSpan CurrentPosition()
    {
        var s = _snap!;
        var p = s.Position;
        if (s.Playing) p += _sincePoll.Elapsed * s.Rate;
        int offset = _track != null ? _settings.TrackOffsets.GetValueOrDefault(_track.Key) : 0;
        return p + TimeSpan.FromMilliseconds(offset);
    }

    /// <summary>Каждый кадр: смена строки + заливка караоке.</summary>
    void OnRender(object? sender, EventArgs e)
    {
        AnimateRing();
        UpdateHitZone();
        // Каждый кадр: не оказались ли под панелью задач. Проверка стоит микросекунды, зато если Windows
        // опустила нас (сворачивание окон, Win+D, клик по трею), возвращаемся уже в следующем кадре
        EnsureOnTop();
        if (_titleGlow && _glowOn) UpdateGlow(1);
        if (_lines == null || _snap == null || _track == null) return;
        if (DateTime.Now < _holdUntil) return;

        if (!_snap.HasTimeline)
        {
            if (_shownIndex != NoTimelineIndex)
            {
                _shownIndex = NoTimelineIndex;
                ShowTrackStatus(_track, SymNoTimeline, L.S("noTimeline"));
            }
            return;
        }

        var pos = CurrentPosition();
        if (_dissolving && _shownIndex >= 0 && _shownIndex < _lines.Count && pos < LineEnd(_shownIndex) - TimeSpan.FromMilliseconds(500))
            _shownIndex = int.MinValue; // перемотали назад — показать строку снова
        // Следующая строка появляется чуть раньше своего времени — чтобы успеть её прочитать
        int i = LrcParser.IndexAt(_lines, pos + TimeSpan.FromMilliseconds(_settings.LineLeadMs));

        if (i != _shownIndex)
        {
            _shownIndex = i;
            _dissolving = false;
            var tip = $"{_track.DisplayName}\n{L.F("source", _source == LocalLyrics.SourceName ? L.S("localSource") : _source)}";
            if (i < 0)
            {
                // Вступление — визуализатор
                ShowVisualizer(tip);
            }
            else if (string.IsNullOrWhiteSpace(_lines[i].Text))
            {
                // Проигрыш
                ShowVisualizer(tip);
            }
            else
            {
                _karaoke = true;
                Transition(_lines[i].Text, 0, tip);
            }
        }

        if (_karaoke && i >= 0)
        {
            var fraction = Fraction(i, pos);
            SetFill(_active, fraction);
            UpdateGlow(fraction);
            TryDissolve(i, pos);
        }
    }

    /// <summary>Строка допета, а до следующей долго — плавно растворяем её, не дожидаясь смены.</summary>
    void TryDissolve(int i, TimeSpan pos)
    {
        if (_dissolving) return;
        var end = LineEnd(i);
        var next = i + 1 < _lines!.Count ? _lines[i + 1].Time : end + TimeSpan.FromSeconds(6);
        next -= TimeSpan.FromMilliseconds(_settings.LineLeadMs); // следующая строка появится чуть раньше
        var pause = next - end;
        if (pause < MinPause || pos < end + DissolveDelay) return;

        var remaining = next - pos;
        var dur = pause * DissolveShare;
        if (dur < DissolveMin) dur = DissolveMin;
        if (dur > DissolveMax) dur = DissolveMax;
        if (dur > remaining * 0.9) dur = remaining * 0.9; // закончить до появления следующей строки
        if (dur <= TimeSpan.FromMilliseconds(100)) return;

        _dissolving = true;
        _activeHidden = true;
        _karaoke = false;
        SetFill(_active, 1);
        HideGlow(dur * 0.7);
        Animate(_active, show: false, duration: dur, slow: true);
    }

    /// <summary>Когда заливка строки доходит до конца.</summary>
    TimeSpan LineEnd(int i)
    {
        var line = _lines![i];
        var gap = i + 1 < _lines.Count ? _lines[i + 1].Time - line.Time : TimeSpan.FromSeconds(5);
        var dur = TimeSpan.FromSeconds(Math.Max(1.0, line.Text.Length * 0.11));
        if (dur > gap * 0.92) dur = gap * 0.92;
        return line.Time + dur;
    }

    /// <summary>
    /// Доля «пропетой» строки. В LRCLIB обычно только построчные метки,
    /// поэтому заливка идёт равномерно: примерно по темпу пения, но не дольше промежутка до следующей строки.
    /// </summary>
    double Fraction(int i, TimeSpan pos)
    {
        var start = _lines![i].Time;
        var dur = LineEnd(i) - start;
        if (dur <= TimeSpan.Zero) return 1;
        return Math.Clamp((pos - start) / dur, 0, 1);
    }

    /// <summary>
    /// Кольцо-визуализатор видно всегда. Строка текста появляется слева от него, когда поётся строка;
    /// всё остальное — простой, поиск, «текст не найден», нет интернета, вступление, проигрыш — это просто
    /// пустая строка рядом с кольцом, а пояснение остаётся во всплывающей подсказке.
    /// </summary>
    void ShowStatus(string text, string? tooltip) => ShowVisualizer(tooltip);

    void ShowVisualizer(string? tooltip)
    {
        _titleGlow = false;
        _karaoke = false;
        Transition("", 0, tooltip);
    }

    void ShowTrackStatus(TrackInfo t, string symbol, string meaning) =>
        ShowStatus($"{t.DisplayName}  {symbol}", $"{t.DisplayName}\n{symbol}  {meaning}");

    // ---------------- Анимация ----------------

    void PrepareLine(TextBlock tb)
    {
        tb.Opacity = 0;
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, 1));
        tg.Children.Add(new TranslateTransform(0, 0));
        tb.RenderTransform = tg;

        // Караоке-заливка: [пропето][мягкий край][ещё не пропето]
        tb.Foreground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(_sung, 0),
                new GradientStop(_sung, 0),
                new GradientStop(_dim, 0),
                new GradientStop(_dim, 1),
            }
        };
    }

    static void SetFill(TextBlock tb, double p)
    {
        if (tb.Foreground is not LinearGradientBrush b) return;
        const double edge = 0.06;
        // Растягиваем диапазон, чтобы мягкий край полностью выходил за границы на 0 и 1
        double x = -edge + p * (1 + edge);
        b.GradientStops[1].Offset = Math.Clamp(x, 0, 1);
        b.GradientStops[2].Offset = Math.Clamp(x + edge, 0, 1);
    }

    void Transition(string text, double fill, string? tooltip)
    {
        if (_update != null)
            tooltip = (string.IsNullOrEmpty(tooltip) ? "" : tooltip + "\n") + "⬆  " + L.F("updateTip", _update.Version);
        Root.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        if (_active.Text != text || _activeHidden) HideGlow(TimeSpan.FromMilliseconds(150));

        if (_active.Text == text && !_activeHidden)
        {
            SetFill(_active, fill);
            return;
        }

        var outgoing = _active;
        var incoming = _idle;
        _active = incoming;
        _idle = outgoing;

        _activeHidden = false;
        incoming.Text = text;
        FitText(incoming);
        ResetLineColors(incoming);
        SetFill(incoming, fill);
        bool letters = _karaoke && fill == 0 && _settings.LetterFx;
        if (letters)
        {
            try { AnimateLetters(incoming); }
            catch (Exception ex) { App.Log("Letters: " + ex.Message); incoming.TextEffects = null; }
        }
        else incoming.TextEffects = null;

        Animate(outgoing, show: false);
        if (!string.IsNullOrEmpty(text)) Animate(incoming, show: true, letters: letters);
    }

    void Animate(TextBlock tb, bool show, TimeSpan? duration = null, bool slow = false, bool letters = false)
    {
        var tg = (TransformGroup)tb.RenderTransform;
        var scale = (ScaleTransform)tg.Children[0];
        var move = (TranslateTransform)tg.Children[1];

        // Входящая строка — всегда с нуля; уходящая — от текущего состояния (переход можно прервать)
        var blur = show || tb.Effect is not BlurEffect current
            ? new BlurEffect { Radius = show ? BlurMax : 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
            : current;
        tb.Effect = blur;

        var dur = duration ?? (show ? InDuration : OutDuration);
        // Медленное растворение — мягкая кривая и почти без движения, строка словно тает на месте
        IEasingFunction ease = slow
            ? new SineEase { EasingMode = EasingMode.EaseInOut }
            : show
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn };
        double blurTo = slow ? DissolveBlur : BlurMax;
        double travel = slow ? Travel * 0.4 : Travel;
        double scaleTo = slow ? 0.97 : ScaleFrom;
        double blurIn = letters ? LetterBlur : BlurMax, travelIn = letters ? 0 : Travel, scaleIn = letters ? 1 : ScaleFrom;

        DoubleAnimation A(double? from, double to) => new(to, dur) { From = from, EasingFunction = ease };

        var opacity = show ? A(0, 1) : A(null, 0);
        opacity.Completed += (_, _) =>
        {
            if (tb.Effect != blur) return; // уже идёт другой переход
            tb.Effect = show && _active == tb ? Glow() : null; // после появления — лёгкое свечение для читаемости
            if (show && _active == tb && (_karaoke || _titleGlow)) ShowGlow(tb);
        };

        tb.BeginAnimation(OpacityProperty, opacity);
        // Со всплывающими буквами размытие сильнее и тает дольше — пока буквы встают на место
        var blurShow = letters
            ? new DoubleAnimation(blurIn, 0, LetterBlurDuration) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } }
            : A(blurIn, 0);
        blur.BeginAnimation(BlurEffect.RadiusProperty, show ? blurShow : A(null, blurTo));
        move.BeginAnimation(TranslateTransform.YProperty, show ? A(travelIn, 0) : A(null, -travel));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, show ? A(scaleIn, 1) : A(null, scaleTo));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, show ? A(scaleIn, 1) : A(null, scaleTo));
    }

    // ---------------- Свечение пропетого текста ----------------

    void PrepareGlow()
    {
        _glowBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int k = 0; k < GlowStops + 2; k++)
            _glowBrush.GradientStops.Add(new GradientStop(Colors.Transparent, k < GlowStops + 1 ? 0 : 1));
        // Два слоя: плотное свечение у самих букв и широкий мягкий ореол
        GlowLine.Foreground = _glowBrush;
        GlowLine.Effect = new BlurEffect { Radius = 5, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        GlowFar.Foreground = _glowBrush;
        GlowFar.Effect = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    }

    /// <summary>
    /// Каждый кадр: свечение повторяет заливку, а по нему медленно плывут переливы двух оттенков.
    /// </summary>
    void UpdateGlow(double p)
    {
        if (!_settings.Glow) return;
        const double edge = 0.06;
        double x = Math.Clamp(-edge + p * (1 + edge), 0, 1);
        double t = _clock.Elapsed.TotalSeconds;
        var stops = _glowBrush.GradientStops;
        // Быстрая часть даёт вспышки на ударах, медленная — общий уровень
        double punch = Math.Clamp((_glowFast - _glowSlow) * 2.5, 0, 1);
        double lvl = _settings.Visualizer ? Math.Clamp(0.15 + 0.55 * _glowFast + 0.6 * punch, 0, 1) : 0.6;
        double breath = 0.5 + 0.5 * lvl; // дыхание от музыки, как у ноты
        if (GlowFar.Effect is BlurEffect far) far.Radius = 11 + 13 * lvl;
        if (GlowLine.Effect is BlurEffect near) near.Radius = 4 + 4 * lvl;

        for (int k = 0; k < GlowStops; k++)
        {
            double u = x * k / (GlowStops - 1);
            double w = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (u * 1.4 - t / 3.2)); // волна дрейфует вдоль строки
            var c = Mix(_glowA, _glowB, w);
            stops[k].Offset = u;
            stops[k].Color = Color.FromArgb((byte)Math.Clamp((115 + 55 * w) * breath, 0, 255), c.R, c.G, c.B);
        }
        var last = stops[GlowStops - 1].Color;
        stops[GlowStops].Offset = Math.Min(1, x + edge);
        stops[GlowStops].Color = Color.FromArgb(0, last.R, last.G, last.B);
        stops[GlowStops + 1].Color = Colors.Transparent;
    }

    void SyncGlowFont(TextBlock tb)
    {
        foreach (var g in new[] { GlowLine, GlowFar })
        {
            g.Text = tb.Text;
            g.FontFamily = tb.FontFamily;
            g.FontSize = tb.FontSize;
            g.FontWeight = tb.FontWeight;
            g.Margin = tb.Margin;
        }
    }

    void ShowGlow(TextBlock tb)
    {
        if (!_settings.Glow) return;
        SyncGlowFont(tb);
        _glowOn = true;
        tb.Effect = null; // тёмная тень лежала бы поверх свечения и гасила его
        foreach (var g in new[] { GlowLine, GlowFar })
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(GlowOpacity, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Текста нет — переливающееся дышащее свечение на весь заголовок.</summary>
    void EnableTitleGlow()
    {
        _titleGlow = true;
        // Если заголовок сейчас проявляется, свечение включится по окончании анимации;
        // если он уже на экране (текст не менялся) — включаем сразу
        if (_active.Effect is not BlurEffect) ShowGlow(_active);
    }

    void HideGlow(TimeSpan duration)
    {
        _glowOn = false;
        foreach (var g in new[] { GlowLine, GlowFar })
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
    }

    void ToggleGlow()
    {
        _settings.Glow = !_settings.Glow;
        _settings.Save();
        if (_settings.Glow && _karaoke) ShowGlow(_active);
        else
        {
            HideGlow(TimeSpan.FromMilliseconds(200));
            if (_active.Effect == null && _active.Opacity > 0) _active.Effect = Glow();
        }
    }

    static Color Mix(Color a, Color b, double k) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k),
        (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    /// <summary>Сдвиг оттенка по цветовому кругу, градусы.</summary>
    static Color ShiftHue(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0, l = (max + min) / 2, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        if (d != 0)
            h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        h = ((h + degrees) % 360 + 360) % 360;

        double cc = (1 - Math.Abs(2 * l - 1)) * s, xx = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - cc / 2;
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (cc, xx, 0.0), < 120 => (xx, cc, 0.0), < 180 => (0.0, cc, xx),
            < 240 => (0.0, xx, cc), < 300 => (xx, 0.0, cc), _ => (cc, 0.0, xx),
        };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    // ---------------- Кольцо-визуализатор ----------------

    /// <summary>
    /// Каждый кадр: кольцо чуть светлеет и едва заметно «дышит» от громкости баса.
    /// Звук захватывается, только пока плеер играет и включено «дыхание под музыку».
    /// </summary>
    void AnimateRing()
    {
        bool wantAudio = _settings.Visualizer && _snap?.Playing == true;
        if (wantAudio && !_audio.IsRunning && DateTime.Now >= _audioRetry)
        {
            _audioRetry = DateTime.Now.AddSeconds(3); // если устройство занято — пробуем не чаще раза в 3 с
            Task.Run(_audio.Start);
        }
        else if (!wantAudio && _audio.IsRunning) Task.Run(_audio.Stop);

        // Общий сглаженный уровень музыки: им дышат и кольцо, и свечение текста
        var level = wantAudio ? _audio.Level : 0;
        _noteLevel += (level - _noteLevel) * 0.12;
        _glowFast += (level - _glowFast) * (level > _glowFast ? 0.5 : 0.1);
        _glowSlow += (level - _glowSlow) * 0.03;
        _ringFade += (1 - _ringFade) * 0.06;

        double s = _noteLevel;
        Ring.Opacity = _ringFade * (0.45 + 0.40 * s);
        if (Ring.RenderTransform is ScaleTransform rs) rs.ScaleX = rs.ScaleY = 1 + 0.12 * s;
        if (Ring.Effect is DropShadowEffect rg) rg.BlurRadius = 4 + 6 * s;
    }

    /// <summary>
    /// Меню и перетаскивание работают не по всей ширине: есть строка — в правой половине (ближе к кольцу),
    /// нет строки — только на самом кольце. В остальном месте клики проходят на панель задач.
    /// </summary>
    void UpdateHitZone()
    {
        bool hasText = !_activeHidden && !string.IsNullOrEmpty(_active.Text);
        double w = hasText ? Stage.ActualWidth / 2 : RingColumn.ActualWidth;
        if (w > 0 && Math.Abs(HitZone.Width - w) > 0.5) HitZone.Width = w;
    }

    void ResetLineColors(TextBlock tb)
    {
        if (tb.Foreground is not LinearGradientBrush b) return;
        var sung = _karaoke ? _sung : _hi; // статусы и заголовок — обычным цветом
        b.GradientStops[0].Color = sung;
        b.GradientStops[1].Color = sung;
        b.GradientStops[2].Color = _dim;
        b.GradientStops[3].Color = _dim;
    }

    void SetAlign(string align)
    {
        _settings.Align = align;
        _settings.Save();
        ApplyAlignment();
    }

    /// <summary>Строка и свечение прижимаются к выбранному краю (кольцо всегда справа).</summary>
    void ApplyAlignment()
    {
        var (ha, ta, ox) = _settings.Align switch
        {
            "left" => (HorizontalAlignment.Left, TextAlignment.Left, 0.0),
            "right" => (HorizontalAlignment.Right, TextAlignment.Right, 1.0),
            _ => (HorizontalAlignment.Center, TextAlignment.Center, 0.5),
        };
        foreach (var tb in new[] { LineA, LineB, GlowLine, GlowFar })
        {
            tb.HorizontalAlignment = ha;
            tb.TextAlignment = ta;
            tb.RenderTransformOrigin = new Point(ox, 0.5); // масштаб при появлении — от своего края
        }
    }

    void ToggleVisualizer()
    {
        _settings.Visualizer = !_settings.Visualizer; // звук включит/выключит AnimateRing
        _settings.Save();
    }

    // ---------------- Буквы всплывают ----------------

    // Параметры: откуда буква выплывает и как быстро идёт волна
    const double LetterBlur = 12;        // размытие строки, пока буквы всплывают
    static readonly TimeSpan LetterBlurDuration = TimeSpan.FromMilliseconds(800);
    const double LetterAngle = -16;      // наклон в начале, градусы
    const double LetterDx = -3, LetterDy = 8;
    static readonly TimeSpan LetterDuration = TimeSpan.FromMilliseconds(460);
    static readonly TimeSpan LetterFade = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// Каждая буква новой строки выплывает снизу под углом, проявляется и встаёт на место с лёгкой «пружинкой».
    /// Пока буква летит, у неё свой цвет; когда встала — эффект снимается и работает обычная караоке-заливка.
    /// </summary>
    void AnimateLetters(TextBlock tb)
    {
        var text = tb.Text;
        var effects = new TextEffectCollection();
        tb.TextEffects = effects;
        if (string.IsNullOrEmpty(text)) return;

        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, tb.FontSize, Brushes.White, null, TextFormattingMode.Display, ppd);

        int visible = text.Count(c => !char.IsWhiteSpace(c) && !char.IsLowSurrogate(c));
        double step = Math.Min(0.035, 0.5 / Math.Max(1, visible)); // вся волна — не дольше ~0,5 с
        var color = _dim;
        int order = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || char.IsLowSurrogate(text[i])) continue;
            int count = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var bounds = ft.BuildHighlightGeometry(new Point(0, 0), i, count)?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty) continue;

            var rotate = new RotateTransform(LetterAngle, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var move = new TranslateTransform(LetterDx, LetterDy);
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(move);
            var brush = new SolidColorBrush(Color.FromArgb(0, color.R, color.G, color.B));

            var fx = new TextEffect { PositionStart = i, PositionCount = count, Transform = group, Foreground = brush };
            effects.Add(fx);

            var begin = TimeSpan.FromSeconds(0.06 + order++ * step);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 };
            DoubleAnimation To0() => new(0, LetterDuration) { BeginTime = begin, EasingFunction = ease };

            var last = To0();
            last.Completed += (_, _) => effects.Remove(fx); // буква на месте — дальше обычная заливка
            rotate.BeginAnimation(RotateTransform.AngleProperty, To0());
            move.BeginAnimation(TranslateTransform.XProperty, To0());
            move.BeginAnimation(TranslateTransform.YProperty, last);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, LetterFade) { BeginTime = begin });
        }
    }

    DropShadowEffect Glow() =>
        new() { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55, Color = _glow, RenderingBias = RenderingBias.Performance };

    // ---------------- Тема и шрифт ----------------

    void ApplyTheme()
    {
        Theme.Apply(_settings.Theme); // меню и окна

        // Цвет строки подбираем под панель задач
        bool light = Theme.TaskbarIsLight(_settings.Theme);
        _hi = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xDC, 0xDC, 0xDC);
        _dim = Color.FromArgb(0x58, _hi.R, _hi.G, _hi.B);
        _glow = light ? Colors.White : Colors.Black;
        // Два соседних оттенка акцентного цвета — между ними и переливается свечение
        var baseGlow = light ? Theme.Accent : Mix(Theme.Accent, Colors.White, 0.45);
        _sung = _settings.AccentSung ? baseGlow : _hi; // тот же цвет, что у свечения и визуализатора
        _glowA = baseGlow;
        _glowB = ShiftHue(baseGlow, 45);
        Ring.Stroke = new SolidColorBrush(baseGlow);
        if (Ring.RenderTransform is not ScaleTransform) Ring.RenderTransform = new ScaleTransform(1, 1);
        if (Ring.Effect is DropShadowEffect ringGlow) ringGlow.Color = baseGlow;
        else Ring.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.8, Color = baseGlow, RenderingBias = RenderingBias.Performance };

        foreach (var tb in new[] { LineA, LineB })
        {
            if (tb.Foreground is LinearGradientBrush b)
            {
                var sung = _karaoke && tb == _active ? _sung : _hi;
                b.GradientStops[0].Color = sung;
                b.GradientStops[1].Color = sung;
                b.GradientStops[2].Color = _dim;
                b.GradientStops[3].Color = _dim;
            }
            if (tb.Effect is DropShadowEffect d) d.Color = _glow;
        }
    }

    /// <summary>
    /// A line that doesn't fit gets a slightly smaller font (down to 65%) instead of "…".
    /// The real font size is used, not scaling, so the text stays crisp.
    /// </summary>
    void FitText(TextBlock tb)
    {
        double max = _settings.FontSize;
        double avail = TextArea.ActualWidth - 2;
        if (avail <= 0 || string.IsNullOrEmpty(tb.Text)) { tb.FontSize = max; CenterText(tb); return; }

        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double min = Math.Max(9, max * 0.65);
        double size = max;
        while (size > min)
        {
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.White, null, TextFormattingMode.Display, ppd);
            if (ft.WidthIncludingTrailingWhitespace <= avail) break;
            size -= 0.5;
        }
        tb.FontSize = size;
        CenterText(tb);
    }

    /// <summary>
    /// Где у строки «визуальный центр»: 0 — середина строчных букв (высота «x»),
    /// 1 — середина заглавных (высота «H»). 0,5 — ровно между ними.
    /// </summary>
    const double CapWeight = 0.5;

    /// <summary>
    /// Ставим строку по центру панели по самим буквам, а не по «коробке» шрифта. У каждого шрифта
    /// свой невидимый запас сверху и снизу строки (под ударения, хвосты, межстрочный интервал) —
    /// из-за него одни шрифты сидели выше, другие ниже. Теперь меряем настоящие буквы «x» и «H»
    /// выбранного шрифта и по центру панели ставим точку между серединой строчных и заглавных.
    /// TextBlock стоит по центру, поэтому отступ сверху (или снизу) вдвое больше нужного сдвига.
    /// </summary>
    void CenterText(TextBlock tb)
    {
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Measure(string ch) => new(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, tb.FontSize, Brushes.White, null, TextFormattingMode.Display, ppd);

        var fx = Measure("x");
        var fh = Measure("H");
        double baseline = fx.Baseline;
        var bx = fx.BuildGeometry(new Point(0, 0)).Bounds;
        var bh = fh.BuildGeometry(new Point(0, 0)).Bounds;
        double xHeight = bx.IsEmpty ? baseline * 0.5 : baseline - bx.Top;
        double capHeight = bh.IsEmpty ? baseline * 0.7 : baseline - bh.Top;

        // Визуальный центр от верха строки
        double center = baseline - (xHeight / 2 * (1 - CapWeight) + capHeight / 2 * CapWeight);
        double dy = fx.Height / 2 - center;
        tb.Margin = dy >= 0 ? new Thickness(0, 2 * dy, 0, 0) : new Thickness(0, 0, 0, -2 * dy);
    }

    void ApplyFont(string? fontKey)
    {
        var family = FontManager.Resolve(fontKey);
        var weight = FontManager.BestWeight(family, _settings.Bold);
        foreach (var tb in new[] { LineA, LineB })
        {
            tb.FontFamily = family;
            tb.FontSize = _settings.FontSize;
            tb.FontWeight = weight;
        }
        FitText(_active);
        SyncGlowFont(_active);
    }

    void PickFont()
    {
        var w = new FontPickerWindow(_settings.FontFamily, _settings.Bold, ApplyFont);
        if (w.ShowDialog() == true)
        {
            _settings.FontFamily = string.IsNullOrEmpty(w.SelectedKey) ? null : w.SelectedKey;
            _settings.Save();
        }
        ApplyFont(_settings.FontFamily);
    }

    // ---------------- Окно на панели задач ----------------

    /// <summary>Ставит окно на панель задач. Возвращает фактический X (в физических пикселях).</summary>
    int? PlaceWindow(int? desiredX = null)
    {
        if (_hwnd == IntPtr.Zero) return null;
        if (Native.GetTaskbarRect() is not { } tb) return null;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        bool horizontal = tb.Width >= tb.Height;

        int w, h, x, y;
        if (horizontal)
        {
            w = Math.Min((int)Math.Round(_settings.Width * scale), tb.Width);
            h = tb.Height;
            // Прижаты к трею: правый край строки — у левой границы трея, с небольшим отступом
            int? docked = _settings.DockToTray && _trayLeft is int tl && tl > tb.Left + w && tl <= tb.Right
                ? tl - w
                : null;
            x = desiredX ?? docked ?? _settings.X ?? tb.Left + (int)(12 * scale);
            ApplyDockPadding(docked != null && desiredX == null, scale);
            x = Math.Max(tb.Left, Math.Min(x, tb.Right - w));
            y = tb.Top;
        }
        else
        {
            // Вертикальная панель: просто низ панели, без перетаскивания
            w = tb.Width;
            h = (int)(48 * scale);
            x = tb.Left;
            y = tb.Bottom - h;
        }

        if (_embedded && Native.GetParent(_hwnd) is var parent && parent != IntPtr.Zero)
        {
            // Внутри панели задач координаты считаются от её левого верхнего угла;
            // IntPtr.Zero (HWND_TOP) — поверх собственного содержимого панели
            var (cx, cy) = Native.ToClient(parent, x, y);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, cx, cy, w, h, Native.SWP_NOACTIVATE);
        }
        else
        {
            // Отдельное окно: порядок окон не трогаем, иначе виджет вылезал поверх своего меню
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
        if (Diag.Enabled && (x != _dbgX || tb.Left != _dbgTb.Left || tb.Right != _dbgTb.Right || tb.Top != _dbgTb.Top))
        {
            Diag.Write($"Place x={x} w={w} (was {_dbgX}), taskbar {tb.Left},{tb.Top}-{tb.Right},{tb.Bottom}, trayLeft {_trayLeft}, desired {desiredX}, embedded {_embedded}, fg {Native.ForegroundInfo()}");
            _dbgX = x; _dbgTb = tb;
        }
        return x;
    }

    // ---- Журнал диагностики (Diag): запоминаем прошлые значения, чтобы писать только изменения ----
    int? _dbgTray;
    int _dbgX = int.MinValue;
    Native.RECT _dbgTb;

    /// <summary>Сводка состояния в журнал диагностики: экран, панель, трей, встраивание, настройки.</summary>
    void DiagState(string what)
    {
        if (!Diag.Enabled) return;
        var tb = Native.GetTaskbarRect();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Diag.Write($"{what}: DPI scale {scale:0.##}, taskbar {(tb is { } r ? $"{r.Left},{r.Top}-{r.Right},{r.Bottom}" : "not found")}, " +
                   $"tray {_trayLeft} (button {_trayButton}), embedded {_embedded}, embedOff {_embedOff}, " +
                   $"dock {_settings.DockToTray}, locked {_settings.Locked}, align {_settings.Align}, width {_settings.Width}, " +
                   $"font \"{_settings.FontFamily}\" {_settings.FontSize}{(_settings.Bold ? " bold" : "")}, offsetY {_settings.OffsetY}, " +
                   $"theme {_settings.Theme}, taskbar light {Theme.TaskbarIsLight(_settings.Theme)}, visualizer {_settings.Visualizer}");
    }

    void ToggleDiagnostics()
    {
        _settings.Diagnostics = !_settings.Diagnostics;
        _settings.Save();
        Diag.SetEnabled(_settings.Diagnostics);
        if (_settings.Diagnostics)
        {
            _dbgTray = null; _dbgX = int.MinValue; // записать текущее положение заново
            DiagState("State");
            if (_track is { } t) Diag.Write($"Track: \"{t.Artist}\" — \"{t.Title}\", album \"{t.Album}\", {t.Duration:m\\:ss}, player {t.Source}, lyrics source {(_source == "" ? "none" : _source)}");
        }
    }

    /// <summary>Открывает папку с журналом и выделяет файл, чтобы его было легко прикрепить к сообщению.</summary>
    static void ShowDiagnosticsFile()
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDir);
            var args = File.Exists(Diag.FilePath) ? $"/select,\"{Diag.FilePath}\"" : $"\"{Settings.AppDir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex.ToString()); }
    }

    void KeepOnTaskbar()
    {
        if (_hwnd == IntPtr.Zero || _dragging) return;

        // Встроенный виджет прячется вместе с панелью задач сам. Отдельное окно прячется, когда поверх
        // панели полноэкранная игра/видео — только если это держится 2 проверки подряд: иначе короткие
        // всплывающие окна (трей, меню «Пуск», уведомления) заставляли кольцо мигать
        _fullscreenHits = !_embedded && Native.IsForegroundFullscreen(_hwnd) ? _fullscreenHits + 1 : 0;
        bool fs = _fullscreenHits >= 2;
        if (fs != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = fs;
            Diag.Write(fs ? $"Hidden: fullscreen window {Native.ForegroundInfo()}" : "Shown again after fullscreen");
            Native.ShowWindow(_hwnd, fs ? Native.SW_HIDE : Native.SW_SHOWNOACTIVATE);
        }

        // Смена DPI/разрешения, новая панель задач — встаём на место
        if (!fs) { Attach(); PlaceWindow(); }
    }

    /// <summary>
    /// Windows сама опускает нас вниз, когда на миг снимает «поверх всех» с панели задач (Win+D, сворачивание окон),
    /// и виджет моргал. Такие команды перехватываем ещё до выполнения: вместо «вниз» остаёмся наверху.
    /// </summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_WINDOWPOSCHANGING || _embedded || _hiddenForFullscreen || lParam == IntPtr.Zero) return IntPtr.Zero;
        var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
        if ((pos.flags & Native.SWP_NOZORDER) != 0) return IntPtr.Zero;

        var after = pos.hwndInsertAfter;
        bool demote = after == Native.HWND_NOTOPMOST || after == Native.HWND_BOTTOM
            || (after != IntPtr.Zero && after != Native.HWND_TOPMOST && !Native.IsTopmost(after));
        if (!demote) return IntPtr.Zero;

        // Меню открыто — просто никуда не двигаемся (иначе вылезли бы поверх меню)
        if (MenuWindow.IsOpen) pos.flags |= Native.SWP_NOZORDER;
        else pos.hwndInsertAfter = Native.HWND_TOPMOST;
        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Каждый кадр. Встроенный виджет: панель задач иногда перекладывает своё содержимое поверх нас —
    /// поднимаемся обратно. Отдельное окно: если нас накрыла панель (Win+D, сворачивание окон) — наверх.
    /// </summary>
    void EnsureOnTop()
    {
        if (_hwnd == IntPtr.Zero || _hiddenForFullscreen || _dragging) return;
        if (_embedded)
        {
            var parent = Native.GetParent(_hwnd);
            if (parent != IntPtr.Zero && !Native.IsTopChild(parent, _hwnd)) Native.RaiseChild(_hwnd);
            return;
        }
        if (MenuWindow.IsOpen) return;
        if (Native.NeedsRaise(_hwnd)) Native.BringToTop(_hwnd);
    }

    /// <summary>
    /// Встраиваемся внутрь панели задач (или, если встраивание выключено/не удалось, — отдельное окно,
    /// принадлежащее панели: Windows держит его над панелью, когда та поднимается кликом).
    /// </summary>
    void Attach()
    {
        var taskbar = Native.TaskbarHandle();
        if (_hwnd == IntPtr.Zero || taskbar == IntPtr.Zero) return;

        // Виджет всегда живёт внутри панели задач; отдельное окно — только запасной вариант
        if (!_embedOff)
        {
            if (_embedded && Native.GetParent(_hwnd) == taskbar) return;
            if (Native.Embed(_hwnd, taskbar))
            {
                Diag.Write("Embedded into the taskbar");
                _embedded = true;
                Native.RaiseChild(_hwnd);
                return;
            }
            App.Log("Embed into taskbar failed — using a separate window");
            _embedOff = true; // не вышло — до перезапуска программы работаем отдельным окном
        }

        if (_embedded) { Native.Unembed(_hwnd); _embedded = false; }
        if (Native.GetOwner(_hwnd) == taskbar) return;
        Native.SetOwner(_hwnd, taskbar);
        if (!MenuWindow.IsOpen) Native.BringToTop(_hwnd);
    }

    /// <summary>
    /// Программу запустили ещё раз: вдруг встроенный виджет не виден — до перезапуска
    /// работаем обычным окном поверх панели.
    /// </summary>
    public void Rescue()
    {
        if (_embedOff && !_embedded) return;
        Diag.Write("Rescue: second launch — switching to a separate window");
        _embedOff = true;
        Attach();
        PlaceWindow();
    }

    // ---------------- Перетаскивание вдоль панели ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        MenuWindow.CloseOpen();
        if (_settings.Locked) return;
        if (!Native.GetCursorPos(out var p) || !Native.GetWindowRect(_hwnd, out var r)) return;
        _dragStartCursorX = p.X;
        _dragStartWindowX = r.Left;
        _dragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !Native.GetCursorPos(out var p)) return;
        if (_settings.DockToTray)
        {
            if (Math.Abs(p.X - _dragStartCursorX) < 4) return; // просто клик — не отцепляем
            _settings.DockToTray = false; // потащили мышью — отцепляемся от трея
            _settings.Save();
        }
        var x = PlaceWindow(_dragStartWindowX + (p.X - _dragStartCursorX));
        if (x != null) _settings.X = x;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _settings.Save();
    }

    // ---------------- Меню ----------------

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        MenuWindow.ShowAtCursor(BuildMenu());
    }

    List<MenuEntry> BuildMenu()
    {
        static MenuEntry Item(string key, Action action, bool check = false) => new() { Header = L.S(key), Action = action, Checked = check };
        static MenuEntry Label(string key) => new() { Header = L.S(key), Enabled = false };

        var ahead = new List<MenuEntry>();
        foreach (var ms in new[] { 0, 150, 300, 500, 800 })
        {
            var header = ms == 0 ? L.S("aheadOff") : L.F("aheadSec", ms / 1000.0);
            ahead.Add(new MenuEntry { Header = header, Checked = _settings.LineLeadMs == ms,
                Action = () => { _settings.LineLeadMs = ms; _settings.Save(); } });
        }

        var sync = new List<MenuEntry>
        {
            Item("late", () => Shift(+500)),
            Item("early", () => Shift(-500)),
            Item("resetOffset", () => Shift(0)),
            MenuEntry.Separator,
            Label("ahead"),
        };
        sync.AddRange(ahead);

        // Шаг «−/+» прямо в строке меню: меню не закрывается, можно нажимать несколько раз подряд (и крутить колесо)
        static MenuEntry Stepper(string key, Func<string> value, Action dec, Action inc) =>
            new() { Header = L.S(key), Value = value, Decrease = dec, Increase = inc };

        var text = new List<MenuEntry>
        {
            Item("font", () => Dispatcher.BeginInvoke(new Action(PickFont))),
            Item("bold", () => { _settings.Bold = !_settings.Bold; _settings.Save(); ApplyFont(_settings.FontFamily); }, _settings.Bold),
            Stepper("fontSize", () => _settings.FontSize.ToString("0", L.Culture), () => SetFont(-1), () => SetFont(+1)),
            MenuEntry.Separator,
            Label("effects"),
            Item("letterFx", () => { _settings.LetterFx = !_settings.LetterFx; _settings.Save(); }, _settings.LetterFx),
            Item("glow", ToggleGlow, _settings.Glow),
            Item("accentSung", () => { _settings.AccentSung = !_settings.AccentSung; _settings.Save(); ApplyTheme(); }, _settings.AccentSung),
            Item("visualizer", ToggleVisualizer, _settings.Visualizer),
        };

        var place = new List<MenuEntry>
        {
            Item("dockTray", ToggleDock, _settings.DockToTray),
            Item("lock", ToggleLock, _settings.Locked),
            MenuEntry.Separator,
            Stepper("width", () => _settings.Width.ToString("0", L.Culture), () => Resize(-20), () => Resize(+20)),
            // «+» — выше: в настройках отрицательный сдвиг означает «вверх»
            Stepper("offsetY", () => (-_settings.OffsetY).ToString("+0;−0;0", L.Culture), () => MoveVertical(+1), () => MoveVertical(-1)),
            MenuEntry.Separator,
            Label("align"),
            Item("alignLeft", () => SetAlign("left"), _settings.Align == "left"),
            Item("alignCenter", () => SetAlign("center"), _settings.Align is not ("left" or "right")),
            Item("alignRight", () => SetAlign("right"), _settings.Align == "right"),
        };

        var theme = new List<MenuEntry>
        {
            // Полное имя: в .NET 9+ у Window есть своё свойство ThemeMode, и короткое имя указывает на него
            Item("themeSystem", () => SetTheme(TaskbarLyrics.ThemeMode.System), _settings.Theme == TaskbarLyrics.ThemeMode.System),
            Item("themeLight", () => SetTheme(TaskbarLyrics.ThemeMode.Light), _settings.Theme == TaskbarLyrics.ThemeMode.Light),
            Item("themeDark", () => SetTheme(TaskbarLyrics.ThemeMode.Dark), _settings.Theme == TaskbarLyrics.ThemeMode.Dark),
        };

        var lyrics = new List<MenuEntry>
        {
            Item("reload", () => OnTrackChanged(force: true)),
            Item("openLrc", OpenLyricsFolder),
        };

        var updates = new List<MenuEntry>
        {
            Item("updateCheck", () => _ = CheckUpdatesAsync(manual: true)),
            Item("updateAuto", ToggleAutoUpdate, _settings.AutoUpdate),
        };

        var diagnostics = new List<MenuEntry>
        {
            Item("diagLog", ToggleDiagnostics, _settings.Diagnostics),
            Item("diagOpen", ShowDiagnosticsFile),
        };

        var language = new List<MenuEntry>
        {
            new() { Header = "English", Checked = L.Lang == "en", Action = () => SetLanguage("en") },
            new() { Header = "Русский", Checked = L.Lang == "ru", Action = () => SetLanguage("ru") },
        };

        var root = new List<MenuEntry>
        {
            new() { Header = L.S("textStyle"), Children = text },
            new() { Header = L.S("position"), Children = place },
            new() { Header = L.S("theme"), Children = theme },
            MenuEntry.Separator,
            new() { Header = L.S("sync"), Children = sync },
            new() { Header = L.S("lyrics"), Children = lyrics },
            new() { Header = L.S("language"), Children = language },
            new() { Header = L.S("diagnostics"), Children = diagnostics },
            new() { Header = L.S("updates"), Children = updates },
            MenuEntry.Separator,
            Item("autostart", () => Autostart.Set(!Autostart.IsEnabled), Autostart.IsEnabled),
            MenuEntry.Separator,
            Item("exit", App.Quit),
        };
        if (!Updater.Configured) root.RemoveAll(m => m.Children == updates);
        // Есть новая версия — первым пунктом меню
        if (_update is { } upd)
        {
            root.Insert(0, new MenuEntry { Header = L.F("updateInstall", upd.Version), Action = () => _ = InstallUpdateAsync(upd) });
            root.Insert(1, MenuEntry.Separator);
        }
        return root;
    }

    // ---------------- Обновления ----------------

    async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateBusy || !Updater.Configured) return;
        _updateBusy = true;
        try
        {
            _update = await Updater.CheckAsync();
            if (manual)
            {
                if (_update is { } u) MessageBox.Show(L.F("updateTip", u.Version), "HaloTaskbarLyrics");
                else MessageBox.Show(L.F("updateLatest", Updater.Current), "HaloTaskbarLyrics");
            }
        }
        catch (Exception ex)
        {
            // Нет интернета при фоновой проверке — это не ошибка, в error.log не пишем
            Diag.Write("Update check failed: " + ex.Message);
            if (manual) MessageBox.Show(L.S("updateFailed"), "HaloTaskbarLyrics");
        }
        finally { _updateBusy = false; }
    }

    async Task InstallUpdateAsync(Updater.Release rel)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (await Updater.InstallAsync(rel)) App.Quit();
        }
        catch (Exception ex)
        {
            App.Log("Update install: " + ex);
            MessageBox.Show(L.F("updateInstallFailed", ex.Message), "HaloTaskbarLyrics");
        }
        finally { _updateBusy = false; }
    }

    void ToggleAutoUpdate()
    {
        _settings.AutoUpdate = !_settings.AutoUpdate;
        _settings.Save();
        if (_settings.AutoUpdate && Updater.Configured) { _updateTimer.Interval = TimeSpan.FromSeconds(5); _updateTimer.Start(); }
        else _updateTimer.Stop();
    }

    /// <summary>
    /// Кольцо стоит в своей колонке справа. У трея колонка ровно в ширину кнопки трея, а отступ справа нулевой:
    /// кольцо встаёт как ещё один значок — с тем же шагом, что и значки в трее.
    /// </summary>
    void ApplyDockPadding(bool docked, double scale)
    {
        double column = docked && _trayButton > 0 ? _trayButton / scale : 24;
        if (RingColumn.Width.Value != column) RingColumn.Width = new GridLength(column);
        var p = new Thickness(10, 0, docked ? 0 : 6, 0);
        if (Root.Padding != p) Root.Padding = p;
    }

    void ToggleDock()
    {
        _settings.DockToTray = !_settings.DockToTray;
        _settings.Save();
        _trayLeft = null;
        if (_settings.DockToTray) RefreshTray();
        else PlaceWindow();
    }

    /// <summary>Раз в секунду в фоне ищем границу трея; если она сдвинулась — переставляем строку.</summary>
    void RefreshTray()
    {
        if (!_settings.DockToTray || _trayBusy) return;
        _trayBusy = true;
        Task.Run(TrayLocator.FindTray).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
        {
            _trayBusy = false;
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } tray) return;
            if (Diag.Enabled && tray.Left != _dbgTray)
            {
                _dbgTray = tray.Left;
                Diag.Write($"Tray read {tray.Left} (btn {tray.Button}), accepted {_trayLeft}, fg {Native.ForegroundInfo()}, shell {Native.IsShellOverlayForeground()}, cursorOverTray {CursorOverTray(tray.Left)}");
            }
            if (tray.Button == _trayButton) _buttonOddHits = 0; // стрелка обычной ширины — счётчик заново
            if (tray.Left == _trayLeft && tray.Button == _trayButton) return;

            // Граница «дрожит» на 1–2 пикселя: при открытом «Пуске»/поиске Windows не отдаёт кнопку-стрелку
            // и край берётся от общего контейнера трея (на 1 px правее), а дробные координаты округляются
            // то вверх, то вниз. Настоящий новый или исчезнувший значок сдвигает трей на десятки пикселей,
            // поэтому такие мелкие изменения не считаем сдвигом.
            // Стрелка «скрытые значки» при наведении и нажатии раздувается (40 → 57 → 71 px) и сдвигает
            // левый край трея влево — из-за этого виджет уезжал влево всё дальше при каждом клике.
            // Пока ширина стрелки не такая, как обычно, или открыто окно скрытых значков, — не двигаемся.
            // (Если другая ширина держится ~4 с подряд — например, сменился масштаб экрана, — принимаем её.)
            if (_trayLeft != null && _trayButton > 0 && tray.Button > 0 && tray.Button != _trayButton && ++_buttonOddHits < 10) return;
            _buttonOddHits = 0;
            if (_trayLeft != null && Native.IsTrayOverflowForeground()) return;

            if (_trayLeft is int cur && Math.Abs(tray.Left - cur) <= 2)
            {
                if (_trayButton == 0 && tray.Button > 0) { _trayButton = tray.Button; if (!_dragging) PlaceWindow(); }
                return;
            }

            // Пока мышь над треем, Windows перестраивает его (подсветка, стрелка «скрытые значки»,
            // всплывающие окна) — из-за этого кольцо скакало, поэтому в это время границу не трогаем.
            // В остальное время значок появился или исчез — сдвигаемся сразу.
            if (_trayLeft != null && CursorOverTray(Math.Min(tray.Left, _trayLeft.Value))) return;
            // Трей «сузился», пока активна панель задач, её меню, «Пуск», Win+Tab и т. п., — скорее всего,
            // Windows просто перестраивает трей (из-за этого виджет съезжал вправо). Ждём, пока это закроется.
            // Если трей стал шире (новый значок) — сдвигаемся сразу, чтобы не перекрыть его.
            // (Скачки на 1 px после «Пуска» отсекаются выше, поэтому исчезнувший значок принимаем сразу.)
            if (_trayLeft != null && tray.Left > _trayLeft.Value && Native.IsShellOverlayForeground()) return;
            if (Diag.Enabled) Diag.Write($"Tray accepted {tray.Left} (was {_trayLeft}), fg {Native.ForegroundInfo()}");
            _trayLeft = tray.Left;
            if (tray.Button > 0 || _trayButton == 0) _trayButton = tray.Button; // стрелку не нашли — помним прежнюю ширину
            if (!_dragging) PlaceWindow();
        })));
    }

    /// <summary>Курсор над правой частью панели задач (трей и немного левее).</summary>
    static bool CursorOverTray(int trayLeft)
    {
        if (!Native.GetCursorPos(out var p) || Native.GetTaskbarRect() is not { } tb) return false;
        return p.Y >= tb.Top && p.Y <= tb.Bottom && p.X >= trayLeft - 60 && p.X <= tb.Right;
    }

    void ToggleLock()
    {
        _settings.Locked = !_settings.Locked;
        _settings.Save();
        Root.Cursor = _settings.Locked ? Cursors.Arrow : Cursors.SizeWE;
    }

    void SetLanguage(string lang)
    {
        _settings.Language = lang;
        _settings.Save();
        L.Set(lang);
        OnTrackChanged(); // re-show the status in the new language
    }

    void Shift(int deltaMs)
    {
        if (_track == null) return;
        var key = _track.Key;
        int v = deltaMs == 0 ? 0 : _settings.TrackOffsets.GetValueOrDefault(key) + deltaMs;
        if (v == 0) _settings.TrackOffsets.Remove(key);
        else _settings.TrackOffsets[key] = v;
        _settings.Save();

        if (_lines == null) return;
        _shownIndex = int.MinValue; // строка пересчитается с новым сдвигом
    }

    void MoveVertical(double deltaDip)
    {
        _settings.OffsetY = Math.Clamp(_settings.OffsetY + deltaDip, -20, 20);
        _settings.Save();
        ApplyOffsetY();
    }

    void ApplyOffsetY() => Stage.RenderTransform = new TranslateTransform(0, _settings.OffsetY);

    void Resize(int deltaDip)
    {
        _settings.Width = Math.Clamp(_settings.Width + deltaDip, 160, 1400);
        _settings.Save();
        PlaceWindow();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FitText(_active)));
    }

    void SetFont(int delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 10, 26);
        ApplyFont(_settings.FontFamily);
        _settings.Save();
    }

    void SetTheme(ThemeMode mode)
    {
        _settings.Theme = mode;
        _settings.Save();
        ApplyTheme();
    }

    void OpenLyricsFolder()
    {
        try
        {
            var dir = _settings.LyricsFolderResolved;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex.ToString()); }
    }
}
