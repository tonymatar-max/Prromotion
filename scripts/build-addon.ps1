# Builds the B1 add-on and its registration file into dist\addon\v<Version>\ :
#   NexusPromotionsAddOn.exe   the add-on, its installer and its uninstaller (dependencies embedded)
#   NexusPromotionsAddOn.ard   registration file matching THAT exe (register this one in B1)
#
# Run it after every change to the add-on: a rebuilt exe no longer matches an older .ard, and B1 rejects the mismatch.
# Every version gets its own folder, so an add-on that is still running from an earlier version (Windows locks a
# running exe) never blocks a build, and B1's upgrade path (a higher version in the .ard) matches what is on disk.
param([string]$Version = "1.1")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
dotnet build "$root\src\Nexus.Promotions.AddOn" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "add-on build failed" }

$dist = "$root\dist\addon\v$Version"
New-Item -ItemType Directory -Force $dist | Out-Null
try {
    Copy-Item "$root\src\Nexus.Promotions.AddOn\bin\Release\net48\NexusPromotionsAddOn.exe" $dist -Force
}
catch {
    throw "Cannot write $dist\NexusPromotionsAddOn.exe: it is running. Stop that add-on, or build a new -Version."
}
& "$PSScriptRoot\New-AddOnArd.ps1" -Exe "$dist\NexusPromotionsAddOn.exe" -Out "$dist\NexusPromotionsAddOn.ard" -Version $Version
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
