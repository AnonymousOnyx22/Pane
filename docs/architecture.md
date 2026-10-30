# Pane architecture

Three projects: a shared protocol library, the host, and the client. The setup project is a thin
installer wrapper around the host.

## Connection lifecycle

1. **Discovery.** The host broadcasts a UDP presence packet on port 48900 every 2s
   (`magic || version || hostId || tcpPort || name`). It carries no secret. The client listens and shows
   live hosts under "Found on your network" and drives the online/offline dots on saved tiles.
2. **Connect.** The client opens TCP 48901 to the host.
3. **Handshake** (`SecureChannel`). Ephemeral ECDH (NIST P-256) on both sides; each side proves knowledge
   of the pairing key with an HMAC over the two public keys. A shared secret is derived, then HKDF (salt =
   pairing key) yields two directional AES-256-GCM keys. An attacker without the key cannot complete it.
4. **Session.** The host sends `ScreenInfo`, then streams `Frame` messages (only changed rectangles). The
   client streams input messages (mouse move/button/wheel, key). Every message is one AES-GCM frame with a
   per-direction counter nonce.

## Host capture path

`ScreenCapture` uses the Desktop Duplication API (via Vortice.Direct3D11/DXGI): acquire frame -> copy the
desktop texture to a CPU-readable staging texture -> read the dirty (and move-destination) rectangles ->
pack each as tight BGRA32 -> **JPEG-encode** (GDI+, quality 80). JPEG is ~10-60x smaller than Deflated raw
BGRA for real desktop content, which is what keeps large changes (dragging/maximizing a window) smooth.
When a frame has many scattered dirty rects (e.g. a window drag), they are coalesced into one bounding
region. The first frame, and any after access loss (resolution change, UAC secure desktop, lock screen),
is sent whole; access loss is handled by re-initializing without dropping the session.

## Host input path

`InputInjector` maps the client's normalized (0..1) coordinates onto the captured monitor's screen rect,
then into the 0..65535 virtual-desktop space `SendInput` wants for absolute moves. Keys are injected by
virtual-key code with the extended-key flag set for the keys that need it.

## Client rendering

`RemoteView` holds a `WriteableBitmap` sized to the host screen. Each received rectangle is inflated and
blitted with `WritePixels`. The `Image` is `Uniform`-stretched; pointer coordinates are converted back to
normalized values accounting for letterboxing before being sent.

## Security summary

- Mutual authentication by pairing key (HMAC), forward secrecy via ephemeral ECDH.
- AES-256-GCM, separate key per direction, monotonic counter nonces (no reuse).
- Pairing key never transmitted and never broadcast; it only authenticates the handshake.

## Why self-contained single-file

The target PC may not have .NET installed, so all exes are published self-contained. The setup embeds the
host exe as a resource so the installer is genuinely one double-clickable file.
