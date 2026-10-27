param(
    [Parameter(Mandatory = $true)]
    [string]$BundlePath,

    [string]$Configuration = "Release",

    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Version) {
    $main = Get-Content (Join-Path $root "Main.cs") -Raw
    if ($main -match 'MelonInfo\([^)]*"TrophyAPS"\s*,\s*"([^"]+)"') {
        $Version = $Matches[1]
    } else {
        throw "Could not read the version from Main.cs. Pass -Version explicitly."
    }
}

$dll = Join-Path $root "bin\$Configuration\TrophyAPS.dll"
if (-not (Test-Path $dll)) {
    throw "Missing $dll. Build the solution in $Configuration configuration first."
}
if (-not (Test-Path $BundlePath)) {
    throw "Missing model bundle at $BundlePath."
}

$stage = Join-Path $root "dist\stage"
$dest  = Join-Path $root "dist\TrophyAPS-v$Version.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $dest)  { Remove-Item $dest -Force }

New-Item -ItemType Directory -Force -Path (Join-Path $stage "TrophyAPS") | Out-Null
Copy-Item $dll (Join-Path $stage "TrophyAPS.dll")
Copy-Item $BundlePath (Join-Path $stage "TrophyAPS\trophy")

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage,
    $dest,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Remove-Item $stage -Recurse -Force

Add-Type -AssemblyName System.IO.Compression
$zip = [System.IO.Compression.ZipFile]::OpenRead($dest)
Write-Host ""
Write-Host "TrophyAPS v$Version" -ForegroundColor Green
foreach ($e in $zip.Entries) {
    Write-Host ("  {0,-24} {1,8} bytes" -f $e.FullName, $e.Length)
}
$zip.Dispose()
Write-Host ""
Write-Host "Wrote $dest"
Write-Host ""
Write-Host "Players extract the archive into Bin\Mods\ so that the result is:"
Write-Host "  Bin\Mods\TrophyAPS.dll"
Write-Host "  Bin\Mods\TrophyAPS\trophy"
