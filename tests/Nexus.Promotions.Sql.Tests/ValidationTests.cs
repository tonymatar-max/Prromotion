using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Nexus.Promotions.Engine;
using Xunit.Abstractions;

namespace Nexus.Promotions.Sql.Tests;

/// <summary>The MS SQL scripts against real engine output: hash parity, every validation rule, and Mode B queueing.</summary>
public class ValidationTests(SqlFixture db, ITestOutputHelper output) : IClassFixture<SqlFixture>
{
    static int _nextDocEntry = 1000;

    static readonly Promotion Ten = new()
    {
        Code = "TEN", Type = PromotionType.ItemDiscount, Scope = new ItemScope { ItemCodes = ["A"] }, RewardValue = 10,
    };
    static readonly Promotion B2G1 = new()
    {
        Code = "B2G1", Type = PromotionType.BuyXGetXFree, Scope = new ItemScope { ItemCodes = ["A"] }, BuyQuantity = 2, GetQuantity = 1,
    };

    static EvaluationResult Evaluate(string? hashKey = null, params BasketLine[] lines) =>
        new PromotionEngine(new EngineOptions { HashKey = hashKey })
            .Evaluate(new Basket { Lines = lines, AmountDecimals = 3 }, [Ten, B2G1]);

    static EvaluationResult StandardOrder() => Evaluate(null,
        new BasketLine { LineNum = 0, ItemCode = "A", Quantity = 3, UnitPrice = 10m },
        new BasketLine { LineNum = 1, ItemCode = "Z", Quantity = 1, UnitPrice = 4.5m });

    /// <summary>Writes the result as the add-on will: lines in result order, DiscPrcnt + APE UDFs, hash on the header.</summary>
    int Save(string header, string lineTable, EvaluationResult r, bool withHash = true,
             Func<int, ResultLine, (int BaseType, int? BaseEntry, int? BaseLine)>? baseOf = null)
    {
        var docEntry = Interlocked.Increment(ref _nextDocEntry);
        using var cn = db.Open();
        using (var h = new SqlCommand($"INSERT dbo.{header} (DocEntry, DocNum, UserSign, U_APE_Hash) VALUES (@e, @e, 1, @h)", cn))
        {
            h.Parameters.AddWithValue("@e", docEntry);
            h.Parameters.AddWithValue("@h", withHash ? r.Hash : DBNull.Value);
            h.ExecuteNonQuery();
        }
        for (int i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            var (bt, be, bl) = baseOf?.Invoke(i, l) ?? (-1, null, null);
            using var c = new SqlCommand($"""
                INSERT dbo.{lineTable} (DocEntry, LineNum, VisOrder, ItemCode, Quantity, PriceBefDi, DiscPrcnt,
                                        BaseType, BaseEntry, BaseLine, U_APE_Promo, U_APE_DiscAmt, U_APE_Free, U_APE_Group)
                VALUES (@e, @n, @n, @item, @q, @p, @d, @bt, @be, @bl, @promo, @amt, @free, @grp)
                """, cn);
            c.Parameters.AddWithValue("@e", docEntry);
            c.Parameters.AddWithValue("@n", i);
            c.Parameters.AddWithValue("@item", l.ItemCode);
            c.Parameters.AddWithValue("@q", l.Quantity);
            c.Parameters.AddWithValue("@p", l.UnitPrice);
            c.Parameters.AddWithValue("@d", l.DiscountPercent);
            c.Parameters.AddWithValue("@bt", bt);
            c.Parameters.AddWithValue("@be", (object?)be ?? DBNull.Value);
            c.Parameters.AddWithValue("@bl", (object?)bl ?? DBNull.Value);
            c.Parameters.AddWithValue("@promo", string.IsNullOrEmpty(l.PromotionCodes) ? DBNull.Value : l.PromotionCodes);
            c.Parameters.AddWithValue("@amt", l.DiscountAmount);
            c.Parameters.AddWithValue("@free", l.IsFree ? "Y" : "N");
            c.Parameters.AddWithValue("@grp", (object?)l.Group ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
        return docEntry;
    }

    (int Error, string Message) Validate(string objType, int docEntry, string trans = "A")
    {
        using var cn = db.Open();
        using var cmd = new SqlCommand("dbo.APE_ValidateDocument", cn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@ObjType", objType);
        cmd.Parameters.AddWithValue("@TransType", trans);
        cmd.Parameters.AddWithValue("@DocKey", docEntry.ToString());
        var err = cmd.Parameters.Add("@Error", SqlDbType.Int);
        err.Direction = ParameterDirection.Output;
        var msg = cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 200);
        msg.Direction = ParameterDirection.Output;
        cmd.ExecuteNonQuery();
        return ((int)err.Value, msg.Value as string ?? "");
    }

    string SqlHash(string objType, int docEntry)
    {
        using var cn = db.Open();
        using var cmd = new SqlCommand("dbo.APE_ComputeDocHash", cn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@ObjType", objType);
        cmd.Parameters.AddWithValue("@DocEntry", docEntry);
        var h = cmd.Parameters.Add("@Hash", SqlDbType.Char, 64);
        h.Direction = ParameterDirection.Output;
        cmd.ExecuteNonQuery();
        return (string)h.Value;
    }

    void Sql(string sql)
    {
        using var cn = db.Open();
        SqlFixture.Exec(cn, sql);
    }

    int QueueCount(string objType, int docEntry)
    {
        using var cn = db.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.APE_Queue WHERE ObjType = @o AND DocEntry = @e", cn);
        cmd.Parameters.AddWithValue("@o", objType);
        cmd.Parameters.AddWithValue("@e", docEntry);
        return (int)cmd.ExecuteScalar();
    }

    void Enqueue(string objType, int docEntry, string trans = "A")
    {
        using var cn = db.Open();
        using var cmd = new SqlCommand("dbo.APE_Enqueue", cn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@ObjType", objType);
        cmd.Parameters.AddWithValue("@TransType", trans);
        cmd.Parameters.AddWithValue("@DocKey", docEntry.ToString());
        cmd.ExecuteNonQuery();
    }

    // ── Hash parity ────────────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("s3cr3t-key")]
    public void Sql_hash_matches_engine_hash(string? key)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        Sql($"UPDATE dbo.APE_Settings SET Value = N'{key ?? ""}' WHERE Name = N'HashKey'");
        try
        {
            var r = Evaluate(key,
                new BasketLine { LineNum = 0, ItemCode = "A", Quantity = 3, UnitPrice = 10m },
                new BasketLine { LineNum = 1, ItemCode = "صابون-1", Quantity = 2.5m, UnitPrice = 1.275m },   // Arabic + decimals
                new BasketLine { LineNum = 2, ItemCode = "Z", Quantity = 1, UnitPrice = 0.125m });
            var doc = Save("ORDR", "RDR1", r);
            Assert.Equal(r.Hash, SqlHash("17", doc));
        }
        finally
        {
            Sql("UPDATE dbo.APE_Settings SET Value = N'' WHERE Name = N'HashKey'");
        }
    }

    [SkippableFact]
    public void Hash_does_not_depend_on_line_order()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var r = StandardOrder();
        var reversed = r with { Lines = r.Lines.Reverse().ToList() };   // free line first, as an add-on might write it
        var doc = Save("ORDR", "RDR1", reversed);
        Assert.Equal(r.Hash, SqlHash("17", doc));
        Assert.Equal(0, Validate("17", doc).Error);
    }

    // ── Validation rules ───────────────────────────────────────────────────────────

    [SkippableFact]
    public void Evaluated_order_passes()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder());
        Assert.Equal((0, ""), Validate("17", doc));
    }

    [SkippableFact]
    public void Document_without_promotions_passes()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var r = new PromotionEngine().Evaluate(new Basket { Lines = [new BasketLine { ItemCode = "Z", Quantity = 1, UnitPrice = 5 }] }, []);
        var doc = Save("OINV", "INV1", r, withHash: false);
        Assert.Equal(0, Validate("13", doc).Error);
    }

    [SkippableFact]
    public void Changed_free_quantity_is_rejected_71002()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder());
        Sql($"UPDATE dbo.RDR1 SET Quantity = 2 WHERE DocEntry = {doc} AND U_APE_Free = N'Y'");
        Assert.Equal(71002, Validate("17", doc, "U").Error);
    }

    [SkippableFact]
    public void Changed_price_is_rejected_71002()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder());
        Sql($"UPDATE dbo.RDR1 SET PriceBefDi = 8 WHERE DocEntry = {doc} AND LineNum = 0");
        Assert.Equal(71002, Validate("17", doc, "U").Error);
    }

    [SkippableFact]
    public void Free_line_must_be_100_percent_71003()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder());
        Sql($"UPDATE dbo.RDR1 SET DiscPrcnt = 50 WHERE DocEntry = {doc} AND U_APE_Free = N'Y'");
        Assert.Equal(71003, Validate("17", doc).Error);
    }

    [SkippableFact]
    public void Changed_discount_percent_is_rejected_71004()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder());
        Sql($"UPDATE dbo.RDR1 SET DiscPrcnt = 25 WHERE DocEntry = {doc} AND LineNum = 0");   // UDFs untouched: hash still valid
        var (error, message) = Validate("17", doc);
        Assert.Equal(71004, error);
        Assert.Contains("row 1", message);
    }

    [SkippableFact]
    public void Discount_percent_rounded_to_company_decimals_passes()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        // 3 × 777.77 at 10% then 10% compounding elsewhere gives a 6-decimal percent; B1 stores 2 decimals.
        var odd = Ten with { Code = "TEN", RewardValue = 12.345678m };
        var r = new PromotionEngine().Evaluate(new Basket { Lines = [new BasketLine { ItemCode = "A", Quantity = 13, UnitPrice = 777.77m }] }, [odd]);
        var doc = Save("ORDR", "RDR1", r);
        Sql($"UPDATE dbo.RDR1 SET DiscPrcnt = ROUND(DiscPrcnt, 2) WHERE DocEntry = {doc}");
        Assert.Equal(0, Validate("17", doc).Error);
    }

    [SkippableFact]
    public void Unknown_promotion_code_is_rejected_71005()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var ghost = Ten with { Code = "GHOST" };
        var r = new PromotionEngine().Evaluate(
            new Basket { Lines = [new BasketLine { ItemCode = "A", Quantity = 1, UnitPrice = 10 }] }, [ghost]);
        var doc = Save("OINV", "INV1", r);
        var (error, message) = Validate("13", doc);
        Assert.Equal(71005, error);
        Assert.Contains("GHOST", message);
    }

    [SkippableFact]
    public void Cleared_hash_on_order_is_a_mode_b_request_unless_mode_b_is_off()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Assert.Equal(0, Validate("17", doc, "U").Error);

        Sql("UPDATE dbo.APE_Settings SET Value = N'N' WHERE Name = N'ModeB'");
        try { Assert.Equal(71002, Validate("17", doc, "U").Error); }
        finally { Sql("UPDATE dbo.APE_Settings SET Value = N'Y' WHERE Name = N'ModeB'"); }
    }

    [SkippableFact]
    public void Invoice_with_promotions_but_no_hash_is_rejected()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("OINV", "INV1", StandardOrder(), withHash: false);   // integration skipped /evaluate (FR-45)
        Assert.Equal(71002, Validate("13", doc).Error);
    }

    // ── Copy-to (FR-18) ────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Partial_delivery_copied_from_order_keeps_promotions_71006_when_changed()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var order = StandardOrder();
        var orderDoc = Save("ORDR", "RDR1", order);

        // Deliver 1 of the 2 paid units plus the free unit; B1 copies the header hash too.
        var partial = order with
        {
            Lines = order.Lines.Select(l => l.SourceLineNum == 0 && !l.IsFree ? l with { Quantity = 1 } : l).ToList(),
        };
        var delivery = Save("ODLN", "DLN1", partial, baseOf: (i, _) => (17, orderDoc, i));
        Sql($"UPDATE dbo.ODLN SET U_APE_Hash = N'{order.Hash}' WHERE DocEntry = {delivery}");
        Assert.Equal(0, Validate("15", delivery).Error);

        Sql($"UPDATE dbo.DLN1 SET DiscPrcnt = 12 WHERE DocEntry = {delivery} AND LineNum = 0");
        Assert.Equal(71006, Validate("15", delivery).Error);
    }

    [SkippableFact]
    public void Delivery_from_order_still_in_mode_b_queue_is_blocked_71001()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var orderDoc = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Enqueue("17", orderDoc);
        var delivery = Save("ODLN", "DLN1", StandardOrder(), withHash: false, baseOf: (i, _) => (17, orderDoc, i));

        var (error, message) = Validate("15", delivery);
        Assert.Equal(71001, error);
        Assert.Contains($"sales order {orderDoc}", message);

        Sql($"UPDATE dbo.APE_Queue SET Status = N'Done' WHERE ObjType = N'17' AND DocEntry = {orderDoc}");
        Assert.NotEqual(71001, Validate("15", delivery).Error);
    }

    // ── Mode B queue ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Order_without_hash_is_queued_once()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Enqueue("17", doc);
        Enqueue("17", doc, "U");
        Assert.Equal(1, QueueCount("17", doc));
    }

    [SkippableFact]
    public void Evaluated_closed_or_worker_documents_are_not_queued()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var evaluated = Save("OQUT", "QUT1", StandardOrder());
        Enqueue("23", evaluated);
        Assert.Equal(0, QueueCount("23", evaluated));

        var closed = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Sql($"UPDATE dbo.ORDR SET DocStatus = N'C' WHERE DocEntry = {closed}");
        Enqueue("17", closed, "U");
        Assert.Equal(0, QueueCount("17", closed));

        var byWorker = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Sql($"UPDATE dbo.ORDR SET UserSign2 = 99 WHERE DocEntry = {byWorker}");
        Sql("UPDATE dbo.APE_Settings SET Value = N'99' WHERE Name = N'TechUserId'");
        try
        {
            Enqueue("17", byWorker, "U");
            Assert.Equal(0, QueueCount("17", byWorker));
        }
        finally { Sql("UPDATE dbo.APE_Settings SET Value = NULL WHERE Name = N'TechUserId'"); }
    }

    [SkippableFact]
    public void Document_the_worker_gave_up_on_is_not_queued_again()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var doc = Save("ORDR", "RDR1", StandardOrder(), withHash: false);
        Sql($"UPDATE dbo.ORDR SET U_APE_Status = N'Failed' WHERE DocEntry = {doc}");
        Enqueue("17", doc, "U");
        Assert.Equal(0, QueueCount("17", doc));

        Sql($"UPDATE dbo.ORDR SET U_APE_Status = NULL WHERE DocEntry = {doc}");   // user clears it to retry
        Enqueue("17", doc, "U");
        Assert.Equal(1, QueueCount("17", doc));
    }

    // ── Cost inside TransactionNotification ────────────────────────────────────────

    [SkippableFact]
    public void Validating_a_500_line_order_is_fast()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var lines = Enumerable.Range(0, 500)
            .Select(i => new BasketLine { LineNum = i, ItemCode = i % 2 == 0 ? "A" : $"Z{i}", Quantity = 3, UnitPrice = 1 + i % 40 })
            .ToArray();
        var r = Evaluate(null, lines);
        var doc = Save("ORDR", "RDR1", r);

        Validate("17", doc); // warm-up (plan compilation)
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal(0, Validate("17", doc).Error);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        output.WriteLine($"Validate {r.Lines.Count} lines: median {times[2]:0.0} ms (min {times[0]:0.0}, max {times[4]:0.0})");
        Assert.True(times[2] < 500, $"Median {times[2]:0} ms");
    }
}
