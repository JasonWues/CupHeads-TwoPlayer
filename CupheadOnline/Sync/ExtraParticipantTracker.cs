using System.Collections.Generic;
using System.Text;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Tracks non-built-in network participants (participant ids > 1) as
    /// lightweight world markers plus aggregate positions. This is the first
    /// safe slice beyond Cuphead's hard-coded two real player objects.
    /// </summary>
    public static class ExtraParticipantTracker
    {
        sealed class ParticipantState
        {
            public byte ParticipantId;
            public Vector2 Position;
            public Vector2 CameraCenter;
            public Vector2 TopPosition;
            public bool IsDead;
            public uint LastTick;
            public float LastSeenAt;
            public GameObject Marker;
            public SpriteRenderer MarkerRenderer;
        }

        const float StaleTimeout = 1.5f;
        const float MarkerYOffset = 180f;
        const float MarkerPulseSpeed = 3.5f;

        static readonly Dictionary<byte, ParticipantState> _states =
            new Dictionary<byte, ParticipantState>(4);
        static readonly List<byte> _staleIds = new List<byte>(4);
        static readonly List<byte> _sortedIds = new List<byte>(4);
        static Texture2D _markerTexture;
        static Sprite _markerSprite;

        static readonly Color[] MarkerColors =
        {
            new Color(0.98f, 0.84f, 0.22f, 0.9f),
            new Color(0.25f, 0.88f, 0.93f, 0.9f),
            new Color(0.98f, 0.48f, 0.38f, 0.9f),
            new Color(0.60f, 0.92f, 0.46f, 0.9f),
            new Color(0.93f, 0.53f, 0.94f, 0.9f),
            new Color(0.98f, 0.70f, 0.28f, 0.9f),
        };

        static ExtraParticipantTracker()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static int LiveCount
        {
            get
            {
                PruneStale();
                int liveCount = 0;
                foreach (var state in _states.Values)
                {
                    if (state != null && !state.IsDead)
                        liveCount++;
                }

                return liveCount;
            }
        }

        public static int TotalCount
        {
            get
            {
                PruneStale();
                return _states.Count;
            }
        }

        public static int DeadCount
        {
            get
            {
                PruneStale();

                int deadCount = 0;
                foreach (var state in _states.Values)
                {
                    if (state != null && state.IsDead)
                        deadCount++;
                }

                return deadCount;
            }
        }

        public static void Apply(PlayerStatePacket pkt)
        {
            if (pkt.PlayerId <= (byte)PlayerId.PlayerTwo)
                return;

            ParticipantState state;
            if (!_states.TryGetValue(pkt.PlayerId, out state))
            {
                state = new ParticipantState { ParticipantId = pkt.PlayerId };
                _states[pkt.PlayerId] = state;
            }
            else if (NetTick.IsOlder(pkt.Tick, state.LastTick))
            {
                return;
            }

            state.Position = new Vector2(pkt.PosX, pkt.PosY);
            state.CameraCenter = state.Position;
            state.TopPosition = state.Position;
            state.IsDead = pkt.IsDead;
            state.LastTick = pkt.Tick;
            state.LastSeenAt = Time.unscaledTime;

            MultiplayerSession.RegisterRemoteParticipant(pkt.PlayerId);
            EnsureMarker(state);
            UpdateMarker(state);
        }

        public static bool TryGetAggregate(
            out int count,
            out Vector2 center,
            out Vector2 cameraCenter,
            out Vector2 topPlayerPosition)
        {
            PruneStale();

            count = 0;
            center = Vector2.zero;
            cameraCenter = Vector2.zero;
            topPlayerPosition = Vector2.zero;

            bool hasTop = false;
            float topY = float.MinValue;
            float topXSum = 0f;

            foreach (var state in _states.Values)
            {
                if (state == null)
                    continue;
                if (state.IsDead)
                    continue;

                count++;
                center += state.Position;
                cameraCenter += state.CameraCenter;
                topXSum += state.TopPosition.x;
                if (!hasTop || state.TopPosition.y > topY)
                {
                    topY = state.TopPosition.y;
                    hasTop = true;
                }
            }

            if (count <= 0)
                return false;

            center /= count;
            cameraCenter /= count;
            topPlayerPosition = new Vector2(topXSum / count, topY);
            return true;
        }

        public static bool AnyInRect(Rect rect)
        {
            PruneStale();

            foreach (var entry in _states)
            {
                var state = entry.Value;
                if (state == null)
                    continue;
                if (state.IsDead)
                    continue;

                Bounds hitbox;
                if (ExtraRemoteAvatarManager.TryGetHitbox(entry.Key, out hitbox))
                {
                    if (rect.Contains(new Vector2(hitbox.center.x, hitbox.center.y)))
                        return true;
                }
                else if (rect.Contains(state.Position))
                {
                    return true;
                }
            }

            return false;
        }

        public static int AppendCameraCenters(List<Vector2> target)
        {
            if (target == null)
                return 0;

            PruneStale();

            int added = 0;
            foreach (var entry in _states)
            {
                var state = entry.Value;
                if (state == null)
                    continue;
                if (state.IsDead)
                    continue;

                Bounds hitbox;
                if (ExtraRemoteAvatarManager.TryGetHitbox(entry.Key, out hitbox))
                    target.Add(new Vector2(hitbox.center.x, hitbox.center.y));
                else
                    target.Add(state.CameraCenter);

                added++;
            }

            return added;
        }

        public static bool TryGetPosition(byte participantId, out Vector2 position)
        {
            position = Vector2.zero;
            PruneStale();

            ParticipantState state;
            if (!_states.TryGetValue(participantId, out state) || state == null)
                return false;

            Bounds hitbox;
            if (ExtraRemoteAvatarManager.TryGetHitbox(participantId, out hitbox))
                position = new Vector2(hitbox.center.x, hitbox.center.y);
            else
                position = state.Position;

            return true;
        }

        public static string BuildStatusSummary()
        {
            PruneStale();
            if (_states.Count <= 0)
                return string.Empty;

            string richerSummary = ParticipantStatusTracker.BuildExtraSummary();
            if (!string.IsNullOrEmpty(richerSummary))
                return richerSummary;

            _sortedIds.Clear();
            foreach (var entry in _states)
                _sortedIds.Add(entry.Key);

            _sortedIds.Sort();

            var sb = new StringBuilder();
            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantState state;
                if (!_states.TryGetValue(_sortedIds[i], out state) || state == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append(" | ");

                sb.Append("P");
                sb.Append(state.ParticipantId + 1);
                sb.Append(" ");
                sb.Append(state.IsDead ? "DOWN" : "LIVE");
            }

            return sb.ToString();
        }

        public static void RemoveParticipant(byte participantId)
        {
            ParticipantState state;
            if (_states.TryGetValue(participantId, out state))
            {
                DestroyMarker(state);
                _states.Remove(participantId);
            }

            ExtraRemoteAvatarManager.RemoveParticipant(participantId);
            MultiplayerSession.UnregisterParticipant(participantId);
        }

        public static void Update()
        {
            PruneStale();

            foreach (var state in _states.Values)
            {
                if (state == null)
                    continue;

                UpdateMarker(state);
            }
        }

        public static void Reset()
        {
            foreach (var state in _states.Values)
                DestroyMarker(state);

            _states.Clear();
            ExtraRemoteAvatarManager.Reset();
        }

        static void EnsureMarker(ParticipantState state)
        {
            if (state == null)
                return;
            if (state.IsDead)
            {
                DestroyMarker(state);
                return;
            }

            if (ExtraRemoteAvatarManager.HasAvatar(state.ParticipantId))
            {
                DestroyMarker(state);
                return;
            }

            if (state.Marker != null)
                return;

            EnsureMarkerSprite();

            state.Marker = new GameObject("NetworkParticipantMarker_" + state.ParticipantId);
            state.Marker.hideFlags = HideFlags.HideAndDontSave;
            state.MarkerRenderer = state.Marker.AddComponent<SpriteRenderer>();
            state.MarkerRenderer.sprite = _markerSprite;
            state.MarkerRenderer.sortingOrder = 3200;
            state.MarkerRenderer.color = MarkerColors[
                (state.ParticipantId - (byte)PlayerId.PlayerTwo - 1) % MarkerColors.Length];
            state.Marker.transform.localScale = new Vector3(90f, 90f, 1f);
        }

        static void UpdateMarker(ParticipantState state)
        {
            if (state == null)
                return;
            if (state.IsDead)
            {
                DestroyMarker(state);
                return;
            }

            if (ExtraRemoteAvatarManager.HasAvatar(state.ParticipantId))
            {
                DestroyMarker(state);
                return;
            }

            if (state.Marker == null)
                return;

            float pulse = 1f + Mathf.Sin(Time.unscaledTime * MarkerPulseSpeed + state.ParticipantId) * 0.12f;
            state.Marker.transform.position = new Vector3(
                state.Position.x,
                state.Position.y + MarkerYOffset,
                0f);
            state.Marker.transform.localScale = new Vector3(90f * pulse, 90f * pulse, 1f);
        }

        static void DestroyMarker(ParticipantState state)
        {
            if (state != null && state.Marker != null)
                Object.Destroy(state.Marker);
        }

        static void PruneStale()
        {
            if (_states.Count == 0)
                return;

            float now = Time.unscaledTime;
            _staleIds.Clear();

            foreach (var entry in _states)
            {
                var state = entry.Value;
                if (state == null || now - state.LastSeenAt > StaleTimeout)
                    _staleIds.Add(entry.Key);
            }

            for (int i = 0; i < _staleIds.Count; i++)
                RemoveParticipant(_staleIds[i]);
        }

        static void EnsureMarkerSprite()
        {
            if (_markerSprite != null)
                return;

            _markerTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _markerTexture.name = "CupheadOnlineExtraParticipantMarker";
            _markerTexture.hideFlags = HideFlags.HideAndDontSave;
            _markerTexture.SetPixel(0, 0, Color.white);
            _markerTexture.Apply(false, true);

            _markerSprite = Sprite.Create(
                _markerTexture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);
            _markerSprite.name = "CupheadOnlineExtraParticipantMarker";
        }
    }
}
