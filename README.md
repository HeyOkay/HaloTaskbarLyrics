# HaloTaskbarLyrics

![HaloTaskbarLyrics](docs/banner.png)

![HaloTaskbarLyrics in action](docs/preview.gif)

Synced lyrics of the song that's playing right now — as one karaoke line on the Windows taskbar,
next to a small ring that breathes with the music.

[Русская версия](README.ru.md)

## Features

- **Works with any player** that shows up in the Windows media controls: Spotify, Yandex Music,
  Apple Music, browsers (YouTube, SoundCloud, VK…), Windows Media Player and others.
- **Lyrics** come from your own `.lrc` files in `Documents\Lyrics` first, then from
  [LRCLIB](https://lrclib.net) — free, no account or API key.
- **Lives inside the taskbar**: sticks to the system tray and follows it when icons appear or
  disappear, doesn't blink when you minimize windows, hides together with the taskbar in
  fullscreen games and videos.
- Karaoke fill, letters floating in, a soft glow in your Windows accent color, light and dark
  taskbar support, any system font or your own `.ttf`/`.otf`.
- English and Russian interface.

## Download

Get `HaloTaskbarLyrics.exe` from the [latest release](../../releases/latest) and run it.
It's a single small file — no installer.

**Requirements:**
- Windows 10 version 2004 or newer, or Windows 11 (64-bit).
- [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) — or any newer
  .NET. If it's missing, Windows shows a message on the first start; click **Yes** and it opens the
  Microsoft download page. On that page pick **.NET Desktop Runtime → Windows → x64**, install it and
  run HaloTaskbarLyrics again.

**"Windows protected your PC"?** The exe isn't code-signed yet, so SmartScreen may warn about an
unknown publisher. Click **More info → Run anyway**. You can compare the file with the
`.sha256` checksum published next to it in the release.

## Use

- **Right-click** the ring (or the right half of the lyrics line) for the menu: font and size,
  effects, position, sync offset for the current track, theme, language, start with Windows.
- **Lyrics are late or early?** *Sync → Lyrics are late / early* — the offset is remembered per
  track.
- **Your own lyrics:** put `Artist - Title.lrc` into `Documents\Lyrics` (subfolders are fine).
- **Updates:** the program checks GitHub once a day and offers *Update to version …* at the top
  of the menu. Turn it off in *Updates*.
- **Something looks wrong?** *Diagnostics → Write diagnostic log*, reproduce the problem, then
  *Show the log file* and attach it to an [issue](../../issues). The log contains technical
  details and track names, but no lyrics.

Settings, cache and logs are stored in `%LOCALAPPDATA%\HaloTaskbarLyrics`.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet publish src/HaloTaskbarLyrics.csproj -c Release -o publish
# result: publish\HaloTaskbarLyrics.exe
```

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a `v*` tag is pushed.

## Limitations

- LRCLIB mostly has line-level timing, so the fill moves evenly through a line rather than
  word by word.
- The player has to report the playback position. Most do; some websites don't.
- Primary monitor only.

## Credits

Lyrics by [LRCLIB](https://lrclib.net). Audio capture by [NAudio](https://github.com/naudio/NAudio).
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE)
