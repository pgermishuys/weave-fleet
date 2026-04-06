#!/usr/bin/env bash
# playwright-setup.sh — Install Playwright browsers for E2E tests.
# Run this once before running E2E tests locally or in CI.
#
# Usage:
#   ./tests/WeaveFleet.E2E/playwright-setup.sh
#
# Requirements:
#   - .NET SDK (same version as the project)
#   - The WeaveFleet.E2E project must have been built first:
#       dotnet build tests/WeaveFleet.E2E/
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "==> Building WeaveFleet.E2E to ensure Playwright binaries are present..."
dotnet build "${REPO_ROOT}/tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj" --configuration Release -v quiet

echo "==> Finding Playwright CLI..."
# The Playwright .NET package installs a playwright.ps1 PowerShell script next to the test assembly.
# On Linux/macOS we use the equivalent shell wrapper.
E2E_BIN_DIR="${REPO_ROOT}/tests/WeaveFleet.E2E/bin/Release/net10.0"

if [[ -f "${E2E_BIN_DIR}/playwright.ps1" ]]; then
    echo "==> Installing Chromium via Playwright PowerShell script..."
    pwsh "${E2E_BIN_DIR}/playwright.ps1" install chromium
elif command -v pwsh &>/dev/null; then
    # Fallback: locate playwright.ps1 via dotnet tool
    PLAYWRIGHT_PS1="$(find "${E2E_BIN_DIR}" -name 'playwright.ps1' 2>/dev/null | head -1)"
    if [[ -n "${PLAYWRIGHT_PS1}" ]]; then
        pwsh "${PLAYWRIGHT_PS1}" install chromium
    else
        echo "ERROR: Could not find playwright.ps1 in ${E2E_BIN_DIR}" >&2
        echo "Make sure the project was built first: dotnet build tests/WeaveFleet.E2E/" >&2
        exit 1
    fi
else
    echo "ERROR: pwsh (PowerShell Core) is not installed." >&2
    echo "Install PowerShell: https://github.com/PowerShell/PowerShell" >&2
    exit 1
fi

echo "==> Playwright Chromium installed successfully."
echo ""
echo "To run E2E tests:"
echo "  dotnet test tests/WeaveFleet.E2E/ --filter Category=E2E"
echo ""
echo "To run with a visible browser (headed mode, for debugging):"
echo "  HEADED=1 dotnet test tests/WeaveFleet.E2E/ --filter Category=E2E"
