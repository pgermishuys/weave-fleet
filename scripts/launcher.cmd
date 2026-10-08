@echo off
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
rem Captured before any shift: shift moves %0 too.
set "LAUNCHER_PATH=%~f0"
set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..") do set "ROOT_DIR=%%~fI"

set "PACKAGE_APP_DIR=%ROOT_DIR%\app"
set "PACKAGE_BIN=%PACKAGE_APP_DIR%\WeaveFleet.Api.exe"
set "PACKAGE_CONTENT_ROOT=%PACKAGE_APP_DIR%"
set "REPO_APP_DIR=%ROOT_DIR%\src\WeaveFleet.Api\bin\Release\net10.0"
set "REPO_BIN=%REPO_APP_DIR%\WeaveFleet.Api.exe"
set "REPO_CONTENT_ROOT=%ROOT_DIR%\src\WeaveFleet.Api"
set "VERSION_FILE=%ROOT_DIR%\VERSION"
set "DEV_VERSION_FILE=%ROOT_DIR%\Directory.Build.props"
set "INSTALL_SCRIPT_URL=%WEAVE_FLEET_INSTALL_SCRIPT_URL%"
if not defined INSTALL_SCRIPT_URL set "INSTALL_SCRIPT_URL=https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.ps1"

set "APP_DIR="
set "APP_BIN="
set "APP_CONTENT_ROOT="
set "INSTALL_LAYOUT=0"

call :detect_powershell

if exist "%PACKAGE_BIN%" (
    set "APP_DIR=%PACKAGE_APP_DIR%"
    set "APP_BIN=%PACKAGE_BIN%"
    set "APP_CONTENT_ROOT=%PACKAGE_CONTENT_ROOT%"
    set "INSTALL_LAYOUT=1"
) else if exist "%REPO_BIN%" (
    set "APP_DIR=%REPO_APP_DIR%"
    set "APP_BIN=%REPO_BIN%"
    set "APP_CONTENT_ROOT=%REPO_CONTENT_ROOT%"
) else (
    echo Error: Fleet binary not found. >&2
    echo Expected one of: >&2
    echo   %PACKAGE_BIN% >&2
    echo   %REPO_BIN% >&2
    echo Build or publish Fleet first. >&2
    exit /b 1
)

if "%INSTALL_LAYOUT%"=="1" call :apply_staged_update

goto :parse_args

:detect_powershell
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    set "PS_CMD=pwsh"
) else (
    set "PS_CMD=powershell"
)
goto :eof

:apply_staged_update
set "UPDATE_DIR=%ROOT_DIR%\update"
set "MANIFEST=%UPDATE_DIR%\update-manifest.json"
if not exist "%MANIFEST%" goto :eof

echo Checking for staged Fleet update...

%PS_CMD% -NoProfile -ExecutionPolicy Bypass -Command ^
  "$m = Get-Content '%MANIFEST%' | ConvertFrom-Json;" ^
  "$v = $m.version; $a = $m.assetFileName;" ^
  "if (-not $v -or -not $a) { exit 1 };" ^
  "$archive = '%UPDATE_DIR%\' + $a;" ^
  "if (-not (Test-Path $archive)) { Write-Host 'Warning: archive not found.'; exit 1 };" ^
  "Write-Host ('Applying Fleet update to v' + $v + '...');" ^
  "$appDir = '%ROOT_DIR%\app'; $appBak = '%ROOT_DIR%\app.bak';" ^
  "if (Test-Path $appBak) { Remove-Item $appBak -Recurse -Force };" ^
  "Copy-Item $appDir $appBak -Recurse;" ^
  "$tmp = '%UPDATE_DIR%\extract_tmp';" ^
  "if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force };" ^
  "New-Item -ItemType Directory -Path $tmp | Out-Null;" ^
  "if ($a.EndsWith('.zip')) { Expand-Archive -Path $archive -DestinationPath $tmp -Force }" ^
  "else { tar -xzf $archive -C $tmp };" ^
  "$extracted = Get-ChildItem $tmp | Where-Object { $_.PSIsContainer } | Select-Object -First 1;" ^
  "if (-not $extracted -or -not (Test-Path ($extracted.FullName + '\app'))) {" ^
  "  Write-Host 'Warning: unexpected archive layout — restoring backup.';" ^
  "  Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue;" ^
  "  Copy-Item $appBak $appDir -Recurse;" ^
  "  Remove-Item $appBak -Recurse -Force;" ^
  "  Remove-Item '%UPDATE_DIR%' -Recurse -Force -ErrorAction SilentlyContinue;" ^
  "  exit 1" ^
  "};" ^
  "Remove-Item $appDir -Recurse -Force;" ^
  "Copy-Item ($extracted.FullName + '\app') $appDir -Recurse;" ^
  "$binDir = '%ROOT_DIR%\bin';" ^
  "if (Test-Path ($extracted.FullName + '\bin')) {" ^
  "  if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force };" ^
  "  Copy-Item ($extracted.FullName + '\bin') $binDir -Recurse" ^
  "};" ^
  "Set-Content -Path '%ROOT_DIR%\VERSION' -Value $v;" ^
  "Remove-Item $appBak -Recurse -Force -ErrorAction SilentlyContinue;" ^
  "Remove-Item '%UPDATE_DIR%' -Recurse -Force -ErrorAction SilentlyContinue;" ^
  "Write-Host ('Fleet updated to v' + $v + '.')"
goto :eof

:read_version
set "VERSION=unknown"
if exist "%VERSION_FILE%" (
    set /p VERSION=<"%VERSION_FILE%"
    goto :eof
)
if exist "%DEV_VERSION_FILE%" (
    for /f "tokens=3 delims=<>" %%V in ('findstr /c:"<Version>" "%DEV_VERSION_FILE%"') do (
        set "VERSION=%%V"
        goto :eof
    )
)
goto :eof

:show_version
call :read_version
echo !VERSION!
exit /b 0

:do_update
echo Updating Fleet...
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm %INSTALL_SCRIPT_URL% | iex"
exit /b %ERRORLEVEL%

:do_uninstall
if not "%INSTALL_LAYOUT%"=="1" (
    echo Error: uninstall is only supported from an installed package layout. >&2
    exit /b 1
)
echo Removing Fleet from %ROOT_DIR%...
rd /s /q "%ROOT_DIR%" >nul 2>&1 & echo Done. & exit /b 0

:show_help
call :read_version
echo Fleet v!VERSION!
echo.
echo Usage: fleet [command] [--port ^<port^>] [--host ^<host^>] [--data-dir ^<path^>] [--profile ^<name^>] [--require-token]
echo.
echo Commands:
echo   (none)       Start the Fleet server
echo   node         Start a node: the API without the web app ^(see "fleet node help"^)
echo   version      Print the installed version
echo   update       Update to the latest version
echo   uninstall    Remove Fleet
echo   help         Show this help message
echo   import-legacy-sessions  Import sessions from a legacy database
echo.
echo Options when starting the server:
echo   --port ^<port^>       Override the server port
echo   --host ^<host^>       Override the bind host
echo   --data-dir ^<path^>   Override the data directory (default: %%USERPROFILE%%\.weave)
echo   --profile ^<name^>    Use a profile-specific data directory
echo   --require-token     Ask for the access token even over loopback ^(behind tailscale serve^)
echo.
echo Environment variables:
echo   WEAVE_FLEET_PORT                Server port ^(default: 6262^)
echo   WEAVE_FLEET_HOST                Bind host ^(default: 127.0.0.1^)
echo   WEAVE_FLEET_DATA_DIR            Data directory ^(default: %%USERPROFILE%%\.weave^)
echo   Fleet__DatabasePath             SQLite database path override
echo   Fleet__AnalyticsDatabasePath    Analytics database path override
echo   Fleet__DataProtection__KeyPath  Data protection key directory override
exit /b 0

:show_node_help
call :read_version
echo Fleet v!VERSION!
echo.
echo Usage: fleet node [--port ^<port^>] [--host ^<host^>] [--data-dir ^<path^>] [--profile ^<name^>]
echo        fleet node install-service [--port ^<port^>] [--host ^<host^>] [--data-dir ^<path^>] [--profile ^<name^>] [--print]
echo        fleet node uninstall-service
echo.
echo Starts Fleet as a node: the API without the web app. Every request needs the access token,
echo even from this machine. Add the node to another Fleet in Settings ^> Machines ^> Add a machine.
echo.
echo Commands:
echo   install-service     Keep the node running as you: now, when you log on, and if it stops
echo                       ^(a scheduled task named "Fleet node"^)
echo   uninstall-service   Stop the node and remove that task
echo.
echo Options:
echo   --port ^<port^>       Override the server port
echo   --host ^<host^>       Override the bind host ^(0.0.0.0 lets other machines connect^)
echo   --data-dir ^<path^>   Override the data directory (default: %%USERPROFILE%%\.weave)
echo   --profile ^<name^>    Use a profile-specific data directory
echo   --print             With install-service: show what it would register and run, and change nothing
exit /b 0

:parse_args
set "PORT_OVERRIDE="
set "HOST_OVERRIDE="
set "DATA_DIR_OVERRIDE="
set "PROFILE_NAME="
set "REQUIRE_TOKEN="
set "NODE="
set "SERVICE_ACTION="
set "PRINT_ONLY="

rem "fleet node ..." takes the same server options; it only has to come first.
if /i "%~1"=="node" (
    set "NODE=1"
    shift
)
if defined NODE (
    if /i "%~1"=="install-service" (
        set "SERVICE_ACTION=install-service"
        shift
    ) else if /i "%~1"=="uninstall-service" (
        set "SERVICE_ACTION=uninstall-service"
        shift
    )
)

:parse_args_loop
if "%~1"=="" goto :start_server
if /i "%~1"=="version" goto :show_version_with_validation
if /i "%~1"=="--version" goto :show_version_with_validation
if /i "%~1"=="-v" goto :show_version_with_validation
if /i "%~1"=="update" goto :do_update_with_validation
if /i "%~1"=="uninstall" goto :do_uninstall_with_validation
if /i "%~1"=="help" goto :show_help_with_validation
if /i "%~1"=="--help" goto :show_help_with_validation
if /i "%~1"=="-h" goto :show_help_with_validation
if /i "%~1"=="import-legacy-sessions" goto :do_import_legacy

if /i "%~1"=="--port" (
    if "%~2"=="" (
        echo Error: --port requires a value. >&2
        exit /b 1
    )
    set "PORT_OVERRIDE=%~2"
    shift
    shift
    goto :parse_args_loop
)

if /i "%~1"=="--host" (
    if "%~2"=="" (
        echo Error: --host requires a value. >&2
        exit /b 1
    )
    set "HOST_OVERRIDE=%~2"
    shift
    shift
    goto :parse_args_loop
)

if /i "%~1"=="--data-dir" (
    if "%~2"=="" (
        echo Error: --data-dir requires a value. >&2
        exit /b 1
    )
    set "DATA_DIR_OVERRIDE=%~2"
    shift
    shift
    goto :parse_args_loop
)

if /i "%~1"=="--profile" (
    if "%~2"=="" (
        echo Error: --profile requires a value. >&2
        exit /b 1
    )
    set "PROFILE_NAME=%~2"
    shift
    shift
    goto :parse_args_loop
)

if /i "%~1"=="--require-token" (
    set "REQUIRE_TOKEN=1"
    shift
    goto :parse_args_loop
)

if /i "%~1"=="--print" (
    if not "%SERVICE_ACTION%"=="install-service" goto :print_without_install
    set "PRINT_ONLY=1"
    shift
    goto :parse_args_loop
)

set "ARG=%~1"
if /i "!ARG:~0,7!"=="--port=" (
    set "PORT_OVERRIDE=!ARG:~7!"
    shift
    goto :parse_args_loop
)

if /i "!ARG:~0,7!"=="--host=" (
    set "HOST_OVERRIDE=!ARG:~7!"
    shift
    goto :parse_args_loop
)

if /i "!ARG:~0,11!"=="--data-dir=" (
    set "DATA_DIR_OVERRIDE=!ARG:~11!"
    shift
    goto :parse_args_loop
)

if /i "!ARG:~0,10!"=="--profile=" (
    set "PROFILE_NAME=!ARG:~10!"
    shift
    goto :parse_args_loop
)

echo Unknown command or option: %~1 >&2
echo Run "fleet help" for usage. >&2
exit /b 1

:show_version_with_validation
if not "%~2"=="" (
    echo Error: version does not accept additional arguments. >&2
    exit /b 1
)
goto :show_version

:do_update_with_validation
if not "%~2"=="" (
    echo Error: update does not accept additional arguments. >&2
    exit /b 1
)
goto :do_update

:do_uninstall_with_validation
if not "%~2"=="" (
    echo Error: uninstall does not accept additional arguments. >&2
    exit /b 1
)
goto :do_uninstall

:show_help_with_validation
if not "%~2"=="" (
    echo Error: help does not accept additional arguments. >&2
    exit /b 1
)
if defined NODE goto :show_node_help
goto :show_help

:do_import_legacy
set "IMPORT_ARGS=--import-legacy-sessions"
shift
:import_legacy_args_loop
if "%~1"=="" goto :start_server_with_import
if /i "%~1"=="--source" (
    if "%~2"=="" (
        echo Error: --source requires a value. >&2
        exit /b 1
    )
    set "IMPORT_ARGS=!IMPORT_ARGS! --source %~2"
    shift
    shift
    goto :import_legacy_args_loop
)
echo Unknown option for import-legacy-sessions: %~1 >&2
exit /b 1

:start_server_with_import
set "EXTRA_ARGS=!IMPORT_ARGS!"
goto :start_server

:start_server
if defined PORT_OVERRIDE (
    echo(%PORT_OVERRIDE%| findstr /r "^[0-9][0-9]*$" >nul || (
        echo Error: --port must be a numeric value. >&2
        exit /b 1
    )
)

if defined PROFILE_NAME (
    echo(%PROFILE_NAME%| findstr /r "^[A-Za-z0-9._-][A-Za-z0-9._-]*$" >nul || (
        echo Error: --profile may only contain letters, numbers, dots, underscores, and hyphens. >&2
        exit /b 1
    )
)

call :read_version
if defined PORT_OVERRIDE (
    set "WEAVE_FLEET_PORT=%PORT_OVERRIDE%"
) else if not defined WEAVE_FLEET_PORT (
    set "WEAVE_FLEET_PORT=6262"
)
if defined HOST_OVERRIDE (
    set "WEAVE_FLEET_HOST=%HOST_OVERRIDE%"
) else if not defined WEAVE_FLEET_HOST (
    set "WEAVE_FLEET_HOST=127.0.0.1"
)
set "LISTEN_URL=http://%WEAVE_FLEET_HOST%:%WEAVE_FLEET_PORT%"
if defined DATA_DIR_OVERRIDE (
    set "DATA_DIR=%DATA_DIR_OVERRIDE%"
) else if defined WEAVE_FLEET_DATA_DIR (
    set "DATA_DIR=%WEAVE_FLEET_DATA_DIR%"
) else (
    set "DATA_DIR=%USERPROFILE%\.weave"
)
if defined PROFILE_NAME set "DATA_DIR=%DATA_DIR%\profiles\%PROFILE_NAME%"
set "DB_PATH_DEFAULT=%DATA_DIR%\fleet.db"
set "ANALYTICS_DB_PATH_DEFAULT=%DATA_DIR%\fleet-analytics.db"
set "KEY_DIR_DEFAULT=%DATA_DIR%\fleet-keys"

if defined SERVICE_ACTION goto :do_service

if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"
if not exist "%KEY_DIR_DEFAULT%" mkdir "%KEY_DIR_DEFAULT%"

set "ASPNETCORE_ENVIRONMENT=Production"
set "ASPNETCORE_URLS=%LISTEN_URL%"
set "URLS=%LISTEN_URL%"
set "ASPNETCORE_CONTENTROOT=%APP_CONTENT_ROOT%"
set "Fleet__Host=%WEAVE_FLEET_HOST%"
set "Fleet__Port=%WEAVE_FLEET_PORT%"
if not defined Fleet__DatabasePath set "Fleet__DatabasePath=%DB_PATH_DEFAULT%"
if not defined Fleet__AnalyticsDatabasePath set "Fleet__AnalyticsDatabasePath=%ANALYTICS_DB_PATH_DEFAULT%"
if not defined Fleet__DataProtection__KeyPath set "Fleet__DataProtection__KeyPath=%KEY_DIR_DEFAULT%"
if defined REQUIRE_TOKEN set "Fleet__Auth__RequireToken=true"

if defined NODE (
    set "EXTRA_ARGS=!EXTRA_ARGS! --node"
    echo Fleet v!VERSION! starting as a node on %LISTEN_URL%
) else (
    echo Fleet v!VERSION! starting on %LISTEN_URL%
)
if defined EXTRA_ARGS (
    "%APP_BIN%" --urls "%LISTEN_URL%" --contentRoot "%APP_CONTENT_ROOT%" !EXTRA_ARGS!
) else (
    "%APP_BIN%" --urls "%LISTEN_URL%" --contentRoot "%APP_CONTENT_ROOT%"
)
exit /b %ERRORLEVEL%

rem fleet node install-service / uninstall-service: a scheduled task named "Fleet node" that runs this launcher as
rem you at log on, hidden, and starts it again when it stops (not after exit code 75: another Fleet already uses the
rem data directory). Never a Windows service, which would run as SYSTEM, away from your sign-ins and repositories.
:do_service
if "%SERVICE_ACTION%"=="uninstall-service" (
    if defined PORT_OVERRIDE goto :service_no_options
    if defined HOST_OVERRIDE goto :service_no_options
    if defined DATA_DIR_OVERRIDE goto :service_no_options
    if defined PROFILE_NAME goto :service_no_options
)
rem The task gets what this run resolved, so it starts the same node whatever its own environment holds.
for %%I in ("%DATA_DIR%") do set "DATA_DIR=%%~fI"
set "FLEET_SVC_ACTION=%SERVICE_ACTION%"
set "FLEET_SVC_PRINT=%PRINT_ONLY%"
set "FLEET_SVC_INSTALL_LAYOUT=%INSTALL_LAYOUT%"
set "FLEET_SVC_LAUNCHER=%LAUNCHER_PATH%"
set "FLEET_SVC_PORT=%WEAVE_FLEET_PORT%"
set "FLEET_SVC_HOST=%WEAVE_FLEET_HOST%"
set "FLEET_SVC_DATA_DIR=%DATA_DIR%"
%PS_CMD% -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$name = 'Fleet node';" ^
  "$existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;" ^
  "if ($env:FLEET_SVC_ACTION -eq 'uninstall-service') {" ^
  "  if (-not $existing) { Write-Host 'There''s no Fleet node task to remove.'; exit 0 };" ^
  "  Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue;" ^
  "  Unregister-ScheduledTask -TaskName $name -Confirm:$false;" ^
  "  Write-Host 'Stopped the Fleet node task and removed it.';" ^
  "  Write-Host 'Its data stays where it was. To start it again: fleet node install-service';" ^
  "  exit 0" ^
  "};" ^
  "$dq = [char]34; $nl = [char]10;" ^
  "$quote = { param($s) '''' + ($s -replace '''', '''''') + '''' };" ^
  "$run = '& ' + (& $quote $env:FLEET_SVC_LAUNCHER) + ' node --port ' + (& $quote $env:FLEET_SVC_PORT) + ' --host ' + (& $quote $env:FLEET_SVC_HOST) + ' --data-dir ' + (& $quote $env:FLEET_SVC_DATA_DIR);" ^
  "$loop = 'while ($true) { ' + $run + '; if ($LASTEXITCODE -eq 75) { break }; Start-Sleep -Seconds 5 }';" ^
  "$arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ' + $dq + $loop + $dq;" ^
  "$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name;" ^
  "$esc = { param($s) [System.Security.SecurityElement]::Escape($s) };" ^
  "$userXml = & $esc $user; $argumentsXml = & $esc $arguments; $homeXml = & $esc $env:USERPROFILE;" ^
  "$xml = @(" ^
  "  '<?xml version=''1.0'' encoding=''UTF-16''?>'," ^
  "  '<Task version=''1.2'' xmlns=''http://schemas.microsoft.com/windows/2004/02/mit/task''>'," ^
  "  '  <RegistrationInfo>'," ^
  "  '    <Description>Weave Fleet node. Written by fleet node install-service; fleet node uninstall-service removes it.</Description>'," ^
  "  '  </RegistrationInfo>'," ^
  "  '  <Triggers>'," ^
  "  ('    <LogonTrigger><Enabled>true</Enabled><UserId>' + $userXml + '</UserId></LogonTrigger>')," ^
  "  '  </Triggers>'," ^
  "  '  <Principals>'," ^
  "  ('    <Principal id=''Author''><UserId>' + $userXml + '</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>')," ^
  "  '  </Principals>'," ^
  "  '  <Settings>'," ^
  "  '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>'," ^
  "  '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>'," ^
  "  '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>'," ^
  "  '    <StartWhenAvailable>true</StartWhenAvailable>'," ^
  "  '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>'," ^
  "  '    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>'," ^
  "  '  </Settings>'," ^
  "  '  <Actions Context=''Author''>'," ^
  "  '    <Exec>'," ^
  "  '      <Command>powershell.exe</Command>'," ^
  "  ('      <Arguments>' + $argumentsXml + '</Arguments>')," ^
  "  ('      <WorkingDirectory>' + $homeXml + '</WorkingDirectory>')," ^
  "  '    </Exec>'," ^
  "  '  </Actions>'," ^
  "  '</Task>'" ^
  ") -join $nl;" ^
  "if ($env:FLEET_SVC_PRINT -eq '1') {" ^
  "  Write-Host ('fleet node install-service would register the scheduled task ' + $name + ':');" ^
  "  Write-Host '';" ^
  "  Write-Host $xml;" ^
  "  Write-Host '';" ^
  "  Write-Host 'and run:';" ^
  "  Write-Host ('  Register-ScheduledTask -TaskName ' + (& $quote $name) + ' -Xml <the task above> -Force');" ^
  "  Write-Host ('  Start-ScheduledTask -TaskName ' + (& $quote $name));" ^
  "  Write-Host '';" ^
  "  Write-Host 'Nothing was changed.';" ^
  "  exit 0" ^
  "};" ^
  "if ($env:FLEET_SVC_INSTALL_LAYOUT -ne '1') { Write-Host 'Error: install-service is only supported from an installed package layout.'; exit 1 };" ^
  "if ($existing) { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue };" ^
  "Register-ScheduledTask -TaskName $name -Xml $xml -Force | Out-Null;" ^
  "$started = $true;" ^
  "try { Start-ScheduledTask -TaskName $name } catch { $started = $false };" ^
  "Write-Host '';" ^
  "if ($existing) { Write-Host 'Updated the Fleet node task and restarted it.' }" ^
  "elseif ($started) { Write-Host 'Installed the Fleet node task. It runs now, when you log on, and again if it stops.' }" ^
  "else { Write-Host 'Installed the Fleet node task. It starts when you next log on.' };" ^
  "Write-Host ('  Task:           ' + $name + ' (Task Scheduler)');" ^
  "Write-Host ('  Data and token: ' + $env:FLEET_SVC_DATA_DIR + ' (the token is in fleet.machine.json)');" ^
  "Write-Host ('  Status:         Get-ScheduledTask -TaskName ' + (& $quote $name));" ^
  "Write-Host '';" ^
  "Write-Host 'To undo: fleet node uninstall-service'"
exit /b %ERRORLEVEL%

:service_no_options
echo Error: uninstall-service does not accept options. >&2
exit /b 1

:print_without_install
echo Error: --print only works with fleet node install-service. >&2
exit /b 1
