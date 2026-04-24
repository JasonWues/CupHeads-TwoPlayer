using HarmonyLib;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(LevelPlayerController), "OnDeath")]
    public static class LevelPlayerDeathStatsPatch
    {
        static void Prefix(LevelPlayerController __instance, PlayerId playerId)
        {
            BattleAssistHud.RecordLocalDeath(__instance);
            if (!MultiplayerSession.IsActive) return;
            if (__instance == null || __instance.id != MultiplayerSession.LocalId) return;
            SessionSync.RecordLocalDeath();
        }
    }

    [HarmonyPatch(typeof(SceneLoader), "ReloadLevel")]
    public static class SceneLoaderRetryStatsPatch
    {
        static void Prefix()
        {
            BattleAssistHud.RecordRetry();
            if (!MultiplayerSession.IsActive) return;
            SessionSync.RecordLocalRetry();
        }
    }

    [HarmonyPatch(typeof(LevelPlayerWeaponManager), "ParrySuccess")]
    public static class LevelPlayerParryStatsPatch
    {
        static void Postfix(LevelPlayerWeaponManager __instance)
        {
            if (__instance == null || __instance.player == null) return;
            BattleAssistHud.RecordLocalParry(__instance.player);
            if (!MultiplayerSession.IsActive) return;
            if (__instance.player.id != MultiplayerSession.LocalId) return;
            SessionSync.RecordLocalParry();
        }
    }
}
