using System.Collections.Generic;
using System.Text;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    public static class ParticipantStatusTracker
    {
        public struct ParticipantStatus
        {
            public byte ParticipantId;
            public byte Health;
            public byte HealthMax;
            public bool IsDead;
            public bool CanDonate;
            public bool IsChalice;
            public bool IsMugman;
            public bool IsKnown;
            public uint Tick;
        }

        static readonly Dictionary<byte, ParticipantStatus> _statuses =
            new Dictionary<byte, ParticipantStatus>(6);
        static readonly List<byte> _sortedIds =
            new List<byte>(6);

        static ParticipantStatusTracker()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Reset()
        {
            _statuses.Clear();
        }

        public static void Apply(PlayerStatusPacket pkt)
        {
            ParticipantStatus existing;
            if (_statuses.TryGetValue(pkt.ParticipantId, out existing)
             && NetTick.IsOlder(pkt.Tick, existing.Tick))
            {
                return;
            }

            _statuses[pkt.ParticipantId] = new ParticipantStatus
            {
                ParticipantId = pkt.ParticipantId,
                Health = pkt.Health,
                HealthMax = pkt.HealthMax,
                IsDead = pkt.IsDead,
                CanDonate = pkt.CanDonate,
                IsChalice = pkt.IsChalice,
                IsMugman = pkt.IsMugman,
                IsKnown = true,
                Tick = pkt.Tick,
            };
        }

        public static bool TryGet(byte participantId, out ParticipantStatus status)
        {
            if (_statuses.TryGetValue(participantId, out status))
                return true;

            if (participantId <= (byte)PlayerId.PlayerTwo)
            {
                var player = PlayerManager.GetPlayer((PlayerId)participantId);
                if (player != null && player.stats != null)
                {
                    status = new ParticipantStatus
                    {
                        ParticipantId = participantId,
                        Health = (byte)Mathf.Clamp(player.stats.Health, 0, 255),
                        HealthMax = (byte)Mathf.Clamp(player.stats.HealthMax, 1, 255),
                        IsDead = player.IsDead,
                        CanDonate = player.stats.PartnerCanSteal && !player.IsDead,
                        IsChalice = player.stats.isChalice,
                        IsMugman = GetCharacterName(player) == "Mugman",
                        IsKnown = true,
                        Tick = MultiplayerSession.Tick,
                    };
                    return true;
                }
            }

            status = default(ParticipantStatus);
            return false;
        }

        public static bool TryBuildLocalPacket(AbstractPlayerController player, out PlayerStatusPacket pkt)
        {
            pkt = default(PlayerStatusPacket);
            if (player == null || player.stats == null)
                return false;

            int health = Mathf.Clamp(player.stats.Health, 0, 255);
            int healthMax = Mathf.Clamp(player.stats.HealthMax, 0, 255);
            byte flags = 0;
            if (player.IsDead) flags |= 1;
            if (player.stats.PartnerCanSteal && !player.IsDead) flags |= 2;
            string characterName = GetCharacterName(player);
            if (player.stats.isChalice) flags |= 4;
            if (characterName == "Mugman") flags |= 8;

            pkt = new PlayerStatusPacket
            {
                ParticipantId = (byte)player.id,
                Health = (byte)health,
                HealthMax = (byte)Mathf.Max(healthMax, 1),
                Flags = flags,
                Tick = MultiplayerSession.Tick,
            };
            return true;
        }

        public static bool TryBuildLocalPacket(PlayerId playerId, out PlayerStatusPacket pkt)
        {
            return TryBuildLocalPacket(PlayerManager.GetPlayer(playerId), out pkt);
        }

        public static void CaptureLocal(AbstractPlayerController player)
        {
            PlayerStatusPacket pkt;
            if (!TryBuildLocalPacket(player, out pkt))
                return;

            Apply(pkt);
        }

        public static void CaptureLocal(PlayerId playerId)
        {
            CaptureLocal(PlayerManager.GetPlayer(playerId));
        }

        public static void PushLocalStatus(AbstractPlayerController player)
        {
            PlayerStatusPacket pkt;
            if (!TryBuildLocalPacket(player, out pkt))
                return;

            Apply(pkt);
            if (Plugin.Net != null && Plugin.Net.IsConnected && MultiplayerSession.IsActive)
                Plugin.Net.SendPlayerStatus(ref pkt);
        }

        public static bool TryGetPosition(byte participantId, out Vector2 position)
        {
            position = Vector2.zero;

            if (participantId <= (byte)PlayerId.PlayerTwo)
            {
                var player = PlayerManager.GetPlayer((PlayerId)participantId);
                if (player == null)
                    return false;

                position = player.center;
                return true;
            }

            return ExtraParticipantTracker.TryGetPosition(participantId, out position);
        }

        public static bool TryGetBestDonor(byte requesterParticipantId, Vector2 requesterPos, out byte donorParticipantId, out Vector2 donorPos)
        {
            donorParticipantId = byte.MaxValue;
            donorPos = requesterPos;

            float bestDistance = float.MaxValue;
            _sortedIds.Clear();
            _sortedIds.Add((byte)PlayerId.PlayerOne);
            _sortedIds.Add((byte)PlayerId.PlayerTwo);
            foreach (var entry in _statuses)
            {
                if (_sortedIds.Contains(entry.Key))
                    continue;
                _sortedIds.Add(entry.Key);
            }

            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;
                if (!status.IsKnown || status.IsDead || !status.CanDonate)
                    continue;
                if (status.ParticipantId == requesterParticipantId)
                    continue;

                Vector2 candidatePos;
                if (!TryGetPosition(status.ParticipantId, out candidatePos))
                    continue;

                float sqrDistance = (candidatePos - requesterPos).sqrMagnitude;
                if (sqrDistance >= bestDistance)
                    continue;

                donorParticipantId = status.ParticipantId;
                donorPos = candidatePos;
                bestDistance = sqrDistance;
            }

            return donorParticipantId != byte.MaxValue;
        }

        public static bool TryGetBestReviveTarget(byte donorParticipantId, Vector2 requestPos, out byte targetParticipantId, out Vector2 targetPos)
        {
            targetParticipantId = byte.MaxValue;
            targetPos = requestPos;

            float bestDistance = float.MaxValue;
            _sortedIds.Clear();
            _sortedIds.Add((byte)PlayerId.PlayerOne);
            _sortedIds.Add((byte)PlayerId.PlayerTwo);
            foreach (var entry in _statuses)
            {
                if (_sortedIds.Contains(entry.Key))
                    continue;
                _sortedIds.Add(entry.Key);
            }

            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;
                if (!status.IsKnown || !status.IsDead)
                    continue;
                if (status.ParticipantId == donorParticipantId)
                    continue;

                Vector2 candidatePos;
                if (!TryGetPosition(status.ParticipantId, out candidatePos))
                    continue;

                float sqrDistance = (candidatePos - requestPos).sqrMagnitude;
                if (sqrDistance >= bestDistance)
                    continue;

                bestDistance = sqrDistance;
                targetParticipantId = status.ParticipantId;
                targetPos = candidatePos;
            }

            return targetParticipantId != byte.MaxValue;
        }

        public static int AppendKnownParticipants(List<byte> target, bool includeBuiltIn = false)
        {
            if (target == null)
                return 0;

            int added = 0;
            if (includeBuiltIn)
            {
                target.Add((byte)PlayerId.PlayerOne);
                target.Add((byte)PlayerId.PlayerTwo);
                added += 2;
            }

            foreach (var entry in _statuses)
            {
                if (!includeBuiltIn && entry.Key <= (byte)PlayerId.PlayerTwo)
                    continue;

                if (target.Contains(entry.Key))
                    continue;

                target.Add(entry.Key);
                added++;
            }

            return added;
        }

        public static string BuildExtraSummary()
        {
            _sortedIds.Clear();
            foreach (var entry in _statuses)
            {
                if (entry.Key <= (byte)PlayerId.PlayerTwo)
                    continue;
                _sortedIds.Add(entry.Key);
            }

            if (_sortedIds.Count == 0)
                return string.Empty;

            _sortedIds.Sort();
            var sb = new StringBuilder();
            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;

                if (sb.Length > 0)
                    sb.Append(" | ");

                sb.Append("P");
                sb.Append(status.ParticipantId + 1);
                sb.Append(" ");
                if (status.IsDead)
                {
                    sb.Append("DOWN");
                }
                else
                {
                    sb.Append("HP");
                    sb.Append(status.Health);
                    sb.Append("/");
                    sb.Append(status.HealthMax);
                }
            }

            return sb.ToString();
        }

        static string GetCharacterName(AbstractPlayerController player)
        {
            if (player == null || player.stats == null)
                return "Cuphead";
            if (player.stats.isChalice)
                return "Ms. Chalice";

            bool playerOneIsMugman = PlayerManager.player1IsMugman;
            if (player.id == PlayerId.PlayerOne)
                return playerOneIsMugman ? "Mugman" : "Cuphead";
            if (player.id == PlayerId.PlayerTwo)
                return playerOneIsMugman ? "Cuphead" : "Mugman";
            return "Cuphead";
        }
    }
}
