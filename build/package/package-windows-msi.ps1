#Requires -Version 5.1
<#
.SYNOPSIS
    Build and sign the Windows MSI installer from the shared publish payload.

.DESCRIPTION
    Reuses artifacts/publish/win-x64 (produced by package-windows.ps1), stages it under
    artifacts/staging/win-x64-msi, optionally embeds a developer self-signed certificate,
    compiles Package.msi.wxs with WiX, and writes
    artifacts/dist/<version>/AiAssistant-<version>-windows-x64[-dev].msi.
    No second dotnet publish is performed.

.EXAMPLE
    pwsh build/package/package-windows-msi.ps1 -Version 0.1.0
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Help,
    [Parameter(Mandatory = $false)][string]$Version = "",
    [Parameter(Mandatory = $false)][string]$CertificatePath = "",
    [string]$PayloadDirectory = "",
    [string]$OutputDirectory = "",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [switch]$SkipSigning,
    [switch]$DevSelfSigned
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "-Version is required."
    exit 2
}

$ErrorActionPreference = "Stop"
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "Cannot resolve repository root: run this script from a Git working tree."
}
$repoRoot = $repoRoot.Trim()
$artifactsRoot = if ([string]::IsNullOrWhiteSpace($env:AIASSISTANT_ARTIFACTS_ROOT)) {
    Join-Path $repoRoot "artifacts"
} else {
    $env:AIASSISTANT_ARTIFACTS_ROOT
}
$Version = $Version.TrimStart("v")
$suffix = if ($DevSelfSigned) { "-dev" } else { "" }

$payload = if ([string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    Join-Path $artifactsRoot "publish\win-x64"
} elseif ([System.IO.Path]::IsPathRooted($PayloadDirectory)) {
    $PayloadDirectory
} else {
    Join-Path $repoRoot $PayloadDirectory
}
if (-not (Test-Path (Join-Path $payload "AiAssistant.exe"))) {
    throw "Shared publish payload not found at $payload. Run package-windows.ps1 first (portable build publishes once)."
}

$distDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $artifactsRoot "dist\$Version"
} elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repoRoot $OutputDirectory
}
$stage = Join-Path $artifactsRoot "staging\win-x64-msi"
$msiPath = Join-Path $distDir "AiAssistant-$Version-windows-x64$suffix.msi"
$wxs = Join-Path $repoRoot "build\package\windows\Package.msi.wxs"

if ($DevSelfSigned -and [string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw "-DevSelfSigned requires -CertificatePath so the installer can import the development root."
}
if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and -not (Test-Path $CertificatePath)) {
    throw "Certificate not found: $CertificatePath"
}

New-Item -ItemType Directory -Force $distDir | Out-Null
if (Test-Path $stage) {
    Remove-Item -Path $stage -Recurse -Force -Confirm:$false
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "== AiAssistant MSI =="
Write-Host "version=$Version payload=$payload stage=$stage"

Copy-Item -Path (Join-Path $payload "*") -Destination $stage -Recurse -Force
Remove-Item (Join-Path $stage "publish-manifest.txt") -Force -ErrorAction SilentlyContinue

if ($DevSelfSigned) {
    Copy-Item $CertificatePath (Join-Path $stage "AiAssistant.cer") -Force
}
else {
    Write-Host "Skipping certificate import payload: MSI is not in -DevSelfSigned mode."
}
Copy-Item (Join-Path $repoRoot "build\package\assets\aiassistant.ico") (Join-Path $stage "aiassistant.ico") -Force

# Legal files are part of the deliverable even when the shared payload was built separately.
. (Join-Path $PSScriptRoot "legal-files.ps1")
Copy-LegalFiles -Destination $stage -RepoRoot $repoRoot

if (-not $SkipSigning -and -not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $signtool) {
        throw "signtool.exe not found in Windows SDK"
    }

    $binaries = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in ".exe", ".dll" }
    Write-Host "Signing $($binaries.Count) staged binaries"
    foreach ($binary in $binaries) {
        & $signtool.FullName sign /fd SHA256 /sha1 $SignThumbprint $binary.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $($binary.FullName)"
        }
    }
}

dotnet tool restore | Out-Null
$wixDevFlag = if ($DevSelfSigned) { '1' } else { '0' }
dotnet wix build -arch x64 -d SourceDir="$stage" -d Version="$Version" -d DEVSELFSIGNED="$wixDevFlag" -o "$msiPath" "$wxs"
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed"
}

if (-not $SkipSigning -and -not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    & $signtool.FullName sign /fd SHA256 /sha1 $SignThumbprint $msiPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "MSI signing failed"
    }
}

Write-Host "msi: $msiPath"
. (Join-Path $PSScriptRoot "checksums.ps1")
Write-DistChecksums -Directory $distDir -Version $Version
