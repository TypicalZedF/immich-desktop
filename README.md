<p align="center">
  <img src="src/ImmichDesktop.App/Assets/logo-256.png" width="96" alt="" />
</p>

<h1 align="center">Immich Desktop</h1>

<p align="center">
  Automatic photo &amp; video backup from your Windows PC to your own <a href="https://immich.app">Immich</a> server.<br/>
  Lives in the system tray. Looks and feels like Immich. For Windows and Linux.
</p>

<p align="center">
  <img src="docs/screenshots/10-overview-uploading-light.png" width="720" alt="Overview" />
</p>

## Features

- **Looks like Immich.** Official Immich logo, Google Sans, Immich's colors and layout (top bar, sidebar, storage meter and server status).
- **Light, dark or follow Windows.** One click on the sun/moon in the top bar, or choose in Settings.
- **Your library, like the web and mobile apps.** A sidebar with Photos (with a date rail to jump through the years), Explore (people and places), Map, Sharing (shared albums, your links, partners) and Albums, plus a Library group: Favorites, Archive, Locked Folder (unlocked with your PIN) and Trash. Search from the top bar, with suggestions and recent searches. A sharp full-screen viewer with zoom, favorite, copy, download, add to album, trash and photo info, and videos play right in the app.
- **Offline map.** A built-in world map with country and state lines, photo thumbnails, a heat map and a side panel with the photos of the area you're looking at; online street detail is optional.
- **Utilities.** Review duplicates, archive screenshots and screen recordings, find your largest files, and see which files on this PC aren't in Immich yet.
- **Private photos, found on your PC.** An optional check suggests photos for the locked folder (nudity, payment cards, IDs, bank and tax papers, passwords, medical records). It runs entirely on your PC with on-device models (NudeNet for nudity, plus an optional thorough mode with CLIP that also understands drawings, art and game screenshots); your photos and results are never sent anywhere and nothing is moved without you.
- **Share.** Select photos (or a whole album) and create a share link with an expiry date, copied straight to your clipboard.
- **Desktop picture frame.** Your desktop background can rotate through photos from your favorites, an album or the whole library.
- **Your profile picture.** Shown from Immich. Change it in Settings by picking a photo from your Immich library (or your PC), then move, zoom and rotate it in the circle, just like the web app.
- **Every way to sign in.** Email & password, **SSO / OAuth** (Authentik, Keycloak, Google, …) through your browser, or an API key.
- **App lock.** Require **Windows Hello** (face, fingerprint, PIN) or an app password to open the window. Backup keeps running while it's locked; it re-locks when the window closes or after 5 minutes idle.
- **Choose exactly what syncs.** Per folder: photos and/or videos, subfolders, skip patterns (`Screenshots`, `*.png`, `Trips/2019/**`), and an optional album.
- **Lives in the tray, like Google Drive.** The tray icon shows backed up / syncing (animated) / paused / offline / needs attention. Right-click to pause (1 h, 8 h, until tomorrow, indefinitely), resume, sync now, open Immich, or quit.
- **Never uploads twice.** Files are hashed and checked against your library first, exactly like the mobile app.
- **Home and away.** Automatic server switching like the mobile app: a local address on your home network, any number of external addresses elsewhere. Data saving on metered connections (photos only, or pause).
- **Your language.** English, Deutsch, Español, Français and Português; dates and numbers follow.
- **Light on your PC.** About 30 MB of memory in the tray, low CPU priority, optional speed limit and pause on battery. Skips cloud-only OneDrive files. Graphics acceleration can be turned off if your graphics driver misbehaves.
- **Proper installer and updates.** A setup wizard where you choose the install location and whether it's for you or for everyone; a clean uninstaller; automatic updates from GitHub Releases (optional).

| | |
|---|---|
| <img src="docs/screenshots/02-sign-in-light.png" alt="Sign in" /> | <img src="docs/screenshots/03-folders-light.png" alt="Choose folders" /> |
| <img src="docs/screenshots/15-photos-light.png" alt="Photos" /> | <img src="docs/screenshots/16-viewer-dark.png" alt="Viewer" /> |
| <img src="docs/screenshots/19b-explore-dark.png" alt="Explore" /> | <img src="docs/screenshots/19d6-map-panel-light.png" alt="Map" /> |
| <img src="docs/screenshots/19-utilities-light.png" alt="Utilities" /> | <img src="docs/screenshots/19p-privacy-light.png" alt="Suggestions for the locked folder" /> |
| <img src="docs/screenshots/20-folders-dark.png" alt="Folders (dark)" /> | <img src="docs/screenshots/51-locked-hello-dark.png" alt="App lock" /> |

## Install

1. Go to **[Releases](../../releases/latest)** and download **`ImmichDesktop-Setup.exe`**.
2. Run it and follow the wizard: choose *just me* (no admin) or *everyone*, pick the install folder, optional desktop shortcut.
3. Sign in to your Immich server and pick folders. Done. The app keeps running in the tray.

No-install option: **`ImmichDesktop-Portable.zip`**: unzip anywhere and run `ImmichDesktop.exe` (no auto-updates).

Uninstall from **Settings → Apps → Installed apps → Immich Desktop**. You'll be asked whether to keep your settings; nothing is ever removed from your Immich server.

**Linux:** download **`ImmichDesktop-linux-x64.tar.gz`**, extract it and run `./install.sh`. It installs to `~/.local/opt/immich-desktop` and adds a menu entry; `./install.sh --uninstall` removes it. For videos to play in the app, install VLC (`sudo apt install vlc`, `sudo dnf install vlc`, …). Linux installs don't update themselves yet: download the new tarball and run `./install.sh` again.

> Until the installer is code-signed, Windows SmartScreen shows "Windows protected your PC". Click **More info → Run anyway**.

## Security & privacy

| What | Where | How it's protected |
|---|---|---|
| Your Immich password | **Never stored** | Sent once to your server to sign in. |
| Session token / API key | `credentials.bin` | Encrypted with Windows DPAPI: only your Windows account can read it. |
| App lock password | `settings.json` | Only a salted PBKDF2-SHA256 hash (600k iterations). |
| Email, name, server address | `settings.json` | Plain text (shown in the app; not secret). |

The app lock protects against someone using your unlocked PC; it can't stop someone who already controls your Windows account.

SSO uses the standard OAuth authorization-code flow with PKCE. Your identity provider sends the browser back to `app.immich:///oauth-callback`, the same redirect the official mobile app uses, so it works wherever mobile SSO works.

## Updates

Installed copies check this repository's **latest GitHub Release** every few hours (Settings → Updates). New installers are downloaded in the background, verified against GitHub's SHA-256 digest, and installed:

- **automatically** while the window is closed and nothing is uploading (if enabled), or
- when you click **Restart to update**.

**Beta channel.** Settings → Updates → Update channel → **Beta** also installs pre-releases, so you get new features first. Switching back to **Stable** never downgrades; you move over with the next stable version.

## Releasing a new version

```bash
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions tests, builds `ImmichDesktop-Setup.exe`, the portable zip and the Linux tarball, and publishes the release.

**Betas:** push to the `beta` branch. Each push publishes a pre-release numbered after the latest stable version (e.g. `v1.3.1-beta.42` after `v1.3.0`), which Beta-channel installs pick up. Merge `beta` into `main` and tag it to ship it to everyone. Every push also runs CI, which installs, launches, quits and uninstalls the app on a clean Windows machine, and renders every screen against a demo server to catch broken pages.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). For the installer: `winget install JRSoftware.InnoSetup`.

```bash
dotnet run --project src/ImmichDesktop.App        # run the app
dotnet test                                       # run the tests
./build/publish.ps1 -Version 1.2.0                # installer + portable zip -> artifacts/installer
./build/publish.sh 1.2.0                          # Linux tarball (run on Linux)
```

Developer tools (render the real UI headlessly):

```bash
dotnet run --project tools/ImmichDesktop.DevTools -- icons src/ImmichDesktop.App/Assets   # app + tray icons
dotnet run --project tools/ImmichDesktop.DevTools -- installer build/installer            # installer wizard art
dotnet run --project tools/ImmichDesktop.DevTools -- screens docs/screenshots             # screenshots
```

Set `IMMICH_DESKTOP_DATA_DIR` to use a throwaway profile while developing.

## Project layout

```
src/ImmichDesktop.Core       Platform-neutral engine (no UI)
  Api/                       Immich REST client: discovery, login, OAuth/PKCE, uploads, library, albums, search, sharing
  Sync/SyncEngine*.cs        Scan → hash → dedupe → upload → albums, watchers, retry/backoff (one file per stage)
  Storage/StateDatabase.cs   SQLite index of every file and its backup state
  Privacy/                   On-device checks for the locked-folder suggestions (ONNX models, OCR)
  Updates/                   GitHub Releases update checker (SHA-256 verified downloads)
  Platform/                  Credentials (DPAPI), autostart, app.immich:// link handler, password hashing, power, network
src/ImmichDesktop.App        Avalonia UI: tray, onboarding, lock screen, library (Views/Photos*), map, backup, settings
  Assets/i18n/               Translations (English text is the key; tests keep the files in step)
tests/                       xUnit tests incl. end-to-end sync against a fake Immich server
tools/ImmichDesktop.DevTools Icons, installer art and screenshots rendered from the real UI
build/                       publish.ps1 / installer.iss (Inno Setup) / publish.sh (Linux)
```

## Linux

The code is cross-platform: Windows-only pieces (DPAPI, registry, Windows Hello) are isolated and have Linux fallbacks: XDG autostart, a `0600` credentials file, `/sys/class/power_supply`, an `x-scheme-handler` for SSO and an app password instead of Windows Hello. Every release includes a Linux tarball (see [Install](#install)), built by `build/publish.sh`. CI builds and tests on Linux on every push.

## Credits

Immich and the Immich logo are © FUTO / the Immich contributors. They are used here only to show that this app works with Immich. This is an unofficial, community-made companion app and is not affiliated with or endorsed by the Immich project or FUTO. Google Sans is licensed under the SIL Open Font License (`src/ImmichDesktop.App/Assets/Fonts/OFL.txt`).
