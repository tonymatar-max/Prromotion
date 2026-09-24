# Writes the SAP B1 add-on registration file (.ard) for NexusPromotionsAddOn.exe.
#
# Same file as the "Add-On Registration Data Generator" (AddOnRegDataGen.exe, SAP Business One SDK Tools)
# produces from the values in the README, byte for byte (UTF-16 LE with BOM, no trailing newline). It exists because
# the .ard carries the MD5 and SHA-256 of the exe, and B1 refuses an add-on whose exe no longer matches: after ANY
# rebuild the .ard must be regenerated, and doing that by hand in the GUI is how a stale one gets registered.
#
# Installer, uninstaller and add-on are all the one exe (see InstallerMode.cs): B1 distributes only files the .ard names.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Version = "1.0",
    [string]$PartnerName = "Nexus",
    [string]$Namespace = "Nex",
    [string]$AddOnName = "NexusPromotion",
    [string]$ContactData = "Nexus",
    [int]$InstallSeconds = 30,
    [int]$UninstallSeconds = 30,
    # B1 runs the uninstaller with exactly these arguments; the exe treats "/U" as "uninstall".
    [string]$UninstallArgs = "/U"
)
$ErrorActionPreference = "Stop"
$exeItem = Get-Item -LiteralPath $Exe
$name = $exeItem.Name
$md5 = (Get-FileHash -LiteralPath $Exe -Algorithm MD5).Hash
$sha = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash

function Attr([string]$key, [string]$value) { "$key=`"$([System.Security.SecurityElement]::Escape($value))`"" }

# Attributes in the order the tool writes them (alphabetical).
$attributes = @(
    (Attr addonexe $name), (Attr addongroup "M"), (Attr addonname $AddOnName), (Attr addonsig $md5), (Attr addonsigsha256 $sha),
    (Attr addonver $Version), (Attr clienttype "A"), (Attr contdata $ContactData), (Attr esttime $InstallSeconds),
    (Attr instname $name), (Attr instparams ""), (Attr instsig $md5), (Attr instsigsha256 $sha),
    (Attr partnername $PartnerName), (Attr partnernmsp $Namespace), (Attr platform "X"),
    (Attr silentinst ""), (Attr silentugd ""), (Attr silentuninst ""),
    (Attr ugdcmdargs ""), (Attr ugdesttime ""), (Attr ugdname ""), (Attr ugdsig ""), (Attr ugdsigsha256 ""),
    (Attr uncmdarg $UninstallArgs), (Attr unesttime $UninstallSeconds), (Attr uninstname $name), (Attr uninstsig $md5), (Attr uninstsigsha256 $sha),
    (Attr zipnameinst ""), (Attr zipnameugd ""), (Attr zipnameuninst ""),
    (Attr zipsiginst ""), (Attr zipsiginstsha256 ""), (Attr zipsigugd ""), (Attr zipsigugdsha256 ""), (Attr zipsiguninst ""), (Attr zipsiguninstsha256 "")
) -join " "

$xml = "<?xml version=`"1.0`" encoding=`"UTF-16`"?><AddOnRegData><addon $attributes/></AddOnRegData>"
[System.IO.File]::WriteAllText($Out, $xml, (New-Object System.Text.UnicodeEncoding($false, $true)))
Write-Output "$Out  (v$Version, MD5 $md5)"
