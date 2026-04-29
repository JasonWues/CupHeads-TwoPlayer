using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// HOST: broadcasts enemy / boss state at 20 Hz (every 3rd FixedUpdate at 60 Hz).
    /// CLIENT: receives EnemyStatePacket and snaps enemy positions + HP + animation.
    ///
    /// The boss AI continues to run independently on the client; we correct it every
    /// ~50 ms so minor divergences are continuously repaired without hard-snapping
    /// which would cause visual pops.
    ///
    /// HP reflection cache: The game does not expose a public HP getter on enemies.
    /// We locate the first 'float hp' (or 'HP', 'health') field on the DamageReceiver
    /// and use it.  If the field is not found, HP sync is silently skipped.
    /// </summary>
    public static class EnemySyncManager
    {
        private static int   _broadcastCounter;
        private const  int   BROADCAST_EVERY = 3; // frames (= 20 Hz at 60 Hz FixedUpdate)
        private const  float HIGH_LATENCY_LEAD_MULTIPLIER = 1.0f;
        private const  float HIGH_LATENCY_CLOCK_LEAD_MULTIPLIER = 1.0f;
        private static int   _recoveryBurstFrames;

        // Reflection cache for enemy HP field, keyed per runtime enemy type.
        private static readonly Dictionary<System.Type, FieldInfo> _hpFields =
            new Dictionary<System.Type, FieldInfo>(64);
        private static readonly HashSet<System.Type> _missingHpFieldTypes =
            new HashSet<System.Type>();
        private static readonly Dictionary<int, EnemySnapshotState> _lastSent = new Dictionary<int, EnemySnapshotState>();
        private static readonly Dictionary<int, uint> _lastReceivedTicks = new Dictionary<int, uint>(128);
        private static readonly Dictionary<int, EnemyStatePacket> _lastReceivedStates = new Dictionary<int, EnemyStatePacket>(128);
        private static readonly Dictionary<int, EnemyMotionEstimate> _motionEstimates = new Dictionary<int, EnemyMotionEstimate>(128);
        private static readonly HashSet<int> _suppressedClientEnemies = new HashSet<int>();
        private static uint _lastReceivedBossTick;
        private static float _lastHighLatencyVisualLogAt = -1f;

        private struct EnemySnapshotState
        {
            public float Hp;
            public byte Phase;
            public Vector3 Position;
        }

        private struct EnemyMotionEstimate
        {
            public bool HasSample;
            public bool HasVelocity;
            public Vector2 LastPosition;
            public float LastTime;
            public Vector2 Velocity;
            public Vector2 Acceleration;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HOST side: called every FixedUpdate from Plugin.Update indirectly via
        //  a MonoBehaviour we attach, or from the PlayerMotorPatch tick.
        //  We call it from Plugin.Update to keep it off the physics thread.
        // ──────────────────────────────────────────────────────────────────────

        public static void HostTick()
        {
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected) return;

            var enemies = Object.FindObjectsOfType<DamageReceiver>();
            int enemyCount = CountEnemies(enemies);
            bool bossPriorityMode = _recoveryBurstFrames > 0 || enemyCount <= 3;
            float bossHp;
            float bossTotalHp;
            bool hasBossHealth = TryGetLevelBossHealth(out bossHp, out bossTotalHp);

            _broadcastCounter++;
            int broadcastEvery = bossPriorityMode ? 1 : BROADCAST_EVERY;
            if (_broadcastCounter < broadcastEvery) return;
            _broadcastCounter = 0;
            if (_recoveryBurstFrames > 0)
                _recoveryBurstFrames--;

            foreach (var dr in enemies)
            {
                if (dr.type != DamageReceiver.Type.Enemy) continue;
                var go = dr.gameObject;

                float hp   = GetEnemyHp(dr);
                byte  phase = GetEnemyPhase(dr);
                int   hash  = 0;
                float animTime = 0f;
                float velX = 0f;
                float velY = 0f;
                var   anim  = GetPrimaryAnimator(go);
                if (anim != null)
                {
                    var state = anim.GetCurrentAnimatorStateInfo(0);
                    hash = state.fullPathHash;
                    animTime = Mathf.Repeat(state.normalizedTime, 1f);
                }
                var rb = GetPrimaryRigidbody(go);
                if (rb != null)
                {
                    velX = rb.velocity.x;
                    velY = rb.velocity.y;
                }

                bool priority = bossPriorityMode || IsBossPriority(go, phase, enemyCount);

                var pkt = new EnemyStatePacket
                {
                    InstanceId = EnemyRegistry.GetStableKey(dr),
                    PosX       = go.transform.position.x,
                    PosY       = go.transform.position.y,
                    Hp         = hp,
                    Phase      = phase,
                    AnimHash   = hash,
                    Tick       = MultiplayerSession.Tick,
                    BossHp     = hasBossHealth ? bossHp : -1f,
                    BossTotalHp = hasBossHealth ? bossTotalHp : -1f,
                    AnimNormalizedTime = animTime,
                    StateTime = HighLatencyInputSync.PacketTimeNow(),
                    VelX = velX,
                    VelY = velY,
                };

                bool reliable = priority && ShouldSendReliableDelta(pkt);
                Plugin.Net.SendEnemyState(ref pkt, reliable);
                _lastSent[pkt.InstanceId] = new EnemySnapshotState
                {
                    Hp = pkt.Hp,
                    Phase = pkt.Phase,
                    Position = go.transform.position,
                };
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  CLIENT side: correct enemy state
        // ──────────────────────────────────────────────────────────────────────

        public static void OnEnemyStateReceived(EnemyStatePacket pkt)
        {
            bool deterministicHighLatency = ShouldTrustDeterministicClientEnemySimulation();
            if (!deterministicHighLatency || ShouldApplyHighLatencyBossHealthAuthority())
                ApplyLevelBossHealth(pkt);

            if (!EnemyRegistry.TryGet(pkt.InstanceId, out var dr))
            {
                EnemyRegistry.MarkDirty(); // trigger a rescan next query
                if (!EnemyRegistry.TryGet(pkt.InstanceId, out dr)) return;
            }

            SuppressClientEnemySimulation(dr);

            uint lastReceivedTick;
            if (_lastReceivedTicks.TryGetValue(pkt.InstanceId, out lastReceivedTick)
             && !NetTick.IsNewer(pkt.Tick, lastReceivedTick))
            {
                return;
            }

            if (ShouldDropUntimedHighLatencyTransform(pkt))
            {
                if (!deterministicHighLatency)
                    SetEnemyHp(dr, pkt.Hp);
                return;
            }

            _lastReceivedTicks[pkt.InstanceId] = pkt.Tick;
            UpdateMotionEstimate(pkt);
            _lastReceivedStates[pkt.InstanceId] = pkt;

            if (deterministicHighLatency)
                return;

            var go = dr.gameObject;

            // ── Position: gentle lerp to avoid visual snap ────────────────────
            var targetPos = GetPresentationPosition(pkt, go);
            float distance = Vector3.Distance(go.transform.position, targetPos);
            if (Plugin.VanillaTwoPlayerOnline)
            {
                go.transform.position = targetPos;
            }
            else
            {
                go.transform.position = distance > 6f
                    ? targetPos
                    : Vector3.Lerp(go.transform.position, targetPos, 0.3f);
            }

            // ── HP correction ─────────────────────────────────────────────────
            SetEnemyHp(dr, pkt.Hp);

            // ── Animation: play the host's animator state ─────────────────────
            var anim = GetPrimaryAnimator(go);
            if (anim != null && pkt.AnimHash != 0)
            {
                // Only force the state if there's a significant divergence;
                // avoid overriding transition logic every single frame.
                int localHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
                float localTime = Mathf.Repeat(anim.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f);
                float remoteTime = GetPresentationAnimTime(pkt);
                float wrappedDelta = Mathf.Abs(Mathf.Repeat(localTime - remoteTime + 0.5f, 1f) - 0.5f);
                if (localHash != pkt.AnimHash || (Plugin.VanillaTwoPlayerOnline && wrappedDelta > 0.03f))
                    anim.Play(pkt.AnimHash, 0, remoteTime);
            }
        }

        public static void ClientLateTick()
        {
            if (!MultiplayerSession.IsClient || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            SuppressAllClientEnemySimulation();
            if (_lastReceivedStates.Count == 0)
                return;

            var keys = new List<int>(_lastReceivedStates.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                EnemyStatePacket pkt;
                if (!_lastReceivedStates.TryGetValue(keys[i], out pkt))
                    continue;

                DamageReceiver dr;
                if (!EnemyRegistry.TryGet(pkt.InstanceId, out dr) || dr == null)
                {
                    _lastReceivedStates.Remove(keys[i]);
                    continue;
                }

                if (ShouldTrustDeterministicClientEnemySimulation())
                    continue;

                ApplyLatestEnemySnapshot(dr, pkt);
            }
        }

        static void ApplyLatestEnemySnapshot(DamageReceiver dr, EnemyStatePacket pkt)
        {
            var go = dr.gameObject;

            var targetPos = GetPresentationPosition(pkt, go);
            if (Plugin.VanillaTwoPlayerOnline)
            {
                go.transform.position = targetPos;
            }
            else
            {
                float distance = Vector3.Distance(go.transform.position, targetPos);
                go.transform.position = distance > 6f
                    ? targetPos
                    : Vector3.Lerp(go.transform.position, targetPos, 0.3f);
            }

            if (!ShouldTrustDeterministicClientEnemySimulation())
                SetEnemyHp(dr, pkt.Hp);

            var anim = GetPrimaryAnimator(go);
            if (anim == null || pkt.AnimHash == 0)
                return;

            int localHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            float localTime = Mathf.Repeat(anim.GetCurrentAnimatorStateInfo(0).normalizedTime, 1f);
            float remoteTime = GetPresentationAnimTime(pkt);
            float wrappedDelta = Mathf.Abs(Mathf.Repeat(localTime - remoteTime + 0.5f, 1f) - 0.5f);
            if (localHash != pkt.AnimHash || (Plugin.VanillaTwoPlayerOnline && wrappedDelta > 0.03f))
                anim.Play(pkt.AnimHash, 0, remoteTime);
        }

        static bool ShouldPredictHostPresentation(EnemyStatePacket pkt)
        {
            if (!Plugin.VanillaTwoPlayerOnline || !MultiplayerSession.IsClient)
                return false;
            if (pkt.StateTime < 0f)
                return false;

            return EstimateIncomingEnemySnapshotAgeSeconds() >= 0.20f;
        }

        static bool ShouldTrustDeterministicClientEnemySimulation()
        {
            return Plugin.VanillaTwoPlayerOnline
                && MultiplayerSession.IsClient
                && HighLatencyInputSync.ShouldSimulateBuiltInRemotePlayers();
        }

        static bool ShouldApplyHighLatencyBossHealthAuthority()
        {
            return Plugin.VanillaTwoPlayerOnline
                && MultiplayerSession.IsClient
                && HighLatencyInputSync.ShouldSimulateBuiltInRemotePlayers();
        }

        static bool ShouldDropUntimedHighLatencyTransform(EnemyStatePacket pkt)
        {
            if (HighLatencyInputSync.ShouldSimulateBuiltInRemotePlayers())
                return false;
            if (!Plugin.VanillaTwoPlayerOnline || !MultiplayerSession.IsClient)
                return false;
            if (pkt.StateTime >= 0f)
                return false;

            return EstimateIncomingEnemySnapshotAgeSeconds() >= 0.20f;
        }

        static Vector3 GetPresentationPosition(EnemyStatePacket pkt, GameObject go)
        {
            var target = new Vector3(pkt.PosX, pkt.PosY, go.transform.position.z);
            if (!ShouldPredictHostPresentation(pkt))
                return target;

            EnemyMotionEstimate estimate;
            if (!_motionEstimates.TryGetValue(pkt.InstanceId, out estimate) || !estimate.HasVelocity)
                return target;

            float age = GetPresentationAge(pkt);
            var basePos = new Vector2(pkt.PosX, pkt.PosY);
            var velocity = new Vector2(pkt.VelX, pkt.VelY);
            if (velocity.sqrMagnitude < 1f)
                velocity = estimate.Velocity;
            var predicted = basePos
                + velocity * age
                + estimate.Acceleration * (0.5f * age * age);

            var delta = predicted - basePos;
            float maxLead = Mathf.Max(120f, 520f * age);
            if (delta.magnitude > maxLead)
                predicted = basePos + delta.normalized * maxLead;

            LogHighLatencyVisualMode();
            return new Vector3(predicted.x, predicted.y, go.transform.position.z);
        }

        static float GetPresentationAnimTime(EnemyStatePacket pkt)
        {
            float t = Mathf.Repeat(pkt.AnimNormalizedTime, 1f);
            if (!ShouldPredictHostPresentation(pkt))
                return t;

            float age = GetPresentationAge(pkt);
            return Mathf.Repeat(t + age, 1f);
        }

        static float GetPresentationAge(EnemyStatePacket pkt)
        {
            if (pkt.StateTime >= 0f)
            {
                float clockAge = HighLatencyInputSync.PlayoutTimeNow() - pkt.StateTime;
                if (clockAge >= 0f && clockAge <= 2.5f)
                    return Mathf.Clamp(clockAge * HIGH_LATENCY_CLOCK_LEAD_MULTIPLIER, 0f, 1.25f);
            }

            float estimatedAge = EstimateIncomingEnemySnapshotAgeSeconds() * HIGH_LATENCY_LEAD_MULTIPLIER;
            return Mathf.Clamp(estimatedAge, 0f, 1.25f);
        }

        static void UpdateMotionEstimate(EnemyStatePacket pkt)
        {
            if (pkt.StateTime < 0f)
                return;

            var position = new Vector2(pkt.PosX, pkt.PosY);
            EnemyMotionEstimate estimate;
            if (!_motionEstimates.TryGetValue(pkt.InstanceId, out estimate) || !estimate.HasSample)
            {
                estimate = new EnemyMotionEstimate
                {
                    HasSample = true,
                    LastPosition = position,
                    LastTime = pkt.StateTime,
                };
                _motionEstimates[pkt.InstanceId] = estimate;
                return;
            }

            float dt = pkt.StateTime - estimate.LastTime;
            if (dt <= 0.001f || dt > 0.5f)
            {
                estimate.LastPosition = position;
                estimate.LastTime = pkt.StateTime;
                _motionEstimates[pkt.InstanceId] = estimate;
                return;
            }

            var newVelocity = (position - estimate.LastPosition) / dt;
            if (estimate.HasVelocity)
            {
                var newAcceleration = (newVelocity - estimate.Velocity) / dt;
                estimate.Acceleration = Vector2.Lerp(estimate.Acceleration, newAcceleration, 0.35f);
                estimate.Velocity = Vector2.Lerp(estimate.Velocity, newVelocity, 0.65f);
            }
            else
            {
                estimate.Velocity = newVelocity;
                estimate.Acceleration = Vector2.zero;
                estimate.HasVelocity = true;
            }

            estimate.LastPosition = position;
            estimate.LastTime = pkt.StateTime;
            _motionEstimates[pkt.InstanceId] = estimate;
        }

        static float EstimateIncomingEnemySnapshotAgeSeconds()
        {
            float age = 0f;

            if (Plugin.Net != null && Plugin.Net.Latency > 0)
                age = Mathf.Max(age, Plugin.Net.Latency * 0.0005f);

            if (Plugin.LanArtificialLatencyMs > 0)
                age = Mathf.Max(age, Plugin.LanArtificialLatencyMs / 1000f);

            return Mathf.Clamp(age, 0f, 2.5f);
        }

        static void LogHighLatencyVisualMode()
        {
            float now = Time.unscaledTime;
            if (_lastHighLatencyVisualLogAt > 0f && now - _lastHighLatencyVisualLogAt < 5f)
                return;

            _lastHighLatencyVisualLogAt = now;
            Plugin.Log.LogInfo(
                "[EnemySync] High-latency host-state prediction active. Estimated snapshot age="
                + EstimateIncomingEnemySnapshotAgeSeconds().ToString("0.000")
                + "s.");
        }

        static Animator GetPrimaryAnimator(GameObject go)
        {
            if (go == null)
                return null;

            var direct = go.GetComponent<Animator>();
            return direct != null ? direct : go.GetComponentInChildren<Animator>();
        }

        static Rigidbody2D GetPrimaryRigidbody(GameObject go)
        {
            if (go == null)
                return null;

            var direct = go.GetComponent<Rigidbody2D>();
            return direct != null ? direct : go.GetComponentInChildren<Rigidbody2D>();
        }

        public static void Reset()
        {
            _broadcastCounter = 0;
            _recoveryBurstFrames = 0;
            _lastSent.Clear();
            _lastReceivedTicks.Clear();
            _lastReceivedStates.Clear();
            _motionEstimates.Clear();
            _suppressedClientEnemies.Clear();
            _lastReceivedBossTick = 0;
            _lastHighLatencyVisualLogAt = -1f;
        }

        static void SuppressAllClientEnemySimulation()
        {
            if (!ShouldSuppressClientEnemySimulation())
                return;

            var enemies = Object.FindObjectsOfType<DamageReceiver>();
            for (int i = 0; i < enemies.Length; i++)
            {
                var dr = enemies[i];
                if (dr != null && dr.type == DamageReceiver.Type.Enemy)
                    SuppressClientEnemySimulation(dr);
            }
        }

        static void SuppressClientEnemySimulation(DamageReceiver dr)
        {
            if (!ShouldSuppressClientEnemySimulation() || dr == null || dr.gameObject == null)
                return;

            int id = dr.gameObject.GetInstanceID();
            if (!_suppressedClientEnemies.Add(id))
                return;

            var behaviours = dr.gameObject.GetComponentsInChildren<MonoBehaviour>(true);
            int disabled = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || ReferenceEquals(behaviour, dr))
                    continue;
                if (behaviour is DamageReceiver)
                    continue;

                behaviour.enabled = false;
                disabled++;
            }

            Plugin.Log.LogInfo("[EnemySync] Suppressed client enemy simulation on "
                + dr.gameObject.name
                + " (disabled "
                + disabled
                + " behaviours).");
        }

        static bool ShouldSuppressClientEnemySimulation()
        {
            return false;
        }

        public static void TriggerRecoveryBurst(int frames = 150)
        {
            _recoveryBurstFrames = Mathf.Max(_recoveryBurstFrames, frames);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HP reflection
        // ──────────────────────────────────────────────────────────────────────

        static float GetEnemyHp(DamageReceiver dr)
        {
            var fi = FindHpField(dr);
            if (fi == null) return -1f;
            try { return (float)fi.GetValue(dr); }
            catch { return -1f; }
        }

        static void SetEnemyHp(DamageReceiver dr, float hp)
        {
            if (hp < 0f) return;
            var fi = FindHpField(dr);
            if (fi == null) return;
            try { fi.SetValue(dr, hp); }
            catch { /* field type mismatch — silently skip */ }
        }

        static FieldInfo FindHpField(DamageReceiver dr)
        {
            if (dr == null)
                return null;

            var t = dr.GetType();
            FieldInfo fi;
            if (_hpFields.TryGetValue(t, out fi))
                return fi;

            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            foreach (var name in new[] { "hp", "HP", "health", "_hp", "currentHp", "currentHealth" })
            {
                fi = t.GetField(name, bf);
                if (fi != null && fi.FieldType == typeof(float))
                {
                    _hpFields[t] = fi;
                    return fi;
                }
            }
            _hpFields[t] = null;
            if (_missingHpFieldTypes.Add(t))
                Plugin.Log.LogWarning("[EnemySync] Could not find HP field on " + t.Name + " - HP sync disabled for that enemy type.");
            return null;
        }

        static byte GetEnemyPhase(DamageReceiver dr)
        {
            // Try to read a common 'phase' or 'currentPhase' int field
            var t  = dr.GetType();
            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            foreach (var name in new[] { "phase", "currentPhase", "_phase", "Phase" })
            {
                var fi = t.GetField(name, bf);
                if (fi != null && fi.FieldType == typeof(int))
                {
                    try { return (byte)(int)fi.GetValue(dr); }
                    catch { }
                }
            }
            return 0;
        }

        static int CountEnemies(DamageReceiver[] receivers)
        {
            int count = 0;
            for (int i = 0; i < receivers.Length; i++)
            {
                if (receivers[i] != null && receivers[i].type == DamageReceiver.Type.Enemy)
                    count++;
            }
            return count;
        }

        static bool ShouldSendReliableDelta(EnemyStatePacket pkt)
        {
            EnemySnapshotState previous;
            if (!_lastSent.TryGetValue(pkt.InstanceId, out previous))
                return true;

            if (previous.Phase != pkt.Phase)
                return true;
            if (Mathf.Abs(previous.Hp - pkt.Hp) >= 0.5f)
                return true;

            var prevPos = previous.Position;
            float dx = prevPos.x - pkt.PosX;
            float dy = prevPos.y - pkt.PosY;
            return (dx * dx + dy * dy) >= 16f;
        }

        static bool IsBossPriority(GameObject go, byte phase, int enemyCount)
        {
            if (phase > 0)
                return true;
            if (enemyCount <= 3)
                return true;
            if (go == null)
                return false;

            string name = go.name.ToLowerInvariant();
            return name.Contains("boss")
                || name.Contains("baroness")
                || name.Contains("dragon")
                || name.Contains("robot")
                || name.Contains("saltbaker")
                || name.Contains("dice")
                || name.Contains("devil")
                || name.Contains("pirate")
                || name.Contains("train")
                || name.Contains("genie")
                || name.Contains("clown")
                || name.Contains("flower")
                || name.Contains("blimp")
                || name.Contains("bee");
        }

        static void ApplyLevelBossHealth(EnemyStatePacket pkt)
        {
            if (pkt.BossHp < 0f || pkt.BossTotalHp <= 0f)
                return;
            if (_lastReceivedBossTick != 0 && !NetTick.IsNewer(pkt.Tick, _lastReceivedBossTick))
                return;
            _lastReceivedBossTick = pkt.Tick;

            object properties = GetCurrentLevelProperties();
            if (properties == null)
                return;

            var type = properties.GetType();
            var currentProperty = type.GetProperty("CurrentHealth", BindingFlags.Instance | BindingFlags.Public);
            var totalField = type.GetField("TotalHealth", BindingFlags.Instance | BindingFlags.Public);
            if (currentProperty == null || !currentProperty.CanWrite || totalField == null)
                return;

            try
            {
                float total = Mathf.Max(1f, pkt.BossTotalHp);
                float hp = Mathf.Clamp(pkt.BossHp, 0f, total);
                float current = (float)currentProperty.GetValue(properties, null);
                if (hp < current - 0.01f)
                {
                    var dealDamage = type.GetMethod("DealDamage", BindingFlags.Instance | BindingFlags.Public);
                    if (dealDamage != null)
                    {
                        dealDamage.Invoke(properties, new object[] { current - hp });
                        current = (float)currentProperty.GetValue(properties, null);
                    }
                }

                totalField.SetValue(properties, total);
                if (Mathf.Abs(current - hp) > 0.01f)
                    currentProperty.SetValue(properties, hp, null);
            }
            catch
            {
            }
        }

        static bool TryGetLevelBossHealth(out float current, out float total)
        {
            current = -1f;
            total = -1f;

            object properties = GetCurrentLevelProperties();
            if (properties == null)
                return false;

            var type = properties.GetType();
            var currentProperty = type.GetProperty("CurrentHealth", BindingFlags.Instance | BindingFlags.Public);
            var totalField = type.GetField("TotalHealth", BindingFlags.Instance | BindingFlags.Public);
            if (currentProperty == null || totalField == null)
                return false;

            try
            {
                current = (float)currentProperty.GetValue(properties, null);
                total = (float)totalField.GetValue(properties);
                return total > 0f && current >= 0f;
            }
            catch
            {
                current = -1f;
                total = -1f;
                return false;
            }
        }

        static object GetCurrentLevelProperties()
        {
            if (Level.Current == null)
                return null;

            var type = Level.Current.GetType();
            while (type != null)
            {
                var field = type.GetField("properties", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null)
                {
                    try { return field.GetValue(Level.Current); }
                    catch { return null; }
                }
                type = type.BaseType;
            }

            return null;
        }
    }
}
