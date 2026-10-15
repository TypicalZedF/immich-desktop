# Contributing to Immich Desktop

Thanks for your interest in improving Immich Desktop! This is an unofficial, community-built desktop backup client for [Immich](https://immich.app). It is not affiliated with or endorsed by the Immich project or FUTO.

By participating you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md). By contributing you agree your work is licensed under the project's [AGPL-3.0 license](LICENSE).

## Ways to help

- Report bugs and request features through [issues](../../issues/new/choose).
- Test the beta channel and share feedback.
- Improve documentation and screenshots.
- Help with Linux support, which is in progress.

## Trying your changes on your own machines

You don't need a beta release to test a change. `build/dev.ps1` builds what you have checked out and puts it on your machines in about a minute:

```powershell
./build/dev.ps1                                   # this PC: replaces your installed copy and restarts it (settings kept)
./build/dev.ps1 -Linux -LinuxHost you@laptop.local # also a Linux machine, over SSH (the host is remembered)
./build/dev.ps1 -Linux -NoWindows                 # only the Linux machine
```

Dev builds are versioned like `1.5.2-dev.10151342`. They count as newer than the current betas, so the app won't switch back by itself; the next stable release or a newer beta replaces them as usual.

On Linux the dev build installs like the tarball, to `~/.local/opt/immich-desktop`. It closes the Flatpak copy if one is running. It keeps its own settings (`~/.local/share/immich-desktop`), so sign in once. Videos need `mpv-libs` (Fedora) or `libmpv2` (Debian/Ubuntu).

One-time SSH setup for `-LinuxHost`:

1. On the Linux machine (Fedora shown), turn on SSH:

   ```bash
   sudo systemctl enable --now sshd
   ```

2. On Windows, in PowerShell, create a key and copy it over so you aren't asked for a password each time:

   ```powershell
   ssh-keygen -t ed25519
   type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh you@laptop.local "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys"
   ```

Without SSH, `-Linux` leaves `artifacts/dev/ImmichDesktop-dev-linux-x64.tar.gz`; copy it over, extract it and run `./immich-desktop/install.sh`.

## Before you open a pull request

- Keep each PR focused on a single change. Smaller PRs are reviewed faster.
- For large or impactful changes, open an issue to discuss the approach first.
- Fill out the pull request template.
- Build and run the tests (`dotnet test`) and make sure they pass.
- Follow the existing code style and structure.

## About AI

This project was built with substantial help from AI coding assistants (Claude, by Anthropic), directed and reviewed by the maintainer. AI-assisted contributions are welcome; you do not need to disclose AI use, but you are responsible for reviewing and testing what you submit.

Note that the upstream Immich project has its own, stricter policy on LLM-generated pull requests. Read [their contribution policy](https://github.com/immich-app/immich/blob/main/CONTRIBUTING.md) before submitting anything to Immich itself.

## Machine learning and third-party assets

Smart search, face recognition, duplicate detection, and similar features are performed by your Immich server, not by this app.

The one exception is the optional **Suggest for the locked folder** utility, which is off until you turn it on. It runs its models entirely on your PC with [ONNX Runtime](https://onnxruntime.ai) (MIT): the bundled [NudeNet](https://github.com/notAI-tech/NudeNet) 320n nudity detector (AGPL-3.0), [LAION's CLIP-based NSFW detector](https://github.com/LAION-AI/CLIP-based-NSFW-Detector) (MIT) and the [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv4 text detection and recognition models (Apache 2.0), plus, only if you turn on the thorough check, [OpenAI CLIP](https://github.com/openai/CLIP) ViT-B/32 (MIT), downloaded once from Immich's published copy and verified by its SHA-256. Nothing it sees or finds leaves your PC; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details. Changes to this feature must keep it fully on-device.

If you propose adding a model, library, icon, or other third-party asset, state its license in the PR and make sure it is compatible with AGPL-3.0 and, for models, that its terms allow redistribution.

## Branding

Immich is a trademark of its respective owners. Don't use the Immich logo or wordmark anywhere in this app (the Immich team asked for that); it has its own logo, `src/ImmichDesktop.App/Assets/logo.png`.

## Security

Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
