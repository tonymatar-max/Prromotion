using System;
using System.IO;
using System.Text;
using SAPbouiCOM;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// Read-only inspection of open sales document forms (NexusPromotionsAddOn.exe --diag), and the add-on's error log.
/// Changes nothing on the form: it only reads items, matrix columns and data source values.
/// </summary>
internal static class Diagnostics
{
    public static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NexusPromotionsAddOn.log");

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch { /* logging must never break the add-on */ }
    }

    public static string Inspect(Application app)
    {
        var sb = new StringBuilder();
        for (int f = 0; f < app.Forms.Count; f++)
        {
            var form = app.Forms.Item(f);
            if (!PromotionApplier.Forms.TryGetValue(form.TypeEx, out var tables)) continue;
            sb.AppendLine($"FORM {form.TypeEx} uid={form.UniqueID} mode={form.Mode} title={form.Title}");

            for (int i = 0; i < form.Items.Count; i++)
            {
                var item = form.Items.Item(i);
                if (item.Type is BoFormItemTypes.it_MATRIX or BoFormItemTypes.it_BUTTON)
                    sb.AppendLine($"  item {item.UniqueID,-8} {item.Type,-12} visible={item.Visible} left={item.Left} top={item.Top}");
            }

            try
            {
                var matrix = (Matrix)form.Items.Item("38").Specific;
                sb.AppendLine($"  matrix 38 rows={matrix.RowCount} columns={matrix.Columns.Count}");
                for (int c = 0; c < matrix.Columns.Count; c++)
                {
                    var col = matrix.Columns.Item(c);
                    string cell1;
                    try { cell1 = matrix.RowCount > 0 ? Cell(col, 1) : ""; }
                    catch (Exception ex) { cell1 = "<" + ex.Message + ">"; }
                    sb.AppendLine($"    col {col.UniqueID,-16} visible={col.Visible,-5} editable={col.Editable,-5} type={col.Type,-14} title={col.TitleObject.Caption,-24} row1={cell1}");
                }
            }
            catch (Exception ex) { sb.AppendLine("  matrix 38: " + ex.Message); }

            try
            {
                var ds = form.DataSources.DBDataSources.Item(tables.Lines);
                sb.AppendLine($"  DBDataSource {tables.Lines} size={ds.Size}");
                for (int r = 0; r < ds.Size; r++)
                    sb.AppendLine($"    [{r}] ItemCode={ds.GetValue("ItemCode", r).Trim()} Quantity={ds.GetValue("Quantity", r).Trim()} "
                        + $"PriceBefDi={ds.GetValue("PriceBefDi", r).Trim()} DiscPrcnt={ds.GetValue("DiscPrcnt", r).Trim()} "
                        + $"U_APE_Promo={ds.GetValue("U_APE_Promo", r).Trim()} U_APE_Free={ds.GetValue("U_APE_Free", r).Trim()}");
            }
            catch (Exception ex) { sb.AppendLine("  DBDataSource: " + ex.Message); }
        }
        return sb.Length == 0 ? "No open sales document form." : sb.ToString();
    }

    static string Cell(Column col, int row)
    {
        var specific = col.Cells.Item(row).Specific;
        return specific switch
        {
            EditText e => e.Value,
            ComboBox c => c.Selected?.Value ?? "",
            CheckBox k => k.Checked.ToString(),
            _ => specific?.GetType().Name ?? "",
        };
    }
}
