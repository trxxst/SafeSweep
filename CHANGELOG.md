# Changelog

All notable changes to SafeSweep are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
SafeSweep uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-09-23

First public release.

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
- Cleaning history, live log with an audit line for every deletion, quarantine, restore,
  purge and refusal.
- Simulation mode, light and dark theme, and a responsive layout for small and large
  screens.
- Protection page listing every protected location, rule and exclusion, and your own
  exclusions.
- Installer (`SafeSweep-Setup-x64.exe`), MSI (`SafeSweep-x64.msi`) and portable
  (`SafeSweep-Portable-x64.exe`) builds, all self-contained.

### Security

- Default-deny protection layer checked at scan time and again immediately before every
  deletion, with protection against path traversal, 8.3 short names, junctions, symbolic
  links, hard links, cloud placeholders and files that change between scan and clean.
- SafeSweep's own install folder and executable are protected, including portable copies.

[Unreleased]: https://github.com/trxxst/SafeSweep/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/trxxst/SafeSweep/releases/tag/v1.0.0
