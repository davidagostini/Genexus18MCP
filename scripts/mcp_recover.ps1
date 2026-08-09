[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5000/mcp',
    [string]$Tool = 'genexus_whoami',
    [string]$ArgumentsJson = '{}',
    [switch]$AllowWrite,
    [switch]$ListTools,
    [ValidateRange(1, 3600)]
    [int]$TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
$protocolVersion = '2025-11-25'

try {
    $arguments = $ArgumentsJson | ConvertFrom-Json -AsHashtable -Depth 50
} catch {
    throw "ArgumentsJson is invalid: $($_.Exception.Message)"
}
if ($null -eq $arguments) { $arguments = @{} }
if ($arguments -isnot [System.Collections.IDictionary]) {
    throw 'ArgumentsJson must be a JSON object, for example: {"limit":10}'
}

function Invoke-McpRequest {
    param(
        [Parameter(Mandatory)]
        [string]$Method,
        [hashtable]$Params,
        [string]$SessionId = ''
    )

    $headers = @{
        'MCP-Protocol-Version' = $protocolVersion
        'Accept' = 'application/json, text/event-stream'
    }
    if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
        $headers['MCP-Session-Id'] = $SessionId
    }

    $request = @{
        jsonrpc = '2.0'
        id = [guid]::NewGuid().ToString('N')
        method = $Method
    }
    if ($null -ne $Params) { $request['params'] = $Params }

    $response = Invoke-WebRequest `
        -Uri $BaseUrl `
        -Method Post `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body ($request | ConvertTo-Json -Depth 50 -Compress) `
        -TimeoutSec $TimeoutSec `
        -UseBasicParsing

    [pscustomobject]@{
        Payload = $response.Content | ConvertFrom-Json -Depth 100
        SessionId = [string](@($response.Headers['MCP-Session-Id'])[0])
    }
}

$initialized = Invoke-McpRequest -Method 'initialize' -Params @{
    protocolVersion = $protocolVersion
    capabilities = @{}
    clientInfo = @{ name = 'genexus-mcp-recovery'; version = '1.0.0' }
}
$sessionId = $initialized.SessionId
if ([string]::IsNullOrWhiteSpace($sessionId)) {
    throw 'The initialize response did not include MCP-Session-Id.'
}
if ($initialized.Payload.error) {
    throw ($initialized.Payload.error | ConvertTo-Json -Depth 20 -Compress)
}

$catalogResponse = Invoke-McpRequest -Method 'tools/list' -Params @{} -SessionId $sessionId
if ($catalogResponse.Payload.error) {
    throw ($catalogResponse.Payload.error | ConvertTo-Json -Depth 20 -Compress)
}
$catalog = @($catalogResponse.Payload.result.tools)

if ($ListTools) {
    $catalog |
        Sort-Object name |
        Select-Object name,
            @{name='readOnly';expression={$_.annotations.readOnlyHint}},
            @{name='destructive';expression={$_.annotations.destructiveHint}} |
        Format-Table -AutoSize
    return
}

$definition = $catalog | Where-Object name -eq $Tool | Select-Object -First 1
if ($null -eq $definition) {
    throw "Tool not found: $Tool. Use -ListTools to inspect the catalog."
}

$readOnly = $definition.annotations.readOnlyHint -eq $true
if (-not $readOnly -and -not $AllowWrite) {
    throw "Tool '$Tool' is not marked read-only. Review the request and pass -AllowWrite explicitly."
}

$callResponse = Invoke-McpRequest -Method 'tools/call' -Params @{
    name = $Tool
    arguments = $arguments
} -SessionId $sessionId
if ($callResponse.Payload.error) {
    throw ($callResponse.Payload.error | ConvertTo-Json -Depth 50 -Compress)
}

foreach ($item in @($callResponse.Payload.result.content)) {
    if ($item.type -eq 'text') {
        Write-Output $item.text
    } else {
        Write-Output ($item | ConvertTo-Json -Depth 50 -Compress)
    }
}
if ($callResponse.Payload.result.isError) { exit 1 }
