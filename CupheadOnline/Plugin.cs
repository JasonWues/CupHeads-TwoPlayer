using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.Net;
using CupheadOnline.UI;
using CupheadOnline.Patches;
using CupheadOnline.Sync;

namespace CupheadOnline
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    [BepInProcess("Cuphead.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        // ──────────────────────────────────────────────────────────────────────
        //  Singleton
        // ──────────────────────────────────────────────────────────────────────
        public static Plugin           Instance { get; private set; }
        public static ManualLogSource  Log      { get; private set; }
        public static SteamNetManager  Net      { get; private set; }

        static ConfigEntry<bool> _cfgShowConnectionHud;
        static ConfigEntry<string> _cfgLanguage;
        static ConfigEntry<bool> _cfgVerboseLogging;
        static ConfigEntry<bool> _cfgAutoOpenSteamFriends;
        static ConfigEntry<bool> _cfgAutoExportBugReports;
        static ConfigEntry<bool> _cfgShowCreditsMenu;
        static ConfigEntry<bool> _cfgShowPauseSessionPanel;
        static ConfigEntry<bool> _cfgShowBossHealthBars;
        static ConfigEntry<bool> _cfgShowBattleAssistHud;
        static ConfigEntry<bool> _cfgEnableQoLHotkeys;
        static ConfigEntry<bool> _cfgLatencyFriendlyDamage;
        static ConfigEntry<bool> _cfgVanillaTwoPlayerOnline;
        static ConfigEntry<string> _cfgNetworkTransportMode;
        static ConfigEntry<string> _cfgLanHostAddress;
        static ConfigEntry<int> _cfgLanPort;
        static ConfigEntry<bool> _cfgAutoStartLanTransport;
        static ConfigEntry<bool> _cfgEnableLocalDevSession;
        static ConfigEntry<bool> _cfgUseLocalLoopbackPacketHarness;
        static ConfigEntry<bool> _cfgAutoRunLocalDevE2E;
        static ConfigEntry<bool> _cfgAutoRunLocalDevTutorial;
        static ConfigEntry<bool> _cfgAutoRunLanSteamE2E;
        static ConfigEntry<bool> _cfgAutoRunLanSteamE2EVisualOnly;
        static ConfigEntry<string> _cfgAutoRunLanSteamE2ETarget;
        static ConfigEntry<bool> _cfgUseSeparateSavePath;
        static ConfigEntry<string> _cfgSeparateSavePath;
        static ConfigEntry<int> _cfgLanArtificialLatencyMs;
        static ConfigEntry<int> _cfgLanArtificialJitterMs;
        static ConfigEntry<float> _cfgLanUnreliableDropPercent;
        static ConfigEntry<int> _cfgOnlineFrameRateCap;
        static ConfigEntry<bool> _cfgEnableStartupSplash;
        static ConfigEntry<bool> _cfgStartupSplashAllowSkip;
        static ConfigEntry<bool> _cfgStartupSplashStaticOverlay;
        static ConfigEntry<float> _cfgStartupSplashVolume;
        static ConfigEntry<float> _cfgStartupSplashStaticIntensity;
        static ConfigEntry<bool> _cfgBossHpScalingEnabled;
        static ConfigEntry<float> _cfgBossHpPerExtraPlayer;
        static ConfigEntry<int> _cfgPreferredPlayerColor;
        static bool _frameRatePolicyApplied;
        static int _previousTargetFrameRate;
        static int _previousVSyncCount;

        public static bool ShowConnectionHud => _cfgShowConnectionHud == null || _cfgShowConnectionHud.Value;
        public static string LanguageMode => _cfgLanguage == null ? "Auto" : (_cfgLanguage.Value ?? "Auto").Trim();
        public static bool VerboseLoggingEnabled => _cfgVerboseLogging != null && _cfgVerboseLogging.Value;
        public static bool AutoOpenSteamFriends => _cfgAutoOpenSteamFriends != null && _cfgAutoOpenSteamFriends.Value;
        public static bool AutoExportBugReports => _cfgAutoExportBugReports == null || _cfgAutoExportBugReports.Value;
        public static bool ShowCreditsMenu => _cfgShowCreditsMenu == null || _cfgShowCreditsMenu.Value;
        public static bool ShowPauseSessionPanel => _cfgShowPauseSessionPanel == null || _cfgShowPauseSessionPanel.Value;
        public static bool ShowBossHealthBars => _cfgShowBossHealthBars == null || _cfgShowBossHealthBars.Value;
        public static bool ShowBattleAssistHud => _cfgShowBattleAssistHud == null || _cfgShowBattleAssistHud.Value;
        public static bool EnableQoLHotkeys => _cfgEnableQoLHotkeys == null || _cfgEnableQoLHotkeys.Value;
        public static bool LatencyFriendlyDamage => _cfgLatencyFriendlyDamage == null || _cfgLatencyFriendlyDamage.Value;
        public static bool VanillaTwoPlayerOnline => _cfgVanillaTwoPlayerOnline == null || _cfgVanillaTwoPlayerOnline.Value;
        public static string NetworkTransportMode => _cfgNetworkTransportMode == null ? "Steam" : (_cfgNetworkTransportMode.Value ?? "Steam").Trim();
        public static string LanHostAddress => _cfgLanHostAddress == null ? "127.0.0.1" : (_cfgLanHostAddress.Value ?? "127.0.0.1").Trim();
        public static int LanPort => _cfgLanPort == null ? 7890 : Mathf.Clamp(_cfgLanPort.Value, 1, 65535);
        public static bool AutoStartLanTransport => _cfgAutoStartLanTransport != null && _cfgAutoStartLanTransport.Value;
        public static bool EnableLocalDevSession => _cfgEnableLocalDevSession != null && _cfgEnableLocalDevSession.Value;
        public static bool UseLocalLoopbackPacketHarness => _cfgUseLocalLoopbackPacketHarness == null || _cfgUseLocalLoopbackPacketHarness.Value;
        public static bool AutoRunLocalDevE2E => _cfgAutoRunLocalDevE2E != null && _cfgAutoRunLocalDevE2E.Value;
        public static bool AutoRunLocalDevTutorial => _cfgAutoRunLocalDevTutorial != null && _cfgAutoRunLocalDevTutorial.Value;
        public static bool AutoRunLanSteamE2E => _cfgAutoRunLanSteamE2E != null && _cfgAutoRunLanSteamE2E.Value;
        public static bool AutoRunLanSteamE2EVisualOnly => _cfgAutoRunLanSteamE2EVisualOnly != null && _cfgAutoRunLanSteamE2EVisualOnly.Value;
        public static string AutoRunLanSteamE2ETarget =>
            _cfgAutoRunLanSteamE2ETarget == null ? string.Empty : (_cfgAutoRunLanSteamE2ETarget.Value ?? string.Empty).Trim();
        public static bool UseSeparateSavePath => _cfgUseSeparateSavePath != null && _cfgUseSeparateSavePath.Value;
        public static string SeparateSavePath
        {
            get
            {
                string configured = _cfgSeparateSavePath == null ? null : _cfgSeparateSavePath.Value;
                if (!string.IsNullOrEmpty(configured))
                    return configured;
                return Path.Combine(Path.Combine(Paths.BepInExRootPath, "CupHeads"), "Saves");
            }
        }
        public static int LanArtificialLatencyMs => _cfgLanArtificialLatencyMs == null ? 0 : Mathf.Max(0, _cfgLanArtificialLatencyMs.Value);
        public static int LanArtificialJitterMs => _cfgLanArtificialJitterMs == null ? 0 : Mathf.Max(0, _cfgLanArtificialJitterMs.Value);
        public static float LanUnreliableDropPercent => _cfgLanUnreliableDropPercent == null ? 0f : Mathf.Clamp(_cfgLanUnreliableDropPercent.Value, 0f, 100f);
        public static int OnlineFrameRateCap => _cfgOnlineFrameRateCap == null ? 60 : Mathf.Clamp(_cfgOnlineFrameRateCap.Value, 0, 240);
        public static bool EnableStartupSplash => _cfgEnableStartupSplash == null || _cfgEnableStartupSplash.Value;
        public static bool StartupSplashAllowSkip => _cfgStartupSplashAllowSkip == null || _cfgStartupSplashAllowSkip.Value;
        public static bool StartupSplashStaticOverlay => _cfgStartupSplashStaticOverlay != null && _cfgStartupSplashStaticOverlay.Value;
        public static float StartupSplashVolume =>
            _cfgStartupSplashVolume == null ? 1f : Mathf.Clamp01(_cfgStartupSplashVolume.Value);
        public static float StartupSplashStaticIntensity =>
            _cfgStartupSplashStaticIntensity == null ? 0.28f : Mathf.Clamp01(_cfgStartupSplashStaticIntensity.Value);
        public static bool BossHpScalingEnabled => _cfgBossHpScalingEnabled != null && _cfgBossHpScalingEnabled.Value;
        public static float BossHpPerExtraPlayer =>
            _cfgBossHpPerExtraPlayer == null ? 0.35f : Mathf.Max(0f, _cfgBossHpPerExtraPlayer.Value);
        public static int PreferredPlayerColorSelection =>
            _cfgPreferredPlayerColor == null ? PlayerColorSync.AutoSelection : PlayerColorSync.NormalizeSelection(_cfgPreferredPlayerColor.Value);

        // ──────────────────────────────────────────────────────────────────────
        //  Unity lifecycle
        // ──────────────────────────────────────────────────────────────────────
        void Awake()
        {
            Instance = this;
            Log      = Logger;
            Log.LogInfo("CupHeads " + PluginInfo.VERSION + " loading\u2026");
            SceneManager.sceneLoaded += OnSceneLoaded;

            _cfgShowConnectionHud = Config.Bind("UI", "ShowConnectionHud", true,
                "Show the in-game connection HUD with status and ping quality.");
            _cfgLanguage = Config.Bind("UI", "Language", "Auto",
                new ConfigDescription(
                    "Mod UI language. Auto follows the game language. / 界面语言：Auto 跟随游戏语言，Chinese 强制中文，English 强制英文。",
                    new AcceptableValueList<string>("Auto", "English", "Chinese")));
            _cfgVerboseLogging = Config.Bind("Debug", "VerboseLogging", false,
                "Enable extra diagnostic logging for menu and network helpers.");
            _cfgAutoOpenSteamFriends = Config.Bind("UI", "AutoOpenSteamFriendsOnJoin", false,
                "Open the Steam Friends overlay immediately when Join Game is selected.");
            _cfgAutoExportBugReports = Config.Bind("Debug", "AutoExportBugReports", true,
                "Automatically create a local paired bug report after a connected multiplayer session disconnects, errors, or is shut down. This does not upload anything.");
            _cfgShowCreditsMenu = Config.Bind("UI", "ShowCreditsMenu", true,
                "Show the custom Credits entry on the title screen.");
            _cfgShowPauseSessionPanel = Config.Bind("UI", "ShowPauseSessionPanel", true,
                "Show the in-game session panel while paused, or when F8 is toggled.");
            _cfgShowBossHealthBars = Config.Bind("UI", "ShowBossHealthBars", true,
                "Show CupHeads boss health bars during battle levels.");
            _cfgShowBattleAssistHud = Config.Bind("UI", "ShowBattleAssistHud", true,
                "Show a compact battle timer/stats HUD during battle levels.");
            _cfgEnableQoLHotkeys = Config.Bind("Controls", "EnableQoLHotkeys", true,
                "Enable CupHeads hotkeys: F6 resync, F7 boss bars, F9 copy diagnostics, F10 battle HUD, F11 dev lab.");
            _cfgLatencyFriendlyDamage = Config.Bind("Networking", "LatencyFriendlyDamage", true,
                "Trust each peer for damage to their own player body. The host still owns scenes, saves, boss state, RNG, and progression.");
            _cfgVanillaTwoPlayerOnline = Config.Bind("Networking", "VanillaTwoPlayerOnline", true,
                "Use the focused two-player model: clients send input only, the host runs Cuphead's real local co-op Player Two, and host snapshots correct client visuals.");
            _cfgNetworkTransportMode = Config.Bind("Networking", "TransportMode", "Steam",
                "Network transport to use. Steam = normal Steam lobby/P2P. LanHost/LanClient = dev-only LAN emulation using the same packet handlers as Steam.");
            _cfgLanHostAddress = Config.Bind("Networking", "LanHostAddress", "127.0.0.1",
                "Host address for TransportMode=LanClient. Use 127.0.0.1 for two local processes or the host PC's LAN IP for two machines.");
            _cfgLanPort = Config.Bind("Networking", "LanPort", 7890,
                "UDP port used by the dev-only LAN Steam emulation transport.");
            _cfgAutoStartLanTransport = Config.Bind("Networking", "AutoStartLanTransport", false,
                "Automatically start LanHost or LanClient at boot when TransportMode is set to a LAN mode.");
            _cfgEnableLocalDevSession = Config.Bind("Debug", "EnableLocalDevSessionHotkey", true,
                "Enable the F11 dev lab and local simulation: Player One is local, Player Two is driven through CupHeads' remote-input path on the same PC.");
            _cfgUseLocalLoopbackPacketHarness = Config.Bind("Debug", "UseLocalLoopbackPacketHarness", true,
                "When local dev simulation is active, inject Player Two input through SteamNetManager packet serialization/dispatch instead of calling the remote input driver directly.");
            _cfgAutoRunLocalDevE2E = Config.Bind("Debug", "AutoRunLocalDevE2E", false,
                "Automatically run a local-dev save-to-map-to-boss multiplayer smoke test. Intended for a separate test copy only.");
            _cfgAutoRunLocalDevTutorial = Config.Bind("Debug", "AutoRunLocalDevTutorial", false,
                "Automatically create/use a fresh local-dev test save and load the tutorial. Intended for a separate test copy only.");
            _cfgAutoRunLanSteamE2E = Config.Bind("Debug", "AutoRunLanSteamE2E", false,
                "Automatically run a two-process LAN transport smoke test. Start one copy as LanHost and one as LanClient with AutoStartLanTransport enabled.");
            _cfgAutoRunLanSteamE2EVisualOnly = Config.Bind("Debug", "AutoRunLanSteamE2EVisualOnly", false,
                "When AutoRunLanSteamE2E is enabled, stop after a clean synced live-combat visual window instead of running revive/pause/retry checks.");
            _cfgAutoRunLanSteamE2ETarget = Config.Bind("Debug", "AutoRunLanSteamE2ETarget", "",
                "Optional Levels enum name for the LAN smoke test boss target, such as Slime, Frogs, Flower, or Veggies. Empty means nearest boss.");
            _cfgUseSeparateSavePath = Config.Bind("Debug", "UseSeparateSavePath", false,
                "Redirect Cuphead save files to a separate folder. Use this only for test copies so normal saves are not touched.");
            _cfgSeparateSavePath = Config.Bind("Debug", "SeparateSavePath", "",
                "Optional absolute folder for redirected Cuphead save files. Empty means BepInEx/CupHeads/Saves.");
            _cfgLanArtificialLatencyMs = Config.Bind("Debug", "LanArtificialLatencyMs", 0,
                "Dev-only one-way packet delay for LAN Steam-emulation tests. Use this to reproduce Steam relay latency.");
            _cfgLanArtificialJitterMs = Config.Bind("Debug", "LanArtificialJitterMs", 0,
                "Dev-only random +/- packet delay jitter for LAN Steam-emulation tests.");
            _cfgLanUnreliableDropPercent = Config.Bind("Debug", "LanUnreliableDropPercent", 0f,
                "Dev-only packet loss percentage for unreliable LAN Steam-emulation packets. Reliable packets are delayed but not dropped.");
            _cfgOnlineFrameRateCap = Config.Bind("Networking", "OnlineFrameRateCap", 60,
                "Frame-rate cap applied while a multiplayer session or LAN/Steam E2E test is active. Cuphead battle logic is frame-sensitive, so peers should use the same cap. Use 0 to disable.");
            _cfgEnableStartupSplash = Config.Bind("StartupSplash", "EnableStartupSplash", true,
                "Play BepInEx/plugins/CupheadOnline/Assets/CupHeadsIntro.mp4 over the game's startup/title intro.");
            _cfgStartupSplashAllowSkip = Config.Bind("StartupSplash", "AllowSkip", true,
                "Allow Escape, Z, Enter, Space, or controller confirm/back/start to skip the startup splash.");
            _cfgStartupSplashStaticOverlay = Config.Bind("StartupSplash", "FilmStaticOverlay", false,
                "Draw an extra live film-static overlay on top of the startup splash video. Off by default because baked-in video static looks cleaner.");
            _cfgStartupSplashVolume = Config.Bind("StartupSplash", "Volume", 1f,
                "Startup splash audio volume from 0.0 to 1.0.");
            _cfgStartupSplashStaticIntensity = Config.Bind("StartupSplash", "StaticIntensity", 0.28f,
                "Startup splash static overlay intensity from 0.0 to 1.0.");
            _cfgBossHpScalingEnabled = Config.Bind("Balance", "EnableBossHpScalingByPlayerCount", false,
                "Scale battle-level boss HP by connected player count. Disabled by default.");
            _cfgBossHpPerExtraPlayer = Config.Bind("Balance", "BossHpPerExtraPlayer", 0.35f,
                "Extra boss HP added per extra active player. Example: 0.35 means 2 players = 1.35x HP.");
            _cfgPreferredPlayerColor = Config.Bind("Cosmetics", "PreferredPlayerColor", PlayerColorSync.AutoSelection,
                "Lobby and in-game player color. 0 = Auto, 1 = Classic, 2+ = fixed tint.");

            if (AutoStartLanTransport || AutoRunLanSteamE2E)
                Application.runInBackground = true;

            // Networking manager — Steam P2P transport (lobby + invite flow)
            Net = new SteamNetManager();
            Net.OnStatusChanged += msg =>
            {
                // Animate dots on "waiting" messages; plain display otherwise.
                // Chinese statuses use 等待/正在/连接中 as the waiting markers.
                bool animate = msg.IndexOf("Waiting", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("Connecting", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("Creating", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("等待", StringComparison.Ordinal) >= 0
                            || msg.IndexOf("正在", StringComparison.Ordinal) >= 0
                            || msg.IndexOf("连接中", StringComparison.Ordinal) >= 0;
                MpMenuState.SetStatus(msg, animate);
                Log.LogInfo("[Net] " + msg);
            };
            Net.TryInitializeSteam();
            Log.LogInfo("[Mode] VanillaTwoPlayerOnline=" + VanillaTwoPlayerOnline + "; TransportMode=" + NetworkTransportMode);

            // ── Diagnostic: scan our own assembly types and expose any failures ──
            try
            {
                var types = Assembly.GetExecutingAssembly().GetTypes();
                Log.LogInfo("[Plugin] Assembly type scan OK — " + types.Length + " types.");
            }
            catch (ReflectionTypeLoadException rtle)
            {
                Log.LogError("[Plugin] === ASSEMBLY TYPE SCAN FAILURES ===");
                foreach (var le in rtle.LoaderExceptions)
                    if (le != null)
                        Log.LogError("[Plugin]   " + le.GetType().Name + ": " + le.Message);
                Log.LogError("[Plugin] === END TYPE SCAN FAILURES ===");
            }
            catch (Exception ex)
            {
                Log.LogError("[Plugin] GetTypes() threw: " + ex);
            }

            // ── Apply patches one-by-one so a single failure does not block all ─
            var harmony = new Harmony(PluginInfo.GUID);
            var registeredPatchTypes = new HashSet<Type>();

            // Core UI — SlotSelect patches inject the native MULTIPLAYER menu item
            PatchTracked(harmony, registeredPatchTypes, typeof(CloudSavePathPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(StartScreenSplashGatePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(StartScreenAudioSplashGatePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SlotSelectAwakePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SlotSelectUpdatePatch));

            // Player lifecycle
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerAwakePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputInitPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelPlayerWeaponManagerLoadoutPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapAwakePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapCreatePlayersPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerLevelInitPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(StatsLevelInitPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelStartPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelPlayAnnouncerReadyPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelTransitionInCompletePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelIntroReadyCompletePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDeathStatePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerReviveStatePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerStatsInitialStatusPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerStatsHealthChangedPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDeathEffectReviveOutOfFramePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDeathEffectExtraVisualStartPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDeathEffectExtraVisualParryPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDeathEffectExtraVisualParryAnimPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(ExtraRemoteAvatarAwakePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerCenterPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerCameraCenterPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerTopPlayerPositionPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerGetNextPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerGetRandomPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerGetFirstPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerCurrentPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerCountPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerGetAllPlayersPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerManagerBothPlayersActivePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(CupheadLevelCameraPathPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelEnemySpawnerPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelPitMoveTriggerPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(ForestPlatformingLevelChomperSpawnerPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(AbstractPlatformingLevelEnemyTriggerPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelPitExtraParticipantPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelShootingEnemyRangePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelShootingEnemyVolumesPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelShootingEnemyShootPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MountainPlatformingLevelElevatorHandlerStartPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(CircusPlatformingLevelTrampolineSleepPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MountainPlatformingLevelScaleStartPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SnowCultLevelPlatformExtraBouncePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlatformingLevelExitExtraParticipantPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelCoinExtraCollectorPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PirateLevelBarrelExtraTriggerPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(AbstractLevelInteractiveEntityExtraPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(HouseLevelExitExtraPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerStartEnumSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerStartIntSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerStartEnumCallbackSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerStartIntCallbackSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerContinueChoiceSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerContinueSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(DialoguerEndSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapDialogueInteractionClientGuardPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapDialogueInteractionStartSpeechBubbleSyncPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapLevelLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapSceneLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapShopLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapTutorialLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapBakeryLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapDiceGateSceneLoaderAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RobotLevelRobotHeadPrimaryPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessKnightLevelInitPatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessKnightCheckTauntPatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessKnightShouldBackDashPatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessBishopFixedUpdatePatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessBishopFindVerticalAnglePatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(ChessBishopFindHorizontalPositionPatch3P));
            PatchTracked(harmony, registeredPatchTypes, typeof(SallyMeteorParryPatch3P));

            // Movement / input sync
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerMotorPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapPlayerMotorPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(MapPlayerAnimationPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RewiredPlayerGetAxisPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RewiredPlayerGetButtonPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RewiredPlayerGetButtonDownPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RewiredPlayerGetButtonUpPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(CupheadInputDisplayForButtonPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputAxisPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputAxisIntPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputButtonPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputButtonDownPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerInputButtonUpPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(ProjectileTrackingPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(ParryPatch));

            // Damage authority
            PatchTracked(harmony, registeredPatchTypes, typeof(EnemyDamageHostAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(EnemyBruteForceDamageHostAuthorityPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(PlayerDamagePatch));

            // Scene transitions
            PatchTracked(harmony, registeredPatchTypes, typeof(SceneLoaderLevelsPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SceneLoaderScenesPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SlotSelectEnterGamePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelPlayerDeathStatsPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(SceneLoaderRetryStatsPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(LevelPlayerParryStatsPatch));

            // Deterministic RNG
            PatchTracked(harmony, registeredPatchTypes, typeof(RandPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RandIntPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(UnityRandomRangeFloatPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(UnityRandomRangeIntPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(UnityRandomValuePatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RandBoolPatch));
            PatchTracked(harmony, registeredPatchTypes, typeof(RandPosOrNegPatch));

            AuditPatchCoverage(registeredPatchTypes);

            Log.LogInfo("[Plugin] Patch pass complete.");
            SessionPausePanel.Ensure();
            LocalDevMenu.Ensure();
        }

        IEnumerator Start()
        {
            // Keep VideoPlayer startup out of Awake; Unity 2017 can behave badly
            // if media decoding starts while the chainloader is still patching.
            yield return null;
            yield return null;
            if (AutoStartLanTransport && Net != null && !Net.IsConnected && !Net.IsInLobby)
                Net.TryAutoStartConfiguredTransport();
            StartupSplashPlayer.TryShow();
        }

        static void PatchTracked(Harmony harmony, HashSet<Type> registeredPatchTypes, Type patchType)
        {
            if (registeredPatchTypes != null && patchType != null)
                registeredPatchTypes.Add(patchType);

            PatchSafe(harmony, patchType);
        }

        static void PatchSafe(Harmony harmony, Type patchType)
        {
            try
            {
                harmony.CreateClassProcessor(patchType).Patch();
                Log.LogInfo("[Plugin] OK: " + patchType.Name);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Plugin] SKIP " + patchType.Name + ": " + ex.Message);
            }
        }

        static void AuditPatchCoverage(HashSet<Type> registeredPatchTypes)
        {
            try
            {
                var ignoredTypes = new HashSet<string>
                {
                    nameof(MainMenuPatch),
                };

                var types = Assembly.GetExecutingAssembly().GetTypes();
                foreach (var type in types)
                {
                    if (type == null || !type.IsClass || type.Namespace != "CupheadOnline.Patches")
                        continue;
                    if (ignoredTypes.Contains(type.Name))
                        continue;
                    if (!Attribute.IsDefined(type, typeof(HarmonyPatch)))
                        continue;
                    if (registeredPatchTypes != null && registeredPatchTypes.Contains(type))
                        continue;

                    Log.LogWarning("[Plugin] Unregistered Harmony patch class detected: " + type.Name);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Plugin] Patch coverage audit failed: " + ex.Message);
            }
        }

        void Update()
        {
            ApplyFrameRatePolicy();
            MainThreadQueue.Drain();
            Net?.Poll();
            MultiplayerSession.EnsureCupheadMultiplayerState();
            LevelStartSync.Update();
            LocalDevSession.Update();
            LocalDevE2ETest.Update();
            LocalDevTutorialLauncher.Update();
            LanSteamE2ETest.Update();
            ClientInputFramePump.Update();
            LoadoutReplicator.Update();
            EnemySyncManager.HostTick();
            if (!VanillaTwoPlayerOnline)
            {
                ExtraRemoteAvatarManager.Update();
                ExtraParticipantDamageBridge.Update();
                ExtraParticipantTracker.Update();
                ExtraParticipantReviveVisuals.Update();
            }
            PlayerColorSync.Update();
            QoLHotkeys.Tick();
            BossHealthScaler.Update();
            BossHealthBarOverlay.Tick();
            BattleAssistHud.Tick();
            SessionSync.Update();
            SessionPausePanel.Ensure();
        }

        void LateUpdate()
        {
            EnemySyncManager.ClientLateTick();
        }

        static void ApplyFrameRatePolicy()
        {
            int cap = OnlineFrameRateCap;
            bool shouldApply = cap > 0
                && (MultiplayerSession.IsActive || AutoRunLanSteamE2E || AutoStartLanTransport);

            if (shouldApply)
            {
                if (!_frameRatePolicyApplied)
                {
                    _previousTargetFrameRate = Application.targetFrameRate;
                    _previousVSyncCount = QualitySettings.vSyncCount;
                    _frameRatePolicyApplied = true;
                }

                if (QualitySettings.vSyncCount != 0)
                    QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != cap)
                    Application.targetFrameRate = cap;
                return;
            }

            if (!_frameRatePolicyApplied)
                return;

            Application.targetFrameRate = _previousTargetFrameRate;
            QualitySettings.vSyncCount = _previousVSyncCount;
            _frameRatePolicyApplied = false;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            UI.MultiplayerMenuInjector.ResetOnSceneChange();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Net?.Dispose();
            MultiplayerSession.End();
            PlayerColorSync.Reset();
            BossHealthScaler.Reset();
            BossHealthBarOverlay.Hide();
            BattleAssistHud.Hide();
            LocalDevMenu.Hide();
            StartupSplashPlayer.Hide();
        }

        public static bool ToggleBossHealthBars()
        {
            if (_cfgShowBossHealthBars == null)
                return true;

            _cfgShowBossHealthBars.Value = !_cfgShowBossHealthBars.Value;
            if (!_cfgShowBossHealthBars.Value)
                BossHealthBarOverlay.Hide();
            return _cfgShowBossHealthBars.Value;
        }

        public static bool ToggleBattleAssistHud()
        {
            if (_cfgShowBattleAssistHud == null)
                return true;

            _cfgShowBattleAssistHud.Value = !_cfgShowBattleAssistHud.Value;
            if (!_cfgShowBattleAssistHud.Value)
                BattleAssistHud.Hide();
            return _cfgShowBattleAssistHud.Value;
        }

        public static void SetPreferredPlayerColorSelection(int selection)
        {
            if (_cfgPreferredPlayerColor == null)
                return;

            _cfgPreferredPlayerColor.Value = PlayerColorSync.NormalizeSelection(selection);
        }

        public static void LogVerbose(string msg)
        {
            if (VerboseLoggingEnabled && Log != null)
                Log.LogInfo("[Verbose] " + msg);
        }

        public static string BuildDiagnosticsReport()
        {
            string nl = Environment.NewLine;
            string report = "CupHeads Diagnostics" + nl
                          + "Version: " + PluginInfo.VERSION + nl
                          + "HUD Enabled: " + ShowConnectionHud + nl
                          + "Verbose Logging: " + VerboseLoggingEnabled + nl
                          + "Auto Open Steam Friends: " + AutoOpenSteamFriends + nl
                          + "Auto Export Bug Reports: " + AutoExportBugReports + nl
                          + "Show Credits Menu: " + ShowCreditsMenu + nl
                          + "Show Pause Session Panel: " + ShowPauseSessionPanel + nl
                          + "Show Boss Health Bars: " + ShowBossHealthBars + nl
                          + "Show Battle Assist HUD: " + ShowBattleAssistHud + nl
                          + "QoL Hotkeys Enabled: " + EnableQoLHotkeys + nl
                          + "Latency Friendly Damage: " + LatencyFriendlyDamage + nl
                          + "Transport Mode: " + NetworkTransportMode + nl
                          + "LAN Host Address: " + LanHostAddress + nl
                          + "LAN Port: " + LanPort + nl
                          + "LAN Auto Start: " + AutoStartLanTransport + nl
                          + "Local Dev Session Enabled: " + EnableLocalDevSession + nl
                          + "Local Dev Session Active: " + LocalDevSession.IsActive + nl
                          + "Startup Splash Enabled: " + EnableStartupSplash + nl
                          + "Startup Splash Video: " + (StartupSplashPlayer.ResolveVideoPath() ?? "missing") + nl
                          + "Startup Splash Static: " + StartupSplashStaticOverlay + nl
                          + "Startup Splash Volume: " + StartupSplashVolume.ToString("0.00") + nl
                          + "Boss HP Scaling Enabled: " + BossHpScalingEnabled + nl
                          + "Boss HP Per Extra Player: " + BossHpPerExtraPlayer.ToString("0.00") + nl
                          + BossHealthScaler.GetStatusSummary() + nl;

            if (Net != null)
                report += nl + Net.BuildDiagnosticsReport();
            else
                report += nl + "Network: not initialized";

            return report.TrimEnd();
        }
    }

    internal static class PluginInfo
    {
        public const string GUID    = "com.cupheadonline.mod";
        public const string NAME    = "CupHeads";
        public const string VERSION = "1.2.46";
    }
}
