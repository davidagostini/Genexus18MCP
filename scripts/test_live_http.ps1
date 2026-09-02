$ErrorActionPreference = "Stop"

$env:GX_MCP_STDIO = "false"
$env:GX_CONFIG_PATH = "C:\Projetos\Genexus18MCP\publish\config.json"

Write-Host ">>> [1/6] Launching Gateway HTTP server on port 5000..."
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "C:\Projetos\Genexus18MCP\publish\GxMcp.Gateway.exe"
$psi.WorkingDirectory = "C:\Projetos\Genexus18MCP\publish"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$gatewayProc = [System.Diagnostics.Process]::Start($psi)

try {
    # Wait for port 5000
    $ready = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Milliseconds 500
        $conn = Get-NetTCPConnection -LocalPort 5000 -ErrorAction SilentlyContinue
        if ($null -ne $conn) {
            $ready = $true
            break
        }
        if ($gatewayProc.HasExited) {
            throw "Gateway exited prematurely with exit code: $($gatewayProc.ExitCode)"
        }
    }

    if (-not $ready) {
        throw "Gateway timed out waiting to bind port 5000"
    }

    Write-Host ">>> Gateway HTTP server ready. PID: $($gatewayProc.Id)"

    $headers = @{
        "MCP-Protocol-Version" = "2025-11-25"
        "Content-Type" = "application/json"
        "Accept" = "application/json, text/event-stream"
    }

    # 1. Initialize
    Write-Host "`n>>> [2/6] Sending MCP initialize..."
    $initPayload = @{
        jsonrpc = "2.0"
        id = "init-1"
        method = "initialize"
        params = @{
            protocolVersion = "2025-11-25"
            capabilities = @{}
            clientInfo = @{ name = "LiveValidationHarness"; version = "1.0.0" }
        }
    } | ConvertTo-Json -Depth 5

    $initResp = Invoke-WebRequest -Uri "http://127.0.0.1:5000/mcp" -Method Post -Headers $headers -Body $initPayload -UseBasicParsing
    $sessionId = $initResp.Headers["MCP-Session-Id"]
    if ([string]::IsNullOrWhiteSpace($sessionId)) {
        # Check lowercase header
        $sessionId = $initResp.Headers["mcp-session-id"]
    }
    Write-Host ">>> Initialize OK. Session ID: $sessionId"
    $headers["MCP-Session-Id"] = $sessionId

    # 2. tools/list
    Write-Host "`n>>> [3/6] Querying tools/list..."
    $toolsPayload = @{
        jsonrpc = "2.0"
        id = "tools-1"
        method = "tools/list"
        params = @{}
    } | ConvertTo-Json -Depth 5

    $toolsResp = Invoke-WebRequest -Uri "http://127.0.0.1:5000/mcp" -Method Post -Headers $headers -Body $toolsPayload -UseBasicParsing
    $toolsJson = $toolsResp.Content | ConvertFrom-Json
    $toolCount = $toolsJson.result.tools.Count
    Write-Host ">>> tools/list OK. Registered tools count: $toolCount"

    # 3. genexus_whoami
    Write-Host "`n>>> [4/6] Calling genexus_whoami..."
    $whoamiPayload = @{
        jsonrpc = "2.0"
        id = "call-1"
        method = "tools/call"
        params = @{
            name = "genexus_whoami"
            arguments = @{}
        }
    } | ConvertTo-Json -Depth 5

    $whoamiResp = Invoke-WebRequest -Uri "http://127.0.0.1:5000/mcp" -Method Post -Headers $headers -Body $whoamiPayload -UseBasicParsing
    $whoamiJson = $whoamiResp.Content | ConvertFrom-Json
    $whoamiRawText = $whoamiJson.result.content[0].text
    Write-Host ">>> whoami raw response excerpt:"
    Write-Host ($whoamiRawText.Substring(0, [Math]::Min(500, $whoamiRawText.Length)))

    # 4. genexus_kb action=list
    Write-Host "`n>>> [5/6] Calling genexus_kb (action=list)..."
    $kbPayload = @{
        jsonrpc = "2.0"
        id = "call-2"
        method = "tools/call"
        params = @{
            name = "genexus_kb"
            arguments = @{ action = "list" }
        }
    } | ConvertTo-Json -Depth 5

    $kbResp = Invoke-WebRequest -Uri "http://127.0.0.1:5000/mcp" -Method Post -Headers $headers -Body $kbPayload -UseBasicParsing
    $kbJson = $kbResp.Content | ConvertFrom-Json
    $kbRawText = $kbJson.result.content[0].text
    Write-Host ">>> genexus_kb response:"
    Write-Host $kbRawText

    # 5. genexus_query (query=*)
    Write-Host "`n>>> [6/7] Calling genexus_query (query=*)..."
    $queryPayload = @{
        jsonrpc = "2.0"
        id = "call-3"
        method = "tools/call"
        params = @{
            name = "genexus_query"
            arguments = @{ query = "*"; limit = 5 }
        }
    } | ConvertTo-Json -Depth 5

    $queryResp = Invoke-WebRequest -Uri "http://127.0.0.1:5000/mcp" -Method Post -Headers $headers -Body $queryPayload -UseBasicParsing
    $queryJson = $queryResp.Content | ConvertFrom-Json
    $queryRawText = $queryJson.result.content[0].text
    Write-Host ">>> genexus_query response excerpt:"
    Write-Host ($queryRawText.Substring(0, [Math]::Min(500, $queryRawText.Length)))

    Write-Host "`n>>> [7/7] LIVE HTTP TEST PASSED CLEANLY!"
}
finally {
    Write-Host "`n>>> Cleaning up server processes..."
    if ($gatewayProc -and -not $gatewayProc.HasExited) {
        $gatewayProc.Kill()
        $gatewayProc.Dispose()
    }
    # Stop any spawned worker
    Get-Process -Name GxMcp.Worker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host ">>> Cleanup complete."
}
