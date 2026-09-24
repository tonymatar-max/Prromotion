# Builds the B1 add-on and its registration file into dist\addon\:
#   NexusPromotionsAddOn.exe   the add-on, its installer and its uninstaller (dependencies embedded)
#   NexusPromotionsAddOn.ard   registration file matching THAT exe (register this one in B1)
# Run it after every change to the add-on: a rebuilt exe no longer matches an older .ard, and B1 rejects the mismatch.
param([string]$Version = "1.0")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
dotnet build "$root\src\Nexus.Promotions.AddOn" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "add-on build failed" }

$dist = "$root\dist\addon"
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\src\Nexus.Promotions.AddOn\bin\Release\net48\NexusPromotionsAddOn.exe" $dist -Force
& "$PSScriptRoot\New-AddOnArd.ps1" -Exe "$dist\NexusPromotionsAddOn.exe" -Out "$dist\NexusPromotionsAddOn.ard" -Version $Version
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
