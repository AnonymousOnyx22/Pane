<p align="center">
  <img src="assets/pane-logo.png" width="112" alt="Pane logo"/>
</p>

<h1 align="center">Pane</h1>

<p align="center"><b>A minimal black-and-white remote desktop for Windows.</b><br/>
Two tiny apps that are entirely yours — encrypted, pairing-key gated, and LAN-only.</p>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-111111">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%20|%2011-111111">
  <img alt="UI" src="https://img.shields.io/badge/UI-WPF-111111">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-111111">
</p>

---

A minimal, custom Windows remote desktop built as two small programs:

- **Pane** — the client app (WPF, .NET 8). Frameless pure black-and-white UI with tabs. The first tab is
  a grid of rounded tiles, one per PC, each with an online dot. Click a tile to open that PC's live screen
  in a new tab with full mouse, keyboard and scroll. Saved PCs persist between launches.
- **Pane Host** — a tiny background program for the PC you want to control (works on Windows 11 Home,
  which has no built-in Remote Desktop host). It captures the primary monitor with the Desktop Duplication
  API, sends only the changed screen regions as compressed frames over an encrypted TCP connection, and
  injects mouse/keyboard with SendInput. It announces itself over UDP so Pane finds it automatically.

Everything is encrypted (ephemeral ECDH + AES-256-GCM) and gated by a **pairing key**, so nothing else on
the network can connect.

## Install the host (on the PC you want to control)

1. Copy **`dist\PaneHostSetup.exe`** to that PC and double-click it. Approve the UAC prompt.
2. It copies the host to `C:\ProgramData\PaneHost`, registers a highest-privilege logon task (so it starts
   automatically and elevated at every login), opens the firewall for its two ports, starts it, and prints
   the **pairing key**. Copy that key.

The pairing key is **permanent** — generated once and stored in `C:\ProgramData\PaneHost\config.json`, reused
across every restart and reconnect. To see it again at any time, run `PaneHost.exe --key`, or read the latest
`C:\ProgramData\PaneHost\panehost.log` (the key is logged on each start).

## Use the client (on this PC)

1. Run **`dist\Pane.exe`**.
2. The host usually appears under **Found on your network** — click it, paste the pairing key, Add.
   Or click the **+** tile and enter the other PC's IP/hostname and key by hand.
3. Click the tile to connect. Close the tab to return to the grid.

## Build from source

Requires the .NET 8 SDK.

```powershell
pwsh scripts/build.ps1      # publishes single-file x64 exes into dist\
```

Produces `dist\Pane.exe`, `dist\PaneHost.exe`, and `dist\PaneHostSetup.exe` (the setup embeds the host).

## Test

```powershell
# Start the host, then run the headless end-to-end protocol test against it:
dist\PaneHost.exe                                   # background host (Ctrl+C to stop)
$key = dist\PaneHost.exe --key
dotnet run --project tests/LoopbackTest -- 127.0.0.1 48901 $key
```

The test covers the encrypted handshake, pairing-key rejection, screen info, a real decoded frame, and input.

## Ports

- **TCP 48901** — encrypted screen + input session.
- **UDP 48900** — host presence broadcast / client discovery.

## Uninstall the host

Run `PaneHost.exe --uninstall` (elevated): removes the scheduled task and firewall rules and stops the host.
Config is left in `C:\ProgramData\PaneHost` so your pairing key survives a reinstall.

## Notes & limits

- Captures the **primary monitor** only (by design, for now).
- Frames are raw BGRA compressed with Deflate — excellent on a LAN; a near-static desktop frame is tiny.
- Injecting into elevated windows needs the host elevated, which the logon task provides.
- `Ctrl+Alt+Del` cannot be injected (Windows reserves the Secure Attention Sequence).

See [docs/architecture.md](docs/architecture.md) for how it fits together.
