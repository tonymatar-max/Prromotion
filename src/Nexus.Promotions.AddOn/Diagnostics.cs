using System;
using System.Diagnostics;
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
    /// <summary>
    /// Per user (%LOCALAPPDATA%\Nexus\PromotionsAddOn), not next to the exe: once B1 installs the add-on it lives under
    /// Program Files, which an ordinary user cannot write to, and every workstation user has their own log.
    /// </summary>
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "PromotionsAddOn", "NexusPromotionsAddOn.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* logging must never break the add-on */ }
    }

    /// <summary>
    /// How long the add-on's own calls into B1 take on the sales forms that are open (NexusPromotionsAddOn.exe --bench).
    /// Every call from this process into B1 crosses a process boundary, and B1 waits for the add-on on each event.
    /// </summary>
    public static string Bench(Application app)
    {
        var sb = new StringBuilder();
        double Ms(Action action) { var sw = Stopwatch.StartNew(); action(); return sw.Elapsed.TotalMilliseconds; }

        const int reps = 50;
        sb.AppendLine($"one plain call into B1 (Forms.Count):            {Ms(() => { for (int i = 0; i < reps; i++) _ = app.Forms.Count; }) / reps:0.000} ms");
        for (int f = 0; f < app.Forms.Count; f++)
        {
            var form = app.Forms.Item(f);
            if (!PromotionApplier.Forms.ContainsKey(form.TypeEx)) continue;

            var count = form.Items.Count;
            sb.AppendLine($"FORM {form.TypeEx} '{form.Title}': {count} items");
            sb.AppendLine($"  old ItemExists (loop, Count re-read each pass): {Ms(() => { for (int i = 0; i < form.Items.Count; i++) _ = form.Items.Item(i).UniqueID; }):0.0} ms");
            sb.AppendLine($"  NEW ItemExists (what the add-on does now):      {Ms(() => _ = PromotionApplier.ItemExists(form, PromotionApplier.ButtonId)):0.0} ms   (button present: {PromotionApplier.ItemExists(form, PromotionApplier.ButtonId)})");
            sb.AppendLine($"  loop with Count read once:                      {Ms(() => { for (int i = 0; i < count; i++) _ = form.Items.Item(i).UniqueID; }):0.0} ms");
            sb.AppendLine($"  direct lookup Items.Item('2') (exists):         {Ms(() => { for (int i = 0; i < reps; i++) _ = form.Items.Item("2"); }) / reps:0.000} ms");
            sb.AppendLine($"  direct lookup of a missing id (try/catch):      {Ms(() => { try { _ = form.Items.Item("apeMissing"); } catch (Exception) { } }):0.000} ms");
            sb.AppendLine($"  reading Left/Top/Width/Height of item 2:        {Ms(() => { var it = form.Items.Item("2"); _ = it.Left; _ = it.Top; _ = it.Width; _ = it.Height; }):0.000} ms");

            // Saving reads every line (PromotionApplier.ReadDocument: 8 fields per line) through the data source.
            if (PromotionApplier.Forms.TryGetValue(form.TypeEx, out var tables))
            {
                var lines = form.DataSources.DBDataSources.Item(tables.Lines);
                sb.AppendLine($"  {tables.Lines}: {lines.Size} lines; one DBDataSource.GetValue:          {Ms(() => { for (int i = 0; i < reps; i++) _ = lines.GetValue("ItemCode", 0); }) / reps:0.000} ms");
                sb.AppendLine($"  reading all 8 fields of every line, as save does:   {Ms(() => { for (int r = 0; r < lines.Size; r++) foreach (var c in new[] { "ItemCode", "Quantity", "PriceBefDi", "DiscPrcnt", "U_APE_Promo", "U_APE_Free", "U_APE_Group", "LineNum" }) _ = lines.GetValue(c, r); }):0.0} ms");
                string xml = "";
                var xmlMs = Ms(() => xml = form.GetAsXML());
                sb.AppendLine($"  whole form as one XML document (GetAsXML):          {xmlMs:0.0} ms, {xml.Length / 1024} KB");
            }
        }
        return sb.ToString();
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
