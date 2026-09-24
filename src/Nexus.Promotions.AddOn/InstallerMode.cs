using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Nexus.Promotions.AddOn;

/// <summary>
/// The .ard registration file lists this exe as installer and uninstaller as well as the add-on, because B1
/// only distributes the files the .ard names. B1 runs the installer with ONE argument,
/// "installFolder|fullPathOfAddOnInstallAPI.dll", waits for the installer to call EndInstall() in that dll, and
/// runs the uninstaller with the "Uninstaller Command Line Arguments" from the .ard ("/U").
/// A real UI API connection string is a hex string with no '|', so the two cases cannot be confused.
///
/// This class must use nothing but the .NET Framework: in installer mode Main runs before any other type
/// (and before the embedded assemblies are needed) is touched. Everything is logged to
/// %TEMP%\NexusPromotionsAddOn.install.log because an installer run by B1 has no window to show errors in.
/// </summary>
internal static class InstallerMode
{
    const string RegistryKey = @"Software\Nexus\PromotionsAddOn";
    const string ExeName = "NexusPromotionsAddOn.exe";

    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "NexusPromotionsAddOn.install.log");

    /// <summary>True when this run was an install or uninstall (exitCode set); false for a normal add-on start.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        // Paths with spaces may reach us split into several arguments; a connection string never has spaces.
        var command = string.Join(" ", args).Trim().Trim('"');
        if (command.Length == 0) return false;

        if (string.Equals(command, "/U", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = Uninstall();
            return true;
        }

        var parts = command.Split('|');
        if (parts.Length == 2 && parts[1].Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = Install(parts[0].Trim().Trim('"'), parts[1].Trim().Trim('"'));
            return true;
        }
        return false;
    }

    static int Install(string folder, string installApiDll)
    {
        Log($"Install: folder='{folder}', AddOnInstallAPI='{installApiDll}'");
        try
        {
            Directory.CreateDirectory(folder);
            var self = Assembly.GetExecutingAssembly().Location;
            var target = Path.Combine(folder, ExeName);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, target, overwrite: true);
                Log($"Copied {self} -> {target}");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
                key?.SetValue("InstallFolder", folder);
        }
        catch (Exception ex)
        {
            Log("Install failed: " + ex);
            return 1;
        }

        // Tell B1 the installation is complete; without this the B1 client waits until the install time in the .ard runs out.
        var result = EndInstall(installApiDll);
        Log($"EndInstall returned {result}");
        return result == 0 ? 0 : 1;
    }

    static int Uninstall()
    {
        Log("Uninstall");
        try
        {
            string? folder;
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryKey))
                folder = key?.GetValue("InstallFolder") as string;
            if (!string.IsNullOrEmpty(folder))
            {
                var target = Path.Combine(folder!, ExeName);
                var self = Assembly.GetExecutingAssembly().Location;
                // The uninstaller may itself be the installed exe; a running exe cannot delete itself.
                if (File.Exists(target) && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    File.Delete(target);
            }
            Registry.CurrentUser.DeleteSubKey(RegistryKey, throwOnMissingSubKey: false);
            return 0;
        }
        catch (Exception ex)
        {
            Log("Uninstall failed: " + ex);
            return 1;
        }
    }

    // AddOnInstallAPI.dll / AddOnInstallAPI_x64.dll: B1 passes the path of the one matching this process,
    // so it is loaded by that full path instead of a fixed [DllImport] name.
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int EndInstallFunction();

    static int EndInstall(string dllPath)
    {
        try
        {
            // The export is overridable only so the plumbing can be tested against a harmless system dll.
            var export = Environment.GetEnvironmentVariable("APE_INSTALL_API_EXPORT") ?? "EndInstall";
            Environment.CurrentDirectory = Path.GetDirectoryName(dllPath) ?? Environment.CurrentDirectory;
            var module = LoadLibrary(dllPath);
            if (module == IntPtr.Zero) { Log($"LoadLibrary failed ({Marshal.GetLastWin32Error()}): {dllPath}"); return 1; }
            var proc = GetProcAddress(module, export);
            if (proc == IntPtr.Zero) { Log($"Export {export} not found in {dllPath}"); return 1; }
            return ((EndInstallFunction)Marshal.GetDelegateForFunctionPointer(proc, typeof(EndInstallFunction)))();
        }
        catch (Exception ex)
        {
            Log("EndInstall failed: " + ex);
            return 1;
        }
    }

    static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch { /* an installer must never fail because it could not log */ }
    }
}
