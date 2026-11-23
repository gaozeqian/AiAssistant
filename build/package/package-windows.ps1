#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Windows release artifacts for AiAssistant (self-contained folder + zip).

.DESCRIPTION
    Produces a self-contained win-x64 publish, optionally Authenticode-signs every binary,
    copies the legal documents (LICENSE, EULA, THIRD-PARTY-NOTICES) and zips the folder.
    The MSI is produced separately by package-windows-msi.ps1; MSIX is no longer supported.

    Signing is opt-in: set AIASSISTANT_SIGN_CERT_THUMBPRINT (and optionally AIASSISTANT_TIMESTAMP_URL).
    When a thumbprint is supplied but signtool is missing, the script fails instead of silently
    producing an unsigned release.

.EXAMPLE
    pwsh build/package/package-windows.ps1 -Version 0.1.0

.EXAMPLE
    $env:AIASSISTANT_SIGN_CERT_THUMBPRINT = "ABCDEF..."
    pwsh build/package/package-windows.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Help,
    [string]$Version = "",
    [string]$OutputRoot = "artifacts/package",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [string]$TimestampUrl = $env:AIASSISTANT_TIMESTAMP_URL,
    [string]$VerifyRootPath = $env:AIASSISTANT_SIGN_ROOT_CER,
    [switch]$FrameworkDependent,
    [switch]$RequireSigning,
    [switch]$SkipTimestamp,
    [switch]$SkipModelBundle
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

if ($RequireSigning -and [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    throw "RequireSigning was set but AIASSISTANT_SIGN_CERT_THUMBPRINT is empty."
}

if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = "http://timestamp.digicert.com"
}

$outputDir = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
$stageDir = Join-Path $artifactsRoot "build\staging\$runtime"
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

Write-Host "== AiAssistant Windows package =="
Write-Host "version=$Version runtime=$runtime selfContained=$selfContained"

if (Test-Path $stageDir -and $PSCmdlet.ShouldProcess($stageDir, "Remove stale staging directory")) {
    Remove-Item -Path $stageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
$publishArgs = @(
    "publish", (Join-Path $repoRoot "src\AiAssistant.Host\AiAssistant.Host.csproj"),
    "-c", "Release",
    "-r", $runtime,
    "--self-contained", $selfContained.ToString().ToLowerInvariant(),
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-tl:off", "-v:m",
    "-o", $stageDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Local model assets ship with the installer; only the chat/embedding models stay external.
$modelBundleSource = Join-Path $artifactsRoot "cache\models\bundle"
$modelBundleTarget = Join-Path $stageDir "models"
if ($SkipModelBundle) {
    Write-Warning "Model bundle skipped; the installer will not include wake-word/ASR/TTS assets."
}
else {
    if (-not (Test-Path -Path $modelBundleSource)) {
        throw "Model bundle not found at $modelBundleSource. Run 'bash build/models/stage-bundle.sh' first, or pass -SkipModelBundle for a developer build."
    }

    Copy-Item -Path $modelBundleSource -Destination $modelBundleTarget -Recurse -Force
    foreach ($required in @(
        (Join-Path $modelBundleTarget "sherpa\kws\encoder.onnx"),
        (Join-Path $modelBundleTarget "sherpa\model.int8.onnx"),
        (Join-Path $modelBundleTarget "sherpa\silero_vad.onnx"),
        (Join-Path $modelBundleTarget "sherpa\tts\model.onnx"))) {
        if (-not (Test-Path -Path $required)) {
            throw "Bundled model missing: $required"
        }
    }

    $bundleBytes = (Get-ChildItem -Path $modelBundleTarget -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Bundled models: {0:N1} MB" -f ($bundleBytes / 1MB))
}

# Legal documents are part of the deliverable, not an optional extra.
. (Join-Path $PSScriptRoot "legal-files.ps1")
Copy-LegalFiles -Destination $stageDir -RepoRoot $repoRoot

$binaries = Get-ChildItem -Path $stageDir -Recurse -Include *.exe, *.dll
if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Find-SdkTool -Name "signtool.exe"
    if ($null -eq $signtool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK or unset AIASSISTANT_SIGN_CERT_THUMBPRINT."
    }

    Write-Host "Signing $($binaries.Count) binaries with certificate $SignThumbprint"
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
            # Self-signed rehearsal: treat the supplied certificate as the trusted root.
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

$zipPath = Join-Path $outputDir "AiAssistant-$Version-$runtime.zip"
if (Test-Path $zipPath -and $PSCmdlet.ShouldProcess($zipPath, "Remove stale zip")) {
    Remove-Item $zipPath -Force
}

if ($PSCmdlet.ShouldProcess($zipPath, "Create release zip")) {
    Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
}
Write-Host "zip: $zipPath"

$checksumFiles = Get-ChildItem -Path $outputDir -File |
    Where-Object { $_.Extension -eq ".zip" } |
    Sort-Object Name
if ($checksumFiles.Count -gt 0) {
    $checksumLines = foreach ($file in $checksumFiles) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }

    $checksumPath = Join-Path $outputDir "SHA256SUMS"
    Set-Content -Path $checksumPath -Value $checksumLines -Encoding ASCII
    Write-Host "checksums: $checksumPath"
}

Write-Host "Windows package complete."
