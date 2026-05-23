@echo off
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
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

:apply_staged_update
set "UPDATE_DIR=%ROOT_DIR%\update"
set "MANIFEST=%UPDATE_DIR%\update-manifest.json"
if not exist "%MANIFEST%" goto :eof

echo Checking for staged Fleet update...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
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
echo Usage: fleet [command] [--port ^<port^>] [--host ^<host^>] [--data-dir ^<path^>] [--profile ^<name^>]
echo.
echo Commands:
echo   (none)       Start the Fleet server
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
echo.
echo Environment variables:
echo   WEAVE_FLEET_PORT                Server port ^(default: 5000^)
echo   WEAVE_FLEET_HOST                Bind host ^(default: 127.0.0.1^)
echo   WEAVE_FLEET_DATA_DIR            Data directory ^(default: %%USERPROFILE%%\.weave^)
echo   Fleet__DatabasePath             SQLite database path override
echo   Fleet__AnalyticsDatabasePath    Analytics database path override
echo   Fleet__DataProtection__KeyPath  Data protection key directory override
exit /b 0

:parse_args
set "PORT_OVERRIDE="
set "HOST_OVERRIDE="
set "DATA_DIR_OVERRIDE="
set "PROFILE_NAME="

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
    set "WEAVE_FLEET_PORT=5000"
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

echo Fleet v!VERSION! starting on %LISTEN_URL%
if defined EXTRA_ARGS (
    "%APP_BIN%" --urls "%LISTEN_URL%" --contentRoot "%APP_CONTENT_ROOT%" !EXTRA_ARGS!
) else (
    "%APP_BIN%" --urls "%LISTEN_URL%" --contentRoot "%APP_CONTENT_ROOT%"
)
exit /b %ERRORLEVEL%
