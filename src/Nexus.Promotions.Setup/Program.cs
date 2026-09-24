using Microsoft.Extensions.Configuration;
using Nexus.Promotions.B1;
using Nexus.Promotions.Setup;

// Nexus Promotions (APE) setup for one SAP B1 company (MS SQL).
//   plan     show what would be created (no connection)
//   install  UDTs, UDFs and UDOs through the Service Layer, then the SQL scripts and the notification hooks
//   verify   check nothing is missing, then run the end-to-end check on real documents
//   get <p>  print a Service Layer GET (for example: get "APE_PROMO('APE-DEMO-10')")
//   cancel-orders <DocEntry> ...   cancel sales orders left behind by an interrupted verify
//   setting <Name> [value]   set (or, without a value, clear) a dbo.APE_Settings row, e.g.
//                            setting ApiUrl http://promo-server:5190   (read by every workstation's add-on)
//
// Settings: appsettings.json, then appsettings.Local.json (not committed), then APE_ environment variables.
// The Service Layer password is never stored in the repository: set APE_ServiceLayer__Password or put it
// in appsettings.Local.json.

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "plan";
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables("APE_")
    .Build();

var slUrl = config["ServiceLayer:Url"] ?? "https://localhost:50000/b1s/v1";
var company = config["ServiceLayer:CompanyDb"] ?? throw new InvalidOperationException("ServiceLayer:CompanyDb is not set");
var user = config["ServiceLayer:User"] ?? "manager";
var sqlConnection = config["Sql:ConnectionString"]
    ?? $"Server=localhost;Database={company};Integrated Security=true;TrustServerCertificate=true";
var sqlDir = config["Sql:ScriptsDir"] ?? FindUp(Path.Combine("sql", "mssql"));
var backupDir = config["Sql:BackupDir"] ?? Path.Combine(Path.GetDirectoryName(sqlDir)!, "..", "backups");

void Log(string line) => Console.WriteLine(line);

if (command == "plan")
{
    var fields = Schema.Fields().ToList();
    Log($"Company {company} via {slUrl}");
    Log($"  {Schema.Tables.Length} user tables, {fields.Count} fields, {Schema.Udos.Length} UDOs");
    foreach (var g in fields.GroupBy(f => f.MdTable)) Log($"  {g.Key,-16} {g.Count()} field(s)");
    Log($"  SQL scripts from {sqlDir}; procedure backups to {Path.GetFullPath(backupDir)}");
    return 0;
}

var password = config["ServiceLayer:Password"];
if (string.IsNullOrEmpty(password))
{
    Log("Service Layer password not set. Set the APE_ServiceLayer__Password environment variable,");
    Log("or add { \"ServiceLayer\": { \"Password\": \"...\" } } to appsettings.Local.json next to the tool.");
    return 2;
}

using var sl = new ServiceLayer(new ServiceLayerOptions
{
    Url = slUrl,
    CompanyDb = company,
    User = user,
    Password = password,
    AllowSelfSignedCertificate = config.GetValue("ServiceLayer:AllowSelfSignedCertificate", true),
});
try
{
    await sl.LoginAsync();
}
catch (ServiceLayerException ex)
{
    Log($"Could not log in to {company} as '{user}': {ex.Message}");
    Log("Nothing was changed. Check the password, or set another user with APE_ServiceLayer__User.");
    return 1;
}
try
{
    var metadata = new MetadataInstaller(sl, Log);
    var sql = new SqlInstaller(sqlConnection, sqlDir, backupDir, Log);

    switch (command)
    {
        case "install":
            Log($"Installing APE into {company}");
            Log("Service Layer metadata:");
            var (created, existing) = await metadata.RunAsync();
            Log($"  {created} created, {existing} already present");
            Log("SQL:");
            sql.InstallScripts();
            sql.HookNotificationProcedures();
            Log("Done.");
            return 0;

        case "verify":
            var missing = await metadata.MissingAsync();
            if (missing.Count > 0)
            {
                Log($"Missing {missing.Count} object(s): {string.Join(", ", missing.Take(20))}");
                return 1;
            }
            Log("All metadata present. End-to-end check:");
            return await new EndToEndCheck(sl, sql, sqlConnection, Log).RunAsync() ? 0 : 1;

        case "get":
            var node = await sl.GetAsync(args.ElementAtOrDefault(1) ?? throw new ArgumentException("get needs a path"));
            Log(node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "(not found)");
            return 0;

        case "setting":
        {
            var name = args.ElementAtOrDefault(1) ?? throw new ArgumentException("setting needs a name");
            var value = args.ElementAtOrDefault(2);
            sql.SetSetting(name, value);
            Log($"  {name} = {(string.IsNullOrEmpty(value) ? "(cleared)" : name.Contains("Key", StringComparison.OrdinalIgnoreCase) ? "(set)" : value)}");
            return 0;
        }

        case "cancel-orders":
            foreach (var entry in args.Skip(1))
            {
                await sl.PostAsync($"Orders({int.Parse(entry)})/Cancel", new { });
                Log($"  order {entry} cancelled");
            }
            return 0;

        default:
            Log($"Unknown command '{command}'. Use plan, install, verify, get, setting or cancel-orders.");
            return 2;
    }
}
catch (ServiceLayerException ex)
{
    Log("Service Layer error: " + ex.Message);
    return 1;
}
finally
{
    await sl.LogoutAsync();
}

static string FindUp(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
    return dir is null ? throw new DirectoryNotFoundException(relative) : Path.Combine(dir.FullName, relative);
}
