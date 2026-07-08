using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    public static class PlayerColorSync
    {
        sealed class PaletteEntry
        {
            public string Name;
            public Color Color;
        }

        sealed class RendererTintState
        {
            public SpriteRenderer Renderer;
            public Material OriginalMaterial;
            public Material TintMaterial;
            public Color OriginalRendererColor;
        }

        sealed class ControllerTintState
        {
            public AbstractPlayerController Controller;
            public readonly List<RendererTintState> Renderers = new List<RendererTintState>(16);
            public int AppliedSelection = -1;
        }

        static readonly PaletteEntry[] Palette =
        {
            new PaletteEntry { Name = "Auto",    Color = new Color(0.70f, 0.72f, 0.76f, 1f) },
            new PaletteEntry { Name = "Classic", Color = Color.white },
            new PaletteEntry { Name = "Teal",    Color = new Color(0.44f, 0.93f, 0.98f, 1f) },
            new PaletteEntry { Name = "Coral",   Color = new Color(1.00f, 0.54f, 0.45f, 1f) },
            new PaletteEntry { Name = "Amber",   Color = new Color(1.00f, 0.78f, 0.34f, 1f) },
            new PaletteEntry { Name = "Mint",    Color = new Color(0.52f, 0.94f, 0.72f, 1f) },
            new PaletteEntry { Name = "Violet",  Color = new Color(0.82f, 0.62f, 1.00f, 1f) },
            new PaletteEntry { Name = "Lime",    Color = new Color(0.74f, 0.96f, 0.36f, 1f) },
        };

        static readonly Dictionary<int, ControllerTintState> ControllerStates =
            new Dictionary<int, ControllerTintState>(8);
        static readonly HashSet<int> SeenControllers =
            new HashSet<int>();
        static readonly List<int> StaleControllers =
            new List<int>(8);
        static readonly List<AbstractPlayerController> ScratchControllers =
            new List<AbstractPlayerController>(8);

        static readonly FieldInfo DeathEffectPlayerIdField =
            typeof(PlayerDeathEffect).GetField(
                "playerId",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        const string ColorProperty = "_Color";
        const string SwatchGlyph = "\u25A0";

        static PlayerColorSync()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static int SelectionCount => Palette.Length;
        public static int AutoSelection => 0;
        public static int ClassicSelection => 1;

        public static void Update()
        {
            if (!MultiplayerSession.IsActive)
            {
                Reset();
                return;
            }

            SeenControllers.Clear();

            TrackBuiltIn(PlayerId.PlayerOne);
            TrackBuiltIn(PlayerId.PlayerTwo);

            ScratchControllers.Clear();
            ExtraRemoteAvatarManager.AppendTargetableControllers(ScratchControllers);
            for (int i = 0; i < ScratchControllers.Count; i++)
            {
                byte participantId;
                if (!ExtraRemoteAvatarManager.TryGetParticipantId(ScratchControllers[i], out participantId))
                    continue;

                TrackController(ScratchControllers[i], participantId);
            }

            StaleControllers.Clear();
            foreach (var key in ControllerStates.Keys)
            {
                if (!SeenControllers.Contains(key))
                    StaleControllers.Add(key);
            }

            for (int i = 0; i < StaleControllers.Count; i++)
                RemoveControllerState(StaleControllers[i]);
        }

        public static void Reset()
        {
            if (ControllerStates.Count == 0)
                return;

            StaleControllers.Clear();
            foreach (var key in ControllerStates.Keys)
                StaleControllers.Add(key);

            for (int i = 0; i < StaleControllers.Count; i++)
                RemoveControllerState(StaleControllers[i]);

            ControllerStates.Clear();
            SeenControllers.Clear();
            ScratchControllers.Clear();
            StaleControllers.Clear();
        }

        public static int NormalizeSelection(int selection)
        {
            if (selection < 0)
                return AutoSelection;
            if (selection >= Palette.Length)
                return Palette.Length - 1;
            return selection;
        }

        public static int GetNextSelection(int currentSelection, int direction)
        {
            int normalized = NormalizeSelection(currentSelection);
            if (Palette.Length <= 1)
                return normalized;

            int next = normalized + (direction < 0 ? -1 : 1);
            if (next < 0)
                next = Palette.Length - 1;
            else if (next >= Palette.Length)
                next = 0;

            return next;
        }

        public static int ResolveSelection(byte participantId, int preferredSelection)
        {
            int normalized = NormalizeSelection(preferredSelection);
            if (normalized != AutoSelection)
                return normalized;

            if (participantId <= (byte)PlayerId.PlayerTwo)
                return ClassicSelection;

            int extraColorCount = Palette.Length - 2;
            if (extraColorCount <= 0)
                return ClassicSelection;

            int offset = Mathf.Max(0, participantId - ((byte)PlayerId.PlayerTwo + 1));
            return 2 + (offset % extraColorCount);
        }

        public static string BuildLocalMenuLabel()
        {
            return Loc.T("PLAYER COLOR");
        }

        public static string BuildColorLineLabel(string prefix, int selection, string suffix)
        {
            return GetSwatchRichText(selection, null) + " " + prefix + ": " + suffix;
        }

        public static string GetSelectionName(int selection)
        {
            int normalized = NormalizeSelection(selection);
            return Loc.T(Palette[normalized].Name);
        }

        public static Color GetPaletteColor(int selection)
        {
            return Palette[NormalizeSelection(selection)].Color;
        }

        public static string GetSelectionDescription(int selection)
        {
            switch (NormalizeSelection(selection))
            {
                case 0:
                    return Loc.T("Classic for the first two gameplay slots, stable unique colors for extra players.");
                case 1:
                    return Loc.T("Keep the vanilla look with no runtime tint.");
                case 2:
                    return Loc.T("Bright teal tint for clean readability.");
                case 3:
                    return Loc.T("Warm coral tint that stands out in motion.");
                case 4:
                    return Loc.T("Golden amber tint with a classic arcade feel.");
                case 5:
                    return Loc.T("Soft mint tint for a lighter look.");
                case 6:
                    return Loc.T("Violet tint for high contrast in crowded runs.");
                case 7:
                    return Loc.T("Lime tint for the strongest map-side visibility.");
                default:
                    return Loc.T("Runtime tint applied without editing sprite files.");
            }
        }

        public static Color GetResolvedColor(byte participantId, int preferredSelection)
        {
            return Palette[ResolveSelection(participantId, preferredSelection)].Color;
        }

        public static string GetSwatchRichText(int selection, byte? participantId)
        {
            int resolved = participantId.HasValue
                ? ResolveSelection(participantId.Value, selection)
                : NormalizeSelection(selection);

            return "<color=#" + ColorUtility.ToHtmlStringRGB(Palette[resolved].Color) + ">" + SwatchGlyph + "</color>";
        }

        public static string GetParticipantLabel(byte participantId)
        {
            return "P" + (participantId + 1);
        }

        public static void ApplyDeathEffectTint(PlayerDeathEffect effect)
        {
            if (effect == null)
                return;

            byte participantId;
            var extraTag = effect.GetComponent<ExtraParticipantDeathBubbleTag>();
            if (extraTag != null)
            {
                participantId = extraTag.ParticipantId;
            }
            else if (!TryGetDeathEffectParticipantId(effect, out participantId))
            {
                return;
            }

            int preferredSelection = GetPreferredSelection(participantId);
            int resolvedSelection = ResolveSelection(participantId, preferredSelection);
            if (resolvedSelection == ClassicSelection)
                return;

            var renderers = effect.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                ApplyEphemeralTint(renderers[i], Palette[resolvedSelection].Color);
        }

        static void TrackBuiltIn(PlayerId playerId)
        {
            AbstractPlayerController player = null;
            try
            {
                player = PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return;
            }

            if (player == null)
                return;

            TrackController(player, (byte)playerId);
        }

        static void TrackController(AbstractPlayerController controller, byte participantId)
        {
            if (controller == null)
                return;

            int key = controller.GetInstanceID();
            SeenControllers.Add(key);

            ControllerTintState state;
            if (!ControllerStates.TryGetValue(key, out state) || state == null || state.Controller == null)
            {
                state = CaptureControllerState(controller);
                if (state == null)
                    return;
                ControllerStates[key] = state;
            }

            int preferredSelection = GetPreferredSelection(participantId);
            int resolvedSelection = ResolveSelection(participantId, preferredSelection);
            if (state.AppliedSelection == resolvedSelection)
                return;

            ApplyControllerTint(state, resolvedSelection);
        }

        static ControllerTintState CaptureControllerState(AbstractPlayerController controller)
        {
            if (controller == null)
                return null;

            var state = new ControllerTintState
            {
                Controller = controller,
            };

            var renderers = controller.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (!ShouldTintRenderer(renderer))
                    continue;

                state.Renderers.Add(new RendererTintState
                {
                    Renderer = renderer,
                    OriginalMaterial = renderer.sharedMaterial,
                    OriginalRendererColor = renderer.color,
                });
            }

            return state;
        }

        static bool ShouldTintRenderer(SpriteRenderer renderer)
        {
            if (renderer == null)
                return false;

            string name = renderer.name ?? string.Empty;
            string lower = name.ToLowerInvariant();
            if (lower.Contains("shadow")
             || lower.Contains("smoke")
             || lower.Contains("dust")
             || lower.Contains("trail")
             || lower.Contains("flash")
             || lower.Contains("spark")
             || lower.Contains("muzzle")
             || lower.Contains("fx")
             || lower.Contains("effect")
             || lower.Contains("projectile")
             || lower.Contains("bullet")
             || lower.Contains("beam"))
            {
                return false;
            }

            return true;
        }

        static int GetPreferredSelection(byte participantId)
        {
            if (MultiplayerSession.IsActive && participantId == (byte)MultiplayerSession.LocalId)
                return NormalizeSelection(Plugin.PreferredPlayerColorSelection);

            if (Plugin.Net != null)
                return Plugin.Net.GetParticipantColorSelection(participantId);

            return AutoSelection;
        }

        static void ApplyControllerTint(ControllerTintState state, int resolvedSelection)
        {
            if (state == null)
                return;

            bool shouldTint = resolvedSelection != ClassicSelection;
            Color tintColor = Palette[resolvedSelection].Color;

            for (int i = 0; i < state.Renderers.Count; i++)
            {
                var rendererState = state.Renderers[i];
                if (rendererState == null || rendererState.Renderer == null)
                    continue;

                if (!shouldTint)
                {
                    RestoreRenderer(rendererState);
                    continue;
                }

                if (EnsureTintMaterial(rendererState, tintColor))
                {
                    if (rendererState.Renderer.sharedMaterial != rendererState.TintMaterial)
                        rendererState.Renderer.sharedMaterial = rendererState.TintMaterial;
                    rendererState.Renderer.color = rendererState.OriginalRendererColor;
                }
                else
                {
                    rendererState.Renderer.color = Multiply(rendererState.OriginalRendererColor, tintColor);
                }
            }

            state.AppliedSelection = resolvedSelection;
        }

        static bool EnsureTintMaterial(RendererTintState state, Color tintColor)
        {
            if (state == null || state.Renderer == null || state.OriginalMaterial == null)
                return false;

            if (state.TintMaterial == null)
            {
                state.TintMaterial = new Material(state.OriginalMaterial);
                state.TintMaterial.name = state.OriginalMaterial.name + "_CupheadOnlineTint";
            }

            if (!state.TintMaterial.HasProperty(ColorProperty))
                return false;

            Color baseColor = Color.white;
            if (state.OriginalMaterial.HasProperty(ColorProperty))
                baseColor = state.OriginalMaterial.color;

            state.TintMaterial.color = Multiply(baseColor, tintColor);
            return true;
        }

        static void RestoreRenderer(RendererTintState state)
        {
            if (state == null || state.Renderer == null)
                return;

            state.Renderer.sharedMaterial = state.OriginalMaterial;
            state.Renderer.color = state.OriginalRendererColor;
        }

        static void RemoveControllerState(int key)
        {
            ControllerTintState state;
            if (!ControllerStates.TryGetValue(key, out state))
                return;

            if (state != null)
            {
                for (int i = 0; i < state.Renderers.Count; i++)
                {
                    var rendererState = state.Renderers[i];
                    if (rendererState == null)
                        continue;

                    if (rendererState.Renderer != null)
                        RestoreRenderer(rendererState);

                    if (rendererState.TintMaterial != null)
                        UnityEngine.Object.Destroy(rendererState.TintMaterial);
                }
            }

            ControllerStates.Remove(key);
        }

        static void ApplyEphemeralTint(SpriteRenderer renderer, Color tintColor)
        {
            if (renderer == null)
                return;

            var source = renderer.sharedMaterial;
            if (source != null && source.HasProperty(ColorProperty))
            {
                var clone = new Material(source);
                clone.name = source.name + "_CupheadOnlineBubbleTint";
                clone.color = Multiply(source.color, tintColor);
                renderer.sharedMaterial = clone;
                return;
            }

            renderer.color = Multiply(renderer.color, tintColor);
        }

        static bool TryGetDeathEffectParticipantId(PlayerDeathEffect effect, out byte participantId)
        {
            participantId = byte.MaxValue;
            if (effect == null || DeathEffectPlayerIdField == null)
                return false;

            object raw = DeathEffectPlayerIdField.GetValue(effect);
            if (!(raw is PlayerId))
                return false;

            participantId = (byte)(PlayerId)raw;
            return true;
        }

        static Color Multiply(Color a, Color b)
        {
            return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a);
        }
    }
}
