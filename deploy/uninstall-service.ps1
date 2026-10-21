<#
.SYNOPSIS
  Stop and remove the Cobalt B1 Promotion service (and its worker, if installed). Run from an ELEVATED PowerShell.
  Published files, appsettings.json, companies.json and data\ are left in place.
#>
param([string]$Name = "CobaltB1Promotion")
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

foreach ($n in @("$($Name)Worker", $Name)) {
  $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
  if (-not $svc) { Write-Host "Service '$n' is not installed." -ForegroundColor Yellow; continue }
  if ($svc.Status -ne 'Stopped') { Stop-Service -Name $n -Force }
  sc.exe delete $n | Out-Null
  Write-Host "Removed service '$n'." -ForegroundColor Green
}
