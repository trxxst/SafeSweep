<#
.SYNOPSIS
    Raises the SafeSweep version everywhere it is used.

.DESCRIPTION
    - Sets VersionPrefix in Directory.Build.props (the app, the exe metadata and
      the installers all read it from there).
    - Turns the "Unreleased" section of CHANGELOG.md into a section for the new
      version and updates the comparison links at the bottom.
    - Creates docs/release-notes/v<version>.md from a template if it does not
      exist yet. Its TODO lines must be replaced before the release tag is
      pushed; the release workflow refuses notes that still contain TODO.

    Follow semantic versioning: patch for fixes only, minor for new features
    that keep existing behaviour, major for changes that break it.

.PARAMETER Part
    Which part to raise: major, minor or patch.

.PARAMETER Version
    An exact MAJOR.MINOR.PATCH version instead of -Part.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\bump-version.ps1 -Part patch
#>
[CmdletBinding(DefaultParameterSetName = 'Part')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Part')]
    [ValidateSet('major', 'minor', 'patch')]
    [string] $Part,

    [Parameter(Mandatory, ParameterSetName = 'Exact')]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $Date = (Get-Date -Format 'yyyy-MM-dd')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$PropsPath = Join-Path $Root 'Directory.Build.props'
$ChangelogPath = Join-Path $Root 'CHANGELOG.md'
$NotesDir = Join-Path $Root 'docs\release-notes'
$Repo = 'https://github.com/trxxst/SafeSweep'
$Utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string] $Path) { [System.IO.File]::ReadAllText($Path) }
function Write-Text([string] $Path, [string] $Text) { [System.IO.File]::WriteAllText($Path, $Text, $Utf8) }
function Get-NewLine([string] $Text) { if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" } }

# --- Work out the versions -------------------------------------------------
$props = Read-Text $PropsPath
$match = [regex]::Match($props, '<VersionPrefix>(\d+)\.(\d+)\.(\d+)</VersionPrefix>')
if (-not $match.Success) { throw 'No MAJOR.MINOR.PATCH VersionPrefix found in Directory.Build.props.' }
$current = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
[int] $major = $match.Groups[1].Value
[int] $minor = $match.Groups[2].Value
[int] $patch = $match.Groups[3].Value

if ($PSCmdlet.ParameterSetName -eq 'Part') {
    switch ($Part) {
        'major' { $Version = "$($major + 1).0.0" }
        'minor' { $Version = "$major.$($minor + 1).0" }
        'patch' { $Version = "$major.$minor.$($patch + 1)" }
    }
}

if ([version] $Version -le [version] $current) {
    throw "The new version ($Version) must be higher than the current one ($current)."
}

# --- CHANGELOG.md ----------------------------------------------------------
$changelog = Read-Text $ChangelogPath
$nl = Get-NewLine $changelog
# Patterns stop at the line ending so CRLF files keep their "\r".
$heading = '(?m)^## \[Unreleased\][ \t]*(?=\r?$)'
$linkLine = '(?m)^\[Unreleased\]: [^\r\n]*'
if (-not [regex]::IsMatch($changelog, $heading)) { throw 'CHANGELOG.md has no "## [Unreleased]" section.' }
if (-not [regex]::IsMatch($changelog, $linkLine)) { throw 'CHANGELOG.md has no "[Unreleased]: ..." link line.' }
if ([regex]::IsMatch($changelog, "(?m)^## \[$([regex]::Escape($Version))\]")) { throw "CHANGELOG.md already has a section for $Version." }

$unreleased = [regex]::Match($changelog, '(?ms)^## \[Unreleased\][ \t]*\r?$(.*?)(?=^## |^\[Unreleased\]:)').Groups[1].Value
if ($unreleased.Trim().Length -eq 0) {
    Write-Warning 'The Unreleased section of CHANGELOG.md is empty. Describe the changes under the new version before releasing.'
}

$changelog = ([regex] $heading).Replace($changelog, "## [Unreleased]$nl$nl## [$Version] - $Date", 1)
$changelog = ([regex] $linkLine).Replace($changelog, "[Unreleased]: $Repo/compare/v$Version...HEAD$nl[$Version]: $Repo/releases/tag/v$Version", 1)
Write-Text $ChangelogPath $changelog

# --- Directory.Build.props (last, so a failure above leaves it unchanged) ---
Write-Text $PropsPath ($props.Replace("<VersionPrefix>$current</VersionPrefix>", "<VersionPrefix>$Version</VersionPrefix>"))

# --- Release notes ---------------------------------------------------------
$notesPath = Join-Path $NotesDir "v$Version.md"
if (-not (Test-Path $notesPath)) {
    $template = @"
# SafeSweep v$Version

## Downloads

| File | Who it is for |
| --- | --- |
| **``SafeSweep-Setup-x64.exe``** | **Download this one if you are not sure.** Installs SafeSweep with a Start menu entry and an uninstaller. |
| ``SafeSweep-Portable-x64.exe`` | Runs without installing, from any folder. |
| ``SafeSweep-x64.msi`` | For IT administrators deploying with Group Policy, Intune or similar tools. |
| ``SHA256SUMS.txt`` | SHA-256 checksums of the files above. |

Requirements: Windows 10 or Windows 11, 64-bit. .NET is included; nothing else to install.

**Windows SmartScreen:** these files are not code-signed yet, so Windows may show
"Windows protected your PC" the first time. Click **More info**, check the file name, then
**Run anyway**. To verify a download, run
``Get-FileHash .\SafeSweep-Setup-x64.exe -Algorithm SHA256`` in PowerShell and compare the
result with ``SHA256SUMS.txt``.

## Highlights

- TODO: the main user-visible changes (see CHANGELOG.md).

## Safety

- TODO: changes to what SafeSweep protects or deletes, or "No changes to the protection rules."

## Known limitations

- TODO
"@
    Write-Text $notesPath (($template -replace "`r`n", "`n") -replace "`n", "`r`n")
    $notesState = 'created from the template (replace the TODO lines)'
}
else {
    $notesState = 'already exists'
}

Write-Host "SafeSweep $current -> $Version"
Write-Host "  Directory.Build.props  updated"
Write-Host "  CHANGELOG.md           section [$Version] - $Date added"
Write-Host "  docs\release-notes\v$Version.md $notesState"
Write-Host ''
Write-Host 'Next: review CHANGELOG.md and the release notes, commit, then tag and push:'
Write-Host "  git tag -a v$Version -m `"SafeSweep $Version`""
Write-Host "  git push origin main v$Version"
