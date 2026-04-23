using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Tracks which incoming damage events the CLIENT is authorised to apply.
    ///
    /// The host sends a DamageEventPacket whenever a player takes damage.
    /// The client queues an "authorised token" keyed by (targetPlayerId, tick).
    /// When PlayerDamageReceiver.TakeDamage fires locally on the client, the prefix
    /// patch checks this store and only allows execution if a matching token exists.
    ///
    /// This prevents the client from double-counting damage from locally-simulated
    /// hits AND from host-sourced projectiles.
    /// </summary>
    public static class DamageAuthority
    {
        // Tag written onto DamageDealer.DamageInfo to mark authorised damage
        // We compare damage amount + source to avoid spoofing.
        private static float  _pendingDamage;
        private static byte   _pendingSource;
        private static PlayerId _pendingTarget = PlayerId.None;
        private static bool   _pendingReady;

        // ──────────────────────────────────────────────────────────────────────
        //  Called by PacketDispatcher on DamageEventPacket received
        // ──────────────────────────────────────────────────────────────────────

        public static void ApplyAuthorized(DamageEventPacket pkt)
        {
            // Find the target player controller and force-apply the damage
            var player = PlayerManager.GetPlayer((PlayerId)pkt.TargetPlayerId);
            if (player == null) return;
            var dr = player.damageReceiver as PlayerDamageReceiver;
            if (dr == null) return;

            // Mark as authorised so the prefix patch lets it through
            _pendingDamage = pkt.Damage;
            _pendingSource = pkt.Source;
            _pendingTarget = (PlayerId)pkt.TargetPlayerId;
            _pendingReady  = true;

            // Build a synthetic DamageInfo and call TakeDamage
            var info = new DamageDealer.DamageInfo(
                pkt.Damage,
                DamageDealer.Direction.Neutral,
                Vector2.zero,
                (DamageDealer.DamageSource)pkt.Source);
            dr.TakeDamage(info);

            _pendingReady = false;
            _pendingTarget = PlayerId.None;
        }

        /// <summary>
        /// Prefix guard called by DamageReceiverPatch.
        /// Returns true (allow) only for damage we triggered ourselves via ApplyAuthorized.
        /// </summary>
        public static bool IsAuthorised(PlayerDamageReceiver receiver, DamageDealer.DamageInfo info)
        {
            if (!_pendingReady) return false;

            var player = receiver != null ? receiver.GetComponent<AbstractPlayerController>() : null;
            if (player == null || player.id != _pendingTarget)
                return false;

            return System.Math.Abs(info.damage - _pendingDamage) < 0.001f
                && info.damageSource == (DamageDealer.DamageSource)_pendingSource;
        }

        public static void Reset()
        {
            _pendingDamage = 0f;
            _pendingSource = 0;
            _pendingTarget = PlayerId.None;
            _pendingReady = false;
        }
    }
}
