using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

/// <summary>
/// Обложка из Deezer (https://api.deezer.com, без ключа API) — когда плеер не передаёт в Windows
/// обложку трека (или передаёт картинку прошлого трека). Ищем трек по исполнителю и названию,
/// берём обложку его альбома 500×500. Найденное и «не найдено» запоминаются на диске
/// (%LOCALAPPDATA%\HaloTaskbarLyrics\covers), «не найдено» перепроверяется раз в неделю.
/// </summary>
public static class CoverService
{
    static readonly HttpClient Http = CreateClient();
    static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);
    const int MaxBytes = 5 * 1024 * 1024;
    static readonly Dictionary<string, byte[]?> Mem = new();

    static string Dir => Path.Combine(Settings.AppDir, "covers");

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HaloTaskbarLyrics/{Updater.Current} (+{Updater.ProjectUrl})");
        return c;
    }

    /// <summary>Картинка обложки или null (не нашлось, нет интернета).</summary>
    public static async Task<byte[]?> FindAsync(TrackInfo t)
    {
        var n = TrackNormalizer.Clean(t.Artist, t.Title);
        var artist = n.MainArtist.Length > 0 ? n.MainArtist : n.Artist;
        var title = n.BaseTitle.Length > 0 ? n.BaseTitle : n.Title;
        if (title.Length == 0) return null;

        var key = Hash(artist.ToLowerInvariant() + "\u001f" + title.ToLowerInvariant());
        if (Mem.TryGetValue(key, out var mem)) return mem;

        // Диск
        var file = Path.Combine(Dir, key + ".jpg");
        var miss = Path.Combine(Dir, key + ".none");
        try
        {
            if (File.Exists(file))
            {
                var cached = await File.ReadAllBytesAsync(file);
                Diag.Write($"Cover web \"{artist}\" — \"{title}\": from cache");
                return Mem[key] = cached;
            }
            if (File.Exists(miss) && DateTime.Now - File.GetLastWriteTime(miss) < MissTtl)
            {
                Diag.Write($"Cover web \"{artist}\" — \"{title}\": not found earlier (cached)");
                return Mem[key] = null;
            }
        }
        catch { }

        // Deezer: сначала поиск по полям, потом свободный
        string? url = null;
        try
        {
            url = await SearchAsync($"artist:\"{artist}\" track:\"{title}\"", artist, title, t.Duration)
                  ?? await SearchAsync($"{artist} {title}", artist, title, t.Duration);
        }
        catch (Exception ex)
        {
            Diag.Write($"Cover web \"{artist}\" — \"{title}\": search failed: {ex.Message}");
            return null; // нет интернета — «не найдено» не запоминаем
        }

        byte[]? bytes = null;
        if (url != null)
        {
            try { bytes = await DownloadAsync(url); }
            catch (Exception ex)
            {
                Diag.Write($"Cover web \"{artist}\" — \"{title}\": download failed: {ex.Message}");
                return null;
            }
        }

        try
        {
            Directory.CreateDirectory(Dir);
            if (bytes != null)
            {
                await File.WriteAllBytesAsync(file, bytes);
                if (File.Exists(miss)) File.Move(miss, miss + ".old", true);
            }
            else await File.WriteAllTextAsync(miss, DateTimeOffset.Now.ToString("O"));
        }
        catch { }
        Diag.Write($"Cover web \"{artist}\" — \"{title}\": {(bytes != null ? $"found on Deezer, {bytes.Length} bytes" : "not found on Deezer")}");
        return Mem[key] = bytes;
    }

    /// <summary>Ищет трек и возвращает адрес обложки лучшего совпадения (или null).</summary>
    static async Task<string?> SearchAsync(string query, string artist, string title, TimeSpan duration)
    {
        var uri = "https://api.deezer.com/search?limit=10&q=" + Uri.EscapeDataString(query);
        using var resp = await Http.GetAsync(uri);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        var wantArtist = Norm(artist);
        var wantTitle = Norm(title);
        string? best = null;
        int bestScore = int.MinValue;
        foreach (var item in data.EnumerateArray())
        {
            var itemTitle = Norm(Str(item, "title"));
            var itemArtist = item.TryGetProperty("artist", out var a) ? Norm(Str(a, "name")) : "";
            if (!item.TryGetProperty("album", out var album)) continue;
            // Самая крупная обложка (1000×1000): её же видно на весь блок в панели
            var cover = Str(album, "cover_xl");
            if (cover.Length == 0) cover = Str(album, "cover_big");
            if (cover.Length == 0) cover = Str(album, "cover_medium");
            if (cover.Length == 0) continue;

            // Исполнитель должен совпасть (с допуском на опечатки), название — начинаться с искомого
            bool artistOk = itemArtist.Length > 0 && wantArtist.Length > 0 &&
                (itemArtist == wantArtist || itemArtist.Contains(wantArtist) || wantArtist.Contains(itemArtist));
            bool titleOk = itemTitle.Length > 0 && (itemTitle == wantTitle || itemTitle.StartsWith(wantTitle) || wantTitle.StartsWith(itemTitle));
            if (!artistOk || !titleOk) continue;

            int score = (itemArtist == wantArtist ? 4 : 0) + (itemTitle == wantTitle ? 4 : 0);
            if (duration > TimeSpan.Zero && item.TryGetProperty("duration", out var d) && d.TryGetInt32(out var sec))
            {
                double diff = Math.Abs(sec - duration.TotalSeconds);
                score += diff <= 3 ? 3 : diff <= 10 ? 1 : -2;
            }
            if (score > bestScore) { bestScore = score; best = cover; }
        }
        return best;
    }

    /// <summary>Скачивает картинку — только с серверов картинок Deezer.</summary>
    static async Task<byte[]?> DownloadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ||
            !(u.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len && len > MaxBytes) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return bytes.Length is > 0 and <= MaxBytes ? bytes : null;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Только буквы и цифры в нижнем регистре — для сравнения названий.</summary>
    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}
