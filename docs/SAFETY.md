# SafeSweep safety design

This document describes how SafeSweep decides what it may delete. All of it lives in
`src/SafeSweep.Core/Safety` and `src/SafeSweep.Core/Cleaning`, and no other code can delete
anything without going through `PathGuard`.

The guiding rule: **when SafeSweep is not sure, it does not delete.** Uncertain items are
shown for manual review and quarantined, never removed outright.

## Cleaning levels

* **Safe Clean** - data that is regenerated automatically and is never user data: temporary
  files older than the safety window, thumbnail and DirectX shader caches, error reports,
  browser caches. Pre-selected and deleted outright.
* **Deep Clean** - icon cache, GPU vendor shader caches, the Windows Update download cache,
  system crash dumps, Windows and application logs, extracted driver installers, Store app
  temp files, developer package caches, app caches, browser offline caches and the Recycle
  Bin. Never pre-selected; **quarantined**, not deleted. Emptying the Recycle Bin is the one
  exception, because it is permanent by nature: it is never bulk-selected and needs its own
  confirmation.
* **Manual Review** - application leftovers, empty folders, old installers, old files,
  large files and duplicates. Never pre-selected; the confirmation requires ticking
  "I have reviewed"; quarantined or sent to the Recycle Bin, **never deleted outright**.

## Protection layers

1. **Default deny for automatic cleaning.** A built-in rule may only delete inside a root
   registered at scan time (`PathGuard.TryRegisterRuleRoot`). A root is refused if it is a
   drive root, a top-level folder, the profile, AppData itself, a personal folder, a cloud
   sync root, or protected. A `%TEMP%` that points at `C:\` or at the profile is refused
   rather than cleaned, and temp rules additionally require the folder to be named `Temp` or
   `Tmp`.
2. **Path normalisation.** Every path is turned into a canonical full path before any
   comparison: `.` and `..` segments are resolved, forward slashes, doubled separators,
   trailing dots and spaces are handled, 8.3 short names are expanded, `\\?\` prefixes and
   administrative shares (`\\localhost\C$`) are recognised, and all comparisons ignore case.
3. **Protected locations** (`ProtectionPolicy`): the Windows directory (with a short list of
   explicit exceptions such as `Windows\Temp`, `SoftwareDistribution\Download`, `Minidump`
   and CBS/DISM logs), System32, SysWOW64, WinSxS, the Windows Installer cache, Prefetch,
   drivers, DriverStore, registry hives, Boot, EFI, Recovery, System Volume Information,
   `Windows.old` and upgrade folders, Program Files (all variants), WindowsApps,
   `ProgramData\Microsoft`, Package Cache, per-user Programs, app execution aliases, DPAPI
   keys, credentials, certificates, the credential vault, INetCookies, WebCache, other user
   profiles, the Default and Public profiles, your personal folders (automatic cleaning
   never enters them), cloud sync folders, SafeSweep's own data and install folder, and the
   install folder of **every installed program** (read from the uninstall registry at
   start-up, and re-read before a scan or clean whenever the last read is more than
   10 minutes old).
4. **Protected names anywhere**: `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`, boot files,
   `desktop.ini`, `NTUSER.DAT*`, `UsrClass.dat*`, registry transaction logs, wallets
   (`wallet.dat`, `*.wallet`), password databases (`*.kdbx`), SSH and PGP keys,
   certificates, Outlook data files, mailboxes and drivers (`*.sys`, `*.efi`).
5. **Protected folder names anywhere**: `.git`, `.svn`, `.hg`, `.ssh`, `.gnupg`, cloud CLI
   configuration folders, Steam, Xbox, Epic, EA, GOG, Riot and Ubisoft game libraries, wallet
   and key-store folders. Folders that look like software projects are skipped by the review
   tools.
6. **Browser profiles**: only cache folders are cleaned. `Local State` (the key that
   decrypts saved passwords), cookies, login data, history, bookmarks, sessions, local
   storage, IndexedDB, extensions and every Firefox profile database are refused.
7. **File-system checks at deletion time**: only local fixed drives; never a junction or
   symbolic link; a path that passes *through* a link is re-evaluated at its real location
   (`GetFinalPathNameByHandle`); never files with more than one hard link; never files
   marked System; never OneDrive or other cloud placeholders; review tools never touch
   AppData, program folders or cloud-synced files.
8. **Unchanged since the scan**: size and modification time must still match, a folder
   offered as empty must still be empty, and a leftover folder is re-checked against a fresh
   installed-program inventory with its contents re-verified. Files that have disappeared
   are reported as already gone, not as errors.
9. **Running programs**: browser and app cache items are shown but locked while the program
   is running, and the check is repeated right before deleting.
10. **Method enforcement**: only Safe rule items and empty folders are deleted outright.
    Everything else is forced into quarantine. The Recycle Bin is used only when it is
    guaranteed to keep the file (the bin is enabled for that drive and the file is well
    under the bin size); otherwise Windows would silently delete permanently, so the
    quarantine is used instead.
11. **Temp safety window**: a temp file must be older than 24 hours (configurable) by
    *both* modification and creation time, because installers extract files that keep an
    old modification date.
12. **Simulation mode** turns every clean into a report of what would happen.
13. **One clean at a time** across the user interface and the scheduled task (a named
    mutex).
14. **Cancellation** stops between items: an item is either fully handled or not touched,
    and the report lists what was done before the cancellation.

## Quarantine

Items are *moved* (a rename on the same drive, so nothing is rewritten). Each drive has its
own store: `%LOCALAPPDATA%\SafeSweep\Quarantine` on the system drive and
`X:\.SafeSweep-Quarantine\<your SID>` on other drives, created with an access list limited
to you, SYSTEM and Administrators. A manifest line is written *before* each move, so a
crash can never leave an item in the store that the manifest does not know about.

Restoring puts an item back at its original path and never overwrites a newer file that
has appeared there in the meantime. Items are purged automatically after the retention
period (30 days by default, 0 keeps them forever).

## How application leftovers are detected

A folder at the top of `AppData\Roaming`, `AppData\Local`, `AppData\LocalLow`, `ProgramData`
or a Store-app data folder is reported only if **all** of the following hold:

* No installed program matches it by display name, publisher, install folder, uninstall key
  name, executable name, Program Files folder, Start menu shortcut, service name, App Path
  or running-process name (fuzzy, deliberately generous matching).
* No service, startup entry, scheduled task, App Path, PATH or environment entry, or
  running process points inside it.
* It is not a Windows, shared, vendor or tool-chain folder, has no GUID or
  machine-generated name, and contains no protected data, links or project files.
* Nothing inside has changed for 90 days (configurable).
* For Store data, the folder name is a real package family name and no installed package
  has it (AppContainer folders such as `cr.sb.*` are ignored).

ProgramData and LocalLow leftovers are marked high risk (licence data, game saves). All
leftovers are quarantined.

## Known limitations

* Leftover detection is heuristic. Portable programs have no uninstall entry and can look
  uninstalled; the 90-day idle rule, high-risk marking and quarantine are the mitigations.
* Windows often does not record last-access times. "Last opened" is then shown as unknown
  and never used as a reason.
* Items belonging to a running browser or app stay locked until it is closed completely.
* The Recycle Bin size check uses the per-drive setting when Windows stores one, otherwise
  it assumes the default (about 5% of the drive).

## Testing the safety layer

`tests/SafeSweep.Tests/SafetyAuditTests.cs` tries every bypass we know of against a sandbox
that mimics a Windows installation: `..` traversal, case changes, trailing dots and spaces,
`\\?\` and administrative-share spellings, 8.3 short names, junctions and symbolic links into
protected folders, hard links, read-only and access-denied files, files locked by another
process, files that change or disappear between scan and clean, forged scan items that a
buggy scanner might produce, cancellation mid-clean, and quarantine round trips. New
protection rules should come with a test that shows the rule refusing something and a
control that shows the test would notice if it did not.
