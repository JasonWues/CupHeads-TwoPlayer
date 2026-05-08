# CupHeads Work In Progress

Steam P2P multiplayer for Cuphead, focused on a more stable two-player online co-op path, plus a desktop installer that handles the mod setup for you.

## About this fork

This repository is built from [Germanized/CupHeads](https://github.com/Germanized/CupHeads). The original project explored broader Cuphead multiplayer support, including larger lobby and extra-participant experiments. This fork intentionally narrows the default gameplay target back to Cuphead's native two-player co-op shape.

The main difference is the focused `VanillaTwoPlayerOnline` mode: the host runs Cuphead's real local co-op `PlayerOne` and `PlayerTwo`, while the guest sends input frames that drive the host's `PlayerTwo`. By favoring a strict two-player model over experimental extra players, this fork avoids several classes of boss-script, map-avatar, death/revive, and UI bugs that appear when the game is pushed beyond the two player slots it was built around.

In short: fewer players by design, fewer fragile custom scene patches in the main path.

## What this repo contains

- `CupheadOnline/` - the BepInEx + Harmony mod
- `CupheadInstaller/` - the Electron installer app
- `build.ps1` - root build script that builds the mod and packages the Electron installer

## Features

### Multiplayer

- Steam P2P transport built for Cuphead through BepInEx + Harmony
- Two-player focused online mode enabled by default: clients send input only, and the host runs Cuphead's real local co-op Player Two
- Host, join, invite friend, retry last action, and export bug reports
- Host-led lobby flow with integrated `SAVE SLOT`, `LEAD`, and `START GAME` actions
- Host-authoritative scene syncing for level and menu transitions
- Host-authoritative client drift correction so the guest's local player gets gently pulled back to the host's true gameplay position instead of splitting into a separate solo run
- Automatic host scene-follow so guests are pulled into the selected save/map when the host starts
- Networked overworld handoff that spawns both native players, remaps guest controls onto Player Two, and syncs map movement before entering a level
- Synced overworld NPC dialogue so speech bubbles open and advance together across host and guest
- Universal input routing so the local player can switch between keyboard and controller at any time without restarting the session
- Safer remote button-edge handling so jump, dash, confirm, cancel, and menu actions fire once per input press instead of repeating across packet gaps
- Hybrid co-op authority: the host keeps world, scene, save, boss, RNG, and progression authority, while latency-friendly damage lets each player own hits to their own body
- Remote menu input routing so guests can drive Player Two interactions in overworld prompts, equip cards, and shop-style internal menus while the host remains authoritative
- Defensive stale-packet guards for save selection, host snapshots, weapon events, revive grants, damage events, scene loads, and status updates
- Save compatibility checks with warnings for mismatched progress, DLC state, or setup
- Live connection HUD with role, status, session info, and sync warnings
- In-game session panel with `F8` toggle for quick diagnostics and session state
- Optional boss health bars during battle levels, using Cuphead's live boss health data when available
- Battle Assist HUD with a live fight timer, deaths, retries, parries, and optional boss HP multiplier readout
- QoL hotkeys: `F6` quick resync, `F7` boss health bars, `F9` copy diagnostics, `F10` Battle Assist HUD, and `F11` Dev Lab
- Local Dev Lab with a same-PC simulator, letting one PC test the mod's remote-input path by driving Player Two through CupHeads instead of needing a second Steam player
- Packet-loopback test harness for local dev mode, so simulated guest input is serialized and dispatched through `SteamNetManager` before it controls Player Two
- Optional startup splash video with audio, skip support, and a configurable Cuphead-style film-static overlay
- Optional boss HP scaling per extra active player, configurable in BepInEx and disabled by default
- Recovery and resync tools for active sessions, including exported diagnostics bundles

### Menu and UX

- Lobby-style multiplayer screen injected into Slot Select
- Cleaner roster, status, and action layout with Steam readiness shown in-menu
- Runtime player color swatches with in-game tinting and no sprite-file recolor work
- Dedicated credits screen
- Clear Steam readiness and connection status messaging
- Clipboard helpers for lobby IDs and diagnostics
- Better retry and reconnect guidance when Steam sessions fail
- Toggleable gameplay HUD features through the BepInEx config, including boss bars, Battle Assist, QoL hotkeys, and the session panel
- Toggleable networking and dev-test settings through the BepInEx config, including latency-friendly damage and the F11 Dev Lab hotkey
- Toggleable startup splash settings through the BepInEx config, including volume, skip support, and static overlay intensity

### Two-Player Focus

- This fork defaults to the native Cuphead two-player model.
- Extra-player and larger-lobby code paths from the broader project are not the primary supported path here.
- The tradeoff is deliberate: fewer simultaneous players, but fewer broken boss scripts, fewer fake-avatar edge cases, and a cleaner remote-input model.

### Characters and DLC

- Cuphead and Mugman are both supported in the main co-op flow
- Ms. Chalice is supported when the Delicious Last Course DLC is installed and her charm/loadout is available
- Save compatibility checks look at DLC world access and DLC progression before the host starts a run

## Current Gameplay Limit

- Vanilla Cuphead is still deeply built around `PlayerOne` and `PlayerTwo`
- This fork does not try to make every Cuphead scene support unlimited players
- The supported goal is stable online co-op for two players, matching the base game's local co-op assumptions as closely as possible

### Installer

- Automatic Cuphead detection through Steam
- Automatic BepInEx repair or installation when needed
- Repair-style installs that always refresh the bundled `CupheadOnline.dll`
- Automatic cleanup of obsolete `LiteNetLib.dll` leftovers from older builds
- Final verification after install so the setup is checked before you launch
- One-click mod installation
- Portable desktop installer
- Built-in verify, open-folder, and launch-Steam helpers

## Requirements

- Windows 10 or later
- Cuphead installed through Steam
- Internet connection only if the bundled BepInEx repair package is missing and the installer has to fall back to GitHub

## Quick Start

1. Download `CupHeads.exe` from [Releases](https://github.com/Adityaki37/CupHeads-TwoPlayer/releases).
2. Run the installer.
3. Let it detect your Cuphead folder, or browse to it manually.
4. Click Install.
5. Launch Cuphead through Steam.

If you test outside the Steam launcher, you may need a `steam_appid.txt` next to `Cuphead.exe`.

## How installation works

The Electron installer:

- detects your Cuphead install
- repairs or installs BepInEx 5 from a bundled repair package if it is missing or damaged
- always refreshes `CupheadOnline.dll` in `Cuphead\\BepInEx\\plugins\\CupheadOnline\\`
- refreshes the optional startup splash video in `Cuphead\\BepInEx\\plugins\\CupheadOnline\\Assets\\`
- removes stale `LiteNetLib.dll` leftovers from the older UDP transport
- verifies the final install before finishing, including checking that the installed DLL contains the same CupHeads version as the installer

## Custom Startup Splash

To replace the intro, put your MP4 in the installer source at:

```text
CupheadInstaller/assets/StartupSplash/CupHeadsIntro.mp4
```

The installer copies it into Cuphead as:

```text
BepInEx/plugins/CupheadOnline/Assets/CupHeadsIntro.mp4
```

Recommended export settings are MP4/H.264 video, AAC audio, 30 FPS, 16:9, and around 3-8 seconds. Avoid HEVC/H.265 exports because Cuphead's Unity 2017 video player usually cannot decode them. The mod pauses and silences Cuphead while the splash plays so it behaves like a real boot card; baked-in static is recommended, and the optional generated static overlay is off by default.

If the BepInEx log says an older version such as `CupHeads 1.2.18 loading...`, the old DLL is still installed. Close Cuphead completely, download the newest `CupHeads.exe` from Releases, run Install again, and confirm the next log says the current release version. As an emergency bypass, set `EnableStartupSplash = false` under `[StartupSplash]` in `BepInEx/config/com.cupheadonline.mod.cfg`.

The mod then patches the game's menu and gameplay flow through Harmony and uses Steamworks P2P for multiplayer.

## Multiplayer Start Flow

1. Host opens `MULTIPLAYER` and creates or joins a Steam lobby.
2. Once connected, the host picks `SAVE SLOT` and `LEAD` directly inside the multiplayer lobby.
3. The guest stays in the lobby and follows the host automatically.
4. The host presses `START GAME` from the multiplayer lobby to launch the run.

Character choice follows Cuphead's native co-op model: the host chooses the lead character, and the guest becomes the opposite character. The base game does not expose a separate fully independent guest character picker.

If the guest sees `REQUEST HOST SAVE`, press it once to ask the host for a fresh save sync. The host also rebroadcasts the selected save automatically while the lobby is open.

When the host starts, CupHeads sends an explicit launch scene and keeps watching host snapshots. If the guest falls behind or misses the first launch packet, the mod attempts to auto-follow the host scene instead of requiring a manual resync.

During the Steam session, guests are mapped onto Cuphead's native Player Two slot. CupHeads remaps the guest's normal keyboard/controller input to that slot automatically, so the guest should move as Mugman on the map instead of loading as a frozen solo Cuphead.

Keyboard and controller are both treated as live local inputs while a session is active. If a controller wakes up mid-run or Rewired assigns it to the other vanilla slot, CupHeads still routes it back to the local Steam player and keeps remote Player Two actions driven by the guest's Steam input frames.

Internal scenes such as Porkrind's shop use the same routing path: the host remains the authority for scene transitions, while guest menu buttons are forwarded so Player Two can back out or interact instead of getting stuck on a mismatched local menu.

## Local Dev Testing

Press `F11` to open the CupHeads Dev Lab. This does not create a Steam lobby. Instead, it can mark Player One as the local host player and Player Two as the network-controlled participant.

By default, local dev mode uses a packet-loopback harness: simulated Player Two input is serialized into CupHeads packets, injected through `SteamNetManager`, dispatched like a guest packet, and only then applied to Player Two. This is closer to a real Steam guest than calling the remote input driver directly, while still being fast enough to run on one PC.

Recommended solo test flow:

1. Launch Cuphead and press `F11`.
2. Choose `START + OPEN SAVE SELECT`.
3. Pick a normal save file.
4. Use Player One controls for Cuphead.
5. Use Player Two/controller bindings for Mugman.
6. Press `F11` again and choose `STOP SIMULATOR` when finished.

The Dev Lab also has `START SIMULATOR`, `COPY DIAGNOSTICS`, and `CLOSE` actions. `START SIMULATOR` keeps you in the current scene, while `START + OPEN SAVE SELECT` starts the simulator and jumps to the save-select flow.

This mode is meant for auditing movement, map interactions, equip/shop menus, death/revive behavior, and boss fights without waiting for a second tester. It is not a full substitute for a real Steam P2P test because it does not prove Steam overlay invites, lobby identity, real packet relay, real latency, or two separate machines.

Two-window testing note:

1. Two Cuphead windows on one Steam account are not a reliable proof of Steam P2P because both processes share one Steam identity.
2. Use two Windows users/Steam accounts or a second PC for true P2P testing.
3. If the Dev Lab fails to spawn or control Player Two, the bug is in local spawn/input routing. If Dev Lab works but Steam multiplayer fails, the bug is probably lobby, packet, or scene-sync related.

## Paired Bug Reports

CupHeads automatically creates a local paired report after a connected multiplayer session disconnects, errors, or is shut down. The manual `EXPORT BUG REPORT` action still exists if you want to capture the current state on demand.

Each exported folder contains `pairing.txt`, `diagnostics.txt`, and `LogOutput.log`; reports from the same lobby share the same `Pairing Key`. Automatic reports are stored under `BepInEx/CupHeads/Reports` with names that start with `auto-report-`.

To merge both sides locally:

```powershell
.\tools\pair-cuphead-reports.ps1 -ReportA "C:\path\to\host-report" -ReportB "C:\path\to\guest-report"
```

The script writes a paired summary plus filtered high-signal logs. CupHeads v1.2.34 and newer also adds timestamped `[PairLog]` entries for network state changes, which makes the host and guest timelines easier to line up.

## Authority Model

CupHeads uses a hybrid co-op model. The host remains authoritative for world state: saves, scene transitions, level starts, boss/enemy state, RNG seeds, progression, retries, and menu flow. For player feel, `Networking.LatencyFriendlyDamage` is enabled by default so each peer owns damage to their own player body. In practical terms, a guest who dodges on their own screen should not lose HP purely because the host saw an older lagged position.

If you need to compare behavior against the older strict host-authoritative damage model, set `LatencyFriendlyDamage = false` under `[Networking]` in `BepInEx/config/com.cupheadonline.mod.cfg`.

## Building from source

### Build the mod only

```powershell
dotnet build .\CupheadOnline\CupheadOnline.csproj -c Release
```

### Build the full release package

```powershell
.\build.ps1 -Release
```

That produces:

- `dist\CupheadOnline.dll`
- `dist\CupHeads.exe`

### Build the installer manually

```powershell
cd .\CupheadInstaller
npm install
npm run dist
```

The packaged installer is written to `CupheadInstaller\dist\CupHeads.exe`.

## Tech stack

- BepInEx 5
- Harmony 2
- Steamworks P2P
- Electron + Node.js

## Credits

- Built from [Germanized/CupHeads](https://github.com/Germanized/CupHeads)
- Germanized and Sh0kr for the original mod foundation
- Made for Daniel
- Special thanks to Internallinked
- BepInEx for the mod framework
- Harmony for patching
- Electron for the installer shell
