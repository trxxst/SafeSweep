<#
.SYNOPSIS
    Builds, tests and packages a SafeSweep release.

.DESCRIPTION
    Produces, in .\dist:
      SafeSweep-Setup-x64.exe     installer for most users (per-machine, Start menu entry, uninstaller)
      SafeSweep-x64.msi           the same installer as a plain MSI, for managed/IT deployment
      SafeSweep-Portable-x64.exe  single-file portable build, no installation
      SHA256SUMS.txt              SHA-256 checksums of the three files above

    All three are self-contained .NET 9 (win-x64) builds, so users do not need to
    install .NET. Nothing is signed: see docs/code-signing.md.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER ExpectedVersion
    Fails the build unless the version in Directory.Build.props equals this
    (CI passes the tag without its leading "v").

.PARAMETER SkipTests
    Skips the unit/UI tests. Only for quick local packaging experiments.

.PARAMETER SkipFormatCheck
    Skips the "dotnet format whitespace" check.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ExpectedVersion = '',
    [switch] $SkipTests,
    [switch] $SkipFormatCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root 'dist'
$Work = Join-Path $Root 'artifacts'
$AppProject = Join-Path $Root 'src\SafeSweep.App\SafeSweep.App.csproj'
$Solution = Join-Path $Root 'SafeSweep.sln'
$MsiProject = Join-Path $Root 'installer\msi\SafeSweep.Installer.wixproj'
$BundleProject = Join-Path $Root 'installer\bundle\SafeSweep.Bundle.wixproj'
$Runtime = 'win-x64'

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Runs an external command and stops the script if it fails.
function Invoke-Checked([string] $FilePath, [string[]] $Arguments) {
    Write-Host "    $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Assert-FileVersion([string] $Path, [string] $Version) {
    $info = (Get-Item $Path).VersionInfo
    $expectedFile = "$Version.0"
    $problems = @()
    if ($info.ProductName -ne 'SafeSweep') { $problems += "ProductName is '$($info.ProductName)'" }
    if ($info.ProductVersion -ne $Version) { $problems += "ProductVersion is '$($info.ProductVersion)', expected '$Version'" }
    if ($info.FileVersion -ne $expectedFile) { $problems += "FileVersion is '$($info.FileVersion)', expected '$expectedFile'" }
    if ([string]::IsNullOrWhiteSpace($info.LegalCopyright)) { $problems += 'LegalCopyright is empty' }
    if ([string]::IsNullOrWhiteSpace($info.CompanyName)) { $problems += 'CompanyName is empty' }
    if ($problems.Count -gt 0) {
        throw "Version information of $(Split-Path -Leaf $Path) is wrong: $($problems -join '; ')."
    }
    Write-Host "    $(Split-Path -Leaf $Path): $($info.ProductName) $($info.ProductVersion) (file $($info.FileVersion)), $($info.LegalCopyright)"
}

# --- Version ---------------------------------------------------------------
[xml] $props = Get-Content (Join-Path $Root 'Directory.Build.props')
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
$Version = if ($versionNode) { $versionNode.InnerText.Trim() } else { '' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Could not read a MAJOR.MINOR.PATCH VersionPrefix from Directory.Build.props (got '$Version')."
}
if ($ExpectedVersion -and $ExpectedVersion -ne $Version) {
    throw "Version mismatch: Directory.Build.props says $Version but the release tag says $ExpectedVersion."
}
Write-Step "Building SafeSweep $Version ($Configuration, $Runtime)"

Invoke-Checked 'dotnet' @('--version')

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Path $Dist, $Work | Out-Null

# --- Restore, lint, build, test --------------------------------------------
Write-Step 'Restoring packages'
Invoke-Checked 'dotnet' @('restore', $Solution)

if (-not $SkipFormatCheck) {
    Write-Step 'Checking formatting (dotnet format whitespace)'
    Invoke-Checked 'dotnet' @('format', 'whitespace', $Solution, '--verify-no-changes', '-v', 'minimal')
}

Write-Step 'Building (warnings are errors)'
Invoke-Checked 'dotnet' @('build', $Solution, '-c', $Configuration, '--no-restore', '-warnaserror', '-nologo')

if (-not $SkipTests) {
    # Integration tests scan the real machine (read-only); they run in CI on a
    # clean runner but are left out here so results do not depend on the PC.
    Write-Step 'Running tests'
    Invoke-Checked 'dotnet' @('test', $Solution, '-c', $Configuration, '--no-build', '--nologo',
        '--filter', 'Category!=Integration',
        '--logger', 'trx;LogFileName=results.trx',
        '--results-directory', (Join-Path $Work 'test-results'))
}

# --- Publish ---------------------------------------------------------------
$commonPublish = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--nologo',
    '-p:DebugType=None', '-p:DebugSymbols=false')

Write-Step 'Publishing the portable single-file build'
$portableDir = Join-Path $Work 'portable'
Invoke-Checked 'dotnet' (@('publish', $AppProject, '-o', $portableDir) + $commonPublish + @(
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true'))
$portableExe = Join-Path $Dist 'SafeSweep-Portable-x64.exe'
Copy-Item (Join-Path $portableDir 'SafeSweep.exe') $portableExe
Assert-FileVersion $portableExe $Version

Write-Step 'Publishing the installer payload (self-contained folder)'
$appDir = Join-Path $Work 'app'
Invoke-Checked 'dotnet' (@('publish', $AppProject, '-o', $appDir) + $commonPublish)
Assert-FileVersion (Join-Path $appDir 'SafeSweep.exe') $Version
Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $appDir 'LICENSE.txt')
Copy-Item (Join-Path $Root 'THIRD-PARTY-NOTICES.md') (Join-Path $appDir 'THIRD-PARTY-NOTICES.md')

# --- Installer -------------------------------------------------------------
Write-Step 'Building the MSI'
$msiOut = Join-Path $Work 'msi'
# No trailing backslashes in these arguments: Windows PowerShell would turn
# '\"' into an escaped quote. The project adds the trailing slash itself.
Invoke-Checked 'dotnet' @('build', $MsiProject, '-c', 'Release', '--nologo', '-o', $msiOut,
    "-p:ProductVersion=$Version",
    "-p:AppPublishDir=$appDir")
$msi = Join-Path $msiOut 'SafeSweep-x64.msi'
if (-not (Test-Path $msi)) { throw "The MSI was not produced at $msi." }
Copy-Item $msi (Join-Path $Dist 'SafeSweep-x64.msi')

Write-Step 'Building the setup program'
$bundleOut = Join-Path $Work 'bundle'
Invoke-Checked 'dotnet' @('build', $BundleProject, '-c', 'Release', '--nologo', '-o', $bundleOut,
    "-p:ProductVersion=$Version",
    "-p:MsiPath=$msi")
$setup = Join-Path $bundleOut 'SafeSweep-Setup-x64.exe'
if (-not (Test-Path $setup)) { throw "The setup program was not produced at $setup." }
Copy-Item $setup (Join-Path $Dist 'SafeSweep-Setup-x64.exe')

# --- Checksums -------------------------------------------------------------
Write-Step 'Writing SHA256SUMS.txt'
$files = 'SafeSweep-Setup-x64.exe', 'SafeSweep-x64.msi', 'SafeSweep-Portable-x64.exe'
$lines = foreach ($name in $files) {
    $hash = (Get-FileHash (Join-Path $Dist $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
# ASCII + LF so "sha256sum -c SHA256SUMS.txt" works as well as PowerShell.
[System.IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), [System.Text.Encoding]::ASCII)

Write-Step "Done: SafeSweep $Version"
Get-ChildItem $Dist | ForEach-Object {
    Write-Host ('    {0,-30} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB))
}
