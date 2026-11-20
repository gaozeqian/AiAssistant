param(
    [string]$Endpoint = "http://127.0.0.1:47831/mcp",
    [string]$Token = $env:AIASSISTANT_MCP_TOKEN,
    [string]$InspectorVersion = "2.16.0"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Set AIASSISTANT_MCP_TOKEN or pass -Token."
}

function Invoke-McpInspector {
    param([string[]]$Arguments)

    $output = & npx --yes "@modelcontextprotocol/inspector@$InspectorVersion" --cli `
        --transport http --server-url $Endpoint `
        --header "Authorization: Bearer $Token" `
        --connect-timeout 10000 --format json @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "MCP Inspector failed with exit code $LASTEXITCODE"
    }

    $json = ($output -join "`n")
    return ($json | ConvertFrom-Json)
}

$initialize = Invoke-McpInspector @("--method", "initialize")
if ([string]::IsNullOrWhiteSpace($initialize.result.protocolVersion)) {
    throw "initialize returned no protocolVersion"
}

$listing = Invoke-McpInspector @("--method", "tools/list")
$names = @($listing.result.tools | ForEach-Object { $_.name })
$expected = @("search_events", "activity_stats", "list_reports", "get_report")
foreach ($name in $expected) {
    if ($names -notcontains $name) {
        throw "Missing expected tool: $name"
    }
}

if ($names.Count -ne $expected.Count) {
    throw "Unexpected tool set: $($names -join ',')"
}

$today = (Get-Date).ToString("yyyy-MM-dd")
$weekAgo = (Get-Date).AddDays(-6).ToString("yyyy-MM-dd")
$stats = Invoke-McpInspector @(
    "--method", "tools/call",
    "--tool-name", "activity_stats",
    "--tool-arg", "fromDate=$weekAgo",
    "--tool-arg", "toDate=$today")
if ($stats.result.isError) {
    throw "activity_stats returned isError=true"
}

Write-Host "MCP Inspector smoke passed: protocol=$($initialize.result.protocolVersion) tools=$($names -join ',')"
