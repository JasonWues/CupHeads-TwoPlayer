using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch]
    public static class ProjectileTrackingPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var level = AccessTools.Method(typeof(AbstractLevelWeapon), "fireProjectile",
                new[] { typeof(AbstractLevelWeapon.Mode), typeof(bool) });
            if (level != null)
                yield return level;

            var plane = AccessTools.Method(typeof(AbstractPlaneWeapon), "fireProjectile",
                new[] { typeof(AbstractPlaneWeapon.Mode) });
            if (plane != null)
                yield return plane;

            var arcade = AccessTools.Method(typeof(AbstractArcadeWeapon), "fireProjectile",
                new[] { typeof(AbstractArcadeWeapon.Mode) });
            if (arcade != null)
                yield return arcade;
        }

        static void Postfix(AbstractProjectile __result)
        {
            LanSteamE2ETest.NotifyProjectileSpawned(__result);
        }
    }
}
