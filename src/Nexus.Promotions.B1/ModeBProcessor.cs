using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

public interface IDocumentEvaluator
{
    Task<DocumentEvaluation> EvaluateAsync(DocumentInput document, CancellationToken ct);
}

/// <summary>In-process evaluation with the worker's own rule cache.</summary>
public sealed class LocalDocumentEvaluator(PromotionStore store, B1MasterData masterData) : IDocumentEvaluator
{
    readonly DocumentEvaluator _inner = new(() => (store.Current.Promotions, store.Current.Engine), masterData);
    public Task<DocumentEvaluation> EvaluateAsync(DocumentInput document, CancellationToken ct) => _inner.EvaluateAsync(document, ct);
}

/// <summary>Evaluation through the promotion API (one rule cache for all channels, PRD 6).</summary>
public sealed class HttpDocumentEvaluator(HttpClient http) : IDocumentEvaluator
{
    public static readonly JsonSerializerOptions Json = CreateJson();

    public async Task<DocumentEvaluation> EvaluateAsync(DocumentInput document, CancellationToken ct)
    {
        var res = await http.PostAsJsonAsync("api/v1/documents/evaluate", document, Json, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Promotion API: HTTP {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
        return (await res.Content.ReadFromJsonAsync<DocumentEvaluation>(Json, ct))!;
    }

    static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return o;
    }
}

public sealed record QueueItem(int Id, string ObjType, int DocEntry, int Attempts);

/// <summary>
/// Mode B (PRD 6.5, FR-40–FR-44): takes documents from dbo.APE_Queue, evaluates them and writes the result back
/// with one Service Layer PATCH (lines and hash together, so the validation procedure accepts it).
/// </summary>
public sealed class ModeBProcessor(ServiceLayer sl, string sqlConnectionString, IDocumentEvaluator evaluator, Action<string> log)
{
    public const int MaxAttempts = 3;
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    int? _amountDecimals;

    /// <summary>
    /// Processes queued documents until none is left. Returns how many were handled. A document is handled at most
    /// once per drain: a failed attempt goes back to the queue and is retried on the next poll, not in a tight loop.
    /// </summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var handled = 0;
        var seen = new HashSet<(string, int)>();
        while (!ct.IsCancellationRequested && Claim() is { } item)
        {
            if (!seen.Add((item.ObjType, item.DocEntry)))
            {
                Release(item);
                break;
            }
            await ProcessAsync(item, ct);
            handled++;
        }
        return handled;
    }

    void Release(QueueItem item)
    {
        using var cn = new SqlConnection(sqlConnectionString);
        cn.Open();
        Exec(cn, $"UPDATE dbo.APE_Queue SET Status = N'New', Attempts = Attempts - 1 WHERE Id = {item.Id}");
    }

    QueueItem? Claim()
    {
        using var cn = new SqlConnection(sqlConnectionString);
        cn.Open();
        // A worker that died mid-document leaves it Processing; hand it out again after a while.
        Exec(cn, $"UPDATE dbo.APE_Queue SET Status = N'New' WHERE Status = N'Processing' AND ProcessedAt < DATEADD(MINUTE, -{StaleAfter.TotalMinutes}, SYSUTCDATETIME())");
        using var cmd = new SqlCommand("""
            WITH next AS (
                SELECT TOP (1) * FROM dbo.APE_Queue WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE Status = N'New' ORDER BY Id)
            UPDATE next SET Status = N'Processing', Attempts = Attempts + 1, ProcessedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.ObjType, inserted.DocEntry, inserted.Attempts;
            """, cn);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new QueueItem(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)) : null;
    }

    public async Task ProcessAsync(QueueItem item, CancellationToken ct)
    {
        var entity = item.ObjType == "23" ? "Quotations" : "Orders";
        var path = $"{entity}({item.DocEntry})";
        try
        {
            var doc = await sl.GetAsync(path, ct);
            if (doc is null) { Finish(item, "Done", "document not found"); return; }
            if (Str(doc, "Cancelled") == "tYES" || Str(doc, "DocumentStatus") == "bost_Close") { Finish(item, "Done", "document closed"); return; }
            if (!string.IsNullOrEmpty(Str(doc, "U_APE_Hash"))) { Finish(item, "Done", "already evaluated"); return; }

            var lines = doc["DocumentLines"]!.AsArray().OfType<JsonNode>().ToList();
            // FR-42: never rewrite lines that were already delivered or copied.
            if (lines.Any(l => Str(l, "LineStatus") == "bost_Close" || Dec(l, "RemainingOpenQuantity") < Dec(l, "Quantity")))
            {
                await sl.PatchAsync(path, new Dictionary<string, object?> { ["U_APE_Status"] = "Skipped" }, ct: ct);
                Finish(item, "Done", "skipped: lines already delivered or copied");
                return;
            }

            var input = new DocumentInput
            {
                DocumentType = item.ObjType == "23" ? "OQUT" : "ORDR",
                CardCode = Str(doc, "CardCode"),
                DocDate = DateTime.TryParse(Str(doc, "DocDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                Coupons = (Str(doc, "U_APE_Coupons") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                AmountDecimals = AmountDecimals(),
                Lines = lines.Select(l => new DocumentLineInput
                {
                    LineNum = (int)Dec(l, "LineNum"),
                    ItemCode = Str(l, "ItemCode") ?? "",
                    Quantity = Dec(l, "Quantity"),
                    UnitPrice = Dec(l, "UnitPrice"),
                    DiscountPercent = Dec(l, "DiscountPercent"),
                    PromotionCodes = Str(l, "U_APE_Promo"),
                    IsFree = Str(l, "U_APE_Free") == "Y",
                    Group = Str(l, "U_APE_Group"),
                }).Where(l => l.ItemCode.Length > 0).ToList(),
            };

            var evaluation = await evaluator.EvaluateAsync(input, ct);
            await sl.PatchAsync(path, PatchBody(evaluation, lines), replaceCollections: true, ct);
            Finish(item, "Done", null);
            log($"Mode B: {entity} {Dec(doc, "DocNum"):0} — {evaluation.Result.Promotions.Count} promotion(s), discount {evaluation.Result.DiscountTotal}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var final = item.Attempts >= MaxAttempts;
            Finish(item, final ? "Failed" : "New", ex.Message);
            log($"Mode B: {entity} {item.DocEntry} attempt {item.Attempts} failed{(final ? " (giving up)" : "")}: {ex.Message}");
            if (final)
            {
                try { await sl.PatchAsync(path, new Dictionary<string, object?> { ["U_APE_Status"] = "Failed" }, ct: ct); }
                catch { /* the queue row already says Failed */ }
            }
        }
    }

    /// <summary>The complete new line set (ReplaceCollectionsOnPatch) plus the hash, in one PATCH.</summary>
    static Dictionary<string, object?> PatchBody(DocumentEvaluation e, List<JsonNode> docLines)
    {
        var byLineNum = docLines.ToDictionary(l => (int)Dec(l, "LineNum"));
        // Existing lines first in LineNum order, new lines last: with ReplaceCollectionsOnPatch the Service Layer
        // numbers new lines by position, so a new line between existing ones collides with a LineNum (-2035).
        // The hash does not depend on line order, so this costs nothing.
        var ordered = e.Lines.OrderBy(p => p.LineNum is null).ThenBy(p => p.LineNum ?? 0);
        var lines = ordered.Select(p =>
        {
            // ReplaceCollectionsOnPatch wants ItemCode on every line, existing ones included.
            var line = new Dictionary<string, object?>
            {
                ["ItemCode"] = p.Line.ItemCode,
                ["Quantity"] = p.Line.Quantity,
                ["DiscountPercent"] = p.DiscountPercent,
                ["U_APE_Promo"] = p.Line.PromotionCodes.Length > 0 ? p.Line.PromotionCodes : "",
                ["U_APE_DiscAmt"] = p.Line.DiscountAmount,
                ["U_APE_Free"] = p.Line.IsFree ? "Y" : "N",
                ["U_APE_Group"] = p.Line.Group ?? "",
            };
            line["UnitPrice"] = p.Line.UnitPrice;
            if (p.LineNum is { } n)
                line["LineNum"] = n;
            else
            {
                // A split-off free line keeps its source line's warehouse and tax.
                if (p.Line.SourceLineNum is { } src && byLineNum.TryGetValue(src, out var source))
                {
                    line["WarehouseCode"] = Str(source, "WarehouseCode");
                    if (Str(source, "VatGroup") is { } vat) line["VatGroup"] = vat;
                }
            }
            return line;
        }).ToArray();

        return new Dictionary<string, object?>
        {
            ["U_APE_Hash"] = e.Result.Hash,
            ["U_APE_Mode"] = "B",
            ["U_APE_Status"] = "Applied",
            ["U_APE_EvalAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["DocumentLines"] = lines,
        };
    }

    void Finish(QueueItem item, string status, string? message)
    {
        using var cn = new SqlConnection(sqlConnectionString);
        cn.Open();
        using var cmd = new SqlCommand(
            "UPDATE dbo.APE_Queue SET Status = @s, LastError = @m, ProcessedAt = SYSUTCDATETIME() WHERE Id = @id", cn);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.ExecuteNonQuery();
    }

    int AmountDecimals()
    {
        if (_amountDecimals is { } d) return d;
        using var cn = new SqlConnection(sqlConnectionString);
        cn.Open();
        using var cmd = new SqlCommand("SELECT SumDec FROM dbo.OADM", cn);
        return (_amountDecimals = Convert.ToInt32(cmd.ExecuteScalar() ?? 2)).Value;
    }

    static void Exec(SqlConnection cn, string sql)
    {
        using var cmd = new SqlCommand(sql, cn);
        cmd.ExecuteNonQuery();
    }

    static string? Str(JsonNode n, string name) =>
        n[name] is { } v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    static decimal Dec(JsonNode n, string name) =>
        n[name] is { } v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<decimal>() : 0m;
}
