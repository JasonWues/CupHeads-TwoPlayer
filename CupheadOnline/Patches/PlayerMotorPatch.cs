using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Patches LevelPlayerMotor.FixedUpdate.
    ///
    /// Host: both built-in gameplay slots run the real Cuphead motor so the host
    /// remains authoritative for movement, collisions, jump state, death, and
    /// animation transitions.
    ///
    /// Client: the host-controlled slot stays a proxy that consumes snapshots.
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
    public static class PlayerMotorPatch
    {
        static bool Prefix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            var player = __instance.player;
            if (player == null)
                return true;

            byte extraParticipantId;
            if (ExtraRemoteAvatarManager.TryGetAvatarParticipantId(__instance, out extraParticipantId))
            {
                RemoteInputDriver.Tick(extraParticipantId);
                ApplyRemoteState(__instance, extraParticipantId);
                return false;
            }

            if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
            {
                RemoteInputDriver.Tick(player.id);

                if (player.id <= PlayerId.PlayerTwo
                 && HighLatencyInputSync.ShouldSimulateBuiltInRemotePlayers())
                {
                    return true;
                }

                // The host simulates the guest with the real gameplay motor.
                if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                    return true;

                ApplyRemoteState(__instance, (byte)player.id);
                return false;
            }

            if (HighLatencyInputSync.ShouldUseHostAuthorityForLocalBuiltInPlayer(player.id))
            {
                RemotePlayer.TryApplyLocalAuthoritySnapshot(player.id);
                return false;
            }

            MultiplayerSession.IncrementTick();
            return true;
        }

        static void Postfix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = __instance.player;
            if (player == null)
                return;

            bool authoritativeBuiltIn = MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo;
            bool localPlayer = MultiplayerSession.IsLocalPlayer(player.id);
            if (!authoritativeBuiltIn && !localPlayer)
                return;

            var pkt = BuildStatePacket(player, __instance);

            if (authoritativeBuiltIn)
                Plugin.Net.SendPlayerState(ref pkt);
            else if (localPlayer)
            {
                SendInputFrameAndState(__instance, player, ref pkt);
                if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(player.id))
                {
                    ApplyAuthoritativeCorrection(__instance, player.id);
                }
            }
        }

        internal static byte BuildFlags(AbstractPlayerController player, LevelPlayerMotor m)
        {
            byte f = 0;
            if (m.Grounded) f |= 1;
            if (m.Dashing) f |= 2;
            if (m.Ducking) f |= 4;
            if (m.GravityReversed) f |= 8;
            if (m.IsHit) f |= 16;
            if (m.IsUsingSuperOrEx) f |= 32;
            if (player != null && player.IsDead) f |= 64;
            return f;
        }

        internal static PlayerStatePacket BuildStatePacket(LevelPlayerController player, LevelPlayerMotor motor)
        {
            int animHash;
            float animTime;
            GetAnimState(player, out animHash, out animTime);

            return new PlayerStatePacket
            {
                PlayerId = (byte)player.id,
                PosX = motor.transform.position.x,
                PosY = motor.transform.position.y,
                LookX = (sbyte)motor.LookDirection.x.Value,
                LookY = (sbyte)motor.LookDirection.y.Value,
                Flags = BuildFlags(player, motor),
                AnimState = (byte)(animHash & 0xFF),
                Tick = MultiplayerSession.Tick,
                AnimHash = animHash,
                AnimNormalizedTime = animTime,
            };
        }

        internal static byte GetAnimHash(LevelPlayerController player)
        {
            int animHash;
            float animTime;
            GetAnimState(player, out animHash, out animTime);
            return (byte)(animHash & 0xFF);
        }

        static void GetAnimState(LevelPlayerController player, out int animHash, out float normalizedTime)
        {
            animHash = 0;
            normalizedTime = 0f;

            var anim = player.animationController?.animator;
            if (anim == null)
                return;

            var state = anim.GetCurrentAnimatorStateInfo(0);
            animHash = state.fullPathHash;
            normalizedTime = Mathf.Repeat(state.normalizedTime, 1f);
        }

        static void SendInputFrameAndState(LevelPlayerMotor motor, LevelPlayerController player, ref PlayerStatePacket statePkt)
        {
            Plugin.Net.SendPlayerState(ref statePkt);
        }

        static void ApplyRemoteState(LevelPlayerMotor motor, byte participantId)
        {
            var snapshot = RemotePlayer.GetNextSnapshot(participantId, mapState: false);
            if (participantId <= (byte)PlayerId.PlayerTwo
             && ParticipantReviveController.TrySuppressRemoteBuiltInDeadBody(motor.player))
            {
                return;
            }

            if (!snapshot.HasValue)
                return;

            var s = snapshot.Value;
            var target = new Vector3(s.PosX, s.PosY, motor.transform.position.z);
            if (Plugin.VanillaTwoPlayerOnline && participantId <= (byte)PlayerId.PlayerTwo)
            {
                motor.transform.position = target;
            }
            else
            {
                motor.transform.position = Vector3.Lerp(
                    motor.transform.position,
                    target,
                    Mathf.Min(1f, 20f * Time.fixedDeltaTime));
            }

            var t = Traverse.Create(motor);
            t.Property("LookDirection").SetValue(new Trilean2(s.LookX, s.LookY));
            t.Property("TrueLookDirection").SetValue(new Trilean2(s.LookX, s.LookY));

            InputFramePacket input;
            if (RemoteInputDriver.TryGetCurrent(participantId, out input))
            {
                t.Property("MoveDirection").SetValue(new Trilean2(
                    input.AxisX > 0.38f ? 1 : input.AxisX < -0.38f ? -1 : 0,
                    input.AxisY > 0.38f ? 1 : input.AxisY < -0.38f ? -1 : 0));
                ApplyRemoteShootingFlag(motor.player, input.IsPressed(CupheadButton.Shoot));
            }
            else
            {
                t.Property("MoveDirection").SetValue(new Trilean2(0, 0));
                ApplyRemoteShootingFlag(motor.player, false);
            }

            t.Property("Grounded").SetValue(s.Grounded);
            t.Property("Dashing").SetValue(s.Dashing);
            t.Property("Ducking").SetValue(s.Ducking);
            t.Property("Locked").SetValue(false);
            t.Property("GravityReversed").SetValue(s.GravReversed);
            t.Property("IsHit").SetValue(s.IsHit);
            t.Property("IsUsingSuperOrEx").SetValue(s.IsSuper);

            RemotePlayer.UpdateStateTransitions(participantId, motor, s);
            ApplyRemoteAnimation(motor.player, s);
        }

        static void ApplyAuthoritativeCorrection(LevelPlayerMotor motor, PlayerId playerId)
        {
            PlayerStatePacket snapshot;
            if (!RemotePlayer.TryGetLocalAuthoritySnapshot(playerId, out snapshot))
                return;
            if (snapshot.IsMapState)
                return;

            var target = new Vector3(snapshot.PosX, snapshot.PosY, motor.transform.position.z);
            float distance = Vector2.Distance(motor.transform.position, target);
            bool tightBuiltInSync = Plugin.VanillaTwoPlayerOnline && playerId <= PlayerId.PlayerTwo;
            float deadZone = tightBuiltInSync ? 0.08f : Plugin.LatencyFriendlyDamage ? 0.85f : 0.35f;
            float snapDistance = tightBuiltInSync ? 1.25f : Plugin.LatencyFriendlyDamage ? 8f : 4f;
            float blend = tightBuiltInSync ? 0.65f : Plugin.LatencyFriendlyDamage ? 0.08f : 0.18f;

            if (distance < deadZone)
                return;

            motor.transform.position = distance > snapDistance
                ? target
                : Vector3.Lerp(motor.transform.position, target, blend);
        }

        static void ApplyRemoteShootingFlag(LevelPlayerController player, bool shooting)
        {
            var anim = player?.animationController?.animator;
            if (anim == null)
                return;

            anim.SetBool("Shooting", shooting);
        }

        internal static void ApplyRemoteAnimation(LevelPlayerController player, PlayerStatePacket snapshot)
        {
            if (snapshot.PlayerId > (byte)PlayerId.PlayerTwo)
                return;

            var anim = player?.animationController?.animator;
            if (anim == null || snapshot.AnimHash == 0)
                return;

            var local = anim.GetCurrentAnimatorStateInfo(0);
            float localTime = Mathf.Repeat(local.normalizedTime, 1f);
            float remoteTime = Mathf.Repeat(snapshot.AnimNormalizedTime, 1f);
            float wrappedDelta = Mathf.Abs(Mathf.Repeat(localTime - remoteTime + 0.5f, 1f) - 0.5f);

            if (local.fullPathHash != snapshot.AnimHash || wrappedDelta > 0.22f)
                anim.Play(snapshot.AnimHash, 0, remoteTime);
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetAxis))]
    public static class PlayerInputAxisPatch
    {
        static bool Prefix(PlayerInput __instance, PlayerInput.Axis axis, ref float __result)
        {
            float scripted;
            if (LocalDevE2ETest.TryGetLocalAxis(__instance.playerId, axis == PlayerInput.Axis.X ? 0 : 1, out scripted))
            {
                __result = scripted;
                return false;
            }

            float delayed;
            if (HighLatencyInputSync.TryGetDelayedAxis(__instance.playerId, axis == PlayerInput.Axis.X ? 0 : 1, out delayed))
            {
                __result = delayed;
                return false;
            }

            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = 0f;
                return false;
            }

            __result = axis == PlayerInput.Axis.X ? input.AxisX : input.AxisY;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetAxisInt))]
    public static class PlayerInputAxisIntPatch
    {
        static bool Prefix(PlayerInput __instance, PlayerInput.Axis axis, ref int __result)
        {
            float scripted;
            if (LocalDevE2ETest.TryGetLocalAxis(__instance.playerId, axis == PlayerInput.Axis.X ? 0 : 1, out scripted))
            {
                __result = scripted > 0.38f ? 1 : scripted < -0.38f ? -1 : 0;
                return false;
            }

            float delayed;
            if (HighLatencyInputSync.TryGetDelayedAxis(__instance.playerId, axis == PlayerInput.Axis.X ? 0 : 1, out delayed))
            {
                __result = delayed > 0.38f ? 1 : delayed < -0.38f ? -1 : 0;
                return false;
            }

            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = 0;
                return false;
            }

            float v = axis == PlayerInput.Axis.X ? input.AxisX : input.AxisY;
            __result = v > 0.38f ? 1 : v < -0.38f ? -1 : 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.GetButton))]
    public static class PlayerInputButtonPatch
    {
        static bool Prefix(PlayerInput __instance, CupheadButton button, ref bool __result)
        {
            bool scripted;
            if (LocalDevE2ETest.TryGetLocalButton(__instance.playerId, (int)button, false, false, out scripted))
            {
                __result = scripted;
                return false;
            }

            bool delayed;
            if (HighLatencyInputSync.TryGetDelayedButton(__instance.playerId, (int)button, false, false, out delayed))
            {
                __result = delayed;
                return false;
            }

            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(__instance.playerId, out input))
            {
                __result = false;
                return false;
            }

            __result = input.IsPressed(button);
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "GetButtonDown")]
    public static class PlayerInputButtonDownPatch
    {
        static bool Prefix(PlayerInput __instance, CupheadButton button, ref bool __result)
        {
            bool scripted;
            if (LocalDevE2ETest.TryGetLocalButton(__instance.playerId, (int)button, true, false, out scripted))
            {
                __result = scripted;
                return false;
            }

            bool delayed;
            if (HighLatencyInputSync.TryGetDelayedButton(__instance.playerId, (int)button, true, false, out delayed))
            {
                __result = delayed;
                return false;
            }

            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            __result = RemoteInputDriver.WasPressedThisFrame((byte)__instance.playerId, button);
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerInput), "GetButtonUp")]
    public static class PlayerInputButtonUpPatch
    {
        static bool Prefix(PlayerInput __instance, CupheadButton button, ref bool __result)
        {
            bool scripted;
            if (LocalDevE2ETest.TryGetLocalButton(__instance.playerId, (int)button, false, true, out scripted))
            {
                __result = scripted;
                return false;
            }

            bool delayed;
            if (HighLatencyInputSync.TryGetDelayedButton(__instance.playerId, (int)button, false, true, out delayed))
            {
                __result = delayed;
                return false;
            }

            if (!MultiplayerSession.IsActive)
                return true;
            if (!MultiplayerSession.IsNetworkControlledPlayer(__instance.playerId))
                return true;

            __result = RemoteInputDriver.WasReleasedThisFrame((byte)__instance.playerId, button);
            return false;
        }
    }
}
