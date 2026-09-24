using Microsoft.Data.SqlClient;

namespace Nexus.Promotions.B1;

/// <summary>
/// dbo.APE_Settings, the same table the worker and the TransactionNotification/PostTransactionNotice
/// procedures read (sql/mssql/01_APE_Tables.sql). One central switch here reaches every workstation running
/// the add-on immediately, with nothing to change on the workstations themselves.
/// </summary>
public sealed class ApeSettings(string? connectionString)
{
    public bool Configured => !string.IsNullOrWhiteSpace(connectionString);

    /// <summary>True (Mode A on) when the setting is missing, or when there is no SQL connection to check.</summary>
    public bool ModeAEnabled
    {
        get => !Configured || Get("ModeA") != "N";
        set => Set("ModeA", value ? "Y" : "N");
    }

    string? Get(string name)
    {
        using var cn = new SqlConnection(connectionString);
        cn.Open();
        using var cmd = new SqlCommand("SELECT Value FROM dbo.APE_Settings WHERE Name = @n", cn);
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar() as string;
    }

    void Set(string name, string value)
    {
        if (!Configured) throw new InvalidOperationException("No SQL connection configured for APE settings.");
        using var cn = new SqlConnection(connectionString);
        cn.Open();
        using var cmd = new SqlCommand(
            "UPDATE dbo.APE_Settings SET Value = @v WHERE Name = @n; " +
            "IF @@ROWCOUNT = 0 INSERT dbo.APE_Settings (Name, Value) VALUES (@n, @v);", cn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
