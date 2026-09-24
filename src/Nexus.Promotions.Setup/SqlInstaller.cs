using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Nexus.Promotions.Setup;

/// <summary>
/// Installs sql/mssql into a company database and hooks it into SBO_SP_TransactionNotification and
/// SBO_SP_PostTransactionNotice. The hook blocks come from 05_TransactionNotification_snippet.sql, so the
/// snippet stays the single source. Each procedure is backed up before it is altered, and a procedure that
/// already contains the APE block is left alone.
/// </summary>
public sealed class SqlInstaller(string connectionString, string sqlDir, string backupDir, Action<string> log)
{
    const string Begin = "-- Nexus Promotions (APE) — BEGIN";
    const string End = "-- Nexus Promotions (APE) — END";

    public void InstallScripts()
    {
        using var cn = Open();
        foreach (var file in Directory.GetFiles(sqlDir, "0*.sql").Order())
        {
            if (file.EndsWith("_snippet.sql")) continue;
            foreach (var batch in Regex.Split(File.ReadAllText(file), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) Exec(cn, batch);
            log($"  script  {Path.GetFileName(file)}");
        }
    }

    public void HookNotificationProcedures()
    {
        var blocks = Regex.Matches(File.ReadAllText(Path.Combine(sqlDir, "05_TransactionNotification_snippet.sql")),
            Regex.Escape(Begin) + ".*?" + Regex.Escape(End), RegexOptions.Singleline);
        if (blocks.Count != 2) throw new InvalidOperationException("The snippet file must contain exactly two APE blocks.");

        Hook("SBO_SP_TransactionNotification", blocks[0].Value);
        Hook("SBO_SP_PostTransactionNotice", blocks[1].Value);
    }

    void Hook(string procedure, string block)
    {
        using var cn = Open();
        var definition = Scalar(cn, $"SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.{procedure}'))") as string
            ?? throw new InvalidOperationException($"dbo.{procedure} not found in {cn.Database}");
        if (definition.Contains(Begin))
        {
            log($"  hook    {procedure}: already present");
            return;
        }

        // Insert before the last "select @error, @error_message" (the procedure's result set).
        var finalSelect = Regex.Matches(definition, @"select\s+@error\s*,\s*@error_message", RegexOptions.IgnoreCase);
        if (finalSelect.Count == 0)
            throw new InvalidOperationException($"{procedure}: final 'select @error, @error_message' not found; add the snippet by hand.");
        var at = finalSelect[^1].Index;
        var patched = definition[..at] + block + Environment.NewLine + definition[at..];
        patched = Regex.Replace(patched, @"^\s*CREATE\s+PROC(EDURE)?\b", "ALTER PROCEDURE", RegexOptions.IgnoreCase);

        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, $"{cn.Database}_{procedure}_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
        File.WriteAllText(backup, definition);

        Exec(cn, patched);
        log($"  hook    {procedure} (backup: {backup})");
    }

    public void SetSetting(string name, string? value)
    {
        using var cn = Open();
        using var cmd = new SqlCommand(
            "UPDATE dbo.APE_Settings SET Value = @v WHERE Name = @n; " +
            "IF @@ROWCOUNT = 0 INSERT dbo.APE_Settings (Name, Value) VALUES (@n, @v);", cn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public SqlConnection Open()
    {
        var cn = new SqlConnection(connectionString);
        cn.Open();
        return cn;
    }

    public static void Exec(SqlConnection cn, string sql)
    {
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 300 };
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqlConnection cn, string sql)
    {
        using var cmd = new SqlCommand(sql, cn);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }
}
