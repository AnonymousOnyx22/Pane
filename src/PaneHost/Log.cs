namespace PaneHost;

/// <summary>Tiny logger: writes to the console and to a rolling-ish file under ProgramData.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(HostConfig.Dir, "panehost.log");

    public static void Info(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            try
            {
                Directory.CreateDirectory(HostConfig.Dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
