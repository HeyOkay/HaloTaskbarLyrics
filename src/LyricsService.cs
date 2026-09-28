using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

public sealed record LyricsResult(string? Synced, string? Plain, bool Instrumental, string Source = "LRCLIB");

/// <summary>
/// Цепочка источников: 1) ваши .lrc-файлы; 2) кэш на диске; 3) LRCLIB (https://lrclib.net) — открытая база без ключа API.
/// Тексты скачиваются во время работы, в программу ничего не встроено.
/// </summary>
public sealed class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly TimeSpan MissTtl = TimeSpan.FromHours(24); // «не найдено» перепроверяем раз в сутки
    const int MaxRequests = 10;

    readonly Func<string> _lyricsFolder;
    readonly Dictionary<string, LyricsResult?> _mem = new();

    sealed record CacheEntry(LyricsResult? Result, DateTimeOffset? MissAt);

    public LyricsService(Func<string> lyricsFolder) => _lyricsFolder = lyricsFolder;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(12) };
        // LRCLIB просит указывать своё приложение в User-Agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HaloTaskbarLyrics/{Updater.Current} (+{Updater.ProjectUrl})");
        return c;
    }

    public async Task<LyricsResult?> GetAsync(TrackInfo t, bool force, CancellationToken ct)
    {
        var n = TrackNormalizer.Clean(t.Artist, t.Title);

        // 1. Свои файлы — всегда первыми и без кэша, чтобы новый файл сразу подхватывался
        var folder = _lyricsFolder();
        var local = await Task.Run(() => LocalLyrics.Find(folder, n), ct);
        if (local != null) return local;

        // 2. Кэш
        var key = t.Key;
        if (!force)
        {
            if (_mem.TryGetValue(key, out var m)) return m;
            if (ReadDisk(key) is { } e)
            {
                if (e.Result != null) return _mem[key] = e.Result;
                if (e.MissAt is { } at && DateTimeOffset.Now - at < MissTtl) return _mem[key] = null;
            }
        }

        // 3. LRCLIB
        var r = await FetchAsync(t, n, ct);
        _mem[key] = r;
        WriteDisk(key, new CacheEntry(r, r == null ? DateTimeOffset.Now : null));
        return r;
    }

    async Task<LyricsResult?> FetchAsync(TrackInfo t, TrackNormalizer.Normalized n, CancellationToken ct)
    {
        static string E(string s) => Uri.EscapeDataString(s);
        double dur = t.Duration.TotalSeconds;

        // Точное совпадение по исполнителю + названию + альбому + длительности
        if (n.Artist.Length > 0 && !string.IsNullOrWhiteSpace(t.Album) && dur > 1)
        {
            var exact = await GetOneAsync(
                $"api/get?artist_name={E(n.Artist)}&track_name={E(n.Title)}&album_name={E(t.Album)}&duration={Math.Round(dur)}", ct);
            if (exact != null && HasContent(exact)) return ToResult(exact);
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int requests = 0;
        foreach (var v in TrackNormalizer.Variants(n))
        {
            if (string.IsNullOrWhiteSpace(v.Title) || (!v.Free && string.IsNullOrWhiteSpace(v.Artist))) continue;
            var q = v.Free
                ? $"api/search?q={E(v.Title)}"
                : $"api/search?track_name={E(v.Title)}&artist_name={E(v.Artist)}";
            if (!tried.Add(q)) continue;
            if (++requests > MaxRequests) break;

            var items = await Http.GetFromJsonAsync<List<LrcLibItem>>(q, Json, ct);
            if (Pick(items, dur, v.MatchTitle, v.StrictDuration) is { } best) return ToResult(best);
        }
        return null;
    }

    static async Task<LrcLibItem?> GetOneAsync(string q, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(q, ct);
        if (!resp.IsSuccessStatusCode) return null; // 404 — не найдено
        return await resp.Content.ReadFromJsonAsync<LrcLibItem>(Json, ct);
    }

    static bool HasContent(LrcLibItem it) =>
        it.Instrumental || !string.IsNullOrWhiteSpace(it.SyncedLyrics) || !string.IsNullOrWhiteSpace(it.PlainLyrics);

    static LyricsResult ToResult(LrcLibItem it) => new(it.SyncedLyrics, it.PlainLyrics, it.Instrumental, "LRCLIB");

    /// <summary>Лучший кандидат: с таймингами и максимально близкий по длительности.</summary>
    static LrcLibItem? Pick(List<LrcLibItem>? items, double duration, string? matchTitle, bool strict)
    {
        if (items == null) return null;
        bool knownDuration = duration > 1;
        if (strict && !knownDuration) return null; // без длительности свободный поиск слишком рискован

        var want = matchTitle != null ? TrackNormalizer.Squash(matchTitle) : null;
        LrcLibItem? best = null;
        double bestScore = double.MaxValue;

        foreach (var it in items)
        {
            if (!HasContent(it)) continue;

            // Длительность записи неизвестна — не отбрасываем (кроме строгого поиска), но ставим после точных совпадений
            double diff = !knownDuration ? 0 : it.Duration is double d && d > 0 ? Math.Abs(d - duration) : (strict ? double.MaxValue : 10);
            if (knownDuration && diff > (strict ? 3 : 15)) continue; // скорее всего другая версия/песня

            if (want is { Length: > 0 })
            {
                var got = TrackNormalizer.Squash(it.TrackName ?? "");
                if (got.Length == 0 || !(got.Contains(want) || want.Contains(got))) continue;
            }

            bool synced = !string.IsNullOrWhiteSpace(it.SyncedLyrics);
            double score = diff + (synced ? 0 : 100);
            if (score < bestScore) { best = it; bestScore = score; }
        }
        return best;
    }

    // ---- дисковый кэш ----

    static string CachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(Settings.AppDir, "cache2", hash + ".json");
    }

    static CacheEntry? ReadDisk(string key)
    {
        try
        {
            var p = CachePath(key);
            return File.Exists(p) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    static void WriteDisk(string key, CacheEntry e)
    {
        try
        {
            var p = CachePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(e));
        }
        catch { }
    }

    sealed class LrcLibItem
    {
        public long Id { get; set; }
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        /// <summary>У некоторых записей LRCLIB длительность пустая (null) — раньше из-за одной такой весь поиск падал.</summary>
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
