using UnityEngine;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    public static class QoLHotkeys
    {
        private static float _lastHotkeyAt = -1f;

        public static void Tick()
        {
            if (!Plugin.EnableQoLHotkeys)
                return;

            if (Time.unscaledTime - _lastHotkeyAt < 0.12f)
                return;

            if (Input.GetKeyDown(KeyCode.F6))
            {
                _lastHotkeyAt = Time.unscaledTime;
                QuickResync();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                _lastHotkeyAt = Time.unscaledTime;
                bool enabled = Plugin.ToggleBossHealthBars();
                ConnectionHUD.Show(enabled ? Loc.T("Boss health bars enabled.") : Loc.T("Boss health bars disabled."));
                return;
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                _lastHotkeyAt = Time.unscaledTime;
                CopyDiagnostics();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                _lastHotkeyAt = Time.unscaledTime;
                bool enabled = Plugin.ToggleBattleAssistHud();
                ConnectionHUD.Show(enabled ? Loc.T("Battle assist HUD enabled.") : Loc.T("Battle assist HUD disabled."));
                return;
            }

            if (Input.GetKeyDown(KeyCode.F11))
            {
                _lastHotkeyAt = Time.unscaledTime;
                LocalDevMenu.Toggle();
            }
        }

        private static void QuickResync()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                ConnectionHUD.Show(Loc.T("No active multiplayer session to resync."));
                return;
            }

            string result = SessionSync.RequestRecovery();
            ConnectionHUD.Show(result);
        }

        private static void CopyDiagnostics()
        {
            string report = Plugin.BuildDiagnosticsReport();
            GUIUtility.systemCopyBuffer = report;
            Plugin.Log.LogInfo("[QoL] Diagnostics copied to clipboard.");
            ConnectionHUD.Show(Loc.T("Diagnostics copied to clipboard."));
        }
    }
}
