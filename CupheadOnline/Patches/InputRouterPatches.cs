using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    internal static class UniversalInputRouter
    {
        // Rewired_Core.dll ships with the game and is referenced directly, so the
        // hot input queries below are plain calls. The MethodInfo fields remain
        // only as Harmony TargetMethod anchors for the patches further down.
        internal static readonly MethodInfo RewiredGetAxisMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetAxis", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButton", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonDownMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButtonDown", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonUpMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButtonUp", new[] { typeof(int) });

        static int _rawQueryDepth;
        static uint _nextInputTick = 1;

        static UniversalInputRouter()
        {
            MultiplayerSession.OnSessionStarted += ResetInputSequence;
            MultiplayerSession.OnSessionEnded += ResetInputSequence;
        }

        internal static bool IsRawQuery => _rawQueryDepth > 0;

        internal static Rewired.Player GetActions(PlayerId playerId)
        {
            // PlayerManager.GetPlayerInput is a static array lookup; the try/catch
            // covers the brief window before the game initializes that array.
            try { return PlayerManager.GetPlayerInput(playerId); }
            catch { return null; }
        }

        internal static bool TryResolvePlayerId(object rewiredPlayer, out PlayerId playerId)
        {
            if (rewiredPlayer != null)
            {
                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerOne)))
                {
                    playerId = PlayerId.PlayerOne;
                    return true;
                }

                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerTwo)))
                {
                    playerId = PlayerId.PlayerTwo;
                    return true;
                }
            }

            playerId = PlayerId.None;
            return false;
        }

        internal static bool TryGetRemoteButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            var button = (CupheadButton)actionId;
            var participantId = (byte)playerId;

            if (down)
                value = RemoteInputDriver.WasPressedThisFrame(participantId, button);
            else if (up)
                value = RemoteInputDriver.WasReleasedThisFrame(participantId, button);
            else
                value = RemoteInputDriver.IsPressed(participantId, button);

            return true;
        }

        internal static bool TryGetRemoteAxis(PlayerId playerId, int actionId, out float value)
        {
            value = 0f;
            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(playerId, out input))
                return true;

            value = actionId == 0 ? input.AxisX : actionId == 1 ? input.AxisY : 0f;
            return true;
        }

        internal static bool TryGetLocalButton(int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            bool one = RawButton(GetActions(PlayerId.PlayerOne), actionId, down, up);
            bool two = RawButton(GetActions(PlayerId.PlayerTwo), actionId, down, up);
            value = one || two;
            return true;
        }

        internal static bool TryGetLocalAxis(int actionId, out float value)
        {
            float one = RawAxis(GetActions(PlayerId.PlayerOne), actionId);
            float two = RawAxis(GetActions(PlayerId.PlayerTwo), actionId);
            value = Mathf.Abs(two) > Mathf.Abs(one) ? two : one;
            return true;
        }

        internal static InputFramePacket BuildLocalInputFrame()
        {
            return BuildLocalInputFrameForPlayer(PlayerId.None);
        }

        internal static InputFramePacket BuildLocalInputFrameForPlayer(PlayerId playerId)
        {
            uint buttons = 0;
            Pack(playerId, CupheadButton.Jump, ref buttons);
            Pack(playerId, CupheadButton.Shoot, ref buttons);
            Pack(playerId, CupheadButton.Super, ref buttons);
            Pack(playerId, CupheadButton.SwitchWeapon, ref buttons);
            Pack(playerId, CupheadButton.Lock, ref buttons);
            Pack(playerId, CupheadButton.Dash, ref buttons);
            Pack(playerId, CupheadButton.Pause, ref buttons);
            Pack(playerId, CupheadButton.NextPage, ref buttons);
            Pack(playerId, CupheadButton.PreviousPage, ref buttons);
            Pack(playerId, CupheadButton.Accept, ref buttons);
            Pack(playerId, CupheadButton.Cancel, ref buttons);
            Pack(playerId, CupheadButton.EquipMenu, ref buttons);
            Pack(playerId, CupheadButton.MenuUp, ref buttons);
            Pack(playerId, CupheadButton.MenuLeft, ref buttons);
            Pack(playerId, CupheadButton.MenuDown, ref buttons);
            Pack(playerId, CupheadButton.MenuRight, ref buttons);

            float axisX;
            float axisY;
            TryGetLocalAxis(playerId, 0, out axisX);
            TryGetLocalAxis(playerId, 1, out axisY);

            return new InputFramePacket
            {
                AxisX = axisX,
                AxisY = axisY,
                Buttons = buttons,
                Tick = NextInputTick(),
                InputTime = HighLatencyInputSync.PacketTimeNow(),
            };
        }

        static uint NextInputTick()
        {
            unchecked
            {
                uint next = _nextInputTick++;
                if (next == 0)
                {
                    _nextInputTick = 1;
                    next = _nextInputTick++;
                }
                return next;
            }
        }

        static void ResetInputSequence()
        {
            _nextInputTick = 1;
        }

        static void Pack(PlayerId playerId, CupheadButton button, ref uint buttons)
        {
            bool pressed;
            if (TryGetLocalButton(playerId, (int)button, false, false, out pressed) && pressed)
                buttons |= 1u << (int)button;
        }

        internal static bool TryGetLocalButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            if (LanSteamE2ETest.TryGetLocalButton(playerId, actionId, down, up, out value))
                return true;

            if (playerId == PlayerId.None)
                return TryGetLocalButton(actionId, down, up, out value);

            value = false;
            if (!IsValidButton(actionId))
                return true;

            value = RawButton(GetActions(playerId), actionId, down, up);
            return true;
        }

        internal static bool TryGetLocalAxis(PlayerId playerId, int actionId, out float value)
        {
            if (LanSteamE2ETest.TryGetLocalAxis(playerId, actionId, out value))
                return true;

            if (playerId == PlayerId.None)
                return TryGetLocalAxis(actionId, out value);

            value = RawAxis(GetActions(playerId), actionId);
            return true;
        }

        static bool RawButton(Rewired.Player player, int actionId, bool down, bool up)
        {
            if (player == null)
                return false;

            // _rawQueryDepth keeps the direct Rewired calls below from re-entering
            // our own GetButton/GetAxis postfix patches.
            _rawQueryDepth++;
            try
            {
                if (down)
                    return player.GetButtonDown(actionId);
                if (up)
                    return player.GetButtonUp(actionId);
                return player.GetButton(actionId);
            }
            catch
            {
                return false;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static float RawAxis(Rewired.Player player, int actionId)
        {
            if (player == null)
                return 0f;

            _rawQueryDepth++;
            try
            {
                return player.GetAxis(actionId);
            }
            catch
            {
                return 0f;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static bool IsValidButton(int actionId)
        {
            return actionId >= 0 && actionId < 32;
        }
    }

    internal static class ClientInputFramePump
    {
        const int HighLatencyInputHistoryFrames = 5;
        static readonly Queue<InputFramePacket> _recentHighLatencyInputs =
            new Queue<InputFramePacket>(HighLatencyInputHistoryFrames);

        static ClientInputFramePump()
        {
            MultiplayerSession.OnSessionStarted += ResetHistory;
            MultiplayerSession.OnSessionEnded += ResetHistory;
        }

        internal static void Update()
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            // Outgoing frames must use the same merged universal routing as the
            // local prediction path (RouteButton). Sampling only the LocalId slot
            // silently drops devices Rewired assigned to the other vanilla slot —
            // on guests that is the auto-assigned first controller, which lands on
            // Player One's slot because the mod blocks the Player Two join flow.
            // The dev simulator is the exception: it deliberately splits devices
            // per slot, so it keeps the per-player sample.
            var packet = LocalDevSession.IsActive
                ? UniversalInputRouter.BuildLocalInputFrameForPlayer(MultiplayerSession.LocalId)
                : UniversalInputRouter.BuildLocalInputFrame();
            HighLatencyInputSync.RecordLocalFrame(MultiplayerSession.LocalId, packet);
            ResendRecentHighLatencyInputs();
            RememberHighLatencyInput(packet);
            Plugin.Net.SendInputFrame(ref packet);
        }

        static void ResendRecentHighLatencyInputs()
        {
            if (!HighLatencyInputSync.ShouldDelayNetworkGameplayInput((byte)MultiplayerSession.LocalId))
                return;
            if (_recentHighLatencyInputs.Count == 0)
                return;

            var frames = _recentHighLatencyInputs.ToArray();
            for (int i = 0; i < frames.Length; i++)
            {
                var resend = frames[i];
                Plugin.Net.SendInputFrame(ref resend);
            }
        }

        static void RememberHighLatencyInput(InputFramePacket packet)
        {
            if (!HighLatencyInputSync.ShouldDelayNetworkGameplayInput((byte)MultiplayerSession.LocalId))
            {
                _recentHighLatencyInputs.Clear();
                return;
            }

            _recentHighLatencyInputs.Enqueue(packet);
            while (_recentHighLatencyInputs.Count > HighLatencyInputHistoryFrames)
                _recentHighLatencyInputs.Dequeue();
        }

        static void ResetHistory()
        {
            _recentHighLatencyInputs.Clear();
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetAxisPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetAxisMethod;
        }

        static void Postfix(object __instance, int actionId, ref float __result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(__instance, out owner))
                return;

            float value;
            if (LocalDevE2ETest.TryGetLocalAxis(owner, actionId, out value))
            {
                __result = value;
                return;
            }

            if (HighLatencyInputSync.TryGetDelayedAxis(owner, actionId, out value))
            {
                __result = value;
                return;
            }

            if (LanSteamE2ETest.TryGetLocalAxis(owner, actionId, out value))
            {
                __result = value;
                return;
            }

            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteAxis(owner, actionId, out value))
                    __result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalAxis(owner, actionId, out value)
                    : UniversalInputRouter.TryGetLocalAxis(actionId, out value)))
            {
                __result = value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RouteButton(__instance, actionId, false, false, ref __result);
        }

        internal static void RouteButton(object rewiredPlayer, int actionId, bool down, bool up, ref bool result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(rewiredPlayer, out owner))
                return;

            bool value;
            if (LocalDevE2ETest.TryGetLocalButton(owner, actionId, down, up, out value))
            {
                result = value;
                return;
            }

            if (HighLatencyInputSync.TryGetDelayedButton(owner, actionId, down, up, out value))
            {
                result = value;
                return;
            }

            if (LanSteamE2ETest.TryGetLocalButton(owner, actionId, down, up, out value))
            {
                result = value;
                return;
            }

            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteButton(owner, actionId, down, up, out value))
                    result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalButton(owner, actionId, down, up, out value)
                    : UniversalInputRouter.TryGetLocalButton(actionId, down, up, out value)))
            {
                result = result || value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonDownPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonDownMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, true, false, ref __result);
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonUpPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonUpMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, false, true, ref __result);
        }
    }

    [HarmonyPatch(typeof(CupheadInput), nameof(CupheadInput.InputDisplayForButton))]
    public static class CupheadInputDisplayForButtonPatch
    {
        static bool _insideFallback;

        static void Postfix(CupheadButton button, int rewiredPlayerId, ref Localization.Translation __result)
        {
            if (_insideFallback || HasText(__result))
                return;

            if (rewiredPlayerId != 0)
            {
                _insideFallback = true;
                try
                {
                    __result = CupheadInput.InputDisplayForButton(button, 0);
                }
                catch
                {
                }
                finally
                {
                    _insideFallback = false;
                }

                if (HasText(__result))
                    return;
            }

            __result = new Localization.Translation { text = FallbackText(button) };
        }

        static bool HasText(Localization.Translation translation)
        {
            return !string.IsNullOrEmpty(translation.text);
        }

        static string FallbackText(CupheadButton button)
        {
            switch (button)
            {
                case CupheadButton.Accept: return "Z";
                case CupheadButton.Cancel: return "ESC";
                case CupheadButton.EquipMenu: return "SHIFT";
                case CupheadButton.Pause: return "ESC";
                case CupheadButton.MenuUp: return "UP";
                case CupheadButton.MenuDown: return "DOWN";
                case CupheadButton.MenuLeft: return "LEFT";
                case CupheadButton.MenuRight: return "RIGHT";
                case CupheadButton.Jump: return "Z";
                case CupheadButton.Dash: return "SHIFT";
                case CupheadButton.Shoot: return "X";
                case CupheadButton.Super: return "V";
                default: return "Z";
            }
        }
    }
}
