<#
.SYNOPSIS
  Publish the Cobalt B1 Promotion API (with its admin app) and the Mode B worker to a folder ready to run as Windows services.

.DESCRIPTION
  The admin app is plain JS served from wwwroot, so there is no UI build. Publishing over an existing folder is the
  upgrade path: your appsettings.json, companies.json and data\ are never overwritten (they are skipped if present),
  then restart the service.

.PARAMETER Dest
  Where to publish. Default: C:\CobaltB1Promotion  (API in the root, worker in .\worker)

.EXAMPLE
  ./deploy/publish.ps1
#>
param([string]$Dest = "C:\CobaltB1Promotion")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Keep the customer's own settings: publish to a staging folder, then copy over everything except these.
$keep = @("appsettings.json", "companies.json", "appsettings.Production.json")
$stage = Join-Path ([IO.Path]::GetTempPath()) ("cobalt-promo-" + [guid]::NewGuid().ToString("N"))

foreach ($p in @(@{ Proj = "src\Nexus.Promotions.Api"; Sub = "" }, @{ Proj = "src\Nexus.Promotions.Worker"; Sub = "worker" })) {
  $out = Join-Path $stage $p.Sub
  Write-Host "Publishing $($p.Proj) ..." -ForegroundColor Cyan
  cmd /c "dotnet publish `"$(Join-Path $root $p.Proj)`" -c Release -r win-x64 --self-contained false -o `"$out`" 2>&1"
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.Proj)" }
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$files = Get-ChildItem $stage -Recurse -File
foreach ($f in $files) {
  $rel = $f.FullName.Substring($stage.Length).TrimStart('\')
  $target = Join-Path $Dest $rel
  if ((Test-Path $target) -and ($keep -contains $f.Name)) { continue }   # never overwrite the customer's settings
  New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
  Copy-Item $f.FullName $target -Force
}
Remove-Item $stage -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "data") | Out-Null

Write-Host "`nPublished to $Dest" -ForegroundColor Green
if (Get-Service -Name CobaltB1Promotion -ErrorAction SilentlyContinue) {
  Write-Host "Upgrade: Restart-Service CobaltB1Promotion  (and the worker service, if installed)" -ForegroundColor Yellow
} else {
  Write-Host "Next: run deploy\install-service.ps1 from an elevated PowerShell." -ForegroundColor Yellow
}
