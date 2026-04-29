using System;
using System.Diagnostics;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Provides a deterministic, seeded PRNG that replaces Cuphead's built-in
    /// Rand calls while a network session is active.
    ///
    /// Both host and client are seeded identically at scene load via SceneChangePacket,
    /// ensuring boss attack patterns, spawn timings, and any other RNG-dependent
    /// behaviour unfolds identically on both machines.
    ///
    /// The underlying algorithm is a 64-bit xorshift+ (extremely fast, no alloc).
    /// </summary>
    public static class RngSync
    {
        public enum RandomStream
        {
            Gameplay,
            Audio,
            Camera,
            Visual,
        }

        struct StreamState
        {
            public ulong S0;
            public ulong S1;
        }

        private static StreamState _gameplay;
        private static StreamState _audio;
        private static StreamState _camera;
        private static StreamState _visual;

        public static bool IsSeeded { get; private set; }
        public static uint CurrentSeed { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        //  Seeding
        // ──────────────────────────────────────────────────────────────────────

        public static void SetSeed(uint seed)
        {
            CurrentSeed = seed;
            _gameplay = CreateStream(seed, 0x00000000u);
            _audio = CreateStream(seed, 0xA0D10F1Fu);
            _camera = CreateStream(seed, 0xCA4E2A11u);
            _visual = CreateStream(seed, 0x51A1E55Eu);
            IsSeeded = true;
            Plugin.Log.LogInfo($"[RngSync] Seeded with {seed:X8}");
        }

        /// <summary>Generate a new random seed for use in the next SceneChangePacket.</summary>
        public static uint NextSeed()
        {
            var seed = (uint)(DateTime.UtcNow.Ticks ^ System.Diagnostics.Process.GetCurrentProcess().Id);
            CurrentSeed = seed;
            return seed;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Generation
        // ──────────────────────────────────────────────────────────────────────

        public static float NextFloat(float min, float max)
        {
            return NextFloat(min, max, RandomStream.Gameplay);
        }

        public static float NextFloat(float min, float max, RandomStream stream)
        {
            if (!IsSeeded) return UnityEngine.Random.Range(min, max);
            double t = (NextRaw(stream) >> 11) * (1.0 / (1ul << 53));
            return min + (float)(t * (max - min));
        }

        public static int NextInt(int min, int max)
        {
            return NextInt(min, max, RandomStream.Gameplay);
        }

        public static int NextInt(int min, int max, RandomStream stream)
        {
            if (!IsSeeded) return UnityEngine.Random.Range(min, max);
            if (min >= max) return min;
            return min + (int)(NextRaw(stream) % (uint)(max - min));
        }

        public static RandomStream ClassifyCaller()
        {
            try
            {
                var trace = new StackTrace(false);
                bool camera = false;
                bool audio = false;
                bool visual = false;

                for (int i = 2; i < trace.FrameCount; i++)
                {
                    var method = trace.GetFrame(i).GetMethod();
                    var type = method == null ? null : method.DeclaringType;
                    string name = type == null ? "" : type.FullName;
                    string methodName = method == null ? "" : method.Name;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (name.IndexOf("AudioManager", StringComparison.Ordinal) >= 0
                     || name.IndexOf("SoundGroup", StringComparison.Ordinal) >= 0)
                    {
                        audio = true;
                        continue;
                    }

                    if (name.IndexOf("CupheadGameCamera", StringComparison.Ordinal) >= 0
                     || name.IndexOf("CupheadLevelCamera", StringComparison.Ordinal) >= 0
                     || name.IndexOf("AbstractCupheadGameCamera", StringComparison.Ordinal) >= 0)
                    {
                        camera = true;
                        continue;
                    }

                    if (name.IndexOf("Effect", StringComparison.Ordinal) >= 0
                     || name.IndexOf("ChromaticAberrationFilmGrain", StringComparison.Ordinal) >= 0
                     || name.IndexOf("MapPlayerDust", StringComparison.Ordinal) >= 0
                     || name.IndexOf("LightRay", StringComparison.Ordinal) >= 0
                     || name.IndexOf("MathUtils", StringComparison.Ordinal) >= 0
                     || name.IndexOf("Particle", StringComparison.Ordinal) >= 0
                     || name.IndexOf("Dust", StringComparison.Ordinal) >= 0
                     || name.IndexOf("WeaponBoomerang", StringComparison.Ordinal) >= 0
                     || (name.IndexOf("AbstractProjectile", StringComparison.Ordinal) >= 0
                        && methodName.IndexOf("RandomizeVariant", StringComparison.Ordinal) >= 0))
                    {
                        visual = true;
                    }
                }

                if (audio)
                    return RandomStream.Audio;
                if (camera)
                    return RandomStream.Camera;
                if (visual)
                    return RandomStream.Visual;
            }
            catch
            {
            }

            return RandomStream.Gameplay;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  xorshift128+
        // ──────────────────────────────────────────────────────────────────────

        static StreamState CreateStream(uint seed, uint salt)
        {
            ulong mixed = (ulong)(seed ^ salt);
            var state = new StreamState();
            state.S0 = SplitMix64(mixed);
            state.S1 = SplitMix64(state.S0 ^ 0x9e3779b97f4a7c15UL);
            return state;
        }

        static ulong NextRaw(RandomStream stream)
        {
            switch (stream)
            {
                case RandomStream.Audio:
                    return NextRaw(ref _audio);
                case RandomStream.Camera:
                    return NextRaw(ref _camera);
                case RandomStream.Visual:
                    return NextRaw(ref _visual);
                default:
                    return NextRaw(ref _gameplay);
            }
        }

        static ulong NextRaw(ref StreamState state)
        {
            ulong s1 = state.S0;
            ulong s0 = state.S1;
            ulong result = s0 + s1;
            state.S0 = s0;
            s1 ^= s1 << 23;
            state.S1  = s1 ^ s0 ^ (s1 >> 17) ^ (s0 >> 26);
            return result;
        }

        static ulong SplitMix64(ulong x)
        {
            x += 0x9e3779b97f4a7c15UL;
            x  = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x  = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
    }
}
