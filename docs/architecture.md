# Pane architecture

Four projects: a shared protocol library (`Pane.Protocol`), a shared media library (`Pane.Media`: H.264
encode/decode over Media Foundation, NV12 colour conversion, WASAPI audio), the host, and the client. The
setup project is a thin installer wrapper around the host.

## Connection lifecycle

1. **Discovery.** The host broadcasts a UDP presence packet on port 48900 every 2s
   (`magic || version || hostId || tcpPort || name`). It carries no secret. The client listens and shows
   live hosts under "Found on your network" and drives the online/offline dots on saved tiles.
2. **Connect.** The client opens TCP 48901 to the host.
3. **Handshake** (`SecureChannel`). Ephemeral ECDH (NIST P-256) on both sides; each side proves knowledge
   of the pairing key with an HMAC over the two public keys. A shared secret is derived, then HKDF (salt =
   pairing key) yields two directional AES-256-GCM keys. An attacker without the key cannot complete it.
4. **Session.** The host sends `ScreenInfo` and `MonitorList`, then streams the screen. The client asks
   for H.264 (`SetCodec`) when it can decode it and for sound (`SetAudio`); it streams input messages
   (mouse move/button/wheel, key). Every message is one AES-GCM frame with a per-direction counter nonce.

## Video: H.264 (default) and JPEG (fallback)

- **H.264.** Each new desktop image is converted BGRA -> NV12 (full-range BT.709, parallel) straight from
  the mapped staging texture and fed to a Media Foundation encoder. `H264Encoder` tries hardware first
  (NVENC / Quick Sync / AMF are *async* MFTs: an event-pump thread answers NeedInput/HaveOutput) and
  self-tests it with one frame; otherwise it uses Microsoft's software encoder (sync MFT). Low-latency
  mode, CBR, no B-frames. Each access unit is one `VideoFrame` message. When the screen goes still the
  host re-encodes the last image ~30 more times so the picture sharpens instead of staying soft. A new
  encoder always starts on an IDR, so keyframes (new client, monitor switch, quality change) are made by
  recreating the encoder. Bitrate follows the Quality preset (12 / 30 / 60 Mbps).
- **Client decode.** Microsoft's H.264 decoder MFT (low-latency) -> NV12 -> BGRA on the receive thread,
  into a small pool of frame buffers. If the UI thread is 2+ frames behind, pictures are decoded (to keep
  references intact) but not converted or queued, so latency never builds up.
- **JPEG.** The original path: dirty rectangles as JPEG tiles (tall regions split into strips and encoded
  in parallel). Used if either side cannot do H.264, and by the default loopback test.

## Monitors, audio, clipboard

- **Monitors.** `MonitorList` lists every attached display; `SelectMonitor` makes the capture thread
  rebuild Desktop Duplication on that output. The host then resends `ScreenInfo` + `MonitorList` and
  retargets input; the client recreates its bitmap.
- **Audio.** WASAPI loopback of the host's default output, converted to PCM16 stereo and sent as `Audio`
  messages (~190 KB/s). The client plays them through a jitter buffer that is trimmed back past ~150 ms.
  Sessions to 127.0.0.1 start muted to avoid an echo loop.
- **Clipboard.** Both sides poll the clipboard sequence number. Priority: files, then text, then image.
  Text is `Clipboard`, images are PNG in `ClipboardImage`, files stream into a temp batch folder on the
  receiving side (`ClipFile`... `ClipFilesDone` one way, `HostClipBegin` / file stream / `HostClipDone`
  the other) and are then put on that PC's clipboard as a file drop, so Ctrl+V in Explorer pastes them.
  Anything a side writes is remembered by sequence number so it is never echoed back. Copies over 512 MB
  are skipped (drag them onto the screen instead).

## Host capture path

`ScreenCapture` uses the Desktop Duplication API (via Vortice.Direct3D11/DXGI): acquire frame -> copy the
desktop texture to a CPU-readable staging texture -> read the dirty (and move-destination) rectangles ->
pack each as tight BGRA32 -> **JPEG-encode** (GDI+, quality 80). JPEG is ~10-60x smaller than Deflated raw
BGRA for real desktop content. (This is the fallback path; H.264, above, is the default.)
When a frame has many scattered dirty rects (e.g. a window drag), they are coalesced into one bounding
region. The first frame, and any after access loss (resolution change, UAC secure desktop, lock screen),
is sent whole; access loss is handled by re-initializing without dropping the session.

## Host input path

`InputInjector` maps the client's normalized (0..1) coordinates onto the captured monitor's screen rect,
then into the 0..65535 virtual-desktop space `SendInput` wants for absolute moves. Keys are injected by
virtual-key code with the extended-key flag set for the keys that need it.

## Client rendering

`RemoteView` holds a `WriteableBitmap` sized to the host screen. Decoded pixels (a full H.264 picture or
JPEG rectangles) arrive already in BGRA and are blitted with `WritePixels`. The `Image` is `Uniform`-stretched; pointer coordinates are converted back to
normalized values accounting for letterboxing before being sent.

## Security summary

- Mutual authentication by pairing key (HMAC), forward secrecy via ephemeral ECDH.
- AES-256-GCM, separate key per direction, monotonic counter nonces (no reuse).
- Pairing key never transmitted and never broadcast; it only authenticates the handshake.

## Why self-contained single-file

The target PC may not have .NET installed, so all exes are published self-contained. The setup embeds the
host exe as a resource so the installer is genuinely one double-clickable file.
