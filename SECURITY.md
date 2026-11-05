# Security policy

## Reporting a vulnerability

Please report security problems privately through GitHub:
**[Security → Report a vulnerability](https://github.com/baverrr/The-Cube/security/advisories/new)**.
Don't open a public issue for them.

Include the Windows build (`winver`), The Cube's version, and steps to reproduce. You should get a
reply within a week.

## Supported versions

Only the latest release gets fixes.

## Scope

The Cube is a local desktop app. It has no network listener, sends no telemetry, and only writes to
`%APPDATA%\The Cube` (settings, `cube.log`, `error.log`). If you allow it on first run, it also adds
Windows virtual desktops and a start-up entry under the current user's `Run` registry key. Things worth reporting include:

- a way for another program or user to make The Cube run code or read data it shouldn't
- DLL loading from anywhere other than System32 or the app's own bundle
- problems in the release workflow or signing pipeline (`.github/workflows/`)
