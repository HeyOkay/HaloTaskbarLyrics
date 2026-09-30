using System.Globalization;

namespace TaskbarLyrics;

/// <summary>Interface strings. English is the default, Russian is optional.</summary>
public static class L
{
    public static string Lang { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public static void Set(string? lang)
    {
        Lang = lang == "ru" ? "ru" : "en";
        Culture = CultureInfo.GetCultureInfo(Lang == "ru" ? "ru-RU" : "en-US");
    }

    public static string S(string key) =>
        T.TryGetValue(key, out var v) ? (Lang == "ru" ? v.Ru : v.En) : key;

    public static string F(string key, params object[] args) => string.Format(Culture, S(key), args);

    static readonly Dictionary<string, (string En, string Ru)> T = new()
    {
        // Menu
        ["sync"] = ("Sync", "Синхронизация"),
        ["late"] = ("Lyrics are late (+0.5 s)", "Текст отстаёт (+0,5 с)"),
        ["early"] = ("Lyrics are early (−0.5 s)", "Текст спешит (−0,5 с)"),
        ["resetOffset"] = ("Reset offset", "Сбросить сдвиг"),
        ["ahead"] = ("Show next line early", "Показывать строку заранее"),
        ["aheadOff"] = ("Off", "не заранее"),
        ["aheadSec"] = ("{0:0.##} s early", "за {0:0.##} с"),
        ["textStyle"] = ("Text", "Текст"),
        ["position"] = ("Position and size", "Положение и размер"),
        ["effects"] = ("Effects", "Эффекты"),
        ["fontSize"] = ("Text size", "Размер текста"),
        ["width"] = ("Width", "Ширина"),
        ["offsetY"] = ("Height on the taskbar", "Высота на панели"),
        ["font"] = ("Font…", "Шрифт…"),
        ["bold"] = ("Bold", "Жирный"),
        ["letterIn"] = ("New line appears", "Появление строки"),
        ["letterOff"] = ("Whole line at once", "Строка целиком"),
        ["letterFloat"] = ("Letters float in at an angle", "Буквы всплывают под углом"),
        ["letterRise"] = ("Letters rise in a wave", "Буквы поднимаются волной"),
        ["accentSung"] = ("Sung words in accent color", "Пропетые слова цветом акцента"),
        ["glow"] = ("Glow on sung text", "Свечение пропетого текста"),
        ["align"] = ("Alignment", "Выравнивание"),
        ["alignLeft"] = ("Left", "По левому краю"),
        ["alignCenter"] = ("Center", "По центру"),
        ["alignRight"] = ("Right", "По правому краю"),
        ["visualizer"] = ("Ring and glow breathe with the music", "Кольцо и свечение дышат под музыку"),
        ["theme"] = ("Theme", "Тема"),
        ["themeSystem"] = ("Same as Windows", "Как в Windows"),
        ["themeLight"] = ("Light", "Светлая"),
        ["themeDark"] = ("Dark", "Тёмная"),
        ["lyrics"] = ("Lyrics", "Тексты песен"),
        ["reload"] = ("Reload lyrics", "Перезагрузить текст"),
        ["openLrc"] = ("Open my .lrc folder", "Открыть папку со своими .lrc"),
        ["lock"] = ("Lock position", "Закрепить на месте"),
        ["dockTray"] = ("Stick to the system tray", "Прижать к трею"),
        ["autostart"] = ("Start with Windows", "Запускать вместе с Windows"),
        ["startButton"] = ("Custom Start button", "Своя кнопка «Пуск»"),
        ["language"] = ("Language", "Язык"),
        ["exit"] = ("Exit", "Выход"),
        ["updates"] = ("Updates", "Обновления"),
        ["updateCheck"] = ("Check for updates now", "Проверить обновления сейчас"),
        ["updateAuto"] = ("Check automatically", "Проверять автоматически"),
        ["updateInstall"] = ("Update to version {0}", "Обновить до версии {0}"),
        ["updateTip"] = ("Update available: version {0} (right-click to install)", "Доступно обновление: версия {0} (правый клик — установить)"),
        ["updateLatest"] = ("You have the latest version ({0}).", "У вас последняя версия ({0})."),
        ["updateFailed"] = ("Could not check for updates. Check your internet connection and try again.", "Не удалось проверить обновления. Проверьте подключение к интернету и попробуйте ещё раз."),
        ["updateInstallFailed"] = ("Could not install the update: {0}", "Не удалось установить обновление: {0}"),
        ["diagnostics"] = ("Diagnostics", "Диагностика"),
        ["diagLog"] = ("Write diagnostic log", "Записывать журнал диагностики"),
        ["diagOpen"] = ("Show the log file", "Показать файл журнала"),
        ["flyoutReturn"] = ("Panel: back to the current line in", "Панель: к текущей строке через"),
        ["seconds"] = ("{0:0.#} s", "{0:0.#} с"),

        // Lyrics panel (click on the ring)
        ["flyoutNothing"] = ("Nothing is playing", "Сейчас ничего не играет"),
        ["share"] = ("Share", "Поделиться"),
        ["linkCopied"] = ("Link copied", "Ссылка скопирована"),
        ["copyFailed"] = ("Couldn't copy — the clipboard is busy", "Не удалось скопировать — буфер обмена занят"),
        ["shuffle"] = ("Shuffle", "Перемешать"),
        ["previous"] = ("Previous", "Предыдущий трек"),
        ["next"] = ("Next", "Следующий трек"),
        ["play"] = ("Play", "Играть"),
        ["pause"] = ("Pause", "Пауза"),
        ["repeatOff"] = ("Repeat: off", "Повтор: выключен"),
        ["repeatAll"] = ("Repeat: all", "Повтор: все треки"),
        ["repeatOne"] = ("Repeat: this track", "Повтор: этот трек"),
        ["ringTip"] = ("Click the ring to open the lyrics panel", "Клик по кольцу — панель с текстом"),

        // Statuses (tooltips)
        ["waiting"] = ("HaloTaskbarLyrics — waiting for music", "HaloTaskbarLyrics — жду музыку"),
        ["noSmtc"] = ("Can't access Windows media sessions", "Нет доступа к медиа-сессиям Windows"),
        ["searching"] = ("searching for lyrics…", "ищу текст…"),
        ["notFound"] = ("lyrics not found", "текст не найден"),
        ["instrumental"] = ("instrumental", "инструментал"),
        ["untimed"] = ("only unsynced lyrics are available", "есть только текст без таймингов"),
        ["netError"] = ("couldn't load lyrics (no internet?)", "не удалось загрузить текст (нет интернета?)"),
        ["noTimeline"] = ("the player doesn't report the playback position", "плеер не сообщает позицию воспроизведения"),
        ["source"] = ("Lyrics source: {0}", "Источник текста: {0}"),
        ["localSource"] = ("your .lrc file", "ваш .lrc-файл"),
        ["offset"] = ("offset {0:+0.0;-0.0;0} s", "сдвиг {0:+0.0;-0.0;0} с"),

        // Font picker
        ["pickerTitle"] = ("Font — HaloTaskbarLyrics", "Шрифт — HaloTaskbarLyrics"),
        ["searchFonts"] = ("Search fonts…", "Поиск шрифта…"),
        ["preview"] = ("Lyrics on your taskbar ♪ 0123", "Текст песни на панели задач ♪ 0123"),
        ["addFromFile"] = ("Add from file…", "Добавить из файла…"),
        ["folder"] = ("Folder", "Папка"),
        ["folderTip"] = ("Open the folder with the fonts you added", "Открыть папку, где хранятся добавленные вами шрифты"),
        ["cancel"] = ("Cancel", "Отмена"),
        ["select"] = ("Select", "Выбрать"),
        ["defaultNote"] = ("default", "по умолчанию"),
        ["userNote"] = ("added by you", "добавлен вами"),
        ["addFontTitle"] = ("Add font", "Добавить шрифт"),
        ["fontFilter"] = ("Fonts (*.ttf, *.otf, *.ttc)|*.ttf;*.otf;*.ttc", "Шрифты (*.ttf, *.otf, *.ttc)|*.ttf;*.otf;*.ttc"),
        ["addFailed"] = ("Couldn't add “{0}”:\n{1}", "Не получилось добавить «{0}»:\n{1}"),
        ["fontCaption"] = ("Font", "Шрифт"),
        ["errNotFont"] = ("A .ttf, .otf or .ttc file is required", "Нужен файл .ttf, .otf или .ttc"),
        ["errNoFamilies"] = ("No fonts were found in the file", "В файле не найдено ни одного шрифта"),
    };
}
