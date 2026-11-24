# Runs scripts/install.ps1 the way `fleet update` does (Windows PowerShell 5.1) against a dummy release served from disk
# (file://). First on PSModulePath is a stand-in Archive module whose Expand-Archive does what one built for .NET Core
# does: it calls ZipArchive.ExtractToDirectory(destination, overwrite), which .NET Framework doesn't have. Windows
# PowerShell can load such a module, and the install must work anyway. The Windows half of scripts/test-install.sh.
$ErrorActionPreference = 'Stop'

function Fail([string]$message) { Write-Host "FAIL: $message"; exit 1 }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("fleet-install-test-" + [guid]::NewGuid().ToString('N'))
$savedModulePath = $env:PSModulePath
try {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
    $assetBase = "fleet-v0.0.1-win-$arch"
    $pkg = Join-Path $work "pkg\$assetBase"
    $release = Join-Path $work 'release'
    New-Item -ItemType Directory -Force -Path "$pkg\app", "$pkg\bin", $release | Out-Null
    Set-Content "$pkg\app\WeaveFleet.Api.exe" 'dummy'
    Set-Content "$pkg\bin\fleet.cmd" '@echo fleet'
    Set-Content "$pkg\VERSION" '0.0.1'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $release "$assetBase.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Set-Content (Join-Path $release "$assetBase.zip.sha256") ((Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant() + "  $assetBase.zip")

    $mod = Join-Path $work 'modules\Microsoft.PowerShell.Archive'
    New-Item -ItemType Directory -Force -Path $mod | Out-Null
    Set-Content (Join-Path $mod 'Microsoft.PowerShell.Archive.psd1') "@{ ModuleVersion = '9.9.9'; RootModule = 'Microsoft.PowerShell.Archive.psm1'; FunctionsToExport = @('Expand-Archive') }"
    Set-Content (Join-Path $mod 'Microsoft.PowerShell.Archive.psm1') @'
function Expand-Archive {
    param([string] $Path, [string] $DestinationPath, [switch] $Force)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $DestinationPath, [bool] $Force) }
    finally { $archive.Dispose() }
}
'@
    $env:PSModulePath = (Join-Path $work 'modules') + ';' + $savedModulePath

    # Windows PowerShell turns a native command's stderr into errors, which 'Stop' would throw on; the checks below look.
    $ErrorActionPreference = 'Continue'

    # The stand-in must break Expand-Archive the way the report did, or this test proves nothing.
    $probe = Join-Path $work 'probe'
    $probeOut = & powershell -NoProfile -Command "Expand-Archive -Path '$zip' -DestinationPath '$probe' -Force" 2>&1 | Out-String
    if ($probeOut -notmatch 'Cannot find an overload for "ExtractToDirectory" and the argument count: "3"') { Fail "the stand-in Expand-Archive didn't fail as expected: $probeOut" }

    $installDir = Join-Path $work 'home\fleet'
    $env:WEAVE_FLEET_VERSION = 'v0.0.1'
    $env:WEAVE_FLEET_DOWNLOAD_BASE_URL = ([System.Uri] $release).AbsoluteUri
    $env:WEAVE_FLEET_INSTALL_DIR = $installDir
    $env:WEAVE_FLEET_SKIP_PATH_UPDATE = '1'
    $script = Join-Path $PSScriptRoot 'install.ps1'
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -Raw '$script' | Invoke-Expression" 2>&1 | Out-String
    Write-Host $out
    if ($LASTEXITCODE -ne 0) { Fail "install.ps1 exited with $LASTEXITCODE" }
    foreach ($file in 'app\WeaveFleet.Api.exe', 'bin\fleet.cmd', 'VERSION') {
        if (-not (Test-Path (Join-Path $installDir $file))) { Fail "install.ps1 didn't install $file" }
    }
    if ($out -notmatch 'Fleet installed to') { Fail "install.ps1 didn't say it installed" }
    Write-Host 'install.ps1 installs when Windows PowerShell loads an Archive module built for .NET Core'
}
finally {
    $env:PSModulePath = $savedModulePath
    foreach ($name in 'WEAVE_FLEET_VERSION', 'WEAVE_FLEET_DOWNLOAD_BASE_URL', 'WEAVE_FLEET_INSTALL_DIR', 'WEAVE_FLEET_SKIP_PATH_UPDATE') {
        Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
