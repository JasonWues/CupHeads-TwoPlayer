using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hooks into LevelPlayerWeaponManager to broadcast weapon events.
    /// We subscribe to the existing C# events rather than patching private methods,
    /// which is more stable and avoids method-name fragility.
    ///
    /// Subscription happens in the PlayerManagerPatch (LevelInit postfix) so we
    /// have a valid reference to the weaponManager at the right lifecycle point.
    /// </summary>
    public static class WeaponManagerPatch
    {
        // Called by PlayerManagerPatch after player construction
        public static void Subscribe(LevelPlayerController player)
        {
            if (player == null) return;
            var wm = player.weaponManager;
            if (wm == null) return;

            // Only subscribe for our LOCAL player — we broadcast their actions
            bool hostAuthoritativeBuiltIn = Plugin.VanillaTwoPlayerOnline
                && MultiplayerSession.IsHost
                && player.id <= PlayerId.PlayerTwo;
            if (!MultiplayerSession.IsLocalPlayer(player.id) && !hostAuthoritativeBuiltIn) return;

            wm.OnWeaponFire      += () => BroadcastShot(player, 0);
            wm.OnExStart         += () => BroadcastShot(player, 1);
            wm.OnSuperStart      += () => BroadcastShot(player, 2);
            wm.OnWeaponChangeEvent += (next) => BroadcastWeaponSwitch(player, next);
        }

        static void BroadcastShot(LevelPlayerController player, byte eventType)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            if (Plugin.VanillaTwoPlayerOnline
             && MultiplayerSession.IsClient
             && MultiplayerSession.IsLocalPlayer(player.id)) return;

            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = eventType,
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = (sbyte)(motor?.LookDirection.y.Value ?? 0),
                WeaponId  = 0,
                Tick      = MultiplayerSession.Tick,
                PosX      = motor == null ? 0f : motor.transform.position.x,
                PosY      = motor == null ? 0f : motor.transform.position.y,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }

        static void BroadcastWeaponSwitch(LevelPlayerController player, Weapon next)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            if (Plugin.VanillaTwoPlayerOnline
             && MultiplayerSession.IsClient
             && MultiplayerSession.IsLocalPlayer(player.id)) return;

            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = 4, // switch
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = 0,
                WeaponId  = (byte)next,
                Tick      = MultiplayerSession.Tick,
                PosX      = motor == null ? 0f : motor.transform.position.x,
                PosY      = motor == null ? 0f : motor.transform.position.y,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }
    }

    /// <summary>
    /// Patch the parry controller to broadcast parry events.
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerParryController), "StartParry")]
    public static class ParryPatch
    {
        static void Postfix(LevelPlayerParryController __instance)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            var player = __instance.player;
            if (player == null || !MultiplayerSession.IsLocalPlayer(player.id)) return;
            if (Plugin.VanillaTwoPlayerOnline && MultiplayerSession.IsClient) return;

            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = 3, // parry
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = (sbyte)(motor?.LookDirection.y.Value ?? 0),
                WeaponId  = 0,
                Tick      = MultiplayerSession.Tick,
                PosX      = motor == null ? 0f : motor.transform.position.x,
                PosY      = motor == null ? 0f : motor.transform.position.y,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }
    }
}
