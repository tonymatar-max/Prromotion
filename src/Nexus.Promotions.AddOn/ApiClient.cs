using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// Where each setting comes from, first match wins: NexusPromotionsAddOn.json next to the exe (a deliberate
/// per-workstation override), then dbo.APE_Settings in the company database (central, one place for every
/// workstation, see <see cref="CentralSettings"/>), then the built-in default.
/// </summary>
public sealed class Settings
{
    public const string DefaultApiUrl = "http://localhost:5190";

    /// <summary>Null until set by the local file or the company database.</summary>
    public string? ApiUrl { get; set; }
    public string? ApiKey { get; set; }
    public string ResolvedApiUrl => string.IsNullOrWhiteSpace(ApiUrl) ? DefaultApiUrl : ApiUrl!.Trim();
    /// <summary>FR-38: how long Add waits for the engine before offering to save without promotions.</summary>
    public int TimeoutSeconds { get; set; } = 5;
    /// <summary>Show the summary (Save / Review) on Add and Update when promotions changed the document.</summary>
    public bool ConfirmOnSave { get; set; } = true;

    public static Settings Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NexusPromotionsAddOn.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), ApiClient.Json) ?? new() : new();
    }
}

// The promotion API's /api/v1/documents/evaluate contract (Nexus.Promotions.B1.DocumentInput / DocumentEvaluation).
// Only the fields the add-on uses are declared.

public sealed class DocumentInput
{
    public string DocumentType { get; set; } = "ORDR";
    public string? CardCode { get; set; }
    public DateTime? DocDate { get; set; }
    public string Channel { get; set; } = "B1";
    public string[] Coupons { get; set; } = [];
    public int AmountDecimals { get; set; } = 2;
    public List<DocumentLineInput> Lines { get; set; } = [];
}

public sealed class DocumentLineInput
{
    /// <summary>The add-on uses the matrix row index (0-based) as the key; the API only needs it to be unique.</summary>
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? PromotionCodes { get; set; }
    public bool IsFree { get; set; }
    public string? Group { get; set; }
}

public sealed class DocumentEvaluation
{
    public EvaluationResult Result { get; set; } = new();
    public List<PlannedLine> Lines { get; set; } = [];
    public List<int> RemovedLineNums { get; set; } = [];
    public bool Changed { get; set; }
}

public sealed class PlannedLine
{
    public int? LineNum { get; set; }
    public ResultLine Line { get; set; } = new();
    public decimal DiscountPercent { get; set; }
}

public sealed class EvaluationResult
{
    public decimal DiscountTotal { get; set; }
    public string Hash { get; set; } = "";
    public List<AppliedPromotion> Promotions { get; set; } = [];
    public List<NearMiss> NearMisses { get; set; } = [];
}

public sealed class ResultLine
{
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public bool IsFree { get; set; }
    public string PromotionCodes { get; set; } = "";
    public string? Group { get; set; }
}

public sealed class AppliedPromotion
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal DiscountAmount { get; set; }
}

public sealed class NearMiss
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ApiClient(Settings settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly HttpClient _http = CreateHttp(settings);

    static HttpClient CreateHttp(Settings s)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(s.ResolvedApiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, s.TimeoutSeconds)),
        };
        if (!string.IsNullOrEmpty(s.ApiKey)) http.DefaultRequestHeaders.Add("X-Api-Key", s.ApiKey);
        return http;
    }

    /// <summary>
    /// Checked before auto-applying on Add/Update (not before the manual button, which is always allowed).
    /// One flip of dbo.APE_Settings 'ModeA' in the admin app turns this off on every workstation at once, with
    /// nothing to change here. Fails open (true) so a momentary network blip does not silently stop promotions;
    /// the ordinary "engine unreachable" prompt in PromotionApplier.Apply still covers a real outage.
    /// </summary>
    public bool IsModeAEnabled() =>
        Task.Run(async () =>
        {
            try
            {
                using var res = await _http.GetAsync("api/v1/admin/info").ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return true;
                var info = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                return !info.TryGetProperty("modeAEnabled", out var v) || v.ValueKind != JsonValueKind.False;
            }
            catch (Exception)
            {
                return true;
            }
        }).GetAwaiter().GetResult();

    /// <summary>Blocking call for the UI thread; runs on the thread pool so it cannot deadlock the B1 message loop.</summary>
    public DocumentEvaluation Evaluate(DocumentInput document) =>
        Task.Run(async () =>
        {
            var body = new StringContent(JsonSerializer.Serialize(document, Json), Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync("api/v1/documents/evaluate", body).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Promotion API returned HTTP {(int)res.StatusCode}: {text}");
            return JsonSerializer.Deserialize<DocumentEvaluation>(text, Json)
                   ?? throw new InvalidOperationException("Promotion API returned an empty answer");
        }).GetAwaiter().GetResult();
}
