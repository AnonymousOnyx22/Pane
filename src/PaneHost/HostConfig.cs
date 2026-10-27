using System.Text.Json;
using PKey = Pane.Protocol.PairingKey;

namespace PaneHost;

/// <summary>Persistent host identity and pairing key, stored machine-wide under ProgramData.</summary>
public sealed class HostConfig
{
    public string HostId { get; set; } = Guid.NewGuid().ToString();
    public string PairingKey { get; set; } = PKey.Encode(PKey.Generate());

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PaneHost");
    public static string Path_ => System.IO.Path.Combine(Dir, "config.json");

    public Guid Id => Guid.Parse(HostId);

    public byte[] KeyBytes
    {
        get
        {
            if (PKey.TryDecode(PairingKey, out var raw)) return raw;
            throw new InvalidOperationException("Stored pairing key is corrupt.");
        }
    }

    public static HostConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var cfg = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(Path_));
                if (cfg != null && PKey.TryDecode(cfg.PairingKey, out _)) return cfg;
            }
        }
        catch { /* fall through to fresh config */ }

        var fresh = new HostConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
