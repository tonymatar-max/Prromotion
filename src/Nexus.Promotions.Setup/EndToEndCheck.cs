using Nexus.Promotions.B1;
using System.Text.Json.Nodes;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.Setup;

/// <summary>
/// Proves the install on real B1 documents through the Service Layer:
///   1. an order written as the Mode A add-on will write it is accepted, and SQL rebuilds the same hash;
///   2. changing its discount afterwards is rejected (71004);
///   3. an order saved without promotions is queued for Mode B;
///   4. delivering that order before the worker ran is blocked (71001);
///   5. the Mode B processor, reading promotions from the @APE_PROMO UDO, applies them to that order and B1 accepts it.
/// The test orders are cancelled at the end.
/// </summary>
public sealed class EndToEndCheck(ServiceLayer sl, SqlInstaller sql, string sqlConnectionString, Action<string> log)
{
    const string Customer = "C20000";
    const string ItemA = "A00001";
    const string ItemB = "A00002";

    static readonly Promotion Ten = new()
    {
        Code = "APE-DEMO-10", Name = "APE demo: 10% off " + ItemA, Type = PromotionType.ItemDiscount,
        Scope = new ItemScope { ItemCodes = [ItemA] }, RewardValue = 10,
    };
    static readonly Promotion B2G1 = new()
    {
        Code = "APE-DEMO-B2G1", Name = "APE demo: buy 2 get 1 " + ItemA, Type = PromotionType.BuyXGetXFree,
        Scope = new ItemScope { ItemCodes = [ItemA] }, BuyQuantity = 2, GetQuantity = 1,
    };

    public async Task<bool> RunAsync()
    {
        var ok = true;
        var cancel = new List<int>();
        try
        {
            await EnsurePromotionAsync(Ten, "ItemDiscount", "P", 10, 0, 0);
            await EnsurePromotionAsync(B2G1, "BuyXGetXFree", "P", 0, 2, 1);

            // 1. Mode A order
            var result = new PromotionEngine().Evaluate(new Basket
            {
                CardCode = Customer,
                Lines =
                [
                    new BasketLine { LineNum = 0, ItemCode = ItemA, Quantity = 3, UnitPrice = 100.00m },
                    new BasketLine { LineNum = 1, ItemCode = ItemB, Quantity = 1, UnitPrice = 50.00m },
                ],
            }, [Ten, B2G1]);

            var order = await sl.PostAsync("Orders", OrderBody(result, withPromotions: true));
            var docEntry = order!["DocEntry"]!.GetValue<int>();
            cancel.Add(docEntry);
            var sqlHash = SqlHash("17", docEntry);
            ok &= Check("Mode A order accepted", true, $"DocNum {order["DocNum"]}, {result.Lines.Count} lines, discount {result.DiscountTotal:0.00}");
            ok &= Check("SQL hash equals engine hash", sqlHash == result.Hash, sqlHash);

            // 2. Tampering
            var tampered = await ExpectErrorAsync(() => sl.PatchAsync($"Orders({docEntry})",
                new { DocumentLines = new[] { new { LineNum = 0, DiscountPercent = 30.0 } } }));
            ok &= Check("Changed discount rejected (71004)", tampered?.Contains("71004") == true || tampered?.Contains("does not match") == true, tampered);

            // 3. Mode B queueing
            var plain = await sl.PostAsync("Orders", OrderBody(result, withPromotions: false));
            var plainEntry = plain!["DocEntry"]!.GetValue<int>();
            cancel.Add(plainEntry);
            var queued = QueueStatus(plainEntry);
            ok &= Check("Order without promotions queued for Mode B", queued == "New", $"queue status {queued ?? "none"}");

            // 4. Delivery before the worker ran
            var blocked = await ExpectErrorAsync(() => sl.PostAsync("DeliveryNotes", new
            {
                CardCode = Customer,
                DocumentLines = new[] { new { BaseType = 17, BaseEntry = plainEntry, BaseLine = 0 } },
            }));
            ok &= Check("Delivery from queued order blocked (71001)", blocked?.Contains("still being applied") == true, blocked);

            // 5. Mode B, as the worker runs it
            var store = new PromotionStore(new ServiceLayerPromotionSource(sl, new B1MasterData(sl, 1)), new EngineOptions(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PromotionStore>.Instance);
            await store.ReloadAsync();
            var processor = new ModeBProcessor(sl, sqlConnectionString,
                new LocalDocumentEvaluator(store, new B1MasterData(sl, 1)), m => log("          " + m));
            await processor.DrainAsync();

            var after = await sl.GetAsync($"Orders({plainEntry})");
            var lines = after!["DocumentLines"]!.AsArray();
            var freeLines = lines.Count(l => l!["U_APE_Free"]?.GetValue<string>() == "Y");
            ok &= Check("Mode B applied promotions", after["U_APE_Status"]?.GetValue<string>() == "Applied" && freeLines == 1,
                $"status {after["U_APE_Status"]}, {lines.Count} lines, {freeLines} free, loaded {store.Current.Promotions.Count} promotion(s) from @APE_PROMO");
            var storedHash = after["U_APE_Hash"]?.GetValue<string>();
            ok &= Check("Mode B hash valid in SQL", storedHash == SqlHash("17", plainEntry), storedHash);
            ok &= Check("Queue row done", QueueStatus(plainEntry) == "Done", QueueStatus(plainEntry));
            MarkQueueDone(plainEntry); // only matters if Mode B failed above
        }
        catch (Exception ex)
        {
            log($"  FAIL    {ex.Message}");
            ok = false;
        }
        finally
        {
            foreach (var d in cancel)
            {
                try { await sl.PostAsync($"Orders({d})/Cancel", new { }); log($"  cleanup order {d} cancelled"); }
                catch (Exception ex) { log($"  cleanup order {d} not cancelled: {ex.Message}"); }
            }
        }
        return ok;
    }

    static object OrderBody(EvaluationResult r, bool withPromotions) => new Dictionary<string, object?>
    {
        ["CardCode"] = Customer,
        ["DocDueDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
        ["Comments"] = "APE setup end-to-end check",
        ["U_APE_Hash"] = withPromotions ? r.Hash : null,
        ["U_APE_Mode"] = withPromotions ? "A" : null,
        ["U_APE_Status"] = withPromotions ? "Applied" : null,
        ["DocumentLines"] = (withPromotions ? r.Lines : r.Lines.Where(l => !l.IsFree)).Select(l => new Dictionary<string, object?>
        {
            ["ItemCode"] = l.ItemCode,
            ["Quantity"] = withPromotions ? l.Quantity : l.Quantity + r.Lines.Where(f => f.IsFree && f.SourceLineNum == l.SourceLineNum).Sum(f => f.Quantity),
            ["UnitPrice"] = l.UnitPrice,
            ["DiscountPercent"] = withPromotions ? l.DiscountPercent : 0m,
            ["U_APE_Promo"] = withPromotions && l.PromotionCodes.Length > 0 ? l.PromotionCodes : null,
            ["U_APE_DiscAmt"] = withPromotions ? l.DiscountAmount : null,
            ["U_APE_Free"] = l.IsFree ? "Y" : "N",
            ["U_APE_Group"] = withPromotions ? l.Group : null,
        }).ToArray(),
    };

    async Task EnsurePromotionAsync(Promotion p, string type, string rewardKind, decimal value, decimal buy, decimal get)
    {
        if (await sl.GetAsync($"APE_PROMO('{p.Code}')") is not null) return;
        await sl.PostAsync("APE_PROMO", new Dictionary<string, object>
        {
            ["Code"] = p.Code,
            ["Name"] = p.Name,
            ["U_Type"] = type,
            ["U_Status"] = "A",
            ["U_RewardKind"] = rewardKind,
            ["U_RewardValue"] = value,
            ["U_BuyQty"] = buy,
            ["U_GetQty"] = get,
            ["APE_PROMO_SCPCollection"] = new[] { new { U_Role = "T", U_ScopeType = "I", U_Value = ItemA } },
        });
        log($"  promo   {p.Code} created in @APE_PROMO");
    }

    async Task<string?> ExpectErrorAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (ServiceLayerException ex) { return ex.Message; }
    }

    bool Check(string what, bool passed, string? detail)
    {
        log($"  {(passed ? "PASS" : "FAIL")}    {what}{(detail is null ? "" : $" — {detail}")}");
        return passed;
    }

    string SqlHash(string objType, int docEntry)
    {
        using var cn = sql.Open();
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "DECLARE @h CHAR(64); EXEC dbo.APE_ComputeDocHash @o, @e, @h OUTPUT; SELECT @h;", cn);
        cmd.Parameters.AddWithValue("@o", objType);
        cmd.Parameters.AddWithValue("@e", docEntry);
        return (string)cmd.ExecuteScalar();
    }

    string? QueueStatus(int docEntry)
    {
        using var cn = sql.Open();
        return SqlInstaller.Scalar(cn, $"SELECT TOP (1) Status FROM dbo.APE_Queue WHERE ObjType = N'17' AND DocEntry = {docEntry} ORDER BY Id DESC") as string;
    }

    void MarkQueueDone(int docEntry)
    {
        using var cn = sql.Open();
        SqlInstaller.Exec(cn, $"UPDATE dbo.APE_Queue SET Status = N'Done', LastError = N'setup end-to-end check', ProcessedAt = SYSUTCDATETIME() WHERE ObjType = N'17' AND DocEntry = {docEntry} AND Status = N'New'");
    }
}
