# CupHeads

Steam P2P multiplayer for Cuphead, plus a desktop installer that handles the mod setup for you.

## What this repo contains

- `CupheadOnline/` - the BepInEx + Harmony mod
- `CupheadInstaller/` - the Electron installer app
- `build.ps1` - root build script that builds the mod and packages the Electron installer

## Features

### Multiplayer

- Steam P2P transport built for Cuphead through BepInEx + Harmony
- Host, join, invite friend, retry last action, and export bug reports
- Host-led lobby flow with integrated `SAVE SLOT`, `LEAD`, and `START GAME` actions
- Host-authoritative scene syncing for level and menu transitions
- Automatic host scene-follow so guests are pulled into the selected save/map when the host starts
- Networked overworld handoff that spawns both native players, remaps guest controls onto Player Two, and syncs map movement before entering a level
- Save compatibility checks with warnings for mismatched progress, DLC state, or setup
- Live connection HUD with role, status, session info, and sync warnings
- In-game session panel with `F8` toggle for quick diagnostics and session state
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

### Experimental Expanded Sessions

- Larger Steam lobbies with tracked extra participants beyond the vanilla two-player setup
- Runtime extra-player avatars, extra-participant HUD summaries, and experimental revive / damage bridges
- Shared targeting, camera, and several platforming / boss hooks patched to be more 3+-aware
- This part of the mod is still experimental: Cuphead has many bespoke scene scripts, so not every fight or event is guaranteed to behave perfectly with extra active participants

### Characters and DLC

- Cuphead and Mugman are both supported in the main co-op flow
- Ms. Chalice is supported when the Delicious Last Course DLC is installed and her charm/loadout is available
- Save compatibility checks look at DLC world access and DLC progression before the host starts a run

## Current Gameplay Limit

- Vanilla Cuphead is still deeply built around `PlayerOne` and `PlayerTwo`
- CupHeads now includes experimental extra-participant support, but it does not magically turn every Cuphead scene into a fully solved unlimited-player game
- The more custom a boss, cutscene, or scripted event is, the more likely it still needs scene-specific work

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

1. Download `CupHeads.exe` from [Releases](https://github.com/Germanized/CupHeads/releases).
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
- removes stale `LiteNetLib.dll` leftovers from the older UDP transport
- verifies the final install before finishing

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

- Germanized and Sh0kr for the mod
- Made for Daniel
- Special thanks to Internallinked
- BepInEx for the mod framework
- Harmony for patching
- Electron for the installer shell
