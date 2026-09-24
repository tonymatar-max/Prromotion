using System;
using SAPbouiCOM;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// Reads the settings every workstation shares from dbo.APE_Settings in the company the user is logged in to,
/// through the DI API session the UI API already has (no password, no extra connection). This is what makes a
/// registered add-on need no per-workstation file: register it once, set 'ApiUrl' (and 'ApiKey') once
/// (Setup tool: "setting ApiUrl http://server:5190"), and every workstation finds the promotion API on its own.
/// SQL Server companies only, like the rest of the SQL side; on any failure the local file and defaults stand.
/// </summary>
internal static class CentralSettings
{
    public static void Fill(Settings settings, Application app)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiUrl) && !string.IsNullOrWhiteSpace(settings.ApiKey)) return;
        try
        {
            var company = (SAPbobsCOM.Company)app.Company.GetDICompany();
            var rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
            rs.DoQuery("SELECT Name, Value FROM APE_Settings WHERE Name IN ('ApiUrl', 'ApiKey')");
            for (; !rs.EoF; rs.MoveNext())
            {
                var value = Convert.ToString(rs.Fields.Item("Value").Value)?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                switch (Convert.ToString(rs.Fields.Item("Name").Value))
                {
                    case "ApiUrl" when string.IsNullOrWhiteSpace(settings.ApiUrl): settings.ApiUrl = value; break;
                    case "ApiKey" when string.IsNullOrWhiteSpace(settings.ApiKey): settings.ApiKey = value; break;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Central settings not available (" + ex.Message + "); using the local file and defaults.");
        }
    }
}
