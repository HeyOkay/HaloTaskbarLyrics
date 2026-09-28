using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics;

/// <summary>Окно выбора шрифта: системные шрифты + свои из файлов, с живым предпросмотром на панели задач.</summary>
public partial class FontPickerWindow : Window
{
    public sealed class Item
    {
        public required string Key { get; init; }   // "" — шрифт по умолчанию
        public required string Name { get; init; }
        public required FontFamily Family { get; init; }
        public string Note { get; init; } = "";
        public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    readonly Action<string?> _preview;
    readonly string? _original;
    readonly bool _bold;
    List<Item> _items = new();

    /// <summary>Выбранный ключ шрифта ("" — по умолчанию); null — окно закрыли без выбора.</summary>
    public string? SelectedKey { get; private set; }

    public FontPickerWindow(string? current, bool bold, Action<string?> preview)
    {
        InitializeComponent();
        Title = L.S("pickerTitle");
        Search.Tag = L.S("searchFonts");
        Preview.Text = L.S("preview");
        AddBtn.Content = L.S("addFromFile");
        FolderBtn.Content = L.S("folder");
        FolderBtn.ToolTip = L.S("folderTip");
        CancelBtn.Content = L.S("cancel");
        OkBtn.Content = L.S("select");
        _original = current;
        _preview = preview;
        _bold = bold;
        Preview.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;

        SourceInitialized += (_, _) => Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, !Theme.IsLight);

        Search.TextChanged += (_, _) => Filter();
        FontList.SelectionChanged += (_, _) =>
        {
            if (FontList.SelectedItem is not Item it) return;
            Preview.FontFamily = it.Family;
            Preview.FontWeight = FontManager.BestWeight(it.Family, _bold);
            _preview(it.Key); // сразу видно на панели задач
        };
        FontList.MouseDoubleClick += (_, _) => Accept();
        OkBtn.Click += (_, _) => Accept();
        AddBtn.Click += (_, _) => AddFromFile();
        FolderBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(FontManager.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontManager.Dir}\"") { UseShellExecute = true });
        };
        Closed += (_, _) => { if (SelectedKey == null) _preview(_original); }; // отмена — вернуть как было

        Load(current ?? "");
        Loaded += (_, _) =>
        {
            Search.Focus();
            if (FontList.SelectedItem != null) FontList.ScrollIntoView(FontList.SelectedItem);
        };
    }

    void Load(string selectKey)
    {
        var items = new List<Item>
        {
            new() { Key = "", Name = "Segoe UI Variable", Family = FontManager.Default, Note = L.S("defaultNote") },
        };
        foreach (var (key, name) in FontManager.UserFonts())
            items.Add(new Item { Key = key, Name = name, Family = FontManager.Resolve(key), Note = L.S("userNote") });
        foreach (var name in FontManager.SystemFonts())
            items.Add(new Item { Key = name, Name = name, Family = new FontFamily(name) });

        _items = items;
        Filter();
        var sel = _items.FirstOrDefault(i => i.Key == selectKey) ?? _items[0];
        FontList.SelectedItem = sel;
        FontList.ScrollIntoView(sel);
    }

    void Filter()
    {
        var q = Search.Text.Trim();
        var sel = FontList.SelectedItem;
        var view = q.Length == 0 ? _items : _items.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        FontList.ItemsSource = view;
        if (sel is Item s && view.Contains(s)) FontList.SelectedItem = s;
    }

    void Accept()
    {
        SelectedKey = (FontList.SelectedItem as Item)?.Key ?? _original ?? "";
        DialogResult = true;
    }

    void AddFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.S("addFontTitle"),
            Filter = L.S("fontFilter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        string? first = null;
        foreach (var f in dlg.FileNames)
        {
            try { first ??= FontManager.Install(f).FirstOrDefault(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("addFailed", Path.GetFileName(f), ex.Message),
                    L.S("fontCaption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        Search.Text = "";
        Load(first ?? (FontList.SelectedItem as Item)?.Key ?? "");
    }
}
