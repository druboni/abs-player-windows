# Homeshelf for Windows

A native Windows (WinUI 3, .NET 10) audiobook player for [Audiobookshelf](https://www.audiobookshelf.org). It's a port of [abs-player-apple](https://github.com/druboni/abs-player-apple), and it syncs position the same way that app does.

> Homeshelf (formerly ABS Player) is an independent app and is not affiliated with the Audiobookshelf project.

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
src\AbsPlayer.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Homeshelf.exe
```

## Installer
```
pwsh installer\build.ps1
```
Runs the tests, publishes a self-contained Release build (it bundles .NET and the Windows App SDK, so nothing else needs installing) and compiles `installer\Homeshelf.iss` with [Inno Setup 6](https://jrsoftware.org/isinfo.php). Output: `installer\Output\Homeshelf-<version>-Setup.exe` (about 60 MB). The version comes from `<Version>` in `src\AbsPlayer.App\AbsPlayer.App.csproj`.

The installer installs for the current user by default, with no admin prompt. It offers an "all users" install on its first page. Uninstalling keeps settings, saved positions and the sign-in token, so a reinstall picks up where you left off. `installer\make-icon.py` redraws the app icon.

Data lives in `%LOCALAPPDATA%\ABSPlayer`. Sign-in tokens are stored in Windows Credential Locker under "ABS Player" (visible in Credential Manager; the folder and this entry keep the app's old name so nothing is lost), never in the settings file.

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

## Continuous build
`.github/workflows/build.yml` runs the tests and builds the installer on every push and pull request. The real-audio tests are skipped there. Pushing a tag such as `v0.2.0` also attaches the installer to that release.

## Downloads and offline listening
Click **Download** on a book's page. The book's audio files, chapters and cover are saved in `%LOCALAPPDATA%\ABSPlayer\Downloads`. Downloaded books:
- show in a **Downloaded** row on the library page;
- play without a connection;
- sync your position to the server once you're back online.

If the connection drops or the app closes, downloads resume where they stopped. In **Settings → Downloads** you can:
- hold downloads while on a metered connection, such as a phone hotspot;
- remove a book automatically when you finish it;
- open the downloads folder, or remove all downloads.

## Audiobook Requester
If you run [Audiobook Requester](https://github.com/druboni/audiobook-requester), add its address in **Settings → Audiobook Requester** and sign in with your Audiobookshelf username and password. A **Request** button then appears in the top bar, with three tabs:
- **Search:** search Audible, see what's already in the library, and request books with an optional note.
- **My Requests:** status and download progress for each request, and **Listen** once a book arrives in the library.
- **Approvals:** admins only. Approve or decline pending requests.

Library search also offers "Request it" when a book isn't in your library.

## Download
Installers are on the [Releases page](https://github.com/druboni/abs-player-windows/releases). Uninstall from Windows Settings → Apps.

## Code signing policy
Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Every signed release is built from this repository by GitHub Actions (`.github/workflows/build.yml`) and approved by hand before it is signed.

- Committers and reviewers: [druboni](https://github.com/druboni)
- Approvers: [druboni](https://github.com/druboni)

## Privacy
This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. It contacts the Audiobookshelf server you sign in to. There is no analytics, tracking or telemetry.

If you connect [Audiobook Requester](https://github.com/druboni/audiobook-requester) in Settings, it also contacts that server, at the address you enter, to search for and request books.

If you search the library for an ISBN, the app asks [Open Library](https://openlibrary.org) which book it is (only the ISBN is sent), then searches your library for that title.

The one exception is the update check. Once a day it asks GitHub's public API (`api.github.com`) for the latest release of this app, and it downloads the installer from GitHub when you choose **Download and install**. These requests carry no account, library or listening data. Turn the check off in **Settings → Updates**.

## License
[MIT](LICENSE). Homeshelf is an independent app and is not affiliated with the Audiobookshelf project.

## Status
- [x] Step 1: API client, models, repository, token storage, offline progress
- [x] Step 2: playback and sync (Windows media player, media keys and overlay)
- [x] Step 3: screens (sign-in, library, book detail, player, settings)
- [x] Step 4: downloads and offline listening
- [x] Step 5: installer (Inno Setup, per-user)
