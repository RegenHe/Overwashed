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
        public const string PluginName = "Overwashed";
        public const string PluginVersion = "1.4.4";

        private static readonly FieldInfo ClientSinkPlateCount = typeof(ClientWashingStation).GetField(
            "m_plateCount",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo SendChefEvent = ResolveChefEventSender();

        private readonly GridPathNavigator _navigator = new GridPathNavigator();
        private readonly AutoServePlanner _servePlanner = new AutoServePlanner();
        private readonly HashSet<string> _reportedErrors = new HashSet<string>();
        private readonly List<Vector3> _avoidanceThreats = new List<Vector3>();
        private static readonly PlayerControls[] NoPlayers = new PlayerControls[0];

        private ManualLogSource _log;
        private ConfigEntry<bool> _autoAvoidance;
        private ConfigEntry<float> _avoidanceDistance;
        private ConfigEntry<bool> _autoServeReadyOrders;
        private ConfigEntry<bool> _serveInOrder;
        private ConfigEntry<bool> _fastMode;
        private ConfigEntry<bool> _extremeMode;
        private bool _enabled;
        private bool _showSettings;
        private bool _avoidingPlayers;
        private PlayerControls _player;
        private ClientPlayerAttachmentCarrier _carrier;
        private BotInputBinding _input;
        private ClientDirtyPlateStack _dirtyTarget;
        private ClientWashingStation _sinkTarget;
        private AutoServePlan _servePlan;
        private ServePhase _servePhase;
        private BotState _state = BotState.Disabled;
        private float _pickupDownUntil;
        private float _dashDownUntil;
        private float _nextDashTime;
        private float _placementPendingUntil;
        private float _nextActionTime;
        private float _nextAcquireTime;
        private float _nextDirtyScanTime;
        private float _nextSinkScanTime;
        private float _nextDropRequestTime;
        private float _dirtyInteractionCellSince;
        private float _sinkInteractionCellSince;
        private float _serveInteractionCellSince;
        private float _nextServeScanTime;
        private float _servePendingUntil;
        private float _nextPlayerSnapshotTime;
        private PlayerControls[] _playerSnapshot = NoPlayers;
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
            MovingToReadyMeal,
            PickingUpReadyMeal,
            MovingToCleanPlate,
            PickingUpCleanPlate,
            PlatingMeal,
            MovingToServingStation,
            ServingMeal,
            AvoidingPlayers,
            Waiting
        }

        private enum ServePhase
        {
            None,
            PickingReadyPlate,
            PickingCleanPlate,
            PlatingMeal,
            Delivering
        }

        private bool FastModeEnabled
        {
            get { return _fastMode != null && _fastMode.Value; }
        }

        private float ScanInterval
        {
            get { return FastModeEnabled ? 0.1f : 0.5f; }
        }

        private float InteractionRetryInterval
        {
            get { return FastModeEnabled ? 0.1f : 0.75f; }
        }

        private void Awake()
        {
            _log = Logger;
            _autoAvoidance = Config.Bind(
                "Avoidance",
                "Enabled",
                false,
                "Move the dishwasher bot away when another chef comes too close.");
            _avoidanceDistance = Config.Bind(
                "Avoidance",
                "Distance",
                1.5f,
                new ConfigDescription(
                    "Distance in grid tiles at which the bot starts avoiding another chef.",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            _autoServeReadyOrders = Config.Bind(
                "Serving",
                "AutoServeReadyOrders",
                false,
                "Automatically plate completed meals when necessary and deliver matching active orders.");
            _serveInOrder = Config.Bind(
                "Serving",
                "ServeInOrder",
                true,
                "Only serve the oldest active order. Disable to serve any matching active order.");
            _fastMode = Config.Bind(
                "Speed",
                "FastMode",
                false,
                "Reduce target scans, interaction retries, and action retry delays to about 0.1 seconds.");
            _extremeMode = Config.Bind(
                "Speed",
                "ExtremeMode",
                false,
                "Repeatedly dash whenever the current route has more than a short distance remaining.");
            CreateStatusBadgeTextures();
            _log.LogInfo(PluginName + " " + PluginVersion + " loaded. Press F8 to toggle; click the active bot icon for settings.");
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
                UpdateDashPulse();
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
            const float height = 258f;
            float left = Mathf.Max(8f, Screen.width - width - 12f);
            Rect panel = new Rect(left, 52f, width, height);

            GUI.depth = -1001;
            GUI.color = Color.white;
            GUI.Box(panel, "Overwashed Settings");

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

            bool autoServe = GUI.Toggle(
                new Rect(panel.x + 16f, panel.y + 110f, panel.width - 32f, 22f),
                _autoServeReadyOrders.Value,
                "Auto serve completed orders");
            if (autoServe != _autoServeReadyOrders.Value)
            {
                _autoServeReadyOrders.Value = autoServe;
                ResetServingPlan(true);
            }

            bool serveInOrder = GUI.Toggle(
                new Rect(panel.x + 16f, panel.y + 136f, panel.width - 32f, 22f),
                _serveInOrder.Value,
                "Serve in order");
            if (serveInOrder != _serveInOrder.Value)
            {
                _serveInOrder.Value = serveInOrder;
                ResetServingPlan(true);
            }

            bool fastMode = GUI.Toggle(
                new Rect(panel.x + 16f, panel.y + 162f, panel.width - 32f, 22f),
                _fastMode.Value,
                "Fast interactions");
            if (fastMode != _fastMode.Value)
            {
                _fastMode.Value = fastMode;
                _nextActionTime = 0f;
                _nextDirtyScanTime = 0f;
                _nextSinkScanTime = 0f;
                _nextServeScanTime = 0f;
                _dirtyInteractionCellSince = 0f;
                _sinkInteractionCellSince = 0f;
                _serveInteractionCellSince = 0f;
            }

            bool extremeMode = GUI.Toggle(
                new Rect(panel.x + 16f, panel.y + 188f, panel.width - 32f, 22f),
                _extremeMode.Value,
                "Extreme dash mode");
            if (extremeMode != _extremeMode.Value)
            {
                _extremeMode.Value = extremeMode;
                _dashDownUntil = 0f;
                _nextDashTime = 0f;
                if (_input != null)
                {
                    _input.SetDash(false);
                }
            }

            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 226f, panel.width - 32f, 22f),
                "Click the bot icon to close settings");
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
            _dashDownUntil = 0f;
            _nextDashTime = 0f;
            _placementPendingUntil = 0f;
            _nextActionTime = 0f;
            _nextDirtyScanTime = 0f;
            _nextSinkScanTime = 0f;
            _nextDropRequestTime = 0f;
            _dirtyInteractionCellSince = 0f;
            _sinkInteractionCellSince = 0f;
            _serveInteractionCellSince = 0f;
            _nextServeScanTime = 0f;
            _servePendingUntil = 0f;
            _nextPlayerSnapshotTime = 0f;
            _playerSnapshot = NoPlayers;
            _droppingItemId = 0;
            _lastDirtyInteractionTarget = null;
            _avoidingPlayers = false;
            _avoidanceThreats.Clear();
            _servePlan = null;
            _servePhase = ServePhase.None;

            if (enabled)
            {
                SetState(BotState.FindingPlayer);
                _log.LogInfo("Dishwasher bot ENABLED. Searching for a locally controlled keyboard chef.");
            }
            else
            {
                _showSettings = false;
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
            _nextAcquireTime = Time.unscaledTime + (FastModeEnabled ? 0.1f : 0.75f);

            PlayerControls[] players = GetPlayerSnapshot(true);
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

        private PlayerControls[] GetPlayerSnapshot(bool forceRefresh)
        {
            if (forceRefresh || Time.unscaledTime >= _nextPlayerSnapshotTime)
            {
                _playerSnapshot = FindObjectsOfType<PlayerControls>();
                _nextPlayerSnapshotTime = Time.unscaledTime + 0.25f;
            }
            return _playerSnapshot ?? NoPlayers;
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
            PlayerControls[] players = GetPlayerSnapshot(false);
            _navigator.SetPlayerSnapshot(players);
            float chefAvoidanceRadius = _autoAvoidance != null && _autoAvoidance.Value
                ? Mathf.Clamp(_avoidanceDistance.Value, 0.5f, 4f)
                : 0f;
            _navigator.SetChefAvoidanceRadius(chefAvoidanceRadius);
            bool carryingDirtyPlates = carried != null && carried.GetComponent<DirtyPlateStack>() != null;
            bool carryingPlate = carried != null && carried.GetComponent<ClientPlate>() != null;
            if (carried != null && !carryingDirtyPlates && (!carryingPlate || !_autoServeReadyOrders.Value))
            {
                DropUnexpectedItem(carried);
                return;
            }
            _droppingItemId = 0;

            if (TryAvoidPlayers(players))
            {
                return;
            }

            if (_autoServeReadyOrders.Value && TryAutoServe(carried))
            {
                return;
            }

            if (carried != null && !carryingDirtyPlates)
            {
                DropUnexpectedItem(carried);
                return;
            }

            if (carried != null)
            {
                _dirtyTarget = null;
                if (!IsUsableSink(_sinkTarget))
                {
                    _sinkTarget = null;
                    _sinkInteractionCellSince = 0f;
                    if (Time.time >= _nextSinkScanTime)
                    {
                        _nextSinkScanTime = Time.time + ScanInterval;
                        _sinkTarget = FindNearestSink();
                        _sinkInteractionCellSince = 0f;
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
                _nextSinkScanTime = Time.time + ScanInterval;
                ClientWashingStation loadedSink = FindSinkWithDirtyPlates();
                if (loadedSink != null && loadedSink != _sinkTarget)
                {
                    _sinkTarget = loadedSink;
                    _sinkInteractionCellSince = 0f;
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
                    _nextDirtyScanTime = Time.time + ScanInterval;
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

        private bool TryAutoServe(GameObject carried)
        {
            if (_autoServeReadyOrders == null || !_autoServeReadyOrders.Value || _player == null || _carrier == null)
            {
                ResetServingPlan(false);
                return false;
            }

            ClientPlate carriedPlate = carried == null ? null : carried.GetComponent<ClientPlate>();
            if (carriedPlate != null)
            {
                if (_servePhase == ServePhase.Delivering && Time.time < _servePendingUntil)
                {
                    SetState(BotState.ServingMeal);
                    return true;
                }

                RecipeList.Entry matchingOrder;
                string error;
                if (_servePlanner.TryFindMatchingOrder(
                    _player,
                    carriedPlate,
                    _serveInOrder.Value,
                    out matchingOrder,
                    out error))
                {
                    if (_servePlan == null || _servePlan.Order != matchingOrder)
                    {
                        _servePlan = new AutoServePlan
                        {
                            Order = matchingOrder,
                            ReadyPlate = carriedPlate,
                            ServingStation = _servePlanner.FindServingStation(_player)
                        };
                    }
                    else if (_servePlan.ServingStation == null)
                    {
                        _servePlan.ServingStation = _servePlanner.FindServingStation(_player);
                    }

                    if (_servePlan.ServingStation == null)
                    {
                        ResetServingPlan(false);
                        return false;
                    }

                    _servePhase = ServePhase.Delivering;
                    MoveServingPlateToStation(_servePlan.ServingStation);
                    return true;
                }
                if (!string.IsNullOrEmpty(error))
                {
                    ReportErrorOnce("auto-serve-orders:" + error, "Could not inspect active orders: " + error);
                }

                if (AutoServePlanner.IsEmptyPlate(carriedPlate)
                    && _servePlan != null
                    && _servePlan.NeedsPlating)
                {
                    if (Time.time < _servePendingUntil && _servePhase == ServePhase.PlatingMeal)
                    {
                        SetState(BotState.PlatingMeal);
                        return true;
                    }
                    if (_servePlanner.MealStillMatches(_servePlan.UnplatedMeal, _servePlan.Order))
                    {
                        _servePhase = ServePhase.PlatingMeal;
                        MovePlateToCompletedMeal(_servePlan.UnplatedMeal);
                        return true;
                    }
                }

                ResetServingPlan(false);
                return false;
            }

            if (carried != null)
            {
                ResetServingPlan(false);
                return false;
            }

            if (Time.time < _servePendingUntil)
            {
                SetState(_servePhase == ServePhase.Delivering ? BotState.ServingMeal : GetServingWaitingState());
                return true;
            }

            if (_servePhase == ServePhase.Delivering)
            {
                ResetServingPlan(true);
            }
            else if (_servePhase == ServePhase.PickingReadyPlate
                && _servePlan != null
                && IsActive(_servePlan.ReadyPlate))
            {
                MoveToServingPlate(_servePlan.ReadyPlate, false);
                return true;
            }
            else if (_servePhase == ServePhase.PickingCleanPlate
                && _servePlan != null
                && IsActive(_servePlan.EmptyPlate)
                && AutoServePlanner.IsEmptyPlate(_servePlan.EmptyPlate)
                && _servePlanner.MealStillMatches(_servePlan.UnplatedMeal, _servePlan.Order))
            {
                MoveToServingPlate(_servePlan.EmptyPlate, true);
                return true;
            }
            else if (_servePhase != ServePhase.None)
            {
                ResetServingPlan(true);
            }

            if (Time.time < _nextServeScanTime)
            {
                return false;
            }
            _nextServeScanTime = Time.time + ScanInterval;

            AutoServePlan plan;
            string planningError;
            if (!_servePlanner.TryBuildPlan(_player, _serveInOrder.Value, out plan, out planningError))
            {
                if (!string.IsNullOrEmpty(planningError))
                {
                    ReportErrorOnce(
                        "auto-serve-plan:" + planningError,
                        "Could not build an automatic serving plan: " + planningError);
                }
                return false;
            }

            _servePlan = plan;
            _serveInteractionCellSince = 0f;
            _navigator.Clear();
            if (plan.ReadyPlate != null)
            {
                _servePhase = ServePhase.PickingReadyPlate;
                MoveToServingPlate(plan.ReadyPlate, false);
            }
            else
            {
                _servePhase = ServePhase.PickingCleanPlate;
                MoveToServingPlate(plan.EmptyPlate, true);
            }
            return true;
        }

        private BotState GetServingWaitingState()
        {
            switch (_servePhase)
            {
                case ServePhase.PickingReadyPlate:
                    return BotState.PickingUpReadyMeal;
                case ServePhase.PickingCleanPlate:
                    return BotState.PickingUpCleanPlate;
                case ServePhase.PlatingMeal:
                    return BotState.PlatingMeal;
                default:
                    return BotState.Waiting;
            }
        }

        private void MoveToServingPlate(ClientPlate plate, bool cleanPlate)
        {
            if (!IsActive(plate))
            {
                ResetServingPlan(true);
                return;
            }

            GameObject target = ResolveServeInteractionTarget(plate.gameObject, true);
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, target, true, out atCell);
            if (IsPickupSelected(plate.gameObject))
            {
                _serveInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                SetState(cleanPlate ? BotState.PickingUpCleanPlate : BotState.PickingUpReadyMeal);
                PulseServingAction(0.8f);
                return;
            }

            SetState(cleanPlate ? BotState.MovingToCleanPlate : BotState.MovingToReadyMeal);
            SetMove(direction);
            UpdateServingInteractionCell(atCell);
        }

        private void MovePlateToCompletedMeal(GameObject meal)
        {
            GameObject target = ResolveServeInteractionTarget(meal, false);
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, target, true, out atCell);
            if (IsPlacementSelected(meal))
            {
                _serveInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                SetState(BotState.PlatingMeal);
                PulseServingAction(0.8f);
                return;
            }

            SetState(BotState.PlatingMeal);
            SetMove(direction);
            UpdateServingInteractionCell(atCell);
        }

        private void MoveServingPlateToStation(ClientPlateStation station)
        {
            if (station == null || !station.enabled || !station.gameObject.activeInHierarchy)
            {
                ResetServingPlan(true);
                return;
            }

            ClientAttachStation attachStation = station.GetComponent<ClientAttachStation>();
            GameObject navigationTarget = attachStation == null
                ? station.gameObject
                : GetAttachPointOrStation(attachStation);
            bool atCell;
            Vector3 direction = _navigator.DirectionTo(_player, navigationTarget, true, out atCell);
            if (IsPlacementSelected(station.gameObject))
            {
                _serveInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                SetState(BotState.ServingMeal);
                PulseServingAction(1.0f);
                return;
            }

            SetState(BotState.MovingToServingStation);
            SetMove(direction);
            UpdateServingInteractionCell(atCell);
        }

        private void PulseServingAction(float pendingSeconds)
        {
            if (Time.time < _nextActionTime)
            {
                return;
            }
            PulsePickup();
            _servePendingUntil = Time.time + (FastModeEnabled ? 0.15f : pendingSeconds);
        }

        private void UpdateServingInteractionCell(bool atCell)
        {
            if (!atCell)
            {
                _serveInteractionCellSince = 0f;
                return;
            }
            if (_serveInteractionCellSince <= 0f)
            {
                _serveInteractionCellSince = Time.time;
            }
            else if (Time.time - _serveInteractionCellSince >= InteractionRetryInterval)
            {
                _navigator.RejectCurrentInteractionCell();
                _serveInteractionCellSince = 0f;
            }
        }

        private bool IsPickupSelected(GameObject target)
        {
            PlayerControls.InteractionObjects interaction = _player.CurrentInteractionObjects;
            if (interaction == null || interaction.m_iHandlePickup == null || target == null)
            {
                return false;
            }
            try
            {
                IClientHandlePickup expected = PlayerControlsHelper.GetControllingPickupHandler_Client(target);
                bool sameHandler = expected != null && ReferenceEquals(expected, interaction.m_iHandlePickup);
                bool sameObject = IsSameObjectHierarchy(interaction.m_TheOriginalHandlePickup, target);
                return (sameHandler || sameObject) && interaction.m_iHandlePickup.CanHandlePickup(_carrier);
            }
            catch
            {
                return false;
            }
        }

        private bool IsPlacementSelected(GameObject target)
        {
            PlayerControls.InteractionObjects interaction = _player.CurrentInteractionObjects;
            if (interaction == null || interaction.m_iHandlePlacement == null || target == null)
            {
                return false;
            }
            try
            {
                IClientHandlePlacement expected = PlayerControlsHelper.GetControllingPlacementHandler_Client(target);
                bool sameHandler = expected != null && ReferenceEquals(expected, interaction.m_iHandlePlacement);
                bool sameObject = IsSameObjectHierarchy(interaction.m_TheOriginalHandlePickup, target);
                if (!sameHandler && !sameObject)
                {
                    return false;
                }
                Vector3 forward = _player.transform.forward;
                Vector2 direction = new Vector2(forward.x, forward.z).normalized;
                return interaction.m_iHandlePlacement.CanHandlePlacement(
                    _carrier,
                    direction,
                    new PlacementContext(PlacementContext.Source.Player));
            }
            catch
            {
                return false;
            }
        }

        private static GameObject ResolveServeInteractionTarget(GameObject target, bool pickup)
        {
            if (target == null)
            {
                return null;
            }

            MonoBehaviour handler = null;
            if (pickup)
            {
                ClientHandlePickupReferral referral = target.GetComponent<ClientHandlePickupReferral>();
                if (referral != null)
                {
                    handler = referral.GetHandlePickupReferree() as MonoBehaviour;
                }
            }
            else
            {
                ClientHandlePlacementReferral referral = target.GetComponent<ClientHandlePlacementReferral>();
                if (referral != null)
                {
                    handler = referral.GetHandlePlacementReferree() as MonoBehaviour;
                }
            }

            ClientAttachStation station = handler as ClientAttachStation;
            if (station == null)
            {
                station = target.GetComponentInParent<ClientAttachStation>();
            }
            if (station != null)
            {
                return GetAttachPointOrStation(station);
            }
            return handler == null ? target : handler.gameObject;
        }

        private static bool IsSameObjectHierarchy(GameObject left, GameObject right)
        {
            return left != null
                && right != null
                && (left == right
                    || left.transform.IsChildOf(right.transform)
                    || right.transform.IsChildOf(left.transform));
        }

        private static bool IsActive(MonoBehaviour behaviour)
        {
            return behaviour != null && behaviour.enabled && behaviour.gameObject.activeInHierarchy;
        }

        private void ResetServingPlan(bool clearNavigator)
        {
            _servePlan = null;
            _servePhase = ServePhase.None;
            _servePendingUntil = 0f;
            _serveInteractionCellSince = 0f;
            _nextServeScanTime = 0f;
            if (clearNavigator)
            {
                _navigator.Clear();
            }
        }

        private bool TryAvoidPlayers(PlayerControls[] players)
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
            bool reachedAvoidanceGoal;
            Vector3 direction = _navigator.DirectionAwayFrom(
                _player,
                _avoidanceThreats,
                releaseDistance,
                out reachedAvoidanceGoal);
            if (reachedAvoidanceGoal)
            {
                StopAvoidingPlayers();
                SetMove(Vector3.zero);
                return false;
            }
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
            _nextDropRequestTime = Time.time + (FastModeEnabled ? 0.1f : 0.75f);

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
            else if (Time.time - _dirtyInteractionCellSince >= InteractionRetryInterval)
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
                _sinkInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                SetState(BotState.PlacingInSink);
                _placementPendingUntil = Time.time + (FastModeEnabled ? 0.15f : 1.2f);
                PulsePickup();
                return;
            }

            SetState(BotState.MovingToSink);
            SetMove(direction);
            UpdateSinkInteractionCell(atCell);
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
                _sinkInteractionCellSince = 0f;
                SetMove(Vector3.zero);
                _input.SetUse(true);
                return;
            }

            _input.SetUse(false);
            SetMove(direction);
            UpdateSinkInteractionCell(atCell);
        }

        private void UpdateSinkInteractionCell(bool atCell)
        {
            if (!atCell)
            {
                _sinkInteractionCellSince = 0f;
                return;
            }
            if (_sinkInteractionCellSince <= 0f)
            {
                _sinkInteractionCellSince = Time.time;
            }
            else if (Time.time - _sinkInteractionCellSince
                >= (FastModeEnabled ? 0.1f : 0.65f))
            {
                if (!_navigator.RejectCurrentInteractionCell())
                {
                    _navigator.Clear();
                }
                _sinkInteractionCellSince = 0f;
            }
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
            _pickupDownUntil = Time.time + (FastModeEnabled ? 0.05f : 0.12f);
            _nextActionTime = Time.time + (FastModeEnabled ? 0.1f : 0.35f);
            _input.SetPickup(true);
        }

        private void UpdatePickupPulse()
        {
            if (_input != null)
            {
                _input.SetPickup(Time.time < _pickupDownUntil);
            }
        }

        private void UpdateDashPulse()
        {
            if (_input != null)
            {
                _input.SetDash(Time.time < _dashDownUntil);
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

            bool extremeMode = _extremeMode != null && _extremeMode.Value;
            bool routeAllowsDash = extremeMode
                ? _navigator.CanExtremeDash
                : _navigator.CanDash;
            if (worldDirection.sqrMagnitude > 0.01f
                && routeAllowsDash
                && Time.time >= _nextDashTime)
            {
                _dashDownUntil = Time.time + (extremeMode ? 0.06f : 0.08f);
                _nextDashTime = Time.time + (extremeMode ? 0.14f : 0.85f);
                _input.SetDash(true);
                _navigator.NotifyDashStarted(_player.Movement.DashTime);
            }
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
            _dashDownUntil = 0f;
            _nextDashTime = 0f;
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
            _nextPlayerSnapshotTime = 0f;
            _playerSnapshot = NoPlayers;
            ResetServingPlan(false);
            ReleaseRobotInputs(true);
            _navigator.Clear();
            _dirtyTarget = null;
            _sinkTarget = null;
            _sinkInteractionCellSince = 0f;
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
