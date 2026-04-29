using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    public static class ParticipantReviveController
    {
        static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static readonly FieldInfo DeathEffectPlayerIdField =
            typeof(PlayerDeathEffect).GetField("playerId", AnyInstance);
        static readonly FieldInfo DeathEffectExitingField =
            typeof(PlayerDeathEffect).GetField("exiting", AnyInstance);
        static readonly FieldInfo DeathEffectParrySwitchField =
            typeof(PlayerDeathEffect).GetField("parrySwitch", AnyInstance);
        static readonly MethodInfo ParrySwitchOnParryPrePauseMethod =
            typeof(ParrySwitch).GetMethod("OnParryPrePause", AnyInstance);
        static readonly MethodInfo ParrySwitchOnParryPostPauseMethod =
            typeof(ParrySwitch).GetMethod("OnParryPostPause", AnyInstance);
        static readonly MethodInfo PlayerDeathEffectOnParrySwitchMethod =
            AccessTools.Method(typeof(PlayerDeathEffect), "OnParrySwitch");
        static readonly MethodInfo PlayerStatsOnStatsDeathMethod =
            AccessTools.Method(typeof(PlayerStatsManager), "OnStatsDeath");
        static readonly MethodInfo LevelPlayerOnDeathMethod =
            AccessTools.Method(typeof(LevelPlayerController), "OnDeath");
        static readonly MethodInfo PlayerOnPreReviveMethod =
            AccessTools.Method(typeof(AbstractPlayerController), "OnPreRevive");
        static readonly MethodInfo PlayerOnReviveMethod =
            AccessTools.Method(typeof(AbstractPlayerController), "OnRevive");

        const float DeathHeartParryPauseSeconds = 0.185f;
        const float DeathHeartParryCatchUpCapSeconds = 0.3f;
        const float DeathHeartParryOffsetToleranceSeconds = 0.03f;

        static readonly List<PlayerDeathEffect> ScratchDeathEffects =
            new List<PlayerDeathEffect>(4);
        static readonly HashSet<long> AppliedGrantKeys =
            new HashSet<long>();
        static readonly HashSet<int> BroadcastedBuiltInVisuals =
            new HashSet<int>();
        static readonly HashSet<int> HostAuthorizedBuiltInParryEffects =
            new HashSet<int>();
        static readonly Dictionary<int, float> ClientRemoteBuiltInParryStartedAt =
            new Dictionary<int, float>();
        static readonly Dictionary<PlayerId, float> MirroredBuiltInParryVisualAt =
            new Dictionary<PlayerId, float>();
        static readonly Dictionary<PlayerId, uint> PendingHostBuiltInReviveTicks =
            new Dictionary<PlayerId, uint>();
        static readonly Dictionary<PlayerId, float> RecentBuiltInRevives =
            new Dictionary<PlayerId, float>();
        static readonly Dictionary<PlayerId, Dictionary<Renderer, bool>> SuppressedBuiltInBodyRenderers =
            new Dictionary<PlayerId, Dictionary<Renderer, bool>>();
        static float _revivePauseCatchUpUntil = -1f;
        static bool _revivePauseCatchUpActive;

        static ParticipantReviveController()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Reset()
        {
            RestoreAllSuppressedBuiltInBodies();
            AppliedGrantKeys.Clear();
            BroadcastedBuiltInVisuals.Clear();
            HostAuthorizedBuiltInParryEffects.Clear();
            ClientRemoteBuiltInParryStartedAt.Clear();
            MirroredBuiltInParryVisualAt.Clear();
            PendingHostBuiltInReviveTicks.Clear();
            RecentBuiltInRevives.Clear();
            _revivePauseCatchUpUntil = -1f;
            _revivePauseCatchUpActive = false;
        }

        public static bool TryOverrideReviveOutOfFrame(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive || effect == null || Plugin.Net == null || !Plugin.Net.IsConnected)
                return false;
            if (Level.IsTowerOfPowerMain)
                return true;

            PlayerId localPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out localPlayerId))
                return false;

            if (localPlayerId != MultiplayerSession.LocalId)
                return false;

            var request = new ReviveRequestPacket
            {
                PosX = effect.transform.position.x,
                PosY = effect.transform.position.y,
                Tick = MultiplayerSession.Tick,
            };

            if (MultiplayerSession.IsHost)
            {
                ResolveHostReviveRequest((byte)localPlayerId, effect.transform.position, request.Tick, Plugin.Net);
                return true;
            }

            Plugin.Net.SendReviveRequest(ref request);
            return true;
        }

        public static void ResolveHostReviveRequest(
            byte requesterParticipantId,
            Vector2 requestPosition,
            uint tick,
            SteamNetManager net)
        {
            if (!MultiplayerSession.IsHost || net == null)
                return;

            ParticipantStatusTracker.ParticipantStatus requesterStatus;
            if (!ParticipantStatusTracker.TryGet(requesterParticipantId, out requesterStatus)
             || !requesterStatus.IsKnown)
            {
                return;
            }

            if (!requesterStatus.IsDead)
            {
                byte targetParticipantId;
                Vector2 revivePosition;
                if (!ParticipantStatusTracker.TryGetBestReviveTarget(
                        requesterParticipantId,
                        requestPosition,
                        out targetParticipantId,
                        out revivePosition))
                {
                    return;
                }

                if (IsHostLocalParticipant(targetParticipantId))
                    ApplyLocalRevive(MultiplayerSession.LocalId, revivePosition);
                else
                    SendGrantToRemoteOwner(
                        net,
                        targetParticipantId,
                        targetParticipantId,
                        requesterParticipantId,
                        revivePosition,
                        tick,
                        applyDonorCost: false,
                        applyRevive: true);
                return;
            }

            byte donorParticipantId;
            Vector2 donorPosition;
            if (!ParticipantStatusTracker.TryGetBestDonor(
                    requesterParticipantId,
                    requestPosition,
                    out donorParticipantId,
                    out donorPosition))
            {
                return;
            }

            if (IsHostLocalParticipant(donorParticipantId))
                ApplyLocalDonorCost(MultiplayerSession.LocalId);
            else
                SendGrantToRemoteOwner(net, donorParticipantId, requesterParticipantId, donorParticipantId, donorPosition, tick, applyDonorCost: true, applyRevive: false);

            if (IsHostLocalParticipant(requesterParticipantId))
                ApplyLocalRevive(MultiplayerSession.LocalId, donorPosition);
            else
                SendGrantToRemoteOwner(net, requesterParticipantId, requesterParticipantId, donorParticipantId, donorPosition, tick, applyDonorCost: false, applyRevive: true);
        }

        public static void ApplyGrant(ReviveGrantPacket pkt)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (!MarkGrantApplied(pkt))
                return;

            PlayerId localPlayerId = MultiplayerSession.LocalId;
            if (pkt.ApplyDonorCost)
                ApplyLocalDonorCost(localPlayerId);
            if (pkt.ApplyRevive)
                ApplyLocalRevive(localPlayerId, new Vector2(pkt.RevivePosX, pkt.RevivePosY));
        }

        public static void NotifyBuiltInParrySwitch(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsHost
             || Plugin.Net == null
             || !Plugin.Net.IsConnected
             || effect == null)
            {
                return;
            }

            ExtraParticipantDeathBubbleTag extraTag;
            if (ExtraParticipantReviveVisuals.IsExtraBubble(effect, out extraTag))
                return;

            PlayerId targetPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out targetPlayerId))
                return;
            if (targetPlayerId != PlayerId.PlayerOne && targetPlayerId != PlayerId.PlayerTwo)
                return;

            int effectId = effect.GetInstanceID();
            if (!BroadcastedBuiltInVisuals.Add(effectId))
                return;

            var position = effect.transform.position;
            float localElapsed;
            float hostElapsed;
            float offset;
            float hostBattleElapsed = SessionSync.TryGetBattleAssistTiming(out localElapsed, out hostElapsed, out offset)
                ? hostElapsed
                : -1f;

            var pkt = new ReviveVisualPacket
            {
                TargetParticipantId = (byte)targetPlayerId,
                DonorParticipantId = (byte)(targetPlayerId == PlayerId.PlayerOne ? PlayerId.PlayerTwo : PlayerId.PlayerOne),
                Flags = 1,
                PosX = position.x,
                PosY = position.y,
                Tick = MultiplayerSession.Tick,
                HostBattleElapsed = hostBattleElapsed,
            };

            Plugin.Net.SendReviveVisual(ref pkt);
            Plugin.Log.LogInfo(
                "[ReviveSync] Broadcast built-in death-heart parry visual for "
                + targetPlayerId
                + " at ("
                + position.x.ToString("0.00")
                + ","
                + position.y.ToString("0.00")
                + ").");
        }

        public static void NotifyBuiltInParryAnimComplete(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsHost
             || Plugin.Net == null
             || !Plugin.Net.IsConnected
             || effect == null)
            {
                return;
            }

            ExtraParticipantDeathBubbleTag extraTag;
            if (ExtraParticipantReviveVisuals.IsExtraBubble(effect, out extraTag))
                return;

            PlayerId targetPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out targetPlayerId))
                return;
            if (targetPlayerId != PlayerId.PlayerOne && targetPlayerId != PlayerId.PlayerTwo)
                return;

            var target = GetPlayerSafe(targetPlayerId);
            if (target == null || target.stats == null)
                return;
            if (!target.IsDead && target.stats.Health > 0 && target.gameObject != null && target.gameObject.activeInHierarchy)
                return;

            PlayerId donorPlayerId = targetPlayerId == PlayerId.PlayerOne
                ? PlayerId.PlayerTwo
                : PlayerId.PlayerOne;
            Vector2 revivePosition = effect.transform.position;

            ResolveHostReviveRequest(
                (byte)donorPlayerId,
                revivePosition,
                MultiplayerSession.Tick,
                Plugin.Net);

            if (target.IsDead
             || target.stats.Health <= 0
             || target.gameObject == null
             || !target.gameObject.activeInHierarchy)
            {
                ApplyLocalRevive(targetPlayerId, revivePosition);
            }

            Plugin.Log.LogInfo(
                "[ReviveSync] Resolved host built-in revive for "
                + targetPlayerId
                + " after death-heart parry animation completed.");
        }

        public static bool TryPlayClientRemoteBuiltInParryVisualOnly(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsClient
             || effect == null)
            {
                return false;
            }

            PlayerId targetPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out targetPlayerId))
                return false;
            if (targetPlayerId != PlayerId.PlayerOne && targetPlayerId != PlayerId.PlayerTwo)
                return false;
            if (MultiplayerSession.IsAuthoritativePlayer(targetPlayerId))
                return false;
            if (DeathEffectParrySwitchField == null
             || ParrySwitchOnParryPrePauseMethod == null
             || ParrySwitchOnParryPostPauseMethod == null)
            {
                return false;
            }

            var parrySwitch = DeathEffectParrySwitchField.GetValue(effect) as PlayerDeathParrySwitch;
            var donor = GetPlayerSafe(MultiplayerSession.LocalId) as LevelPlayerController;
            if (parrySwitch == null || donor == null)
                return false;

            int effectId = effect.GetInstanceID();
            if (ClientRemoteBuiltInParryStartedAt.ContainsKey(effectId))
                return true;

            ClientRemoteBuiltInParryStartedAt[effectId] = Time.unscaledTime;

            try
            {
                ParrySwitchOnParryPrePauseMethod.Invoke(parrySwitch, new object[] { donor });
                if (Plugin.Instance != null)
                    Plugin.Instance.StartCoroutine(FinishMirroredParrySwitch(parrySwitch, donor));
                else
                    ParrySwitchOnParryPostPauseMethod.Invoke(parrySwitch, new object[] { donor });

                Plugin.Log.LogInfo(
                    "[ReviveSync] Playing client-local parry visual on remote-owned "
                    + targetPlayerId
                    + " death heart; host revive will authorize completion.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    "[ReviveSync] Could not play client-local parry visual for remote-owned "
                    + targetPlayerId
                    + ": "
                    + ex.Message);
                return false;
            }
        }

        public static bool TrySuppressClientRemoteBuiltInParryAnimComplete(PlayerDeathEffect effect)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsClient
             || effect == null)
            {
                return false;
            }

            PlayerId targetPlayerId;
            if (!TryGetDeathEffectPlayerId(effect, out targetPlayerId))
                return false;
            if (targetPlayerId != PlayerId.PlayerOne && targetPlayerId != PlayerId.PlayerTwo)
                return false;
            if (MultiplayerSession.IsAuthoritativePlayer(targetPlayerId))
                return false;

            Plugin.Log.LogInfo(
                "[ReviveSync] Suppressed client-local revive completion for remote-owned "
                + targetPlayerId
                + " death heart; waiting for host status.");
            return true;
        }

        public static void ApplyReviveVisual(ReviveVisualPacket pkt)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsClient)
                return;
            if (!pkt.ParrySwitch || pkt.TargetParticipantId > (byte)PlayerId.PlayerTwo)
                return;

            var targetPlayerId = (PlayerId)pkt.TargetParticipantId;
            var effect = FindLocalDeathEffect(targetPlayerId);
            if (effect == null)
                return;

            effect.transform.position = new Vector3(pkt.PosX, pkt.PosY, effect.transform.position.z);
            bool alreadyExiting = IsDeathEffectExiting(effect);
            bool localVisualAlreadyStarted = ClientRemoteBuiltInParryStartedAt.ContainsKey(effect.GetInstanceID());
            if (PlayerDeathEffectOnParrySwitchMethod == null && !alreadyExiting && !localVisualAlreadyStarted)
                return;

            try
            {
                int effectId = effect.GetInstanceID();
                HostAuthorizedBuiltInParryEffects.Add(effectId);
                float localVisualAt;
                MirroredBuiltInParryVisualAt[targetPlayerId] =
                    ClientRemoteBuiltInParryStartedAt.TryGetValue(effectId, out localVisualAt)
                        ? localVisualAt
                        : Time.unscaledTime;

                if (alreadyExiting || localVisualAlreadyStarted)
                {
                    Plugin.Log.LogInfo(
                        "[ReviveSync] Host authorized existing client death-heart parry visual for "
                        + targetPlayerId
                        + " at tick "
                        + pkt.Tick
                        + ".");
                    return;
                }

                if (!TryBeginMirroredParrySwitch(effect, pkt))
                    PlayerDeathEffectOnParrySwitchMethod.Invoke(effect, null);
                _revivePauseCatchUpUntil = Time.unscaledTime + 2f;
                Plugin.Log.LogInfo(
                    "[ReviveSync] Mirrored built-in death-heart parry visual for "
                    + targetPlayerId
                    + " at tick "
                    + pkt.Tick
                    + ".");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    "[ReviveSync] Could not mirror built-in death-heart parry visual for "
                    + targetPlayerId
                    + ": "
                    + ex.Message);
            }
        }

        public static void TryCatchUpAfterHostTimingSnapshot(float localMinusHostSeconds)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsClient
             || Plugin.Instance == null
             || _revivePauseCatchUpActive
             || _revivePauseCatchUpUntil < 0f
             || Time.unscaledTime > _revivePauseCatchUpUntil
             || localMinusHostSeconds <= DeathHeartParryOffsetToleranceSeconds)
            {
                return;
            }

            float holdSeconds = Mathf.Min(localMinusHostSeconds, DeathHeartParryCatchUpCapSeconds);
            Plugin.Instance.StartCoroutine(HoldGuestForHostPauseCorrection(holdSeconds));
        }

        static bool TryBeginMirroredParrySwitch(PlayerDeathEffect effect, ReviveVisualPacket pkt)
        {
            if (effect == null
             || DeathEffectParrySwitchField == null
             || ParrySwitchOnParryPrePauseMethod == null
             || ParrySwitchOnParryPostPauseMethod == null)
            {
                return false;
            }

            var parrySwitch = DeathEffectParrySwitchField.GetValue(effect) as PlayerDeathParrySwitch;
            if (parrySwitch == null)
                return false;

            PlayerId donorPlayerId;
            if (pkt.DonorParticipantId <= (byte)PlayerId.PlayerTwo)
                donorPlayerId = (PlayerId)pkt.DonorParticipantId;
            else
                donorPlayerId = pkt.TargetParticipantId == (byte)PlayerId.PlayerOne
                    ? PlayerId.PlayerTwo
                    : PlayerId.PlayerOne;

            var donor = GetPlayerSafe(donorPlayerId) as LevelPlayerController;
            if (donor == null)
                return false;

            ParrySwitchOnParryPrePauseMethod.Invoke(parrySwitch, new object[] { donor });
            if (!IsDeathEffectExiting(effect) && PlayerDeathEffectOnParrySwitchMethod != null)
                PlayerDeathEffectOnParrySwitchMethod.Invoke(effect, null);

            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(FinishMirroredParrySwitch(parrySwitch, donor));
            else
                ParrySwitchOnParryPostPauseMethod.Invoke(parrySwitch, new object[] { donor });
            return true;
        }

        static IEnumerator FinishMirroredParrySwitch(
            PlayerDeathParrySwitch parrySwitch,
            LevelPlayerController donor)
        {
            bool pausedByUs = false;
            try
            {
                if (PauseManager.state != PauseManager.State.Paused)
                {
                    PauseManager.Pause();
                    pausedByUs = true;
                }
            }
            catch
            {
                pausedByUs = false;
            }

            float endAt = Time.unscaledTime + DeathHeartParryPauseSeconds;
            while (Time.unscaledTime < endAt)
                yield return null;

            float catchUpEndAt = Time.unscaledTime + DeathHeartParryCatchUpCapSeconds;
            while (Time.unscaledTime < catchUpEndAt && IsGuestStillAheadOfHostTimer())
                yield return null;

            try
            {
                if (pausedByUs && PauseManager.state == PauseManager.State.Paused)
                    PauseManager.Unpause();
            }
            catch
            {
            }

            if (parrySwitch != null && donor != null && ParrySwitchOnParryPostPauseMethod != null)
                ParrySwitchOnParryPostPauseMethod.Invoke(parrySwitch, new object[] { donor });
        }

        static IEnumerator HoldGuestForHostPauseCorrection(float seconds)
        {
            _revivePauseCatchUpActive = true;
            bool pausedByUs = false;
            try
            {
                if (PauseManager.state != PauseManager.State.Paused)
                {
                    PauseManager.Pause();
                    pausedByUs = true;
                }

                float endAt = Time.unscaledTime + Mathf.Max(0f, seconds);
                while (Time.unscaledTime < endAt)
                    yield return null;
            }
            finally
            {
                try
                {
                    if (pausedByUs && PauseManager.state == PauseManager.State.Paused)
                        PauseManager.Unpause();
                }
                catch
                {
                }

                _revivePauseCatchUpActive = false;
            }
        }

        static bool IsGuestStillAheadOfHostTimer()
        {
            float localElapsed;
            float hostElapsed;
            float offset;
            if (!SessionSync.TryGetBattleAssistTiming(out localElapsed, out hostElapsed, out offset))
                return false;

            return offset > DeathHeartParryOffsetToleranceSeconds;
        }

        public static bool TryMirrorHostBuiltInRevive(PlayerStatusPacket pkt)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsClient)
                return false;
            if (pkt.ParticipantId > (byte)PlayerId.PlayerTwo)
                return false;
            if (pkt.IsDead || pkt.Health <= 0)
                return false;

            var playerId = (PlayerId)pkt.ParticipantId;
            if (MultiplayerSession.IsAuthoritativePlayer(playerId)
             && pkt.ParticipantId != (byte)MultiplayerSession.LocalId)
            {
                return false;
            }

            var player = GetPlayerSafe(playerId);
            if (player == null)
                return false;
            var pendingDeathEffect = FindLocalDeathEffect(playerId);
            if (!player.IsDead && pendingDeathEffect == null)
                return false;

            Vector2 revivePosition;
            if (!TryGetHostRevivePosition(playerId, out revivePosition))
                revivePosition = player.center;

            if (ShouldDeferHostBuiltInRevive(playerId, pendingDeathEffect))
            {
                QueueDeferredHostBuiltInRevive(playerId, revivePosition, pkt.Tick);
                return true;
            }

            ApplyLocalRevive(playerId, revivePosition, pushStatus: false);
            Plugin.Log.LogInfo(
                "[ReviveSync] Mirrored host revive for "
                + playerId
                + " from status tick "
                + pkt.Tick
                + " at ("
                + revivePosition.x.ToString("0.00")
                + ","
                + revivePosition.y.ToString("0.00")
                + ").");
            return true;
        }

        public static bool TryMirrorHostBuiltInDeath(PlayerStatusPacket pkt)
        {
            if (!MultiplayerSession.IsActive || !MultiplayerSession.IsClient)
                return false;
            if (pkt.ParticipantId > (byte)PlayerId.PlayerTwo)
                return false;
            if (!pkt.IsDead || pkt.Health > 0)
                return false;

            var playerId = (PlayerId)pkt.ParticipantId;
            if (MultiplayerSession.IsAuthoritativePlayer(playerId))
                return false;

            var player = GetPlayerSafe(playerId) as LevelPlayerController;
            if (player == null || player.stats == null)
                return false;

            var existing = FindLocalDeathEffect(playerId);
            if (player.IsDead && existing != null)
            {
                SuppressRemoteBuiltInBody(player);
                return true;
            }

            try
            {
                player.stats.SetHealth(0);

                if (PlayerStatsOnStatsDeathMethod != null)
                    PlayerStatsOnStatsDeathMethod.Invoke(player.stats, null);

                existing = FindLocalDeathEffect(playerId);
                if (existing == null && LevelPlayerOnDeathMethod != null)
                    LevelPlayerOnDeathMethod.Invoke(player, new object[] { player.id });

                existing = FindLocalDeathEffect(playerId);
                if (existing != null)
                    SuppressRemoteBuiltInBody(player);

                Plugin.Log.LogInfo(
                    "[ReviveSync] Mirrored host death for "
                    + playerId
                    + " from status tick "
                    + pkt.Tick
                    + ".");
                return existing != null || player.IsDead;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    "[ReviveSync] Could not mirror host death for "
                    + playerId
                    + ": "
                    + ex.Message);
                return false;
            }
        }

        public static bool TrySuppressRemoteBuiltInDeadBody(LevelPlayerController player)
        {
            if (!ShouldSuppressRemoteBuiltInDeadBody(player))
            {
                RestoreSuppressedBuiltInBody(player);
                return false;
            }

            SuppressRemoteBuiltInBody(player);
            return true;
        }

        public static bool ShouldSuppressRecentBuiltInReviveDeath(PlayerStatusPacket pkt, bool fromRemote)
        {
            if (!MultiplayerSession.IsActive || pkt.ParticipantId > (byte)PlayerId.PlayerTwo)
                return false;
            if (!pkt.IsDead && pkt.Health > 0)
                return false;

            var playerId = (PlayerId)pkt.ParticipantId;
            float suppressUntil;
            if (!RecentBuiltInRevives.TryGetValue(playerId, out suppressUntil))
                return false;
            if (Time.unscaledTime > suppressUntil)
            {
                RecentBuiltInRevives.Remove(playerId);
                return false;
            }

            Vector2 revivePosition;
            if (!TryGetHostRevivePosition(playerId, out revivePosition))
            {
                var player = GetPlayerSafe(playerId);
                revivePosition = player == null ? Vector2.zero : (Vector2)player.center;
            }

            ApplyLocalRevive(playerId, revivePosition, pushStatus: false);
            Plugin.Log.LogInfo(
                "[ReviveSync] Suppressed "
                + (fromRemote ? "remote" : "local")
                + " stale death status for recently revived "
                + playerId
                + " tick="
                + pkt.Tick
                + ".");
            return true;
        }

        public static void RestoreSuppressedBuiltInBody(LevelPlayerController player)
        {
            if (player == null)
                return;

            RestoreSuppressedBuiltInBody(player.id);
        }

        static bool ShouldSuppressRemoteBuiltInDeadBody(LevelPlayerController player)
        {
            if (!MultiplayerSession.IsActive
             || !MultiplayerSession.IsClient
             || player == null
             || player.id > PlayerId.PlayerTwo)
            {
                return false;
            }
            if (MultiplayerSession.IsAuthoritativePlayer(player.id))
                return false;
            if (FindLocalDeathEffect(player.id) == null)
                return false;

            ParticipantStatusTracker.ParticipantStatus status;
            bool statusDead = ParticipantStatusTracker.TryGet((byte)player.id, out status) && status.IsDead;
            return player.IsDead || statusDead;
        }

        static void SuppressRemoteBuiltInBody(LevelPlayerController player)
        {
            if (player == null)
                return;

            Dictionary<Renderer, bool> originalStates;
            if (!SuppressedBuiltInBodyRenderers.TryGetValue(player.id, out originalStates))
            {
                originalStates = new Dictionary<Renderer, bool>();
                SuppressedBuiltInBodyRenderers[player.id] = originalStates;
            }

            var renderers = player.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!originalStates.ContainsKey(renderer))
                    originalStates[renderer] = renderer.enabled;

                renderer.enabled = false;
            }
        }

        static void RestoreSuppressedBuiltInBody(PlayerId playerId)
        {
            Dictionary<Renderer, bool> originalStates;
            if (!SuppressedBuiltInBodyRenderers.TryGetValue(playerId, out originalStates))
                return;

            foreach (var entry in originalStates)
            {
                if (entry.Key != null)
                    entry.Key.enabled = entry.Value;
            }

            SuppressedBuiltInBodyRenderers.Remove(playerId);
        }

        static void RestoreAllSuppressedBuiltInBodies()
        {
            var ids = new List<PlayerId>(SuppressedBuiltInBodyRenderers.Keys);
            for (int i = 0; i < ids.Count; i++)
                RestoreSuppressedBuiltInBody(ids[i]);
        }

        static bool ShouldDeferHostBuiltInRevive(PlayerId playerId, PlayerDeathEffect pendingDeathEffect)
        {
            if (!MultiplayerSession.IsClient || Plugin.Instance == null)
                return false;
            if (!MultiplayerSession.IsAuthoritativePlayer(playerId))
                return true;
            if (pendingDeathEffect == null)
                return false;

            float visualAt;
            return MirroredBuiltInParryVisualAt.TryGetValue(playerId, out visualAt)
                && Time.unscaledTime - visualAt < 1.5f;
        }

        static void QueueDeferredHostBuiltInRevive(PlayerId playerId, Vector2 revivePosition, uint tick)
        {
            uint existingTick;
            if (PendingHostBuiltInReviveTicks.TryGetValue(playerId, out existingTick)
             && !NetTick.IsOlder(existingTick, tick))
            {
                return;
            }

            PendingHostBuiltInReviveTicks[playerId] = tick;
            Plugin.Instance.StartCoroutine(ApplyDeferredHostBuiltInRevive(playerId, revivePosition, tick));
        }

        static IEnumerator ApplyDeferredHostBuiltInRevive(PlayerId playerId, Vector2 revivePosition, uint tick)
        {
            float requestedAt = Time.unscaledTime;
            float hardDeadline = requestedAt + 0.6f;
            while (Time.unscaledTime < hardDeadline)
            {
                float visualAt;
                if (MirroredBuiltInParryVisualAt.TryGetValue(playerId, out visualAt))
                {
                    if (Time.unscaledTime < visualAt + DeathHeartParryPauseSeconds + 0.08f)
                    {
                        yield return null;
                        continue;
                    }

                    break;
                }

                if (Time.unscaledTime - requestedAt >= 0.12f)
                    break;

                yield return null;
            }

            uint latestTick;
            if (!PendingHostBuiltInReviveTicks.TryGetValue(playerId, out latestTick)
             || latestTick != tick)
            {
                yield break;
            }

            PendingHostBuiltInReviveTicks.Remove(playerId);

            var player = GetPlayerSafe(playerId);
            if (player == null)
                yield break;

            var deathEffect = FindLocalDeathEffect(playerId);
            if (!player.IsDead && deathEffect == null)
                yield break;

            ApplyLocalRevive(playerId, revivePosition, pushStatus: false);
            Plugin.Log.LogInfo(
                "[ReviveSync] Applied deferred host revive for "
                + playerId
                + " from status tick "
                + tick
                + " at ("
                + revivePosition.x.ToString("0.00")
                + ","
                + revivePosition.y.ToString("0.00")
                + ").");
        }

        static void SendGrantToRemoteOwner(
            SteamNetManager net,
            byte ownerParticipantId,
            byte targetParticipantId,
            byte donorParticipantId,
            Vector2 revivePosition,
            uint tick,
            bool applyDonorCost,
            bool applyRevive)
        {
            var pkt = new ReviveGrantPacket
            {
                TargetParticipantId = targetParticipantId,
                DonorParticipantId = donorParticipantId,
                Flags = (byte)((applyDonorCost ? 1 : 0) | (applyRevive ? 2 : 0)),
                RevivePosX = revivePosition.x,
                RevivePosY = revivePosition.y,
                Tick = tick,
            };

            net.SendReviveGrantToParticipant(ownerParticipantId, ref pkt);
        }

        static bool IsHostLocalParticipant(byte participantId)
        {
            return participantId == (byte)PlayerId.PlayerOne;
        }

        static void ApplyLocalDonorCost(PlayerId localPlayerId)
        {
            var player = GetPlayerSafe(localPlayerId);
            if (player == null || player.stats == null || !player.stats.PartnerCanSteal)
                return;

            player.stats.OnPartnerStealHealth();
            ParticipantStatusTracker.PushLocalStatus(player);
        }

        static void ApplyLocalRevive(PlayerId localPlayerId, Vector2 revivePosition, bool pushStatus = true)
        {
            var player = GetPlayerSafe(localPlayerId);
            if (player == null)
                return;

            var deathEffect = FindLocalDeathEffect(localPlayerId);
            if (deathEffect != null && DeathEffectExitingField != null)
                DeathEffectExitingField.SetValue(deathEffect, true);

            if (PlayerOnPreReviveMethod != null)
                PlayerOnPreReviveMethod.Invoke(player, new object[] { (Vector3)revivePosition });
            if (PlayerOnReviveMethod != null)
                PlayerOnReviveMethod.Invoke(player, new object[] { (Vector3)revivePosition });

            var levelPlayer = player as LevelPlayerController;
            if (levelPlayer != null && player.stats != null && player.stats.isChalice)
                levelPlayer.motor.OnChaliceRevive();
            if (levelPlayer != null)
                RestoreSuppressedBuiltInBody(levelPlayer);

            if (player.gameObject != null && !player.gameObject.activeSelf)
                player.gameObject.SetActive(true);
            if (player.stats != null && player.stats.Health <= 0)
                player.stats.SetHealth(Mathf.Max(1, player.stats.HealthMax));

            if (deathEffect != null)
                UnityEngine.Object.Destroy(deathEffect.gameObject);

            RecentBuiltInRevives[localPlayerId] = Time.unscaledTime + 2.0f;

            if (pushStatus)
                ParticipantStatusTracker.PushLocalStatus(player);
        }

        static bool TryGetHostRevivePosition(PlayerId playerId, out Vector2 position)
        {
            PlayerStatePacket snapshot;
            if (RemotePlayer.TryGetLocalAuthoritySnapshot(playerId, out snapshot))
            {
                position = new Vector2(snapshot.PosX, snapshot.PosY);
                return true;
            }

            var deathEffect = FindLocalDeathEffect(playerId);
            if (deathEffect != null)
            {
                position = deathEffect.transform.position;
                return true;
            }

            position = Vector2.zero;
            return false;
        }

        static bool IsDeathEffectExiting(PlayerDeathEffect effect)
        {
            if (effect == null || DeathEffectExitingField == null)
                return false;

            object raw = DeathEffectExitingField.GetValue(effect);
            return raw is bool && (bool)raw;
        }

        static bool MarkGrantApplied(ReviveGrantPacket pkt)
        {
            if (AppliedGrantKeys.Count > 128)
                AppliedGrantKeys.Clear();

            long key = ((long)pkt.Tick << 24)
                     ^ ((long)pkt.TargetParticipantId << 16)
                     ^ ((long)pkt.DonorParticipantId << 8)
                     ^ pkt.Flags;
            return AppliedGrantKeys.Add(key);
        }

        static AbstractPlayerController GetPlayerSafe(PlayerId playerId)
        {
            try
            {
                return PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return null;
            }
        }

        static PlayerDeathEffect FindLocalDeathEffect(PlayerId localPlayerId)
        {
            ScratchDeathEffects.Clear();
            ScratchDeathEffects.AddRange(UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>());
            for (int i = 0; i < ScratchDeathEffects.Count; i++)
            {
                var effect = ScratchDeathEffects[i];
                if (effect == null)
                    continue;

                PlayerId effectPlayerId;
                if (!TryGetDeathEffectPlayerId(effect, out effectPlayerId))
                    continue;
                if (effectPlayerId == localPlayerId)
                    return effect;
            }

            return null;
        }

        static bool TryGetDeathEffectPlayerId(PlayerDeathEffect effect, out PlayerId playerId)
        {
            playerId = PlayerId.PlayerOne;
            if (effect == null || DeathEffectPlayerIdField == null)
                return false;

            object raw = DeathEffectPlayerIdField.GetValue(effect);
            if (!(raw is PlayerId))
                return false;

            playerId = (PlayerId)raw;
            return true;
        }
    }
}
