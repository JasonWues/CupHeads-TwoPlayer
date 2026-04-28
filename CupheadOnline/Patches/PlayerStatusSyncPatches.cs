using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(PlayerStatsManager), "LevelInit")]
    public static class PlayerStatsInitialStatusPatch
    {
        static void Postfix(PlayerStatsManager __instance)
        {
            PlayerStatusPatchHelpers.RestoreSpawnHealthIfNeeded(__instance);
            PlayerStatusPatchHelpers.PushLocalStatus(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerStatsManager), "OnHealthChanged")]
    public static class PlayerStatsHealthChangedPatch
    {
        static void Postfix(PlayerStatsManager __instance)
        {
            PlayerStatusPatchHelpers.PushLocalStatus(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "ReviveOutOfFrame")]
    public static class PlayerDeathEffectReviveOutOfFramePatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            return !ParticipantReviveController.TryOverrideReviveOutOfFrame(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "Start")]
    public static class PlayerDeathEffectExtraVisualStartPatch
    {
        static void Postfix(PlayerDeathEffect __instance)
        {
            ExtraParticipantReviveVisuals.OnEffectStarted(__instance);
            PlayerColorSync.ApplyDeathEffectTint(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "OnParrySwitch")]
    public static class PlayerDeathEffectExtraVisualParryPatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            if (ExtraParticipantReviveVisuals.HandleParrySwitch(__instance))
                return false;
            return !ParticipantReviveController.TryPlayClientRemoteBuiltInParryVisualOnly(__instance);
        }

        static void Postfix(PlayerDeathEffect __instance)
        {
            ParticipantReviveController.NotifyBuiltInParrySwitch(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "OnReviveParryAnimComplete")]
    public static class PlayerDeathEffectExtraVisualParryAnimPatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            if (ExtraParticipantReviveVisuals.HandleParryAnimComplete(__instance))
                return false;
            return !ParticipantReviveController.TrySuppressClientRemoteBuiltInParryAnimComplete(__instance);
        }
    }

    static class PlayerStatusPatchHelpers
    {
        internal static void RestoreSpawnHealthIfNeeded(PlayerStatsManager stats)
        {
            if (!MultiplayerSession.IsActive || stats == null)
                return;

            var player = stats.GetComponent<AbstractPlayerController>();
            if (player == null)
                return;

            if (!MultiplayerSession.IsAuthoritativePlayer(player.id))
                return;

            if (stats.Health > 0 || stats.HealthMax <= 0)
                return;

            int restoredHealth = System.Math.Max(1, stats.HealthMax);
            stats.SetHealth(restoredHealth);
            Plugin.Log.LogWarning(
                "[SpawnHealth] Restored " + player.id + " from zero HP on level init (" + CurrentLevelName() + ").");
        }

        internal static void PushLocalStatus(PlayerStatsManager stats)
        {
            if (!MultiplayerSession.IsActive || stats == null)
                return;

            var player = stats.GetComponent<AbstractPlayerController>();
            if (player == null)
                return;
            if (!MultiplayerSession.IsAuthoritativePlayer(player.id))
                return;

            ParticipantStatusTracker.PushLocalStatus(player);
        }

        static string CurrentLevelName()
        {
            try
            {
                return Level.Current == null ? "unknown" : Level.Current.CurrentLevel.ToString();
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
