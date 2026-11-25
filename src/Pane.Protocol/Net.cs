using System;

namespace Pane.Protocol;

/// <summary>Shared wire constants for Pane and Pane Host.</summary>
public static class Net
{
    /// <summary>TCP port the host listens on for the encrypted screen/input session.</summary>
    public const int TcpPort = 48901;

    /// <summary>
    /// TCP port for one-time pairing. The channel is encrypted with a well-known secret (so it is NOT
    /// authenticated); the host's owner approves each request with an on-screen Allow/Deny box, then the
    /// host hands the client its real pairing key, which the client stores. Nothing is typed.
    /// </summary>
    public const int PairPort = 48902;

    /// <summary>UDP port the host broadcasts presence on and the client listens on.</summary>
    public const int DiscoveryPort = 48900;

    /// <summary>Bumped when the wire format changes incompatibly. v2 = JPEG frames (was Deflated BGRA).</summary>
    public const int ProtocolVersion = 2;

    /// <summary>Magic prefix for discovery broadcast packets.</summary>
    public static readonly byte[] DiscoveryMagic = "PANEv1\0\0"u8.ToArray();
}
