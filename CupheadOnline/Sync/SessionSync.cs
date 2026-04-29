using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.Net;
using CupheadOnline.Patches;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    public enum SessionIssueSeverity
    {
        None = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    public static class SessionSync
    {
        private static SaveProfilePacket? _localSaveProfile;
        private static SaveProfilePacket? _remoteSaveProfile;
        private static SessionSnapshotPacket? _lastHostSnapshot;

        private static byte _trackedSaveSlot = byte.MaxValue;
        private static Scenes _trackedMapScene = Scenes.scene_map_world_1;
        private static bool _trackedSaveEmpty;
        private static bool _hasTrackedSave;
        private static bool _hasCompletedHandshake;
        private static ushort _saveRevision;

        private static bool _localGuestReady;
        private static bool _remoteGuestReady;

        private static float _nextHostSnapshotAt;
        private static float _lastHostSnapshotAt = -1f;
        private static float _lastRecoveryRequestedAt = -1f;
        private static float _lastRecoveryBundleAt = -1f;
        private static float _lastAutoFollowAt = -1f;
        private static string _lastAutoFollowTarget = string.Empty;
        private static int _sceneMismatchStreak;
        private static int _recoveryRequestCount;
        private static int _recoveryBundleCount;

        private static string _compatibilitySummary = "Compatibility: waiting for host save.";
        private static SessionIssueSeverity _compatibilitySeverity = SessionIssueSeverity.Info;

        private static string _desyncSummary = string.Empty;
        private static SessionIssueSeverity _desyncSeverity = SessionIssueSeverity.None;

        private static int _localDeaths;
        private static int _localRetries;
        private static int _localParries;
        private static readonly Dictionary<byte, BattleParticipantStats> _remoteBattleStats =
            new Dictionary<byte, BattleParticipantStats>(4);
        private static int _trackedBattleLevel = -1;
        private static bool _preserveBattleStatsForRetryReload;
        private static int _preservedRetryBattleLevel = -1;
        private static bool _preserveBattleStatsSawInactive;
        private static float _preserveBattleStatsStartedAt = -1f;
        private static float _lastLocalBattleStatsSentAt = -1f;
        private static int _lastSentBattleLevel = -1;
        private static int _lastSentDeaths = -1;
        private static int _lastSentParries = -1;
        private static int _hostBattleLevel = -1;
        private static float _hostBattleElapsedSeconds;
        private static float _hostBattleSnapshotAt = -1f;
        private static bool _hostBattlePaused;
        private static int _hostBattleDeaths;
        private static int _hostBattleRetries;
        private static int _hostBattleParries;
        private static float _lastBattleTimingLogAt = -1f;

        private struct BattleParticipantStats
        {
            public int Level;
            public int Deaths;
            public int Parries;
            public uint Tick;
        }

        private static readonly BindingFlags InstanceAny =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo PauseGuiPauseMethod =
            typeof(AbstractPauseGUI).GetMethod("Pause", InstanceAny);
        private static readonly MethodInfo PauseGuiUnpauseMethod =
            typeof(AbstractPauseGUI).GetMethod("Unpause", InstanceAny);

        private static bool _hasLastBroadcastPauseState;
        private static bool _lastBroadcastPauseState;
        private static bool _lastAppliedHostPauseState;
        private static float _lastAppliedHostPauseAt = -1f;

        static SessionSync()
        {
            MultiplayerSession.OnSessionStarted += HandleSessionStarted;
            MultiplayerSession.OnSessionEnded += HandleSessionEnded;
        }

        public static string CompatibilitySummary => _compatibilitySummary;
        public static SessionIssueSeverity CompatibilitySeverity => _compatibilitySeverity;
        public static string DesyncSummary => _desyncSummary;
        public static SessionIssueSeverity DesyncSeverity => _desyncSeverity;
        public static int LocalDeaths => _localDeaths;
        public static int LocalRetries => _localRetries;
        public static int LocalParries => _localParries;
        public static bool HasTrackedSave => _hasTrackedSave;
        public static ushort SaveRevision => _saveRevision;
        public static bool IsLocalReady => _hasTrackedSave;
        public static bool IsRemoteReady => _hasTrackedSave;
        public static bool CanGuestToggleReady => false;

        public static void Update()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            TrackBattleStatsLevel();
            MaybeSendLocalBattleStats(false);

            if (MultiplayerSession.IsHost)
            {
                BroadcastPauseSnapshotIfChanged();

                if (Time.unscaledTime >= _nextHostSnapshotAt)
                {
                    if (_hasTrackedSave && IsTitleScene())
                    {
                        RebroadcastTrackedSaveSelection();
                        BroadcastSelectedSaveProfile();
                    }

                    BroadcastSessionSnapshot(true);
                    _nextHostSnapshotAt = Time.unscaledTime + (ShouldUseFastBattleSnapshotCadence() ? 0.2f : 1f);
                }
            }
            else
            {
                EvaluateDesync();
            }
        }

        public static void OnConnected(bool isHost)
        {
            _hasCompletedHandshake = true;
            _desyncSummary = string.Empty;
            _desyncSeverity = SessionIssueSeverity.None;
            _sceneMismatchStreak = 0;
            _nextHostSnapshotAt = Time.unscaledTime + 0.25f;
            _lastHostSnapshotAt = -1f;
            _lastRecoveryRequestedAt = -1f;

            if (isHost)
            {
                _remoteGuestReady = false;

                if (!_hasTrackedSave)
                {
                    string sceneName = GetActiveSceneName();
                    bool canRecoverContext = !string.Equals(sceneName, "scene_title", StringComparison.OrdinalIgnoreCase);
                    if (canRecoverContext)
                    {
                        _trackedSaveSlot = (byte)Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);
                        _trackedMapScene = PlayerData.Data != null ? PlayerData.Data.CurrentMap : Scenes.scene_map_world_1;
                        _trackedSaveEmpty = false;
                        _hasTrackedSave = true;
                        if (_saveRevision == 0)
                            _saveRevision = 1;
                    }
                }

                if (_hasTrackedSave)
                {
                    CaptureLocalSaveProfile(_trackedSaveSlot, _trackedSaveEmpty);
                    BroadcastSelectedSaveProfile();
                }

                BroadcastRecoveryBundle("Peer connected.");
            }
            else
            {
                _localGuestReady = false;
                if (_hasTrackedSave)
                    BroadcastGuestSaveProfile();
                else
                {
                    _compatibilitySummary = "Compatibility: waiting for host save.";
                    _compatibilitySeverity = SessionIssueSeverity.Info;
                }
            }
        }

        public static void RecordSelectedSave(ref SaveSlotSyncPacket pkt)
        {
            bool selectionChanged =
                !_hasTrackedSave
             || _trackedSaveSlot != pkt.SlotIndex
             || _trackedMapScene != (Scenes)pkt.CurrentMapScene
             || _trackedSaveEmpty != pkt.IsEmpty
             || (_localSaveProfile.HasValue && _localSaveProfile.Value.Player1IsMugman != pkt.Player1IsMugman);

            if (pkt.SaveRevision == 0)
                pkt.SaveRevision = selectionChanged || _saveRevision == 0 ? NextSaveRevision() : _saveRevision;
            else
            {
                selectionChanged = selectionChanged || (_saveRevision != 0 && pkt.SaveRevision != _saveRevision);
                _saveRevision = pkt.SaveRevision;
            }

            _trackedSaveSlot = pkt.SlotIndex;
            _trackedMapScene = (Scenes)pkt.CurrentMapScene;
            _trackedSaveEmpty = pkt.IsEmpty;
            _hasTrackedSave = true;
            if (selectionChanged)
                _remoteGuestReady = false;

            CaptureLocalSaveProfile(pkt.SlotIndex, pkt.IsEmpty);
            EvaluateCompatibility();
        }

        public static void ApplyRemoteSaveSelection(ref SaveSlotSyncPacket pkt)
        {
            if (IsStaleSaveRevision(pkt.SaveRevision))
                return;

            bool selectionChanged =
                !_hasTrackedSave
             || _trackedSaveSlot != pkt.SlotIndex
             || _trackedMapScene != (Scenes)pkt.CurrentMapScene
             || _trackedSaveEmpty != pkt.IsEmpty
             || (_saveRevision != 0 && pkt.SaveRevision != 0 && pkt.SaveRevision != _saveRevision)
             || (_localSaveProfile.HasValue && _localSaveProfile.Value.Player1IsMugman != pkt.Player1IsMugman);

            _trackedSaveSlot = pkt.SlotIndex;
            _trackedMapScene = (Scenes)pkt.CurrentMapScene;
            _trackedSaveEmpty = pkt.IsEmpty;
            _hasTrackedSave = true;
            _saveRevision = pkt.SaveRevision == 0 ? (ushort)1 : pkt.SaveRevision;
            if (selectionChanged)
                _localGuestReady = false;

            CaptureLocalSaveProfile(pkt.SlotIndex, pkt.IsEmpty);
            BroadcastGuestSaveProfile();
            EvaluateCompatibility();
        }

        public static void ApplyRemoteSaveProfile(SaveProfilePacket pkt)
        {
            _remoteSaveProfile = pkt;
            if (!_hasTrackedSave)
            {
                _trackedSaveSlot = pkt.SlotIndex;
                _trackedMapScene = (Scenes)pkt.CurrentMapScene;
                _trackedSaveEmpty = pkt.IsEmpty;
                _hasTrackedSave = true;
            }

            EvaluateCompatibility();
        }

        public static void ApplyHostSnapshot(SessionSnapshotPacket pkt)
        {
            if (IsStaleHostSnapshot(pkt))
                return;

            _lastHostSnapshot = pkt;
            _lastHostSnapshotAt = Time.unscaledTime;
            _hostBattleLevel = pkt.CurrentLevel;
            _hostBattleElapsedSeconds = Mathf.Max(0f, pkt.BattleElapsedSeconds);
            _hostBattleSnapshotAt = Time.unscaledTime;
            _hostBattlePaused = pkt.IsPaused;
            _hostBattleDeaths = pkt.BattleDeaths;
            _hostBattleRetries = pkt.BattleRetries;
            _hostBattleParries = pkt.BattleParries;
            BattleAssistHud.SeedDiagnosticTimerFromHost(EstimateHostBattleElapsedSeconds(), pkt.CurrentLevel);
            float localElapsedForCatchUp;
            float hostElapsedForCatchUp;
            float localMinusHostForCatchUp;
            if (TryGetBattleAssistTiming(out localElapsedForCatchUp, out hostElapsedForCatchUp, out localMinusHostForCatchUp))
                ParticipantReviveController.TryCatchUpAfterHostTimingSnapshot(localMinusHostForCatchUp);
            LogBattleTimingOffset();
            if (pkt.SaveRevision != 0 && !IsStaleSaveRevision(pkt.SaveRevision))
                _saveRevision = pkt.SaveRevision;

            if (pkt.HasTrackedSave && pkt.SaveSlotIndex != byte.MaxValue)
            {
                _trackedSaveSlot = pkt.SaveSlotIndex;
                if (IsDefinedScene(pkt.CurrentMapScene))
                    _trackedMapScene = (Scenes)pkt.CurrentMapScene;
                _trackedSaveEmpty = false;
                _hasTrackedSave = true;
            }

            TryAutoFollowHostSnapshot(pkt);
            ApplyHostPauseState(pkt);
            EvaluateDesync();
        }

        public static void ApplySessionStart(SessionStartPacket pkt)
        {
            _hasCompletedHandshake = true;
            if (pkt.SaveRevision != 0 && !IsStaleSaveRevision(pkt.SaveRevision))
                _saveRevision = pkt.SaveRevision;
        }

        public static bool IsStaleSaveRevision(ushort revision)
        {
            if (revision == 0 || _saveRevision == 0 || revision == _saveRevision)
                return false;

            return unchecked((short)(revision - _saveRevision)) < 0;
        }

        public static bool TryGetBattleAssistDisplay(
            out int deaths,
            out int retries,
            out int parries)
        {
            deaths = 0;
            retries = 0;
            parries = 0;

            if (!MultiplayerSession.IsActive || !IsBattleActive())
                return false;

            int currentLevel = GetCurrentBattleLevel();
            if (MultiplayerSession.IsHost)
            {
                deaths = GetAggregateBattleDeaths(currentLevel);
                retries = _localRetries;
                parries = GetAggregateBattleParries(currentLevel);
                return true;
            }

            if (_hostBattleSnapshotAt < 0f || _hostBattleLevel != currentLevel)
                return false;

            deaths = _hostBattleDeaths;
            retries = _hostBattleRetries;
            parries = _hostBattleParries;
            return true;
        }

        public static bool TryGetBattleAssistTiming(
            out float localElapsedSeconds,
            out float hostElapsedSeconds,
            out float localMinusHostSeconds)
        {
            localElapsedSeconds = BattleAssistHud.ElapsedSeconds;
            hostElapsedSeconds = localElapsedSeconds;
            localMinusHostSeconds = 0f;

            if (!MultiplayerSession.IsActive || !IsBattleActive())
                return false;

            if (MultiplayerSession.IsHost)
                return true;

            if (_hostBattleSnapshotAt < 0f || _hostBattleLevel != GetCurrentBattleLevel())
                return false;
            if (!BattleAssistHud.HasHostDiagnosticSeedForCurrentBattle)
                return false;

            hostElapsedSeconds = EstimateHostBattleElapsedSeconds();
            localMinusHostSeconds = localElapsedSeconds - hostElapsedSeconds;
            return true;
        }

        public static void ApplySessionSignal(SessionSignalPacket pkt)
        {
            if (LanSteamE2ETest.TryHandleSessionSignal(pkt))
                return;

            switch (pkt.Kind)
            {
                case SessionSignalKind.GuestReady:
                    if (!MultiplayerSession.IsHost) return;
                    _remoteGuestReady = _hasTrackedSave && pkt.SaveRevision == _saveRevision;
                    Plugin.Log.LogInfo(
                        _remoteGuestReady
                            ? "[Session] Guest readied up for save revision " + _saveRevision + "."
                            : "[Session] Ignored stale guest ready signal for revision " + pkt.SaveRevision + ".");
                    break;

                case SessionSignalKind.GuestUnready:
                    if (!MultiplayerSession.IsHost) return;
                    _remoteGuestReady = false;
                    Plugin.Log.LogInfo("[Session] Guest marked not ready.");
                    break;

                case SessionSignalKind.RequestRecovery:
                    if (!MultiplayerSession.IsHost) return;
                    _recoveryRequestCount++;
                    BroadcastRecoveryBundle("Guest requested a resync.");
                    break;

                case SessionSignalKind.LevelLoaded:
                    LevelStartSync.HandleRemoteLevelLoaded(pkt.SaveRevision);
                    break;

                case SessionSignalKind.LevelStartRelease:
                    LevelStartSync.HandleRemoteLevelStartRelease(pkt.SaveRevision, pkt.UtcReleaseTicks);
                    break;

                case SessionSignalKind.LevelBattleReady:
                    LevelStartSync.HandleRemoteLevelBattleReady(pkt.SaveRevision);
                    break;

                case SessionSignalKind.LevelBattleRelease:
                    LevelStartSync.HandleRemoteLevelBattleRelease(pkt.SaveRevision, pkt.UtcReleaseTicks);
                    break;

                case SessionSignalKind.LevelIntroReady:
                    LevelStartSync.HandleRemoteLevelIntroReady(pkt.SaveRevision);
                    break;

                case SessionSignalKind.LevelIntroRelease:
                    LevelStartSync.HandleRemoteLevelIntroRelease(pkt.SaveRevision, pkt.UtcReleaseTicks);
                    break;
            }
        }

        public static bool CanHostStartRun(out string reason)
        {
            reason = string.Empty;

            if (Plugin.Net == null || !Plugin.Net.IsConnected || !MultiplayerSession.IsHost)
                return true;

            if (!_hasTrackedSave)
            {
                reason = "Choose a save slot first.";
                return false;
            }

            return true;
        }

        public static string ToggleGuestReady()
        {
            return _hasTrackedSave
                ? "No ready check needed. Waiting for the host to start."
                : "Waiting for the host to choose a save.";
        }

        public static string RequestRecovery()
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return "Connect first before asking for a resync.";

            if (MultiplayerSession.IsHost)
            {
                BroadcastRecoveryBundle("Host requested a fresh resync.");
                return "Sent a fresh recovery bundle to the guest.";
            }

            var pkt = new SessionSignalPacket
            {
                Signal = (byte)SessionSignalKind.RequestRecovery,
                SaveRevision = _saveRevision,
            };
            Plugin.Net.SendSessionSignal(ref pkt);
            _lastRecoveryRequestedAt = Time.unscaledTime;
            return "Requested a resync from the host.";
        }

        public static void BroadcastSelectedSaveProfile()
        {
            if (!_hasTrackedSave || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            BroadcastSaveProfile(_trackedSaveSlot, _trackedMapScene, _trackedSaveEmpty);
        }

        public static void BroadcastGuestSaveProfile()
        {
            if (!_hasTrackedSave || Plugin.Net == null || !Plugin.Net.IsConnected || MultiplayerSession.IsHost)
                return;

            BroadcastSaveProfile(_trackedSaveSlot, _trackedMapScene, _trackedSaveEmpty);
        }

        public static void RebroadcastTrackedSaveSelection()
        {
            if (!_hasTrackedSave || Plugin.Net == null || !Plugin.Net.IsConnected || !MultiplayerSession.IsHost)
                return;

            var pkt = new SaveSlotSyncPacket
            {
                SlotIndex = _trackedSaveSlot,
                Flags = (byte)((_trackedSaveEmpty ? 1 : 0) | (GetTrackedPlayer1IsMugman() ? 2 : 0)),
                SaveRevision = _saveRevision == 0 ? NextSaveRevision() : _saveRevision,
                CurrentMapScene = (int)_trackedMapScene,
            };

            _saveRevision = pkt.SaveRevision;
            Plugin.Net.SendSaveSlotSync(ref pkt);
        }

        public static void BroadcastSessionSnapshot(bool reliable)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || !MultiplayerSession.IsHost)
                return;

            var pkt = new SessionSnapshotPacket
            {
                SaveSlotIndex = _hasTrackedSave ? _trackedSaveSlot : byte.MaxValue,
                Flags = BuildSnapshotFlags(),
                SaveRevision = _saveRevision,
                CurrentLevel = Level.Current != null ? (int)Level.Current.CurrentLevel : -1,
                CurrentMapScene = PlayerData.Data != null ? (int)PlayerData.Data.CurrentMap : -1,
                HostTick = MultiplayerSession.Tick,
                BattleElapsedSeconds = BattleAssistHud.ElapsedSeconds,
                BattleDeaths = ClampToUShort(GetAggregateBattleDeaths(GetCurrentBattleLevel())),
                BattleRetries = ClampToUShort(_localRetries),
                BattleParries = ClampToUShort(GetAggregateBattleParries(GetCurrentBattleLevel())),
                SceneName = GetActiveSceneName(),
            };

            Plugin.Net.SendSessionSnapshot(ref pkt, reliable);
        }

        private static void BroadcastPauseSnapshotIfChanged()
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || !MultiplayerSession.IsHost)
                return;

            bool paused = PauseManager.state == PauseManager.State.Paused;
            if (_hasLastBroadcastPauseState && _lastBroadcastPauseState == paused)
                return;

            _hasLastBroadcastPauseState = true;
            _lastBroadcastPauseState = paused;
            BroadcastSessionSnapshot(true);
            Plugin.Log.LogInfo("[Session] Broadcast host pause state: " + (paused ? "paused" : "unpaused") + ".");
        }

        private static void ApplyHostPauseState(SessionSnapshotPacket snapshot)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || MultiplayerSession.IsHost)
                return;

            if (!snapshot.IsInLevel)
                return;

            string localScene = GetActiveSceneName();
            if (!string.IsNullOrEmpty(snapshot.SceneName)
             && !string.IsNullOrEmpty(localScene)
             && !string.Equals(snapshot.SceneName, localScene, StringComparison.OrdinalIgnoreCase))
                return;

            bool localPaused = PauseManager.state == PauseManager.State.Paused;
            if (localPaused == snapshot.IsPaused)
                return;

            bool appliedThroughGui = TrySetLevelPauseGuiState(snapshot.IsPaused);
            if (!appliedThroughGui)
            {
                if (snapshot.IsPaused)
                    PauseManager.Pause();
                else
                    PauseManager.Unpause();
            }

            _lastAppliedHostPauseState = snapshot.IsPaused;
            _lastAppliedHostPauseAt = Time.unscaledTime;
            Plugin.Log.LogInfo("[Session] Applied host pause state on guest: "
                + (snapshot.IsPaused ? "paused" : "unpaused")
                + (appliedThroughGui ? " via LevelPauseGUI." : " via PauseManager fallback."));
        }

        private static bool TrySetLevelPauseGuiState(bool paused)
        {
            try
            {
                var gui = UnityEngine.Object.FindObjectOfType<LevelPauseGUI>();
                if (gui == null)
                    return false;

                if (paused)
                {
                    if (gui.state == AbstractPauseGUI.State.Paused
                     || (gui.state == AbstractPauseGUI.State.Animating && PauseManager.state == PauseManager.State.Paused))
                        return true;

                    if (PauseGuiPauseMethod != null)
                    {
                        PauseGuiPauseMethod.Invoke(gui, null);
                        return true;
                    }

                    gui.StartCoroutine(gui.ShowPauseMenu());
                    return true;
                }

                if (gui.state == AbstractPauseGUI.State.Unpaused
                 && PauseManager.state == PauseManager.State.Unpaused)
                    return true;

                if (PauseGuiUnpauseMethod != null)
                {
                    PauseGuiUnpauseMethod.Invoke(gui, null);
                    return true;
                }

                PauseManager.Unpause();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Session] Failed to apply host pause through LevelPauseGUI: " + ex.Message);
                return false;
            }
        }

        public static void BroadcastRecoveryBundle(string reason)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || !MultiplayerSession.IsHost)
                return;

            if (_hasTrackedSave)
            {
                RebroadcastTrackedSaveSelection();
                BroadcastSelectedSaveProfile();
            }

            var start = new SessionStartPacket
            {
                Flags = (byte)((Level.Current != null ? 1 : 0) | (_hasTrackedSave ? 2 : 0)),
                CurrentLevel = Level.Current != null ? (int)Level.Current.CurrentLevel : -1,
                SaveRevision = _saveRevision,
                CurrentTick = MultiplayerSession.Tick,
                RngSeed = RngSync.CurrentSeed,
            };
            Plugin.Net.SendSessionStart(ref start);
            BroadcastSessionSnapshot(true);
            EnemySyncManager.TriggerRecoveryBurst();

            _recoveryBundleCount++;
            _lastRecoveryBundleAt = Time.unscaledTime;
            Plugin.Log.LogInfo("[Session] Recovery bundle sent. Reason: " + reason);
        }

        public static void RecordLocalDeath()
        {
            if (!MultiplayerSession.IsActive) return;
            TrackBattleStatsLevel();
            _localDeaths++;
            MaybeSendLocalBattleStats(true);
        }

        public static void RecordLocalRetry()
        {
            if (!MultiplayerSession.IsActive) return;
            int retryLevel = GetCurrentBattleLevel();
            if (retryLevel < 0 && Enum.IsDefined(typeof(Levels), SceneLoader.CurrentLevel))
                retryLevel = (int)SceneLoader.CurrentLevel;

            if (retryLevel >= 0)
            {
                _preserveBattleStatsForRetryReload = true;
                _preservedRetryBattleLevel = retryLevel;
                _preserveBattleStatsSawInactive = false;
                _preserveBattleStatsStartedAt = Time.unscaledTime;
                if (_trackedBattleLevel < 0)
                    _trackedBattleLevel = retryLevel;
            }
            else
            {
                TrackBattleStatsLevel();
            }

            _localRetries++;
            MaybeSendLocalBattleStats(true);
        }

        public static void RecordLocalParry()
        {
            if (!MultiplayerSession.IsActive) return;
            TrackBattleStatsLevel();
            _localParries++;
            MaybeSendLocalBattleStats(true);
        }

        public static void ApplyBattleAssistStats(BattleAssistStatsPacket pkt, byte sourceParticipantId)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost)
                return;

            int currentLevel = GetCurrentBattleLevel();
            if (currentLevel < 0 || pkt.CurrentLevel != currentLevel)
                return;

            byte participantId = pkt.ParticipantId;
            if (participantId == byte.MaxValue && sourceParticipantId != byte.MaxValue)
                participantId = sourceParticipantId;
            if (participantId == byte.MaxValue || participantId == (byte)MultiplayerSession.LocalId)
                return;

            BattleParticipantStats previous;
            if (_remoteBattleStats.TryGetValue(participantId, out previous)
             && previous.Level == pkt.CurrentLevel
             && pkt.Tick != 0
             && previous.Tick != 0
             && NetTick.IsOlder(pkt.Tick, previous.Tick))
            {
                return;
            }

            _remoteBattleStats[participantId] = new BattleParticipantStats
            {
                Level = pkt.CurrentLevel,
                Deaths = pkt.Deaths,
                Parries = pkt.Parries,
                Tick = pkt.Tick,
            };
        }

        public static string GetFooterHint()
        {
            if (Plugin.Net == null)
                return string.Empty;

            if (Plugin.Net.IsConnected)
            {
                if (Plugin.Net.IsHost)
                {
                    if (!_hasTrackedSave)
                        return "Guest connected - press Start to choose a save";
                    if (_compatibilitySeverity >= SessionIssueSeverity.Error)
                        return _compatibilitySummary + " - host can still start";
                    return "Save synced - host can start";
                }

                if (!_hasTrackedSave)
                    return "Connected - waiting for host save";

                if (_desyncSeverity >= SessionIssueSeverity.Warning)
                    return _desyncSummary;
                if (_compatibilitySeverity >= SessionIssueSeverity.Error)
                    return _compatibilitySummary;
                return "Save synced - waiting for host to start";
            }

            if (Plugin.Net.IsInLobby)
                return Plugin.Net.IsHost
                    ? "Lobby ready - invite or share the lobby ID"
                    : "Lobby joined - finishing connection";

            return string.Empty;
        }

        public static string GetStageSummary()
        {
            if (Plugin.Net == null)
                return "OFFLINE";

            if (!Plugin.Net.IsConnected)
            {
                if (Plugin.Net.IsInLobby)
                    return Plugin.Net.IsHost ? "HOSTING LOBBY" : "JOINING HOST";
                return Plugin.Net.IsSteamReady ? "NOT CONNECTED" : "STEAM UNAVAILABLE";
            }

            if (Plugin.Net.IsHost)
            {
                if (!_hasTrackedSave)
                    return "WAITING FOR SAVE";
                if (_compatibilitySeverity >= SessionIssueSeverity.Error)
                    return "SAVE WARNING";
                return "READY TO START";
            }

            if (!_hasTrackedSave)
                return "WAITING FOR HOST SAVE";
            if (_compatibilitySeverity >= SessionIssueSeverity.Error)
                return "SAVE MISMATCH";
            return "FOLLOWING HOST";
        }

        public static string GetMenuPresenceSummary()
        {
            if (!MultiplayerSession.IsActive && (Plugin.Net == null || !Plugin.Net.IsConnected))
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("Session: " + GetStageSummary());

            if (_hasTrackedSave)
            {
                sb.Append("Save: SLOT ");
                sb.Append(_trackedSaveSlot + 1);
                sb.Append(" | ");
                sb.Append(_trackedMapScene);
                sb.Append(" | REV ");
                sb.Append(_saveRevision);
                if (_trackedSaveEmpty)
                    sb.Append(" | EMPTY");
                sb.AppendLine();
            }

            sb.AppendLine("Launch: host-controlled");

            sb.Append("Players: ");
            sb.Append(MultiplayerSession.ActivePlayerCount);
            sb.Append(" | Boss HP: ");
            sb.AppendLine(BossHealthScaler.GetStatusSummary().Replace("Boss HP scaling: ", string.Empty));

            string localLead = GetLocalLeadCharacterName();
            string remoteLead = GetRemoteLeadCharacterName();
            if (!string.IsNullOrEmpty(localLead) || !string.IsNullOrEmpty(remoteLead))
            {
                sb.Append("Lead Char: Local ");
                sb.Append(string.IsNullOrEmpty(localLead) ? "Unknown" : localLead);
                sb.Append(" | Remote ");
                sb.AppendLine(string.IsNullOrEmpty(remoteLead) ? "Unknown" : remoteLead);
            }

            if (!string.IsNullOrEmpty(_compatibilitySummary))
            {
                sb.Append("Check: ");
                sb.AppendLine(_compatibilitySummary.Replace(Environment.NewLine, " "));
            }

            if (!string.IsNullOrEmpty(_desyncSummary))
            {
                sb.Append("Sync: ");
                sb.AppendLine(_desyncSummary.Replace(Environment.NewLine, " "));
            }

            return sb.ToString().TrimEnd();
        }

        public static string BuildPausePanelText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Stage: " + GetStageSummary());

            if (_hasTrackedSave)
            {
                sb.Append("Save Slot: ");
                sb.Append(_trackedSaveSlot + 1);
                sb.Append(" | Map: ");
                sb.Append(_trackedMapScene);
                sb.Append(" | Rev: ");
                sb.AppendLine(_saveRevision.ToString());
            }
            else
            {
                sb.AppendLine("Save Slot: waiting for host");
            }

            sb.AppendLine("Launch: host-controlled");

            sb.Append("Players: ");
            sb.Append(MultiplayerSession.ActivePlayerCount);
            sb.Append(" | Characters: ");
            sb.Append(MultiplayerSession.GetLocalCharacterName());
            sb.Append(" / ");
            sb.AppendLine(MultiplayerSession.GetRemoteCharacterName());

            sb.AppendLine(BossHealthScaler.GetStatusSummary());

            sb.Append("Save Check: ");
            sb.AppendLine(string.IsNullOrEmpty(_compatibilitySummary) ? "No save data yet." : _compatibilitySummary);

            sb.Append("Sync Check: ");
            sb.AppendLine(string.IsNullOrEmpty(_desyncSummary) ? "Following host." : _desyncSummary);

            sb.Append("Stats: ");
            sb.Append(_localDeaths);
            sb.Append(" deaths | ");
            sb.Append(_localRetries);
            sb.Append(" retries | ");
            sb.Append(_localParries);
            sb.Append(" parries");

            string extraSummary = ExtraParticipantTracker.BuildStatusSummary();
            if (!string.IsNullOrEmpty(extraSummary))
            {
                sb.AppendLine();
                sb.Append("Extras: ");
                sb.Append(extraSummary);
            }

            return sb.ToString();
        }

        public static string BuildDiagnosticsSection()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Stage: " + GetStageSummary());
            sb.AppendLine("Tracked Save: " + (_hasTrackedSave ? (_trackedSaveSlot + 1).ToString() : "(none)"));
            sb.AppendLine("Save Revision: " + _saveRevision);
            sb.AppendLine("Ready Gate: disabled");
            sb.AppendLine("Players: " + MultiplayerSession.ActivePlayerCount);
            sb.AppendLine("Local Character: " + MultiplayerSession.GetLocalCharacterName());
            sb.AppendLine("Remote Character: " + MultiplayerSession.GetRemoteCharacterName());
            sb.AppendLine(BossHealthScaler.GetStatusSummary());
            sb.AppendLine("Compatibility: " + _compatibilitySummary);
            sb.AppendLine("Compatibility Severity: " + _compatibilitySeverity);
            sb.AppendLine("Desync: " + (string.IsNullOrEmpty(_desyncSummary) ? "(none)" : _desyncSummary));
            sb.AppendLine("Desync Severity: " + _desyncSeverity);
            sb.AppendLine("Recovery Requests: " + _recoveryRequestCount);
            sb.AppendLine("Recovery Bundles: " + _recoveryBundleCount);
            sb.AppendLine("Stats: deaths=" + _localDeaths + ", retries=" + _localRetries + ", parries=" + _localParries);

            if (_lastHostSnapshot.HasValue)
            {
                var snap = _lastHostSnapshot.Value;
                sb.AppendLine("Host Scene: " + snap.SceneName);
                sb.AppendLine("Host Level: " + snap.CurrentLevel);
                sb.AppendLine("Host Tick: " + snap.HostTick);
                sb.AppendLine("Host Save Revision: " + snap.SaveRevision);
                sb.AppendLine("Host Paused: " + snap.IsPaused);
            }

            return sb.ToString().TrimEnd();
        }

        public static Color GetSeverityColor(SessionIssueSeverity severity)
        {
            switch (severity)
            {
                case SessionIssueSeverity.Error:
                    return new Color(0.95f, 0.35f, 0.30f, 1f);
                case SessionIssueSeverity.Warning:
                    return new Color(0.98f, 0.80f, 0.38f, 1f);
                case SessionIssueSeverity.Info:
                    return new Color(0.72f, 0.86f, 0.96f, 1f);
                default:
                    return new Color(0.78f, 0.90f, 0.70f, 1f);
            }
        }

        private static void BroadcastSaveProfile(byte slotIndex, Scenes mapScene, bool isEmpty)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = BuildProfile(slotIndex, mapScene, isEmpty);
            _localSaveProfile = pkt;
            Plugin.Net.SendSaveProfile(ref pkt);
        }

        private static void CaptureLocalSaveProfile(byte slotIndex, bool isEmpty)
        {
            Scenes mapScene = PlayerData.Data != null ? PlayerData.Data.CurrentMap : Scenes.scene_map_world_1;
            if (_hasTrackedSave)
                mapScene = _trackedMapScene;

            _localSaveProfile = BuildProfile(slotIndex, mapScene, isEmpty);
        }

        private static bool GetTrackedPlayer1IsMugman()
        {
            int clampedSlot = Mathf.Clamp(_trackedSaveSlot, 0, 2);
            var data = PlayerData.GetDataForSlot(clampedSlot);
            return data != null && data.isPlayer1Mugman;
        }

        private static SaveProfilePacket BuildProfile(byte slotIndex, Scenes mapScene, bool isEmpty)
        {
            int clampedSlot = Mathf.Clamp(slotIndex, 0, 2);
            var data = PlayerData.GetDataForSlot(clampedSlot);

            if (data != null && !isEmpty)
            {
                mapScene = data.CurrentMap;
                if (!DLCManager.DLCEnabled() && mapScene == Scenes.scene_map_world_DLC)
                    mapScene = Scenes.scene_map_world_1;
            }

            byte flags = 0;
            if (isEmpty) flags |= 1;
            if (DLCManager.DLCEnabled()) flags |= 2;
            if (data != null && data.isPlayer1Mugman) flags |= 4;

            int weapon1 = (int)Weapon.level_weapon_peashot;
            int weapon2 = (int)Weapon.None;
            int super = (int)Super.None;
            int charm = (int)Charm.None;
            float baseCompletion = 0f;
            float dlcCompletion = 0f;
            ushort coins = 0;

            if (data != null)
            {
                baseCompletion = data.GetCompletionPercentage();
                dlcCompletion = DLCManager.DLCEnabled() ? data.GetCompletionPercentageDLC() : 0f;
                coins = (ushort)Math.Min((int)ushort.MaxValue, Math.Max(0, data.NumCoinsCollected));

                var loadouts = data.Loadouts;
                if (loadouts != null)
                {
                    var loadout = loadouts.GetPlayerLoadout(PlayerId.PlayerOne);
                    weapon1 = (int)loadout.primaryWeapon;
                    weapon2 = (int)loadout.secondaryWeapon;
                    super = (int)loadout.super;
                    charm = (int)loadout.charm;
                }
            }

            return new SaveProfilePacket
            {
                SlotIndex = (byte)clampedSlot,
                Flags = flags,
                CurrentMapScene = (int)mapScene,
                CompletionPct = baseCompletion,
                CompletionPctDlc = dlcCompletion,
                Coins = coins,
                Weapon1 = weapon1,
                Weapon2 = weapon2,
                Super = super,
                Charm = charm,
            };
        }

        private static string GetLocalLeadCharacterName()
        {
            return DescribeProfileCharacter(_localSaveProfile);
        }

        private static string GetRemoteLeadCharacterName()
        {
            return DescribeProfileCharacter(_remoteSaveProfile);
        }

        private static string DescribeProfileCharacter(SaveProfilePacket? profile)
        {
            if (!profile.HasValue)
                return string.Empty;

            var value = profile.Value;
            if (value.DlcEnabled && (Charm)value.Charm == Charm.charm_chalice)
                return "Ms. Chalice";
            return value.Player1IsMugman ? "Mugman" : "Cuphead";
        }

        private static void EvaluateCompatibility()
        {
            if (!_hasTrackedSave)
            {
                _compatibilitySummary = "Compatibility: waiting for host save.";
                _compatibilitySeverity = SessionIssueSeverity.Info;
                return;
            }

            if (!_remoteSaveProfile.HasValue)
            {
                _compatibilitySummary = MultiplayerSession.IsHost
                    ? "Compatibility: waiting for guest save data."
                    : "Compatibility: sending your save summary.";
                _compatibilitySeverity = SessionIssueSeverity.Info;
                return;
            }

            if (!_localSaveProfile.HasValue || _localSaveProfile.Value.SlotIndex != _trackedSaveSlot)
                CaptureLocalSaveProfile(_trackedSaveSlot, _trackedSaveEmpty);

            if (!_localSaveProfile.HasValue)
            {
                _compatibilitySummary = "Compatibility: local save data unavailable.";
                _compatibilitySeverity = SessionIssueSeverity.Warning;
                return;
            }

            var local = _localSaveProfile.Value;
            var remote = _remoteSaveProfile.Value;

            var issues = new List<string>();
            SessionIssueSeverity severity = SessionIssueSeverity.None;

            bool remoteNeedsDlc = (Scenes)remote.CurrentMapScene == Scenes.scene_map_world_DLC;
            if (remoteNeedsDlc && !DLCManager.DLCEnabled())
            {
                severity = SessionIssueSeverity.Error;
                issues.Add("DLC world selected but DLC is not enabled locally.");
            }

            if (!remote.IsEmpty && local.IsEmpty)
            {
                severity = MaxSeverity(severity, SessionIssueSeverity.Warning);
                issues.Add("Host save is populated, but your matching slot is empty.");
            }

            float completionDelta = Mathf.Abs(remote.CompletionPct - local.CompletionPct);
            if (completionDelta >= 20f)
            {
                severity = MaxSeverity(severity, SessionIssueSeverity.Warning);
                issues.Add("Base-game progression differs by " + completionDelta.ToString("0") + "%.");
            }

            float dlcDelta = Mathf.Abs(remote.CompletionPctDlc - local.CompletionPctDlc);
            if (dlcDelta >= 15f)
            {
                severity = MaxSeverity(severity, SessionIssueSeverity.Warning);
                issues.Add("DLC progression differs by " + dlcDelta.ToString("0") + "%.");
            }

            int coinDelta = Mathf.Abs(remote.Coins - local.Coins);
            if (coinDelta >= 10)
            {
                severity = MaxSeverity(severity, SessionIssueSeverity.Warning);
                issues.Add("Coin totals differ by " + coinDelta + ".");
            }

            if (remote.CurrentMapScene != local.CurrentMapScene && !remote.IsEmpty && !local.IsEmpty)
            {
                severity = MaxSeverity(severity, SessionIssueSeverity.Info);
                issues.Add("Current map progress does not match.");
            }

            if (issues.Count == 0)
            {
                _compatibilitySummary = "Compatibility looks good.";
                _compatibilitySeverity = SessionIssueSeverity.None;
            }
            else
            {
                _compatibilitySummary = issues[0];
                if (issues.Count > 1)
                    _compatibilitySummary += " +" + (issues.Count - 1) + " more";
                _compatibilitySeverity = severity;
            }

            if (!MultiplayerSession.IsHost && _compatibilitySeverity >= SessionIssueSeverity.Error && _localGuestReady)
                UpdateLocalGuestReady(false, true);
        }

        private static void EvaluateDesync()
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || MultiplayerSession.IsHost)
            {
                _desyncSummary = string.Empty;
                _desyncSeverity = SessionIssueSeverity.None;
                return;
            }

            if (!_lastHostSnapshot.HasValue)
            {
                _desyncSummary = _hasCompletedHandshake
                    ? "Waiting for host snapshot..."
                    : string.Empty;
                _desyncSeverity = _hasCompletedHandshake ? SessionIssueSeverity.Info : SessionIssueSeverity.None;
                return;
            }

            var snapshot = _lastHostSnapshot.Value;
            string localScene = GetActiveSceneName();
            bool sceneMismatch = !string.IsNullOrEmpty(snapshot.SceneName)
                              && !string.IsNullOrEmpty(localScene)
                              && !string.Equals(snapshot.SceneName, localScene, StringComparison.OrdinalIgnoreCase);

            if (sceneMismatch)
                _sceneMismatchStreak++;
            else
                _sceneMismatchStreak = 0;

            if (_sceneMismatchStreak >= 2)
            {
                _desyncSummary = "Scene mismatch detected - auto-following host.";
                _desyncSeverity = SessionIssueSeverity.Warning;
                return;
            }

            float localElapsed;
            float hostElapsed;
            float localMinusHost;
            if (snapshot.IsInLevel
             && TryGetBattleAssistTiming(out localElapsed, out hostElapsed, out localMinusHost)
             && Mathf.Abs(localMinusHost) >= 0.25f)
            {
                _desyncSummary = "Battle timer drift detected (local-host="
                    + FormatSignedSeconds(localMinusHost)
                    + "s). Use REQUEST RESYNC.";
                _desyncSeverity = SessionIssueSeverity.Warning;
                return;
            }

            uint localTick = MultiplayerSession.Tick;
            uint hostTick = snapshot.HostTick;
            uint tickDelta = localTick > hostTick ? localTick - hostTick : hostTick - localTick;
            if (!Plugin.VanillaTwoPlayerOnline && snapshot.IsInLevel && tickDelta > 240)
            {
                _desyncSummary = "Simulation drift detected (" + tickDelta + " ticks). Use REQUEST RESYNC.";
                _desyncSeverity = SessionIssueSeverity.Warning;
                return;
            }

            if (_lastHostSnapshotAt > 0f && Time.unscaledTime - _lastHostSnapshotAt > 4f)
            {
                _desyncSummary = "Host snapshots stalled - request a resync if gameplay diverges.";
                _desyncSeverity = SessionIssueSeverity.Warning;
                return;
            }

            _desyncSummary = string.Empty;
            _desyncSeverity = SessionIssueSeverity.None;
        }

        private static void UpdateLocalGuestReady(bool ready, bool notifyHost)
        {
            _localGuestReady = ready;

            if (!notifyHost || Plugin.Net == null || !Plugin.Net.IsConnected || MultiplayerSession.IsHost)
                return;

            var pkt = new SessionSignalPacket
            {
                Signal = (byte)(ready ? SessionSignalKind.GuestReady : SessionSignalKind.GuestUnready),
                SaveRevision = _saveRevision,
            };
            Plugin.Net.SendSessionSignal(ref pkt);
        }

        private static ushort NextSaveRevision()
        {
            unchecked
            {
                _saveRevision++;
                if (_saveRevision == 0)
                    _saveRevision = 1;
            }

            return _saveRevision;
        }

        private static void TrackBattleStatsLevel()
        {
            int level = GetCurrentBattleLevel();
            if (_trackedBattleLevel == level)
            {
                if (_preserveBattleStatsForRetryReload
                 && _preserveBattleStatsSawInactive
                 && level == _preservedRetryBattleLevel)
                    ClearRetryBattleStatsPreservation(resetSendBookkeeping: true);

                return;
            }

            if (_preserveBattleStatsForRetryReload)
            {
                if (level < 0)
                {
                    _preserveBattleStatsSawInactive = true;
                    _trackedBattleLevel = level;
                    ResetHostBattleTimingIfInactive();
                    return;
                }

                if (level == _preservedRetryBattleLevel && _preserveBattleStatsSawInactive)
                {
                    _trackedBattleLevel = level;
                    ClearRetryBattleStatsPreservation(resetSendBookkeeping: true);
                    return;
                }

                if (Time.unscaledTime - _preserveBattleStatsStartedAt < RetryStatsPreserveTimeoutSeconds())
                    return;

                ClearRetryBattleStatsPreservation(resetSendBookkeeping: false);
            }

            _trackedBattleLevel = level;
            _localDeaths = 0;
            _localRetries = 0;
            _localParries = 0;
            _remoteBattleStats.Clear();
            _lastLocalBattleStatsSentAt = -1f;
            _lastSentBattleLevel = -1;
            _lastSentDeaths = -1;
            _lastSentParries = -1;
            if (level < 0)
                ResetHostBattleTimingIfInactive();
        }

        private static void ResetHostBattleTimingIfInactive()
        {
            _hostBattleLevel = -1;
            _hostBattleElapsedSeconds = 0f;
            _hostBattleSnapshotAt = -1f;
            _hostBattlePaused = false;
            _hostBattleDeaths = 0;
            _hostBattleRetries = 0;
            _hostBattleParries = 0;
            _lastBattleTimingLogAt = -1f;
        }

        private static float RetryStatsPreserveTimeoutSeconds()
        {
            return 8f;
        }

        private static void ClearRetryBattleStatsPreservation(bool resetSendBookkeeping)
        {
            _preserveBattleStatsForRetryReload = false;
            _preservedRetryBattleLevel = -1;
            _preserveBattleStatsSawInactive = false;
            _preserveBattleStatsStartedAt = -1f;

            if (!resetSendBookkeeping)
                return;

            _lastLocalBattleStatsSentAt = -1f;
            _lastSentBattleLevel = -1;
            _lastSentDeaths = -1;
            _lastSentParries = -1;
        }

        private static void MaybeSendLocalBattleStats(bool force)
        {
            if (!MultiplayerSession.IsActive
             || MultiplayerSession.IsHost
             || Plugin.Net == null
             || !Plugin.Net.IsConnected
             || !IsBattleActive())
            {
                return;
            }

            int level = GetCurrentBattleLevel();
            float now = Time.unscaledTime;
            bool changed = level != _lastSentBattleLevel
                        || _localDeaths != _lastSentDeaths
                        || _localParries != _lastSentParries;
            if (!force && !changed && _lastLocalBattleStatsSentAt > 0f && now - _lastLocalBattleStatsSentAt < 1f)
                return;

            var pkt = new BattleAssistStatsPacket
            {
                ParticipantId = (byte)MultiplayerSession.LocalId,
                CurrentLevel = level,
                Deaths = ClampToUShort(_localDeaths),
                Parries = ClampToUShort(_localParries),
                Tick = MultiplayerSession.Tick,
            };

            Plugin.Net.SendBattleAssistStats(ref pkt);
            _lastLocalBattleStatsSentAt = now;
            _lastSentBattleLevel = level;
            _lastSentDeaths = _localDeaths;
            _lastSentParries = _localParries;
        }

        private static int GetAggregateBattleDeaths(int level)
        {
            if (level < 0)
                return 0;

            int total = _localDeaths;
            foreach (var entry in _remoteBattleStats.Values)
                if (entry.Level == level)
                    total += entry.Deaths;
            return total;
        }

        private static int GetAggregateBattleParries(int level)
        {
            if (level < 0)
                return 0;

            int total = _localParries;
            foreach (var entry in _remoteBattleStats.Values)
                if (entry.Level == level)
                    total += entry.Parries;
            return total;
        }

        private static ushort ClampToUShort(int value)
        {
            if (value <= 0)
                return 0;
            if (value >= ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }

        private static bool IsBattleActive()
        {
            try
            {
                return Level.Current != null && Level.Current.LevelType == Level.Type.Battle;
            }
            catch
            {
                return false;
            }
        }

        private static int GetCurrentBattleLevel()
        {
            if (!IsBattleActive())
                return -1;

            try
            {
                return (int)Level.Current.CurrentLevel;
            }
            catch
            {
                return -1;
            }
        }

        private static float EstimateHostBattleElapsedSeconds()
        {
            float hostElapsed = Mathf.Max(0f, _hostBattleElapsedSeconds);
            if (!_hostBattlePaused && _hostBattleSnapshotAt >= 0f)
                hostElapsed += Mathf.Max(0f, Time.unscaledTime - _hostBattleSnapshotAt);
            return hostElapsed;
        }

        private static void LogBattleTimingOffset()
        {
            if (MultiplayerSession.IsHost || !IsBattleActive() || _hostBattleLevel != GetCurrentBattleLevel())
                return;
            if (!BattleAssistHud.HasHostDiagnosticSeedForCurrentBattle)
                return;

            float now = Time.unscaledTime;
            if (_lastBattleTimingLogAt > 0f && now - _lastBattleTimingLogAt < 2f)
                return;

            float localElapsed = BattleAssistHud.ElapsedSeconds;
            float hostElapsed = EstimateHostBattleElapsedSeconds();
            float offset = localElapsed - hostElapsed;
            string message = "[BattleAssist] Guest timer offset local-host="
                + FormatSignedSeconds(offset)
                + "s (local="
                + localElapsed.ToString("0.0")
                + "s, host="
                + hostElapsed.ToString("0.0")
                + "s).";

            if (Mathf.Abs(offset) >= 0.25f)
                Plugin.Log.LogWarning(message);
            else
                Plugin.Log.LogInfo(message);

            _lastBattleTimingLogAt = now;
        }

        private static string FormatSignedSeconds(float value)
        {
            if (value > 0f)
                return "+" + value.ToString("0.0");
            return value.ToString("0.0");
        }

        private static byte BuildSnapshotFlags()
        {
            byte flags = 0;
            if (_hasTrackedSave) flags |= 1;
            if (Level.Current != null) flags |= 2;
            if (PauseManager.state == PauseManager.State.Paused) flags |= 4;
            return flags;
        }

        private static bool ShouldUseFastBattleSnapshotCadence()
        {
            return IsBattleActive() && IsBattleTimerPaused();
        }

        private static bool IsBattleTimerPaused()
        {
            try
            {
                return PauseManager.state == PauseManager.State.Paused || CupheadTime.IsPaused();
            }
            catch
            {
                return PauseManager.state == PauseManager.State.Paused;
            }
        }

        private static void TryAutoFollowHostSnapshot(SessionSnapshotPacket snapshot)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || MultiplayerSession.IsHost)
                return;

            string localScene = GetActiveSceneName();
            if (string.IsNullOrEmpty(snapshot.SceneName)
             || string.IsNullOrEmpty(localScene)
             || string.Equals(snapshot.SceneName, localScene, StringComparison.OrdinalIgnoreCase))
                return;

            string targetKey = snapshot.IsInLevel
                ? "level:" + snapshot.CurrentLevel
                : "scene:" + snapshot.SceneName + ":" + snapshot.CurrentMapScene;
            if (targetKey == _lastAutoFollowTarget && Time.unscaledTime - _lastAutoFollowAt < 2.5f)
                return;

            if (snapshot.IsInLevel && Enum.IsDefined(typeof(Levels), snapshot.CurrentLevel))
            {
                _lastAutoFollowTarget = targetKey;
                _lastAutoFollowAt = Time.unscaledTime;
                ConnectionHUD.Show("Following host into level...");
                Plugin.Log.LogInfo("[Session] Auto-following host level " + ((Levels)snapshot.CurrentLevel) + ".");
                LevelStartSync.BeginClientLevelLoad((Levels)snapshot.CurrentLevel);
                SceneSyncState.AllowNextClientLevelLoad();
                SceneLoader.LoadLevel((Levels)snapshot.CurrentLevel, SceneLoader.Transition.Iris);
                return;
            }

            Scenes targetScene;
            if (!TryResolveSnapshotScene(snapshot, out targetScene))
                return;
            if (!ShouldAutoFollowScene(targetScene))
                return;

            _lastAutoFollowTarget = targetKey;
            _lastAutoFollowAt = Time.unscaledTime;
            ConnectionHUD.Show("Following host scene...");
            Plugin.Log.LogInfo("[Session] Auto-following host scene " + targetScene + ".");
            SceneSyncState.AllowNextClientSceneLoad();
            SceneLoader.LoadScene(
                targetScene,
                SceneLoader.Transition.Iris,
                SceneLoader.Transition.Iris,
                SceneLoader.Icon.Hourglass,
                null);
        }

        private static bool TryResolveSnapshotScene(SessionSnapshotPacket snapshot, out Scenes scene)
        {
            if (TryParseSceneName(snapshot.SceneName, out scene))
                return true;

            if (IsDefinedScene(snapshot.CurrentMapScene))
            {
                scene = (Scenes)snapshot.CurrentMapScene;
                return true;
            }

            scene = Scenes.scene_title;
            return false;
        }

        private static bool TryParseSceneName(string sceneName, out Scenes scene)
        {
            if (!string.IsNullOrEmpty(sceneName))
            {
                foreach (Scenes candidate in Enum.GetValues(typeof(Scenes)))
                {
                    if (string.Equals(candidate.ToString(), sceneName, StringComparison.OrdinalIgnoreCase))
                    {
                        scene = candidate;
                        return true;
                    }
                }
            }

            scene = Scenes.scene_title;
            return false;
        }

        private static bool IsDefinedScene(int sceneValue)
        {
            return Enum.IsDefined(typeof(Scenes), sceneValue);
        }

        private static bool ShouldAutoFollowScene(Scenes scene)
        {
            return scene != Scenes.scene_start
                && scene != Scenes.scene_title
                && scene != Scenes.scene_slot_select
                && scene != Scenes.scene_menu;
        }

        private static string GetActiveSceneName()
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                return string.IsNullOrEmpty(scene.name) ? string.Empty : scene.name;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsTitleScene()
        {
            return string.Equals(GetActiveSceneName(), "scene_title", StringComparison.OrdinalIgnoreCase);
        }

        private static SessionIssueSeverity MaxSeverity(SessionIssueSeverity left, SessionIssueSeverity right)
        {
            return left >= right ? left : right;
        }

        private static bool IsStaleHostSnapshot(SessionSnapshotPacket pkt)
        {
            if (!_lastHostSnapshot.HasValue)
                return false;

            var previous = _lastHostSnapshot.Value;
            if (pkt.HostTick == 0 || previous.HostTick == 0)
                return false;

            return NetTick.IsOlder(pkt.HostTick, previous.HostTick);
        }

        private static void HandleSessionStarted()
        {
            _localSaveProfile = null;
            _remoteSaveProfile = null;
            _lastHostSnapshot = null;
            _trackedSaveSlot = byte.MaxValue;
            _trackedMapScene = Scenes.scene_map_world_1;
            _trackedSaveEmpty = false;
            _hasTrackedSave = false;
            _hasCompletedHandshake = false;
            _saveRevision = 0;
            _localGuestReady = false;
            _remoteGuestReady = false;
            _nextHostSnapshotAt = 0f;
            _lastHostSnapshotAt = -1f;
            _lastRecoveryRequestedAt = -1f;
            _lastRecoveryBundleAt = -1f;
            _lastAutoFollowAt = -1f;
            _lastAutoFollowTarget = string.Empty;
            _sceneMismatchStreak = 0;
            _recoveryRequestCount = 0;
            _recoveryBundleCount = 0;
            _compatibilitySummary = "Compatibility: waiting for host save.";
            _compatibilitySeverity = SessionIssueSeverity.Info;
            _desyncSummary = string.Empty;
            _desyncSeverity = SessionIssueSeverity.None;
            _localDeaths = 0;
            _localRetries = 0;
            _localParries = 0;
            ResetBattleTracking();
            ResetPauseTracking();
        }

        private static void HandleSessionEnded()
        {
            _localSaveProfile = null;
            _remoteSaveProfile = null;
            _lastHostSnapshot = null;
            _trackedSaveSlot = byte.MaxValue;
            _trackedMapScene = Scenes.scene_map_world_1;
            _trackedSaveEmpty = false;
            _hasTrackedSave = false;
            _hasCompletedHandshake = false;
            _saveRevision = 0;
            _localGuestReady = false;
            _remoteGuestReady = false;
            _nextHostSnapshotAt = 0f;
            _lastHostSnapshotAt = -1f;
            _lastRecoveryRequestedAt = -1f;
            _lastRecoveryBundleAt = -1f;
            _lastAutoFollowAt = -1f;
            _lastAutoFollowTarget = string.Empty;
            _sceneMismatchStreak = 0;
            _recoveryRequestCount = 0;
            _recoveryBundleCount = 0;
            _compatibilitySummary = "Compatibility: waiting for host save.";
            _compatibilitySeverity = SessionIssueSeverity.Info;
            _desyncSummary = string.Empty;
            _desyncSeverity = SessionIssueSeverity.None;
            _localDeaths = 0;
            _localRetries = 0;
            _localParries = 0;
            ResetBattleTracking();
            ResetPauseTracking();
        }

        private static void ResetBattleTracking()
        {
            _remoteBattleStats.Clear();
            _trackedBattleLevel = -1;
            _preserveBattleStatsForRetryReload = false;
            _preservedRetryBattleLevel = -1;
            _preserveBattleStatsSawInactive = false;
            _preserveBattleStatsStartedAt = -1f;
            _lastLocalBattleStatsSentAt = -1f;
            _lastSentBattleLevel = -1;
            _lastSentDeaths = -1;
            _lastSentParries = -1;
            _hostBattleLevel = -1;
            _hostBattleElapsedSeconds = 0f;
            _hostBattleSnapshotAt = -1f;
            _hostBattlePaused = false;
            _hostBattleDeaths = 0;
            _hostBattleRetries = 0;
            _hostBattleParries = 0;
            _lastBattleTimingLogAt = -1f;
        }

        private static void ResetPauseTracking()
        {
            _hasLastBroadcastPauseState = false;
            _lastBroadcastPauseState = false;
            _lastAppliedHostPauseState = false;
            _lastAppliedHostPauseAt = -1f;
        }
    }
}
