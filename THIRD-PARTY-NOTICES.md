# Third-party notices

HaloTaskbarLyrics uses the following components.

| Component | License | Link |
|---|---|---|
| .NET 10 runtime and WPF (installed separately) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| NAudio.Wasapi 2.2.1 | MIT | https://github.com/naudio/NAudio |

## Lyrics

Lyrics are not included in the program. They are downloaded at run time from
[LRCLIB](https://lrclib.net), a free, open, community-maintained lyrics database, or read from
the user's own `.lrc` files. Lyrics belong to their respective rights holders.

## Album covers

When the player doesn't pass a cover to Windows, the lyrics panel looks the track up in the
[Deezer API](https://developers.deezer.com/api) (artist and title only) and shows the album cover.
Covers are not included in the program; they belong to their respective rights holders.
