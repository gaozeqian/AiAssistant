<#
.SYNOPSIS
    Writes per-file .sha256 files and the aggregate AiAssistant-<version>-SHA256SUMS.txt.
.DESCRIPTION
    Shared by package-windows.ps1 and package-windows-msi.ps1. Only distributable files are hashed.
#>
function Write-DistChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Version
    )

    if (-not (Test-Path -Path $Directory)) {
        throw "Distribution directory does not exist: $Directory"
    }

    # 校验清单固定 LF，保证 Linux/macOS 的 sha256sum/shasum 可直接校验 Windows 产出的文件。
    function Write-AsciiLf {
        param([string]$Path, [string[]]$Lines)
        [System.IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
    }

    $summaryPath = Join-Path $Directory "AiAssistant-$Version-SHA256SUMS.txt"
    $lines = New-Object System.Collections.Generic.List[string]
    $files = Get-ChildItem -Path $Directory -File | Where-Object {
        $_.Name -match '\.(msi|zip|pkg|deb|cdx\.json)$' -or $_.Name.EndsWith('.tar.gz')
    } | Sort-Object Name

    foreach ($file in $files) {
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $line = "$hash  $($file.Name)"
        Write-AsciiLf -Path (Join-Path $Directory "$($file.Name).sha256") -Lines @($line)
        $lines.Add($line)
    }

    if ($lines.Count -gt 0) {
        Write-AsciiLf -Path $summaryPath -Lines $lines.ToArray()
        Write-Host "checksums: $summaryPath"
    }
}
