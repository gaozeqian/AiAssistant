param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$CertificatePath,
    [string]$OutputDirectory = "artifacts/packages",
    [string]$SignThumbprint = $env:AIASSISTANT_SIGN_CERT_THUMBPRINT,
    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outputRoot = Join-Path $repoRoot $OutputDirectory
$stage = Join-Path $outputRoot "staging\msi"
$msiPath = Join-Path $outputRoot "AiAssistant-$Version-win-x64.msi"
$wxs = Join-Path $repoRoot "build\package\windows\Package.msi.wxs"

if (-not (Test-Path $CertificatePath)) {
    throw "Certificate not found: $CertificatePath"
}

New-Item -ItemType Directory -Force $outputRoot | Out-Null
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force -Confirm:$false
}

Write-Host "== AiAssistant MSI =="
Write-Host "version=$Version stage=$stage"

dotnet publish (Join-Path $repoRoot "src\AiAssistant.Host\AiAssistant.Host.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:InformationalVersion=$Version `
    -tl:off -v:m -o $stage

Copy-Item $CertificatePath (Join-Path $stage "AiAssistant.cer") -Force
Copy-Item (Join-Path $repoRoot "build\package\assets\aiassistant.ico") (Join-Path $stage "aiassistant.ico") -Force

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
dotnet wix build -arch x64 -d SourceDir="$stage" -d Version="$Version" -o "$msiPath" "$wxs"
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
