#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exports the OpenAPI specification from the WeaveFleet.Api project.

.DESCRIPTION
    Starts the API on a fixed port, waits for it to be ready, downloads the OpenAPI spec,
    saves it to client/openapi.json, and then stops the API process.
#>

$ErrorActionPreference = "Stop"

$apiPort = 5001
$apiUrl = "http://localhost:$apiPort"
$openApiEndpoint = "$apiUrl/openapi/v1.json"
$projectPath = Join-Path $PSScriptRoot ".." "src" "WeaveFleet.Api"
$outputDir = Join-Path $PSScriptRoot ".." "client"
$outputFile = Join-Path $outputDir "openapi.json"

Write-Host "Starting API export process..." -ForegroundColor Cyan

# Ensure output directory exists
if (-not (Test-Path -LiteralPath $outputDir)) {
    Write-Host "Creating output directory: $outputDir" -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Start the API in the background
Write-Host "Starting API on $apiUrl..." -ForegroundColor Cyan
$apiProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run", "--project", $projectPath, "--urls", $apiUrl `
    -PassThru `
    -NoNewWindow `
    -RedirectStandardOutput (Join-Path $env:TEMP "weavefleet-api-stdout.log") `
    -RedirectStandardError (Join-Path $env:TEMP "weavefleet-api-stderr.log")

try {
    # Wait for API to be ready (max 60 seconds)
    Write-Host "Waiting for API to be ready..." -ForegroundColor Cyan
    $maxAttempts = 60
    $attempt = 0
    $ready = $false

    while ($attempt -lt $maxAttempts -and -not $ready) {
        Start-Sleep -Seconds 1
        $attempt++
        
        try {
            $response = Invoke-WebRequest -Uri $openApiEndpoint -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                $ready = $true
                Write-Host "API is ready after $attempt seconds" -ForegroundColor Green
            }
        }
        catch {
            # API not ready yet, continue waiting
        }
    }

    if (-not $ready) {
        throw "API did not become ready within $maxAttempts seconds"
    }

    # Download the OpenAPI spec
    Write-Host "Downloading OpenAPI spec from $openApiEndpoint..." -ForegroundColor Cyan
    $spec = Invoke-RestMethod -Uri $openApiEndpoint -Method Get

    # Save to file
    Write-Host "Saving spec to $outputFile..." -ForegroundColor Cyan
    $spec | ConvertTo-Json -Depth 100 | Set-Content -Path $outputFile -Encoding UTF8

    # Validate the spec
    $savedSpec = Get-Content -Path $outputFile -Raw | ConvertFrom-Json
    $pathCount = ($savedSpec.paths.PSObject.Properties | Measure-Object).Count
    
    Write-Host "✓ OpenAPI spec exported successfully!" -ForegroundColor Green
    Write-Host "  Title: $($savedSpec.info.title)" -ForegroundColor Gray
    Write-Host "  Version: $($savedSpec.info.version)" -ForegroundColor Gray
    Write-Host "  Paths: $pathCount" -ForegroundColor Gray
    Write-Host "  Output: $outputFile" -ForegroundColor Gray
}
finally {
    # Stop the API process
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Write-Host "Stopping API process..." -ForegroundColor Cyan
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Write-Host "Export complete!" -ForegroundColor Green
