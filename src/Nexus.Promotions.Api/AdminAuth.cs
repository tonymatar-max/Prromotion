using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Nexus.Promotions.Api;

/// <summary>
/// Sign-in for the admin app (Cobalt service standard, section 4): file-backed users, PBKDF2, a cookie, and a first administrator
/// created from a browser on the server itself. The engine endpoints (/evaluate, /documents/evaluate, the portal endpoint ...)
/// are for machines and keep using X-Api-Key; this only protects the admin API the browser uses.
/// </summary>
public static class Roles
{
    /// <summary>Everything, including users and company settings.</summary>
    public const string Admin = "Admin";
    /// <summary>Can open promotions and run the simulator; cannot change anything.</summary>
    public const string Viewer = "Viewer";
    public static bool IsValid(string? r) => r is Admin or Viewer;
}

public sealed class UserRecord
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; }
}

/// <summary><c>data/users.json</c>. A handful of users; sign-in works before anything else is configured.</summary>
public sealed class UserStore
{
    private const int Iterations = 210_000;
    public const int MinPasswordLength = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<UserRecord> _users;

    // After 5 failures within 5 minutes a username is locked for the rest of that window.
    private readonly Dictionary<string, List<DateTime>> _failures = new(StringComparer.OrdinalIgnoreCase);

    public UserStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "users.json");
        _users = File.Exists(_path) ? JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path), Json) ?? [] : [];
    }

    public bool HasUsers { get { lock (_lock) return _users.Count > 0; } }

    public IReadOnlyList<(string Username, string Role)> List()
    {
        lock (_lock) return _users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Select(u => (u.Username, u.Role)).ToList();
    }

    /// <summary>An error message, or null on success.</summary>
    public string? Create(string username, string password, string role)
    {
        username = (username ?? "").Trim();
        if (username.Length < 2 || username.Length > 64) return "The user name is 2 to 64 characters.";
        if ((password ?? "").Length < MinPasswordLength) return $"The password needs at least {MinPasswordLength} characters.";
        if (!Roles.IsValid(role)) return "The role is Admin or Viewer.";
        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))) return "That user name already exists.";
            _users.Add(Hash(username, password!, role));
            Save();
        }
        return null;
    }

    public string? SetPassword(string username, string password)
    {
        if ((password ?? "").Length < MinPasswordLength) return $"The password needs at least {MinPasswordLength} characters.";
        lock (_lock)
        {
            var i = _users.FindIndex(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "User not found.";
            _users[i] = Hash(_users[i].Username, password!, _users[i].Role);
            Save();
        }
        return null;
    }

    public string? Delete(string username, string actingUser)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return "User not found.";
            if (string.Equals(u.Username, actingUser, StringComparison.OrdinalIgnoreCase)) return "You cannot delete yourself.";
            if (u.Role == Roles.Admin && _users.Count(x => x.Role == Roles.Admin) <= 1) return "You cannot delete the last administrator.";
            _users.Remove(u);
            Save();
        }
        return null;
    }

    /// <summary>The user when the credentials are right. <paramref name="lockedOut"/> is true when too many attempts failed.</summary>
    public UserRecord? Validate(string username, string password, out bool lockedOut)
    {
        lockedOut = false;
        lock (_lock)
        {
            var window = DateTime.UtcNow.AddMinutes(-5);
            if (_failures.TryGetValue(username ?? "", out var f))
            {
                f.RemoveAll(t => t < window);
                if (f.Count >= 5) { lockedOut = true; return null; }
            }
            var u = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            // Always do the hash work, so the time taken does not tell whether the user exists.
            var salt = u is null ? new byte[16] : Convert.FromBase64String(u.Salt);
            var computed = Pbkdf2(password ?? "", salt, u?.Iterations ?? Iterations);
            var ok = u is not null && CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(u.Hash));
            if (ok) { _failures.Remove(username!); return u; }
            if (!_failures.TryGetValue(username ?? "", out f)) _failures[username ?? ""] = f = [];
            f.Add(DateTime.UtcNow);
            return null;
        }
    }

    private static UserRecord Hash(string username, string password, string role)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new UserRecord
        {
            Username = username, Role = role, Iterations = Iterations,
            Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Pbkdf2(password, salt, Iterations)),
        };
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

public sealed record Credentials(string? Username, string? Password);
public sealed record NewUser(string? Username, string? Password, string? Role);
public sealed record NewPassword(string? Password);
public sealed record ChangePassword(string? Current, string? Password);

public static class AdminAuth
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// Guard for the admin API group. Signed in is required; changing anything is for Admin. Two exceptions: GET /info stays open
    /// because the B1 add-on reads "is auto-apply on" from it with its API key, and the read-only POSTs (simulate, check) are open
    /// to Viewers. Errors use the admin app's { errors: [...] } shape.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Gate(bool requireLogin) => async (ctx, next) =>
    {
        if (!requireLogin) return await next(ctx);
        var http = ctx.HttpContext;
        var path = http.Request.Path.Value ?? "";
        var get = HttpMethods.IsGet(http.Request.Method);
        if (get && path.EndsWith("/admin/info", StringComparison.OrdinalIgnoreCase)) return await next(ctx);
        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Json(new { errors = new[] { "Sign in to use the admin app." }, signIn = true }, statusCode: StatusCodes.Status401Unauthorized);
        var readOnly = get || path.EndsWith("/simulate", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/promotions/check", StringComparison.OrdinalIgnoreCase);
        if (!readOnly && http.User.FindFirstValue(ClaimTypes.Role) != Roles.Admin)
            return Results.Json(new { errors = new[] { "Your sign-in can view promotions and use the simulator, not change anything." } }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    };

    public static void MapAuth(this WebApplication app, bool requireLogin)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/status", (HttpContext ctx, UserStore users) => Results.Ok(new
        {
            loginRequired = requireLogin,
            setupRequired = requireLogin && !users.HasUsers,
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            user = ctx.User.Identity?.IsAuthenticated == true
                ? new { username = ctx.User.Identity!.Name, role = ctx.User.FindFirstValue(ClaimTypes.Role) }
                : null,
        }));

        // First run only: the first administrator, and only from a browser on the server itself.
        auth.MapPost("/setup", async (Credentials body, HttpContext ctx, UserStore users) =>
        {
            if (users.HasUsers) return Results.BadRequest(new { errors = new[] { "Setup is already complete." } });
            if (!IsLoopback(ctx)) return Results.BadRequest(new { errors = new[] { "Create the first administrator from a browser on the server itself (http://localhost:<port>)." } });
            var err = users.Create(body.Username ?? "", body.Password ?? "", Roles.Admin);
            if (err is not null) return Results.BadRequest(new { errors = new[] { err } });
            await SignIn(ctx, body.Username!.Trim(), Roles.Admin);
            return Results.Ok(new { username = body.Username!.Trim(), role = Roles.Admin });
        });

        auth.MapPost("/login", async (Credentials body, HttpContext ctx, UserStore users) =>
        {
            var u = users.Validate(body.Username ?? "", body.Password ?? "", out var lockedOut);
            if (lockedOut) return Results.Json(new { errors = new[] { "Too many failed attempts. Try again in a few minutes." } }, statusCode: StatusCodes.Status429TooManyRequests);
            if (u is null) return Results.Json(new { errors = new[] { "Invalid user name or password." } }, statusCode: StatusCodes.Status401Unauthorized);
            await SignIn(ctx, u.Username, u.Role);
            return Results.Ok(new { username = u.Username, role = u.Role });
        });

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(Scheme);
            return Results.NoContent();
        });

        // Anyone signed in may change their own password (the current one is required).
        auth.MapPost("/password", (ChangePassword body, HttpContext ctx, UserStore users) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Json(new { errors = new[] { "Sign in first." } }, statusCode: StatusCodes.Status401Unauthorized);
            var me = ctx.User.Identity!.Name!;
            if (users.Validate(me, body.Current ?? "", out var locked) is null)
                return Results.BadRequest(new { errors = new[] { locked ? "Too many failed attempts. Try again in a few minutes." : "The current password is not right." } });
            var err = users.SetPassword(me, body.Password ?? "");
            return err is null ? Results.NoContent() : Results.BadRequest(new { errors = new[] { err } });
        });

        var usersApi = app.MapGroup("/api/auth/users").RequireAuthorization("Admin");
        usersApi.MapGet("/", (UserStore users) => users.List().Select(u => new { username = u.Username, role = u.Role }));
        usersApi.MapPost("/", (NewUser body, UserStore users) =>
        {
            var err = users.Create(body.Username ?? "", body.Password ?? "", body.Role ?? "");
            return err is null ? Results.Ok(new { username = body.Username!.Trim(), role = body.Role }) : Results.BadRequest(new { errors = new[] { err } });
        });
        usersApi.MapDelete("/{username}", (string username, HttpContext ctx, UserStore users) =>
        {
            var err = users.Delete(username, ctx.User.Identity?.Name ?? "");
            return err is null ? Results.NoContent() : Results.BadRequest(new { errors = new[] { err } });
        });
        usersApi.MapPost("/{username}/password", (string username, NewPassword body, UserStore users) =>
        {
            var err = users.SetPassword(username, body.Password ?? "");
            return err is null ? Results.NoContent() : Results.BadRequest(new { errors = new[] { err } });
        });
    }

    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null || System.Net.IPAddress.IsLoopback(ip);   // no address: the in-memory test server
    }

    private static Task SignIn(HttpContext ctx, string username, string role) =>
        ctx.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], Scheme)));
}
