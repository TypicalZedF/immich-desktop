# Security Policy

## Supported versions

Only the latest stable release and the latest beta release of Immich Desktop receive security fixes. Please update before reporting.

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues, discussions, or pull requests.

Use GitHub's private vulnerability reporting instead: open the repository's Security tab and choose "Report a vulnerability". Include the affected version, your operating system, steps to reproduce, and the impact you expect.

You can expect an acknowledgement within a few days. This is a volunteer-run project, so fix timelines depend on severity and maintainer availability. We ask that you give us reasonable time to release a fix before any public disclosure, and we will credit you in the release notes if you wish.

## Scope

In scope: this application, including how it stores credentials (Windows DPAPI, salted PBKDF2-SHA256 app-lock hash), its update mechanism, and its handling of Immich server connections and API keys.

Out of scope: vulnerabilities in the Immich server itself. Please report those to the [Immich project](https://github.com/immich-app/immich/security) directly.
