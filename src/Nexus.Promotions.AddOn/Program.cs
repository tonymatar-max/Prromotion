using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SAPbouiCOM;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// Nexus Promotions Mode A add-on for the SAP B1 client.
/// Adds an "Apply Promotions" button to sales quotations, orders, deliveries and A/R invoices, and applies
/// promotions automatically when the user presses Add/Update (with a Save / Review summary).
///
/// Two ways to attach:
///  - Registered as a B1 add-on: B1 launches this exe with a real connection string as args[0], which
///    SboGuiApi.Connect() understands. That is the production path (Add-Ons administration in B1).
///  - Development / manual run: no args. SAP's development connection string (from the SDK's own
///    COM UI samples, e.g. Samples\COM UI\CSharp\01.HelloWorld) attaches to the B1 client that is running
///    and logged in to a company in the same Windows session.
/// </summary>
internal static class Program
{
    const string DevelopmentConnection =
        "0030002C0030002C00530041005000420044005F00440061007400650076002C0050004C006F006D0056004900490056";

    static Application? _app;
    static PromotionApplier? _applier;

    [STAThread]
    static int Main(string[] args)
    {
        // Installer / uninstaller mode (see InstallerMode) uses nothing but the .NET Framework and returns before
        // any other type is touched; Run() is a separate method so its types are only loaded when it is called.
        if (InstallerMode.TryRun(args, out var installerExitCode)) return installerExitCode;
        return Run(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        var diag = args.Length > 0 && args[0] == "--diag";
        var bench = args.Length > 0 && args[0] == "--bench";
        try
        {
            var gui = new SboGuiApi();
            gui.Connect(args.Length > 0 && !diag && !bench ? args[0] : DevelopmentConnection);
            _app = gui.GetApplication(-1);
        }
        catch (COMException ex)
        {
            System.Windows.Forms.MessageBox.Show(
                "Could not connect to SAP Business One.\n\n"
                + "Open the B1 client, log in to a company, then run this add-on again.\n\n" + ex.Message,
                "Nexus Promotions", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }

        if (bench)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bench.txt"), Diagnostics.Bench(_app!));
            return 0;
        }

        if (diag)
        {
            // Read-only: write what the open sales forms look like, then exit.
            var report = Diagnostics.Inspect(_app!);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.txt"), report);
            return 0;
        }

        var settings = Settings.Load();
        CentralSettings.Fill(settings, _app!);
        _applier = new PromotionApplier(_app, new ApiClient(settings, CompanyName(_app)), settings);

        SetFilters(_app);
        _app.ItemEvent += OnItemEvent;
        _app.AppEvent += OnAppEvent;
        _app.StatusBar.SetText($"Nexus Promotions connected (engine {settings.ResolvedApiUrl}).",
            BoMessageTime.bmt_Short, BoStatusBarMessageType.smt_Success);

        System.Windows.Forms.Application.Run();
        return 0;
    }

    /// <summary>The database of the company this B1 client is logged in to; one add-on process runs per client.</summary>
    static string? CompanyName(Application app)
    {
        try { return app.Company.DatabaseName; }
        catch (Exception) { return null; } // an unnamed company just skips the check
    }

    /// <summary>Only the events and forms the add-on needs, so the B1 client is not slowed down.</summary>
    static void SetFilters(Application app)
    {
        var filters = new EventFilters();
        foreach (var type in new[] { BoEventTypes.et_FORM_LOAD, BoEventTypes.et_ITEM_PRESSED })
        {
            var filter = filters.Add(type);
            foreach (var formType in PromotionApplier.Forms.Keys) filter.AddEx(formType);
        }
        app.SetFilter(filters);
    }

    static void OnItemEvent(string formUid, ref ItemEvent e, out bool bubble)
    {
        bubble = true;
        var clock = Stopwatch.StartNew();
        try
        {
            if (!PromotionApplier.Forms.ContainsKey(e.FormTypeEx)) return;

            if (e.EventType == BoEventTypes.et_FORM_LOAD && !e.BeforeAction)
                _applier!.AddButton(_app!.Forms.Item(formUid));

            else if (e.EventType == BoEventTypes.et_ITEM_PRESSED && !e.BeforeAction && e.ItemUID == PromotionApplier.ButtonId)
                _applier!.Apply(_app!.Forms.Item(formUid), onSave: false);

            // Add/Update button, before B1 saves: the document can still be changed, and returning false stops the save.
            else if (e.EventType == BoEventTypes.et_ITEM_PRESSED && e.BeforeAction && e.ItemUID == "1"
                     && e.FormMode is (int)BoFormMode.fm_ADD_MODE or (int)BoFormMode.fm_UPDATE_MODE)
                bubble = _applier!.Apply(_app!.Forms.Item(formUid), onSave: true);
        }
        catch (Exception ex)
        {
            // Never block the user's document because of an add-on error; report it and let B1 continue.
            Diagnostics.Log(ex.ToString());
            _app?.StatusBar.SetText("Nexus Promotions: " + ex.Message, BoMessageTime.bmt_Medium, BoStatusBarMessageType.smt_Error);
        }
        finally
        {
            // B1 waits for the add-on on every event, so anything slow here is a slow B1: name it in the log.
            if (clock.ElapsedMilliseconds > 300)
                Diagnostics.Log($"Slow event {e.EventType} on form {e.FormTypeEx} item '{e.ItemUID}': {clock.ElapsedMilliseconds} ms");
        }
    }

    static void OnAppEvent(BoAppEventTypes e)
    {
        if (e is BoAppEventTypes.aet_ShutDown or BoAppEventTypes.aet_CompanyChanged or BoAppEventTypes.aet_ServerTerminition)
            System.Windows.Forms.Application.Exit();
    }
}
