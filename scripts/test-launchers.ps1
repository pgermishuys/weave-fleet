# Runs scripts/launcher.cmd the way an install does (bin\fleet.cmd next to app\) against a stub app that records the
# arguments and settings it was started with. The Windows half of scripts/test-launchers.sh, which also checks the
# launchers accept every option the app reads. Needs Windows PowerShell 5.1 (Add-Type builds the stub .exe).
$ErrorActionPreference = 'Continue'

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("fleet-launcher-test-" + [guid]::NewGuid().ToString('N'))
$fleetDir = Join-Path $work 'fleet'
New-Item -ItemType Directory -Force -Path (Join-Path $fleetDir 'bin'), (Join-Path $fleetDir 'app') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'launcher.cmd') (Join-Path $fleetDir 'bin\fleet.cmd')
Set-Content -Path (Join-Path $fleetDir 'VERSION') -Value '0.0.0-test'

Add-Type -OutputType ConsoleApplication -OutputAssembly (Join-Path $fleetDir 'app\WeaveFleet.Api.exe') -TypeDefinition @'
using System;
using System.IO;
public static class Program
{
    public static void Main(string[] args)
    {
        File.WriteAllLines(Environment.GetEnvironmentVariable("FLEET_TEST_OUT"), new[]
        {
            "args=" + string.Join(" ", args),
            "host=" + Environment.GetEnvironmentVariable("Fleet__Host"),
            "port=" + Environment.GetEnvironmentVariable("Fleet__Port"),
            "require_token=" + Environment.GetEnvironmentVariable("Fleet__Auth__RequireToken"),
        });
    }
}
'@

$fleet = Join-Path $fleetDir 'bin\fleet.cmd'
$out = Join-Path $work 'out.txt'
$env:FLEET_TEST_OUT = $out
$env:WEAVE_FLEET_DATA_DIR = Join-Path $work 'data'
Remove-Item Env:\Fleet__Auth__RequireToken -ErrorAction SilentlyContinue
Remove-Item Env:\WEAVE_FLEET_PORT -ErrorAction SilentlyContinue
Remove-Item Env:\WEAVE_FLEET_HOST -ErrorAction SilentlyContinue

$script:failures = 0
function Fail([string]$message) {
    Write-Host "FAIL: $message"
    $script:failures++
}

# Runs bin\fleet.cmd; returns its console output. The stub's record lands in $out.
function Invoke-Fleet([string]$arguments) {
    Remove-Item $out -ErrorAction SilentlyContinue
    $output = & cmd.exe /d /c "`"$fleet`" $arguments 2>&1"
    $script:fleetExitCode = $LASTEXITCODE
    return ($output -join "`n")
}

function Test-Started([string]$arguments, [string]$output) {
    if (-not (Test-Path $out)) {
        Fail "fleet $arguments didn't start the app: $output"
        return $false
    }
    return $true
}

function Test-Record([string]$expected, [string]$arguments) {
    $record = Get-Content $out
    if ($record -notcontains $expected) {
        Fail "expected '$expected' after fleet $arguments, got: $($record -join ' ')"
    }
}

# --require-token reaches the app as Fleet:Auth:RequireToken.
$output = Invoke-Fleet '--port 2113 --require-token'
if (Test-Started '--port 2113 --require-token' $output) {
    Test-Record 'require_token=true' '--port 2113 --require-token'
    Test-Record 'port=2113' '--port 2113 --require-token'
    Test-Record 'host=127.0.0.1' '--port 2113 --require-token'
}
$output = Invoke-Fleet '--require-token --host 127.0.0.1'
if (Test-Started '--require-token --host 127.0.0.1' $output) {
    Test-Record 'require_token=true' '--require-token --host 127.0.0.1'
}

# Without it, the launcher leaves the setting alone.
$output = Invoke-Fleet '--port 2113'
if (Test-Started '--port 2113' $output) {
    Test-Record 'require_token=' '--port 2113'
}

# Unknown options still stop before the app starts, so a typo can't start Fleet without the token.
$output = Invoke-Fleet '--port 2113 --requre-token'
if ($script:fleetExitCode -eq 0) { Fail "fleet --requre-token should fail" }
if (Test-Path $out) { Fail "fleet --requre-token started the app" }
if ($output -notmatch 'Unknown command or option: --requre-token') { Fail "fleet --requre-token: unexpected output: $output" }

# Every option `fleet help` lists is accepted.
$help = Invoke-Fleet 'help'
$helpOptions = [regex]::Matches($help, '--[a-z][a-z-]*') | ForEach-Object { $_.Value } | Sort-Object -Unique
if (-not $helpOptions) { Fail "fleet help listed no options: $help" }
$values = @{
    '--require-token' = ''
    '--port' = '2113'
    '--host' = '127.0.0.1'
    '--profile' = 'test'
    '--data-dir' = "`"$(Join-Path $work 'data-dir')`""
}
foreach ($option in $helpOptions) {
    if (-not $values.ContainsKey($option)) {
        Fail "fleet help lists $option, which this test doesn't know how to run"
        continue
    }
    $arguments = "$option $($values[$option])".Trim()
    $output = Invoke-Fleet $arguments
    Test-Started $arguments $output | Out-Null
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) launcher check(s) failed."
    exit 1
}
Write-Host 'Launcher checks passed.'
