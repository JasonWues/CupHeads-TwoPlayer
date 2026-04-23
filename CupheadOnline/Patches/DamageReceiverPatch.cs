using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Implements host-authoritative damage:
    ///
    ///   HOST   — processes damage normally, then broadcasts DamageEventPacket to client.
    ///   CLIENT — suppresses all incoming damage; only applies it after receiving the
    ///            corresponding DamageEventPacket from the host (DamageAuthority).
    ///
    /// This prevents the client from ever getting ahead of the host on HP changes,
    /// which would cause visible desync in the HUD.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDamageReceiver), nameof(PlayerDamageReceiver.TakeDamage))]
    public static class PlayerDamagePatch
    {
        // ── Prefix: gate damage on client ────────────────────────────────────
        static bool Prefix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info)
        {
            if (!MultiplayerSession.IsActive) return true; // singleplayer: unchanged

            if (MultiplayerSession.IsClient)
            {
                // Accept only damage that arrived via an authorised DamageEventPacket
                return DamageAuthority.IsAuthorised(__instance, info);
            }
            return true; // host: process normally
        }

        // ── Postfix: host broadcasts damage to client ─────────────────────────
        static void Postfix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info)
        {
            if (!MultiplayerSession.IsHost) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            if (info.damage <= 0f && info.stoneTime <= 0f) return;

            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null) return;
            Plugin.Net.SendDamageEventForParticipant(
                (byte)player.id,
                info.damage,
                (byte)info.damageSource,
                MultiplayerSession.Tick);
        }
    }
}
