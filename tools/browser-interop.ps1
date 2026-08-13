<#
    Browser interop for the HTTP/2 demo host.

    Starts the demo, drives a headless Chromium browser through the self-test at
    /browser, and exits non-zero unless every check passed.

    The page runs the battery itself and POSTs its verdict to /report, which the
    demo prints — so the SERVER LOG is the record. Nothing here scrapes a DOM or
    guesses when the headless run finished: the script waits for the verdict line
    to appear, which is also the signal that the browser got that far.

    What the browser adds over curl and HttpClient is the first check in that
    battery: `performance.nextHopProtocol`. Everything else in this repository is
    us asserting we spoke HTTP/2; that one is Chrome saying it.

    Only the self-signed certificate needs handling, via
    --ignore-certificate-errors. Unlike the sibling HTTP/3 project this needs no
    SPKI pin and no draft feature flags — plain h2 in a browser is unremarkable,
    which is the point.

    Usage:
        pwsh tools/browser-interop.ps1                    # Chrome, then Edge
        pwsh tools/browser-interop.ps1 -Browser chrome
        pwsh tools/browser-interop.ps1 -Port 9443 -NoBuild
#>
[CmdletBinding()]
param(
    [ValidateSet("all", "chrome", "edge")] [string] $Browser = "all",
    [int]    $Port    = 9443,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Deliberately not 8443: the sibling demos (HTTP1-/HTTP3ConformanceTests) like
# that port too, and this script is not entitled to evict whatever is on it.
$cleartextPort = $Port - 363   # 9443 -> 9080, mirroring the 8443/8080 pairing

function Find-Browser([string] $Kind) {
    $candidates = switch ($Kind) {
        "chrome" { @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
                     "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
                     "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") }
        "edge"   { @("$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
                     "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") }
    }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

if (-not $NoBuild) {
    Write-Host "Building the demo host..."
    & dotnet build (Join-Path $root "Demo/HTTP2.Demo.csproj") -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Demo build failed" }
}

$dll = $null
foreach ($cfg in @("Debug", "Release")) {
    $cand = Join-Path $root "Demo/bin/$cfg/net10.0/HTTP2.Demo.dll"
    if (Test-Path $cand) { $dll = $cand; break }
}
if (-not $dll) { throw "Demo host not built (no HTTP2.Demo.dll). Run without -NoBuild first." }

# Run the DLL rather than `dotnet run`: the latter forks a child it does not
# forward signals to, so stopping it would orphan the real server with the port
# still bound — the same lesson tests/h2spec.sh records.
$log  = Join-Path ([System.IO.Path]::GetTempPath()) "h2-browser-demo-$PID.log"
$demo = Start-Process -FilePath "dotnet" `
                      -ArgumentList @($dll, "--port", $Port, "--cleartext-port", $cleartextPort) `
                      -PassThru -WindowStyle Hidden `
                      -RedirectStandardOutput $log -RedirectStandardError "$log.err"

$failed = 0
try {

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($demo.HasExited) { throw "Demo host exited during startup:`n$(Get-Content $log -Raw)" }
        try {
            $probe = [System.Net.Sockets.TcpClient]::new()
            $probe.Connect("127.0.0.1", $Port)
            $probe.Close()
            $ready = $true
            break
        } catch { }
    }
    if (-not $ready) { throw "Demo host did not start listening on :$Port" }
    Write-Host "  Demo host up (pid $($demo.Id)) on https://localhost:$Port/" -ForegroundColor Green

    $browsers = if ($Browser -eq "all") { @("chrome", "edge") } else { @($Browser) }

    foreach ($kind in $browsers) {

        $exe = Find-Browser $kind
        if (-not $exe) {
            Write-Host "  SKIP  $kind (not installed)" -ForegroundColor DarkGray
            continue
        }

        # A throwaway profile per run: without it a browser already running with
        # the default profile just hands the URL to that instance and returns
        # immediately, and the headless run never happens.
        $profileDir = Join-Path ([System.IO.Path]::GetTempPath()) "h2-browser-$kind-$PID"
        # .Contains, not -like: PowerShell's wildcard syntax reads "[browser]" as a
        # character class, so `-like "*[browser]*"` matches any line containing one
        # of b,r,o,w,s,e — which is every line the demo prints.
        $before     = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_.Contains("[browser]") }).Count

        $proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -ArgumentList @(
                    "--headless=new",
                    "--disable-gpu",
                    "--no-first-run",
                    "--user-data-dir=$profileDir",
                    "--ignore-certificate-errors",
                    "https://localhost:$Port/browser"
                )

        # Wait for the verdict LINE, not for the browser to exit: headless Chrome
        # lingers after the page is done, and the report is what we came for.
        $verdict = $null
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Milliseconds 500
            $lines = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_.Contains("[browser]") })
            if ($lines.Count -gt $before) { $verdict = $lines[-1]; break }
        }

        try { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch { }
        Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue

        if (-not $verdict) {
            Write-Host "  FAIL  $kind — no verdict within 30 s" -ForegroundColor Red
            $failed++
            continue
        }

        $json = $verdict -replace '^.*?\[browser\]\s*', ''
        try   { $report = $json | ConvertFrom-Json } catch { $report = $null }

        if ($report -and $report.verdict -eq "PASS") {
            Write-Host "  PASS  $kind — $($report.passed)/$($report.total) checks" -ForegroundColor Green
            foreach ($c in $report.checks) { Write-Host "          $($c.name): $($c.detail)" -ForegroundColor DarkGray }
        }
        else {
            $failed++
            Write-Host "  FAIL  $kind — $(if ($report) { "$($report.passed)/$($report.total) checks" } else { 'unparseable report' })" -ForegroundColor Red
            if ($report) {
                foreach ($c in $report.checks | Where-Object { -not $_.ok }) {
                    Write-Host "          $($c.name): $($c.detail)" -ForegroundColor Red
                }
            }
            else { Write-Host "          $json" -ForegroundColor DarkGray }
        }
    }

}
finally {
    if ($demo -and -not $demo.HasExited) { Stop-Process -Id $demo.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $log, "$log.err" -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "$failed browser(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "Browser interop: all checks passed." -ForegroundColor Green
exit 0
