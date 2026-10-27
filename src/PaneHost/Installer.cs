using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Self-installing logic for the host. `PaneHost.exe --install` copies the exe to ProgramData,
/// registers a highest-privilege logon scheduled task, opens the firewall, starts the host, and
/// prints the pairing key. This is what the one-click setup runs after extracting the host.
/// </summary>
public static class Installer
{
    public const string TaskName = "PaneHost";
    public static string InstalledExe => Path.Combine(HostConfig.Dir, "PaneHost.exe");

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int Install()
    {
        if (!IsAdministrator())
        {
            Console.WriteLine("Pane Host install must run as Administrator.");
            return 1;
        }

        Console.WriteLine("Installing Pane Host...");
        Directory.CreateDirectory(HostConfig.Dir);

        // 1. Copy the running exe into ProgramData (unless already there).
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            // Stop any running instance / existing task so the file isn't locked.
            Run("schtasks", $"/End /TN \"{TaskName}\"");
            foreach (var p in Process.GetProcessesByName("PaneHost"))
            {
                if (p.Id != Environment.ProcessId)
                    try { p.Kill(true); p.WaitForExit(3000); } catch { }
            }
            File.Copy(self, InstalledExe, overwrite: true);
            Console.WriteLine($"  Copied host to {InstalledExe}");
        }

        // 2. Ensure identity + pairing key exist.
        var cfg = HostConfig.LoadOrCreate();

        // 3. Scheduled task: run at logon, highest privileges.
        Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        var create = Run("schtasks",
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{InstalledExe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        Console.WriteLine(create.ok ? "  Scheduled task registered (runs at logon, elevated)."
                                     : "  WARNING: could not register scheduled task:\n" + create.output);

        // 4. Firewall rules.
        Run("netsh", "advfirewall firewall delete rule name=\"Pane Host TCP\"");
        Run("netsh", "advfirewall firewall delete rule name=\"Pane Host UDP\"");
        Run("netsh", $"advfirewall firewall add rule name=\"Pane Host TCP\" dir=in action=allow protocol=TCP localport={Net.TcpPort}");
        Run("netsh", $"advfirewall firewall add rule name=\"Pane Host UDP\" dir=in action=allow protocol=UDP localport={Net.DiscoveryPort}");
        Console.WriteLine($"  Firewall opened for TCP {Net.TcpPort} and UDP {Net.DiscoveryPort}.");

        // 4b. Show UAC prompts on the normal (capturable) desktop instead of the secure desktop, so admin
        //     prompts are visible and clickable through Pane. UAC still asks for consent; it just isn't
        //     hidden on a desktop that screen capture cannot see.
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            k!.SetValue("PromptOnSecureDesktop", 0, RegistryValueKind.DWord);
            Console.WriteLine("  UAC prompts set to appear on the normal desktop (clickable through Pane).");
        }
        catch (Exception ex) { Console.WriteLine("  Could not adjust UAC secure-desktop setting: " + ex.Message); }

        // 5. Start it now (detached), so there is no wait for the next logon.
        try
        {
            foreach (var p in Process.GetProcessesByName("PaneHost"))
                if (p.Id != Environment.ProcessId)
                    try { p.Kill(true); p.WaitForExit(2000); } catch { }
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            Console.WriteLine("  Host started.");
        }
        catch (Exception ex) { Console.WriteLine("  Could not auto-start host: " + ex.Message); }

        Console.WriteLine();
        Console.WriteLine("  ==================================================");
        Console.WriteLine("   Pane Host is installed and running.");
        Console.WriteLine();
        Console.WriteLine($"   This PC:      {Environment.MachineName}");
        Console.WriteLine($"   Pairing key:  {cfg.PairingKey}");
        Console.WriteLine();
        Console.WriteLine("   Enter this key in Pane on your other PC to connect.");
        Console.WriteLine("  ==================================================");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdministrator()) { Console.WriteLine("Uninstall must run as Administrator."); return 1; }
        Run("schtasks", $"/End /TN \"{TaskName}\"");
        Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        Run("netsh", "advfirewall firewall delete rule name=\"Pane Host TCP\"");
        Run("netsh", "advfirewall firewall delete rule name=\"Pane Host UDP\"");
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            k!.SetValue("PromptOnSecureDesktop", 1, RegistryValueKind.DWord); // restore Windows default
        }
        catch { }
        foreach (var p in Process.GetProcessesByName("PaneHost"))
            if (p.Id != Environment.ProcessId) try { p.Kill(true); } catch { }
        Console.WriteLine("Pane Host uninstalled (config left in " + HostConfig.Dir + ").");
        return 0;
    }

    private static (bool ok, string output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, o);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
