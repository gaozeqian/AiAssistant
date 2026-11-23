<#
.SYNOPSIS
    Copies the single-source legal documents into a package staging directory.

.DESCRIPTION
    Dot-source this file, then call Copy-LegalFiles -Destination <dir> [-RepoRoot <root>].
    When -RepoRoot is omitted the repository root is resolved with git rev-parse.
    Get-Help Copy-LegalFiles -Full prints this help.
#>
function Copy-LegalFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$RepoRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
    }
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        throw "Cannot resolve repository root: run this script from a Git working tree."
    }
    $RepoRoot = $RepoRoot.Trim()

    if (-not (Test-Path -Path $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    foreach ($name in @("LICENSE", "EULA.md", "THIRD-PARTY-NOTICES.md")) {
        $source = Join-Path $RepoRoot $name
        if (-not (Test-Path -Path $source)) {
            throw "Missing legal file: $source"
        }
        Copy-Item -Path $source -Destination (Join-Path $Destination $name) -Force
    }

    [xml]$packages = Get-Content -Path (Join-Path $RepoRoot "Directory.Packages.props")
    $ortPackage = $packages.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq "Microsoft.ML.OnnxRuntime.Managed" } |
        Select-Object -First 1
    if ($null -eq $ortPackage -or [string]::IsNullOrWhiteSpace($ortPackage.Version)) {
        throw "Unable to read Microsoft.ML.OnnxRuntime.Managed version from Directory.Packages.props."
    }

    $nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path $env:USERPROFILE ".nuget\packages"
    }
    else {
        $env:NUGET_PACKAGES
    }

    $ortNotices = Join-Path $nugetRoot "microsoft.ml.onnxruntime.managed\$($ortPackage.Version)\ThirdPartyNotices.txt"
    if (-not (Test-Path -Path $ortNotices)) {
        throw "Missing ONNX Runtime notices: $ortNotices (run 'dotnet restore' first)."
    }
    Copy-Item -Path $ortNotices -Destination (Join-Path $Destination "THIRD-PARTY-NOTICES-ONNXRUNTIME.txt") -Force

    Write-Host "Legal files: LICENSE, EULA.md, THIRD-PARTY-NOTICES.md, THIRD-PARTY-NOTICES-ONNXRUNTIME.txt"
}
