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

function Test-StartedAsNode {
    $argsLine = Get-Content $out | Where-Object { $_ -like 'args=*' }
    return ($argsLine -split ' ') -contains '--node'
}

# `fleet node` starts the app with --node and the usual options; plain `fleet` doesn't.
$output = Invoke-Fleet 'node --port 5512 --host 0.0.0.0'
if (Test-Started 'node --port 5512 --host 0.0.0.0' $output) {
    if (-not (Test-StartedAsNode)) { Fail "fleet node didn't pass --node: $((Get-Content $out) -join ' ')" }
    Test-Record 'port=5512' 'node --port 5512 --host 0.0.0.0'
    Test-Record 'host=0.0.0.0' 'node --port 5512 --host 0.0.0.0'
}
$output = Invoke-Fleet '--port 5512'
if ((Test-Started '--port 5512' $output) -and (Test-StartedAsNode)) { Fail "fleet --port 5512 passed --node" }

# `node` only works first, and a typo after it still stops before the app starts.
$output = Invoke-Fleet '--port 5512 node'
if ($script:fleetExitCode -eq 0) { Fail "fleet --port 5512 node should fail" }
$output = Invoke-Fleet 'node --requre-token'
if ($script:fleetExitCode -eq 0) { Fail "fleet node --requre-token should fail" }
if (Test-Path $out) { Fail "fleet node --requre-token started the app" }

# `fleet node help` explains node mode, and every option it lists is accepted after `fleet node`.
$nodeHelp = Invoke-Fleet 'node help'
if (Test-Path $out) { Fail "fleet node help started the app" }
if ($nodeHelp -notmatch 'Usage: fleet node') { Fail "fleet node help: unexpected output: $nodeHelp" }
$nodeValues = @{
    '--port' = '2113'
    '--host' = '127.0.0.1'
    '--profile' = 'test'
    '--data-dir' = "`"$(Join-Path $work 'data-dir')`""
}
foreach ($option in ([regex]::Matches($nodeHelp, '--[a-z][a-z-]*') | ForEach-Object { $_.Value } | Sort-Object -Unique)) {
    if ($option -eq '--print') { continue }  # install-service only; checked below
    if (-not $nodeValues.ContainsKey($option)) {
        Fail "fleet node help lists $option, which this test doesn't know how to run"
        continue
    }
    $arguments = "node $option $($nodeValues[$option])"
    $output = Invoke-Fleet $arguments
    if ((Test-Started $arguments $output) -and -not (Test-StartedAsNode)) { Fail "fleet $arguments didn't pass --node" }
}

# install-service --print shows the scheduled task it would register, and changes nothing.
$taskName = 'Fleet node'
$output = Invoke-Fleet "node install-service --port 5512 --host 0.0.0.0 --data-dir `"$(Join-Path $work 'node data')`" --print"
if ($script:fleetExitCode -ne 0) { Fail "fleet node install-service --print failed: $output" }
if (Test-Path $out) { Fail "fleet node install-service --print started the app" }
foreach ($expected in @(
    '<LogonTrigger>',
    '<LogonType>InteractiveToken</LogonType>',
    '<RunLevel>LeastPrivilege</RunLevel>',
    '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>',
    '<Command>powershell.exe</Command>',
    '-WindowStyle Hidden',
    "fleet.cmd&apos; node --port &apos;5512&apos; --host &apos;0.0.0.0&apos; --data-dir &apos;$(Join-Path $work 'node data')&apos;",
    'if ($LASTEXITCODE -eq 75) { break }',
    'Nothing was changed.')) {
    if (-not $output.Contains($expected)) { Fail "fleet node install-service --print: missing '$expected' in: $output" }
}
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Fail "fleet node install-service --print registered the task" }
Write-Host '--- fleet node install-service --port 5512 --host 0.0.0.0 --print (Windows) ---'
Write-Host $output
Write-Host '---'

# --print belongs to install-service, and uninstall-service takes no options.
$output = Invoke-Fleet 'node --print'
if ($script:fleetExitCode -eq 0) { Fail "fleet node --print should fail: $output" }
$output = Invoke-Fleet 'node uninstall-service --port 5512'
if ($script:fleetExitCode -eq 0) { Fail "fleet node uninstall-service --port should fail: $output" }

# install-service registers the task as this user; installing again updates it; uninstall-service removes it.
$output = Invoke-Fleet 'node install-service --port 5512'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Fail "fleet node install-service registered no task: $output"
} else {
    if ($task.Actions[0].Execute -ne 'powershell.exe') { Fail "the task runs $($task.Actions[0].Execute), not powershell.exe" }
    if ($task.Actions[0].Arguments -notmatch "fleet\.cmd' node --port '5512'") { Fail "the task doesn't run the launcher as a node: $($task.Actions[0].Arguments)" }
    if ($task.Settings.ExecutionTimeLimit -ne 'PT0S') { Fail "the task has a time limit: $($task.Settings.ExecutionTimeLimit)" }
    if ($task.Triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger') { Fail "the task doesn't start at log on: $($task.Triggers[0].CimClass.CimClassName)" }
}
$output = Invoke-Fleet 'node install-service --port 5512'
if ($output -notmatch 'Updated the Fleet node task') { Fail "installing again didn't update the task: $output" }
$output = Invoke-Fleet 'node uninstall-service'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Fail "fleet node uninstall-service left the task: $output" }
if ($output -notmatch 'removed it') { Fail "fleet node uninstall-service: unexpected output: $output" }
$output = Invoke-Fleet 'node uninstall-service'
if ($output -notmatch "There's no Fleet node task") { Fail "fleet node uninstall-service with nothing installed: unexpected output: $output" }

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
