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
    private string? _clipDir;                                // batch folder for an incoming clipboard copy
    private readonly SemaphoreSlim _outgoing = new(1, 1);    // one host->client file stream at a time

    /// <summary>Largest clipboard file copy we move automatically (bigger: drag onto the screen instead).</summary>
    public const long MaxClipBytes = 512L * 1024 * 1024;

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
        await _outgoing.WaitAsync(_ct);
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
        finally { _outgoing.Release(); }
    }

    // --- clipboard files ---

    /// <summary>Send files/folders copied on this PC so the client can paste them.</summary>
    public async Task SendClipFilesAsync(IReadOnlyList<string> paths)
    {
        await _outgoing.WaitAsync(_ct);
        try
        {
            await _channel.SendAsync(Fs.HostClipBegin(), _ct);
            var names = new List<string>();
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    names.Add(Path.GetFileName(path));
                    await SendFileAsync(path, Path.GetFileName(path));
                }
                else if (Directory.Exists(path))
                {
                    var baseName = new DirectoryInfo(path.TrimEnd('\\', '/')).Name;
                    names.Add(baseName);
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        await SendFileAsync(file, Path.Combine(baseName, Path.GetRelativePath(path, file)));
                }
            }
            await _channel.SendAsync(Fs.HostClipDone(names), _ct);
        }
        catch (Exception ex) { Log.Info($"Clipboard file send failed: {ex.Message}"); }
        finally { _outgoing.Release(); }
    }

    /// <summary>Total bytes of a set of files/folders (stops counting past the clipboard cap).</summary>
    public static long MeasureBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) total += new FileInfo(p).Length;
                else if (Directory.Exists(p))
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        total += new FileInfo(f).Length;
                        if (total > MaxClipBytes) return total;
                    }
            }
            catch { }
            if (total > MaxClipBytes) return total;
        }
        return total;
    }

    /// <summary>Begin one file of a clipboard copy from the client (into a fresh temp batch folder).</summary>
    public void ClipFile(string relPath, long size)
    {
        CloseUpload();
        try
        {
            _clipDir ??= Path.Combine(Path.GetTempPath(), "Pane Clipboard", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            var dest = Path.Combine(_clipDir, Sanitize(relPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _upload = File.Create(dest);
        }
        catch (Exception ex) { Log.Info($"Clipboard file failed: {ex.Message}"); _upload = null; }
    }

    /// <summary>All clipboard files received: returns the full paths of the top-level items to put on the clipboard.</summary>
    public List<string> ClipFilesDone(string[] topNames)
    {
        CloseUpload();
        var result = new List<string>();
        var dir = _clipDir;
        _clipDir = null;
        if (dir == null) return result;
        foreach (var n in topNames)
        {
            var full = Path.Combine(dir, Sanitize(n));
            if (File.Exists(full) || Directory.Exists(full)) result.Add(full);
            else { try { Directory.CreateDirectory(full); result.Add(full); } catch { } } // empty folder
        }
        return result;
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

    // --- handoff ---

    /// <summary>Begin a handoff file, written under THIS PC's user profile at the given relative path.</summary>
    public void HandoffFile(string relFromProfile, long size)
    {
        CloseUpload();
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dest = Path.Combine(profile, Sanitize(relFromProfile));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _upload = File.Create(dest);
        }
        catch (Exception ex) { Log.Info($"Handoff file failed: {ex.Message}"); _upload = null; }
    }

    /// <summary>All handoff files received: open VS Code on the handed-off folder (rebased to this profile).</summary>
    public void HandoffFinish(string relFolderFromProfile)
    {
        CloseUpload();
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folder = Path.Combine(profile, Sanitize(relFolderFromProfile));
            LaunchVsCode(folder);
            Log.Info($"Handoff complete -> opened VS Code on {folder}");
        }
        catch (Exception ex) { Log.Info($"Handoff finish failed: {ex.Message}"); }
    }

    private static void LaunchVsCode(string folder)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userCmd = Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd");
        var sysCmd = @"C:\Program Files\Microsoft VS Code\bin\code.cmd";
        string args = File.Exists(userCmd) ? $"/c \"\"{userCmd}\" \"{folder}\"\""
                    : File.Exists(sysCmd) ? $"/c \"\"{sysCmd}\" \"{folder}\"\""
                    : $"/c code \"{folder}\""; // fall back to PATH
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", args)
        { UseShellExecute = false, CreateNoWindow = true });
    }

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
