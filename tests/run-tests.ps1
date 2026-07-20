<#
    Test runner for the hand-rolled HTTP/2 stack.

    Two kinds of harness live under tests/:

      * self-contained  — spin up their own server (ours and/or Kestrel) on a
        private port, self-report a check count, and exit non-zero on any
        failure. Run directly.

      * demo-driven      — raw frame-level clients that talk to the running
        Demo host on https://localhost:8443. They print per-check ✓/✗ lines
        but exit 0 regardless, so we scan their output for ✗ to decide
        pass/fail. Each is invoked once per scenario (mode [+ sub-case]).

    Usage:
        pwsh tests/run-tests.ps1                # build + run everything
        pwsh tests/run-tests.ps1 -NoBuild       # skip the build step
        pwsh tests/run-tests.ps1 -Filter attack # only harnesses matching *attack*
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [string] $Filter = ""
)

# Continue (not Stop): the harnesses log to stderr on normal shutdown (e.g. a
# GOAWAY notice), and under Stop PowerShell 5.1 turns that native stderr into a
# terminating error. Explicit `throw`s below still abort regardless.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot          # repo root
$sln  = Join-Path $root "HTTP2.slnx"

$script:total  = 0
$script:passed = 0
$fails = [System.Collections.Generic.List[string]]::new()

function Section($name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }

# Run one harness process, decide pass/fail, record it.
#   -ExitCode : pass when the process exits 0 (self-verifying harnesses)
#   -NoCross  : pass when stdout contains no ✗ (demo-driven scenario harnesses)
function Invoke-Harness {
    param(
        [string]   $Label,
        [string]   $Project,
        [string[]] $Args = @(),
        [ValidateSet("ExitCode", "NoCross")] [string] $Mode = "ExitCode"
    )

    if ($Filter -and ($Label -notlike "*$Filter*") -and ($Project -notlike "*$Filter*")) { return }

    $script:total++
    $csproj = Join-Path $PSScriptRoot "$Project/$Project.csproj"
    $out = & dotnet run --project $csproj --no-build -- @Args 2>&1 | Out-String

    $ok = if ($Mode -eq "ExitCode") { $LASTEXITCODE -eq 0 }
          else { ($LASTEXITCODE -eq 0) -and ($out -notmatch [char]0x2717) }   # ✗

    if ($ok) {
        $script:passed++
        # Surface the harness's own last verdict line (a self-reported count, or
        # the last per-check tick) so the runner echoes something meaningful.
        $check = [char]0x2713   # ✓
        $summary = ($out -split "`n" | Where-Object { $_ -match "checks passed" -or $_.Contains($check) } | Select-Object -Last 1)
        if (-not $summary) { $summary = "" }
        Write-Host ("  PASS  {0,-34} {1}" -f $Label, ($summary.Trim())) -ForegroundColor Green
    }
    else {
        $fails.Add($Label)
        Write-Host ("  FAIL  {0}" -f $Label) -ForegroundColor Red
        ($out -split "`n" | Where-Object { $_ -match ([char]0x2717) } | ForEach-Object { Write-Host "        $($_.Trim())" -ForegroundColor Red })
        if ($out -notmatch [char]0x2717) { Write-Host "        (exit $LASTEXITCODE)`n$out" -ForegroundColor DarkGray }
    }
}

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if (-not $NoBuild) {
    Section "Build"
    & dotnet build $sln -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }
    Write-Host "  Build OK" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Demo-driven harnesses — need the Demo host on :8443
# (the former self-contained harnesses now live as NUnit tests in Hermod's
#  HermodTests/HTTP2/ — see tests/README.md)
# ---------------------------------------------------------------------------
Section "Starting Demo host on :8443"

# Free the ports if a stale demo is still bound (rebuilds lock the exe
# otherwise, and a failed bind faults the demo's whole listener set). The demo
# serves both the TLS listener on 8443 and the cleartext h2c listener on 8080.
Get-NetTCPConnection -LocalPort 8443, 8080 -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }

$demo = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "Demo/HTTP2.Demo.csproj"), "--no-build") `
    -PassThru -WindowStyle Hidden

try {
    # Wait for the listener to accept (up to ~15 s).
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        if (Get-NetTCPConnection -LocalPort 8443 -State Listen -ErrorAction SilentlyContinue) { $ready = $true; break }
    }
    if (-not $ready) { throw "Demo host did not start listening on :8443" }
    Write-Host "  Demo host up (pid $($demo.Id))" -ForegroundColor Green

    Section "RFC 9110 semantics"
    Invoke-Harness -Label "h2semantics" -Project "h2semantics"

    Section "Attack / hardening scenarios (h2attack)"
    foreach ($m in "contcount","contbytes","ping","settings","rapidreset","streamid-exhaustion","outbound-headerlimit") {
        Invoke-Harness -Label "h2attack $m" -Project "h2attack" -Args @($m) -Mode NoCross
    }
    foreach ($c in "missingpath","missingmethod","missingscheme","empty-path","uppercase","connection","te-bad","te-ok","pseudo-after-regular","unknown-pseudo","duplicate-pseudo") {
        Invoke-Harness -Label "h2attack malformed $c" -Project "h2attack" -Args @("malformed", $c) -Mode NoCross
    }
    foreach ($c in "ok","pseudo","no-endstream") {
        Invoke-Harness -Label "h2attack trailers $c" -Project "h2attack" -Args @("trailers", $c) -Mode NoCross
    }
    foreach ($c in "even-stream","idle-data","idle-windowupdate","implicit-close-data","implicit-close-windowupdate") {
        Invoke-Harness -Label "h2attack idle $c" -Project "h2attack" -Args @("idle", $c) -Mode NoCross
    }
    Invoke-Harness -Label "h2attack rst-cancel" -Project "h2attack" -Args @("rst-cancel") -Mode NoCross

    Section "CONNECT / WebSocket scenarios (h2connect)"
    foreach ($m in "settings","loopback","ws-echo","ws-fragmented","ws-ping","ws-close","reject","cancel","multiplex") {
        Invoke-Harness -Label "h2connect $m" -Project "h2connect" -Args @($m) -Mode NoCross
    }
    foreach ($c in "scheme-on-connect","path-on-connect","missing-authority","missing-scheme-extended","missing-path-extended","protocol-on-get") {
        Invoke-Harness -Label "h2connect malformed $c" -Project "h2connect" -Args @("malformed", $c) -Mode NoCross
    }

    Section "RFC 9218 priority scenarios (h2priority)"
    foreach ($m in "settings","urgency-header","priority-update","priority-update-unknown-stream","malformed-priority") {
        Invoke-Harness -Label "h2priority $m" -Project "h2priority" -Args @($m) -Mode NoCross
    }
}
finally {
    Section "Stopping Demo host"
    if ($demo -and -not $demo.HasExited) { Stop-Process -Id $demo.Id -Force -ErrorAction SilentlyContinue }
    Get-NetTCPConnection -LocalPort 8443, 8080 -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Section "Summary"
Write-Host ("  {0}/{1} harness runs passed" -f $passed, $total) -ForegroundColor $(if ($fails.Count -eq 0) { "Green" } else { "Red" })
if ($fails.Count -gt 0) {
    Write-Host "  Failures:" -ForegroundColor Red
    $fails | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    exit 1
}
exit 0
