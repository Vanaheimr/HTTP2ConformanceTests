<#
    Run the h2spec HTTP/2 conformance suite against the demo host.

    h2spec (https://github.com/summerwind/h2spec) is the canonical RFC 9113 +
    RFC 7541 conformance suite. It is an external binary and is NOT vendored in
    this repo -- download a release first (see tests/TestingAgainst_h2spec.md),
    then point this script at it with -H2spec, or put it on your PATH.

    This script does the fiddly bits for you:
      * builds the solution (unless -NoBuild),
      * frees ports 8443 (TLS) and 8080 (cleartext h2c) if a stale demo is bound,
      * starts the demo host with its stdout/stderr redirected to a file
        (CRUCIAL: under h2spec's flood of malformed cases the demo logs heavily,
        and an *undrained* console pipe can block it mid-run -- it then looks like
        a crash, with later connections, especially IPv6 ::1, timing out),
      * runs h2spec against the TLS and/or cleartext listener,
      * stops the demo again.

    Usage:
        pwsh tests/h2spec.ps1                       # both transports (TLS + h2c)
        pwsh tests/h2spec.ps1 -Transport tls        # only the TLS listener :8443
        pwsh tests/h2spec.ps1 -Transport cleartext  # only the h2c  listener :8080
        pwsh tests/h2spec.ps1 -H2spec C:\tools\h2spec.exe   # explicit binary path
        pwsh tests/h2spec.ps1 -NoBuild -Spec http2/6.5      # skip build, one section
#>
[CmdletBinding()]
param(
    [ValidateSet("both", "tls", "cleartext")] [string] $Transport = "both",
    [string]   $H2spec  = "h2spec",   # command on PATH, or a full path to the binary
    [string]   $Path    = "/echo",    # a POST-capable endpoint for the DATA/body tests
    [string[]] $Spec    = @(),        # optional: limit to specific sections, e.g. http2/6.5
    [switch]   $Strict,               # pass -S (include strict test cases)
    [switch]   $NoBuild
)

# Continue (not Stop): the demo logs to stderr on normal shutdown (a GOAWAY
# notice); under Stop, PowerShell 5.1 turns that native stderr into a
# terminating error. Our own failure conditions below use explicit `throw`.
$ErrorActionPreference = "Continue"

$root     = Split-Path -Parent $PSScriptRoot
$sln      = Join-Path $root "src/HTTP2.slnx"
$demoProj = Join-Path $root "src/Demo/HTTP2.Demo.csproj"

# --- locate h2spec ---------------------------------------------------------
$cmd = Get-Command $H2spec -ErrorAction SilentlyContinue
if (-not $cmd) {
    throw "h2spec not found ('$H2spec'). Download it from " +
          "https://github.com/summerwind/h2spec/releases and pass -H2spec <path>, " +
          "or put it on your PATH. See tests/TestingAgainst_h2spec.md."
}
$h2specExe = $cmd.Source
Write-Host "Using h2spec: $h2specExe" -ForegroundColor Cyan

# --- build -----------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host "Building solution..." -ForegroundColor Cyan
    & dotnet build $sln -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }
}

# --- free the demo ports ---------------------------------------------------
Get-NetTCPConnection -LocalPort 8443, 8080 -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

# --- start the demo, output redirected to a file (see header) --------------
$outLog = Join-Path $env:TEMP "h2spec-demo-out.log"
$errLog = Join-Path $env:TEMP "h2spec-demo-err.log"
$demo = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", $demoProj, "--no-build") `
    -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog

$failed = 0
try {

    # Wait for whichever listener we need (TLS -> 8443, cleartext -> 8080).
    $waitPort = if ($Transport -eq "tls") { 8443 } else { 8080 }
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($demo.HasExited) { throw "Demo host exited during startup -- see $errLog" }
        if (Get-NetTCPConnection -LocalPort $waitPort -State Listen -ErrorAction SilentlyContinue) { $ready = $true; break }
    }
    if (-not $ready) { throw "Demo host did not start listening on port $waitPort" }
    Write-Host "Demo host up (pid $($demo.Id))" -ForegroundColor Green

    # Let the freshly-started host JIT-warm before h2spec starts firing: h2spec's
    # per-test timeout defaults to 2 s (-o), and a cold .NET process's first
    # TLS handshake + request can otherwise trip it.
    Start-Sleep -Seconds 1

    $extra = @()
    if ($Strict) { $extra += "-S" }
    if ($Spec.Count -gt 0) { $extra += $Spec }

    if ($Transport -eq "both" -or $Transport -eq "tls") {
        Write-Host ""
        Write-Host "=== h2spec over TLS (h2, :8443) ===" -ForegroundColor Cyan
        & $h2specExe -t -k -h 127.0.0.1 -p 8443 -P $Path @extra
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }

    if ($Transport -eq "both" -or $Transport -eq "cleartext") {
        Write-Host ""
        Write-Host "=== h2spec over cleartext (h2c, :8080) ===" -ForegroundColor Cyan
        & $h2specExe -h 127.0.0.1 -p 8080 -P $Path @extra
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }

}
finally {
    if ($demo -and -not $demo.HasExited) { Stop-Process -Id $demo.Id -Force -ErrorAction SilentlyContinue }
    Get-NetTCPConnection -LocalPort 8443, 8080 -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "h2spec reported failures on $failed transport(s)." -ForegroundColor Red
    exit 1
}
Write-Host "h2spec: all conformance tests passed." -ForegroundColor Green
exit 0
