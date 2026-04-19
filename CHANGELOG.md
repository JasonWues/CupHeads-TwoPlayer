# Changelog

## v1.2.8 - 2026-04-19

- Added universal local input routing so the active player can switch between keyboard and controller at any time during maps, levels, equip cards, and menus.
- Routed host-side remote menu input through Steam input frames, allowing guest button presses to drive Player Two interactions in overworld bubbles and shop-style internal menus.
- Added fallback glyph text for Player Two prompts so interaction bubbles no longer render with an empty key when Rewired has no direct binding for that slot.

## v1.2.7 - 2026-04-19

- Narrowed guest input remapping so only spawned PlayerInput components swap to the guest's local controls, avoiding global menu/input side effects.
- Added host-authoritative scene-load gating so connected guests cannot accidentally launch their own local map or level transition while waiting for host sync.
- Added stale allowance and disconnect guards around scene, movement, weapon, damage, death/revive, and loadout sync sends for cleaner teardown and recovery behavior.

## v1.2.6 - 2026-04-19

- Fixed Steam multiplayer launches landing both players in separate solo overworld sessions by forcing Cuphead's native two-player flag before map and level player spawning.
- Added client input-slot remapping so Steam guests control the Player Two/Mugman slot with their normal keyboard or controller instead of being stuck on an unassigned local Player Two input.
- Added overworld map player state sync so host and guest map avatars can see each other moving before entering a boss or level.

## v1.2.5 - 2026-04-19

- Added an explicit host launch scene packet when `START GAME` is pressed so guests are pulled into the selected save/map immediately.
- Added automatic client scene-follow from host snapshots, so scene mismatch recovery now tries to move the guest to the host scene instead of requiring manual resync.
- Improved the installer DLL-refresh failure message so blocked writes explain to close Cuphead/Steam and run the installer as Administrator instead of showing a raw `copyfile` error.

## v1.2.4 - 2026-04-19

- Removed the guest ready-up gate from multiplayer starts. The host can now press `START GAME` once a save slot is selected, and the guest follows automatically.
- Simplified the guest lobby flow so guests wait for the host instead of needing to ready up.
- Kept `REQUEST HOST SAVE` as a manual fallback when the guest has not received the host's selected save yet.

## v1.2.3 - 2026-04-19

- Fixed a multiplayer lobby soft-block where the guest could stay stuck on `WAIT FOR SAVE` and never get a `READY UP` action after the host picked a save.
- Added host-side save-selection rebroadcasts while waiting for guest ready, so missed or early save-sync packets are repaired automatically.
- Changed the guest lobby action from a dead `WAIT FOR SAVE` row into `REQUEST HOST SAVE`, giving guests a manual resync button if the host selection has not arrived yet.

## v1.2.2 - 2026-04-19

- Reworked the integrated multiplayer start flow so the host now chooses `SAVE SLOT`, `LEAD`, and `START GAME` directly from the lobby instead of bouncing through Cuphead's solo save menu.
- Fixed the host save-selection sync so choosing the same save again no longer clears the guest's ready state right before launch.
- Improved the guest-ready gate so connected sessions can actually start once the host selection is locked in and the guest has readied up.
- Updated the README to document the new host-led lobby flow and the current character-selection behavior.

## v1.2.1 - 2026-04-19

- Fixed the Electron installer's BepInEx repair flow so it resolves the current Windows asset names instead of falling back to an outdated 404 URL.
- Bundled the BepInEx x64 and x86 repair archives directly into the installer so normal installs no longer depend on a live GitHub download.
- Added stronger installer timeout and progress handling around the BepInEx fallback download path so stalled downloads fail clearly instead of looking frozen.
- Updated the installer copy and README to reflect the new bundled BepInEx repair behavior.

## v1.2.0 - 2026-04-17

- Added optional boss HP scaling by active player count for battle levels, with a configurable per-extra-player modifier in the BepInEx config and the feature disabled by default.
- Added character-aware session diagnostics and HUD details so sessions can report Cuphead, Mugman, and Ms. Chalice more clearly, including DLC-aware save summaries.
- Added boss-scaling status to exported diagnostics and session panels so balancing issues are easier to verify and report.
- Updated the README to document DLC character support, optional boss scaling, and the current hard limit that live gameplay still follows Cuphead's native two-player runtime.

## v1.1.1 - 2026-04-17

- Upgraded the Electron installer so every install acts like a repair pass: it refreshes the bundled `CupheadOnline.dll`, repairs BepInEx only when needed, and verifies the final setup before finishing.
- Added automatic cleanup for legacy `LiteNetLib.dll` leftovers from the older UDP transport so stale files do not stick around in the plugin folder or packaged `dist` output.
- Polished the installer UI with clearer repair/update messaging, install-plan summaries, final verification feedback, and better wording around what the installer actually refreshes.
- Updated the README feature list and install notes to match the current save-sync, session, and self-repair installer workflow.

## v1.1.0 - 2026-04-17

- Added host/guest save compatibility checks so the mod now compares the chosen slot, map progress, DLC usage, coins, and completion before a run starts.
- Added periodic host session snapshots for reconnect recovery and lightweight desync warnings, plus stronger host-side enemy state broadcasting during live play.
- Added a new in-game session panel that can be viewed while paused or toggled with `F8`, including session stage, save/sync status, and local deaths, retries, and parries.
- Upgraded the connection HUD and multiplayer menu with live stage summaries, compatibility warnings, and richer session diagnostics.
- Added run-stat tracking patches for retries, parries, and player deaths to make playtesting and debugging easier.

## v1.0.3 - 2026-04-17

- Added a host-led `OPEN SAVE SLOT` flow so the multiplayer connection can stay alive while the host returns to Cuphead's normal save selection screen.
- Added save-slot and non-level scene sync so the guest follows the host into the chosen map or intro flow more reliably.
- Added a `COPY LOBBY ID` action in the multiplayer menu and automatically copy the lobby ID to the clipboard when a host lobby is created.
- Added a live title-screen footer that shows the mod version and whether the session is connected, waiting in lobby, or ready for the host to press `Start`.
- Upgraded the in-game connection HUD to show host/client role, peer context, lobby shorthand, and session elapsed time alongside ping quality.

## v1.0.2 - 2026-04-17

- Fixed the multiplayer submenu back behavior so `BACK`, `Escape`, and controller `B` cleanly return to the main menu instead of appearing to refresh the submenu.
- Added a permanent multiplayer hint that explicitly says to press `Escape` or controller `B` to go back.

## v1.0.1 - 2026-04-17

- Fixed Steam startup guards so Steam P2P polling only runs after Steamworks is initialized and stops spamming errors when the game is not launched through Steam.
- Fixed the Slot Select reflection crash by resolving the live `SlotSelectScreen` instance before reading non-static fields.
- Reworked the multiplayer and credits menus so they render reliably, respect back/cancel input, and no longer soft-lock or hide the screen.
- Fixed the credits screen formatting by switching it to explicit line placement instead of fragile multiline layout behavior in Unity 2017 UI.
- Fixed the multiplayer submenu layout so the actions stack correctly instead of overlapping on top of each other.
- Added clearer Steam status, richer connection HUD feedback, retry/invite/diagnostics actions, and safer host/join state transitions.
- Removed the obsolete legacy installer artifact and standardized the repo on the Electron web installer workflow.
- Added installer utility actions for opening the Cuphead folder, launching Steam, and verifying the install.
