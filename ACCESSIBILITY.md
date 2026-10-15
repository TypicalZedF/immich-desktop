# Accessibility Statement

We want Immich Splash to be usable by as many people as possible, including people who use keyboards, screen readers, or other assistive technology.

## Commitment

We aim to make the app work with keyboard navigation, readable contrast, and meaningful labels for controls, and we treat accessibility problems as bugs.

## Supported environments

- Windows 10 and Windows 11 (primary platform)
- Linux (in progress)

The UI is built with [Avalonia](https://avaloniaui.net/), which exposes UI Automation on Windows, so screen readers such as Narrator and NVDA can read much of the interface.

## Known limitations

This is a small volunteer project and has not been formally audited. Some known gaps:

- Some icon-only buttons may lack descriptive labels for screen readers.
- The photo library browser may not be fully navigable by keyboard or screen reader.
- Accessibility on Linux has not been tested.

## Reporting accessibility barriers

If you run into a barrier, please [open an issue](../../issues/new/choose) and describe what you were trying to do, the assistive technology you use, and your operating system. We will do our best to fix it. Pull requests that improve accessibility are very welcome.
