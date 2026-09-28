using Windows.Media.Control;

namespace TaskbarLyrics;

public sealed record TrackInfo(string Artist, string Title, string Album, TimeSpan Duration, string Source)
{
    public string Key => (Artist + "\u001f" + Title).ToLowerInvariant();
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

public sealed record MediaSnapshot(TrackInfo Track, TimeSpan Position, bool Playing, double Rate, bool HasTimeline);

/// <summary>
/// Читает, что сейчас играет, через System Media Transport Controls (SMTC) —
/// тот же механизм, что показывает трек в оверлее громкости Windows.
/// Работает с любым плеером, который публикует себя в SMTC:
/// Spotify, Яндекс Музыка, браузеры (YouTube, VK, SoundCloud…), Apple Music, WMP и др.
/// </summary>
public sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    string? _lastSource;

    public async Task StartAsync() =>
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    public async Task<MediaSnapshot?> PollAsync()
    {
        if (_mgr == null) return null;
        var s = PickSession();
        if (s == null) return null;
        _lastSource = s.SourceAppUserModelId;

        var props = await s.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

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
        return new MediaSnapshot(track, pos, playing, rate, hasTimeline);
    }

    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        var sessions = _mgr!.GetSessions();
        static bool Playing(GlobalSystemMediaTransportControlsSession x) =>
            x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        // 1) текущая сессия Windows, если играет; 2) прошлый источник, если играет;
        // 3) любая играющая; 4) прошлый источник на паузе; 5) текущая
        var cur = _mgr.GetCurrentSession();
        if (cur != null && Playing(cur)) return cur;
        var last = sessions.FirstOrDefault(x => x.SourceAppUserModelId == _lastSource && Playing(x));
        if (last != null) return last;
        var any = sessions.FirstOrDefault(Playing);
        if (any != null) return any;
        return sessions.FirstOrDefault(x => x.SourceAppUserModelId == _lastSource) ?? cur;
    }
}
