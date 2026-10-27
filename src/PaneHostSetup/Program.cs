using System.Diagnostics;
using System.Reflection;

// One-click installer for Pane Host. It carries the host exe as an embedded resource, extracts it to
// a temp folder, and runs it with --install (copy to ProgramData, register the logon task, open the
// firewall, start it, print the pairing key). This process is elevated via its manifest, so the host
// install step inherits administrator rights.

Console.Title = "Pane Host Setup";
Console.WriteLine("Pane Host Setup");
Console.WriteLine("===============");
Console.WriteLine();

try
{
    var asm = Assembly.GetExecutingAssembly();
    using var res = asm.GetManifestResourceStream("PaneHost.exe");
    if (res == null)
    {
        Console.WriteLine("ERROR: this setup build does not contain the host payload.");
        Console.WriteLine("Rebuild with scripts/build.ps1, which stages the host before publishing the setup.");
        return Pause(1);
    }

    var dir = Path.Combine(Path.GetTempPath(), "PaneSetup");
    Directory.CreateDirectory(dir);
    var hostPath = Path.Combine(dir, "PaneHost.exe");
    using (var fs = File.Create(hostPath))
        res.CopyTo(fs);

    Console.WriteLine("Running install...");
    Console.WriteLine();

    var psi = new ProcessStartInfo(hostPath, "--install") { UseShellExecute = false };
    using var p = Process.Start(psi)!;
    p.WaitForExit();

    Console.WriteLine();
    return Pause(p.ExitCode);
}
catch (Exception ex)
{
    Console.WriteLine("Install failed: " + ex.Message);
    return Pause(1);
}

static int Pause(int code)
{
    Console.WriteLine();
    Console.WriteLine("Press any key to close...");
    try { Console.ReadKey(true); } catch { }
    return code;
}
