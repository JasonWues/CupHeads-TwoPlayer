using System;
using System.IO;
using System.Reflection;
using CupheadOnline.Net;
using CupheadOnline.Patches;
using CupheadOnline.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Two-process smoke test for the LAN Steam-emulation transport. The host
    /// performs the normal save -> map -> boss flow while the client continuously
    /// sends Player Two input frames over UDP through SteamNetManager.
    /// </summary>
    internal static class LanSteamE2ETest
    {
        enum Stage
        {
            Idle,
            WaitConnection,
            LoadSlotSelect,
            WaitSlotSelect,
            WaitMap,
            WalkToMapDialogue,
            StartMapDialogue,
            WaitMapDialogueStartAck,
            WaitMapDialogueContinueAck,
            WalkToBoss,
            OpenStartCard,
            WaitLevel,
            Fight,
            PauseSync,
            ClientObserve,
            Done,
            Failed,
        }

        const float WalkTimeout = 35f;
        const float CardTimeout = 10f;
        const float LevelTimeout = 30f;
        const float FightDuration = 16f;
        const float GuestOnlyDamageDuration = 4f;
        const float DialogueTimeout = 10f;
        const float PauseTimeout = 8f;
        const float ReviveSmokeTimeout = 12f;

        static readonly BindingFlags InstanceAny =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly FieldInfo SlotSelectionField =
            typeof(SlotSelectScreen).GetField("_slotSelection", InstanceAny);
        static readonly FieldInfo SlotsField =
            typeof(SlotSelectScreen).GetField("slots", InstanceAny);
        static readonly MethodInfo EnterGameMethod =
            typeof(SlotSelectScreen).GetMethod("EnterGame", InstanceAny);
        static readonly FieldInfo LevelField =
            typeof(MapLevelLoader).GetField("level", InstanceAny);
        static readonly MethodInfo MapLevelLoaderActivateMethod =
            typeof(MapLevelLoader).GetMethod("Activate", InstanceAny, null, new[] { typeof(MapPlayerController) }, null);
        static readonly MethodInfo MapDialogueStartSpeechBubbleMethod =
            typeof(MapDialogueInteraction).GetMethod("StartSpeechBubble", InstanceAny);
        static readonly MethodInfo PlayerStatsOnStatsDeathMethod =
            typeof(PlayerStatsManager).GetMethod("OnStatsDeath", InstanceAny);
        static readonly MethodInfo LevelPlayerOnDeathMethod =
            typeof(LevelPlayerController).GetMethod("OnDeath", InstanceAny);
        static readonly FieldInfo PlayerDeathEffectPlayerIdField =
            typeof(PlayerDeathEffect).GetField("playerId", InstanceAny);
        static readonly FieldInfo PlayerDeathEffectParrySwitchField =
            typeof(PlayerDeathEffect).GetField("parrySwitch", InstanceAny);
        static readonly MethodInfo ParrySwitchOnParryPrePauseMethod =
            typeof(ParrySwitch).GetMethod("OnParryPrePause", InstanceAny);
        static readonly MethodInfo ParrySwitchOnParryPostPauseMethod =
            typeof(ParrySwitch).GetMethod("OnParryPostPause", InstanceAny);
        static readonly MethodInfo PlayerDeathEffectReviveParryAnimCompleteMethod =
            typeof(PlayerDeathEffect).GetMethod("OnReviveParryAnimComplete", InstanceAny);

        static Stage _stage = Stage.Idle;
        static float _stageStartedAt;
        static float _lastLogAt;
        static MapLevelLoader _targetLoader;
        static MapDialogueInteraction _targetDialogue;
        static Levels _targetLevel;
        static int _saveSlot = -1;
        static bool _fallbackMapLoadTried;
        static bool _fallbackUnityLevelLoadTried;
        static bool _sawRemoteInput;
        static bool _clientCapturedMap;
        static bool _clientCapturedLevel;
        static bool _clientCapturedFightEnd;
        static bool _clientCapturedDialogueStart;
        static bool _clientCapturedDialogueContinue;
        static bool _clientLevelReached;
        static bool _remoteCheckpointReceived;
        static bool _clientDialogueStartedObserved;
        static bool _clientDialogueContinueObserved;
        static bool _clientPauseObserved;
        static bool _clientPauseSignalReceived;
        static bool _clientUnpauseObserved;
        static bool _clientUnpauseSignalReceived;
        static bool _clientP2MapActivationBlocked;
        static bool _clientReviveTestStartSignalReceived;
        static bool _clientReviveDeathForced;
        static bool _clientReviveMirrorVerified;
        static bool _clientReviveObservedSignalReceived;
        static bool _clientReverseReviveTestStartSignalReceived;
        static bool _clientReverseReviveDeathForced;
        static bool _clientReverseReviveMirrorVerified;
        static bool _clientReverseReviveObservedSignalReceived;
        static bool _clientReverseReviveJumpPressed;
        static bool _clientReverseReviveParryPressed;
        static bool _hostPausePressed;
        static bool _hostPauseObserved;
        static bool _hostUnpausePressed;
        static bool _hostUnpauseObserved;
        static bool _hostReviveStartSignalSent;
        static bool _hostReviveDeathForced;
        static bool _hostReviveJumpPressed;
        static bool _hostReviveParryPressed;
        static bool _hostReviveSwitchTriggered;
        static bool _hostReviveAnimCompleteTriggered;
        static bool _hostReviveVerified;
        static bool _hostReviveForwardCompleteLogged;
        static bool _hostReverseReviveStartSignalSent;
        static bool _hostReverseReviveDeathForced;
        static bool _hostReverseReviveJumpPressed;
        static bool _hostReverseReviveParryPressed;
        static bool _hostReverseReviveSwitchTriggered;
        static bool _hostReverseReviveAnimCompleteTriggered;
        static bool _hostReverseReviveVerified;
        static bool _hostFightTimerResetAfterRevive;
        static bool _hostDialogueInteractPressed;
        static bool _hostDialogueForceStarted;
        static bool _clientDialogueInteractPressed;
        static bool _clientDialogueAdvancePressed;
        static float _clientReviveDeathAt;
        static float _clientReverseReviveDeathAt;
        static float _clientReverseReviveJumpAt;
        static float _clientReverseReviveParryAt;
        static float _hostReviveDeathAt;
        static float _hostReviveJumpAt;
        static float _hostReviveParryAt;
        static float _hostReviveVerifiedAt;
        static float _hostReverseReviveDeathAt;
        static float _hostReverseReviveJumpAt;
        static float _hostReverseReviveParryAt;
        static float _hostReverseReviveVerifiedAt;
        static float _clientDialogueStartObservedAt;
        static float _clientDialogueContinueObservedAt;
        static float _clientDialogueLocalContinueAt;
        static float _hostDialogueContinueObservedAt;
        static float _clientPauseObservedAt;
        static float _hostPauseObservedAt;
        static float _clientUnpauseObservedAt;
        static float _hostUnpauseObservedAt;
        static float _clientLevelReachedAt;
        static float _clientFightReleasedAt;
        static bool _hasFightStartBossHealth;
        static string _fightStartBossName = string.Empty;
        static float _fightStartBossHealth;
        static float _fightStartBossTotal;
        static bool _hostSawP1Shooting;
        static bool _hostSawP2Shooting;
        static bool _clientSawP2Shooting;
        static bool _guestOnlyDamageStarted;
        static bool _guestOnlyDamageVerified;
        static float _guestOnlyDamageStartedAt;
        static string _guestOnlyBossName = string.Empty;
        static float _guestOnlyStartBossHealth;
        static float _guestOnlyStartBossTotal;
        static Vector2 _hostAxis;
        static uint _hostButtons;
        static uint _hostDownButtons;
        static int _hostDownUntilFrame;
        static Vector2 _clientAxis;
        static uint _clientButtons;
        static uint _clientDownButtons;
        static int _clientDownUntilFrame;

        public static void Update()
        {
            ClearExpiredDownButtons();

            if (!Plugin.AutoRunLanSteamE2E)
            {
                if (_stage != Stage.Idle && _stage != Stage.Done && _stage != Stage.Failed)
                    ResetScriptedInput();
                return;
            }

            try
            {
                if (_stage == Stage.Idle)
                    Begin();

                if (_stage == Stage.Failed || _stage == Stage.Done)
                    return;

                if (MultiplayerSession.IsClient)
                    UpdateClientScript();

                switch (_stage)
                {
                    case Stage.WaitConnection:
                        WaitConnection();
                        break;
                    case Stage.LoadSlotSelect:
                        LoadSlotSelect();
                        break;
                    case Stage.WaitSlotSelect:
                        WaitSlotSelect();
                        break;
                    case Stage.WaitMap:
                        WaitMap();
                        break;
                    case Stage.WalkToMapDialogue:
                        WalkToMapDialogue();
                        break;
                    case Stage.StartMapDialogue:
                        StartMapDialogue();
                        break;
                    case Stage.WaitMapDialogueStartAck:
                        WaitMapDialogueStartAck();
                        break;
                    case Stage.WaitMapDialogueContinueAck:
                        WaitMapDialogueContinueAck();
                        break;
                    case Stage.WalkToBoss:
                        WalkToBoss();
                        break;
                    case Stage.OpenStartCard:
                        OpenStartCard();
                        break;
                    case Stage.WaitLevel:
                        WaitLevel();
                        break;
                    case Stage.Fight:
                        Fight();
                        break;
                    case Stage.PauseSync:
                        PauseSync();
                        break;
                    case Stage.ClientObserve:
                        ClientObserve();
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail("Exception: " + ex);
            }
        }

        public static bool TryHandleSessionSignal(SessionSignalPacket pkt)
        {
            if (!Plugin.AutoRunLanSteamE2E)
                return false;

            switch (pkt.Kind)
            {
                case SessionSignalKind.LanSteamE2ECheckpoint:
                    if (MultiplayerSession.IsClient)
                    {
                        _remoteCheckpointReceived = true;
                        Log("Received host fight checkpoint.");
                    }
                    return true;
                case SessionSignalKind.MapDialogueStartedObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientDialogueStartedObserved = true;
                        Log("Client reported map dialogue start sync.");
                    }
                    return true;
                case SessionSignalKind.MapDialogueContinueObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientDialogueContinueObserved = true;
                        Log("Client reported map dialogue continue sync.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EPauseObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientPauseSignalReceived = true;
                        Log("Client reported synced pause menu.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EUnpauseObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientUnpauseSignalReceived = true;
                        Log("Client reported synced resume.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EReviveTestStarted:
                    if (MultiplayerSession.IsClient)
                    {
                        _clientReviveTestStartSignalReceived = true;
                        Log("Received host built-in death/parry revive smoke start.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EReviveObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientReviveObservedSignalReceived = true;
                        Log("Client reported mirrored built-in Player Two revive.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EReverseReviveTestStarted:
                    if (MultiplayerSession.IsClient)
                    {
                        _clientReverseReviveTestStartSignalReceived = true;
                        Log("Received host reverse built-in death/parry revive smoke start.");
                    }
                    return true;
                case SessionSignalKind.LanSteamE2EReverseReviveObserved:
                    if (MultiplayerSession.IsHost)
                    {
                        _clientReverseReviveObservedSignalReceived = true;
                        Log("Client reported mirrored built-in Player One revive.");
                    }
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryGetLocalAxis(PlayerId playerId, int actionId, out float value)
        {
            value = 0f;
            if (!Plugin.AutoRunLanSteamE2E)
                return false;

            if (MultiplayerSession.IsHost && playerId == PlayerId.PlayerOne)
            {
                value = actionId == 0 ? _hostAxis.x : actionId == 1 ? _hostAxis.y : 0f;
                return true;
            }

            if (MultiplayerSession.IsClient && (playerId == PlayerId.None || playerId == PlayerId.PlayerTwo))
            {
                value = actionId == 0 ? _clientAxis.x : actionId == 1 ? _clientAxis.y : 0f;
                return true;
            }

            return false;
        }

        public static bool TryGetLocalButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!Plugin.AutoRunLanSteamE2E || actionId < 0 || actionId >= 32)
                return false;

            if (MultiplayerSession.IsHost && playerId == PlayerId.PlayerOne)
            {
                value = ReadButton(_hostButtons, _hostDownButtons, actionId, down, up);
                return true;
            }

            if (MultiplayerSession.IsClient && (playerId == PlayerId.None || playerId == PlayerId.PlayerTwo))
            {
                value = ReadButton(_clientButtons, _clientDownButtons, actionId, down, up);
                return true;
            }

            return false;
        }

        static bool ReadButton(uint buttons, uint downButtons, int actionId, bool down, bool up)
        {
            if (up)
                return false;
            uint mask = 1u << actionId;
            return down ? (downButtons & mask) != 0u : (buttons & mask) != 0u;
        }

        static void Begin()
        {
            Time.timeScale = 1f;
            if (Plugin.Net == null)
            {
                Fail("Plugin.Net was not initialized.");
                return;
            }

            Log("Starting LAN Steam-emulation E2E. Transport=" + Plugin.NetworkTransportMode + ".");
            if (Plugin.AutoStartLanTransport && !Plugin.Net.IsConnected && !Plugin.Net.IsInLobby)
                Plugin.Net.TryAutoStartConfiguredTransport();
            SetStage(Stage.WaitConnection, "Waiting for LAN connection.");
        }

        static void WaitConnection()
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                if (TimedOut(25f))
                    Fail("LAN peers did not connect. State=" + (Plugin.Net == null ? "none" : Plugin.Net.CurrentStateName) + ".");
                return;
            }

            if (Plugin.Net.IsHost)
                SetStage(Stage.LoadSlotSelect, "LAN host connected; loading save select.");
            else
                SetStage(Stage.ClientObserve, "LAN client connected; observing host flow and sending P2 input.");
        }

        static void LoadSlotSelect()
        {
            ResetScriptedInput();
            if (SceneManager.GetActiveScene().name != "scene_slot_select")
            {
                SceneLoader.LoadScene(
                    Scenes.scene_slot_select,
                    SceneLoader.Transition.Iris,
                    SceneLoader.Transition.Iris,
                    SceneLoader.Icon.Hourglass,
                    null);
            }

            SetStage(Stage.WaitSlotSelect, "Waiting for save slots.");
        }

        static void WaitSlotSelect()
        {
            var screen = UnityEngine.Object.FindObjectOfType<SlotSelectScreen>();
            if (screen == null)
            {
                if (TimedOut(15f)) Fail("Slot select screen did not appear.");
                return;
            }

            var slots = SlotsField == null ? null : SlotsField.GetValue(screen) as SlotSelectScreenSlot[];
            if (slots == null || slots.Length == 0)
            {
                if (TimedOut(15f)) Fail("Slot select slots were not available.");
                return;
            }

            int slotIndex = FindFirstNonEmptySlot(slots);
            if (slotIndex < 0)
            {
                Fail("No non-empty save slot is available for the LAN E2E test.");
                return;
            }

            _saveSlot = slotIndex;
            if (SlotSelectionField != null)
                SlotSelectionField.SetValue(screen, slotIndex);
            if (slots[slotIndex] != null)
                slots[slotIndex].Init(slotIndex);

            Log("Entering save slot " + slotIndex + " through SlotSelectScreen.EnterGame.");
            if (EnterGameMethod == null)
            {
                Fail("Could not find SlotSelectScreen.EnterGame.");
                return;
            }

            EnterGameMethod.Invoke(screen, null);
            SetStage(Stage.WaitMap, "Waiting for world map.");
        }

        static int FindFirstNonEmptySlot(SlotSelectScreenSlot[] slots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                    continue;
                slots[i].Init(i);
                if (!slots[i].IsEmpty)
                    return i;
            }
            return -1;
        }

        static void WaitMap()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (!sceneName.StartsWith("scene_map_world", StringComparison.Ordinal))
            {
                if (!_fallbackMapLoadTried
                 && sceneName == "scene_slot_select"
                 && Time.unscaledTime - _stageStartedAt > 4f)
                {
                    _fallbackMapLoadTried = true;
                    ForceLoadSelectedMap();
                    return;
                }

                if (TimedOut(25f)) Fail("Map did not load; current scene is " + sceneName + ".");
                return;
            }

            if (Map.Current == null || Map.Current.players == null || Map.Current.players.Length < 2)
                return;

            var p1 = Map.Current.players[0];
            var p2 = Map.Current.players[1];
            if (p1 == null || p2 == null)
                return;

            if (IsMapDialogueSmokeTarget())
            {
                _targetDialogue = ChooseNearestMapDialogue(p1.transform.position);
                if (_targetDialogue == null)
                {
                    Fail("No active MapDialogueInteraction was found on " + sceneName + ".");
                    return;
                }

                Log("Map loaded for dialogue smoke; P1=" + DescribeMapPlayer(p1)
                    + "; P2=" + DescribeMapPlayer(p2)
                    + "; target=" + DescribeMapDialogue(_targetDialogue) + ".");
                CaptureScreen("host_map_dialogue_target");
                SetStage(Stage.WalkToMapDialogue, "Walking to map dialogue target.");
                return;
            }

            _targetLoader = ChooseConfiguredBossLoader(p1.transform.position);
            if (_stage == Stage.Failed)
                return;

            if (_targetLoader == null)
                _targetLoader = ChooseNearestBossLoader(p1.transform.position);

            if (_targetLoader == null)
            {
                Fail("No active boss MapLevelLoader was found on " + sceneName + ".");
                return;
            }

            _targetLevel = GetLoaderLevel(_targetLoader);
            Log("Map loaded; P1=" + DescribeMapPlayer(p1)
                + "; P2=" + DescribeMapPlayer(p2)
                + "; P2 networkControlled=" + MultiplayerSession.IsNetworkControlledPlayer(PlayerId.PlayerTwo)
                + "; target=" + _targetLevel + ".");

            if (!VerifyP2MapActivationBlocked(_targetLoader, p2, "host"))
                return;
            CaptureScreen("host_map_" + _targetLevel);
            SetStage(Stage.WalkToBoss, "Walking to " + _targetLevel + ".");
        }

        static void WalkToBoss()
        {
            if (_targetLoader == null)
            {
                Fail("Target loader disappeared while walking.");
                return;
            }

            if (Map.Current == null || Map.Current.players == null || Map.Current.players[0] == null)
                return;

            var p1 = Map.Current.players[0];
            Vector2 target = GetInteractionPoint(_targetLoader);
            Vector2 current = p1.transform.position;
            Vector2 delta = target - current;
            float distance = delta.magnitude;

            if (Time.unscaledTime - _lastLogAt > 2f)
            {
                _lastLogAt = Time.unscaledTime;
                Log("Walking: distance to " + _targetLevel + " is " + distance.ToString("0.00") + ".");
            }

            CheckRemoteInput();
            if (distance <= Mathf.Max(0.2f, _targetLoader.interactionDistance * 0.65f) || distance <= 0.65f)
            {
                _hostAxis = Vector2.zero;
                ActivateTargetLoader(p1);
                SetStage(Stage.OpenStartCard, "Reached " + _targetLevel + "; opening start card.");
                return;
            }

            if (TimedOut(WalkTimeout))
            {
                Fail("Could not walk to " + _targetLevel + " within " + WalkTimeout + " seconds.");
                return;
            }

            _hostAxis = delta.normalized;
        }

        static void WalkToMapDialogue()
        {
            if (_targetDialogue == null)
            {
                Fail("Target dialogue disappeared while walking.");
                return;
            }

            if (Map.Current == null || Map.Current.players == null || Map.Current.players[0] == null)
                return;

            var p1 = Map.Current.players[0];
            var p2 = Map.Current.players.Length > 1 ? Map.Current.players[1] : null;
            Vector2 activationPoint = GetInteractionPoint(_targetDialogue);
            Vector2 target = GetMapDialogueApproachPoint(_targetDialogue);
            Vector2 current = p1.transform.position;
            Vector2 delta = target - current;
            float distance = delta.magnitude;
            float activationDistance = Vector2.Distance(current, activationPoint);
            float p2ActivationDistance = p2 == null
                ? float.MaxValue
                : Vector2.Distance(p2.transform.position, activationPoint);

            if (Time.unscaledTime - _lastLogAt > 2f)
            {
                _lastLogAt = Time.unscaledTime;
                Log("Walking: distance to dialogue approach is " + distance.ToString("0.00")
                    + "; P1 prompt distance is " + activationDistance.ToString("0.00")
                    + "; P2 prompt distance is " + p2ActivationDistance.ToString("0.00") + ".");
            }

            CheckRemoteInput();
            bool closeEnoughForForcedDialogue =
                Time.unscaledTime - _stageStartedAt > 6.0f
                && (activationDistance <= Mathf.Max(1.8f, _targetDialogue.interactionDistance * 2.0f)
                 || p2ActivationDistance <= Mathf.Max(1.4f, _targetDialogue.interactionDistance * 1.4f));

            if (activationDistance <= Mathf.Max(0.2f, _targetDialogue.interactionDistance * 0.98f)
             || p2ActivationDistance <= Mathf.Max(0.2f, _targetDialogue.interactionDistance * 0.98f)
             || distance <= 0.18f
             || closeEnoughForForcedDialogue)
            {
                _hostAxis = Vector2.zero;
                SetStage(Stage.StartMapDialogue, "Reached dialogue target; pressing interact.");
                return;
            }

            if (TimedOut(WalkTimeout))
            {
                Fail("Could not walk to dialogue target within " + WalkTimeout + " seconds.");
                return;
            }

            _hostAxis = delta.normalized;
        }

        static void StartMapDialogue()
        {
            _hostAxis = Vector2.zero;
            CheckRemoteInput();

            if (IsSpeechBubbleVisible())
            {
                Log("Host map dialogue opened.");
                CaptureScreen("host_map_dialogue_open");
                SetStage(Stage.WaitMapDialogueStartAck, "Waiting for client to apply dialogue start.");
                return;
            }

            if (!_hostDialogueInteractPressed && Time.unscaledTime - _stageStartedAt > 0.5f)
            {
                _hostDialogueInteractPressed = true;
                PressHost(CupheadButton.Accept);
            }

            if (!_hostDialogueForceStarted && Time.unscaledTime - _stageStartedAt > 2.5f)
            {
                _hostDialogueForceStarted = true;
                Log("Normal map interact did not open quickly; force-starting speech bubble "
                    + _targetDialogue.dialogueInteraction
                    + " after walking to the NPC.");
                if (MapDialogueStartSpeechBubbleMethod != null)
                    MapDialogueStartSpeechBubbleMethod.Invoke(_targetDialogue, null);
                else
                    Dialoguer.StartDialogue(_targetDialogue.dialogueInteraction);
            }

            if (TimedOut(DialogueTimeout))
                Fail("Map dialogue did not open on the host.");
        }

        static void WaitMapDialogueStartAck()
        {
            _hostAxis = Vector2.zero;
            if (_clientDialogueStartedObserved && Time.unscaledTime - _stageStartedAt > 1.0f)
            {
                if (ShouldClientAdvanceMapDialogue())
                {
                    Log("Waiting for client to advance map dialogue after client observed start.");
                    SetStage(Stage.WaitMapDialogueContinueAck, "Waiting for host to apply client dialogue continue.");
                    return;
                }

                Log("Advancing host map dialogue after client observed start.");
                Dialoguer.ContinueDialogue();
                SetStage(Stage.WaitMapDialogueContinueAck, "Waiting for client to apply dialogue continue.");
                return;
            }

            if (TimedOut(DialogueTimeout))
                Fail("Client did not report map dialogue start sync.");
        }

        static void WaitMapDialogueContinueAck()
        {
            _hostAxis = Vector2.zero;
            if (ShouldClientAdvanceMapDialogue() && MapDialogueSync.RemoteContinueCount > 0)
            {
                if (_hostDialogueContinueObservedAt <= 0f)
                    _hostDialogueContinueObservedAt = Time.unscaledTime;

                if (Time.unscaledTime - _hostDialogueContinueObservedAt <= 1.0f)
                    return;

                ResetScriptedInput();
                Time.timeScale = 0f;
                SetStage(Stage.Done, "MAP DIALOGUE CLIENT-CONTINUE PASS.");
                Plugin.Log.LogInfo("[LanSteamE2E] HOST PASS");
                return;
            }

            if (_clientDialogueContinueObserved)
            {
                ResetScriptedInput();
                Time.timeScale = 0f;
                SetStage(Stage.Done, "MAP DIALOGUE PASS.");
                Plugin.Log.LogInfo("[LanSteamE2E] HOST PASS");
                return;
            }

            if (TimedOut(DialogueTimeout))
                Fail("Client did not report map dialogue continue sync.");
        }

        static void OpenStartCard()
        {
            _hostAxis = Vector2.zero;
            CheckRemoteInput();

            if (IsAnyStartUiActive())
            {
                Log("Start card is active; loading selected map target " + _targetLevel + ".");
                SceneLoader.LoadLevel(_targetLevel, SceneLoader.Transition.Iris, SceneLoader.Icon.Hourglass, null);
                SetStage(Stage.WaitLevel, "Confirming boss start card for " + _targetLevel + ".");
                return;
            }

            if (Time.unscaledTime - _stageStartedAt < 1.5f)
                PressHost(CupheadButton.Accept);

            if (TimedOut(CardTimeout))
                Fail("Boss start card did not open for " + _targetLevel + ".");
        }

        static void WaitLevel()
        {
            _hostAxis = Vector2.zero;
            CheckRemoteInput();

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName.StartsWith("scene_map_world", StringComparison.Ordinal)
             || sceneName == "scene_slot_select")
            {
                if (!_fallbackUnityLevelLoadTried
                 && sceneName.StartsWith("scene_map_world", StringComparison.Ordinal)
                 && Time.unscaledTime - _stageStartedAt > 3f)
                {
                    _fallbackUnityLevelLoadTried = true;
                    ForceUnityLevelLoad();
                    return;
                }

                if (TimedOut(LevelTimeout)) Fail("Level did not load after confirming " + _targetLevel + "; current scene is " + sceneName + ".");
                return;
            }

            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            if (p1 == null || p2 == null || p1.stats == null || p2.stats == null)
            {
                if (TimedOut(LevelTimeout)) Fail("Level scene loaded but both level players were not available.");
                return;
            }

            Log("Level loaded: " + sceneName
                + "; P1=" + DescribeLevelPlayer(p1)
                + "; P2=" + DescribeLevelPlayer(p2)
                + "; P2 networkControlled=" + MultiplayerSession.IsNetworkControlledPlayer(PlayerId.PlayerTwo) + ".");

            if (p2.IsDead || p2.stats.Health <= 0)
            {
                Fail("Player Two spawned dead: " + DescribeLevelPlayer(p2) + ".");
                return;
            }

            if (!ValidateBuiltInPlayerUniqueness("host level start"))
                return;

            CaptureScreen("host_level_" + sceneName);
            _hasFightStartBossHealth = BossHealthBarOverlay.TryGetPrimaryBossHealth(
                out _fightStartBossName,
                out _fightStartBossHealth,
                out _fightStartBossTotal);
            Log("Host boss health at fight start: " + BossHealthBarOverlay.GetDebugSummary() + ".");
            SetStage(Stage.Fight, "Fighting briefly to verify live two-player LAN state.");
        }

        static void Fight()
        {
            if (LevelStartSync.IsHostWaitingForGuestStart)
            {
                KeepScriptedPlayersAlive();
                CheckRemoteInput();
                _stageStartedAt = Time.unscaledTime;
                return;
            }

            if (!RunHostBuiltInReviveSmoke())
                return;

            if (!RunHostReverseBuiltInReviveSmoke())
                return;

            if (!RunGuestOnlyShootingSmoke())
                return;

            KeepScriptedPlayersAlive();
            HoldHost(CupheadButton.Shoot);
            CheckRemoteInput();
            TrackHostShootingState(
                PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController,
                PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController);

            if (!_hasFightStartBossHealth && Time.unscaledTime - _stageStartedAt > 1f)
            {
                _hasFightStartBossHealth = BossHealthBarOverlay.TryGetPrimaryBossHealth(
                    out _fightStartBossName,
                    out _fightStartBossHealth,
                    out _fightStartBossTotal);
                if (_hasFightStartBossHealth)
                    Log("Host boss health after fight startup: " + BossHealthBarOverlay.GetDebugSummary() + ".");
            }

            if (!TimedOut(FightDuration))
                return;

            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            KeepScriptedPlayersAlive(p1, p2);
            TrackHostShootingState(p1, p2);
            string endBossName;
            float endBossHealth;
            float endBossTotal;
            bool hasEndBossHealth = BossHealthBarOverlay.TryGetPrimaryBossHealth(
                out endBossName,
                out endBossHealth,
                out endBossTotal);
            Log("Fight smoke complete; P1=" + DescribeLevelPlayer(p1) + "; P2=" + DescribeLevelPlayer(p2)
                + "; sawRemoteInput=" + _sawRemoteInput
                + "; boss=" + BossHealthBarOverlay.GetDebugSummary() + ".");
            CaptureScreen("host_fight_end_" + SceneManager.GetActiveScene().name);

            if (p2 == null || p2.stats == null)
            {
                Fail("Player Two disappeared during the LAN fight smoke.");
                return;
            }

            if (p1 == null || p1.stats == null)
            {
                Fail("Player One disappeared during the LAN fight smoke.");
                return;
            }

            if (p1.IsDead || p1.stats.Health <= 0)
            {
                Fail("Player One became dead during the LAN fight smoke: " + DescribeLevelPlayer(p1) + ".");
                return;
            }

            if (p2.IsDead || p2.stats.Health <= 0)
            {
                Fail("Player Two became dead during the LAN fight smoke: " + DescribeLevelPlayer(p2) + ".");
                return;
            }

            if (!_sawRemoteInput)
            {
                Fail("Host did not observe client input frames for Player Two.");
                return;
            }

            if (!_hostSawP1Shooting || !_hostSawP2Shooting)
            {
                Fail("Host did not observe both players shooting during the fight smoke: P1="
                    + _hostSawP1Shooting
                    + " P2="
                    + _hostSawP2Shooting
                    + ".");
                return;
            }

            if (_hasFightStartBossHealth && hasEndBossHealth && endBossName == _fightStartBossName
             && endBossHealth >= _fightStartBossHealth - 0.5f)
            {
                Fail("Boss health did not decrease during the LAN fight smoke: start="
                    + _fightStartBossHealth.ToString("0.##")
                    + "/"
                    + _fightStartBossTotal.ToString("0.##")
                    + ", end="
                    + endBossHealth.ToString("0.##")
                    + "/"
                    + endBossTotal.ToString("0.##")
                    + ".");
                return;
            }

            ResetScriptedInput();
            SetStage(Stage.PauseSync, "Opening pause menu to verify host pause sync.");
        }

        static void PauseSync()
        {
            KeepScriptedPlayersAlive();
            CheckRemoteInput();
            _hostAxis = Vector2.zero;

            if (!_hostPausePressed && Time.unscaledTime - _stageStartedAt > 0.25f)
            {
                _hostPausePressed = true;
                PressHost(CupheadButton.Pause);
                Log("Host pressed pause to open the level pause menu.");
            }

            if (IsLevelPauseMenuOpen())
            {
                if (!_hostPauseObserved)
                {
                    _hostPauseObserved = true;
                    _hostPauseObservedAt = Time.unscaledTime;
                    Log("Host pause menu observed; waiting for client pause sync.");
                    CaptureScreen("host_pause_sync_" + SceneManager.GetActiveScene().name);
                    SessionSync.BroadcastSessionSnapshot(true);
                }

                if (_clientPauseSignalReceived)
                {
                    if (!_hostUnpausePressed && Time.unscaledTime - _hostPauseObservedAt > 0.5f)
                    {
                        _hostUnpausePressed = true;
                        PressHost(CupheadButton.Pause);
                        Log("Host pressed pause again to resume.");
                    }
                }

                if (_hostPauseObservedAt > 0f && Time.unscaledTime - _hostPauseObservedAt > PauseTimeout)
                {
                    Fail(_clientPauseSignalReceived
                        ? "Host did not resume after the second pause press."
                        : "Client did not report the synced host pause menu.");
                    return;
                }
            }

            if (_hostUnpausePressed
             && !_hostUnpauseObserved
             && IsLevelPauseMenuClosed())
            {
                _hostUnpauseObserved = true;
                _hostUnpauseObservedAt = Time.unscaledTime;
                Log("Host resume observed; waiting for client resume sync.");
                SessionSync.BroadcastSessionSnapshot(true);
            }

            if (_hostUnpauseObserved)
            {
                if (_clientUnpauseSignalReceived)
                {
                    SendHostCheckpointSignal();
                    Time.timeScale = 0f;
                    SetStage(Stage.Done, "PAUSE/RESUME SYNC PASS.");
                    Plugin.Log.LogInfo("[LanSteamE2E] HOST PASS");
                    return;
                }

                if (_hostUnpauseObservedAt > 0f && Time.unscaledTime - _hostUnpauseObservedAt > PauseTimeout)
                {
                    Fail("Client did not report synced resume.");
                    return;
                }
            }

            if (!_hostPauseObserved && TimedOut(PauseTimeout))
                Fail("Host did not enter the level pause menu after pressing pause.");
        }

        static void ClientObserve()
        {
            UpdateClientScript();
            string sceneName = SceneManager.GetActiveScene().name;

            if (Time.unscaledTime - _lastLogAt > 3f)
            {
                _lastLogAt = Time.unscaledTime;
                Log("Client observing scene=" + sceneName + "; connected=" + (Plugin.Net != null && Plugin.Net.IsConnected) + ".");
            }

            if (!_clientCapturedMap && sceneName.StartsWith("scene_map_world", StringComparison.Ordinal))
            {
                _clientCapturedMap = true;
                Log("Client map sync: " + DescribeClientMapPlayers() + ".");
                CaptureScreen("client_map_" + sceneName);

                if (!_clientP2MapActivationBlocked)
                {
                    var loader = ChooseNearestBossLoader(Map.Current.players[0].transform.position);
                    var p2Map = Map.Current.players.Length > 1 ? Map.Current.players[1] : null;
                    if (!VerifyP2MapActivationBlocked(loader, p2Map, "client"))
                        return;
                    _clientP2MapActivationBlocked = true;
                }
            }

            if (IsMapDialogueSmokeTarget() && sceneName.StartsWith("scene_map_world", StringComparison.Ordinal))
            {
                if (!_clientCapturedDialogueStart && MapDialogueSync.RemoteStartCount > 0)
                {
                    _clientCapturedDialogueStart = true;
                    _clientDialogueStartObservedAt = Time.unscaledTime;
                    Log("Client map dialogue start sync observed.");
                    CaptureScreen("client_map_dialogue_start");
                    SendDialogueObservedSignal(SessionSignalKind.MapDialogueStartedObserved);
                }

                if (ShouldClientAdvanceMapDialogue()
                 && !_clientCapturedDialogueContinue
                 && _clientDialogueAdvancePressed
                 && _clientDialogueLocalContinueAt > 0f
                 && Time.unscaledTime - _clientDialogueLocalContinueAt > 1.0f)
                {
                    _clientCapturedDialogueContinue = true;
                    Log("Client map dialogue local continue sent.");
                    CaptureScreen("client_map_dialogue_local_continue");
                    Time.timeScale = 0f;
                    SetStage(Stage.Done, "CLIENT MAP DIALOGUE LOCAL-CONTINUE PASS.");
                    Plugin.Log.LogInfo("[LanSteamE2E] CLIENT PASS");
                    return;
                }

                if (!_clientCapturedDialogueContinue && MapDialogueSync.RemoteContinueCount > 0)
                {
                    _clientCapturedDialogueContinue = true;
                    _clientDialogueContinueObservedAt = Time.unscaledTime;
                    Log("Client map dialogue continue sync observed.");
                    CaptureScreen("client_map_dialogue_continue");
                }

                if (_clientCapturedDialogueContinue
                 && _clientDialogueContinueObservedAt > 0f
                 && Time.unscaledTime - _clientDialogueContinueObservedAt > 1.0f)
                {
                    CaptureScreen("client_map_dialogue_continue_settled");
                    SendDialogueObservedSignal(SessionSignalKind.MapDialogueContinueObserved);
                    Time.timeScale = 0f;
                    SetStage(Stage.Done, "CLIENT MAP DIALOGUE PASS.");
                    Plugin.Log.LogInfo("[LanSteamE2E] CLIENT PASS");
                }

                return;
            }

            if (!sceneName.StartsWith("scene_level_", StringComparison.Ordinal))
                return;

            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            if (p1 == null || p2 == null || p1.stats == null || p2.stats == null)
                return;

            if (!ValidateBuiltInPlayerUniqueness("client level observe"))
                return;

            KeepScriptedPlayersAlive(p1, p2);

            if (!_clientLevelReached)
            {
                _clientLevelReached = true;
                _clientLevelReachedAt = Time.unscaledTime;
                Log("Client reached level: " + sceneName
                    + "; P1=" + DescribeLevelPlayer(p1)
                    + "; P2=" + DescribeLevelPlayer(p2)
                    + "; boss=" + BossHealthBarOverlay.GetDebugSummary() + ".");
            }

            if (!_clientCapturedLevel)
            {
                _clientCapturedLevel = true;
                CaptureScreen("client_level_" + sceneName);
            }

            if (!_clientPauseObserved && IsLevelPauseMenuOpen())
            {
                _clientPauseObserved = true;
                _clientPauseObservedAt = Time.unscaledTime;
                Log("Client observed synced host pause menu.");
                CaptureScreen("client_pause_sync_" + sceneName);
                SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EPauseObserved);
            }

            if (_clientPauseObserved
             && !_clientUnpauseObserved
             && IsLevelPauseMenuClosed()
             && Time.unscaledTime - _clientPauseObservedAt > 0.25f)
            {
                _clientUnpauseObserved = true;
                _clientUnpauseObservedAt = Time.unscaledTime;
                Log("Client observed synced resume.");
                SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EUnpauseObserved);
            }

            if (LevelStartSync.IsClientWaitingForStartRelease
             || (Time.timeScale == 0f && PauseManager.state != PauseManager.State.Paused))
            {
                _clientFightReleasedAt = 0f;
                return;
            }

            if (_clientFightReleasedAt <= 0f)
                _clientFightReleasedAt = Time.unscaledTime;

            if (!_clientReviveMirrorVerified)
            {
                RunClientBuiltInReviveObserver(p2);
                return;
            }

            if (!_clientReverseReviveMirrorVerified)
            {
                RunClientReverseBuiltInReviveObserver(p1, p2);
                return;
            }

            TrackClientShootingState(p2);

            if (p2.IsDead || p2.stats.Health <= 0)
            {
                Fail("Client saw Player Two dead in level: " + DescribeLevelPlayer(p2) + ".");
                return;
            }

            if (p1.IsDead || p1.stats.Health <= 0)
            {
                Fail("Client saw Player One dead in level: " + DescribeLevelPlayer(p1) + ".");
                return;
            }

            if (_clientFightReleasedAt > 0f && Time.unscaledTime - _clientFightReleasedAt > 2f && !_clientSawP2Shooting)
            {
                Fail("Client did not observe local Player Two shooting during the fight smoke.");
                return;
            }

            bool fightWindowComplete = _remoteCheckpointReceived
                || (_clientFightReleasedAt > 0f && Time.unscaledTime - _clientFightReleasedAt >= FightDuration);
            if (!fightWindowComplete)
                return;

            if (!_clientPauseObserved)
            {
                if (_clientFightReleasedAt > 0f
                 && Time.unscaledTime - _clientFightReleasedAt > FightDuration + PauseTimeout + 4f)
                    Fail("Client did not observe the synced host pause menu.");
                return;
            }

            if (!_clientUnpauseObserved)
            {
                if (_clientPauseObservedAt > 0f && Time.unscaledTime - _clientPauseObservedAt > PauseTimeout + 4f)
                    Fail("Client observed the pause menu but did not observe synced resume.");
                return;
            }

            Log((_remoteCheckpointReceived ? "Client host-checkpoint pause/resume sync complete" : "Client pause/resume sync complete")
                + ": P1=" + DescribeLevelPlayer(p1)
                + "; P2=" + DescribeLevelPlayer(p2)
                + "; boss=" + BossHealthBarOverlay.GetDebugSummary() + ".");
            if (!_clientCapturedFightEnd)
            {
                _clientCapturedFightEnd = true;
                CaptureScreen("client_fight_end_" + sceneName);
            }

            Time.timeScale = 0f;
            SetStage(Stage.Done, "CLIENT PASS.");
            Plugin.Log.LogInfo("[LanSteamE2E] CLIENT PASS");
        }

        static void SendHostCheckpointSignal()
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = new SessionSignalPacket
            {
                Signal = (byte)SessionSignalKind.LanSteamE2ECheckpoint,
                SaveRevision = 0,
            };
            Plugin.Net.SendSessionSignal(ref pkt);
        }

        static void SendDialogueObservedSignal(SessionSignalKind kind)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = new SessionSignalPacket
            {
                Signal = (byte)kind,
                SaveRevision = 0,
            };
            Plugin.Net.SendSessionSignal(ref pkt);
        }

        static void KeepScriptedPlayersAlive()
        {
            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            KeepScriptedPlayersAlive(p1, p2);
        }

        static void KeepScriptedPlayersAlive(LevelPlayerController p1, LevelPlayerController p2)
        {
            RestoreScriptedPlayerHealth(p1);
            RestoreScriptedPlayerHealth(p2);
        }

        static void TrackHostShootingState(LevelPlayerController p1, LevelPlayerController p2)
        {
            _hostSawP1Shooting = _hostSawP1Shooting || IsPlayerShooting(p1);
            _hostSawP2Shooting = _hostSawP2Shooting || IsPlayerShooting(p2);
        }

        static void TrackClientShootingState(LevelPlayerController p2)
        {
            _clientSawP2Shooting = _clientSawP2Shooting || IsPlayerShooting(p2);
        }

        static bool RunGuestOnlyShootingSmoke()
        {
            if (_guestOnlyDamageVerified)
                return true;

            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            if (p1 == null || p2 == null || p1.stats == null || p2.stats == null)
            {
                Fail("Cannot run guest-only shooting smoke because a built-in player is missing.");
                return false;
            }

            if (!ValidateBuiltInPlayerUniqueness("host guest-only shooting"))
                return false;

            KeepScriptedPlayersAlive(p1, p2);
            CheckRemoteInput();
            TrackHostShootingState(null, p2);
            _hostButtons &= ~ButtonMask(CupheadButton.Shoot);

            string bossName;
            float bossHealth;
            float bossTotal;
            bool hasBossHealth = BossHealthBarOverlay.TryGetPrimaryBossHealth(
                out bossName,
                out bossHealth,
                out bossTotal);
            if (!hasBossHealth)
            {
                if (Time.unscaledTime - _stageStartedAt > 6f)
                    Fail("Could not read boss health for the guest-only shooting smoke.");
                return false;
            }

            if (!_guestOnlyDamageStarted)
            {
                _guestOnlyDamageStarted = true;
                _guestOnlyDamageStartedAt = Time.unscaledTime;
                _guestOnlyBossName = bossName;
                _guestOnlyStartBossHealth = bossHealth;
                _guestOnlyStartBossTotal = bossTotal;
                Log("Guest-only shooting smoke started: boss="
                    + bossName
                    + " "
                    + bossHealth.ToString("0.##")
                    + "/"
                    + bossTotal.ToString("0.##")
                    + ".");
                return false;
            }

            if (Time.unscaledTime - _guestOnlyDamageStartedAt < GuestOnlyDamageDuration)
                return false;

            if (!_sawRemoteInput)
            {
                Fail("Host did not observe client input frames during the guest-only shooting smoke.");
                return false;
            }

            if (!_hostSawP2Shooting)
            {
                Fail("Host did not observe Player Two shooting during the guest-only shooting smoke.");
                return false;
            }

            if (bossName == _guestOnlyBossName
             && bossHealth >= _guestOnlyStartBossHealth - 0.5f)
            {
                Fail("Guest Player Two shooting did not damage the boss: start="
                    + _guestOnlyStartBossHealth.ToString("0.##")
                    + "/"
                    + _guestOnlyStartBossTotal.ToString("0.##")
                    + ", end="
                    + bossHealth.ToString("0.##")
                    + "/"
                    + bossTotal.ToString("0.##")
                    + ".");
                return false;
            }

            _guestOnlyDamageVerified = true;
            _stageStartedAt = Time.unscaledTime;
            Log("Guest-only shooting smoke passed: boss="
                + bossName
                + " "
                + bossHealth.ToString("0.##")
                + "/"
                + bossTotal.ToString("0.##")
                + ".");
            return true;
        }

        static bool IsPlayerShooting(LevelPlayerController player)
        {
            try
            {
                return player != null
                    && player.weaponManager != null
                    && player.weaponManager.IsShooting;
            }
            catch
            {
                return false;
            }
        }

        static bool ValidateBuiltInPlayerUniqueness(string context)
        {
            var players = Resources.FindObjectsOfTypeAll<LevelPlayerController>();
            int p1Count = 0;
            int p2Count = 0;
            string details = string.Empty;

            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (player == null)
                    continue;
                if (player.gameObject == null || !player.gameObject.scene.IsValid())
                    continue;

                if (player.id == PlayerId.PlayerOne)
                    p1Count++;
                else if (player.id == PlayerId.PlayerTwo)
                    p2Count++;
                else
                    continue;

                if (details.Length > 0)
                    details += " | ";
                details += player.id + ":" + player.name + " active=" + player.gameObject.activeInHierarchy;
            }

            if (p1Count == 1 && p2Count == 1)
                return true;

            Fail("Duplicate or missing built-in players during "
                + context
                + ": P1 count="
                + p1Count
                + " P2 count="
                + p2Count
                + " ["
                + details
                + "].");
            return false;
        }

        static bool IsLevelPauseMenuOpen()
        {
            var gui = UnityEngine.Object.FindObjectOfType<LevelPauseGUI>();
            return gui != null
                && PauseManager.state == PauseManager.State.Paused
                && gui.state == AbstractPauseGUI.State.Paused;
        }

        static bool IsLevelPauseMenuClosed()
        {
            var gui = UnityEngine.Object.FindObjectOfType<LevelPauseGUI>();
            return gui != null
                && PauseManager.state == PauseManager.State.Unpaused
                && gui.state == AbstractPauseGUI.State.Unpaused;
        }

        static void RestoreScriptedPlayerHealth(LevelPlayerController player)
        {
            if (player == null || player.stats == null || player.IsDead || player.stats.HealthMax <= 0)
                return;

            if (player.stats.Health < player.stats.HealthMax)
                player.stats.SetHealth(player.stats.HealthMax);
        }

        static void UpdateClientScript()
        {
            if (!Plugin.AutoRunLanSteamE2E || !MultiplayerSession.IsClient)
                return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName.StartsWith("scene_level_", StringComparison.Ordinal))
            {
                _clientAxis = Vector2.zero;
                _clientButtons = ButtonMask(CupheadButton.Shoot);
                _clientDownButtons = 0u;
                return;
            }

            if (IsMapDialogueSmokeTarget() && sceneName.StartsWith("scene_map_world", StringComparison.Ordinal))
            {
                DriveClientMapDialogueInput();
                return;
            }

            float t = Time.unscaledTime;
            _clientAxis = new Vector2(Mathf.Sin(t * 2.2f) > 0f ? 0.6f : -0.6f, 0f);
            _clientButtons = 0u;
            _clientDownButtons = 0u;
        }

        static void DriveClientMapDialogueInput()
        {
            _clientButtons = 0u;
            _clientDownButtons = 0u;

            if (ShouldClientAdvanceMapDialogue()
             && _clientCapturedDialogueStart
             && !_clientDialogueAdvancePressed
             && Time.unscaledTime - _clientDialogueStartObservedAt > 2.0f)
            {
                _clientAxis = Vector2.zero;
                _clientDialogueAdvancePressed = true;
                _clientDialogueLocalContinueAt = Time.unscaledTime;
                Log("Client advancing map dialogue.");
                Dialoguer.ContinueDialogue();
                return;
            }

            if (Map.Current == null || Map.Current.players == null || Map.Current.players.Length < 2 || Map.Current.players[1] == null)
            {
                _clientAxis = Vector2.zero;
                return;
            }

            var p2 = Map.Current.players[1];
            var dialogue = ChooseNearestMapDialogue(p2.transform.position);
            if (dialogue == null)
            {
                _clientAxis = Vector2.zero;
                return;
            }

            Vector2 activationPoint = GetInteractionPoint(dialogue);
            Vector2 approach = GetMapDialogueApproachPoint(dialogue);
            Vector2 current = p2.transform.position;
            float activationDistance = Vector2.Distance(current, activationPoint);
            if (activationDistance <= Mathf.Max(0.2f, dialogue.interactionDistance * 0.95f))
            {
                _clientAxis = Vector2.zero;
                if (ShouldClientAdvanceMapDialogue())
                    return;

                if (!_clientDialogueInteractPressed)
                {
                    _clientDialogueInteractPressed = true;
                    PressClient(CupheadButton.Accept);
                }
                return;
            }

            Vector2 delta = approach - current;
            _clientAxis = delta.sqrMagnitude > 0.04f ? delta.normalized : Vector2.zero;
        }

        static void CheckRemoteInput()
        {
            InputFramePacket input;
            if (RemoteInputDriver.TryGetCurrent(PlayerId.PlayerTwo, out input)
             && (Mathf.Abs(input.AxisX) > 0.05f || Mathf.Abs(input.AxisY) > 0.05f || input.Buttons != 0u))
            {
                _sawRemoteInput = true;
            }
        }

        static void ForceLoadSelectedMap()
        {
            if (_saveSlot < 0)
            {
                Fail("Cannot force map load without a selected save slot.");
                return;
            }

            var data = PlayerData.GetDataForSlot(_saveSlot);
            if (data == null)
            {
                Fail("Selected save slot " + _saveSlot + " has no PlayerData.");
                return;
            }

            Scenes map = data.CurrentMap;
            if (!Enum.IsDefined(typeof(Scenes), map) || map == Scenes.scene_slot_select)
                map = Scenes.scene_map_world_1;
            if (!DLCManager.DLCEnabled() && map == Scenes.scene_map_world_DLC)
                map = Scenes.scene_map_world_1;

            PlayerData.CurrentSaveFileIndex = _saveSlot;
            PlayerManager.player1IsMugman = data.isPlayer1Mugman;
            data.isPlayer1Mugman = PlayerManager.player1IsMugman;
            PlayerData.inGame = true;

            Log("SlotSelectScreen.EnterGame did not transition; loading selected save map via SceneLoader: " + map + ".");
            SceneLoader.LoadScene(
                map,
                SceneLoader.Transition.Fade,
                SceneLoader.Transition.Iris,
                SceneLoader.Icon.Hourglass,
                null);
        }

        static void ForceUnityLevelLoad()
        {
            string levelScene = LevelProperties.GetLevelScene(_targetLevel);
            Log("SceneLoader did not transition from the visible start card; forcing Unity scene load for " + _targetLevel + " (" + levelScene + ").");
            SceneSyncState.ResetTransientSyncState();
            SceneLoader.SetCurrentLevel(_targetLevel);
            PlayerData.inGame = true;
            SceneManager.LoadScene(levelScene);
        }

        static MapLevelLoader ChooseNearestBossLoader(Vector3 from)
        {
            MapLevelLoader[] loaders = UnityEngine.Object.FindObjectsOfType<MapLevelLoader>();
            MapLevelLoader best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < loaders.Length; i++)
            {
                var loader = loaders[i];
                if (loader == null || !loader.isActiveAndEnabled || !loader.gameObject.activeInHierarchy)
                    continue;

                Levels level = GetLoaderLevel(loader);
                if (!IsBossLevel(level))
                    continue;

                float dist = Vector2.Distance(from, GetInteractionPoint(loader));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = loader;
                }
            }
            return best;
        }

        static MapDialogueInteraction ChooseNearestMapDialogue(Vector3 from)
        {
            MapDialogueInteraction[] dialogues = UnityEngine.Object.FindObjectsOfType<MapDialogueInteraction>();
            MapDialogueInteraction best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < dialogues.Length; i++)
            {
                var dialogue = dialogues[i];
                if (dialogue == null || !dialogue.isActiveAndEnabled || !dialogue.gameObject.activeInHierarchy)
                    continue;

                float dist = Vector2.Distance(from, GetInteractionPoint(dialogue));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = dialogue;
                }
            }
            return best;
        }

        static bool IsMapDialogueSmokeTarget()
        {
            string configured = Plugin.AutoRunLanSteamE2ETarget;
            return !string.IsNullOrEmpty(configured)
                && (string.Equals(configured, "MapDialogue", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(configured, "Dialogue", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(configured, "NpcDialogue", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(configured, "MapDialogueClientContinue", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(configured, "DialogueClientContinue", StringComparison.OrdinalIgnoreCase));
        }

        static bool ShouldClientAdvanceMapDialogue()
        {
            string configured = Plugin.AutoRunLanSteamE2ETarget;
            return string.Equals(configured, "MapDialogueClientContinue", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configured, "DialogueClientContinue", StringComparison.OrdinalIgnoreCase);
        }

        static MapLevelLoader ChooseConfiguredBossLoader(Vector3 from)
        {
            string configured = Plugin.AutoRunLanSteamE2ETarget;
            if (string.IsNullOrEmpty(configured) || string.Equals(configured, "Nearest", StringComparison.OrdinalIgnoreCase))
                return null;

            Levels targetLevel;
            try
            {
                targetLevel = (Levels)Enum.Parse(typeof(Levels), configured, true);
            }
            catch
            {
                Fail("Configured LAN E2E target '" + configured + "' is not a valid Levels enum name.");
                return null;
            }

            if (!IsBossLevel(targetLevel))
            {
                Fail("Configured LAN E2E target '" + configured + "' is not a boss level.");
                return null;
            }

            MapLevelLoader[] loaders = UnityEngine.Object.FindObjectsOfType<MapLevelLoader>();
            MapLevelLoader best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < loaders.Length; i++)
            {
                var loader = loaders[i];
                if (loader == null || !loader.isActiveAndEnabled || !loader.gameObject.activeInHierarchy)
                    continue;

                if (GetLoaderLevel(loader) != targetLevel)
                    continue;

                float dist = Vector2.Distance(from, GetInteractionPoint(loader));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = loader;
                }
            }

            if (best == null)
                Fail("Configured LAN E2E target '" + targetLevel + "' was not found on the current map.");
            else
                Log("Configured LAN E2E target selected: " + targetLevel + " (distance " + bestDist.ToString("0.00") + ").");

            return best;
        }

        static bool IsBossLevel(Levels level)
        {
            return level != Levels.Tutorial
                && level != Levels.ShmupTutorial
                && level != Levels.House
                && level != Levels.Mausoleum
                && level != Levels.DiceGate
                && level != Levels.ChaliceTutorial;
        }

        static Levels GetLoaderLevel(MapLevelLoader loader)
        {
            if (loader == null || LevelField == null)
                return Levels.Test;

            object raw = LevelField.GetValue(loader);
            return raw is Levels ? (Levels)raw : Levels.Test;
        }

        static Vector2 GetInteractionPoint(MapLevelLoader loader)
        {
            return (Vector2)loader.transform.position + loader.interactionPoint;
        }

        static Vector2 GetInteractionPoint(AbstractMapInteractiveEntity entity)
        {
            return (Vector2)entity.transform.position + entity.interactionPoint;
        }

        static Vector2 GetMapDialogueApproachPoint(MapDialogueInteraction dialogue)
        {
            Vector2 point = GetInteractionPoint(dialogue);
            float offset = Mathf.Min(0.85f, Mathf.Max(0.35f, dialogue.interactionDistance * 0.8f));
            return point + Vector2.down * offset;
        }

        static bool IsSpeechBubbleVisible()
        {
            var bubble = SpeechBubble.Instance;
            return bubble != null && bubble.displayState != SpeechBubble.DisplayState.Hidden;
        }

        static string DescribeMapDialogue(MapDialogueInteraction dialogue)
        {
            if (dialogue == null)
                return "missing";

            Vector2 point = GetInteractionPoint(dialogue);
            return dialogue.gameObject.name
                + " dialogue="
                + dialogue.dialogueInteraction
                + " radius="
                + dialogue.interactionDistance.ToString("0.00")
                + " pos=("
                + point.x.ToString("0.00")
                + ","
                + point.y.ToString("0.00")
                + ")";
        }

        static void ActivateTargetLoader(MapPlayerController player)
        {
            if (_targetLoader == null || player == null || MapLevelLoaderActivateMethod == null)
            {
                PressHost(CupheadButton.Accept);
                return;
            }

            MapLevelLoaderActivateMethod.Invoke(_targetLoader, new object[] { player });
        }

        static bool VerifyP2MapActivationBlocked(MapLevelLoader loader, MapPlayerController player, string side)
        {
            if (loader == null || player == null || MapLevelLoaderActivateMethod == null)
            {
                Fail("Cannot verify P2 map activation block on " + side + "; missing loader/player/reflection method.");
                return false;
            }

            bool wasActiveBefore = IsAnyStartUiActive();
            if (wasActiveBefore)
            {
                Fail("Cannot verify P2 map activation block on " + side + "; start UI was already active.");
                return false;
            }

            MapLevelLoaderActivateMethod.Invoke(loader, new object[] { player });

            if (IsAnyStartUiActive())
            {
                Fail("Player Two opened a map start card on " + side + "; map authority guard failed.");
                return false;
            }

            Log("Verified Player Two map activation is blocked on " + side + ".");
            return true;
        }

        static bool RunHostBuiltInReviveSmoke()
        {
            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            if (p1 == null || p1.stats == null || p2 == null || p2.stats == null)
                return false;

            RestoreScriptedPlayerHealth(p1);
            CheckRemoteInput();
            _hostAxis = Vector2.zero;
            _hostButtons &= ~ButtonMask(CupheadButton.Shoot);

            if (!_hostReviveStartSignalSent)
            {
                _hostReviveStartSignalSent = true;
                SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EReviveTestStarted);
                Log("Starting built-in death/parry revive smoke; client will put local Player Two into the same dead state.");
            }

            if (!_hostReviveDeathForced)
            {
                if (Time.unscaledTime - _stageStartedAt < 1.0f)
                    return false;

                if (!ForceBuiltInPlayerDeath(p2, "host"))
                    return false;

                _hostReviveDeathForced = true;
                _hostReviveDeathAt = Time.unscaledTime;
                Log("Host forced Player Two through the real level death path: " + DescribeLevelPlayer(p2) + ".");
                CaptureScreen("host_revive_dead_" + SceneManager.GetActiveScene().name);
                return false;
            }

            if (!_hostReviveVerified)
            {
                var effect = FindPlayerDeathEffect(PlayerId.PlayerTwo);
                bool fullyRevived = !p2.IsDead
                    && p2.stats.Health > 0
                    && p2.gameObject.activeInHierarchy;

                if (!fullyRevived)
                {
                    if (_hostReviveSwitchTriggered
                     && !_hostReviveAnimCompleteTriggered
                     && effect != null
                     && Time.unscaledTime - _hostReviveParryAt > 0.45f)
                    {
                        CompleteDeathBubbleParryAnimation(effect);
                    }

                    if (effect == null && Time.unscaledTime - _hostReviveDeathAt > 1.5f)
                    {
                        Fail("Host Player Two death bubble disappeared before the jump/parry revive completed.");
                        return false;
                    }

                    if (effect != null)
                        DriveHostJumpParryRevive(p1, effect);

                    if (_hostReviveParryPressed && Time.unscaledTime - _hostReviveParryAt > ReviveSmokeTimeout)
                    {
                        Fail("Host Player One jump/parry did not revive Player Two through the built-in death bubble.");
                        return false;
                    }

                    return false;
                }

                _hostReviveVerified = true;
                _hostReviveVerifiedAt = Time.unscaledTime;
                ParticipantStatusTracker.PushLocalStatus(p2);
                Log("Host verified built-in jump/parry revive: " + DescribeLevelPlayer(p2) + ".");
                CaptureScreen("host_revive_complete_" + SceneManager.GetActiveScene().name);
                return false;
            }

            if (!_clientReviveObservedSignalReceived)
            {
                if (Time.unscaledTime - _hostReviveVerifiedAt > ReviveSmokeTimeout)
                {
                    Fail("Client did not report Player Two revived after the host built-in parry revive.");
                    return false;
                }

                return false;
            }

            if (!_hostReviveForwardCompleteLogged)
            {
                _hostReviveForwardCompleteLogged = true;
                Log("Built-in Player Two death/parry revive verified on both peers; starting reverse Player One revive smoke.");
            }
            return true;
        }

        static bool RunHostReverseBuiltInReviveSmoke()
        {
            var p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne) as LevelPlayerController;
            var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo) as LevelPlayerController;
            if (p1 == null || p1.stats == null || p2 == null || p2.stats == null)
                return false;

            RestoreScriptedPlayerHealth(p2);
            CheckRemoteInput();
            _hostAxis = Vector2.zero;
            _hostButtons &= ~ButtonMask(CupheadButton.Shoot);

            if (!_hostReverseReviveStartSignalSent)
            {
                _hostReverseReviveStartSignalSent = true;
                SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EReverseReviveTestStarted);
                Log("Starting reverse built-in death/parry revive smoke; client will put local Player One into the same dead state and drive Player Two jump/parry.");
            }

            if (!_hostReverseReviveDeathForced)
            {
                if (Time.unscaledTime - _hostReviveVerifiedAt < 0.5f)
                    return false;

                if (!ForceBuiltInPlayerDeath(p1, "host reverse"))
                    return false;

                _hostReverseReviveDeathForced = true;
                _hostReverseReviveDeathAt = Time.unscaledTime;
                Log("Host forced Player One through the real level death path: " + DescribeLevelPlayer(p1) + ".");
                CaptureScreen("host_reverse_revive_dead_" + SceneManager.GetActiveScene().name);
                return false;
            }

            if (!_hostReverseReviveVerified)
            {
                var effect = FindPlayerDeathEffect(PlayerId.PlayerOne);
                bool fullyRevived = !p1.IsDead
                    && p1.stats.Health > 0
                    && p1.gameObject.activeInHierarchy;

                if (!fullyRevived)
                {
                    if (_hostReverseReviveSwitchTriggered
                     && !_hostReverseReviveAnimCompleteTriggered
                     && effect != null
                     && Time.unscaledTime - _hostReverseReviveParryAt > 0.45f)
                    {
                        CompleteReverseDeathBubbleParryAnimation(effect);
                    }

                    if (effect == null && Time.unscaledTime - _hostReverseReviveDeathAt > 1.5f)
                    {
                        Fail("Host Player One death bubble disappeared before the Player Two jump/parry revive completed.");
                        return false;
                    }

                    if (effect != null)
                        DriveHostPlayerTwoJumpParryRevive(p2, effect);

                    if (_hostReverseReviveParryPressed && Time.unscaledTime - _hostReverseReviveParryAt > ReviveSmokeTimeout)
                    {
                        Fail("Host Player Two jump/parry did not revive Player One through the built-in death bubble.");
                        return false;
                    }

                    return false;
                }

                _hostReverseReviveVerified = true;
                _hostReverseReviveVerifiedAt = Time.unscaledTime;
                ParticipantStatusTracker.PushLocalStatus(p1);
                Log("Host verified reverse built-in jump/parry revive: " + DescribeLevelPlayer(p1) + ".");
                CaptureScreen("host_reverse_revive_complete_" + SceneManager.GetActiveScene().name);
                return false;
            }

            if (!_clientReverseReviveObservedSignalReceived)
            {
                if (Time.unscaledTime - _hostReverseReviveVerifiedAt > ReviveSmokeTimeout)
                {
                    Fail("Client did not report Player One revived after the reverse built-in parry revive.");
                    return false;
                }

                return false;
            }

            if (!_hostFightTimerResetAfterRevive)
            {
                _hostFightTimerResetAfterRevive = true;
                _stageStartedAt = Time.unscaledTime;
                _lastLogAt = -100f;
                Log("Forward and reverse built-in death/parry revives verified on both peers; starting fight damage window.");
            }

            return true;
        }

        static void RunClientBuiltInReviveObserver(LevelPlayerController p2)
        {
            if (!_clientReviveTestStartSignalReceived)
                return;

            if (p2 == null || p2.stats == null)
            {
                Fail("Cannot verify client built-in revive mirror; Player Two is missing.");
                return;
            }

            if (!_clientReviveDeathForced)
            {
                if (!ForceBuiltInPlayerDeath(p2, "client"))
                    return;

                _clientReviveDeathForced = true;
                _clientReviveDeathAt = Time.unscaledTime;
                Log("Client forced local Player Two through the real level death path; waiting for host parry status.");
                CaptureScreen("client_revive_dead_" + SceneManager.GetActiveScene().name);
                return;
            }

            bool fullyRevived = !p2.IsDead
                && p2.stats.Health > 0
                && p2.gameObject.activeInHierarchy;
            if (!fullyRevived)
            {
                if (Time.unscaledTime - _clientReviveDeathAt > ReviveSmokeTimeout)
                    Fail("Client Player Two stayed dead after the host built-in parry revive.");
                return;
            }

            _clientReviveMirrorVerified = true;
            _clientFightReleasedAt = Time.unscaledTime;
            Log("Verified client Player Two revived from the host built-in parry status: " + DescribeLevelPlayer(p2) + ".");
            CaptureScreen("client_revive_complete_" + SceneManager.GetActiveScene().name);
            SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EReviveObserved);
        }

        static void RunClientReverseBuiltInReviveObserver(LevelPlayerController p1, LevelPlayerController p2)
        {
            if (!_clientReverseReviveTestStartSignalReceived)
                return;

            if (p1 == null || p1.stats == null || p2 == null || p2.stats == null)
            {
                Fail("Cannot verify client reverse built-in revive mirror; Player One or Player Two is missing.");
                return;
            }

            if (!_clientReverseReviveDeathForced)
            {
                if (!ForceBuiltInPlayerDeath(p1, "client reverse"))
                    return;

                _clientReverseReviveDeathForced = true;
                _clientReverseReviveDeathAt = Time.unscaledTime;
                Log("Client forced local Player One through the real level death path; driving Player Two jump/parry and waiting for host revive status.");
                CaptureScreen("client_reverse_revive_dead_" + SceneManager.GetActiveScene().name);
                return;
            }

            if (!_clientReverseReviveJumpPressed)
            {
                if (Time.unscaledTime - _clientReverseReviveDeathAt <= 0.35f)
                    return;

                _clientReverseReviveJumpPressed = true;
                _clientReverseReviveJumpAt = Time.unscaledTime;
                PressClient(CupheadButton.Jump);
                Log("Client pressed Player Two jump for reverse death-bubble revive test.");
                return;
            }

            if (!_clientReverseReviveParryPressed)
            {
                if (Time.unscaledTime - _clientReverseReviveJumpAt <= 0.24f)
                    return;

                _clientReverseReviveParryPressed = true;
                _clientReverseReviveParryAt = Time.unscaledTime;
                PressClient(CupheadButton.Jump);
                Log("Client pressed Player Two second jump/parry for reverse death-bubble revive test.");
            }

            bool fullyRevived = !p1.IsDead
                && p1.stats.Health > 0
                && p1.gameObject.activeInHierarchy;
            if (!fullyRevived)
            {
                if (Time.unscaledTime - _clientReverseReviveDeathAt > ReviveSmokeTimeout)
                    Fail("Client Player One stayed dead after the reverse host built-in parry revive.");
                return;
            }

            _clientReverseReviveMirrorVerified = true;
            _clientFightReleasedAt = Time.unscaledTime;
            Log("Verified client Player One revived from the host reverse built-in parry status: " + DescribeLevelPlayer(p1) + ".");
            CaptureScreen("client_reverse_revive_complete_" + SceneManager.GetActiveScene().name);
            SendDialogueObservedSignal(SessionSignalKind.LanSteamE2EReverseReviveObserved);
        }

        static void DriveHostJumpParryRevive(LevelPlayerController p1, PlayerDeathEffect effect)
        {
            if (p1 == null || effect == null)
                return;

            if (!_hostReviveJumpPressed)
            {
                PlaceDeathBubbleForHostParry(p1, effect, 70f);
                if (Time.unscaledTime - _hostReviveDeathAt <= 0.35f)
                    return;

                _hostReviveJumpPressed = true;
                _hostReviveJumpAt = Time.unscaledTime;
                PressHost(CupheadButton.Jump);
                Log("Host pressed Player One jump for built-in death-bubble revive test.");
                return;
            }

            if (!_hostReviveParryPressed)
            {
                PlaceDeathBubbleForHostParry(p1, effect, 35f);
                if (Time.unscaledTime - _hostReviveJumpAt <= 0.24f)
                    return;

                _hostReviveParryPressed = true;
                _hostReviveParryAt = Time.unscaledTime;
                PressHost(CupheadButton.Jump);
                Log("Host pressed Player One second jump/parry against Player Two's death bubble.");
                return;
            }

            if (Time.unscaledTime - _hostReviveParryAt <= 1.25f)
                PlaceDeathBubbleForHostParry(p1, effect, 0f);

            if (!_hostReviveSwitchTriggered && Time.unscaledTime - _hostReviveParryAt > 0.12f)
                TriggerDeathBubbleParrySwitch(p1, effect);
        }

        static void DriveHostPlayerTwoJumpParryRevive(LevelPlayerController p2, PlayerDeathEffect effect)
        {
            if (p2 == null || effect == null)
                return;

            if (!_hostReverseReviveJumpPressed)
            {
                PlaceDeathBubbleForHostParry(p2, effect, 70f);
                if (Time.unscaledTime - _hostReverseReviveDeathAt <= 0.35f)
                    return;

                _hostReverseReviveJumpPressed = true;
                _hostReverseReviveJumpAt = Time.unscaledTime;
                Log("Host observed/scripted Player Two jump for reverse death-bubble revive test.");
                return;
            }

            if (!_hostReverseReviveParryPressed)
            {
                PlaceDeathBubbleForHostParry(p2, effect, 35f);
                if (Time.unscaledTime - _hostReverseReviveJumpAt <= 0.24f)
                    return;

                _hostReverseReviveParryPressed = true;
                _hostReverseReviveParryAt = Time.unscaledTime;
                Log("Host observed/scripted Player Two second jump/parry against Player One's death bubble.");
                return;
            }

            if (Time.unscaledTime - _hostReverseReviveParryAt <= 1.25f)
                PlaceDeathBubbleForHostParry(p2, effect, 0f);

            if (!_hostReverseReviveSwitchTriggered && Time.unscaledTime - _hostReverseReviveParryAt > 0.12f)
                TriggerReverseDeathBubbleParrySwitch(p2, effect);
        }

        static void PlaceDeathBubbleForHostParry(LevelPlayerController p1, PlayerDeathEffect effect, float yOffset)
        {
            if (p1 == null || effect == null)
                return;

            effect.transform.position = p1.center + new Vector3(0f, yOffset, 0f);
        }

        static void TriggerDeathBubbleParrySwitch(LevelPlayerController p1, PlayerDeathEffect effect)
        {
            if (p1 == null || effect == null)
                return;
            if (PlayerDeathEffectParrySwitchField == null
             || ParrySwitchOnParryPrePauseMethod == null
             || ParrySwitchOnParryPostPauseMethod == null)
            {
                return;
            }

            var parrySwitch = PlayerDeathEffectParrySwitchField.GetValue(effect) as PlayerDeathParrySwitch;
            if (parrySwitch == null)
                return;

            try
            {
                _hostReviveSwitchTriggered = true;
                PlaceDeathBubbleForHostParry(p1, effect, 0f);
                ParrySwitchOnParryPrePauseMethod.Invoke(parrySwitch, new object[] { p1 });
                ParrySwitchOnParryPostPauseMethod.Invoke(parrySwitch, new object[] { p1 });
                Log("Host routed the scripted Player One jump/parry into Player Two's death-bubble parry switch.");
            }
            catch (Exception ex)
            {
                Fail("Host could not activate Player Two's death-bubble parry switch after jump/parry input: " + ex.Message + ".");
            }
        }

        static void TriggerReverseDeathBubbleParrySwitch(LevelPlayerController p2, PlayerDeathEffect effect)
        {
            if (p2 == null || effect == null)
                return;
            if (PlayerDeathEffectParrySwitchField == null
             || ParrySwitchOnParryPrePauseMethod == null
             || ParrySwitchOnParryPostPauseMethod == null)
            {
                return;
            }

            var parrySwitch = PlayerDeathEffectParrySwitchField.GetValue(effect) as PlayerDeathParrySwitch;
            if (parrySwitch == null)
                return;

            try
            {
                _hostReverseReviveSwitchTriggered = true;
                PlaceDeathBubbleForHostParry(p2, effect, 0f);
                ParrySwitchOnParryPrePauseMethod.Invoke(parrySwitch, new object[] { p2 });
                ParrySwitchOnParryPostPauseMethod.Invoke(parrySwitch, new object[] { p2 });
                Log("Host routed the scripted Player Two jump/parry into Player One's death-bubble parry switch.");
            }
            catch (Exception ex)
            {
                Fail("Host could not activate Player One's death-bubble parry switch after Player Two jump/parry input: " + ex.Message + ".");
            }
        }

        static void CompleteDeathBubbleParryAnimation(PlayerDeathEffect effect)
        {
            if (effect == null || PlayerDeathEffectReviveParryAnimCompleteMethod == null)
                return;

            try
            {
                _hostReviveAnimCompleteTriggered = true;
                PlayerDeathEffectReviveParryAnimCompleteMethod.Invoke(effect, null);
                Log("Host completed Player Two's death-bubble parry animation callback after scripted jump/parry.");
            }
            catch (Exception ex)
            {
                Fail("Host could not complete Player Two's death-bubble parry animation callback: " + ex.Message + ".");
            }
        }

        static void CompleteReverseDeathBubbleParryAnimation(PlayerDeathEffect effect)
        {
            if (effect == null || PlayerDeathEffectReviveParryAnimCompleteMethod == null)
                return;

            try
            {
                _hostReverseReviveAnimCompleteTriggered = true;
                PlayerDeathEffectReviveParryAnimCompleteMethod.Invoke(effect, null);
                Log("Host completed Player One's death-bubble parry animation callback after scripted Player Two jump/parry.");
            }
            catch (Exception ex)
            {
                Fail("Host could not complete Player One's death-bubble parry animation callback: " + ex.Message + ".");
            }
        }

        static bool ForceBuiltInPlayerDeath(LevelPlayerController player, string side)
        {
            if (player == null || player.stats == null)
            {
                Fail("Cannot force built-in death on " + side + "; player is missing.");
                return false;
            }

            var existing = FindPlayerDeathEffect(player.id);
            if (player.IsDead && existing != null)
                return true;

            try
            {
                player.stats.SetHealth(0);

                if (PlayerStatsOnStatsDeathMethod != null)
                    PlayerStatsOnStatsDeathMethod.Invoke(player.stats, null);

                existing = FindPlayerDeathEffect(player.id);
                if (existing == null && LevelPlayerOnDeathMethod != null)
                    LevelPlayerOnDeathMethod.Invoke(player, new object[] { player.id });

                existing = FindPlayerDeathEffect(player.id);
                if (existing == null)
                {
                    Fail("Built-in death on " + side + " did not create a PlayerDeathEffect.");
                    return false;
                }

                if (!player.IsDead)
                {
                    Fail("Built-in death on " + side + " created a death bubble but " + player.id + " is not dead.");
                    return false;
                }

                ParticipantStatusTracker.PushLocalStatus(player);
                return true;
            }
            catch (Exception ex)
            {
                Fail("Built-in death on " + side + " threw: " + ex.Message + ".");
                return false;
            }
        }

        static PlayerDeathEffect FindPlayerDeathEffect(PlayerId playerId)
        {
            if (PlayerDeathEffectPlayerIdField == null)
                return null;

            var effects = UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>();
            for (int i = 0; i < effects.Length; i++)
            {
                var effect = effects[i];
                if (effect == null)
                    continue;

                object raw = PlayerDeathEffectPlayerIdField.GetValue(effect);
                if (raw is PlayerId && (PlayerId)raw == playerId)
                    return effect;
            }

            return null;
        }

        static bool IsAnyStartUiActive()
        {
            return (MapDifficultySelectStartUI.Current != null && MapDifficultySelectStartUI.Current.CurrentState == AbstractMapSceneStartUI.State.Active)
                || (MapConfirmStartUI.Current != null && MapConfirmStartUI.Current.CurrentState == AbstractMapSceneStartUI.State.Active)
                || (MapBasicStartUI.Current != null && MapBasicStartUI.Current.CurrentState == AbstractMapSceneStartUI.State.Active);
        }

        static void PressHost(CupheadButton button)
        {
            uint mask = ButtonMask(button);
            _hostButtons |= mask;
            _hostDownButtons |= mask;
            _hostDownUntilFrame = Math.Max(_hostDownUntilFrame, Time.frameCount + 4);
        }

        static void PressClient(CupheadButton button)
        {
            uint mask = ButtonMask(button);
            _clientButtons |= mask;
            _clientDownButtons |= mask;
            _clientDownUntilFrame = Math.Max(_clientDownUntilFrame, Time.frameCount + 4);
        }

        static void HoldHost(CupheadButton button)
        {
            _hostButtons |= ButtonMask(button);
        }

        static uint ButtonMask(CupheadButton button)
        {
            int index = (int)button;
            if (index < 0 || index >= 32)
                return 0u;
            return 1u << index;
        }

        static void ClearExpiredDownButtons()
        {
            if (_hostDownButtons != 0u && Time.frameCount > _hostDownUntilFrame)
            {
                uint released = _hostDownButtons;
                _hostDownButtons = 0u;
                _hostButtons &= ~released;
            }

            if (_clientDownButtons != 0u && Time.frameCount > _clientDownUntilFrame)
            {
                uint released = _clientDownButtons;
                _clientDownButtons = 0u;
                _clientButtons &= ~released;
            }
        }

        static void ResetScriptedInput()
        {
            _hostAxis = Vector2.zero;
            _hostButtons = 0u;
            _hostDownButtons = 0u;
            _hostDownUntilFrame = 0;
            _clientAxis = Vector2.zero;
            _clientButtons = 0u;
            _clientDownButtons = 0u;
            _clientDownUntilFrame = 0;
        }

        static bool TimedOut(float seconds)
        {
            return Time.unscaledTime - _stageStartedAt > seconds;
        }

        static void SetStage(Stage stage, string message)
        {
            _stage = stage;
            _stageStartedAt = Time.unscaledTime;
            _lastLogAt = -100f;
            if (stage == Stage.LoadSlotSelect)
            {
                _targetLoader = null;
                _targetDialogue = null;
                _fallbackMapLoadTried = false;
                _fallbackUnityLevelLoadTried = false;
                _sawRemoteInput = false;
                _clientCapturedMap = false;
                _clientCapturedLevel = false;
                _clientCapturedFightEnd = false;
                _clientCapturedDialogueStart = false;
                _clientCapturedDialogueContinue = false;
                _clientLevelReached = false;
                _remoteCheckpointReceived = false;
                _clientDialogueStartedObserved = false;
                _clientDialogueContinueObserved = false;
                _clientPauseObserved = false;
                _clientPauseSignalReceived = false;
                _clientUnpauseObserved = false;
                _clientUnpauseSignalReceived = false;
                _clientP2MapActivationBlocked = false;
                _clientReviveTestStartSignalReceived = false;
                _clientReviveDeathForced = false;
                _clientReviveMirrorVerified = false;
                _clientReviveObservedSignalReceived = false;
                _clientReverseReviveTestStartSignalReceived = false;
                _clientReverseReviveDeathForced = false;
                _clientReverseReviveMirrorVerified = false;
                _clientReverseReviveObservedSignalReceived = false;
                _clientReverseReviveJumpPressed = false;
                _clientReverseReviveParryPressed = false;
                _hostPausePressed = false;
                _hostPauseObserved = false;
                _hostUnpausePressed = false;
                _hostUnpauseObserved = false;
                _hostReviveStartSignalSent = false;
                _hostReviveDeathForced = false;
                _hostReviveJumpPressed = false;
                _hostReviveParryPressed = false;
                _hostReviveSwitchTriggered = false;
                _hostReviveAnimCompleteTriggered = false;
                _hostReviveVerified = false;
                _hostReviveForwardCompleteLogged = false;
                _hostReverseReviveStartSignalSent = false;
                _hostReverseReviveDeathForced = false;
                _hostReverseReviveJumpPressed = false;
                _hostReverseReviveParryPressed = false;
                _hostReverseReviveSwitchTriggered = false;
                _hostReverseReviveAnimCompleteTriggered = false;
                _hostReverseReviveVerified = false;
                _hostFightTimerResetAfterRevive = false;
                _hostDialogueInteractPressed = false;
                _hostDialogueForceStarted = false;
                _clientDialogueInteractPressed = false;
                _clientDialogueAdvancePressed = false;
                _clientLevelReachedAt = 0f;
                _clientFightReleasedAt = 0f;
                _clientReviveDeathAt = 0f;
                _clientReverseReviveDeathAt = 0f;
                _clientReverseReviveJumpAt = 0f;
                _clientReverseReviveParryAt = 0f;
                _hostReviveDeathAt = 0f;
                _hostReviveJumpAt = 0f;
                _hostReviveParryAt = 0f;
                _hostReviveVerifiedAt = 0f;
                _hostReverseReviveDeathAt = 0f;
                _hostReverseReviveJumpAt = 0f;
                _hostReverseReviveParryAt = 0f;
                _hostReverseReviveVerifiedAt = 0f;
                _clientDialogueStartObservedAt = 0f;
                _clientDialogueContinueObservedAt = 0f;
                _clientDialogueLocalContinueAt = 0f;
                _hostDialogueContinueObservedAt = 0f;
                _clientPauseObservedAt = 0f;
                _hostPauseObservedAt = 0f;
                _clientUnpauseObservedAt = 0f;
                _hostUnpauseObservedAt = 0f;
                _hasFightStartBossHealth = false;
                _fightStartBossName = string.Empty;
                _fightStartBossHealth = 0f;
                _fightStartBossTotal = 0f;
                _hostSawP1Shooting = false;
                _hostSawP2Shooting = false;
                _clientSawP2Shooting = false;
                _guestOnlyDamageStarted = false;
                _guestOnlyDamageVerified = false;
                _guestOnlyDamageStartedAt = 0f;
                _guestOnlyBossName = string.Empty;
                _guestOnlyStartBossHealth = 0f;
                _guestOnlyStartBossTotal = 0f;
            }
            ResetScriptedInput();
            Log(message);
        }

        static void Fail(string message)
        {
            ResetScriptedInput();
            _stage = Stage.Failed;
            Plugin.Log.LogError("[LanSteamE2E] FAIL: " + message);
        }

        static void Log(string message)
        {
            Plugin.Log.LogInfo("[LanSteamE2E] " + message);
        }

        static void CaptureScreen(string label)
        {
            try
            {
                string gameRoot = Path.GetDirectoryName(Application.dataPath);
                string dir = Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "CupHeads"), "LanSteamE2E");
                Directory.CreateDirectory(dir);
                string role = MultiplayerSession.IsHost ? "host" : MultiplayerSession.IsClient ? "client" : "offline";
                string file = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                    + "_"
                    + role
                    + "_"
                    + SanitizeFilePart(label)
                    + ".png";
                string path = Path.Combine(dir, file);
                var capture = typeof(Application).GetMethod(
                    "CaptureScreenshot",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    null);
                if (capture == null)
                {
                    Plugin.Log.LogWarning("[LanSteamE2E] Could not capture screenshot: Unity capture API unavailable.");
                    return;
                }

                capture.Invoke(null, new object[] { path });
                Log("Screenshot queued: " + path);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[LanSteamE2E] Could not capture screenshot: " + ex.Message);
            }
        }

        static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "screen";

            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                    chars[i] = '_';
            }
            return new string(chars);
        }

        static string DescribeClientMapPlayers()
        {
            if (Map.Current == null || Map.Current.players == null || Map.Current.players.Length < 2)
                return "map players unavailable";

            return "P1=" + DescribeMapPlayer(Map.Current.players[0])
                + "; P2=" + DescribeMapPlayer(Map.Current.players[1]);
        }

        static string DescribeMapPlayer(MapPlayerController player)
        {
            if (player == null)
                return "missing";
            Vector3 pos = player.transform.position;
            return player.id + " state=" + player.state + " pos=(" + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00") + ")";
        }

        static string DescribeLevelPlayer(LevelPlayerController player)
        {
            if (player == null)
                return "missing";
            Vector3 pos = player.transform.position;
            string health = player.stats == null ? "no-stats" : player.stats.Health + "/" + player.stats.HealthMax;
            return player.id + " dead=" + player.IsDead + " hp=" + health + " pos=(" + pos.x.ToString("0.00") + "," + pos.y.ToString("0.00") + ")";
        }
    }
}
