<#
    Run the Autobahn TestSuite WebSocket conformance run against our echo server.

    The Autobahn TestSuite (https://github.com/crossbario/autobahn-testsuite) is
    the canonical RFC 6455 WebSocket conformance suite. Its "fuzzingclient" mode
    connects to a WebSocket ECHO server and drives ~500 cases (framing,
    fragmentation, UTF-8 handling, close handshake, ...). We run it from the
    official Docker image -- the native `wstest` is legacy Python 2 -- so the
    only prerequisite is Docker (Docker Desktop on Windows).

    The echo server under test (tests/autobahn-server) exposes the SAME
    WebSocketConnection framing used in production, over a plain-TCP tunnel
    behind a minimal HTTP/1.1 Upgrade handshake -- Autobahn's client speaks
    WebSocket over HTTP/1.1, not RFC 8441 over HTTP/2, but the framing layer
    under test is transport-agnostic (see tests/TestingAgainst_Autobahn.md).

    This script:
      * builds the echo server (unless -NoBuild),
      * frees port 9010 if a stale server is bound, starts it (output to a file),
      * runs the Autobahn fuzzingclient (Docker) against it,
      * parses the JSON report and exits non-zero if any case FAILED,
      * stops the echo server again.

    Usage:
        pwsh tests/autobahn.ps1
        pwsh tests/autobahn.ps1 -NoBuild
        pwsh tests/autobahn.ps1 -Port 9010 -Image crossbario/autobahn-testsuite
#>
[CmdletBinding()]
param(
    [int]    $Port   = 9010,
    [string] $Image  = "crossbario/autobahn-testsuite",
    [switch] $NoBuild
)

$ErrorActionPreference = "Continue"

$root    = Split-Path -Parent $PSScriptRoot
$sln     = Join-Path $root "src/HTTP2.slnx"
$srvProj = Join-Path $root "tests/autobahn-server/autobahn-server.csproj"
$cfgDir  = Join-Path $root "tests/autobahn"
$repDir  = Join-Path $root "tests/autobahn/reports"

# --- require docker --------------------------------------------------------
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    throw "docker not found. Install Docker Desktop and retry. See tests/TestingAgainst_Autobahn.md."
}

# --- build -----------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "Building echo server..." -ForegroundColor Cyan
    & dotnet build $sln -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

# --- free the port ---------------------------------------------------------
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

# --- start the echo server, output redirected to a file --------------------
$srvDll = Join-Path $root "tests/autobahn-server/bin/Debug/net10.0/autobahn-server.dll"
if (-not (Test-Path $srvDll)) { throw "Echo server not built ($srvDll). Run without -NoBuild first." }

$outLog = Join-Path $env:TEMP "autobahn-server.log"
$srv = Start-Process -FilePath "dotnet" -ArgumentList @($srvDll, "$Port") `
    -PassThru -RedirectStandardOutput $outLog -RedirectStandardError "$outLog.err"

$failed = 0
try {

    # Wait for the echo server to accept connections.
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 300
        if ($srv.HasExited) { throw "Echo server exited during startup -- see $outLog.err" }
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $c.Connect("127.0.0.1", $Port); $c.Close(); $ready = $true; break
        } catch { }
    }
    if (-not $ready) { throw "Echo server did not start listening on port $Port" }
    Write-Host "Echo server up (pid $($srv.Id)) on ws://127.0.0.1:$Port/" -ForegroundColor Green

    if (Test-Path $repDir) { Remove-Item -Recurse -Force $repDir }
    New-Item -ItemType Directory -Force -Path $repDir | Out-Null

    # Run the Autobahn fuzzingclient. On Docker Desktop, host.docker.internal
    # (used in fuzzingclient.json) reaches the host, so the default bridge
    # network is fine.
    Write-Host "Running Autobahn fuzzingclient (Docker image $Image)..." -ForegroundColor Cyan
    & docker run --rm `
        -v "${cfgDir}:/config" `
        -v "${repDir}:/reports" `
        $Image `
        wstest -m fuzzingclient -s /config/fuzzingclient.json
    if ($LASTEXITCODE -ne 0) { Write-Host "docker run returned $LASTEXITCODE" -ForegroundColor Yellow }

    # --- parse the report --------------------------------------------------
    $index = Join-Path $repDir "index.json"
    if (-not (Test-Path $index)) { throw "No Autobahn report at $index (did the container reach the server?)" }

    $allowed = @("OK", "NON-STRICT", "INFORMATIONAL")
    $report  = Get-Content $index -Raw | ConvertFrom-Json
    $total = 0; $bad = @()
    foreach ($agent in $report.PSObject.Properties) {
        foreach ($case in $agent.Value.PSObject.Properties) {
            $total++
            $b  = $case.Value.behavior
            $bc = $case.Value.behaviorClose
            if (($allowed -notcontains $b) -or ($allowed -notcontains $bc)) {
                $bad += "$($case.Name): behavior=$b close=$bc"
            }
        }
    }

    Write-Host ""
    Write-Host "Autobahn: $($total - $bad.Count)/$total cases OK" -ForegroundColor Cyan
    if ($bad.Count -gt 0) {
        $failed = 1
        Write-Host "Non-passing cases:" -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
    Write-Host "Full HTML report: $repDir/index.html"

}
finally {
    if ($srv -and -not $srv.HasExited) { Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue }
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($failed -gt 0) { Write-Host "Autobahn reported non-passing cases." -ForegroundColor Red; exit 1 }
Write-Host "Autobahn: all cases passed." -ForegroundColor Green
exit 0
