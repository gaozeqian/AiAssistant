#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Windows portable zip for AiAssistant from the shared publish payload.

.DESCRIPTION
    Publishes once to artifacts/publish/win-x64, optionally Authenticode-signs every binary,
    copies legal documents and bundled models, then produces
    artifacts/dist/<version>/AiAssistant-<version>-windows-x64-portable[-dev].zip with a
    single top-level directory. The MSI reuses the same publish payload
    (package-windows-msi.ps1 -PayloadDirectory).

.EXAMPLE
    pwsh build/package/package-windows.ps1 -Version 0.1.0 -SkipModelBundle

.EXAMPLE
    $env:AIASSISTANT_SIGN_CERT_THUMBPRINT = "ABCDEF..."
    pwsh build/package/package-windows.ps1 -DevSelfSigned
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Help,
    [string]$Version = "",
    [string]$OutputRoot = "",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [string]$TimestampUrl = $env:AIASSISTANT_TIMESTAMP_URL,
    [string]$VerifyRootPath = $env:AIASSISTANT_SIGN_ROOT_CER,
    [switch]$FrameworkDependent,
    [switch]$RequireSigning,
    [switch]$SkipTimestamp,
    [switch]$SkipModelBundle,
    [switch]$DevSelfSigned
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
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
$runtime = "win-x64"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.VersionPrefix
}

$Version = $Version.TrimStart("v")
$suffix = if ($DevSelfSigned) { "-dev" } else { "" }
$distDir = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $artifactsRoot "dist\$Version"
} elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path $repoRoot $OutputRoot
}

$publishDir = Join-Path $artifactsRoot "publish\$runtime"
$portableName = "AiAssistant-$Version-windows-x64"
$portableStage = Join-Path $artifactsRoot "staging\$runtime-portable\$portableName"

if ($RequireSigning -and [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    throw "RequireSigning was set but AIASSISTANT_SIGN_CERT_THUMBPRINT is empty."
}

if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = "http://timestamp.digicert.com"
}
$selfContained = -not $FrameworkDependent

function Find-SdkTool {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin")
    )
    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path $root)) {
            continue
        }

        $found = Get-ChildItem -Path $root -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'x64' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $found) {
            return $found.FullName
        }
    }

    return $null
}

Write-Host "== AiAssistant Windows portable package =="
Write-Host "version=$Version runtime=$runtime selfContained=$selfContained publish=$publishDir"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet publish (Join-Path $repoRoot "src\AiAssistant.Host\AiAssistant.Host.csproj") `
    -c Release -r $runtime --self-contained $selfContained.ToLowerInvariant() `
    -p:Version=$Version -p:InformationalVersion=$Version `
    -tl:off -v:m -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$modelBundleSource = Join-Path $artifactsRoot "cache\models\bundle"
if ($SkipModelBundle) {
    Write-Warning "Model bundle skipped; the portable package will not include wake-word/ASR/TTS assets."
}
else {
    if (-not (Test-Path -Path $modelBundleSource)) {
        throw "Model bundle not found at $modelBundleSource. Run 'bash build/models/stage-bundle.sh' first, or pass -SkipModelBundle."
    }

    Copy-Item -Path $modelBundleSource -Destination (Join-Path $publishDir "models") -Recurse -Force
    foreach ($required in @(
        (Join-Path $publishDir "models\sherpa\kws\encoder.onnx"),
        (Join-Path $publishDir "models\sherpa\model.int8.onnx"),
        (Join-Path $publishDir "models\sherpa\silero_vad.onnx"),
        (Join-Path $publishDir "models\sherpa\tts\model.onnx"))) {
        if (-not (Test-Path -Path $required)) {
            throw "Bundled model missing: $required"
        }
    }
}

. (Join-Path $PSScriptRoot "legal-files.ps1")
Copy-LegalFiles -Destination $publishDir -RepoRoot $repoRoot

$binaries = Get-ChildItem -Path $publishDir -Recurse -Include *.exe, *.dll
if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Find-SdkTool -Name "signtool.exe"
    if ($null -eq $signtool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK or unset AIASSISTANT_SIGN_CERT_THUMBPRINT."
    }

    Write-Host "Signing $($binaries.Count) binaries in the shared publish payload"
    foreach ($binary in $binaries) {
        if ($SkipTimestamp) {
            & $signtool sign /fd SHA256 /sha1 $SignThumbprint $binary.FullName
        }
        else {
            & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $SignThumbprint $binary.FullName
        }

        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $($binary.FullName)"
        }
    }

    foreach ($binary in $binaries) {
        if ([string]::IsNullOrWhiteSpace($VerifyRootPath)) {
            & $signtool verify /pa $binary.FullName | Out-Null
        }
        else {
            & $signtool verify /pa /r $VerifyRootPath $binary.FullName | Out-Null
        }

        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed for $($binary.FullName)"
        }
    }
}
else {
    Write-Warning "No signing certificate supplied; producing an unsigned developer build."
}

if (Test-Path $portableStage) {
    Remove-Item -Path $portableStage -Recurse -Force
}
New-Item -ItemType Directory -Path $portableStage -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $portableStage -Recurse -Force

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$zipPath = Join-Path $distDir "AiAssistant-$Version-windows-x64-portable$suffix.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path $portableStage -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "portable zip: $zipPath"

. (Join-Path $PSScriptRoot "checksums.ps1")
Write-DistChecksums -Directory $distDir -Version $Version
Write-Host "Windows portable package complete."
