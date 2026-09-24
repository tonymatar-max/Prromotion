using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nexus.Promotions.B1;

public sealed class ServiceLayerOptions
{
    public string Url { get; set; } = "https://localhost:50000/b1s/v1";
    public string CompanyDb { get; set; } = "";
    public string User { get; set; } = "manager";
    /// <summary>From the environment (APE_ServiceLayer__Password) or a local, uncommitted settings file.</summary>
    public string? Password { get; set; }
    public bool AllowSelfSignedCertificate { get; set; } = true;
    /// <summary>Price list used for reward items the engine adds (P05 AddNew).</summary>
    public int PriceList { get; set; } = 1;
}

public sealed class ServiceLayerException(string message, int code = 0, HttpStatusCode status = 0) : Exception(message)
{
    public int Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Service Layer client for long-running services and tools: one cookie session, logs in again when the
/// session expires (HTTP 401), follows paging. Property names are sent exactly as written, because the
/// Service Layer is case-sensitive ("UserName", not "userName").
/// </summary>
public sealed class ServiceLayer : IDisposable
{
    readonly HttpClient _http;
    readonly ServiceLayerOptions _options;
    readonly SemaphoreSlim _loginLock = new(1, 1);
    bool _loggedIn;

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public ServiceLayer(ServiceLayerOptions options)
    {
        _options = options;
        // SocketsHttpHandler, not HttpClientHandler: accepting a self-signed certificate still makes .NET build a
        // certificate chain to show the validation callback, and HttpClientHandler builds that chain with online
        // revocation checking (OCSP/CRL) left at its default. Against a local/self-signed cert with nowhere to
        // check revocation, that stalls for several seconds on every new TLS connection — SocketsHttpHandler lets
        // this be turned off directly. This, not the login race, was most of the admin app's slow first load.
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            SslOptions = new SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.NoCheck },
        };
        if (options.AllowSelfSignedCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10), // adding UDFs to all marketing documents can take minutes
        };
        _http.DefaultRequestHeaders.Add("Prefer", "odata.maxpagesize=100");
    }

    public string CompanyDb => _options.CompanyDb;

    /// <summary>Always logs in, even if already logged in. Use for an explicit login, or to recover from a 401.</summary>
    public Task LoginAsync(CancellationToken ct = default) => LoginAsync(force: true, ct);

    /// <summary>
    /// Double-checked: several requests can race here (e.g. the admin app's five parallel lookups on first load).
    /// Without the check just inside the lock, each one would log in again in turn — five real B1 logins back to
    /// back instead of one, which is exactly what made the first page load take 8-17 s instead of under 1 s.
    /// </summary>
    async Task LoginAsync(bool force, CancellationToken ct)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            if (!force && _loggedIn) return; // someone else logged in while we were waiting for the lock
            if (string.IsNullOrEmpty(_options.Password))
                throw new ServiceLayerException("Service Layer password is not set (APE_ServiceLayer__Password).");
            var res = await _http.PostAsync("Login", JsonContent.Create(
                new { CompanyDB = _options.CompanyDb, UserName = _options.User, Password = _options.Password }, options: Json), ct);
            if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "Login", ct);
            _loggedIn = true;
        }
        finally { _loginLock.Release(); }
    }

    public async Task LogoutAsync()
    {
        try { await _http.PostAsync("Logout", null); } catch { /* best effort */ }
        _loggedIn = false;
    }

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default)
    {
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "GET " + path, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>All rows of a collection, following odata.nextLink.</summary>
    public async Task<List<JsonNode>> GetAllAsync(string path, CancellationToken ct = default)
    {
        var rows = new List<JsonNode>();
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, ct) ?? throw new ServiceLayerException($"GET {next}: not found");
            foreach (var row in page["value"]?.AsArray() ?? []) if (row is not null) rows.Add(row);
            next = (page["odata.nextLink"] ?? page["@odata.nextLink"])?.GetValue<string>();
            if (next is not null && next.StartsWith('/')) next = next[(next.IndexOf("/b1s/v1/", StringComparison.Ordinal) + 8)..];
        }
        return rows;
    }

    public async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct = default)
    {
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) }, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "POST " + path, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <param name="replaceCollections">
    /// Sends B1S-ReplaceCollectionsOnPatch: the document's lines become exactly the lines sent
    /// (lines with LineNum are updated, lines without are added, lines left out are deleted).
    /// </param>
    public async Task PatchAsync(string path, object body, bool replaceCollections = false, CancellationToken ct = default)
    {
        var res = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body, options: Json) };
            if (replaceCollections) req.Headers.Add("B1S-ReplaceCollectionsOnPatch", "true");
            return req;
        }, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "PATCH " + path, ct);
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        if (!_loggedIn) await LoginAsync(force: false, ct);
        var res = await _http.SendAsync(request(), ct);
        if (res.StatusCode != HttpStatusCode.Unauthorized) return res;

        await LoginAsync(force: true, ct); // session expired (default 30 minutes): log in again and retry
        return await _http.SendAsync(request(), ct);
    }

    static async Task<ServiceLayerException> ErrorAsync(HttpResponseMessage res, string what, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try
        {
            var err = JsonNode.Parse(text)?["error"];
            var code = err?["code"]?.GetValue<int>() ?? 0;
            var msg = err?["message"]?["value"]?.GetValue<string>() ?? text;
            return new ServiceLayerException($"{what}: {msg} (code {code})", code, res.StatusCode);
        }
        catch (JsonException)
        {
            return new ServiceLayerException($"{what}: HTTP {(int)res.StatusCode} {text}", 0, res.StatusCode);
        }
    }

    public void Dispose() => _http.Dispose();
}
