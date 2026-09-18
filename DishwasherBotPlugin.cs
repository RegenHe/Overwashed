using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace Overcooked2DishwasherBot
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DishwasherBotPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.overcooked2.dishwasherbot";
        public const string PluginName = "Overcooked 2 Dishwasher Bot";
        public const string PluginVersion = "1.1.0";

        private static readonly FieldInfo ClientSinkPlateCount = typeof(ClientWashingStation).GetField(
            "m_plateCount",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo SendChefEvent = ResolveChefEventSender();

        private readonly GridPathNavigator _navigator = new GridPathNavigator();
        private readonly HashSet<string> _reportedErrors = new HashSet<string>();
        private readonly List<Vector3> _avoidanceThreats = new List<Vector3>();

        private ManualLogSource _log;
        private ConfigEntry<bool> _autoAvoidance;
        private ConfigEntry<float> _avoidanceDistance;
        private bool _enabled;
        private bool _showSettings;
        private bool _avoidingPlayers;
        private PlayerControls _player;
        private ClientPlayerAttachmentCarrier _carrier;
        private BotInputBinding _input;
        private ClientDirtyPlateStack _dirtyTarget;
        private ClientWashingStation _sinkTarget;
        private BotState _state = BotState.Disabled;
        private float _pickupDownUntil;
        private float _placementPendingUntil;
        private float _nextActionTime;
        private float _nextAcquireTime;
        private float _nextDirtyScanTime;
        private float _nextSinkScanTime;
        private float _nextDropRequestTime;
        private float _dirtyInteractionCellSince;
        private int _droppingItemId;
        private GameObject _lastDirtyInteractionTarget;
        private Texture2D _statusBackground;
        private Texture2D _statusIcon;

        private enum BotState
        {
            Disabled,
            FindingPlayer,
            FindingDirtyPlates,
            MovingToDirtyPlates,
            PickingUp,
            DroppingUnexpectedItem,
            MovingToSink,
            PlacingInSink,
            Washing,
            AvoidingPlayers,
            Waiting
        }

        private void Awake()
        {
            _log = Logger;
            _autoAvoidance = Config.Bind(
                "Avoidance",
                "Enabled",
                true,
                "Move the dishwasher bot away when another chef comes too close.");
            _avoidanceDistance = Config.Bind(
                "Avoidance",
                "Distance",
                1.5f,
                new ConfigDescription(
                    "Distance in grid tiles at which the bot starts avoiding another chef.",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            CreateStatusBadgeTextures();
            _log.LogInfo(PluginName + " " + PluginVersion + " loaded. Press F8 to toggle; F7 opens settings.");
            if (ClientSinkPlateCount == null)
            {
                _log.LogWarning("ClientWashingStation.m_plateCount was not found; sink completion will use interaction state only.");
            }
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    SetBotEnabled(!_enabled);
                }
                if (Input.GetKeyDown(KeyCode.F7))
                {
                    _showSettings = !_showSettings;
                }

                if (!_enabled)
                {
                    return;
                }

                if (!EnsureKeyboardPlayer())
                {
                    ReleaseRobotInputs(false);
                    SetState(BotState.FindingPlayer);
                    return;
                }

                UpdatePickupPulse();
                TickBot();
            }
            catch (Exception exception)
            {
                ReleaseRobotInputs(false);
                ReportErrorOnce(
                    "update:" + exception.GetType().FullName + ":" + exception.Message,
                    "Dishwasher bot update failed: " + exception);
            }
        }

        private void OnDisable()
        {
            ShutdownBinding();
        }

        private void OnDestroy()
        {
            ShutdownBinding();
            DestroyStatusBadgeTextures();
        }

        private void OnGUI()
        {
            int previousDepth = GUI.depth;
            Color previousColor = GUI.color;

            if (_enabled && _statusBackground != null && _statusIcon != null)
            {
                float badgeLeft = Screen.width - 44f;
                Rect badgeRect = new Rect(badgeLeft, 12f, 32f, 32f);
                GUI.depth = -1000;
                GUI.color = Color.white;
                GUI.DrawTexture(badgeRect, _statusBackground, ScaleMode.StretchToFill, true);
                GUI.DrawTexture(new Rect(badgeLeft + 6f, 18f, 20f, 20f), _statusIcon, ScaleMode.ScaleToFit, true);

                Event currentEvent = Event.current;
                if (currentEvent != null
                    && currentEvent.type == EventType.MouseDown
                    && currentEvent.button == 0
                    && badgeRect.Contains(currentEvent.mousePosition))
                {
                    _showSettings = !_showSettings;
                    currentEvent.Use();
                }
            }

            if (_showSettings)
            {
                DrawSettingsPanel();
            }

            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }

        private void DrawSettingsPanel()
        {
            const float width = 286f;
            const float height = 142f;
            float left = Mathf.Max(8f, Screen.width - width - 12f);
            Rect panel = new Rect(left, 52f, width, height);

            GUI.depth = -1001;
            GUI.color = Color.white;
            GUI.Box(panel, "Dishwasher Bot Settings");

            bool enabled = GUI.Toggle(
                new Rect(panel.x + 16f, panel.y + 32f, panel.width - 32f, 22f),
                _autoAvoidance.Value,
                "Auto avoidance");
            if (enabled != _autoAvoidance.Value)
            {
                _autoAvoidance.Value = enabled;
                if (!enabled)
                {
                    StopAvoidingPlayers();
                }
            }

            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 59f, panel.width - 32f, 22f),
                "Avoidance distance: " + _avoidanceDistance.Value.ToString("0.0") + " tiles");
            float distance = GUI.HorizontalSlider(
                new Rect(panel.x + 18f, panel.y + 85f, panel.width - 36f, 18f),
                _avoidanceDistance.Value,
                0.5f,
                4f);
            distance = Mathf.Round(distance * 10f) * 0.1f;
            if (Mathf.Abs(distance - _avoidanceDistance.Value) >= 0.05f)
            {
                _avoidanceDistance.Value = distance;
            }

            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 110f, panel.width - 32f, 22f),
                "F7: close settings");
        }

        private void CreateStatusBadgeTextures()
        {
            _statusBackground = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _statusBackground.name = "DishwasherBotStatusBackground";
            _statusBackground.hideFlags = HideFlags.HideAndDontSave;
            _statusBackground.SetPixel(0, 0, new Color(0.025f, 0.055f, 0.075f, 0.82f));
            _statusBackground.Apply(false, true);

            const int size = 20;
            Color32[] pixels = new Color32[size * size];
            Color32 bubble = new Color32(168, 232, 255, 255);
            Color32 rim = new Color32(235, 249, 255, 255);
            Color32 plate = new Color32(75, 174, 218, 255);
            Color32 active = new Color32(93, 224, 137, 255);

            DrawFilledCircle(pixels, size, 5, 16, 2, bubble);
            DrawFilledCircle(pixels, size, 11, 17, 1, bubble);
            DrawFilledCircle(pixels, size, 10, 8, 8, 5, rim);
            DrawFilledCircle(pixels, size, 10, 8, 5, 3, plate);
            DrawFilledCircle(pixels, size, 16, 4, 2, active);

            _statusIcon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            _statusIcon.name = "DishwasherBotStatusIcon";
            _statusIcon.hideFlags = HideFlags.HideAndDontSave;
            _statusIcon.filterMode = FilterMode.Point;
            _statusIcon.wrapMode = TextureWrapMode.Clamp;
            _statusIcon.SetPixels32(pixels);
            _statusIcon.Apply(false, true);
        }

        private static void DrawFilledCircle(Color32[] pixels, int width, int centerX, int centerY, int radius, Color32 color)
        {
            DrawFilledCircle(pixels, width, centerX, centerY, radius, radius, color);
        }

        private static void DrawFilledCircle(
            Color32[] pixels,
            int width,
            int centerX,
            int centerY,
            int radiusX,
            int radiusY,
            Color32 color)
        {
            int height = pixels.Length / width;
            int radiusXSquared = radiusX * radiusX;
            int radiusYSquared = radiusY * radiusY;
            int threshold = radiusXSquared * radiusYSquared;
            for (int y = centerY - radiusY; y <= centerY + radiusY; y++)
            {
                if (y < 0 || y >= height)
                {
                    continue;
                }
                for (int x = centerX - radiusX; x <= centerX + radiusX; x++)
                {
                    if (x < 0 || x >= width)
                    {
                        continue;
                    }
                    int deltaX = x - centerX;
                    int deltaY = y - centerY;
                    if (deltaX * deltaX * radiusYSquared + deltaY * deltaY * radiusXSquared <= threshold)
                    {
                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        private void DestroyStatusBadgeTextures()
        {
            if (_statusBackground != null)
            {
                Destroy(_statusBackground);
                _statusBackground = null;
            }
            if (_statusIcon != null)
            {
                Destroy(_statusIcon);
                _statusIcon = null;
            }
        }

        private void SetBotEnabled(bool enabled)
        {
            _enabled = enabled;
            _navigator.Clear();
            _dirtyTarget = null;
            _sinkTarget = null;
            _pickupDownUntil = 0f;
            _placementPendingUntil = 0f;
            _nextActionTime = 0f;
            _nextDirtyScanTime = 0f;
            _nextSinkScanTime = 0f;
            _nextDropRequestTime = 0f;
            _dirtyInteractionCellSince = 0f;
            _droppingItemId = 0;
            _lastDirtyInteractionTarget = null;
            _avoidingPlayers = false;
            _avoidanceThreats.Clear();

            if (enabled)
            {
                SetState(BotState.FindingPlayer);
                _log.LogInfo("Dishwasher bot ENABLED. Searching for a locally controlled keyboard chef.");
            }
            else
            {
                ReleaseRobotInputs(false);
                EndCurrentInteraction();
                ShutdownBinding();
                SetState(BotState.Disabled);
                _log.LogInfo("Dishwasher bot DISABLED. All robot input was released.");
            }
        }

        private bool EnsureKeyboardPlayer()
        {
            if (_player != null
                && _player.gameObject != null
                && _player.enabled
                && _player.PlayerIDProvider != null
                && _player.PlayerIDProvider.IsLocallyControlled()
                && IsKeyboard(_player.PlayerIDProvider.GetID())
                && _player.ControlScheme != null)
            {
                if (_input == null || !_input.IsInstalled)
                {
                    BindInput(_player);
                }
                return EnsureNetworkInput();
            }

            ShutdownBinding();
            _player = null;
            _carrier = null;
            if (Time.unscaledTime < _nextAcquireTime)
            {
                return false;
            }
            _nextAcquireTime = Time.unscaledTime + 0.75f;

            PlayerControls[] players = FindObjectsOfType<PlayerControls>();
            Array.Sort(players, delegate(PlayerControls left, PlayerControls right)
            {
                int leftId = left == null || left.PlayerIDProvider == null ? int.MaxValue : (int)left.PlayerIDProvider.GetID();
                int rightId = right == null || right.PlayerIDProvider == null ? int.MaxValue : (int)right.PlayerIDProvider.GetID();
                return leftId.CompareTo(rightId);
            });

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControls candidate = players[i];
                if (candidate == null || !candidate.enabled || candidate.PlayerIDProvider == null)
                {
                    continue;
                }

                PlayerIDProvider idProvider = candidate.PlayerIDProvider;
                if (!idProvider.IsLocallyControlled() || !IsKeyboard(idProvider.GetID()) || candidate.ControlScheme == null)
                {
                    continue;
                }

                ClientPlayerAttachmentCarrier carrier = candidate.GetComponent<ClientPlayerAttachmentCarrier>();
                if (carrier == null)
                {
                    continue;
                }

                _player = candidate;
                _carrier = carrier;
                BindInput(candidate);
                return EnsureNetworkInput();
            }

            return false;
        }

        private bool EnsureNetworkInput()
        {
            if (_input == null || _player == null)
            {
                return false;
            }

            string error;
            bool ready = _input.TryInstallNetworkInput(_player, out error);
            if (!ready && !string.IsNullOrEmpty(error))
            {
                ReportErrorOnce("network-input:" + error, "Could not bind bot input to the network transmitter: " + error);
            }
            return ready;
        }

        private static bool IsKeyboard(PlayerInputLookup.Player player)
        {
            try
            {
                return KeyboardUtils.IsKeyboard(player);
            }
            catch
            {
                return false;
            }
        }

        private void BindInput(PlayerControls player)
        {
            if (_input != null)
            {
                _input.Restore();
            }
            _input = new BotInputBinding(player.ControlScheme);
            _input.SetEnabled(true);
            _input.ReleaseAll();
        }

        private void TickBot()
        {
            if (_input == null || _carrier == null)
            {
                return;
            }

            _input.SetUse(false);
            SetMove(Vector3.zero);

            GameObject carried = _carrier.InspectCarriedItem();
            if (carried != null && carried.GetComponent<DirtyPlateStack>() == null)
            {
                DropUnexpectedItem(carried);
                return;
            }
            _droppingItemId = 0;

            if (TryAvoidPlayers())
            {
                return;
            }

            if (carried != null)
            {
                _dirtyTarget = null;
                if (!IsUsableSink(_sinkTarget))
                {
                    _sinkTarget = null;
                    if (Time.time >= _nextSinkScanTime)
                    {
                        _nextSinkScanTime = Time.time + 0.5f;
                        _sinkTarget = FindNearestSink();
                        _navigator.Clear();
                    }
                }

                if (_sinkTarget == null)
                {
                    Wait();
                    return;
                }

                MoveDirtyPlatesToSink(_sinkTarget);
                return;
            }

            if (Time.time < _placementPendingUntil
                && IsUsableSink(_sinkTarget)
                && GetPlateCount(_sinkTarget) <= 0)
            {
                SetState(BotState.PlacingInSink);
                return;
            }

            if ((!IsUsableSink(_sinkTarget) || GetPlateCount(_sinkTarget) <= 0)
                && Time.time >= _nextSinkScanTime)
            {
                _nextSinkScanTime = Time.time + 0.5f;
                ClientWashingStation loadedSink = FindSinkWithDirtyPlates();
                if (loadedSink != null && loadedSink != _sinkTarget)
                {
                    _sinkTarget = loadedSink;
                    _navigator.Clear();
                }
            }

            if (IsUsableSink(_sinkTarget) && GetPlateCount(_sinkTarget) > 0)
            {
                WashAtSink(_sinkTarget);
                return;
            }

            if (!IsUsableDirtyTarget(_dirtyTarget))
            {
                _dirtyTarget = null;
                if (Time.time >= _nextDirtyScanTime)
                {
                    _nextDirtyScanTime = Time.time + 0.5f;
                    _dirtyTarget = FindNearestDirtyStack();
                    _navigator.Clear();
                }
            }

            if (_dirtyTarget == null)
            {
                Wait();
                return;
            }

            MoveToDirtyPlates(_dirtyTarget);
        }

        private bool TryAvoidPlayers()
        {
            if (_autoAvoidance == null || !_autoAvoidance.Value || _player == null)
            {
                StopAvoidingPlayers();
                return false;
            }

            _avoidanceThreats.Clear();
            float triggerDistance = Mathf.Clamp(_avoidanceDistance.Value, 0.5f, 4f);
            float releaseDistance = triggerDistance + 0.35f;
            float nearestSqrDistance = float.PositiveInfinity;
            Vector3 botPosition = _player.transform.position;
            GridManager botGrid = GameUtils.GetGridManager(_player.transform);

            PlayerControls[] players = FindObjectsOfType<PlayerControls>();
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControls other = players[i];
                if (other == null
                    || other == _player
                    || !other.enabled
                    || !other.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 otherPosition = other.transform.position;
                if (Mathf.Abs(otherPosition.y - botPosition.y) > 1.1f)
                {
                    continue;
                }
                GridManager otherGrid = GameUtils.GetGridManager(other.transform);
                if (botGrid != null && otherGrid != null && botGrid != otherGrid)
                {
                    continue;
                }
                float sqrDistance = HorizontalSqrDistance(botPosition, otherPosition);
                if (sqrDistance < nearestSqrDistance)
                {
                    nearestSqrDistance = sqrDistance;
                }
                _avoidanceThreats.Add(otherPosition);
            }

            if (_avoidanceThreats.Count == 0)
            {
                StopAvoidingPlayers();
                return false;
            }

            float activeDistance = _avoidingPlayers ? releaseDistance : triggerDistance;
            if (nearestSqrDistance > activeDistance * activeDistance)
            {
                StopAvoidingPlayers();
                return false;
            }

            if (!_avoidingPlayers)
            {
                _avoidingPlayers = true;
                _navigator.Clear();
            }

            SetState(BotState.AvoidingPlayers);
            bool hasEscapePath;
            Vector3 direction = _navigator.DirectionAwayFrom(
                _player,
                _avoidanceThreats,
                releaseDistance,
                out hasEscapePath);
            SetMove(direction);
            return true;
        }

        private void StopAvoidingPlayers()
        {
            if (!_avoidingPlayers)
            {
                return;
            }

            _avoidingPlayers = false;
            _avoidanceThreats.Clear();
            _navigator.Clear();
        }

        private void DropUnexpectedItem(GameObject carried)
        {
            SetMove(Vector3.zero);
            _input.SetUse(false);
            _pickupDownUntil = 0f;
            _input.SetPickup(false);
            SetState(BotState.DroppingUnexpectedItem);

            int itemId = carried.GetInstanceID();
            if (_droppingItemId != itemId)
            {
                _droppingItemId = itemId;
                _nextDropRequestTime = 0f;
            }

            if (Time.time < _nextDropRequestTime)
            {
                return;
            }
            _nextDropRequestTime = Time.time + 0.75f;

            if (SendChefEvent == null)
            {
                ReportErrorOnce(
                    "drop-message-missing",
                    "Cannot drop the unexpected item because ClientMessenger.ChefEventMessage was not found.");
                return;
            }

            try
            {
                // This is the game's normal no-target placement path. The server handles
                // ChefEventType.Take by detaching the carried object so it falls to the floor.
                SendChefEvent.Invoke(
                    null,
                    new object[]
                    {
                        ChefEventMessage.ChefEventType.Take,
                        _player.gameObject,
                        null
                    });
            }
            catch (Exception exception)
            {
                ReportErrorOnce(
                    "drop:" + exception.GetType().FullName + ":" + exception.Message,
                    "Could not drop unexpected carried item: " + exception);
            }
        }

        private void MoveToDirtyPlates(ClientDirtyPlateStack dirty)
        {
            GameObject interactionTarget = ResolveDirtyInteractionTarget(dirty);
            if (_lastDirtyInteractionTarget != interactionTarget)
            {
                _lastDirtyInteractionTarget = interactionTarget;
                _dirtyInteractionCellSince = 0f;
            }
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, interactionTarget, true, out atCell);

            if (IsDirtyPickupSelected(dirty))
            {
                _dirtyInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                SetState(BotState.PickingUp);
                PulsePickup();
                return;
            }

            SetState(BotState.MovingToDirtyPlates);
            SetMove(direction);
            if (!atCell)
            {
                _dirtyInteractionCellSince = 0f;
                return;
            }

            if (_dirtyInteractionCellSince <= 0f)
            {
                _dirtyInteractionCellSince = Time.time;
            }
            else if (Time.time - _dirtyInteractionCellSince >= 0.75f)
            {
                _navigator.RejectCurrentInteractionCell();
                _dirtyInteractionCellSince = 0f;
            }
        }

        private static GameObject ResolveDirtyInteractionTarget(ClientDirtyPlateStack dirty)
        {
            if (dirty == null)
            {
                return null;
            }

            ClientHandlePickupReferral referral = dirty.GetComponent<ClientHandlePickupReferral>();
            if (referral != null)
            {
                ClientAttachStation referredStation = referral.GetHandlePickupReferree() as ClientAttachStation;
                if (referredStation != null && IsDirtyTargetObject(referredStation.InspectItem(), dirty))
                {
                    return GetAttachPointOrStation(referredStation);
                }
            }

            ClientAttachStation parentStation = dirty.GetComponentInParent<ClientAttachStation>();
            if (parentStation != null && IsDirtyTargetObject(parentStation.InspectItem(), dirty))
            {
                return GetAttachPointOrStation(parentStation);
            }

            return dirty.gameObject;
        }

        private static GameObject GetAttachPointOrStation(ClientAttachStation clientStation)
        {
            AttachStation station = clientStation.GetComponent<AttachStation>();
            if (station != null && station.m_attachPoint != null)
            {
                return station.m_attachPoint.gameObject;
            }
            return clientStation.gameObject;
        }

        private void MoveDirtyPlatesToSink(ClientWashingStation sink)
        {
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, sink.gameObject, out atCell);

            if (IsSinkPlacementSelected(sink))
            {
                SetMove(Vector3.zero);
                SetState(BotState.PlacingInSink);
                _placementPendingUntil = Time.time + 1.2f;
                PulsePickup();
                return;
            }

            SetState(BotState.MovingToSink);
            SetMove(direction);
        }

        private void WashAtSink(ClientWashingStation sink)
        {
            SetState(BotState.Washing);
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, sink.gameObject, out atCell);

            ClientInteractable selected = _player.CurrentInteractionObjects.m_interactable;
            ClientInteractable sinkInteractable = sink.GetComponent<ClientInteractable>();
            if (selected != null && sinkInteractable != null && selected == sinkInteractable)
            {
                SetMove(Vector3.zero);
                _input.SetUse(true);
                return;
            }

            _input.SetUse(false);
            SetMove(direction);
        }

        private bool IsDirtyPickupSelected(ClientDirtyPlateStack dirty)
        {
            PlayerControls.InteractionObjects interaction = _player.CurrentInteractionObjects;
            if (interaction == null || interaction.m_iHandlePickup == null)
            {
                return false;
            }

            bool targetsDirtyStack = IsDirtyTargetObject(interaction.m_TheOriginalHandlePickup, dirty);
            ClientAttachStation attachStation = interaction.m_iHandlePickup as ClientAttachStation;
            if (!targetsDirtyStack && attachStation != null)
            {
                targetsDirtyStack = IsDirtyTargetObject(attachStation.InspectItem(), dirty);
            }

            if (!targetsDirtyStack)
            {
                return false;
            }

            return interaction.m_iHandlePickup.CanHandlePickup(_carrier);
        }

        private static bool IsDirtyTargetObject(GameObject candidate, ClientDirtyPlateStack dirty)
        {
            if (candidate == null || dirty == null)
            {
                return false;
            }

            if (candidate == dirty.gameObject
                || candidate.transform.IsChildOf(dirty.transform)
                || dirty.transform.IsChildOf(candidate.transform))
            {
                return true;
            }

            DirtyPlateStack selectedStack = candidate.GetComponent<DirtyPlateStack>();
            if (selectedStack == null)
            {
                selectedStack = candidate.GetComponentInParent<DirtyPlateStack>();
            }
            return selectedStack != null && selectedStack.gameObject == dirty.gameObject;
        }

        private bool IsSinkPlacementSelected(ClientWashingStation sink)
        {
            IClientHandlePlacement placement = _player.CurrentInteractionObjects.m_iHandlePlacement;
            MonoBehaviour behaviour = placement as MonoBehaviour;
            if (behaviour == null)
            {
                return false;
            }
            return behaviour.gameObject == sink.gameObject
                || behaviour.transform.IsChildOf(sink.transform)
                || sink.transform.IsChildOf(behaviour.transform);
        }

        private ClientDirtyPlateStack FindNearestDirtyStack()
        {
            ClientDirtyPlateStack[] stacks = FindObjectsOfType<ClientDirtyPlateStack>();
            ClientDirtyPlateStack nearest = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < stacks.Length; i++)
            {
                ClientDirtyPlateStack stack = stacks[i];
                if (!IsUsableDirtyTarget(stack))
                {
                    continue;
                }
                GameObject interactionTarget = ResolveDirtyInteractionTarget(stack);
                float distance = HorizontalSqrDistance(_player.transform.position, interactionTarget.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = stack;
                }
            }
            return nearest;
        }

        private ClientWashingStation FindNearestSink()
        {
            ClientWashingStation[] sinks = FindObjectsOfType<ClientWashingStation>();
            ClientWashingStation nearest = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < sinks.Length; i++)
            {
                ClientWashingStation sink = sinks[i];
                if (!IsUsableSink(sink))
                {
                    continue;
                }
                float distance = HorizontalSqrDistance(_player.transform.position, sink.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = sink;
                }
            }
            return nearest;
        }

        private ClientWashingStation FindSinkWithDirtyPlates()
        {
            ClientWashingStation[] sinks = FindObjectsOfType<ClientWashingStation>();
            ClientWashingStation nearest = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < sinks.Length; i++)
            {
                ClientWashingStation sink = sinks[i];
                if (!IsUsableSink(sink) || GetPlateCount(sink) <= 0)
                {
                    continue;
                }
                float distance = HorizontalSqrDistance(_player.transform.position, sink.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = sink;
                }
            }
            return nearest;
        }

        private static bool IsUsableDirtyTarget(ClientDirtyPlateStack stack)
        {
            return stack != null && stack.enabled && stack.gameObject.activeInHierarchy && stack.GetCount() > 0;
        }

        private static bool IsUsableSink(ClientWashingStation sink)
        {
            return sink != null && sink.enabled && sink.gameObject.activeInHierarchy;
        }

        private static int GetPlateCount(ClientWashingStation sink)
        {
            if (sink == null || ClientSinkPlateCount == null)
            {
                return 0;
            }
            object value = ClientSinkPlateCount.GetValue(sink);
            return value is int ? (int)value : 0;
        }

        private void PulsePickup()
        {
            if (Time.time < _nextActionTime)
            {
                return;
            }
            _pickupDownUntil = Time.time + 0.12f;
            _nextActionTime = Time.time + 0.65f;
            _input.SetPickup(true);
        }

        private void UpdatePickupPulse()
        {
            if (_input != null)
            {
                _input.SetPickup(Time.time < _pickupDownUntil);
            }
        }

        private void SetMove(Vector3 worldDirection)
        {
            if (_input == null || _player == null)
            {
                return;
            }
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude > 1f)
            {
                worldDirection.Normalize();
            }
            _input.SetWorldDirection(new Vector3Like(worldDirection.x, worldDirection.z), _player.Movement);
        }

        private void Wait()
        {
            SetState(BotState.Waiting);
            ReleaseRobotInputs(false);
        }

        private void SetState(BotState state)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
        }

        private void ReleaseRobotInputs(bool restoreOriginals)
        {
            _pickupDownUntil = 0f;
            if (_input != null)
            {
                _input.ReleaseAll();
                if (restoreOriginals)
                {
                    _input.Restore();
                    _input = null;
                }
            }
        }

        private void EndCurrentInteraction()
        {
            if (_player == null || _player.gameObject == null)
            {
                return;
            }
            try
            {
                if (_player.GetCurrentlyInteracting() != null && SendChefEvent != null)
                {
                    SendChefEvent.Invoke(
                        null,
                        new object[]
                        {
                            ChefEventMessage.ChefEventType.Interact,
                            _player.gameObject,
                            null
                        });
                }
            }
            catch (Exception exception)
            {
                ReportErrorOnce(
                    "end-interaction:" + exception.GetType().FullName + ":" + exception.Message,
                    "Could not explicitly end the current interaction: " + exception);
            }
        }

        private void ReportErrorOnce(string key, string message)
        {
            if (_log != null && _reportedErrors.Add(key))
            {
                _log.LogError(message);
            }
        }

        private void ShutdownBinding()
        {
            _avoidingPlayers = false;
            _avoidanceThreats.Clear();
            ReleaseRobotInputs(true);
            _navigator.Clear();
            _dirtyTarget = null;
            _sinkTarget = null;
            _carrier = null;
            _player = null;
        }

        private static float HorizontalSqrDistance(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float z = left.z - right.z;
            return x * x + z * z;
        }

        private static MethodInfo ResolveChefEventSender()
        {
            Type messenger = typeof(PlayerControls).Assembly.GetType("ClientMessenger", false);
            if (messenger == null)
            {
                return null;
            }
            return messenger.GetMethod(
                "ChefEventMessage",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new Type[]
                {
                    typeof(ChefEventMessage.ChefEventType),
                    typeof(GameObject),
                    typeof(GameObject)
                },
                null);
        }
    }
}
