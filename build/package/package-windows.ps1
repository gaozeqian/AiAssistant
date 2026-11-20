#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Windows release artifacts for AiAssistant (self-contained folder + zip + optional MSIX).

.DESCRIPTION
    Produces a self-contained win-x64 publish, optionally Authenticode-signs every binary,
    zips the folder, and can additionally pack a sideloadable MSIX when the Windows SDK is present.

    Signing is opt-in: set AIASSISTANT_SIGN_CERT_THUMBPRINT (and optionally AIASSISTANT_TIMESTAMP_URL).
    When a thumbprint is supplied but signtool is missing, the script fails instead of silently
    producing an unsigned release.

.EXAMPLE
    pwsh build/package/package-windows.ps1 -Version 0.1.0

.EXAMPLE
    $env:AIASSISTANT_SIGN_CERT_THUMBPRINT = "ABCDEF..."
    pwsh build/package/package-windows.ps1 -Msix -Publisher "CN=AiAssistant"
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$OutputRoot = "artifacts/packages",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [string]$TimestampUrl = $env:AIASSISTANT_TIMESTAMP_URL,
    [switch]$FrameworkDependent,
    [switch]$Msix,
    [switch]$RequireSigning,
    [string]$Publisher = "CN=AiAssistant"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
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

$outputDir = Join-Path $repoRoot $OutputRoot
$stageDir = Join-Path $outputDir "staging\$runtime"
$msixStage = Join-Path $outputDir "staging\msix-$runtime"
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

if (Test-Path $stageDir) {
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

$binaries = Get-ChildItem -Path $stageDir -Recurse -Include *.exe, *.dll
if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signtool = Find-SdkTool -Name "signtool.exe"
    if ($null -eq $signtool) {
        throw "Signing was requested but signtool.exe was not found. Install the Windows SDK or unset AIASSISTANT_SIGN_CERT_THUMBPRINT."
    }

    Write-Host "Signing $($binaries.Count) binaries with certificate $SignThumbprint"
    foreach ($binary in $binaries) {
        & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $SignThumbprint $binary.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $($binary.FullName)"
        }
    }

    foreach ($binary in $binaries) {
        & $signtool verify /pa $binary.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed for $($binary.FullName)"
        }
    }
}
else {
    Write-Warning "No signing certificate supplied; producing an unsigned developer build."
}

$zipPath = Join-Path $outputDir "AiAssistant-$Version-$runtime.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "zip: $zipPath"

if ($Msix) {
    if ([string]::IsNullOrWhiteSpace($Publisher)) {
        throw "MSIX packaging requires -Publisher matching the signing certificate subject."
    }

    $makeappx = Find-SdkTool -Name "makeappx.exe"
    if ($null -eq $makeappx) {
        throw "MSIX packaging was requested but makeappx.exe was not found. Install the Windows SDK."
    }

    if (Test-Path $msixStage) {
        Remove-Item -Path $msixStage -Recurse -Force
    }

    New-Item -ItemType Directory -Path $msixStage -Force | Out-Null
    Copy-Item -Path (Join-Path $stageDir "*") -Destination $msixStage -Recurse -Force
    Copy-Item -Path (Join-Path $PSScriptRoot "assets\msix") -Destination (Join-Path $msixStage "Assets") -Recurse -Force

    $msixVersion = $Version
    if ($Version -match '^\d+\.\d+\.\d+$') {
        $msixVersion = "$Version.0"
    }
    elseif ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "MSIX requires a numeric version such as 0.1.0 or 0.1.0.0, got '$Version'."
    }

    $manifest = Get-Content (Join-Path $PSScriptRoot "windows\Package.appxmanifest.template") -Raw
    $manifest = $manifest.Replace("__PUBLISHER__", $Publisher).Replace("__VERSION__", $msixVersion)
    Set-Content -Path (Join-Path $msixStage "AppxManifest.xml") -Value $manifest -Encoding UTF8

    $msixPath = Join-Path $outputDir "AiAssistant-$Version-$runtime.msix"
    if (Test-Path $msixPath) {
        Remove-Item $msixPath -Force
    }

    & $makeappx pack /d $msixStage /p $msixPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx failed with exit code $LASTEXITCODE"
    }

    if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
        $signtool = Find-SdkTool -Name "signtool.exe"
        & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $SignThumbprint $msixPath
        if ($LASTEXITCODE -ne 0) {
            throw "MSIX signing failed"
        }
    }

    Write-Host "msix: $msixPath"
}

$checksumFiles = Get-ChildItem -Path $outputDir -File |
    Where-Object { $_.Extension -in @(".zip", ".msix") } |
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
