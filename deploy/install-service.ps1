<#
.SYNOPSIS
  Install (or reinstall) Cobalt B1 Promotion as a Windows service. Run from an ELEVATED PowerShell.

.PARAMETER Dest     Published folder from publish.ps1. Default C:\CobaltB1Promotion
.PARAMETER Name     Service name. Default CobaltB1Promotion  (display name: "Cobalt B1 Promotion")
.PARAMETER Urls     Where to listen. Default http://0.0.0.0:5190 because the B1 add-ons on every workstation call this server;
                    use http://localhost:5190 if it is only reached through a reverse proxy. Open the firewall port yourself.
.PARAMETER Worker   Also install the Mode B worker as "<Name> Worker".
.PARAMETER ServiceLayerPassword
                    Stored machine-wide in APE_ServiceLayer__Password (the services read it at start). Omit if already set.
.PARAMETER Account / Password   Log-on account (default LocalSystem).

.EXAMPLE
  ./deploy/install-service.ps1 -Worker -ServiceLayerPassword "..."
#>
param(
  [string]$Dest    = "C:\CobaltB1Promotion",
  [string]$Name    = "CobaltB1Promotion",
  [string]$Display = "Cobalt B1 Promotion",
  [string]$Urls    = "http://0.0.0.0:5190",
  [switch]$Worker,
  [string]$ServiceLayerPassword,
  [string]$Account = "LocalSystem",
  [string]$Password
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$apiExe = Join-Path $Dest "Nexus.Promotions.Api.exe"
if (-not (Test-Path $apiExe)) { throw "$apiExe not found. Run deploy\publish.ps1 -Dest '$Dest' first." }

if ($ServiceLayerPassword) {
  [Environment]::SetEnvironmentVariable("APE_ServiceLayer__Password", $ServiceLayerPassword, "Machine")
  Write-Host "Stored the Service Layer password machine-wide (APE_ServiceLayer__Password)." -ForegroundColor DarkGray
}

function Install-One([string]$svcName, [string]$display, [string]$exe, [string]$args, [string]$description) {
  if (-not [System.Diagnostics.EventLog]::SourceExists($display)) { New-EventLog -LogName Application -Source $display }
  $existing = Get-Service -Name $svcName -ErrorAction SilentlyContinue
  if ($existing) {
    Write-Host "Stopping and removing existing service '$svcName'..." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $svcName -Force }
    sc.exe delete $svcName | Out-Null
    Start-Sleep -Seconds 2
  }
  # The name inside the exe (UseWindowsService) is only used for logging; the SCM name is what matters.
  $cmd = "`"$exe`"" + $(if ($args) { " $args" } else { "" })
  $params = @{ Name = $svcName; BinaryPathName = $cmd; DisplayName = $display; Description = $description; StartupType = "Automatic" }
  if ($Account -ne "LocalSystem") {
    if (-not $Password) { throw "Provide -Password for account '$Account' (or use the default LocalSystem)." }
    $sec = ConvertTo-SecureString $Password -AsPlainText -Force
    $params.Credential = New-Object System.Management.Automation.PSCredential($Account, $sec)
  }
  New-Service @params | Out-Null
  sc.exe failure $svcName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
  Start-Service -Name $svcName
}

if ($Account -ne "LocalSystem") { icacls $Dest /grant "${Account}:(OI)(CI)M" | Out-Null }

Install-One $Name $Display $apiExe "--urls $Urls --Service:Name=$Name" "Cobalt B1 Promotion: promotion engine, REST API and admin app ($Urls)."
if ($Worker) {
  $wName = "$Name" + "Worker"
  Install-One $wName "$Display Worker" (Join-Path $Dest "worker\Nexus.Promotions.Worker.exe") "--Worker:ServiceName=$wName" "Cobalt B1 Promotion Mode B worker: applies promotions to saved sales documents."
}

Start-Sleep -Seconds 3
Get-Service -Name "$Name*" | Format-Table -AutoSize
Write-Host "`nInstalled. Admin app: $($Urls -replace '0\.0\.0\.0','localhost')   Health: $($Urls -replace '0\.0\.0\.0','localhost')/health" -ForegroundColor Green
Write-Host "Set Service Layer / company details in $Dest\appsettings.json (or the admin app Settings > Companies), then Restart-Service $Name." -ForegroundColor Green
