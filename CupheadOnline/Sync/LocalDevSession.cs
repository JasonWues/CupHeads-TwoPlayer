using CupheadOnline.Net;
using CupheadOnline.Patches;
using CupheadOnline.UI;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Offline same-PC simulation for testing the CupHeads remote-input path.
    /// It starts a host-style session with Player One local and Player Two marked
    /// as network-controlled, then feeds Player Two from the local Player Two /
    /// controller bindings through RemoteInputDriver.
    /// </summary>
    public static class LocalDevSession
    {
        static uint _inputTick = 1;

        public static bool IsActive { get; private set; }

        static LocalDevSession()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Toggle()
        {
            if (IsActive)
            {
                Stop("Local dev session stopped.");
                return;
            }

            Start();
        }

        public static void Start()
        {
            if (!Plugin.EnableLocalDevSession)
            {
                ConnectionHUD.Show("Local dev session hotkey is disabled in config.");
                return;
            }

            if (Plugin.Net != null && Plugin.Net.IsConnected)
            {
                ConnectionHUD.Show("Leave the Steam session before starting local dev mode.");
                return;
            }

            IsActive = true;
            _inputTick = 1;
            MultiplayerSession.StartAsHost();
            MultiplayerSession.EnsureCupheadMultiplayerState();
            RemoteInputDriver.Reset(PlayerId.PlayerTwo);
            RemotePlayer.Reset(PlayerId.PlayerTwo);
            ParticipantStatusTracker.CaptureLocal(PlayerId.PlayerOne);
            ParticipantStatusTracker.CaptureLocal(PlayerId.PlayerTwo);
            ConnectionHUD.Show("Local dev session active. P1 uses Player One controls; P2 uses Player Two/controller controls.");
            Plugin.Log.LogInfo("[LocalDev] Started local same-PC session.");
        }

        public static void Stop(string message = null)
        {
            if (!IsActive)
                return;

            IsActive = false;
            RemoteInputDriver.Reset(PlayerId.PlayerTwo);
            RemotePlayer.Reset(PlayerId.PlayerTwo);
            MultiplayerSession.End();

            if (!string.IsNullOrEmpty(message))
                ConnectionHUD.Show(message);
            Plugin.Log.LogInfo("[LocalDev] Stopped local same-PC session.");
        }

        public static void Update()
        {
            if (!IsActive)
                return;

            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost)
            {
                Stop();
                return;
            }

            MultiplayerSession.EnsureCupheadMultiplayerState();

            var input = UniversalInputRouter.BuildLocalInputFrameForPlayer(PlayerId.PlayerTwo);
            input.Tick = NextInputTick();
            RemoteInputDriver.Apply(PlayerId.PlayerTwo, input);
        }

        static uint NextInputTick()
        {
            unchecked
            {
                uint next = _inputTick++;
                if (next == 0)
                {
                    _inputTick = 1;
                    next = _inputTick++;
                }
                return next;
            }
        }

        static void Reset()
        {
            IsActive = false;
            _inputTick = 1;
        }
    }
}
