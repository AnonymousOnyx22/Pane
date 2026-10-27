using System;
using System.Security.Cryptography;

namespace Pane.Protocol;

/// <summary>
/// A pairing key is 20 random bytes shared between a host and the client.
/// It is shown to the user as Crockford base32 in groups of four, e.g. "K7Q2-9F3M-...".
/// </summary>
public static class PairingKey
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford, no I L O U
    public const int RawLength = 20;

    public static byte[] Generate()
    {
        var b = new byte[RawLength];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    public static string Encode(byte[] raw)
    {
        // 5 bits per char. Encode continuously, then dash every 4 chars for readability.
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var by in raw)
        {
            buffer = (buffer << 8) | by;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);

        var raw32 = sb.ToString();
        var grouped = new System.Text.StringBuilder();
        for (int i = 0; i < raw32.Length; i++)
        {
            if (i > 0 && i % 4 == 0) grouped.Append('-');
            grouped.Append(raw32[i]);
        }
        return grouped.ToString();
    }

    /// <summary>Parse a user-typed key back to raw bytes. Tolerant of case, dashes, spaces and O/0, I/1, L/1 confusions.</summary>
    public static bool TryDecode(string? text, out byte[] raw)
    {
        raw = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        int buffer = 0, bits = 0;
        var outBytes = new System.Collections.Generic.List<byte>();
        foreach (var ch in text.ToUpperInvariant())
        {
            char c = ch switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                'U' => 'V',
                _ => ch
            };
            if (c == '-' || c == ' ' || c == '\t') continue;
            int v = Alphabet.IndexOf(c);
            if (v < 0) return false;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                outBytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        if (outBytes.Count != RawLength) return false;
        raw = outBytes.ToArray();
        return true;
    }
}
