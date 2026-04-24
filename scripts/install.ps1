Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = if ($env:WEAVE_FLEET_GITHUB_REPO) { $env:WEAVE_FLEET_GITHUB_REPO } else { 'pgermishuys/weave-fleet' }
$homeDirectory = [Environment]::GetFolderPath('UserProfile')
$installDir = if ($env:WEAVE_FLEET_INSTALL_DIR) { $env:WEAVE_FLEET_INSTALL_DIR } else { Join-Path $homeDirectory '.weave/weave-fleet' }
$checksumsName = if ($env:WEAVE_FLEET_CHECKSUMS_NAME) { $env:WEAVE_FLEET_CHECKSUMS_NAME } else { 'checksums.txt' }
$skipPathUpdate = $env:WEAVE_FLEET_SKIP_PATH_UPDATE -eq '1'

function Write-Log {
    param(
        [string] $Message
    )

    Write-Host $Message
}

function Get-NormalizedTag {
    param(
        [string] $Version
    )

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Version is required.'
    }

    if ($Version.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Version
    }

    return "v$Version"
}

function Get-ReleaseTag {
    if ($env:WEAVE_FLEET_VERSION) {
        return Get-NormalizedTag -Version $env:WEAVE_FLEET_VERSION
    }

    $releaseApiUrl = if ($env:WEAVE_FLEET_RELEASE_API_URL) {
        $env:WEAVE_FLEET_RELEASE_API_URL
    }
    else {
        "https://api.github.com/repos/$repo/releases/latest"
    }

    $release = Invoke-RestMethod -Uri $releaseApiUrl
    if (-not $release.tag_name) {
        throw 'Could not determine the latest Weave Fleet release tag.'
    }

    return Get-NormalizedTag -Version $release.tag_name
}

function Get-DownloadBaseUrl {
    param(
        [string] $ReleaseTag
    )

    if ($env:WEAVE_FLEET_DOWNLOAD_BASE_URL) {
        return $env:WEAVE_FLEET_DOWNLOAD_BASE_URL
    }

    return "https://github.com/$repo/releases/download/$ReleaseTag"
}

function Get-WindowsAssetBaseName {
    param(
        [string] $ReleaseTag
    )

    return "weave-fleet-$ReleaseTag-win-x64"
}

function Get-ExpectedHashFromContent {
    param(
        [string] $Content,
        [string] $AssetName
    )

    foreach ($line in ($Content -split "`r?`n")) {
        $trimmedLine = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedLine)) {
            continue
        }

        $parts = $trimmedLine -split '\s+', 2
        if ($parts.Count -eq 0 -or $parts[0] -notmatch '^[0-9A-Fa-f]+$') {
            continue
        }

        if ($parts.Count -eq 1) {
            return $parts[0]
        }

        if ($parts[1].Contains($AssetName, [System.StringComparison]::Ordinal)) {
            return $parts[0]
        }
    }

    return $null
}

function Get-ExpectedHash {
    param(
        [string] $DownloadBaseUrl,
        [string] $AssetName,
        [string] $WorkDir
    )

    $perAssetChecksumUrl = "$DownloadBaseUrl/$AssetName.sha256"
    $perAssetChecksumPath = Join-Path $WorkDir "$AssetName.sha256"

    try {
        Invoke-WebRequest -Uri $perAssetChecksumUrl -OutFile $perAssetChecksumPath | Out-Null
        $perAssetChecksum = Get-Content -Raw -Path $perAssetChecksumPath
        $expectedHash = Get-ExpectedHashFromContent -Content $perAssetChecksum -AssetName $AssetName
        if ($expectedHash) {
            return $expectedHash
        }
    }
    catch {
    }

    $checksumsUrl = "$DownloadBaseUrl/$checksumsName"
    $checksumsPath = Join-Path $WorkDir $checksumsName
    try {
        Invoke-WebRequest -Uri $checksumsUrl -OutFile $checksumsPath | Out-Null
        $checksumsContent = Get-Content -Raw -Path $checksumsPath
        $expectedHash = Get-ExpectedHashFromContent -Content $checksumsContent -AssetName $AssetName
        if ($expectedHash) {
            return $expectedHash
        }
    }
    catch {
    }

    throw "Could not locate a checksum for $AssetName."
}

function Get-PackageRoot {
    param(
        [string] $ExtractDir
    )

    if ((Test-Path (Join-Path $ExtractDir 'app')) -and (Test-Path (Join-Path $ExtractDir 'bin'))) {
        return $ExtractDir
    }

    $children = @(Get-ChildItem -Path $ExtractDir -Directory)
    if ($children.Count -eq 1) {
        $candidate = $children[0].FullName
        if ((Test-Path (Join-Path $candidate 'app')) -and (Test-Path (Join-Path $candidate 'bin'))) {
            return $candidate
        }
    }

    throw 'Extracted archive did not contain the expected Weave Fleet package layout.'
}

function Update-UserPath {
    param(
        [string] $BinDir
    )

    if ($skipPathUpdate) {
        Write-Log 'Skipping PATH update because WEAVE_FLEET_SKIP_PATH_UPDATE=1.'
        return
    }

    $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $normalizedEntries = @()
    if ($currentUserPath) {
        $normalizedEntries = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
    }

    $userPathContainsBinDir = $false
    foreach ($entry in $normalizedEntries) {
        if ($entry.TrimEnd('\\') -eq $BinDir.TrimEnd('\\')) {
            $userPathContainsBinDir = $true
            break
        }
    }

    if (-not $userPathContainsBinDir) {
        $updatedUserPath = if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
            $BinDir
        }
        else {
            "$currentUserPath;$BinDir"
        }

        [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, 'User')
    }

    $pathSeparator = [System.IO.Path]::PathSeparator
    $currentProcessPath = $env:Path
    $processEntries = @()
    if ($currentProcessPath) {
        $processEntries = $currentProcessPath.Split($pathSeparator, [System.StringSplitOptions]::RemoveEmptyEntries)
    }

    $processPathContainsBinDir = $false
    foreach ($entry in $processEntries) {
        if ($entry.TrimEnd('\\') -eq $BinDir.TrimEnd('\\')) {
            $processPathContainsBinDir = $true
            break
        }
    }

    if (-not $processPathContainsBinDir) {
        $env:Path = if ([string]::IsNullOrWhiteSpace($currentProcessPath)) {
            $BinDir
        }
        else {
            "$BinDir$pathSeparator$currentProcessPath"
        }
    }

    if ((-not $userPathContainsBinDir) -and (-not $processPathContainsBinDir)) {
        Write-Log "Added $BinDir to the user PATH and current session PATH."
        return
    }

    if (-not $userPathContainsBinDir) {
        Write-Log "Added $BinDir to the user PATH."
        return
    }

    if (-not $processPathContainsBinDir) {
        Write-Log "Added $BinDir to the current session PATH."
        return
    }

    Write-Log "PATH already includes $BinDir."
}

$releaseTag = Get-ReleaseTag
$downloadBaseUrl = Get-DownloadBaseUrl -ReleaseTag $releaseTag
$assetBaseName = Get-WindowsAssetBaseName -ReleaseTag $releaseTag
$assetName = "$assetBaseName.zip"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $workDir

try {
    $archivePath = Join-Path $workDir $assetName

    try {
        Invoke-WebRequest -Uri "$downloadBaseUrl/$assetName" -OutFile $archivePath | Out-Null
    }
    catch {
        $assetName = "$assetBaseName.tar.gz"
        $archivePath = Join-Path $workDir $assetName
        Invoke-WebRequest -Uri "$downloadBaseUrl/$assetName" -OutFile $archivePath | Out-Null
    }

    $expectedHash = (Get-ExpectedHash -DownloadBaseUrl $downloadBaseUrl -AssetName $assetName -WorkDir $workDir).ToLowerInvariant()
    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
    if ($expectedHash -ne $actualHash) {
        throw "Checksum verification failed for $assetName."
    }

    $extractDir = Join-Path $workDir 'extracted'
    $null = New-Item -ItemType Directory -Path $extractDir

    if ($assetName.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force
    }
    else {
        tar -xzf $archivePath -C $extractDir
    }

    $packageRoot = Get-PackageRoot -ExtractDir $extractDir

    if (Test-Path $installDir) {
        Remove-Item -Recurse -Force -Path $installDir
    }

    $null = New-Item -ItemType Directory -Path $installDir -Force
    Copy-Item -Path (Join-Path $packageRoot '*') -Destination $installDir -Recurse -Force

    $binDir = Join-Path $installDir 'bin'
    Update-UserPath -BinDir $binDir

    Write-Log "Weave Fleet installed to $installDir."
    Write-Log 'Start it with: weave-fleet'
}
finally {
    if (Test-Path $workDir) {
        Remove-Item -Recurse -Force -Path $workDir
    }
}
