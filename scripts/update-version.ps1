#
# Computes and persists the build version for EyePeeOnMyTV.
#
# Format: major.minor.MMDDYY.revision
#   - major, minor    : read from version.json, never modified here (developer-controlled).
#   - MMDDYY           : today's date.
#   - revision         : increments by 1 for each build run on the same date, resets to 1
#                          on the first build of a new date.
#
# Invoked automatically by EyePeeOnMyTV.csproj's ComputeBuildVersion target before every
# build/publish. Prints ONLY the resulting full version string to stdout so MSBuild can
# capture it via ConsoleToMSBuild.
#
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir
)

$ErrorActionPreference = 'Stop'

$versionFile = Join-Path $ProjectDir 'version.json'

if (-not (Test-Path $versionFile)) {
    throw "version.json not found at $versionFile"
}

$data = Get-Content -Path $versionFile -Raw | ConvertFrom-Json

$today = Get-Date -Format 'MMddyy'

if ($data.buildDate -eq $today) {
    $revision = [int]$data.revision + 1
} else {
    $revision = 1
}

$fullVersion = "$($data.major).$($data.minor).$today.$revision"

$updated = [ordered]@{
    major     = $data.major
    minor     = $data.minor
    buildDate = $today
    revision  = $revision
    version   = $fullVersion
}

$json = $updated | ConvertTo-Json
[System.IO.File]::WriteAllText($versionFile, $json + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Output $fullVersion
