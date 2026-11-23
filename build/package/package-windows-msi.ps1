<#
.SYNOPSIS
    Build and sign the Windows MSI installer for AiAssistant.

.DESCRIPTION
    Resolves the repository root with git rev-parse, stages the payload and compiles Package.msi.wxs.
    Signing is opt-in (-SkipSigning); use -WhatIf to preview destructive staging operations.
    Exit codes: 0 success / 1 build failure.

.EXAMPLE
    pwsh build/package/package-windows-msi.ps1 -Version 0.1.0 -CertificatePath cert.cer
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Help,
    [Parameter(Mandatory = $false)][string]$Version = "",
    [Parameter(Mandatory = $false)][string]$CertificatePath = "",
    [string]$OutputDirectory = "artifacts/package",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [switch]$SkipSigning,
    [switch]$SkipModelBundle,
    [switch]$DevSelfSigned
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}
if ([string]::IsNullOrWhiteSpace($Version) -or [string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Error "-Version and -CertificatePath are required."
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
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$stage = Join-Path $artifactsRoot "build\staging\msi"
$msiPath = Join-Path $outputRoot "AiAssistant-$Version-win-x64.msi"
$wxs = Join-Path $repoRoot "build\package\windows\Package.msi.wxs"

if (-not (Test-Path $CertificatePath)) {
    throw "Certificate not found: $CertificatePath"
}

New-Item -ItemType Directory -Force $outputRoot | Out-Null
if (Test-Path $stage -and $PSCmdlet.ShouldProcess($stage, "Remove stale MSI staging directory")) {
    Remove-Item $stage -Recurse -Force -Confirm:$false
}

Write-Host "== AiAssistant MSI =="
Write-Host "version=$Version stage=$stage"

dotnet publish (Join-Path $repoRoot "src\AiAssistant.Host\AiAssistant.Host.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:InformationalVersion=$Version `
    -tl:off -v:m -o $stage

if ($DevSelfSigned) {
    Copy-Item $CertificatePath (Join-Path $stage "AiAssistant.cer") -Force
}
else {
    Write-Host "Skipping certificate import payload: MSI is not in --dev-self-signed mode."
}
Copy-Item (Join-Path $repoRoot "build\package\assets\aiassistant.ico") (Join-Path $stage "aiassistant.ico") -Force

# Local model assets ship with the installer; only the chat/embedding models stay external.
$modelBundleSource = Join-Path $artifactsRoot "cache\models\bundle"
if ($SkipModelBundle) {
    Write-Warning "Model bundle skipped; the installer will not include wake-word/ASR/TTS assets."
}
else {
    if (-not (Test-Path -Path $modelBundleSource)) {
        throw "Model bundle not found at $modelBundleSource. Run 'bash build/models/stage-bundle.sh' first, or pass -SkipModelBundle."
    }

    Copy-Item -Path $modelBundleSource -Destination (Join-Path $stage "models") -Recurse -Force
    foreach ($required in @(
        (Join-Path $stage "models\sherpa\kws\encoder.onnx"),
        (Join-Path $stage "models\sherpa\model.int8.onnx"),
        (Join-Path $stage "models\sherpa\silero_vad.onnx"),
        (Join-Path $stage "models\sherpa\tts\model.onnx"))) {
        if (-not (Test-Path -Path $required)) {
            throw "Bundled model missing: $required"
        }
    }
}

. (Join-Path $PSScriptRoot "legal-files.ps1")
Copy-LegalFiles -Destination $stage -RepoRoot $repoRoot

if (-not $SkipSigning -and -not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $signtool) {
        throw "signtool.exe not found in Windows SDK"
    }

    $binaries = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in ".exe", ".dll" }
    Write-Host "Signing $($binaries.Count) binaries"
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
    & $signtool.FullName sign /fd SHA256 /sha1 $SignThumbprint $msiPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "MSI signing failed"
    }
}

Write-Host "msi: $msiPath"
