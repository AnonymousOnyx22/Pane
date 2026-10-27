using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pane;

/// <summary>A remote PC the user has added. Online status is runtime-only, driven by UDP discovery.</summary>
public sealed class SavedPc : INotifyPropertyChanged
{
    public string Id { get; set; } = "";       // host GUID when known from discovery, else ""
    public string Name { get; set; } = "PC";
    public string Host { get; set; } = "";      // IP or hostname
    public string Key { get; set; } = "";       // pairing key, Crockford base32 as shown by the host

    private bool _online;
    [JsonIgnore]
    public bool Online
    {
        get => _online;
        set { if (_online != value) { _online = value; OnChanged(nameof(Online)); OnChanged(nameof(StatusText)); } }
    }

    [JsonIgnore] public string StatusText => Online ? "online" : "offline";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Loads and saves the user's PC list under %APPDATA%\Pane\pcs.json.</summary>
public static class PcStore
{
    private static string Dir =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Pane");
    private static string FilePath => Path.Combine(Dir, "pcs.json");

    public static List<SavedPc> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SavedPc>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(IEnumerable<SavedPc> pcs)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(pcs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
