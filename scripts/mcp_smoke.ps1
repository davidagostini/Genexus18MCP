param(
    [string]$BaseUrl = "http://127.0.0.1:5000/mcp",
    [string]$ObjectName = "",
    [string]$ReadPart = "Source",
    [string]$PatchContext = "",
    [string]$PatchContent = ""
)

$ErrorActionPreference = "Stop"

function Invoke-Mcp {
    param(
        [string]$Method,
        [hashtable]$Params,
        [string]$SessionId = "",
        [ref]$ResponseHeaders
    )

    $headers = @{
        "MCP-Protocol-Version" = "2025-11-25"
        "Content-Type" = "application/json"
        # The gateway rejects POSTs without both media types (406 Not Acceptable).
        "Accept" = "application/json, text/event-stream"
    }
    if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
        $headers["MCP-Session-Id"] = $SessionId
    }

    $bodyObj = @{
        jsonrpc = "2.0"
        id = [guid]::NewGuid().ToString("N")
        method = $Method
    }
    if ($null -ne $Params) {
        $bodyObj["params"] = $Params
    }

    $body = $bodyObj | ConvertTo-Json -Depth 20 -Compress
    $resp = Invoke-WebRequest -Uri $BaseUrl -Method Post -Headers $headers -Body $body -UseBasicParsing
    $ResponseHeaders.Value = $resp.Headers
    return ($resp.Content | ConvertFrom-Json)
}

function Parse-ToolTextResult {
    param([object]$ToolCallResponse)

    if ($null -eq $ToolCallResponse.result -or $null -eq $ToolCallResponse.result.content) {
        return $null
    }

    $text = $ToolCallResponse.result.content[0].text
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return ($text | ConvertFrom-Json)
    } catch {
        return $text
    }
}

Write-Host "[SMOKE] initialize"
$initHeaders = $null
$init = Invoke-Mcp -Method "initialize" -Params @{
    protocolVersion = "2025-11-25"
    capabilities = @{}
    clientInfo = @{ name = "mcp-smoke"; version = "1.0.0" }
} -ResponseHeaders ([ref]$initHeaders)

$sessionId = $initHeaders["MCP-Session-Id"]
if ([string]::IsNullOrWhiteSpace($sessionId)) {
    throw "[SMOKE] Missing MCP-Session-Id in initialize response."
}
Write-Host "[SMOKE] session: $sessionId"

Write-Host "[SMOKE] tools/list"
$headersOut = $null
$tools = Invoke-Mcp -Method "tools/list" -Params @{} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
if ($null -eq $tools.result -or $null -eq $tools.result.tools) {
    throw "[SMOKE] tools/list did not return tool catalog."
}

Write-Host "[SMOKE] resources/list"
$resources = Invoke-Mcp -Method "resources/list" -Params @{} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
if ($null -eq $resources.result -or $null -eq $resources.result.resources) {
    throw "[SMOKE] resources/list did not return resources catalog."
}

Write-Host "[SMOKE] tools/call genexus_query"
$queryCall = Invoke-Mcp -Method "tools/call" -Params @{
    name = "genexus_query"
    arguments = @{
        query = "@quick"
        limit = 1
    }
} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
$queryPayload = Parse-ToolTextResult -ToolCallResponse $queryCall
if ($null -eq $queryPayload) {
    throw "[SMOKE] genexus_query returned empty payload."
}

if (-not [string]::IsNullOrWhiteSpace($ObjectName)) {
    Write-Host "[SMOKE] tools/call genexus_read ($ObjectName/$ReadPart)"
    $readCall = Invoke-Mcp -Method "tools/call" -Params @{
        name = "genexus_read"
        arguments = @{
            name = $ObjectName
            part = $ReadPart
            limit = 30
        }
    } -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
    $readPayload = Parse-ToolTextResult -ToolCallResponse $readCall
    if ($null -eq $readPayload) {
        throw "[SMOKE] genexus_read returned empty payload."
    }

    if (-not [string]::IsNullOrWhiteSpace($PatchContext)) {
        Write-Host "[SMOKE] tools/call genexus_edit (dryRun patch)"
        $editCall = Invoke-Mcp -Method "tools/call" -Params @{
            name = "genexus_edit"
            arguments = @{
                name = $ObjectName
                part = $ReadPart
                mode = "patch"
                operation = "Replace"
                context = $PatchContext
                content = $PatchContent
                dryRun = $true
                expectedCount = 1
            }
        } -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
        $editPayload = Parse-ToolTextResult -ToolCallResponse $editCall
        if ($null -eq $editPayload) {
            throw "[SMOKE] genexus_edit dryRun returned empty payload."
        }
    }
}

Write-Host "[SMOKE] tools/call genexus_lifecycle status op:<fake>"
$opStatus = Invoke-Mcp -Method "tools/call" -Params @{
    name = "genexus_lifecycle"
    arguments = @{
        action = "status"
        target = "op:00000000000000000000000000000000"
    }
} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
$opStatusPayload = Parse-ToolTextResult -ToolCallResponse $opStatus
if ($null -eq $opStatusPayload) {
    throw "[SMOKE] lifecycle status for operation returned empty payload."
}

Write-Host "[SMOKE] tools/call genexus_lifecycle result op:<fake>"
$opResult = Invoke-Mcp -Method "tools/call" -Params @{
    name = "genexus_lifecycle"
    arguments = @{
        action = "result"
        target = "op:00000000000000000000000000000000"
    }
} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
$opResultPayload = Parse-ToolTextResult -ToolCallResponse $opResult
if ($null -eq $opResultPayload) {
    throw "[SMOKE] lifecycle result for operation returned empty payload."
}

Write-Host "[SMOKE] tools/call genexus_lifecycle status gateway:metrics"
$metrics = Invoke-Mcp -Method "tools/call" -Params @{
    name = "genexus_lifecycle"
    arguments = @{
        action = "status"
        target = "gateway:metrics"
    }
} -SessionId $sessionId -ResponseHeaders ([ref]$headersOut)
$metricsPayload = Parse-ToolTextResult -ToolCallResponse $metrics
if ($null -eq $metricsPayload) {
    throw "[SMOKE] lifecycle gateway:metrics returned empty payload."
}

Write-Host "[SMOKE] PASS"
