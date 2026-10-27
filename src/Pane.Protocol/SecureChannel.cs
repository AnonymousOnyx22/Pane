using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Pane.Protocol;

/// <summary>
/// An authenticated, encrypted message channel over a byte stream (typically a TCP NetworkStream).
///
/// Handshake (all frames length-prefixed, 4-byte big-endian):
///   1. client -> server : clientPub (ECDH P-256 SubjectPublicKeyInfo)
///   2. server -> client : serverPub || serverAuth   (serverAuth = HMAC(key, clientPub||serverPub))
///   3. client -> server : clientAuth                (clientAuth = HMAC(key, serverPub||clientPub))
/// Both sides derive a shared secret via ECDH, then HKDF(salt = pairing key) to two 32-byte
/// directional AES-256-GCM keys. Knowledge of the pairing key is proven by the HMAC tags, so an
/// attacker without the key cannot complete the handshake.
///
/// After the handshake every application message is one AES-GCM frame:
///   [4-byte BE length of (ciphertext+tag)] [ciphertext] [16-byte tag]
/// The 12-byte nonce is a per-direction monotonic counter (never transmitted, never reused because
/// each direction has its own key and counter).
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _recvCipher;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int MaxFrame = 64 * 1024 * 1024; // 64 MiB ceiling per message

    private SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _sendCipher = new AesGcm(sendKey, TagSize);
        _recvCipher = new AesGcm(recvKey, TagSize);
    }

    public static Task<SecureChannel> ClientHandshakeAsync(Stream s, byte[] pairingKey, CancellationToken ct)
        => HandshakeAsync(s, pairingKey, isClient: true, ct);

    public static Task<SecureChannel> ServerHandshakeAsync(Stream s, byte[] pairingKey, CancellationToken ct)
        => HandshakeAsync(s, pairingKey, isClient: false, ct);

    private static async Task<SecureChannel> HandshakeAsync(Stream s, byte[] pairingKey, bool isClient, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] myPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        byte[] clientPub, serverPub;

        if (isClient)
        {
            await WriteRawAsync(s, myPub, ct);
            clientPub = myPub;
            var serverMsg = await ReadRawAsync(s, ct);
            if (serverMsg.Length <= 32) throw new IOException("Bad server hello.");
            int pubLen = serverMsg.Length - 32;
            serverPub = serverMsg[..pubLen];
            var serverAuth = serverMsg[pubLen..];
            var expected = Hmac(pairingKey, clientPub, serverPub);
            if (!CryptographicOperations.FixedTimeEquals(serverAuth, expected))
                throw new CryptographicException("Host rejected: wrong pairing key.");
            await WriteRawAsync(s, Hmac(pairingKey, serverPub, clientPub), ct);
        }
        else
        {
            clientPub = await ReadRawAsync(s, ct);
            serverPub = myPub;
            var hello = new byte[serverPub.Length + 32];
            Buffer.BlockCopy(serverPub, 0, hello, 0, serverPub.Length);
            Buffer.BlockCopy(Hmac(pairingKey, clientPub, serverPub), 0, hello, serverPub.Length, 32);
            await WriteRawAsync(s, hello, ct);
            var clientAuth = await ReadRawAsync(s, ct);
            var expected = Hmac(pairingKey, serverPub, clientPub);
            if (clientAuth.Length != 32 || !CryptographicOperations.FixedTimeEquals(clientAuth, expected))
                throw new CryptographicException("Client failed pairing-key authentication.");
        }

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(isClient ? serverPub : clientPub, out _);
        byte[] ikm = ecdh.DeriveKeyFromHash(peerKey.PublicKey, HashAlgorithmName.SHA256);

        // Two directional keys. "c2s" is used by the client to send / the server to receive.
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, pairingKey, "pane-channel-v1"u8.ToArray());
        byte[] c2s = okm[..32];
        byte[] s2c = okm[32..];
        return isClient ? new SecureChannel(s, c2s, s2c) : new SecureChannel(s, s2c, c2s);
    }

    private static byte[] Hmac(byte[] key, byte[] a, byte[] b)
    {
        using var h = new HMACSHA256(key);
        h.TransformBlock(a, 0, a.Length, null, 0);
        h.TransformFinalBlock(b, 0, b.Length);
        return h.Hash!;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var nonce = new byte[NonceSize];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _sendCounter++);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            _sendCipher.Encrypt(nonce, plaintext.Span, cipher, tag);

            var frame = new byte[4 + cipher.Length + TagSize];
            BinaryPrimitives.WriteInt32BigEndian(frame, cipher.Length + TagSize);
            Buffer.BlockCopy(cipher, 0, frame, 4, cipher.Length);
            Buffer.BlockCopy(tag, 0, frame, 4 + cipher.Length, TagSize);
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Reads one message. Returns null on a clean end of stream.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadFullAsync(_stream, lenBuf, ct)) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < TagSize || len > MaxFrame) throw new IOException($"Bad frame length {len}.");

        var frame = new byte[len];
        if (!await ReadFullAsync(_stream, frame, ct)) throw new EndOfStreamException();
        int cipherLen = len - TagSize;
        var nonce = new byte[NonceSize];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _recvCounter++);
        var plain = new byte[cipherLen];
        _recvCipher.Decrypt(nonce, frame.AsSpan(0, cipherLen), frame.AsSpan(cipherLen, TagSize), plain);
        return plain;
    }

    private static async Task WriteRawAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(data, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadRawAsync(Stream s, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadFullAsync(s, lenBuf, ct)) throw new EndOfStreamException();
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > 4096) throw new IOException("Bad handshake frame.");
        var buf = new byte[len];
        if (!await ReadFullAsync(s, buf, ct)) throw new EndOfStreamException();
        return buf;
    }

    private static async Task<bool> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return off == 0 ? false : throw new EndOfStreamException();
            off += n;
        }
        return true;
    }

    public void Dispose()
    {
        _sendCipher.Dispose();
        _recvCipher.Dispose();
        _sendLock.Dispose();
    }
}
