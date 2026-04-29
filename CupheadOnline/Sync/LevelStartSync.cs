using System;
using CupheadOnline.Patches;
using CupheadOnline.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadOnline.Sync
{
    internal static class LevelStartSync
    {
        const float HostTimeout = 12f;
        const float ClientTimeout = 18f;
        const float BattleStartTimeout = 10f;
        const float HighLatencyVisualAlignmentSeconds = 0.250f;

        static bool _hostGateActive;
        static bool _hostSceneLoaded;
        static bool _hostGuestLoaded;
        static bool _hostReleaseSent;
        static Levels _hostLevel;
        static float _hostGateStartedAt;
        static float _hostLocalReleaseAt;
        static float _hostReleaseDelaySeconds;
        static bool _hostIntroGateActive;
        static bool _hostIntroGateComplete;
        static bool _hostGuestIntroReady;
        static bool _hostIntroReleaseSent;
        static float _hostIntroGateStartedAt;
        static float _hostIntroLocalReleaseAt;
        static bool _hostBattleGateActive;
        static bool _hostBattleGateComplete;
        static bool _hostGuestBattleReady;
        static bool _hostBattleReleaseSent;
        static float _hostBattleGateStartedAt;
        static float _hostBattleLocalReleaseAt;
        static Level.IntroProperties _hostPendingBattleIntro;

        static bool _clientGateActive;
        static bool _clientLoadedSent;
        static bool _clientReleaseReceived;
        static Levels _clientLevel;
        static float _clientGateStartedAt;
        static long _clientReleaseUtcTicks;
        static bool _clientIntroGateActive;
        static bool _clientIntroGateComplete;
        static bool _clientIntroReadySent;
        static bool _clientIntroReleaseReceived;
        static float _clientIntroGateStartedAt;
        static long _clientIntroReleaseUtcTicks;
        static bool _clientBattleGateActive;
        static bool _clientBattleGateComplete;
        static bool _clientBattleReadySent;
        static bool _clientBattleReleaseReceived;
        static float _clientBattleGateStartedAt;
        static long _clientBattleReleaseUtcTicks;
        static Level.IntroProperties _clientPendingBattleIntro;

        static bool _holdingTimeScale;
        static float _lastLogAt;
        static bool _hostStartHeldLogged;
        static bool _clientStartHeldLogged;
        static float _lastHostWaitingRecoveryAt = -1f;

        public static bool IsHostWaitingForGuestStart => _hostGateActive
            || (_hostIntroGateActive && !_hostIntroGateComplete)
            || (_hostBattleGateActive && !_hostBattleGateComplete);
        public static bool IsClientWaitingForStartRelease => (_clientGateActive && !IsReleaseDue(_clientReleaseReceived, _clientReleaseUtcTicks))
            || (_clientIntroGateActive && !IsReleaseDue(_clientIntroReleaseReceived, _clientIntroReleaseUtcTicks))
            || (_clientBattleGateActive && !IsReleaseDue(_clientBattleReleaseReceived, _clientBattleReleaseUtcTicks));
        public static float LastHostReleaseDelaySeconds => _hostReleaseDelaySeconds;

        public static void BeginHostLevelLoad(Levels level)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            _hostGateActive = true;
            _hostSceneLoaded = false;
            _hostGuestLoaded = false;
            _hostReleaseSent = false;
            _hostLevel = level;
            _hostGateStartedAt = Time.unscaledTime;
            _hostLocalReleaseAt = 0f;
            _hostReleaseDelaySeconds = 0f;
            _hostIntroGateActive = false;
            _hostIntroGateComplete = false;
            _hostGuestIntroReady = false;
            _hostIntroReleaseSent = false;
            _hostIntroGateStartedAt = 0f;
            _hostIntroLocalReleaseAt = 0f;
            _hostBattleGateActive = false;
            _hostBattleGateComplete = false;
            _hostGuestBattleReady = false;
            _hostBattleReleaseSent = false;
            _hostBattleGateStartedAt = 0f;
            _hostBattleLocalReleaseAt = 0f;
            _hostPendingBattleIntro = null;
            _hostStartHeldLogged = false;
            _lastHostWaitingRecoveryAt = -1f;
            _lastLogAt = -100f;
            HighLatencyInputSync.ResetLevelClock();
            Plugin.Log.LogInfo("[LevelStartSync] Host waiting for guest to load " + level + ".");
        }

        public static void BeginClientLevelLoad(Levels level)
        {
            if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            _clientGateActive = true;
            _clientLoadedSent = false;
            _clientReleaseReceived = false;
            _clientLevel = level;
            _clientGateStartedAt = Time.unscaledTime;
            _clientReleaseUtcTicks = 0L;
            _clientIntroGateActive = false;
            _clientIntroGateComplete = false;
            _clientIntroReadySent = false;
            _clientIntroReleaseReceived = false;
            _clientIntroGateStartedAt = 0f;
            _clientIntroReleaseUtcTicks = 0L;
            _clientBattleGateActive = false;
            _clientBattleGateComplete = false;
            _clientBattleReadySent = false;
            _clientBattleReleaseReceived = false;
            _clientBattleGateStartedAt = 0f;
            _clientBattleReleaseUtcTicks = 0L;
            _clientPendingBattleIntro = null;
            _clientStartHeldLogged = false;
            _lastLogAt = -100f;
            HighLatencyInputSync.ResetLevelClock();
            Plugin.Log.LogInfo("[LevelStartSync] Client waiting for host start release for " + level + ".");
        }

        public static void HandleRemoteLevelLoaded(ushort levelToken)
        {
            if (!MultiplayerSession.IsHost || !_hostGateActive)
                return;

            if (levelToken != ToToken(_hostLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored loaded signal for stale level token " + levelToken + ".");
                return;
            }

            _hostGuestLoaded = true;
            Plugin.Log.LogInfo("[LevelStartSync] Guest loaded " + _hostLevel + ".");
        }

        public static void HandleRemoteLevelStartRelease(ushort levelToken, long releaseUtcTicks)
        {
            if (MultiplayerSession.IsHost || !_clientGateActive)
                return;

            if (levelToken != ToToken(_clientLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored release signal for stale level token " + levelToken + ".");
                return;
            }

            _clientReleaseReceived = true;
            _clientReleaseUtcTicks = releaseUtcTicks;
            Plugin.Log.LogInfo("[LevelStartSync] Host released level start for " + _clientLevel + ReleaseTargetSuffix(releaseUtcTicks) + ".");
        }

        public static void HandleRemoteLevelIntroReady(ushort levelToken)
        {
            if (!MultiplayerSession.IsHost || _hostIntroGateComplete)
                return;

            if (levelToken != ToToken(_hostLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored intro-ready signal for stale level token " + levelToken + ".");
                return;
            }

            _hostGuestIntroReady = true;
            if (_hostGateActive && !_hostGuestLoaded)
            {
                _hostGuestLoaded = true;
                Plugin.Log.LogInfo("[LevelStartSync] Treating guest pre-intro signal as level-loaded for " + _hostLevel + ".");
            }
            Plugin.Log.LogInfo("[LevelStartSync] Guest reached pre-intro start gate for " + _hostLevel + ".");
        }

        public static void HandleRemoteLevelIntroRelease(ushort levelToken, long releaseUtcTicks)
        {
            if (MultiplayerSession.IsHost || _clientIntroGateComplete)
                return;

            if (levelToken != ToToken(_clientLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored intro release for stale level token " + levelToken + ".");
                return;
            }

            _clientIntroReleaseReceived = true;
            _clientIntroReleaseUtcTicks = releaseUtcTicks;
            Plugin.Log.LogInfo("[LevelStartSync] Host released pre-intro start gate for " + _clientLevel + ReleaseTargetSuffix(releaseUtcTicks) + ".");
        }

        public static void HandleRemoteLevelBattleReady(ushort levelToken)
        {
            if (!MultiplayerSession.IsHost || _hostBattleGateComplete)
                return;

            if (levelToken != ToToken(_hostLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored battle-ready signal for stale level token " + levelToken + ".");
                return;
            }

            _hostGuestBattleReady = true;
            Plugin.Log.LogInfo("[LevelStartSync] Guest reached first playable battle frame for " + _hostLevel + ".");
        }

        public static void HandleRemoteLevelBattleRelease(ushort levelToken, long releaseUtcTicks)
        {
            if (MultiplayerSession.IsHost || _clientBattleGateComplete)
                return;

            if (levelToken != ToToken(_clientLevel))
            {
                Plugin.Log.LogInfo("[LevelStartSync] Ignored battle release for stale level token " + levelToken + ".");
                return;
            }

            _clientBattleReleaseReceived = true;
            _clientBattleReleaseUtcTicks = releaseUtcTicks;
            Plugin.Log.LogInfo("[LevelStartSync] Host released first playable battle frame for " + _clientLevel + ReleaseTargetSuffix(releaseUtcTicks) + ".");
        }

        public static void NotifyLocalTransitionInComplete(Level level)
        {
            if (level == null || !MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            ushort levelToken = ToToken(level.CurrentLevel);

            if (MultiplayerSession.IsHost)
            {
                if (!_hostGateActive || levelToken != ToToken(_hostLevel))
                    return;

                _hostSceneLoaded = true;
                HoldLevelStart("host-transition");
                Plugin.Log.LogInfo("[LevelStartSync] Host transition complete for " + _hostLevel + ".");
                return;
            }

            if (!_clientGateActive || levelToken != ToToken(_clientLevel))
                return;

            HoldLevelStart("client-transition");
            SendClientLoadedIfNeeded();
            Plugin.Log.LogInfo("[LevelStartSync] Client transition complete for " + _clientLevel + ".");
        }

        public static void NotifyLocalLevelStartEntering(Level level)
        {
            if (level == null || !MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            ushort levelToken = ToToken(level.CurrentLevel);

            if (MultiplayerSession.IsHost)
            {
                if (!_hostGateActive || levelToken != ToToken(_hostLevel))
                    return;

                HoldLevelStart("host-level-start");
                if (!_hostStartHeldLogged)
                {
                    _hostStartHeldLogged = true;
                    Plugin.Log.LogInfo("[LevelStartSync] Host froze level Start for " + _hostLevel + " before intro timing can advance.");
                }
                return;
            }

            if (!_clientGateActive || levelToken != ToToken(_clientLevel))
                return;

            HoldLevelStart("client-level-start");
            if (!_clientStartHeldLogged)
            {
                _clientStartHeldLogged = true;
                Plugin.Log.LogInfo("[LevelStartSync] Client froze level Start for " + _clientLevel + " before intro timing can advance.");
            }
        }

        public static void NotifyBattleIntroSequenceStarting(Level level)
        {
            if (level == null || !MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (!Plugin.VanillaTwoPlayerOnline || !IsInLevelScene())
                return;

            ushort levelToken = ToToken(level.CurrentLevel);

            if (MultiplayerSession.IsHost)
            {
                if (_hostIntroGateComplete || levelToken != ToToken(_hostLevel))
                    return;

                if (_hostGateActive && !_hostSceneLoaded)
                {
                    _hostSceneLoaded = true;
                    Plugin.Log.LogInfo("[LevelStartSync] Treating host pre-intro signal as scene-loaded for " + _hostLevel + ".");
                }

                if (!_hostIntroGateActive)
                {
                    _hostIntroGateActive = true;
                    _hostIntroGateStartedAt = Time.unscaledTime;
                    Plugin.Log.LogInfo("[LevelStartSync] Host reached pre-intro start gate for " + _hostLevel + "; waiting for guest.");
                }

                HoldLevelStart("host-intro-start");
                return;
            }

            if (_clientIntroGateComplete || levelToken != ToToken(_clientLevel))
                return;

            if (!_clientIntroGateActive)
            {
                _clientIntroGateActive = true;
                _clientIntroGateStartedAt = Time.unscaledTime;
                Plugin.Log.LogInfo("[LevelStartSync] Client reached pre-intro start gate for " + _clientLevel + "; waiting for host release.");
            }

            HoldLevelStart("client-intro-start");
            SendClientLoadedIfNeeded();

            if (!_clientIntroReadySent)
            {
                SendSignal(SessionSignalKind.LevelIntroReady, ToToken(_clientLevel));
                _clientIntroReadySent = true;
                Plugin.Log.LogInfo("[LevelStartSync] Client reported pre-intro start gate for " + _clientLevel + ".");
            }
        }

        public static bool TryHoldBattleIntroReady(Level.IntroProperties intro)
        {
            if (intro == null || !MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return false;
            if (!Plugin.VanillaTwoPlayerOnline || !IsInLevelScene())
                return false;
            if (Level.Current == null)
                return false;

            if (MultiplayerSession.IsHost)
            {
                if (_hostBattleGateComplete)
                    return false;

                _hostPendingBattleIntro = intro;
                if (!_hostBattleGateActive)
                {
                    _hostBattleGateActive = true;
                    _hostBattleGateStartedAt = Time.unscaledTime;
                    HoldLevelStart("host-battle-ready");
                    Plugin.Log.LogInfo("[LevelStartSync] Host reached battle intro ready point for " + _hostLevel + "; waiting for guest.");
                }
                return true;
            }

            if (_clientBattleGateComplete)
                return false;

            _clientPendingBattleIntro = intro;
            if (!_clientBattleGateActive)
            {
                _clientBattleGateActive = true;
                _clientBattleGateStartedAt = Time.unscaledTime;
                HoldLevelStart("client-battle-ready");
                Plugin.Log.LogInfo("[LevelStartSync] Client reached battle intro ready point for " + _clientLevel + "; waiting for host release.");
            }

            if (!_clientBattleReadySent)
            {
                SendSignal(SessionSignalKind.LevelBattleReady, ToToken(_clientLevel));
                _clientBattleReadySent = true;
                Plugin.Log.LogInfo("[LevelStartSync] Client reported battle intro ready point for " + _clientLevel + ".");
            }

            return true;
        }

        public static void Update()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                ClearAll();
                return;
            }

            if (MultiplayerSession.IsHost)
            {
                UpdateHost();
                UpdateHostIntroStart();
                UpdateHostBattleStart();
            }
            else
            {
                UpdateClient();
                UpdateClientIntroStart();
                UpdateClientBattleStart();
            }
        }

        static void UpdateHost()
        {
            if (!_hostGateActive)
                return;

            if (_hostSceneLoaded)
                HoldLevelStart("host");

            if (_hostReleaseSent)
            {
                if (Time.unscaledTime >= _hostLocalReleaseAt)
                {
                    _hostGateActive = false;
                    ReleaseLocal("host-latency-aligned");
                    return;
                }

                LogWaiting("Host release sent; holding local start for "
                    + Mathf.Max(0f, _hostLocalReleaseAt - Time.unscaledTime).ToString("0.00")
                    + "s so the guest receives the release.");
                return;
            }

            if (_hostSceneLoaded && _hostGuestLoaded)
            {
                ReleaseHostAndGuest();
                return;
            }

            if (_hostSceneLoaded && !_hostGuestLoaded)
                MaybeBroadcastWaitingRecovery("Guest has not reached the level start yet.");

            if (Time.unscaledTime - _hostGateStartedAt > HostTimeout)
            {
                Plugin.Log.LogWarning("[LevelStartSync] Guest did not report level load before timeout; releasing host.");
                ReleaseHostAndGuest();
            }
            else
            {
                LogWaiting("Host level start is paused until the guest finishes loading.");
            }
        }

        static void UpdateClient()
        {
            if (!_clientGateActive)
                return;

            if (IsInLevelScene() && _clientLoadedSent)
            {
                HoldLevelStart("client");
            }

            if (IsReleaseDue(_clientReleaseReceived, _clientReleaseUtcTicks))
            {
                _clientGateActive = false;
                ReleaseLocal("client");
                return;
            }

            if (Time.unscaledTime - _clientGateStartedAt > ClientTimeout)
            {
                Plugin.Log.LogWarning("[LevelStartSync] Host release did not arrive before timeout; releasing client.");
                _clientGateActive = false;
                ReleaseLocal("client-timeout");
            }
            else
            {
                LogWaiting("Client level start is paused until the host releases both players.");
            }
        }

        static void UpdateHostIntroStart()
        {
            if (_hostIntroGateComplete)
                return;
            if (!_hostIntroGateActive)
                return;

            HoldLevelStart("host-intro");

            if (!_hostIntroReleaseSent)
            {
                if (_hostGuestIntroReady || Time.unscaledTime - _hostIntroGateStartedAt > BattleStartTimeout)
                {
                    if (!_hostGuestIntroReady)
                        Plugin.Log.LogWarning("[LevelStartSync] Guest did not report pre-intro start gate before timeout; releasing host.");

                    _hostIntroReleaseSent = true;
                    _hostReleaseDelaySeconds = EstimateOneWayReleaseDelaySeconds();
                    _hostIntroLocalReleaseAt = Time.unscaledTime + _hostReleaseDelaySeconds;
                    SendSignal(SessionSignalKind.LevelIntroRelease, ToToken(_hostLevel), UtcTicksAfterSeconds(_hostReleaseDelaySeconds));
                    Plugin.Log.LogInfo("[LevelStartSync] Released pre-intro start gate for "
                        + _hostLevel
                        + "; delaying host local intro release by "
                        + _hostReleaseDelaySeconds.ToString("0.000")
                        + "s.");
                }
                else
                {
                    LogWaiting("Host pre-intro start is paused until the guest reaches it.");
                }

                return;
            }

            if (Time.unscaledTime < _hostIntroLocalReleaseAt)
            {
                LogWaiting("Host intro release sent; holding intro for "
                    + Mathf.Max(0f, _hostIntroLocalReleaseAt - Time.unscaledTime).ToString("0.00")
                    + "s so the guest receives the release.");
                return;
            }

            _hostIntroGateActive = false;
            _hostIntroGateComplete = true;
            ReleaseLocal("host-intro-latency-aligned");
        }

        static void UpdateClientIntroStart()
        {
            if (_clientIntroGateComplete)
                return;
            if (!_clientIntroGateActive)
                return;

            HoldLevelStart("client-intro");

            if (IsReleaseDue(_clientIntroReleaseReceived, _clientIntroReleaseUtcTicks))
            {
                _clientIntroGateActive = false;
                _clientIntroGateComplete = true;
                ReleaseLocal("client-intro");
                return;
            }

            if (Time.unscaledTime - _clientIntroGateStartedAt > BattleStartTimeout + ClientTimeout)
            {
                Plugin.Log.LogWarning("[LevelStartSync] Host intro release did not arrive before timeout; releasing client.");
                _clientIntroGateActive = false;
                _clientIntroGateComplete = true;
                ReleaseLocal("client-intro-timeout");
            }
            else
            {
                LogWaiting("Client pre-intro start is paused until the host releases both players.");
            }
        }

        static void UpdateHostBattleStart()
        {
            if (_hostBattleGateComplete)
                return;
            if (!_hostBattleGateActive)
                return;

            HoldLevelStart("host-battle");

            if (!_hostBattleReleaseSent)
            {
                if (_hostGuestBattleReady || Time.unscaledTime - _hostBattleGateStartedAt > BattleStartTimeout)
                {
                    if (!_hostGuestBattleReady)
                        Plugin.Log.LogWarning("[LevelStartSync] Guest did not report first playable battle frame before timeout; releasing host.");

                    _hostBattleReleaseSent = true;
                    _hostReleaseDelaySeconds = EstimateOneWayReleaseDelaySeconds();
                    _hostBattleLocalReleaseAt = Time.unscaledTime + _hostReleaseDelaySeconds;
                    SendSignal(SessionSignalKind.LevelBattleRelease, ToToken(_hostLevel), UtcTicksAfterSeconds(_hostReleaseDelaySeconds));
                    Plugin.Log.LogInfo("[LevelStartSync] Released first playable battle frame for "
                        + _hostLevel
                        + "; delaying host local battle release by "
                        + _hostReleaseDelaySeconds.ToString("0.000")
                        + "s.");
                }
                else
                {
                    LogWaiting("Host first playable battle frame is paused until the guest reaches it.");
                }

                return;
            }

            if (Time.unscaledTime < _hostBattleLocalReleaseAt)
            {
                LogWaiting("Host battle release sent; holding first playable frame for "
                    + Mathf.Max(0f, _hostBattleLocalReleaseAt - Time.unscaledTime).ToString("0.00")
                    + "s so the guest receives the release.");
                return;
            }

            CompleteHostBattleIntroGate("host-battle-latency-aligned");
        }

        static void CompleteHostBattleIntroGate(string role)
        {
            PrepareDeterministicBattleStart(_hostLevel, role);
            if (_hostPendingBattleIntro != null)
                _hostPendingBattleIntro.readyComplete = true;
            _hostPendingBattleIntro = null;
            _hostBattleGateActive = false;
            _hostBattleGateComplete = true;
            ReleaseLocal(role);
        }

        static void UpdateClientBattleStart()
        {
            if (_clientBattleGateComplete)
                return;
            if (!_clientBattleGateActive)
                return;

            HoldLevelStart("client-battle");

            if (IsReleaseDue(_clientBattleReleaseReceived, _clientBattleReleaseUtcTicks))
            {
                CompleteClientBattleIntroGate("client-battle");
                return;
            }

            if (Time.unscaledTime - _clientBattleGateStartedAt > BattleStartTimeout + ClientTimeout)
            {
                Plugin.Log.LogWarning("[LevelStartSync] Host battle release did not arrive before timeout; releasing client.");
                CompleteClientBattleIntroGate("client-battle-timeout");
            }
            else
            {
                LogWaiting("Client first playable battle frame is paused until the host releases both players.");
            }
        }

        static void CompleteClientBattleIntroGate(string role)
        {
            PrepareDeterministicBattleStart(_clientLevel, role);
            if (_clientPendingBattleIntro != null)
                _clientPendingBattleIntro.readyComplete = true;
            _clientPendingBattleIntro = null;
            _clientBattleGateActive = false;
            _clientBattleGateComplete = true;
            ReleaseLocal(role);
        }

        static void SendClientLoadedIfNeeded()
        {
            if (_clientLoadedSent)
                return;

            SendSignal(SessionSignalKind.LevelLoaded, ToToken(_clientLevel));
            _clientLoadedSent = true;
            Plugin.Log.LogInfo("[LevelStartSync] Client reported transition-ready for " + _clientLevel + ".");
        }

        static void ReleaseHostAndGuest()
        {
            if (!_hostReleaseSent)
            {
                _hostReleaseSent = true;
                _hostReleaseDelaySeconds = EstimateOneWayReleaseDelaySeconds();
                _hostLocalReleaseAt = Time.unscaledTime + _hostReleaseDelaySeconds;
                SendSignal(SessionSignalKind.LevelStartRelease, ToToken(_hostLevel), UtcTicksAfterSeconds(_hostReleaseDelaySeconds));
                Plugin.Log.LogInfo("[LevelStartSync] Released level start for "
                    + _hostLevel
                    + "; delaying host local release by "
                    + _hostReleaseDelaySeconds.ToString("0.000")
                    + "s for latency alignment.");
            }

            if (_hostReleaseDelaySeconds <= 0.001f)
            {
                _hostGateActive = false;
                ReleaseLocal("host");
            }
        }

        static void HoldLevelStart(string role)
        {
            if (PauseManager.state == PauseManager.State.Paused)
                return;

            if (Time.timeScale != 0f)
                Time.timeScale = 0f;
            if (!CupheadTime.IsPaused())
                CupheadTime.SetAll(0f);
            _holdingTimeScale = true;
        }

        static void ReleaseLocal(string role)
        {
            if (HasActiveLocalHold())
            {
                HoldLevelStart(role + "-continued");
                Plugin.Log.LogInfo("[LevelStartSync] Local level start gate completed on " + role + "; continuing hold for another start gate.");
                return;
            }

            if (_holdingTimeScale && PauseManager.state != PauseManager.State.Paused)
            {
                Time.timeScale = 1f;
                CupheadTime.SetAll(1f);
            }
            _holdingTimeScale = false;
            HighLatencyInputSync.NotifyLevelStartReleased();
            Plugin.Log.LogInfo("[LevelStartSync] Local level start gate released on " + role + ".");
        }

        static bool HasActiveLocalHold()
        {
            if (MultiplayerSession.IsHost)
            {
                return _hostGateActive
                    || (_hostIntroGateActive && !_hostIntroGateComplete)
                    || (_hostBattleGateActive && !_hostBattleGateComplete);
            }

            return (_clientGateActive && !IsReleaseDue(_clientReleaseReceived, _clientReleaseUtcTicks))
                || (_clientIntroGateActive && !IsReleaseDue(_clientIntroReleaseReceived, _clientIntroReleaseUtcTicks))
                || (_clientBattleGateActive && !IsReleaseDue(_clientBattleReleaseReceived, _clientBattleReleaseUtcTicks));
        }

        static void PrepareDeterministicBattleStart(Levels level, string role)
        {
            if (!Plugin.VanillaTwoPlayerOnline || !MultiplayerSession.IsActive)
                return;

            uint levelSalt = unchecked((uint)ToToken(level) * 0x9E3779B9u);
            uint battleSeed = RngSync.CurrentSeed ^ 0xB1771E5Du ^ levelSalt;
            RngSync.SetSeed(battleSeed);
            PlayerSelectionMath.ResetSelectionCursor();
            Plugin.Log.LogInfo(
                "[LevelStartSync] Prepared deterministic battle start on "
                + role
                + " seed="
                + battleSeed.ToString("X8")
                + ".");
        }

        static void ClearAll()
        {
            if (_holdingTimeScale && PauseManager.state != PauseManager.State.Paused)
            {
                Time.timeScale = 1f;
                CupheadTime.SetAll(1f);
            }
            _holdingTimeScale = false;
            _hostGateActive = false;
            _clientGateActive = false;
            _hostIntroGateActive = false;
            _hostIntroGateComplete = false;
            _hostGuestIntroReady = false;
            _hostIntroReleaseSent = false;
            _hostBattleGateActive = false;
            _hostBattleGateComplete = false;
            _hostGuestBattleReady = false;
            _hostBattleReleaseSent = false;
            _hostPendingBattleIntro = null;
            _clientIntroGateActive = false;
            _clientIntroGateComplete = false;
            _clientIntroReadySent = false;
            _clientIntroReleaseReceived = false;
            _clientIntroReleaseUtcTicks = 0L;
            _clientBattleGateActive = false;
            _clientBattleGateComplete = false;
            _clientBattleReadySent = false;
            _clientBattleReleaseReceived = false;
            _clientBattleReleaseUtcTicks = 0L;
            _clientPendingBattleIntro = null;
            _clientReleaseUtcTicks = 0L;
            _hostStartHeldLogged = false;
            _clientStartHeldLogged = false;
            _lastHostWaitingRecoveryAt = -1f;
            HighLatencyInputSync.ResetLevelClock();
        }

        static void MaybeBroadcastWaitingRecovery(string reason)
        {
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Time.unscaledTime - _lastHostWaitingRecoveryAt < 2.0f)
                return;

            _lastHostWaitingRecoveryAt = Time.unscaledTime;
            SessionSync.BroadcastRecoveryBundle(reason);
        }

        static void SendSignal(SessionSignalKind kind, ushort levelToken)
        {
            SendSignal(kind, levelToken, 0L);
        }

        static void SendSignal(SessionSignalKind kind, ushort levelToken, long releaseUtcTicks)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = new SessionSignalPacket
            {
                Signal = (byte)kind,
                SaveRevision = levelToken,
                UtcReleaseTicks = releaseUtcTicks,
            };
            Plugin.Net.SendSessionSignal(ref pkt);
        }

        static long UtcTicksAfterSeconds(float seconds)
        {
            if (seconds <= 0f)
                return 0L;

            try
            {
                return DateTime.UtcNow.AddSeconds(seconds).Ticks;
            }
            catch
            {
                return 0L;
            }
        }

        static bool IsReleaseDue(bool received, long releaseUtcTicks)
        {
            if (!received)
                return false;
            if (releaseUtcTicks <= 0L)
                return true;

            long nowTicks = DateTime.UtcNow.Ticks;
            long deltaTicks = releaseUtcTicks - nowTicks;
            double deltaSeconds = deltaTicks / (double)TimeSpan.TicksPerSecond;
            if (deltaSeconds > 1.75 || deltaSeconds < -0.50)
                return true;

            return deltaTicks <= 0L;
        }

        static string ReleaseTargetSuffix(long releaseUtcTicks)
        {
            if (releaseUtcTicks <= 0L)
                return ".";

            double remainingMs = (releaseUtcTicks - DateTime.UtcNow.Ticks) / 10000.0;
            return " with synchronized release target in " + Mathf.Max(0f, (float)(remainingMs / 1000.0)).ToString("0.000") + "s.";
        }

        public static float EstimateOneWayReleaseDelaySeconds()
        {
            float delay = 0f;

            if (Plugin.Net != null && Plugin.Net.Latency > 0)
                delay = Mathf.Max(delay, Plugin.Net.Latency * 0.0005f);

            if (Plugin.LanArtificialLatencyMs > 0)
                delay = Mathf.Max(delay, Plugin.LanArtificialLatencyMs / 1000f);

            if (delay >= 0.50f && Plugin.VanillaTwoPlayerOnline)
                delay += HighLatencyVisualAlignmentSeconds;

            return Mathf.Clamp(delay, 0f, 2.5f);
        }

        static ushort ToToken(Levels level)
        {
            return unchecked((ushort)((int)level & 0xffff));
        }

        static bool IsInLevelScene()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            return !string.IsNullOrEmpty(sceneName)
                && sceneName.StartsWith("scene_level_", StringComparison.Ordinal);
        }

        static bool IsCurrentLevelStarted(Levels expectedLevel)
        {
            if (!IsInLevelScene())
                return false;
            if (Level.Current == null || !Level.Current.Started)
                return false;

            try
            {
                return ToToken(Level.Current.CurrentLevel) == ToToken(expectedLevel);
            }
            catch
            {
                return true;
            }
        }

        static void LogWaiting(string message)
        {
            if (Time.unscaledTime - _lastLogAt <= 2f)
                return;

            _lastLogAt = Time.unscaledTime;
            Plugin.Log.LogInfo("[LevelStartSync] " + message);
        }
    }
}
