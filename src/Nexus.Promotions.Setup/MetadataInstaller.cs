using Nexus.Promotions.B1;
﻿namespace Nexus.Promotions.Setup;

/// <summary>Creates the APE user tables, fields and UDOs through the Service Layer. Idempotent: existing objects are skipped, never changed.</summary>
public sealed class MetadataInstaller(ServiceLayer sl, Action<string> log)
{
    public async Task<(int Created, int Existing)> RunAsync()
    {
        int created = 0, existing = 0;

        foreach (var t in Schema.Tables)
        {
            if (await sl.GetAsync($"UserTablesMD('{t.Name}')") is not null) { existing++; continue; }
            await sl.PostAsync("UserTablesMD", new { TableName = t.Name, TableDescription = t.Description, TableType = t.Type });
            log($"  table   @{t.Name}");
            created++;
        }

        foreach (var f in Schema.Fields())
        {
            if (await FieldExistsAsync(f)) { existing++; continue; }
            await sl.PostAsync("UserFieldsMD", new
            {
                f.Name,
                f.Description,
                TableName = f.MdTable,
                f.Type,
                f.SubType,
                Size = f.Size > 0 ? f.Size : (int?)null,
                EditSize = f.Size > 0 ? f.Size : (int?)null,
                DefaultValue = f.Default,
                ValidValuesMD = f.Values?.Select(v => new { v.Value, v.Description }).ToArray(),
            });
            log($"  field   {f.MdTable}.U_{f.Name}");
            created++;
        }

        foreach (var u in Schema.Udos)
        {
            if (await sl.GetAsync($"UserObjectsMD('{u.Code}')") is not null) { existing++; continue; }
            await sl.PostAsync("UserObjectsMD", new
            {
                u.Code,
                u.Name,
                TableName = u.Table,
                ObjectType = "boud_MasterData",
                CanFind = "tYES",
                CanDelete = "tYES",
                CanCancel = "tNO",
                CanClose = "tNO",
                CanYearTransfer = "tNO",
                CanCreateDefaultForm = "tNO",
                ManageSeries = "tNO",
                CanLog = "tYES",               // change history for audit (NFR-04)
                LogTableName = "A" + u.Table,
                UserObjectMD_ChildTables = u.Children.Select(c => new { TableName = c, ObjectName = c }).ToArray(),
                UserObjectMD_FindColumns = u.FindColumns.Select(c => new { ColumnAlias = c, ColumnDescription = c }).ToArray(),
            });
            log($"  UDO     {u.Code}");
            created++;
        }
        return (created, existing);
    }

    public async Task<List<string>> MissingAsync()
    {
        var missing = new List<string>();
        foreach (var t in Schema.Tables)
            if (await sl.GetAsync($"UserTablesMD('{t.Name}')") is null) missing.Add("@" + t.Name);
        foreach (var f in Schema.Fields())
            if (!await FieldExistsAsync(f)) missing.Add($"{f.MdTable}.U_{f.Name}");
        foreach (var u in Schema.Udos)
            if (await sl.GetAsync($"UserObjectsMD('{u.Code}')") is null) missing.Add("UDO " + u.Code);
        return missing;
    }

    async Task<bool> FieldExistsAsync(Schema.Field f)
    {
        var res = await sl.GetAsync($"UserFieldsMD?$filter=TableName eq '{f.MdTable}' and Name eq '{f.Name}'&$select=FieldID");
        return res?["value"]?.AsArray().Count > 0;
    }
}
