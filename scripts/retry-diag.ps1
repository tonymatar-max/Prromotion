# Retries the read-only add-on diagnostic against the running B1 client until it connects (or gives up).
# Used after an add-on process was killed abruptly: B1 can spend a few minutes timing out its dead connection.
param([string]$Exe = "$PSScriptRoot\..\src\Nexus.Promotions.AddOn\bin\Release\net48\NexusPromotionsAddOn.exe", [int]$Attempts = 12)
$dir = Split-Path (Resolve-Path $Exe)
for ($i = 1; $i -le $Attempts; $i++) {
    Remove-Item "$dir\diag.txt" -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $Exe -ArgumentList "--diag" -WorkingDirectory $dir -PassThru
    if ($p.WaitForExit(30000)) { Write-Output "attempt ${i}: connected, exit code $($p.ExitCode), diag.txt: $(Test-Path "$dir\diag.txt")"; exit 0 }
    $p.Kill(); Write-Output "attempt ${i}: still blocked in Connect"
}
exit 1
