using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Nexus.Promotions.Api;

namespace Nexus.Promotions.Api.Tests;

/// <summary>Sign-in for the admin app: the admin API needs it, the machine endpoints (API key) do not.</summary>
public class AdminAuthTests : IDisposable
{
    readonly string _data = Path.Combine(Path.GetTempPath(), $"ape-auth-{Guid.NewGuid():N}");
    readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        try { if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true); } catch (IOException) { /* a key file may still be open */ }
    }

    /// <summary>A server with sign-in on and its own empty data folder. Each client keeps its own cookies.</summary>
    WebApplicationFactory<Program> Server(params (string Key, string Value)[] extra)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Admin:RequireLogin", "true");
            b.UseSetting("Admin:DataDir", _data);
            foreach (var (k, v) in extra) b.UseSetting(k, v);
        });
        _factories.Add(f);
        return f;
    }

    static async Task<HttpResponseMessage> Post(HttpClient c, string url, object body) => await c.PostAsJsonAsync(url, body);

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<HttpClient> SignedInAdmin(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/auth/setup", new { username = "admin", password = "Admin-pass-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> SignedIn(WebApplicationFactory<Program> f, string user, string password)
    {
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/auth/login", new { username = user, password })).StatusCode);
        return c;
    }

    [Fact]
    public async Task The_admin_api_needs_a_sign_in_but_info_and_the_engine_endpoints_do_not()
    {
        var c = Server().CreateClient();
        var status = await Json(await c.GetAsync("/api/auth/status"));
        Assert.True(status.GetProperty("setupRequired").GetBoolean());
        Assert.False(status.GetProperty("authenticated").GetBoolean());

        var denied = await c.GetAsync("/api/v1/admin/promotions");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True((await Json(denied)).GetProperty("signIn").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/admin/promotions/NIVEA15")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/admin/settings/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, "/api/v1/admin/mode-a", new { enabled = false })).StatusCode);

        // The B1 add-on reads this with its API key and no cookie; the engine endpoints are for machines.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/info")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/promotions")).StatusCode);
    }

    [Fact]
    public async Task The_first_administrator_is_created_once_and_signs_in()
    {
        var f = Server();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/auth/setup", new { username = "admin", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/auth/setup", new { username = "", password = "Admin-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/auth/setup", new { username = "admin", password = "Admin-pass-1" })).StatusCode);

        var status = await Json(await c.GetAsync("/api/auth/status"));
        Assert.False(status.GetProperty("setupRequired").GetBoolean());
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("Admin", status.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/promotions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/promotions/NIVEA15")).StatusCode);

        var second = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(second, "/api/auth/setup", new { username = "intruder", password = "Intruder-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/admin/promotions")).StatusCode);

        await Post(c, "/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/admin/promotions")).StatusCode);
        await SignedIn(f, "admin", "Admin-pass-1");
    }

    [Fact]
    public async Task Passwords_are_never_stored_in_clear_and_a_wrong_one_is_refused()
    {
        var f = Server();
        await SignedInAdmin(f);
        var file = await File.ReadAllTextAsync(Path.Combine(_data, "users.json"));
        Assert.DoesNotContain("Admin-pass-1", file);
        Assert.Contains("\"hash\"", file);

        var c = f.CreateClient();
        var bad = await Post(c, "/api/auth/login", new { username = "admin", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains("Invalid user name or password", await bad.Content.ReadAsStringAsync());   // not "no such user": the answer is the same either way
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, "/api/auth/login", new { username = "nobody", password = "wrong-password" })).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_user_name_for_a_while()
    {
        var f = Server();
        await SignedInAdmin(f);
        var c = f.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, "/api/auth/login", new { username = "admin", password = $"wrong-{i}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(c, "/api/auth/login", new { username = "admin", password = "Admin-pass-1" })).StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_look_and_simulate_but_not_change_anything()
    {
        var f = Server();
        var admin = await SignedInAdmin(f);
        Assert.Equal(HttpStatusCode.OK, (await Post(admin, "/api/auth/users", new { username = "viewer", password = "Viewer-pass-1", role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, "/api/auth/users", new { username = "x1", password = "Viewer-pass-1", role = "Root" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, "/api/auth/users", new { username = "VIEWER", password = "Viewer-pass-1", role = "Viewer" })).StatusCode);   // names are not case-sensitive

        var v = await SignedIn(f, "viewer", "Viewer-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/v1/admin/promotions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/v1/admin/promotions/NIVEA15")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await v.PutAsJsonAsync("/api/v1/admin/promotions/NIVEA15", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(v, "/api/v1/admin/promotions", new { code = "NEW1", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(v, "/api/v1/admin/promotions/NIVEA15/status", new { status = "C" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(v, "/api/v1/admin/mode-a", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await v.PutAsJsonAsync("/api/v1/admin/settings/companies", Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await v.GetAsync("/api/auth/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(v, "/api/auth/users", new { username = "sneaky", password = "Sneaky-pass-1", role = "Admin" })).StatusCode);

        // Read-only POSTs are allowed to a viewer: the checker gets as far as validating the promotion (not a 403).
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Post(v, "/api/v1/admin/promotions/check", new { code = "NEW1", name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Users_can_change_their_own_password_and_admins_cannot_remove_themselves_or_the_last_admin()
    {
        var f = Server();
        var admin = await SignedInAdmin(f);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, "/api/auth/password", new { current = "not-my-password", password = "Brand-new-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, "/api/auth/password", new { current = "Admin-pass-1", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, "/api/auth/password", new { current = "Admin-pass-1", password = "Brand-new-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(f.CreateClient(), "/api/auth/login", new { username = "admin", password = "Admin-pass-1" })).StatusCode);
        await SignedIn(f, "admin", "Brand-new-pass-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(f.CreateClient(), "/api/auth/password", new { current = "x", password = "Whatever-pass-1" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/auth/users/admin")).StatusCode);   // yourself, and the last administrator
        await Post(admin, "/api/auth/users", new { username = "second", password = "Second-pass-1", role = "Admin" });
        var second = await SignedIn(f, "second", "Second-pass-1");
        Assert.Equal(HttpStatusCode.NoContent, (await second.DeleteAsync("/api/auth/users/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.DeleteAsync("/api/auth/users/second")).StatusCode);   // now the last one
        Assert.Equal(HttpStatusCode.BadRequest, (await second.DeleteAsync("/api/auth/users/ghost")).StatusCode);   // no such user
    }

    [Fact]
    public async Task Sign_in_and_api_keys_work_side_by_side()
    {
        var f = Server(("Promotions:ApiKeys:0", "machine-key"));
        // No key needed to reach the sign-in itself, and a signed-in browser needs no key for the admin API.
        var c = await SignedInAdmin(f);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/promotions")).StatusCode);

        // Machines still need the key, and the key does not open the admin API.
        var machine = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/api/v1/promotions")).StatusCode);
        machine.DefaultRequestHeaders.Add("X-Api-Key", "machine-key");
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/v1/promotions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/v1/admin/info")).StatusCode);          // what the add-on reads
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/api/v1/admin/promotions")).StatusCode);

        // A signed-in browser does not skip the key on the engine endpoints.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/promotions")).StatusCode);
    }
}
