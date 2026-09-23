# Contributing to SafeSweep

Thank you for helping. SafeSweep deletes files on real people's computers, so the bar for
changes that affect deletion is deliberately high. Everything else (the user interface,
documentation, translations of the explanations, new read-only reports) is very welcome
and easy to get merged.

## Ground rules

1. **Never weaken a protection to make something work.** If a location should be cleaned
   and is currently refused, add a narrow, explicit rule for it with tests. Do not loosen a
   general check.
2. **When unsure, do not delete.** New categories start in Deep Clean or Manual Review, not
   Safe Clean. Safe Clean is only for data that is recreated automatically and is never user
   data.
3. **Every deletion goes through `PathGuard` and `CleanEngine`.** Do not call
   `File.Delete`, `Directory.Delete` or shell delete functions anywhere else.
4. **Explain every item.** A new category needs a description, a reason it is removable and
   a note on what happens afterwards, in plain language.
5. **Keep private data out of the repository**: no real user names, machine paths, logs or
   screenshots of your own files in code, tests, issues or pull requests.

## Getting started

Requirements: Windows 10 or 11 (x64) and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/trxxst/SafeSweep.git
cd SafeSweep
dotnet build SafeSweep.sln
dotnet test SafeSweep.sln --filter "Category!=Integration"
dotnet run --project src/SafeSweep.App
```

Turn on **Simulation mode** in Settings while you try things out: every clean then only
reports what it would do.

## Before you open a pull request

Run the same checks as the CI build:

```powershell
dotnet format whitespace SafeSweep.sln --verify-no-changes
dotnet build SafeSweep.sln -c Release -warnaserror
dotnet test SafeSweep.sln -c Release --filter "Category!=Integration"
```

`dotnet format whitespace SafeSweep.sln` (without `--verify-no-changes`) fixes formatting
for you. The full release build, including the installer, is
`powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1`.

## Tests

- Tests that delete files must do so only inside a `Sandbox` (see
  `tests/SafeSweep.Tests/Sandbox.cs`). Never point a test at real user folders.
- A change to the protection rules needs two kinds of test: one showing the new behaviour
  and one showing that the protected neighbour is still refused. A regression test should
  fail when the fix is removed; please check that it does.
- UI changes should keep `Category=UI` tests green. They open the real window at every
  supported size and check that nothing is cut off. Set `SAFESWEEP_UI_SNAPSHOTS` to a
  folder to get PNG snapshots of every page for a visual check.
- README screenshots are regenerated with the opt-in `ReadmeScreenshots` test; its comment
  explains how to run it without exposing your own user name in the paths shown.

## Coding style

- The `.editorconfig` in the repository defines formatting. C# uses file-scoped namespaces,
  braces on new lines and `var` only when the type is obvious.
- Keep the Core project free of UI code; the App project uses MVVM with
  CommunityToolkit.Mvvm.
- Comments explain *why*, especially for anything related to safety.

## Versions and releases

Every change to the app gets a new version number. Describe user-visible changes under
`## [Unreleased]` in `CHANGELOG.md`; the maintainer raises the version with
`scripts/bump-version.ps1` when releasing. The whole process is in
[docs/RELEASING.md](docs/RELEASING.md).

## Commit messages and pull requests

- Keep commits focused; describe what changed and why.
- Fill in the pull request template, including the safety checklist.
- Pull requests that change what can be deleted are reviewed with extra care and may take
  longer. That is not a judgement on the contribution.

## Reporting bugs and security problems

Use the issue templates for bugs and feature ideas. Anything that makes SafeSweep delete
something it should not is a security problem: please report it privately as described in
[SECURITY.md](SECURITY.md).

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE).
