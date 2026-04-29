param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [string] $OutputDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string] $Version,

    [string] $PackageName = 'fleet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-VersionFromProps {
    param(
        [string] $RootDir
    )

    $propsPath = Join-Path $RootDir 'Directory.Build.props'
    $propsContent = Get-Content -Raw -Path $propsPath
    $match = [System.Text.RegularExpressions.Regex]::Match($propsContent, '<Version>([^<]+)</Version>')

    if (-not $match.Success) {
        throw "Could not determine version from $propsPath."
    }

    return $match.Groups[1].Value
}

function Get-NormalizedTag {
    param(
        [string] $PackageVersion
    )

    if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
        throw 'Version is required.'
    }

    if ($PackageVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $PackageVersion
    }

    return "v$PackageVersion"
}

function Get-ArchiveExtension {
    param(
        [string] $RuntimeIdentifier
    )

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return '.zip'
    }

    return '.tar.gz'
}

function Get-LauncherSourcePath {
    param(
        [string] $RuntimeIdentifier
    )

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return Join-Path $PSScriptRoot 'launcher.cmd'
    }

    return Join-Path $PSScriptRoot 'launcher.sh'
}

function Get-LauncherTargetName {
    param(
        [string] $RuntimeIdentifier
    )

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'fleet.cmd'
    }

    return 'fleet'
}

function Write-Log {
    param(
        [string] $Message
    )

    Write-Host $Message
}

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish directory does not exist: $PublishDir"
}

$publishItems = Get-ChildItem -Force -Path $PublishDir
if ($publishItems.Count -eq 0) {
    throw "Publish directory is empty: $PublishDir"
}

$rootDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedOutputDir = if (Test-Path -LiteralPath $OutputDir -PathType Container) {
    (Resolve-Path $OutputDir).Path
}
else {
    $null = New-Item -ItemType Directory -Path $OutputDir -Force
    (Resolve-Path $OutputDir).Path
}

$packageVersion = if ($Version) { $Version } else { Get-VersionFromProps -RootDir $rootDir }
$releaseTag = Get-NormalizedTag -PackageVersion $packageVersion
$archiveExtension = Get-ArchiveExtension -RuntimeIdentifier $Rid
$assetBaseName = "$PackageName-$releaseTag-$Rid"
$archiveName = "$assetBaseName$archiveExtension"
$archivePath = Join-Path $resolvedOutputDir $archiveName
$checksumPath = "$archivePath.sha256"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $workDir $assetBaseName
$packageBinDir = Join-Path $packageRoot 'bin'
$packageAppDir = Join-Path $packageRoot 'app'

try {
    $null = New-Item -ItemType Directory -Path $packageBinDir -Force
    $null = New-Item -ItemType Directory -Path $packageAppDir -Force

    $launcherSourcePath = Get-LauncherSourcePath -RuntimeIdentifier $Rid
    $launcherTargetName = Get-LauncherTargetName -RuntimeIdentifier $Rid
    Copy-Item -LiteralPath $launcherSourcePath -Destination (Join-Path $packageBinDir $launcherTargetName) -Force

    foreach ($item in $publishItems) {
        Copy-Item -LiteralPath $item.FullName -Destination $packageAppDir -Recurse -Force
    }

    Set-Content -Path (Join-Path $packageRoot 'VERSION') -Value $packageVersion -NoNewline

    if ($launcherTargetName -eq 'fleet') {
        chmod +x (Join-Path $packageBinDir $launcherTargetName)

        $apiBinaryPath = Join-Path $packageAppDir 'WeaveFleet.Api'
        if (Test-Path -LiteralPath $apiBinaryPath -PathType Leaf) {
            chmod +x $apiBinaryPath
        }
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -Force -Path $archivePath
    }

    if (Test-Path -LiteralPath $checksumPath) {
        Remove-Item -Force -Path $checksumPath
    }

    if ($archiveExtension -eq '.zip') {
        Compress-Archive -Path $packageRoot -DestinationPath $archivePath -Force
    }
    else {
        tar -czf $archivePath -C $workDir $assetBaseName
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed while creating $archiveName."
        }
    }

    $hash = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
    Set-Content -Path $checksumPath -Value "$hash  $archiveName" -NoNewline

    Write-Log "Created $archivePath"
    Write-Log "Created $checksumPath"
}
finally {
    if (Test-Path -LiteralPath $workDir) {
        Remove-Item -Recurse -Force -Path $workDir
    }
}
