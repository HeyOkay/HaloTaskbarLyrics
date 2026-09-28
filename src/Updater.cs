using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace TaskbarLyrics;

/// <summary>
/// Проверка обновлений через GitHub Releases. Раз в сутки (и по кнопке в меню) спрашиваем у GitHub
/// последний релиз; если его версия новее нашей и в нём есть HaloTaskbarLyrics.exe — сообщаем.
/// Установка по клику: скачиваем exe, проверяем (заголовок «MZ», размер, SHA-256 от GitHub, если он есть),
/// переименовываем работающий exe в *.old.exe (Windows это разрешает), кладём новый на его место
/// и запускаем его с ключом --after-update. Старый exe удаляется при следующем запуске.
/// </summary>
public static class Updater
{
    /// <summary>Репозиторий на GitHub: "владелец/имя". Пусто — проверка обновлений выключена.</summary>
    public const string Repo = "HeyOkay/HaloTaskbarLyrics";

    const string AssetName = "HaloTaskbarLyrics.exe";
    public const string AfterUpdateArg = "--after-update";

    public sealed record Release(Version Version, string Tag, string Page, string DownloadUrl, long Size, string? Sha256);

    public static bool Configured => !string.IsNullOrWhiteSpace(Repo);

    /// <summary>Страница проекта (для User-Agent и ссылок).</summary>
    public static string ProjectUrl => Configured ? $"https://github.com/{Repo}" : "https://github.com";

    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HaloTaskbarLyrics/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Последний релиз, если он новее текущей версии; иначе null. Ошибки сети — исключение.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (!Configured) return null;
        var r = await Http.GetFromJsonAsync<GhRelease>($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (r == null || r.Draft || r.Prerelease || string.IsNullOrWhiteSpace(r.TagName)) return null;
        if (!Version.TryParse(r.TagName.Trim().TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        var asset = r.Assets?.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        Diag.Write($"Update check: latest {r.TagName}, current {Current}, asset {(asset != null ? "found" : "missing")}");
        if (v <= Current || asset?.BrowserDownloadUrl == null) return null;
        // Скачиваем только из релизов своего репозитория и только по https — никаких сторонних адресов
        if (!asset.BrowserDownloadUrl.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            Diag.Write("Update check: unexpected download address, ignored");
            return null;
        }

        string? sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
        return new Release(v, r.TagName, r.HtmlUrl ?? $"https://github.com/{Repo}/releases/latest", asset.BrowserDownloadUrl, asset.Size, sha);
    }

    /// <summary>Скачивает и ставит обновление. true — новая версия запущена, текущую пора закрыть.</summary>
    public static async Task<bool> InstallAsync(Release rel, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Unknown program path");
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, AssetName + ".download");
        var old = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".old.exe");

        Diag.Write($"Update: downloading {rel.Tag} from {rel.DownloadUrl}");
        using (var resp = await Http.GetAsync(rel.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var file = File.Create(tmp);
            await resp.Content.CopyToAsync(file, ct);
        }

        // Проверка: это exe, размер как у GitHub, контрольная сумма совпадает
        var bytes = await File.ReadAllBytesAsync(tmp, ct);
        bool ok = bytes.Length > 1024 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z'
                  && (rel.Size <= 0 || bytes.Length == rel.Size);
        if (ok && rel.Sha256 != null)
            ok = string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), rel.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            TryDelete(tmp);
            throw new InvalidDataException("Downloaded update is damaged");
        }

        // Работающий exe нельзя перезаписать, но можно переименовать
        TryDelete(old);
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; } // вернуть как было

        Diag.Write($"Update: installed {rel.Tag}, restarting");
        Process.Start(new ProcessStartInfo(exe, AfterUpdateArg) { UseShellExecute = false, WorkingDirectory = dir });
        return true;
    }

    /// <summary>При запуске: убрать exe предыдущей версии, оставшийся после обновления.</summary>
    public static void CleanupOld()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var dir = Path.GetDirectoryName(exe)!;
        TryDelete(Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".old.exe"));
        TryDelete(Path.Combine(dir, AssetName + ".download"));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ещё занят — удалим в следующий раз */ }
    }

    sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
