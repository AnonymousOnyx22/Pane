using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pane.Protocol;

/// <summary>One entry in a remote directory listing.</summary>
public readonly record struct FsEntry(string Name, bool IsDir, long Size);

/// <summary>
/// Builders and readers for the file-transfer messages. Files move as chunked streams:
/// FileBegin(relPath,size) -> FileChunk(bytes)* -> FileEnd, and a Download of a folder sends several
/// such file streams (each with its path relative to the folder) followed by TransferDone.
/// </summary>
public static class Fs
{
    public const int ChunkSize = 1 << 20; // 1 MiB

    // --- Client -> Host ---

    public static byte[] ListDir(string path) => Tagged(MsgType.ListDir, w => WriteString(w, path));
    public static byte[] Download(string path) => Tagged(MsgType.Download, w => WriteString(w, path));
    public static byte[] UploadBegin(string relPath, long size) =>
        Tagged(MsgType.UploadBegin, w => { WriteString(w, relPath); WriteLong(w, size); });
    public static byte[] UploadChunk(byte[] data, int len) => Raw(MsgType.UploadChunk, data, len);
    public static byte[] UploadEnd() => new[] { (byte)MsgType.UploadEnd };

    // --- Host -> Client ---

    public static byte[] DirListing(string path, IReadOnlyList<FsEntry> entries) => Tagged(MsgType.DirListing, w =>
    {
        WriteString(w, path);
        WriteInt(w, entries.Count);
        foreach (var e in entries) { WriteString(w, e.Name); w.WriteByte((byte)(e.IsDir ? 1 : 0)); WriteLong(w, e.Size); }
    });
    public static byte[] FileBegin(string relPath, long size) =>
        Tagged(MsgType.FileBegin, w => { WriteString(w, relPath); WriteLong(w, size); });
    public static byte[] FileChunk(byte[] data, int len) => Raw(MsgType.FileChunk, data, len);
    public static byte[] FileEnd() => new[] { (byte)MsgType.FileEnd };
    public static byte[] TransferDone() => new[] { (byte)MsgType.TransferDone };
    public static byte[] FsError(string message) => Tagged(MsgType.FsError, w => WriteString(w, message));

    // --- readers ---

    public static string ReadPath(byte[] m) { int o = 1; return ReadString(m, ref o); }
    public static string ReadError(byte[] m) { int o = 1; return ReadString(m, ref o); }

    public static (string relPath, long size) ReadBegin(byte[] m)
    { int o = 1; var p = ReadString(m, ref o); var s = ReadLong(m, ref o); return (p, s); }

    /// <summary>Returns the chunk bytes (the message payload after the 1-byte tag).</summary>
    public static ArraySegment<byte> ReadChunk(byte[] m) => new(m, 1, m.Length - 1);

    public static (string path, List<FsEntry> entries) ReadListing(byte[] m)
    {
        int o = 1;
        var path = ReadString(m, ref o);
        int count = ReadInt(m, ref o);
        var list = new List<FsEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var name = ReadString(m, ref o);
            bool isDir = m[o++] == 1;
            long size = ReadLong(m, ref o);
            list.Add(new FsEntry(name, isDir, size));
        }
        return (path, list);
    }

    // --- primitives ---

    private static byte[] Tagged(MsgType t, Action<MemoryStream> body)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)t);
        body(ms);
        return ms.ToArray();
    }

    private static byte[] Raw(MsgType t, byte[] data, int len)
    {
        var b = new byte[1 + len];
        b[0] = (byte)t;
        Buffer.BlockCopy(data, 0, b, 1, len);
        return b;
    }

    private static void WriteString(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v ?? "");
        WriteInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }
    private static void WriteInt(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteLong(Stream s, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }

    private static string ReadString(byte[] m, ref int o)
    {
        int len = ReadInt(m, ref o);
        var s = Encoding.UTF8.GetString(m, o, len);
        o += len;
        return s;
    }
    private static int ReadInt(byte[] m, ref int o) { int v = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(o)); o += 4; return v; }
    private static long ReadLong(byte[] m, ref int o) { long v = BinaryPrimitives.ReadInt64LittleEndian(m.AsSpan(o)); o += 8; return v; }
}
