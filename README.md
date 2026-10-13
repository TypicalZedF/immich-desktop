<p align="center">
  <img src="src/ImmichDesktop.App/Assets/logo-256.png" width="96" alt="" />
</p>

<h1 align="center">Immich Desktop</h1>

<p align="center">
  Automatic photo &amp; video backup from your Windows PC to your own <a href="https://immich.app">Immich</a> server.<br/>
  Lives in the system tray. Looks and feels like Immich. Built for Windows first, ready for Linux.
</p>

<p align="center">
  <img src="docs/screenshots/10-overview-uploading-light.png" width="720" alt="Overview" />
</p>

## Features

- **Looks like Immich.** Official Immich logo, Google Sans, Immich's colors and layout (top bar, sidebar, storage meter and server status).
- **Light, dark or follow Windows.** One click on the sun/moon in the top bar, or choose in Settings.
- **Browse your library.** Your Immich timeline right in the app: grouped by month, thumbnails load as you scroll, full-size viewer with arrow keys (videos open in Immich).
- **Your profile picture.** Shown from Immich, and you can change or remove it from Settings.
- **Every way to sign in.** Email & password, **SSO / OAuth** (Authentik, Keycloak, Google, …) through your browser, or an API key.
- **App lock.** Require **Windows Hello** (face, fingerprint, PIN) or an app password to open the window. Backup keeps running while it's locked; it re-locks when the window closes or after 5 minutes idle.
- **Choose exactly what syncs.** Per folder: photos and/or videos, subfolders, skip patterns (`Screenshots`, `*.png`, `Trips/2019/**`), and an optional album.
- **Lives in the tray, like Google Drive.** The tray icon shows backed up / syncing (animated) / paused / offline / needs attention. Right-click to pause (1 h, 8 h, until tomorrow, indefinitely), resume, sync now, open Immich, or quit.
- **Never uploads twice.** Files are hashed and checked against your library first, exactly like the mobile app.
- **Light on your PC.** About 30 MB of memory in the tray, low CPU priority, optional speed limit and pause on battery. Skips cloud-only OneDrive files.
- **Proper installer and updates.** A setup wizard where you choose the install location and whether it's for you or for everyone; a clean uninstaller; automatic updates from GitHub Releases (optional).

| | |
|---|---|
| <img src="docs/screenshots/02-sign-in-light.png" alt="Sign in" /> | <img src="docs/screenshots/03-folders-light.png" alt="Choose folders" /> |
| <img src="docs/screenshots/15-photos-light.png" alt="Photos" /> | <img src="docs/screenshots/16-viewer-dark.png" alt="Viewer" /> |
| <img src="docs/screenshots/20-folders-dark.png" alt="Folders (dark)" /> | <img src="docs/screenshots/51-locked-hello-dark.png" alt="App lock" /> |

## Install

1. Go to **[Releases](../../releases/latest)** and download **`ImmichDesktop-Setup.exe`**.
2. Run it and follow the wizard: choose *just me* (no admin) or *everyone*, pick the install folder, optional desktop shortcut.
3. Sign in to your Immich server and pick folders. Done. The app keeps running in the tray.

No-install option: **`ImmichDesktop-Portable.zip`**: unzip anywhere and run `ImmichDesktop.exe` (no auto-updates).

Uninstall from **Settings → Apps → Installed apps → Immich Desktop**. You'll be asked whether to keep your settings; nothing is ever removed from your Immich server.

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

> Installed apps can only see releases of a **public** repository. While this repo is private, update checks report that no releases were found. To enable updates, make the repo public or publish releases to a public repo and change `AppInfo.UpdateRepository`.

## Releasing a new version

```bash
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions tests, builds `ImmichDesktop-Setup.exe` + the portable zip and publishes the release. Every push also runs CI, which installs, launches, quits and uninstalls the app on a clean Windows machine.

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
  Api/                       Immich REST client: discovery, login, OAuth/PKCE, bulk-upload-check, uploads, albums, storage
  Sync/SyncEngine.cs         Scan → hash → dedupe → upload → albums, watchers, retry/backoff
  Storage/StateDatabase.cs   SQLite index of every file and its backup state
  Updates/                   GitHub Releases update checker (SHA-256 verified downloads)
  Platform/                  Credentials (DPAPI), autostart, app.immich:// link handler, password hashing, power
src/ImmichDesktop.App        Avalonia UI: tray, onboarding, lock screen, overview, folders, activity, settings
tests/                       xUnit tests incl. end-to-end sync against a fake Immich server
tools/ImmichDesktop.DevTools Icons, installer art and screenshots rendered from the real UI
build/                       publish.ps1 / installer.iss (Inno Setup) / publish.sh (Linux)
```

## Linux

The code is cross-platform: Windows-only pieces (DPAPI, registry, Windows Hello) are isolated and have Linux fallbacks: XDG autostart, a `0600` credentials file, `/sys/class/power_supply`, an `x-scheme-handler` for SSO and an app password instead of Windows Hello. `build/publish.sh` produces a tarball with an `install.sh`. CI builds and tests on Linux on every push.

## Credits

Immich and the Immich logo are © FUTO / the Immich contributors. This is an unofficial companion app. Check Immich's brand guidelines before distributing it publicly under this name. Google Sans is licensed under the SIL Open Font License (`src/ImmichDesktop.App/Assets/Fonts/OFL.txt`).
