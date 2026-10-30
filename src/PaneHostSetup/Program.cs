using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// One-click installer for Pane Host. Windowless: it extracts the embedded host, runs it with --install
// (copy to ProgramData, register the logon task, open the firewall, set UAC auto-approve, start it),
// then shows the pairing key in a dialog. No console window appears at all.

try
{
    var asm = Assembly.GetExecutingAssembly();
    using var res = asm.GetManifestResourceStream("PaneHost.exe");
    if (res == null)
    {
        Msg("This setup build is missing the host payload.\nRebuild with scripts/build.ps1.", "Pane Host Setup");
        return 1;
    }

    var dir = Path.Combine(Path.GetTempPath(), "PaneSetup");
    Directory.CreateDirectory(dir);
    var hostPath = Path.Combine(dir, "PaneHost.exe");
    using (var fs = File.Create(hostPath)) res.CopyTo(fs);

    var psi = new ProcessStartInfo(hostPath, "--install")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();

    // Read the pairing key straight from the host's config (reliable regardless of console capture).
    string key = "(run PaneHost.exe --key to see it)";
    try
    {
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                               "PaneHost", "config.json");
        var m = Regex.Match(File.ReadAllText(cfg), "\"PairingKey\"\\s*:\\s*\"([^\"]+)\"");
        if (m.Success) key = m.Groups[1].Value;
    }
    catch { }

    if (p.ExitCode == 0)
        Msg($"Pane Host is installed and running on {Environment.MachineName}.\n\n" +
            $"Pairing key:\n{key}\n\n" +
            "Enter this key in Pane on your other PC to connect.\n" +
            "(Tip: press Ctrl+C to copy this message.)", "Pane Host — Installed");
    else
        Msg("The install reported a problem:\n\n" + output, "Pane Host Setup");

    return p.ExitCode;
}
catch (Exception ex)
{
    Msg("Install failed:\n\n" + ex.Message, "Pane Host Setup");
    return 1;
}

static void Msg(string text, string caption) => Native.MessageBoxW(IntPtr.Zero, text, caption, 0x40);

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
