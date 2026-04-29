using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Stores the most recently received gameplay input for network-controlled
    /// slots. Today that is still one live remote slot, but the storage is keyed
    /// by PlayerId so the rest of the code does not have to stay single-remote.
    /// </summary>
    public static class RemoteInputDriver
    {
        sealed class RemoteInputState
        {
            public readonly List<DelayedInputFrame> Delayed = new List<DelayedInputFrame>(MaxDelayedFrames);
            public InputFramePacket Current;
            public InputFramePacket Previous;
            public bool HasData;
            public bool HasReceivedTick;
            public uint LastReceivedTick;
            public bool HasPromotedTick;
            public uint LastPromotedTick;
            public int StallFrames;
            public uint DownEdges;
            public uint UpEdges;
            public readonly int[] DownServedFrames = CreateServedFrameBuffer();
            public readonly int[] UpServedFrames = CreateServedFrameBuffer();
        }

        struct DelayedInputFrame
        {
            public InputFramePacket Packet;
            public float DueAt;
        }

        const int MinStallFrames = 18; // ~300 ms at 60 Hz before inputs are released on low-latency links.
        const int MaxStallFrames = 90; // Keep stale controls bounded even on poor Steam relay routes.
        const int MaxDelayedFrames = 2048;

        static readonly Dictionary<byte, RemoteInputState> _states =
            new Dictionary<byte, RemoteInputState>(2);

        static RemoteInputDriver()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(InputFramePacket pkt)
        {
            Apply(MultiplayerSession.GetPrimaryRemoteGameplayId(), pkt);
        }

        public static void Apply(byte participantId, InputFramePacket pkt)
        {
            if (HighLatencyInputSync.ShouldDropUntimedNetworkGameplayInput(participantId, pkt))
                return;

            var state = GetOrCreateState(participantId);

            if (ShouldDelayRemoteInput(participantId, pkt))
            {
                ApplyDelayed(state, pkt);
                return;
            }

            if (state.HasReceivedTick)
            {
                if (pkt.Tick == state.LastReceivedTick)
                    return;

                if (!NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
                    return;
            }

            state.HasReceivedTick = true;
            state.LastReceivedTick = pkt.Tick;
            state.StallFrames = 0;

            PromoteCurrent(state, pkt, false);
        }

        public static void Apply(PlayerId playerId, InputFramePacket pkt)
        {
            Apply((byte)playerId, pkt);
        }

        public static bool HasDataFor(PlayerId playerId)
        {
            RemoteInputState state;
            return _states.TryGetValue((byte)playerId, out state) && state.HasData;
        }

        public static bool TryGetCurrent(PlayerId playerId, out InputFramePacket pkt)
        {
            return TryGetCurrent((byte)playerId, out pkt);
        }

        public static bool TryGetCurrent(byte participantId, out InputFramePacket pkt)
        {
            RemoteInputState state;
            if (_states.TryGetValue(participantId, out state))
            {
                AdvanceDelayed(state);
                if (state.HasData)
                {
                    pkt = state.Current;
                    return true;
                }
            }

            pkt = default(InputFramePacket);
            return false;
        }

        public static bool WasPressedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
                return false;

            AdvanceDelayed(state);
            if (!state.HasData)
                return false;

            return ConsumeEdgeForFrame(state.DownEdges, state.DownServedFrames, button);
        }

        public static bool PeekPressedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
                return false;

            AdvanceDelayed(state);
            if (!state.HasData)
                return false;

            int buttonIndex = (int)button;
            if (buttonIndex < 0 || buttonIndex >= 32)
                return false;

            return (state.DownEdges & (1u << buttonIndex)) != 0u;
        }

        public static bool WasReleasedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
                return false;

            AdvanceDelayed(state);
            if (!state.HasData)
                return false;

            return ConsumeEdgeForFrame(state.UpEdges, state.UpServedFrames, button);
        }

        public static bool IsPressed(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
                return false;

            AdvanceDelayed(state);
            return state.HasData && state.Current.IsPressed(button);
        }

        /// <summary>Called each FixedUpdate by PlayerMotorPatch to age the input.</summary>
        public static void Tick(PlayerId playerId)
        {
            Tick((byte)playerId);
        }

        public static void Tick(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
                return;

            AdvanceDelayed(state);
            if (state.Delayed.Count > 0)
                return;
            if (!state.HasData)
                return;

            state.StallFrames++;
            if (state.StallFrames > ComputeMaxStallFrames())
            {
                // Starvation: zero all inputs so the proxy player stops moving.
                state.Previous = state.Current;
                state.Current = default(InputFramePacket);
                state.HasData = false;
                state.DownEdges = 0u;
                state.UpEdges = 0u;
                state.Delayed.Clear();
                ResetServedFrames(state.DownServedFrames);
                ResetServedFrames(state.UpServedFrames);
            }
        }

        public static void Reset()
        {
            _states.Clear();
        }

        public static void Reset(PlayerId playerId)
        {
            _states.Remove((byte)playerId);
        }

        public static void Reset(byte participantId)
        {
            _states.Remove(participantId);
        }

        static RemoteInputState GetOrCreateState(PlayerId playerId)
        {
            return GetOrCreateState((byte)playerId);
        }

        static RemoteInputState GetOrCreateState(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
            {
                state = new RemoteInputState();
                _states[participantId] = state;
            }
            return state;
        }

        static bool ShouldDelayRemoteInput(byte participantId, InputFramePacket pkt)
        {
            if (pkt.InputTime < 0f)
                return false;
            if (!HighLatencyInputSync.ShouldDelayNetworkGameplayInput(participantId))
                return false;

            return true;
        }

        static void ApplyDelayed(RemoteInputState state, InputFramePacket pkt)
        {
            if (state.HasPromotedTick)
            {
                if (pkt.Tick == state.LastPromotedTick)
                    return;

                if (!NetTick.IsNewer(pkt.Tick, state.LastPromotedTick))
                    return;
            }

            if (ContainsDelayedTick(state, pkt.Tick))
                return;

            if (!state.HasReceivedTick || NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
            {
                state.HasReceivedTick = true;
                state.LastReceivedTick = pkt.Tick;
            }
            state.StallFrames = 0;

            InsertDelayedSorted(state, new DelayedInputFrame
            {
                Packet = pkt,
                DueAt = HighLatencyInputSync.GetDueTime(pkt),
            });

            while (state.Delayed.Count > MaxDelayedFrames)
                state.Delayed.RemoveAt(0);

            AdvanceDelayed(state);
        }

        static void AdvanceDelayed(RemoteInputState state)
        {
            if (state == null || state.Delayed.Count == 0)
                return;

            float now = HighLatencyInputSync.PlayoutTimeNow();
            bool promotedAny = false;
            while (state.Delayed.Count > 0)
            {
                var next = state.Delayed[0];
                if (next.DueAt > now)
                    break;

                if (!promotedAny)
                {
                    state.DownEdges = 0u;
                    state.UpEdges = 0u;
                    ResetServedFrames(state.DownServedFrames);
                    ResetServedFrames(state.UpServedFrames);
                }

                state.Delayed.RemoveAt(0);
                if (state.HasPromotedTick
                 && (next.Packet.Tick == state.LastPromotedTick
                    || !NetTick.IsNewer(next.Packet.Tick, state.LastPromotedTick)))
                {
                    continue;
                }

                PromoteCurrent(state, next.Packet, true);
                promotedAny = true;
            }
        }

        static void PromoteCurrent(RemoteInputState state, InputFramePacket pkt, bool accumulateEdges)
        {
            var previous = state.HasData ? state.Current : default(InputFramePacket);
            state.Previous = previous;
            state.Current = pkt;
            state.HasData = true;
            state.StallFrames = 0;
            state.HasPromotedTick = true;
            state.LastPromotedTick = pkt.Tick;

            if (accumulateEdges)
            {
                state.DownEdges |= state.Current.Buttons & ~previous.Buttons;
                state.UpEdges |= previous.Buttons & ~state.Current.Buttons;
                return;
            }

            RecomputeEdges(state);
        }

        static int ComputeMaxStallFrames()
        {
            int latencyMs = 0;
            try
            {
                if (Plugin.Net != null)
                    latencyMs = Plugin.Net.Latency;
            }
            catch
            {
                latencyMs = 0;
            }

            float fixedStep = Time.fixedDeltaTime > 0.001f ? Time.fixedDeltaTime : (1f / 60f);
            float toleratedSeconds = Mathf.Max(0.30f, 0.30f + (Mathf.Max(0, latencyMs) / 1000f) * 1.25f);
            int frames = Mathf.CeilToInt(toleratedSeconds / fixedStep);
            return Mathf.Clamp(frames, MinStallFrames, MaxStallFrames);
        }

        static void RecomputeEdges(RemoteInputState state)
        {
            state.DownEdges = state.Current.Buttons & ~state.Previous.Buttons;
            state.UpEdges = state.Previous.Buttons & ~state.Current.Buttons;
            ResetServedFrames(state.DownServedFrames);
            ResetServedFrames(state.UpServedFrames);
        }

        static bool ContainsDelayedTick(RemoteInputState state, uint tick)
        {
            for (int i = 0; i < state.Delayed.Count; i++)
            {
                if (state.Delayed[i].Packet.Tick == tick)
                    return true;
            }

            return false;
        }

        static void InsertDelayedSorted(RemoteInputState state, DelayedInputFrame frame)
        {
            int index = state.Delayed.Count;
            while (index > 0)
            {
                var existing = state.Delayed[index - 1];
                if (existing.DueAt < frame.DueAt)
                    break;
                if (Mathf.Abs(existing.DueAt - frame.DueAt) <= 0.0001f
                 && !NetTick.IsNewer(existing.Packet.Tick, frame.Packet.Tick))
                    break;
                index--;
            }

            state.Delayed.Insert(index, frame);
        }

        static bool ConsumeEdgeForFrame(uint edges, int[] servedFrames, CupheadButton button)
        {
            int buttonIndex = (int)button;
            if (buttonIndex < 0 || buttonIndex >= 32)
                return false;

            uint mask = 1u << buttonIndex;
            if ((edges & mask) == 0u)
                return false;

            int frame = Time.frameCount;
            if (servedFrames[buttonIndex] == frame)
                return true;
            if (servedFrames[buttonIndex] >= 0)
                return false;

            servedFrames[buttonIndex] = frame;
            return true;
        }

        static int[] CreateServedFrameBuffer()
        {
            var frames = new int[32];
            ResetServedFrames(frames);
            return frames;
        }

        static void ResetServedFrames(int[] frames)
        {
            if (frames == null)
                return;

            for (int i = 0; i < frames.Length; i++)
                frames[i] = -1;
        }
    }
}
