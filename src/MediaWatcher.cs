using Windows.Foundation;
using Windows.Media.Control;

namespace TaskbarLyrics;

public sealed record TrackInfo(string Artist, string Title, string Album, TimeSpan Duration, string Source)
{
    public string Key => (Artist + "\u001f" + Title).ToLowerInvariant();
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

public sealed record MediaSnapshot(TrackInfo Track, TimeSpan Position, bool Playing, double Rate, bool HasTimeline)
{
    /// <summary>Что плеер разрешает делать через Windows (кнопки в панели с текстом).</summary>
    public MediaControlsInfo Controls { get; init; } = MediaControlsInfo.None;
}

/// <summary>
/// Возможности плеера, как он сам их сообщает Windows (SMTC). Shuffle и Repeat есть не у всех плееров —
/// их кнопки показываются, только если плеер их поддерживает. Shuffle/Repeat — текущее состояние (null — неизвестно).
/// </summary>
public sealed record MediaControlsInfo(bool CanPlayPause, bool CanPrevious, bool CanNext, bool CanSeek,
    bool CanShuffle, bool CanRepeat, bool? Shuffle, global::Windows.Media.MediaPlaybackAutoRepeatMode? Repeat)
{
    public static MediaControlsInfo None { get; } = new(false, false, false, false, false, false, null, null);
}

/// <summary>
/// Читает, что сейчас играет, через System Media Transport Controls (SMTC) —
/// тот же механизм, что показывает трек в оверлее громкости Windows.
/// Работает с любым плеером, который публикует себя в SMTC:
/// Spotify, Яндекс Музыка, браузеры (YouTube, VK, SoundCloud…), Apple Music, WMP и др.
/// </summary>
public sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session; // сессия, которую показываем (для кнопок панели)
    string? _lastSource;
    string? _lastTitle;   // название трека последнего плеера — чтобы на паузе найти именно его вкладку

    public async Task StartAsync() =>
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    public async Task<MediaSnapshot?> PollAsync()
    {
        if (_mgr == null) return null;
        var s = await PickSessionAsync();
        _session = s;
        if (s == null) return null;
        _lastSource = s.SourceAppUserModelId;

        var props = await s.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
        _lastTitle = props.Title.Trim();

        var tl = s.GetTimelineProperties();
        var pb = s.GetPlaybackInfo();
        bool playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double rate = pb?.PlaybackRate ?? 1.0;
        if (rate <= 0) rate = 1.0;

        var dur = tl.EndTime - tl.StartTime;
        var pos = tl.Position - tl.StartTime;
        bool hasTimeline = dur > TimeSpan.Zero;

        // Плееры обновляют позицию редко — досчитываем от момента последнего обновления
        if (playing && hasTimeline)
        {
            var elapsed = DateTimeOffset.Now - tl.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < dur) pos += elapsed * rate;
        }
        if (hasTimeline && pos > dur) pos = dur;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

        var artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
        var track = new TrackInfo(artist.Trim(), props.Title.Trim(), props.AlbumTitle ?? "", dur, s.SourceAppUserModelId);
        var c = pb?.Controls;
        var controls = c == null ? MediaControlsInfo.None : new MediaControlsInfo(
            c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled,
            c.IsPreviousEnabled, c.IsNextEnabled, c.IsPlaybackPositionEnabled && hasTimeline,
            c.IsShuffleEnabled, c.IsRepeatEnabled, pb!.IsShuffleActive, pb.AutoRepeatMode);
        return new MediaSnapshot(track, pos, playing, rate, hasTimeline) { Controls = controls };
    }

    // ---------------- Управление плеером (кнопки панели с текстом) ----------------

    /// <summary>Выполняет команду для текущего плеера. false — плеер отказался или его уже нет.</summary>
    async Task<bool> Command(string name, Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        var s = _session;
        if (s == null) { Diag.Write($"Command {name}: no player"); return false; }
        try
        {
            bool ok = await command(s);
            // В журнал: кому ушла команда и что в этот момент было с остальными плеерами
            if (Diag.Enabled)
            {
                var others = string.Join("; ", _mgr!.GetSessions().Select(x =>
                    $"{x.SourceAppUserModelId}{(ReferenceEquals(x, s) ? " [target]" : "")} {x.GetPlaybackInfo()?.PlaybackStatus}"));
                Diag.Write($"Command {name} → {s.SourceAppUserModelId} \"{_lastTitle}\": {(ok ? "accepted" : "refused")}. Sessions: {others}");
            }
            return ok;
        }
        catch (Exception ex) { App.Log($"Media command {name}: {ex.Message}"); return false; }
    }

    public Task<bool> PlayPauseAsync(bool playing) => Command(playing ? "pause" : "play", s =>
        s.GetPlaybackInfo()?.Controls?.IsPlayPauseToggleEnabled == true ? s.TryTogglePlayPauseAsync()
        : playing ? s.TryPauseAsync() : s.TryPlayAsync());

    public Task<bool> NextAsync() => Command("next", s => s.TrySkipNextAsync());
    public Task<bool> PreviousAsync() => Command("previous", s => s.TrySkipPreviousAsync());
    public Task<bool> SetShuffleAsync(bool on) => Command("shuffle " + on, s => s.TryChangeShuffleActiveAsync(on));
    public Task<bool> SetRepeatAsync(global::Windows.Media.MediaPlaybackAutoRepeatMode mode) =>
        Command("repeat " + mode, s => s.TryChangeAutoRepeatModeAsync(mode));

    /// <summary>Перемотка на позицию от начала трека.</summary>
    public Task<bool> SeekAsync(TimeSpan position) => Command($"seek {position:m\\:ss}", s =>
        s.TryChangePlaybackPositionAsync((s.GetTimelineProperties().StartTime + position).Ticks));

    /// <summary>Картинка обложки и её отпечаток (SHA-256).</summary>
    public sealed record CoverImage(byte[] Bytes, string Hash);

    // Чьи это обложки: «отпечаток на глаз» картинки → трек, у которого она появилась впервые (и его альбом)
    readonly List<(ulong Look, string Track, string Album, string Artist)> _coverOwners = new();
    const int SameLookBits = 6; // из 64: отличие не больше — это та же картинка (пересжатая, чуть другого размера)

    /// <summary>
    /// Обложка трека t. Некоторые плееры (особенно браузеры), когда у нового трека обложки нет, продолжают
    /// отдавать картинку прошлого трека — иногда заново пересжатую, то есть байты другие, а картинка та же.
    /// Поэтому картинки сравниваются «на глаз»: уменьшаем до 9×8 в оттенках серого и сравниваем, где светлее,
    /// где темнее. Запоминаем, у какого трека картинка появилась впервые; чужую (у другого трека и не из того же
    /// альбома) не отдаём — будет заглушка. Всё записывается в журнал диагностики.
    /// </summary>
    public async Task<CoverImage?> GetCoverAsync(TrackInfo t)
    {
        var bytes = await GetThumbnailAsync(t.Title);
        if (bytes == null) return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var (look, size) = LookOf(bytes);
        string who = $"\"{t.Artist}\" — \"{t.Title}\"";
        if (look is not ulong h)
        {
            Diag.Write($"Cover {who}: {bytes.Length} bytes, {size}, can't read the picture — shown as is");
            return new CoverImage(bytes, hash);
        }

        int mine = _coverOwners.FindIndex(o => o.Track == t.Key && Distance(o.Look, h) <= SameLookBits);
        if (mine >= 0)
        {
            Diag.Write($"Cover {who}: {bytes.Length} bytes, {size}, look {h:X16} — this track's cover, shown");
            return new CoverImage(bytes, hash);
        }
        int other = _coverOwners.FindIndex(o => o.Track != t.Key && Distance(o.Look, h) <= SameLookBits);
        if (other >= 0)
        {
            var o = _coverOwners[other];
            // Тот же альбом — только если совпадает и исполнитель: сайты часто пишут в «альбом» название
            // плейлиста («Треки», «Мне нравится»), и тогда у разных исполнителей «один альбом»
            bool sameAlbum = t.Album.Length > 0 && string.Equals(o.Album, t.Album, StringComparison.OrdinalIgnoreCase)
                && SameArtist(o.Artist, t.Artist);
            Diag.Write($"Cover {who}: {bytes.Length} bytes, {size}, look {h:X16} — like the cover of another track " +
                       $"(differs by {Distance(o.Look, h)} of 64, album \"{o.Album}\") — {(sameAlbum ? "same album, shown" : "stale, NOT shown")}");
            if (!sameAlbum) return null;
        }
        else Diag.Write($"Cover {who}: {bytes.Length} bytes, {size}, look {h:X16} — new picture, shown");

        if (_coverOwners.Count > 2000) _coverOwners.RemoveRange(0, 1000);
        _coverOwners.Add((h, t.Key, t.Album, t.Artist));
        return new CoverImage(bytes, hash);
    }

    internal static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Тот же исполнитель, с допуском на опечатки сайтов: «Tears For Fear» = «Tears For Fears».
    /// Сравниваем только буквы и цифры; разница до 2 букв (у коротких имён — только точное совпадение).
    /// </summary>
    static bool SameArtist(string a, string b)
    {
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var x = Norm(a);
        var y = Norm(b);
        if (x == y) return true;
        if (Math.Min(x.Length, y.Length) < 6) return false;
        return EditDistance(x, y) <= 2;
    }

    /// <summary>Сколько букв надо вставить, удалить или заменить, чтобы из a получить b.</summary>
    static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// «Отпечаток на глаз» (dHash): картинка 9×8 в оттенках серого, 64 бита — светлее ли каждая точка соседней справа.
    /// У одной и той же картинки после пересжатия или другого размера отпечаток почти не меняется.
    /// Второе значение — размер картинки для журнала.
    /// </summary>
    internal static (ulong? Look, string Size) LookOf(byte[] bytes)
    {
        string size = "?";
        try
        {
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(bytes),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            size = $"{frame.PixelWidth}x{frame.PixelHeight}";

            var small = new System.Windows.Media.Imaging.BitmapImage();
            small.BeginInit();
            small.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            small.DecodePixelWidth = 9;
            small.DecodePixelHeight = 8;
            small.StreamSource = new MemoryStream(bytes);
            small.EndInit();
            var gray = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Gray8, null, 0);
            if (gray.PixelWidth != 9 || gray.PixelHeight != 8) return (null, size);
            var px = new byte[9 * 8];
            gray.CopyPixels(px, 9, 0);
            ulong look = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    look <<= 1;
                    if (px[y * 9 + x] < px[y * 9 + x + 1]) look |= 1;
                }
            return (look, size);
        }
        catch { return (null, size); }
    }

    /// <summary>
    /// Обложка текущего трека (байты картинки) или null, если плеер её не отдаёт.
    /// title — название трека, для которого нужна обложка: если плеер уже переключился на другой трек, — null.
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(string? title = null)
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (title != null && !string.Equals(props?.Title?.Trim(), title, StringComparison.Ordinal))
            {
                Diag.Write($"Cover \"{title}\": the player already shows another track (\"{props?.Title}\") — skipped");
                return null;
            }
            if (props?.Thumbnail is not { } reference)
            {
                Diag.Write($"Cover \"{title}\": the player gives no picture — placeholder");
                return null;
            }
            using var ras = await reference.OpenReadAsync();
            using var input = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch (Exception ex) { Diag.Write("Cover: " + ex.Message); return null; }
    }

    async Task<GlobalSystemMediaTransportControlsSession?> PickSessionAsync()
    {
        var sessions = _mgr!.GetSessions();
        static bool Playing(GlobalSystemMediaTransportControlsSession x) =>
            x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        // 0) та же сессия, что и в прошлый раз, если всё ещё играет. Иначе, когда играют две вкладки браузера
        //    (музыка и, например, беззвучное видео на другом сайте), Windows то и дело переключает «текущую»,
        //    и трек с текстом прыгал туда-обратно каждые несколько секунд.
        // 1) текущая сессия Windows, если играет; 2) прошлый источник, если играет;
        // 3) любая играющая; 4) прошлый источник на паузе; 5) текущая
        var same = _session == null ? null : sessions.FirstOrDefault(x => ReferenceEquals(x, _session));
        if (same != null && Playing(same)) return same;
        var cur = _mgr.GetCurrentSession();
        if (cur != null && Playing(cur)) return cur;
        var last = sessions.FirstOrDefault(x => x.SourceAppUserModelId == _lastSource && Playing(x));
        if (last != null) return last;
        var any = sessions.FirstOrDefault(Playing);
        if (any != null) return any;

        // Ничего не играет (пауза). Держимся за ТОТ ЖЕ плеер, что был: иначе у нескольких вкладок одного
        // браузера (Яндекс Музыка и ВКонтакте — обе «Chrome») бралась первая попавшаяся, и кнопка play
        // запускала музыку не там. Та же сессия — по ссылке, а если Windows её пересоздала — по названию трека
        if (same != null) return same;
        var sameApp = sessions.Where(x => x.SourceAppUserModelId == _lastSource).ToList();
        if (sameApp.Count > 1 && _lastTitle != null)
        {
            foreach (var x in sameApp)
            {
                try
                {
                    var p = await x.TryGetMediaPropertiesAsync();
                    if (string.Equals(p?.Title?.Trim(), _lastTitle, StringComparison.Ordinal)) return x;
                }
                catch { }
            }
        }
        return sameApp.FirstOrDefault() ?? cur;
    }
}
