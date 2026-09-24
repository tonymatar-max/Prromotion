using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Nexus.Promotions.Sql.Tests;

/// <summary>
/// Builds a throwaway database (APE_SqlTest) at compatibility level 100, like most B1 company databases,
/// with a stub of the B1 tables, then installs sql/mssql. Skipped when no SQL Server is reachable.
/// Server: APE_SQL_SERVER environment variable, default localhost with Windows authentication.
/// </summary>
public sealed class SqlFixture : IDisposable
{
    public const string Database = "APE_SqlTest";

    public string? SkipReason { get; }
    public string ConnectionString { get; } = "";

    public SqlFixture()
    {
        var server = Environment.GetEnvironmentVariable("APE_SQL_SERVER") ?? "localhost";
        var master = $"Server={server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";
        try
        {
            using var cn = new SqlConnection(master);
            cn.Open();
            Exec(cn, $"""
                IF DB_ID('{Database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Database}];
                END
                CREATE DATABASE [{Database}] COLLATE SQL_Latin1_General_CP850_CI_AS;
                ALTER DATABASE [{Database}] SET COMPATIBILITY_LEVEL = 100;
                """);
        }
        catch (SqlException ex)
        {
            SkipReason = $"SQL Server not reachable ({ex.Message})";
            return;
        }

        ConnectionString = master.Replace("Database=master", $"Database={Database}");
        var root = FindRepoRoot();
        using var db = Open();
        RunScript(db, Path.Combine(root, "tests", "sql", "b1_stub_mssql.sql"));
        foreach (var file in Directory.GetFiles(Path.Combine(root, "sql", "mssql"), "0*.sql").Order())
            if (!file.EndsWith("_snippet.sql")) RunScript(db, file);
        Exec(db, "INSERT dbo.[@APE_PROMO] (Code, Name, U_Status) VALUES (N'TEN', N'10% off', N'A'), (N'B2G1', N'Buy 2 get 1', N'A');");
    }

    public SqlConnection Open()
    {
        var cn = new SqlConnection(ConnectionString);
        cn.Open();
        return cn;
    }

    public static void Exec(SqlConnection cn, string sql)
    {
        using var cmd = new SqlCommand(sql, cn);
        cmd.ExecuteNonQuery();
    }

    static void RunScript(SqlConnection cn, string path)
    {
        var batches = Regex.Split(File.ReadAllText(path), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
            Exec(cn, batch);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.Promotions.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public void Dispose()
    {
        // The database is kept after the run so failures can be inspected; the next run recreates it.
    }
}
