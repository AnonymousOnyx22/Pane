using System.Collections.Generic;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Host side of file transfer: lists the remote filesystem, streams downloads to the client, and writes
/// uploads into "Pane Transfers" on the interactive user's Desktop. The client is already authenticated
/// with the pairing key, so reads of any path it asks for are allowed; uploads are confined to the drop
/// folder (path traversal stripped).
/// </summary>
public sealed class FileService
{
    private readonly SecureChannel _channel;
    private readonly CancellationToken _ct;
    private FileStream? _upload;

    public static string DropFolder
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(desktop, "Pane Transfers");
        }
    }

    public FileService(SecureChannel channel, CancellationToken ct) { _channel = channel; _ct = ct; }

    public async Task ListDirAsync(string path)
    {
        try
        {
            var entries = new List<FsEntry>();
            if (string.IsNullOrEmpty(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.IsReady) entries.Add(new FsEntry(d.Name, true, 0));
            }
            else
            {
                foreach (var dir in Directory.GetDirectories(path))
                    entries.Add(new FsEntry(Path.GetFileName(dir), true, 0));
                foreach (var file in Directory.GetFiles(path))
                {
                    long size = 0; try { size = new FileInfo(file).Length; } catch { }
                    entries.Add(new FsEntry(Path.GetFileName(file), false, size));
                }
            }
            entries.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            await _channel.SendAsync(Fs.DirListing(path, entries), _ct);
        }
        catch (Exception ex) { await Err(ex.Message); }
    }

    public async Task DownloadAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                await SendFileAsync(path, Path.GetFileName(path));
            }
            else if (Directory.Exists(path))
            {
                var baseName = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
                if (string.IsNullOrEmpty(baseName)) baseName = "download";
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.Combine(baseName, Path.GetRelativePath(path, file));
                    await SendFileAsync(file, rel);
                }
            }
            else { await Err("Not found: " + path); return; }
            await _channel.SendAsync(Fs.TransferDone(), _ct);
        }
        catch (Exception ex) { await Err(ex.Message); }
    }

    private async Task SendFileAsync(string fullPath, string relPath)
    {
        using var fs = File.OpenRead(fullPath);
        await _channel.SendAsync(Fs.FileBegin(relPath, fs.Length), _ct);
        var buf = new byte[Fs.ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), _ct)) > 0)
            await _channel.SendAsync(Fs.FileChunk(buf, n), _ct);
        await _channel.SendAsync(Fs.FileEnd(), _ct);
    }

    public void UploadBegin(string relPath, long size)
    {
        CloseUpload();
        try
        {
            var dest = Path.Combine(DropFolder, Sanitize(relPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _upload = File.Create(dest);
        }
        catch (Exception ex) { Log.Info($"Upload begin failed: {ex.Message}"); _upload = null; }
    }

    public void UploadChunk(ArraySegment<byte> data)
    {
        try { _upload?.Write(data.Array!, data.Offset, data.Count); }
        catch (Exception ex) { Log.Info($"Upload write failed: {ex.Message}"); CloseUpload(); }
    }

    public void UploadEnd() => CloseUpload();

    private void CloseUpload() { try { _upload?.Dispose(); } catch { } _upload = null; }

    private async Task Err(string msg) { try { await _channel.SendAsync(Fs.FsError(msg), _ct); } catch { } }

    /// <summary>Confine an uploaded relative path to the drop folder: drop any root/drive and .. segments.</summary>
    private static string Sanitize(string relPath)
    {
        relPath = relPath.Replace('/', '\\');
        var parts = new List<string>();
        foreach (var raw in relPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            if (seg == "." || seg == "..") continue;
            if (seg.Length >= 2 && seg[1] == ':') continue; // drive like "C:"
            foreach (var c in Path.GetInvalidFileNameChars()) seg = seg.Replace(c, '_');
            if (seg.Length > 0) parts.Add(seg);
        }
        var result = string.Join('\\', parts);
        return string.IsNullOrEmpty(result) ? "upload.bin" : result;
    }
}
