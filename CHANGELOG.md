# Changelog

All notable changes to SafeSweep are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
SafeSweep uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Every change
to the app gets a new version: see [docs/RELEASING.md](docs/RELEASING.md).

## [Unreleased]

## [1.1.0] - 2026-09-23

First public release.

### Added

- Installer (`SafeSweep-Setup-x64.exe`), MSI (`SafeSweep-x64.msi`) and portable
  (`SafeSweep-Portable-x64.exe`) builds. All are self-contained, so .NET does not need to
  be installed. Uninstalling also removes the scheduled clean, and keeps the quarantine.
- A select-all check box in the header of every result table, with a partial state when
  only some rows are selected. It acts only on the rows shown by the current search or
  filter. Space toggles the highlighted row.
- Select all, and the number and size of the selected items, in Quarantine.
- The version and links to the project, releases and issue tracker in Settings and the
  sidebar.
- Clean type badges and an empty state in History.

### Changed

- Thin dark scroll bars that widen when the pointer is over them, with space reserved so
  content never shifts when a scroll bar appears.
- Consistent check boxes, tabs, tables and buttons across all pages, with visible keyboard
  focus.
- The layout adapts to the window: an icon-only sidebar below 1180 pixels wide and shorter
  page headers below 760 pixels high. Tested at 1366x768, 1920x1080 and 2560x1440 with
  100%, 125% and 150% display scaling. The minimum window size is 940x540.
- Long paths are shortened in the middle, keeping the file name visible, with the full path
  in a tooltip.

### Fixed

- Buttons overlapping text in narrow windows.
- Paths on the Protection page being cut off.
- The Name column of result tables being squeezed too narrow to read.

### Security

- SafeSweep's own program folder and executable are protected, including portable copies
  in Downloads or on the Desktop.
- More cloud sync folders are recognised and protected (Dropbox, Google Drive, iCloud,
  Box, pCloud and MEGA, besides OneDrive).
- Folders that contain a software project (for example `.git`, `*.sln` or `package.json`)
  are never reported as application leftovers or cleaned from temporary folders.
- System folders on other drives (such as `D:\Windows` or `D:\Program Files`) are refused.
- A program that starts between the scan and the clean locks its cache items again.
- Files that disappeared after the scan are reported as skipped instead of as errors.

## 1.0.0 - 2026-09-23

Development build, not released publicly.

### Added

- Quick, Full and Custom scans with three cleaning levels: Safe Clean, Deep Clean and
  Manual Review.
- Detailed explanation for every item: location, size, dates, category, risk level, why it
  is removable and what happens afterwards.
- Review tools for large files, duplicate files (SHA-256 verified), old and unused files,
  leftovers of uninstalled applications and empty folders.
- Quarantine with restore to the original location and automatic purge after a retention
  period (30 days by default).
- Startup app manager (enable or disable, nothing is deleted).
- Scheduled Safe Clean through Windows Task Scheduler.
- Cleaning history, and a live log with an audit line for every deletion, quarantine,
  restore, purge and refusal.
- Simulation mode and light and dark themes.
- Protection page listing every protected location, rule and exclusion, and your own
  exclusions.

### Security

- Default-deny protection layer checked at scan time and again immediately before every
  deletion, with protection against path traversal, 8.3 short names, junctions, symbolic
  links, hard links, cloud placeholders and files that change between scan and clean.

[Unreleased]: https://github.com/trxxst/SafeSweep/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/trxxst/SafeSweep/releases/tag/v1.1.0
