# Contributing to Boot Video Manager

Thanks for your interest in improving Boot Video Manager! Bug reports, ideas, translations and pull requests are all
welcome. This guide explains how to get set up and what a good contribution looks like.

## Table of contents

- [Reporting a bug](#reporting-a-bug)
- [Suggesting a feature](#suggesting-a-feature)
- [Development setup](#development-setup)
- [Project rules](#project-rules)
- [Code style](#code-style)
- [Tests](#tests)
- [Interface texts and translations](#interface-texts-and-translations)
- [Commits and pull requests](#commits-and-pull-requests)
- [Releases](#releases)

## Reporting a bug

Before opening an issue, please check that it has not already been reported and that you are on the
[latest release](https://github.com/Robocnop/BootVideoManager/releases/latest).

A good bug report contains:

- the app version (*Settings › About*) and your Windows (or Linux) version;
- what you did, what you expected, and what happened instead;
- **today's log file**: *Settings › About › Open the logs folder*, then attach `bootvideomanager-<date>.log`.
  Logs contain file paths from your PC (Steam folder, user name) but no account information — have a quick look
  before sharing;
- a screenshot if the problem is visual.

For a security issue (for example a way to make the app run or delete an unexpected file), please do not open a
public issue: report it privately with the **Report a vulnerability** button in the repository's
[Security tab](https://github.com/Robocnop/BootVideoManager/security).

## Suggesting a feature

Open an issue describing the problem you want to solve, not only the solution. Mention whether you would like to
implement it yourself. For large changes, please agree on the approach in the issue before writing code.

## Development setup

Requirements:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the exact version is pinned by `global.json`);
- any editor with C# support (Visual Studio, Rider or VS Code with C# Dev Kit);
- optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) to build the Windows installer
  (`winget install JRSoftware.InnoSetup`).

```bash
git clone https://github.com/Robocnop/BootVideoManager.git
cd BootVideoManager
dotnet build BootVideoManager.slnx
dotnet test --solution BootVideoManager.slnx
dotnet run --project src/BootVideoManager.App
```

**Never experiment on your real Steam folder.** Create a fake one (a folder that contains a `config` sub-folder) and
point the app at it:

```bash
dotnet run --project src/BootVideoManager.App -- --steam-root /path/to/FakeSteam
```

The `--steam-root` option is not saved, so your normal settings are left untouched.

## Project rules

These rules protect users' Steam installations and the steamdeckrepo.com community. Pull requests that break them
will not be merged.

1. **Never delete or overwrite a file the app did not install** without an explicit confirmation from the user.
   Every file the app writes to the Steam folder is recorded in the install manifest; anything else is treated as
   the user's.
2. **Never modify Steam's own files** (`steamui/movies`, Points Shop items). Enabling a stock animation copies it.
3. **Verify before installing**: downloads go to a temporary `.part` file and are checked (WebM signature, size,
   SHA-256) before being moved into place. Updates of the app itself are checked against the published checksums.
4. **Be polite to steamdeckrepo.com**: keep requests cached and conditional, keep searching and filtering local, keep
   the identifiable User-Agent, honour `Retry-After`, and never add a request loop. See
   [docs/research.md](docs/research.md) for the API.
5. **Keep `BootVideoManager.Core` free of UI code.** Anything that touches files, the network or Steam belongs in
   Core, behind an abstraction that can be tested (`IFileSystem`, `HttpClient` handlers, `TimeProvider`).

## Code style

The build is strict on purpose: `TreatWarningsAsErrors` is on and the .NET analyzers run at `latest-recommended`, so
a warning fails the build. Fix the cause rather than suppressing it; if a suppression is really needed, keep it as
narrow as possible and explain why in a comment.

- Nullable reference types are enabled everywhere; do not use `!` to silence a real possibility of `null`.
- Match the surrounding code: file-scoped namespaces, `sealed` classes by default, `async` all the way down,
  `ConfigureAwait(false)` in Core.
- Public types and non-obvious members get a short `/// <summary>` explaining **why**, not just what.
- Package versions live in `Directory.Packages.props` (central package management): reference packages without a
  version in the project files.
- View models use CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`); views use compiled bindings
  (`x:DataType`).
- Expected failures (network, disk, Steam holding a file) become typed exceptions (`InstallException`,
  `RepoApiException`, `UpdateException`) with a clear message for the user, and are logged with `AppLog`.

## Tests

Every change must keep the whole suite green:

```bash
dotnet test --solution BootVideoManager.slnx
```

- **Core** (`tests/BootVideoManager.Core.Tests`): xUnit v3. Use `MockFileSystem`, the `StubHttpHandler` from
  `TestSupport.cs` and `FakeTimeProvider` — tests must never hit the network or the real disk outside a temporary
  folder.
- **UI** (`tests/BootVideoManager.App.Tests`): headless Avalonia tests (`[AvaloniaFact]`) that drive the real views and
  view models with services pointed at a temporary folder. This project stays on xUnit v3 3.2 because
  `Avalonia.Headless.XUnit` is built against it.

Bug fixes should come with a test that fails without the fix. New behaviour in Core should be covered by unit tests;
new screens or interactions should get at least one headless test.

## Interface texts and translations

The app is fully available in **French and English**. Every text shown to users must exist in both languages:

- **XAML**: add a property to `src/BootVideoManager.App/Localization/Strings.cs` and bind it with
  `{x:Static l:Strings.YourText}`.
- **Code**: write the pair inline with `Loc.T("Texte en français", "English text")`, next to where it is used.

Log messages (`AppLog`) and developer-facing exceptions (`ArgumentException`…) stay in English only.

Want to add a language? Open an issue first: the current two-language helper would need to grow into a proper
resource-based system.

## Commits and pull requests

- Create a branch from `main` (`fix/thumbnail-leak`, `feat/queue-limit`…).
- Write commit messages in English using the [Conventional Commits](https://www.conventionalcommits.org/) style
  already used in the history: `feat: …`, `fix: …`, `docs: …`, `chore: …`, `test: …`, `refactor: …`.
  Explain *why* in the body when it is not obvious.
- Keep pull requests focused: one topic per pull request is much easier to review.
- In the description, explain what changed, why, and how you tested it (including manual checks with a fake Steam
  folder if the change touches installs).
- CI builds and tests the solution on Windows and Linux for every pull request; it must pass before merging.
- Update the README or the docs when behaviour visible to users changes.

## Releases

Releases are made by the maintainer:

1. Bump `<Version>` in `Directory.Build.props`.
2. Write the release notes in `docs/release-notes/v<version>.md`.
3. Push a matching tag (`v1.2.0`).

The [release workflow](.github/workflows/release.yml) builds the installers, portable executables and Linux archive,
and publishes them with `SHA256SUMS.txt`, which the in-app updater needs. Tags with a suffix (`v1.2.0-beta`) are
published as pre-releases and are never offered by the app.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
