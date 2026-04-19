using System;
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
        static ConfigEntry<bool> _cfgVerboseLogging;
        static ConfigEntry<bool> _cfgAutoOpenSteamFriends;
        static ConfigEntry<bool> _cfgShowCreditsMenu;
        static ConfigEntry<bool> _cfgShowPauseSessionPanel;
        static ConfigEntry<bool> _cfgBossHpScalingEnabled;
        static ConfigEntry<float> _cfgBossHpPerExtraPlayer;
        static ConfigEntry<int> _cfgPreferredPlayerColor;

        public static bool ShowConnectionHud => _cfgShowConnectionHud == null || _cfgShowConnectionHud.Value;
        public static bool VerboseLoggingEnabled => _cfgVerboseLogging != null && _cfgVerboseLogging.Value;
        public static bool AutoOpenSteamFriends => _cfgAutoOpenSteamFriends != null && _cfgAutoOpenSteamFriends.Value;
        public static bool ShowCreditsMenu => _cfgShowCreditsMenu == null || _cfgShowCreditsMenu.Value;
        public static bool ShowPauseSessionPanel => _cfgShowPauseSessionPanel == null || _cfgShowPauseSessionPanel.Value;
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
            _cfgVerboseLogging = Config.Bind("Debug", "VerboseLogging", false,
                "Enable extra diagnostic logging for menu and network helpers.");
            _cfgAutoOpenSteamFriends = Config.Bind("UI", "AutoOpenSteamFriendsOnJoin", false,
                "Open the Steam Friends overlay immediately when Join Game is selected.");
            _cfgShowCreditsMenu = Config.Bind("UI", "ShowCreditsMenu", true,
                "Show the custom Credits entry on the title screen.");
            _cfgShowPauseSessionPanel = Config.Bind("UI", "ShowPauseSessionPanel", true,
                "Show the in-game session panel while paused, or when F8 is toggled.");
            _cfgBossHpScalingEnabled = Config.Bind("Balance", "EnableBossHpScalingByPlayerCount", false,
                "Scale battle-level boss HP by connected player count. Disabled by default.");
            _cfgBossHpPerExtraPlayer = Config.Bind("Balance", "BossHpPerExtraPlayer", 0.35f,
                "Extra boss HP added per extra active player. Example: 0.35 means 2 players = 1.35x HP.");
            _cfgPreferredPlayerColor = Config.Bind("Cosmetics", "PreferredPlayerColor", PlayerColorSync.AutoSelection,
                "Lobby and in-game player color. 0 = Auto, 1 = Classic, 2+ = fixed tint.");

            // Networking manager — Steam P2P transport (lobby + invite flow)
            Net = new SteamNetManager();
            Net.OnStatusChanged += msg =>
            {
                // Animate dots on "waiting" messages; plain display otherwise
                bool animate = msg.IndexOf("Waiting", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("Connecting", StringComparison.OrdinalIgnoreCase) >= 0
                            || msg.IndexOf("Creating", StringComparison.OrdinalIgnoreCase) >= 0;
                MpMenuState.SetStatus(msg, animate);
                Log.LogInfo("[Net] " + msg);
            };
            Net.TryInitializeSteam();

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

            // Core UI — SlotSelect patches inject the native MULTIPLAYER menu item
            PatchSafe(harmony, typeof(SlotSelectAwakePatch));
            PatchSafe(harmony, typeof(SlotSelectUpdatePatch));

            // Player lifecycle
            PatchSafe(harmony, typeof(PlayerManagerAwakePatch));
            PatchSafe(harmony, typeof(PlayerLevelInitPatch));
            PatchSafe(harmony, typeof(StatsLevelInitPatch));
            PatchSafe(harmony, typeof(LevelStartPatch));
            PatchSafe(harmony, typeof(PlayerDeathStatePatch));
            PatchSafe(harmony, typeof(PlayerReviveStatePatch));
            PatchSafe(harmony, typeof(PlayerStatsInitialStatusPatch));
            PatchSafe(harmony, typeof(PlayerStatsHealthChangedPatch));
            PatchSafe(harmony, typeof(PlayerDeathEffectReviveOutOfFramePatch));
            PatchSafe(harmony, typeof(PlayerDeathEffectExtraVisualStartPatch));
            PatchSafe(harmony, typeof(PlayerDeathEffectExtraVisualParryPatch));
            PatchSafe(harmony, typeof(PlayerDeathEffectExtraVisualParryAnimPatch));
            PatchSafe(harmony, typeof(ExtraRemoteAvatarAwakePatch));
            PatchSafe(harmony, typeof(PlayerManagerCenterPatch));
            PatchSafe(harmony, typeof(PlayerManagerCameraCenterPatch));
            PatchSafe(harmony, typeof(PlayerManagerTopPlayerPositionPatch));
            PatchSafe(harmony, typeof(PlayerManagerGetNextPatch));
            PatchSafe(harmony, typeof(PlayerManagerGetRandomPatch));
            PatchSafe(harmony, typeof(PlayerManagerGetFirstPatch));
            PatchSafe(harmony, typeof(PlayerManagerCurrentPatch));
            PatchSafe(harmony, typeof(PlayerManagerCountPatch));
            PatchSafe(harmony, typeof(PlayerManagerGetAllPlayersPatch));
            PatchSafe(harmony, typeof(PlayerManagerBothPlayersActivePatch));
            PatchSafe(harmony, typeof(CupheadLevelCameraPathPatch));
            PatchSafe(harmony, typeof(PlatformingLevelEnemySpawnerPatch));
            PatchSafe(harmony, typeof(PlatformingLevelPitMoveTriggerPatch));
            PatchSafe(harmony, typeof(ForestPlatformingLevelChomperSpawnerPatch));
            PatchSafe(harmony, typeof(AbstractPlatformingLevelEnemyTriggerPatch));
            PatchSafe(harmony, typeof(LevelPitExtraParticipantPatch));
            PatchSafe(harmony, typeof(PlatformingLevelShootingEnemyRangePatch));
            PatchSafe(harmony, typeof(PlatformingLevelShootingEnemyVolumesPatch));
            PatchSafe(harmony, typeof(PlatformingLevelShootingEnemyShootPatch));
            PatchSafe(harmony, typeof(MountainPlatformingLevelElevatorHandlerStartPatch));
            PatchSafe(harmony, typeof(CircusPlatformingLevelTrampolineSleepPatch));
            PatchSafe(harmony, typeof(MountainPlatformingLevelScaleStartPatch));
            PatchSafe(harmony, typeof(SnowCultLevelPlatformExtraBouncePatch));
            PatchSafe(harmony, typeof(PlatformingLevelExitExtraParticipantPatch));
            PatchSafe(harmony, typeof(LevelCoinExtraCollectorPatch));
            PatchSafe(harmony, typeof(PirateLevelBarrelExtraTriggerPatch));
            PatchSafe(harmony, typeof(AbstractLevelInteractiveEntityExtraPatch));
            PatchSafe(harmony, typeof(HouseLevelExitExtraPatch));
            PatchSafe(harmony, typeof(RobotLevelRobotHeadPrimaryPatch));
            PatchSafe(harmony, typeof(ChessKnightLevelInitPatch3P));
            PatchSafe(harmony, typeof(ChessKnightCheckTauntPatch3P));
            PatchSafe(harmony, typeof(ChessKnightShouldBackDashPatch3P));
            PatchSafe(harmony, typeof(ChessBishopFixedUpdatePatch3P));
            PatchSafe(harmony, typeof(ChessBishopFindVerticalAnglePatch3P));
            PatchSafe(harmony, typeof(ChessBishopFindHorizontalPositionPatch3P));
            PatchSafe(harmony, typeof(SallyMeteorParryPatch3P));

            // Movement / input sync
            PatchSafe(harmony, typeof(PlayerMotorPatch));
            PatchSafe(harmony, typeof(PlayerInputAxisPatch));
            PatchSafe(harmony, typeof(PlayerInputAxisIntPatch));
            PatchSafe(harmony, typeof(PlayerInputButtonPatch));
            PatchSafe(harmony, typeof(ParryPatch));

            // Damage authority
            PatchSafe(harmony, typeof(PlayerDamagePatch));

            // Scene transitions
            PatchSafe(harmony, typeof(SceneLoaderLevelsPatch));
            PatchSafe(harmony, typeof(SceneLoaderScenesPatch));
            PatchSafe(harmony, typeof(SlotSelectEnterGamePatch));
            PatchSafe(harmony, typeof(LevelPlayerDeathStatsPatch));
            PatchSafe(harmony, typeof(SceneLoaderRetryStatsPatch));
            PatchSafe(harmony, typeof(LevelPlayerParryStatsPatch));

            Log.LogInfo("[Plugin] Patch pass complete.");
            SessionPausePanel.Ensure();
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

        void Update()
        {
            MainThreadQueue.Drain();
            Net?.Poll();
            EnemySyncManager.HostTick();
            ExtraRemoteAvatarManager.Update();
            ExtraParticipantDamageBridge.Update();
            ExtraParticipantTracker.Update();
            ExtraParticipantReviveVisuals.Update();
            PlayerColorSync.Update();
            BossHealthScaler.Update();
            SessionSync.Update();
            SessionPausePanel.Ensure();
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
                          + "Show Credits Menu: " + ShowCreditsMenu + nl
                          + "Show Pause Session Panel: " + ShowPauseSessionPanel + nl
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
        public const string VERSION = "1.2.3";
    }
}
