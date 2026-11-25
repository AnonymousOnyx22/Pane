using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Pane;

/// <summary>
/// Pane.exe carries the host inside it. "Let this PC be controlled" extracts it and runs its installer once
/// (one Windows permission prompt); after that the host starts with Windows and this PC shows up in Pane
/// on your other PCs automatically.
/// </summary>
public static class HostSetup
{
    private static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PaneHost", "PaneHost.exe");

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("PaneHost.exe");
    public static bool IsRunning => Process.GetProcessesByName("PaneHost").Length > 0;
    public static bool IsInstalled => File.Exists(InstalledExe) && IsRunning;

    /// <summary>Extract the embedded host and install it elevated. Returns an error message, or null on success.</summary>
    public static string? Install()
    {
        try
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("PaneHost.exe");
            if (res == null) return "This build of Pane has no host inside it.";
            var dir = Path.Combine(Path.GetTempPath(), "PaneSetup");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "PaneHost.exe");
            using (var fs = File.Create(path)) res.CopyTo(fs);

            var psi = new ProcessStartInfo(path, "--install")
            {
                UseShellExecute = true,   // required for the elevation prompt
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(60_000);
            return p.HasExited && p.ExitCode == 0 ? null : "The install did not finish. Try again and click Yes on the Windows prompt.";
        }
        catch (System.ComponentModel.Win32Exception) { return "Windows permission was declined."; }
        catch (Exception ex) { return ex.Message; }
    }
}
