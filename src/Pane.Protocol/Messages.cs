using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pane.Protocol;

public enum MsgType : byte
{
    // Host -> Client
    ScreenInfo = 1,   // int width, int height
    Frame = 2,        // int rectCount, then per rect: int x,y,w,h, int compLen, bytes (Deflate of BGRA32)

    // Client -> Host
    MouseMove = 20,   // float nx, float ny   (0..1 across the shared screen)
    MouseButton = 21, // byte button(0=L,1=R,2=M), byte down(1/0), float nx, float ny
    MouseWheel = 22,  // int delta, byte horizontal(1/0), float nx, float ny
    Key = 23,         // ushort vk, byte down(1/0), byte extended(1/0)
    RequestFullFrame = 24,
}

/// <summary>A changed rectangle of the screen, pixels as Deflate-compressed BGRA32 (stride = w*4).</summary>
public readonly record struct FrameRect(int X, int Y, int W, int H, byte[] Compressed);

/// <summary>Minimal manual binary writer/reader for the application protocol.</summary>
public static class Messages
{
    public static byte[] ScreenInfo(int w, int h)
    {
        var b = new byte[9];
        b[0] = (byte)MsgType.ScreenInfo;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), h);
        return b;
    }

    public static byte[] Frame(IReadOnlyList<FrameRect> rects)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)MsgType.Frame);
        WriteInt(ms, rects.Count);
        foreach (var r in rects)
        {
            WriteInt(ms, r.X); WriteInt(ms, r.Y); WriteInt(ms, r.W); WriteInt(ms, r.H);
            WriteInt(ms, r.Compressed.Length);
            ms.Write(r.Compressed, 0, r.Compressed.Length);
        }
        return ms.ToArray();
    }

    public static byte[] MouseMove(float nx, float ny)
    {
        var b = new byte[9];
        b[0] = (byte)MsgType.MouseMove;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), nx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(5), ny);
        return b;
    }

    public static byte[] MouseButton(int button, bool down, float nx, float ny)
    {
        var b = new byte[11];
        b[0] = (byte)MsgType.MouseButton;
        b[1] = (byte)button;
        b[2] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(3), nx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(7), ny);
        return b;
    }

    public static byte[] MouseWheel(int delta, bool horizontal, float nx, float ny)
    {
        var b = new byte[14];
        b[0] = (byte)MsgType.MouseWheel;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), delta);
        b[5] = (byte)(horizontal ? 1 : 0);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6), nx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(10), ny);
        return b;
    }

    public static byte[] Key(ushort vk, bool down, bool extended)
    {
        var b = new byte[5];
        b[0] = (byte)MsgType.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), vk);
        b[3] = (byte)(down ? 1 : 0);
        b[4] = (byte)(extended ? 1 : 0);
        return b;
    }

    public static byte[] RequestFullFrame() => new[] { (byte)MsgType.RequestFullFrame };

    // --- readers ---

    public static MsgType TypeOf(byte[] msg) => (MsgType)msg[0];

    public static (int w, int h) ReadScreenInfo(byte[] m)
        => (BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(1)), BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(5)));

    public static List<FrameRect> ReadFrame(byte[] m)
    {
        int o = 1;
        int count = ReadInt(m, ref o);
        var list = new List<FrameRect>(count);
        for (int i = 0; i < count; i++)
        {
            int x = ReadInt(m, ref o), y = ReadInt(m, ref o), w = ReadInt(m, ref o), h = ReadInt(m, ref o);
            int len = ReadInt(m, ref o);
            var data = new byte[len];
            Buffer.BlockCopy(m, o, data, 0, len); o += len;
            list.Add(new FrameRect(x, y, w, h, data));
        }
        return list;
    }

    public static (float nx, float ny) ReadMouseMove(byte[] m)
        => (BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(1)), BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(5)));

    public static (int button, bool down, float nx, float ny) ReadMouseButton(byte[] m)
        => (m[1], m[2] == 1, BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(3)), BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(7)));

    public static (int delta, bool horizontal, float nx, float ny) ReadMouseWheel(byte[] m)
        => (BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(1)), m[5] == 1,
            BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(6)), BinaryPrimitives.ReadSingleLittleEndian(m.AsSpan(10)));

    public static (ushort vk, bool down, bool extended) ReadKey(byte[] m)
        => (BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(1)), m[3] == 1, m[4] == 1);

    private static void WriteInt(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static int ReadInt(byte[] m, ref int o)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(o));
        o += 4;
        return v;
    }
}

/// <summary>
/// UDP presence packet the host broadcasts: magic || version || hostIdGuid(16) || tcpPort(int) || nameLen(int) || nameUtf8.
/// Carries no secret, so it is safe to broadcast; the pairing key is still required to connect.
/// </summary>
public sealed record DiscoveryPacket(Guid HostId, int TcpPort, string Name)
{
    public byte[] Encode()
    {
        var name = Encoding.UTF8.GetBytes(Name);
        using var ms = new MemoryStream();
        ms.Write(Net.DiscoveryMagic);
        var tmp = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, Net.ProtocolVersion); ms.Write(tmp);
        ms.Write(HostId.ToByteArray());
        BinaryPrimitives.WriteInt32LittleEndian(tmp, TcpPort); ms.Write(tmp);
        BinaryPrimitives.WriteInt32LittleEndian(tmp, name.Length); ms.Write(tmp);
        ms.Write(name);
        return ms.ToArray();
    }

    public static bool TryParse(byte[] data, out DiscoveryPacket packet)
    {
        packet = default!;
        try
        {
            int magicLen = Net.DiscoveryMagic.Length;
            if (data.Length < magicLen + 4 + 16 + 4 + 4) return false;
            for (int i = 0; i < magicLen; i++) if (data[i] != Net.DiscoveryMagic[i]) return false;
            int o = magicLen;
            int ver = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4;
            if (ver != Net.ProtocolVersion) return false;
            var guid = new Guid(data.AsSpan(o, 16).ToArray()); o += 16;
            int port = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4;
            int nameLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4;
            if (nameLen < 0 || o + nameLen > data.Length) return false;
            var name = Encoding.UTF8.GetString(data, o, nameLen);
            packet = new DiscoveryPacket(guid, port, name);
            return true;
        }
        catch { return false; }
    }
}
