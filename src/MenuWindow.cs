using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TaskbarLyrics;

/// <summary>One menu row (or a separator).</summary>
public sealed class MenuEntry
{
    public string Header { get; init; } = "";
    public Action? Action { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    public List<MenuEntry>? Children { get; init; }
    /// <summary>Stepper row: current value shown between "−" and "+" buttons; the menu stays open.</summary>
    public Func<string>? Value { get; init; }
    public Action? Decrease { get; init; }
    public Action? Increase { get; init; }

    public static MenuEntry Separator => new() { IsSeparator = true };
}

/// <summary>
/// Context menu as a separate window. On Windows 11 it gets the real system acrylic and
/// rounded, anti-aliased corners (a WPF ContextMenu can't do that — its corners stay square).
/// On Windows 10 it falls back to a blurred rectangle.
/// </summary>
public sealed class MenuWindow : Window
{
    static MenuWindow? _open;

    readonly MenuWindow? _parent;
    readonly bool _win11 = Native.WindowsBuild >= 22000;
    readonly Border _root;
    MenuWindow? _child;
    Border? _childRow;
    bool _closing;

    public static bool IsOpen => _open != null;
    public static void CloseOpen() => _open?.CloseAll();

    /// <summary>Opens the menu next to the mouse cursor (above it if the taskbar is at the bottom).</summary>
    public static void ShowAtCursor(List<MenuEntry> entries)
    {
        CloseOpen();
        var w = new MenuWindow(entries, null);
        _open = w;
        Native.GetCursorPos(out var p);
        w.ShowNear(p.X, p.Y);
    }

    MenuWindow(List<MenuEntry> entries, MenuWindow? parent)
    {
        _parent = parent;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = parent == null;
        // Windows 11: normal window + acrylic + system rounded corners. Windows 10: transparent window + blur.
        AllowsTransparency = !_win11;
        Background = Brushes.Transparent;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "MenuForeground");
        Left = -32000;
        Top = -32000;

        var panel = new StackPanel();
        foreach (var e in entries)
            panel.Children.Add(e.IsSeparator ? MakeSeparator() : e.Value != null ? MakeStepper(e) : MakeRow(e));

        _root = new Border { Padding = new Thickness(0, 4, 0, 4), MinWidth = 230, Child = panel };
        if (!_win11)
        {
            _root.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            _root.BorderThickness = new Thickness(1);
        }
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            if (_win11)
            {
                if (HwndSource.FromHwnd(h) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
                Native.ApplyWin11Backdrop(h, dark: !Theme.IsLight, Theme.BlurTint);
            }
            else
            {
                Native.EnableBlur(h, Theme.BlurTint, 0);
            }
        };

        Loaded += (_, _) => _root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        // Click outside the menu → close everything
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!RootMenu.AnyActive()) RootMenu.CloseAll();
        }));

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { RootMenu.CloseAll(); e.Handled = true; }
        };
    }

    MenuWindow RootMenu => _parent?.RootMenu ?? this;

    bool AnyActive()
    {
        for (var w = this; w != null; w = w._child)
            if (w.IsActive) return true;
        return false;
    }

    void CloseAll() => RootMenu.CloseChain();

    void CloseChain()
    {
        _child?.CloseChain();
        _child = null;
        if (_closing) return;
        _closing = true;
        if (_open == this) _open = null;
        Close();
    }

    // ---------- Rows ----------

    static FrameworkElement MakeSeparator()
    {
        var b = new Border { Height = 1, Margin = new Thickness(12, 4, 12, 4) };
        b.SetResourceReference(Border.BackgroundProperty, "MenuSeparator");
        return b;
    }

    FrameworkElement MakeRow(MenuEntry e)
    {
        var icons = (FontFamily)Application.Current.Resources["IconFont"];

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new TextBlock
        {
            Text = "", FontFamily = icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Visibility = e.Checked ? Visibility.Visible : Visibility.Hidden,
        };
        var text = new TextBlock { Text = e.Header, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new TextBlock
        {
            Text = "", FontFamily = icons, FontSize = 10, Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = e.Children != null ? Visibility.Visible : Visibility.Collapsed,
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "MenuForegroundDim");
        Grid.SetColumn(text, 1);
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(check);
        grid.Children.Add(text);
        grid.Children.Add(arrow);

        var row = new Border
        {
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 1, 4, 1),
            Padding = new Thickness(8, 6, 10, 6),
            Background = Brushes.Transparent,
            Child = grid,
        };

        if (!e.Enabled)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "MenuForegroundDim");
            return row;
        }

        row.MouseEnter += (_, _) =>
        {
            row.SetResourceReference(Border.BackgroundProperty, "MenuHover");
            if (_childRow != null && _childRow != row) CloseChild();
            if (e.Children != null) OpenChild(row, e.Children);
        };
        row.MouseLeave += (_, _) =>
        {
            if (_childRow != row) row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (e.Children != null) { OpenChild(row, e.Children); return; }
            var action = e.Action;
            CloseAll();
            action?.Invoke();
        };
        return row;
    }

    /// <summary>"Text size   −  16  +" — clicks and the mouse wheel change the value without closing the menu.</summary>
    FrameworkElement MakeStepper(MenuEntry e)
    {
        var icons = (FontFamily)Application.Current.Resources["IconFont"];

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = e.Header, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock
        {
            Text = e.Value!(), MinWidth = 34, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "MenuForegroundDim");

        void Step(Action? a)
        {
            try { a?.Invoke(); } catch (Exception ex) { App.Log(ex.ToString()); }
            value.Text = e.Value!();
        }

        Border Button(string glyph, Action? a)
        {
            var b = new Border
            {
                Width = 28, Height = 26, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = glyph, FontFamily = icons, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "MenuHover");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, args) => { Step(a); args.Handled = true; };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        buttons.Children.Add(Button("\uE738", e.Decrease)); // −
        buttons.Children.Add(value);
        buttons.Children.Add(Button("\uE710", e.Increase)); // +

        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        var row = new Border
        {
            Margin = new Thickness(4, 1, 4, 1),
            Padding = new Thickness(8, 2, 4, 2),
            Background = Brushes.Transparent,
            Child = grid,
        };
        row.MouseEnter += (_, _) => { if (_childRow != null) CloseChild(); };
        row.MouseWheel += (_, args) => { Step(args.Delta > 0 ? e.Increase : e.Decrease); args.Handled = true; };
        return row;
    }

    void CloseChild()
    {
        _child?.CloseChain();
        _child = null;
        if (_childRow != null) _childRow.Background = Brushes.Transparent;
        _childRow = null;
    }

    void OpenChild(Border row, List<MenuEntry> entries)
    {
        if (_childRow == row && _child != null) return;
        CloseChild();
        _childRow = row;
        row.SetResourceReference(Border.BackgroundProperty, "MenuHover");

        var child = new MenuWindow(entries, this);
        _child = child;
        var rowTop = row.TranslatePoint(new Point(0, 0), this).Y;
        child.ShowBeside(Left, Left + ActualWidth, Top + rowTop - 5);
    }

    // ---------- Positioning (DIPs) ----------

    /// <summary>
    /// Как у меню Windows 11 на панели задач: над панелью с небольшим зазором и по центру
    /// относительно места клика. Панель слева/справа/сверху — меню рядом с ней с той же стороны.
    /// </summary>
    void ShowNear(int cursorX, int cursorY)
    {
        const double Gap = 8; // зазор между панелью задач и меню, DIP
        Show();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double x = cursorX / sx, y = cursorY / sy;
        var wa = SystemParameters.WorkArea;
        double w = ActualWidth, h = ActualHeight;

        double left = x - w / 2, top = y - h - Gap;
        if (Native.GetTaskbarRect() is { } tb)
        {
            double tl = tb.Left / sx, tt = tb.Top / sy, tr = tb.Right / sx, tbm = tb.Bottom / sy;
            bool horizontal = tb.Width >= tb.Height;
            if (horizontal && tt > wa.Top) { left = x - w / 2; top = tt - h - Gap; }            // панель внизу
            else if (horizontal) { left = x - w / 2; top = tbm + Gap; }                          // панель вверху
            else if (tl > wa.Left) { left = tl - w - Gap; top = y - h / 2; }                     // панель справа
            else { left = tr + Gap; top = y - h / 2; }                                           // панель слева
        }

        // Не выходим за края экрана
        Left = Math.Max(wa.Left + Gap, Math.Min(left, wa.Right - w - Gap));
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - h));
        Activate();
    }

    void ShowBeside(double parentLeft, double parentRight, double top)
    {
        Show();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        double w = ActualWidth, h = ActualHeight;

        double left = parentRight - 2;
        if (left + w > wa.Right) left = parentLeft - w + 2; // no room on the right — open to the left
        if (top + h > wa.Bottom) top = wa.Bottom - h;

        Left = Math.Max(wa.Left, left);
        Top = Math.Max(wa.Top, top);
    }
}
