# Contributing to Immich Desktop

Thanks for your interest in improving Immich Desktop! This is an unofficial, community-built desktop backup client for [Immich](https://immich.app). It is not affiliated with or endorsed by the Immich project or FUTO.

By participating you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md). By contributing you agree your work is licensed under the project's [AGPL-3.0 license](LICENSE).

## Ways to help

- Report bugs and request features through [issues](../../issues/new/choose).
- Test the beta channel and share feedback.
- Improve documentation and screenshots.
- Help with Linux support, which is in progress.

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

Immich is a trademark of its respective owners. Please do not use the Immich logo in a way that suggests this app is official.

## Security

Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
