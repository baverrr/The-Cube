# Contributing to The Cube

Bug reports, Windows-build reports and pull requests are welcome.

## Reporting a bug

Open an issue with:

- your Windows version and build (`winver`, e.g. 24H2 26100.4652)
- The Cube's version: the release you downloaded, or the `started: The Cube …` line in `cube.log`
- what you did, what you expected, and what happened
- `%APPDATA%\The Cube\cube.log` if it's relevant. It lists window classes and process names but never
  window titles. Look through it before attaching it.

Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). See [Development](README.md#development)
for the commands. `TheCube.Core` and its tests build and run on any OS. The app builds anywhere but only
runs on Windows, so anything that touches `TheCube.App` needs testing on a real Windows PC. Say in the PR
which Windows build(s) you tried it on.

## Pull requests

- Keep each PR to one change, and explain why it's needed.
- CI (build, tests, publish) must pass before merge.
- Add or update tests in `tests/TheCube.Core.Tests` for anything in `TheCube.Core`.
- Follow the existing style. `.editorconfig` covers whitespace; match the surrounding code for the rest.
- Update the README when you change a feature, setting or shortcut.
- New GitHub Actions must be pinned to a full commit SHA, with the version in a comment.

### Windows builds and the virtual-desktop interfaces

The desktop-switching COM interfaces are undocumented and change between Windows builds. To add a build,
add its interface IDs in `src/TheCube.App/Interop/` beside the existing sets (`ComWin10.cs`, `ComWin11.cs`), note where they came from,
and say which build you tested on.

### What can't be accepted

- Code copied from KWin or other GPL-licensed projects. The Cube reimplements the cube's behaviour; it
  doesn't reuse its code.
- Network calls, telemetry or anything that sends data off the PC.

## Licence

By contributing, you agree your contribution is licensed under the [MIT License](LICENSE).
