# One saved, active scenario per promotion type in the PRD catalogue (P01-P06, P08/09), created through the
# admin API against SBODemoHO's real items. Safe to re-run: existing codes are left alone (POST returns 409).
$ErrorActionPreference = "Stop"
$base = "http://localhost:5190/api/v1/admin/promotions"

$scenarios = @(
    # P01 — Item discount: 15% off all J.B. Printers (item group 101)
    @{
        code = "APE-SCEN-P01"; name = "P01: 15% off J.B. Printers"; type = "ItemDiscount"; status = "A"
        rewardKind = "P"; rewardValue = 15
        scopes = @(@{ role = "T"; scopeType = "G"; value = "101"; exclude = $false })
    },
    # P02 — Fixed price: Rainbow ColorJet at a promotional unit price
    @{
        code = "APE-SCEN-P02"; name = "P02: Rainbow ColorJet at 350.00"; type = "FixedPrice"; status = "A"
        rewardKind = "F"; rewardValue = 350
        scopes = @(@{ role = "T"; scopeType = "I"; value = "A00004"; exclude = $false })
    },
    # P03 — Quantity tiers: Rainbow Printers (group 102), 3+ units 5%, 6+ units 10%
    @{
        code = "APE-SCEN-P03"; name = "P03: Rainbow Printers volume 5%/10%"; type = "QuantityTier"; status = "A"
        rewardKind = "P"
        scopes = @(@{ role = "T"; scopeType = "G"; value = "102"; exclude = $false })
        tiers  = @(@{ from = 3; value = 5 }, @{ from = 6; value = 10 })
    },
    # P04 — Buy X get X free: buy 2 J.B. Officeprint 1111, get 1 free
    @{
        code = "APE-SCEN-P04"; name = "P04: Buy 2 A00002 get 1 free"; type = "BuyXGetXFree"; status = "A"
        buyQty = 2; getQty = 1; freeMode = "B"; rewardUnits = "C"
        scopes = @(@{ role = "T"; scopeType = "I"; value = "A00002"; exclude = $false })
    },
    # P05 — Buy X get Y: buy a Rainbow ColorJet (A00004), the Rainbow ColorJet 7.5 (A00005) at 50%
    @{
        code = "APE-SCEN-P05"; name = "P05: Buy A00004, A00005 at 50%"; type = "BuyXGetY"; status = "A"
        buyQty = 1; getQty = 1; freeMode = "B"; rewardUnits = "C"; rewardKind = "P"; rewardValue = 50
        scopes = @(
            @{ role = "T"; scopeType = "I"; value = "A00004"; exclude = $false },
            @{ role = "R"; scopeType = "I"; value = "A00005"; exclude = $false }
        )
    },
    # P06 — Mix and match: any 3 from MAKE UP (group 108) for a fixed set price
    @{
        code = "APE-SCEN-P06"; name = "P06: Any 3 Make Up items for 60.00"; type = "MixAndMatch"; status = "A"
        buyQty = 3; rewardUnits = "C"; rewardKind = "F"; rewardValue = 60
        scopes = @(@{ role = "T"; scopeType = "G"; value = "108"; exclude = $false })
    },
    # P08/09 — Spend threshold, tiered: whole basket, spend 500 get 5%, spend 1000 get 10%
    @{
        code = "APE-SCEN-P08"; name = "P08/09: Spend 500 get 5%, 1000 get 10%"; type = "BasketThreshold"; status = "A"
        rewardKind = "P"
        tiers = @(@{ from = 500; value = 5 }, @{ from = 1000; value = 10 })
    }
)

foreach ($s in $scenarios) {
    try {
        $res = Invoke-RestMethod -Uri $base -Method Post -ContentType "application/json" -Body ($s | ConvertTo-Json -Depth 10)
        Write-Host "created  $($res.code)  $($res.name)"
    }
    catch {
        $err = $_.ErrorDetails.Message
        if ($_.Exception.Response.StatusCode -eq 409) { Write-Host "exists   $($s.code)" }
        else { Write-Host "FAILED   $($s.code): $err" }
    }
}
