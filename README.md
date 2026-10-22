# ABS Player for Windows

A native Windows (WinUI 3, .NET 10) audiobook player for [Audiobookshelf](https://www.audiobookshelf.org). It's a port of [abs-player-apple](https://github.com/druboni/abs-player-apple), and it syncs position the same way that app does.

> ABS Player is an independent app and is not affiliated with the Audiobookshelf project.

## Layout
```
src/AbsPlayer.Core/          No UI. Ported from abs-player-apple/Shared.
  Api/                       Audiobookshelf HTTP client: token refresh, server discovery, errors
  Models/                    API models and tolerant JSON converters
  Storage/                   Settings file, Credential Locker tokens, user preferences
  AudiobookRepository.cs     What the UI calls
  LocalProgressStore.cs      Position saved on this device; sent to the server once back online
  Playback/                  PlaybackController (sessions, sync, chapters, sleep timer, bookmarks),
                             AudioPlayer (multi-file books in whole-book time), and the Windows pieces:
                             WindowsMediaEngine (Windows.Media.Playback) and SmtcNowPlaying (media overlay + keys)
src/AbsPlayer.App/           WinUI 3 app (unpackaged, Windows App SDK bundled): shell, pages, Now Playing bar
tests/AbsPlayer.Core.Tests/  xUnit tests
```

## Building and running
```
dotnet build src/AbsPlayer.App -p:Platform=x64
src\AbsPlayer.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ABSPlayer.exe
```

## Installer
```
pwsh installer\build.ps1
```
Runs the tests, publishes a self-contained Release build (it bundles .NET and the Windows App SDK, so nothing else needs installing) and compiles `installer\ABSPlayer.iss` with [Inno Setup 6](https://jrsoftware.org/isinfo.php). Output: `installer\Output\ABSPlayer-<version>-Setup.exe` (about 60 MB). The version comes from `<Version>` in `src\AbsPlayer.App\AbsPlayer.App.csproj`.

The installer installs for the current user by default, with no admin prompt. It offers an "all users" install on its first page. Uninstalling keeps settings, saved positions and the sign-in token, so a reinstall picks up where you left off. `installer\make-icon.py` redraws the app icon.

Data lives in `%LOCALAPPDATA%\ABSPlayer`. Sign-in tokens are stored in Windows Credential Locker under "ABS Player" (visible in Credential Manager), never in the settings file.

## Tests
```
dotnet test
```
Most tests run against a fake HTTP handler, a fake audio engine and a fake clock, and need no server.
`WindowsEngineTests` run the real Windows engine on generated silent WAV files at volume 0: nothing is audible and no window opens.

`LiveServerTests` are read-only checks against a real server. They don't start sessions, change progress or touch bookmarks. One streams a real book through the Windows engine at volume 0. They're skipped unless both of these are set:
```powershell
$env:ABS_LIVE_URL = "https://abs.example.com"
$env:ABS_LIVE_TOKEN = "<an Audiobookshelf API key or user token>"
dotnet test --filter LiveServerTests
```

## Status
- [x] Step 1: API client, models, repository, token storage, offline progress
- [x] Step 2: playback and sync (Windows media player, media keys and overlay)
- [ ] Step 3: screens (sign-in, library, book detail, player, settings) — built, not yet tried on screen
- [ ] Step 4: downloads and offline playback
- [x] Step 5: installer (Inno Setup, per-user)
