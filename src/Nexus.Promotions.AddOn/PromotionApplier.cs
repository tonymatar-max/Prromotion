using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SAPbouiCOM;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// Mode A on a marketing document form (PRD 6.5, FR-37–FR-39).
/// Everything on the lines goes through the matrix cells, so B1 recalculates prices, tax and totals exactly as if
/// a user typed them. Lines are read from the line DBDataSource, which B1 keeps current on system forms.
/// FlushToDataSource / LoadFromDataSource are not used: on a system matrix they fail with
/// "The item is not a user-defined item [66000-8]". The header APE fields go into the header DBDataSource.
/// </summary>
public sealed class PromotionApplier(Application app, ApiClient api, Settings settings)
{
    public const string ButtonId = "apeApply";
    const string MatrixId = "38";
    const string ColItem = "1", ColQty = "11", ColPrice = "14", ColDisc = "15";
    const string ColPromo = "U_APE_Promo", ColDiscAmt = "U_APE_DiscAmt", ColFree = "U_APE_Free", ColGroup = "U_APE_Group";
    const string DeleteRowMenu = "1293";

    /// <summary>Form type → header and line tables.</summary>
    public static readonly Dictionary<string, (string Header, string Lines, string ObjectTable)> Forms = new()
    {
        ["149"] = ("OQUT", "QUT1", "OQUT"),     // Sales Quotation
        ["139"] = ("ORDR", "RDR1", "ORDR"),     // Sales Order
        ["140"] = ("ODLN", "DLN1", "ODLN"),     // Delivery
        ["133"] = ("OINV", "INV1", "OINV"),     // A/R Invoice
        ["60091"] = ("OINV", "INV1", "OINV"),   // A/R Reserve Invoice
    };

    bool _busy;
    char? _decimalSeparator;
    int _amountDecimals = 2;

    public void AddButton(Form form)
    {
        if (ItemExists(form, ButtonId)) return;
        var cancel = form.Items.Item("2");
        var item = form.Items.Add(ButtonId, BoFormItemTypes.it_BUTTON);
        item.Left = cancel.Left + cancel.Width + 6;
        item.Top = cancel.Top;
        item.Height = cancel.Height;
        item.Width = 110;
        item.AffectsFormMode = false;
        ((Button)item.Specific).Caption = "Apply Promotions";
    }

    /// <summary>Returns false to stop the save (user chose Review, or cancelled when the engine was unreachable).</summary>
    public bool Apply(Form form, bool onSave)
    {
        if (_busy) return true;
        if (form.Mode is not (BoFormMode.fm_ADD_MODE or BoFormMode.fm_UPDATE_MODE or BoFormMode.fm_OK_MODE)) return true;
        if (!Forms.TryGetValue(form.TypeEx, out var tables)) return true;

        var header = form.DataSources.DBDataSources.Item(tables.Header);
        if (header.GetValue("DocType", 0).Trim() != "I") return true;   // service documents have no items to promote
        // The central switch (dbo.APE_Settings 'ModeA' via the admin app) only turns off the automatic behaviour;
        // the button a user clicks themselves always still works.
        if (onSave && !api.IsModeAEnabled()) return true;
        LoadCompanyFormats();

        var matrix = (Matrix)form.Items.Item(MatrixId).Specific;
        var lines = form.DataSources.DBDataSources.Item(tables.Lines);
        var input = ReadDocument(tables.ObjectTable, header, lines);
        if (input.Lines.Count == 0) return true;

        DocumentEvaluation evaluation;
        try
        {
            evaluation = api.Evaluate(input);
        }
        catch (Exception ex)
        {
            // FR-38: the engine is down. Save without promotions (quotations/orders get re-evaluated by Mode B),
            // or stay on the form.
            var choice = app.MessageBox(
                $"The promotion engine could not be reached ({Short(ex)}).\n\nSave without applying promotions?",
                2, "Save without promotions", "Cancel");
            if (choice != 1) return false;
            SetHeaderFields(form, "", header.GetValue("U_APE_Mode", 0).Trim(), "Pending", header.GetValue("U_APE_EvalAt", 0).Trim());
            return true;
        }

        var storedHash = header.GetValue("U_APE_Hash", 0).Trim();
        Diagnostics.Log($"Apply form={form.TypeEx} mode={form.Mode} onSave={onSave} lines={input.Lines.Count} changed={evaluation.Changed} "
            + $"storedHash='{storedHash}' newHash={evaluation.Result.Hash.Substring(0, 12)}…");
        if (!evaluation.Changed && storedHash == evaluation.Result.Hash)
        {
            if (!onSave) app.StatusBar.SetText("Promotions are already up to date.", BoMessageTime.bmt_Short, BoStatusBarMessageType.smt_Success);
            return true;
        }

        _busy = true;
        form.Freeze(true);
        try
        {
            WriteBack(form, matrix, header, evaluation);
        }
        finally
        {
            form.Freeze(false);
            _busy = false;
        }

        var summary = Summary(evaluation);
        if (onSave && settings.ConfirmOnSave && evaluation.Changed)
            return app.MessageBox(summary, 1, "Save", "Review") == 1;

        app.StatusBar.SetText(summary.Replace('\n', ' '), BoMessageTime.bmt_Medium, BoStatusBarMessageType.smt_Success);
        return true;
    }

    DocumentInput ReadDocument(string objectTable, DBDataSource header, DBDataSource lines)
    {
        var input = new DocumentInput
        {
            DocumentType = objectTable,
            CardCode = header.GetValue("CardCode", 0).Trim(),
            DocDate = DateTime.TryParseExact(header.GetValue("DocDate", 0).Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null,
            Coupons = header.GetValue("U_APE_Coupons", 0).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim()).Where(c => c.Length > 0).ToArray(),
            AmountDecimals = _amountDecimals,
        };
        for (int i = 0; i < lines.Size; i++)
        {
            var item = lines.GetValue("ItemCode", i).Trim();
            if (item.Length == 0) continue;
            input.Lines.Add(new DocumentLineInput
            {
                LineNum = i,
                ItemCode = item,
                Quantity = Num(lines.GetValue("Quantity", i)),
                UnitPrice = Num(lines.GetValue("PriceBefDi", i)),
                DiscountPercent = Num(lines.GetValue("DiscPrcnt", i)),
                PromotionCodes = NullIfEmpty(lines.GetValue("U_APE_Promo", i)),
                IsFree = lines.GetValue("U_APE_Free", i).Trim() == "Y",
                Group = NullIfEmpty(lines.GetValue("U_APE_Group", i)),
            });
        }
        return input;
    }

    void WriteBack(Form form, Matrix matrix, DBDataSource header, DocumentEvaluation e)
    {
        // 1. Delete rows that go (earlier free lines, lines that became entirely free), bottom up.
        var removed = e.RemovedLineNums.OrderByDescending(n => n).ToList();
        foreach (var index in removed)
        {
            form.Select();
            matrix.SelectRow(index + 1, true, false);
            app.ActivateMenuItem(DeleteRowMenu);
        }
        int NewIndex(int oldIndex) => oldIndex - removed.Count(r => r < oldIndex);

        // 2. Update kept rows: only cells that change, quantity before discount (B1 may re-apply its own
        //    period/volume discount when the quantity changes; ours is written after it).
        var finalRows = new List<(int Row, PlannedLine Plan)>();
        foreach (var p in e.Lines.Where(p => p.LineNum is not null))
        {
            var row = NewIndex(p.LineNum!.Value) + 1;
            SetIfDifferent(matrix, ColQty, row, p.Line.Quantity);
            SetIfDifferent(matrix, ColDisc, row, p.DiscountPercent);
            finalRows.Add((row, p));
        }

        // 3. Append new lines (free lines, added reward items) in the empty last row. The hash does not
        //    depend on line order, so they need not sit next to their paid line.
        foreach (var p in e.Lines.Where(p => p.LineNum is null))
        {
            var row = matrix.RowCount;
            SetCell(matrix, ColItem, row, p.Line.ItemCode);
            SetCell(matrix, ColQty, row, Format(p.Line.Quantity));
            SetCell(matrix, ColPrice, row, Format(p.Line.UnitPrice));
            SetCell(matrix, ColDisc, row, Format(p.DiscountPercent));
            finalRows.Add((row, p));
        }

        // 4. APE line fields through their matrix columns (B1 adds UDF columns to the document matrix).
        foreach (var (row, p) in finalRows)
        {
            SetText(matrix, ColPromo, row, p.Line.PromotionCodes);
            SetIfDifferent(matrix, ColDiscAmt, row, p.Line.DiscountAmount);
            SetCombo(matrix, ColFree, row, p.Line.IsFree ? "Y" : "N");
            SetText(matrix, ColGroup, row, p.Line.Group ?? "");
        }

        // 5. Header: hash and status on the user-defined fields form (form type "-139" next to a Sales Order),
        //    where B1 keeps document header UDFs; its items are named after the fields.
        SetHeaderFields(form, e.Result.Hash, "A", "Applied",
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    void SetHeaderFields(Form form, string hash, string mode, string status, string evaluatedAt)
    {
        var udf = HeaderFieldsForm(form);
        SetUdfText(udf, "U_APE_Hash", hash);
        SetUdfCombo(udf, "U_APE_Mode", mode);
        SetUdfCombo(udf, "U_APE_Status", status);
        SetUdfText(udf, "U_APE_EvalAt", evaluatedAt);
        Diagnostics.Log($"  header set on {udf.TypeEx}/{udf.UniqueID}: udf hash='{((EditText)udf.Items.Item("U_APE_Hash").Specific).Value}' "
            + $"datasource hash='{form.DataSources.DBDataSources.Item(0).GetValue("U_APE_Hash", 0).Trim()}' form mode={form.Mode}");
    }

    /// <summary>The header UDF form must be open (View > User-Defined Fields); B1 only creates it then.</summary>
    Form HeaderFieldsForm(Form form)
    {
        try { return app.Forms.GetForm("-" + form.TypeEx, form.TypeCount); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Turn on View > User-Defined Fields so promotions can be recorded on the document header. (" + ex.Message + ")");
        }
    }

    static void SetUdfText(Form udf, string field, string value)
    {
        var edit = (EditText)udf.Items.Item(field).Specific;
        if (edit.Value.Trim() != value) edit.Value = value;
    }

    static void SetUdfCombo(Form udf, string field, string value)
    {
        if (value.Length == 0) return;   // nothing to select; leave the field as it is
        var combo = (ComboBox)udf.Items.Item(field).Specific;
        if (combo.Selected?.Value != value) combo.Select(value, BoSearchKey.psk_ByValue);
    }

    /// <summary>Sets a text cell only when it changes, so unchanged rows fire no B1 validation.</summary>
    static void SetText(Matrix matrix, string column, int row, string value)
    {
        RequireVisible(matrix, column);
        if (CellText(matrix, column, row).Trim() != value) SetCell(matrix, column, row, value);
    }

    static void SetCombo(Matrix matrix, string column, int row, string value)
    {
        RequireVisible(matrix, column);
        var combo = (ComboBox)matrix.Columns.Item(column).Cells.Item(row).Specific;
        if (combo.Selected?.Value != value) combo.Select(value, BoSearchKey.psk_ByValue);
    }

    /// <summary>Hidden matrix cells cannot be set through the UI API; say which column to show instead of failing obscurely.</summary>
    static void RequireVisible(Matrix matrix, string column)
    {
        if (!matrix.Columns.Item(column).Visible)
            throw new InvalidOperationException(
                $"Column {column} is hidden on this form. Show it in Form Settings (Table Format) so promotions can be recorded.");
    }

    static string Summary(DocumentEvaluation e)
    {
        var free = e.Lines.Count(l => l.Line.IsFree);
        var text = e.Result.Promotions.Count == 0
            ? "No promotion applies to this document."
            : $"{e.Result.Promotions.Count} promotion(s) applied, saving {e.Result.DiscountTotal:N2}"
              + (free > 0 ? $", {free} free line(s)" : "") + ":\n"
              + string.Join("\n", e.Result.Promotions.Select(p => $"  {p.Code}  {p.Name}  {p.DiscountAmount:N2}"));
        if (e.Result.NearMisses.Count > 0)
            text += "\n\nAlmost there:\n" + string.Join("\n", e.Result.NearMisses.Take(3).Select(n => "  " + n.Message));
        return text;
    }

    void SetIfDifferent(Matrix matrix, string column, int row, decimal value)
    {
        var current = CellText(matrix, column, row);
        if (TryNum(current, out var now) && now == Math.Round(value, 6)) return;
        SetCell(matrix, column, row, Format(value));
    }

    static void SetCell(Matrix matrix, string column, int row, string value) =>
        ((EditText)matrix.Columns.Item(column).Cells.Item(row).Specific).Value = value;

    static string CellText(Matrix matrix, string column, int row) =>
        ((EditText)matrix.Columns.Item(column).Cells.Item(row).Specific).Value;

    /// <summary>UI cells take numbers in the company's display format; data sources take invariant text.</summary>
    string Format(decimal value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', _decimalSeparator ?? '.');

    bool TryNum(string text, out decimal value)
    {
        // Cells may carry a currency prefix ("GBP 100.00") and use the company's separators.
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '-' || c == (_decimalSeparator ?? '.')).ToArray())
            .Replace(_decimalSeparator ?? '.', '.');
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    static decimal Num(string dataSourceValue) =>
        decimal.TryParse(dataSourceValue.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    void LoadCompanyFormats()
    {
        if (_decimalSeparator is not null) return;
        var company = (SAPbobsCOM.Company)app.Company.GetDICompany();
        var rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(SAPbobsCOM.BoObjectTypes.BoRecordset);
        rs.DoQuery("SELECT DecSep, SumDec FROM OADM");
        var sep = rs.Fields.Item("DecSep").Value?.ToString();
        _decimalSeparator = string.IsNullOrEmpty(sep) ? '.' : sep![0];
        _amountDecimals = Convert.ToInt32(rs.Fields.Item("SumDec").Value);
    }

    static bool ItemExists(Form form, string id)
    {
        for (int i = 0; i < form.Items.Count; i++)
            if (form.Items.Item(i).UniqueID == id) return true;
        return false;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string Short(Exception ex) => ex.GetBaseException().Message.Length > 120
        ? ex.GetBaseException().Message.Substring(0, 120) + "…"
        : ex.GetBaseException().Message;
}
