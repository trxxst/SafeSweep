# Releasing SafeSweep

SafeSweep follows [semantic versioning](https://semver.org). **Every change to the app gets
a new version**, so a version number always identifies exactly one build of the program.

| Change | Raise | Example |
| --- | --- | --- |
| Bug fixes only | patch | 1.1.0 to 1.1.1 |
| New features, existing behaviour kept | minor | 1.1.0 to 1.2.0 |
| Behaviour that users rely on changes or is removed | major | 1.1.0 to 2.0.0 |

The version lives in one place, `VersionPrefix` in `Directory.Build.props`. The app, the
version shown in the sidebar and in Settings, the executable's file details, the MSI, the
setup program and the release tag all take it from there.

## Steps

1. While working, describe user-visible changes under `## [Unreleased]` in `CHANGELOG.md`.
2. Raise the version:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\bump-version.ps1 -Part patch   # or minor, major
   ```

   This updates `Directory.Build.props`, turns the Unreleased section into a section for
   the new version, updates the links at the bottom of the changelog and creates
   `docs/release-notes/v<version>.md` from a template.
3. Fill in the release notes and replace every `TODO` line.
4. Build and test locally:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
   ```

5. Commit, tag and push:

   ```powershell
   git commit -am "Release 1.2.3"
   git tag -a v1.2.3 -m "SafeSweep 1.2.3"
   git push origin main v1.2.3
   ```

6. The **Build** workflow builds and tests the tag and creates a **draft** release with the
   installer, MSI, portable exe and checksums. Open the draft on the
   [Releases page](https://github.com/trxxst/SafeSweep/releases), check the files and the
   notes, then press **Publish release**.

## Safety nets

- The release build fails when `CHANGELOG.md` has no section for the version or the
  release notes file is missing.
- A tag build fails when the tag does not match `Directory.Build.props`.
- The release job refuses release notes that still contain `TODO`.
- Every build on `main` warns when the app or the installer changed after the current
  version was already released, as a reminder to raise the version.
