using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Pane.Protocol;
using PaneHost;

// Entry point. Modes:
//   --install     copy to ProgramData, register logon task, open firewall, start, print key
//   --uninstall   remove task + firewall rules, stop host
//   --key         print the pairing key for this host
//   (no args)     run as the background host service (headless: WinExe, no console window)

if (args.Contains("--install") || args.Contains("--uninstall") || args.Contains("--key"))
{
    EnsureConsole(); // attach to the caller's console so these commands can print
    if (args.Contains("--install")) return Installer.Install();
    if (args.Contains("--uninstall")) return Installer.Uninstall();
    Console.WriteLine(HostConfig.LoadOrCreate().PairingKey);
    return 0;
}

static void EnsureConsole()
{
    // If stdout is already a pipe/file (the GUI setup captures it) writes go there — do nothing.
    if (Console.IsOutputRedirected) return;
    // If we already inherited a console from a console parent, use it (no new window).
    if (ConsoleNative.GetConsoleWindow() != IntPtr.Zero) return;
    // Otherwise attach to the launching terminal, or make one only if there truly is none.
    if (!ConsoleNative.AttachConsole(-1)) ConsoleNative.AllocConsole(); // -1 = ATTACH_PARENT_PROCESS
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
}

// Single instance guard.
using var mutex = new Mutex(initiallyOwned: true, "Global\\PaneHostSingleton", out bool isNew);
if (!isNew)
{
    Log.Info("Another Pane Host instance is already running; exiting.");
    return 0;
}

var cfg = HostConfig.LoadOrCreate();
Log.Info($"Pane Host starting. Machine='{Environment.MachineName}' HostId={cfg.Id}");
Log.Info($"Pairing key (permanent): {cfg.PairingKey}");

using var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; appCts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => appCts.Cancel();

var discovery = new DiscoveryBroadcaster(cfg.Id, Net.TcpPort, Environment.MachineName);
_ = discovery.RunAsync(appCts.Token);

var listener = new TcpListener(IPAddress.Any, Net.TcpPort);
listener.Start();
Log.Info($"Listening for clients on TCP {Net.TcpPort}.");

// One active session at a time; a new connection replaces the old one.
CancellationTokenSource? sessionCts = null;

try
{
    while (!appCts.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(appCts.Token); }
        catch (OperationCanceledException) { break; }

        sessionCts?.Cancel();
        sessionCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
        var myCts = sessionCts;
        _ = Task.Run(async () =>
        {
            using var session = new Session(client, cfg.KeyBytes);
            try { await session.RunAsync(myCts.Token); }
            catch (Exception ex) { Log.Info($"Session ended: {ex.Message}"); }
        });
    }
}
finally
{
    listener.Stop();
    Log.Info("Pane Host stopped.");
}
return 0;

static class ConsoleNative
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AllocConsole();
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
}
