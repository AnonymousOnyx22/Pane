# Pane
A custom Windows remote desktop: a WPF client (Pane) + a background host (Pane Host) for a PC
without built-in Remote Desktop. Encrypted, pairing-key gated, LAN auto-discovery.

## Run
- Build all: `pwsh scripts/build.ps1` -> single-file exes in `dist\`
- Host (service): `dist\PaneHost.exe`   | install: `PaneHostSetup.exe` (elevated) | key: `PaneHost.exe --key`
- Client: `dist\Pane.exe`
- .NET 8 SDK lives at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (not on PATH globally).

## Test
`dotnet run --project tests/LoopbackTest -- 127.0.0.1 48901 <key>`  (start the host first)

## Folder map
- src/Pane.Protocol/ - shared wire protocol: SecureChannel (ECDH+AES-GCM), Messages, PairingKey, Discovery
- src/PaneHost/ - host: ScreenCapture (Desktop Duplication/Vortice), InputInjector (SendInput), Session, Installer
- src/Pane/ - WPF client: MainWindow (tabs+intro), GridView (tiles), RemoteView (live screen+input), dialogs
- src/PaneHostSetup/ - one-click installer that embeds the published host exe and runs it with --install
- tests/LoopbackTest/ - headless end-to-end protocol check
- scripts/build.ps1 - publishes all three exes; stages host into the setup's embed/ folder
- docs/ - architecture notes

## Rules
- Ports: TCP 48901 (session), UDP 48900 (discovery). Pairing key is permanent in C:\ProgramData\PaneHost\config.json.
- Host + client are x64, self-contained single-file. Host/client/setup all carry a PerMonitorV2 DPI manifest.
- AES-GCM nonces are per-direction counters with separate keys — never reuse a key+nonce; keep that invariant.
- Don't commit dist/ or src/PaneHostSetup/embed/*.exe (generated). Never commit a real pairing key.

## Status
- Working end to end on loopback (handshake, frames, input, wrong-key rejection all pass). Primary monitor only.
