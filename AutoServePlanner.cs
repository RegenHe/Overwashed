using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Overcooked2DishwasherBot
{
    internal sealed class AutoServePlan
    {
        internal RecipeList.Entry Order;
        internal ClientPlate ReadyPlate;
        internal ClientPlate EmptyPlate;
        internal GameObject UnplatedMeal;
        internal ClientPlateStation ServingStation;

        internal bool NeedsPlating
        {
            get { return ReadyPlate == null && EmptyPlate != null && UnplatedMeal != null; }
        }
    }

    internal sealed class AutoServePlanner
    {
        private const int OrderDefinitionBucketCount = 9;
        private const float WarmupSliceInterval = 0.04f;
        private const float SteadySliceInterval = 0.2f;

        private static int s_orderRevision;

        private static readonly FieldInfo ActiveOrdersField = typeof(ClientOrderControllerBase).GetField(
            "m_activeOrders",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Type ActiveOrderType = typeof(ClientOrderControllerBase).GetNestedType(
            "ActiveOrder",
            BindingFlags.NonPublic);

        private static readonly FieldInfo ActiveOrderRecipeField = ActiveOrderType == null
            ? null
            : ActiveOrderType.GetField("RecipeListEntry", BindingFlags.Instance | BindingFlags.Public);

        private readonly List<RecipeList.Entry> _orders = new List<RecipeList.Entry>();
        private readonly HashSet<int> _heldObjects = new HashSet<int>();
        private readonly HashSet<int> _scannedDefinitionIds = new HashSet<int>();
        private readonly List<MonoBehaviour> _orderDefinitionCache = new List<MonoBehaviour>();
        private readonly List<MonoBehaviour>[] _orderDefinitionBuckets;
        private readonly OrderCompositionChangedCallback _compositionChangedCallback;
        private static readonly ClientPlate[] NoPlates = new ClientPlate[0];
        private static readonly MonoBehaviour[] NoBehaviours = new MonoBehaviour[0];
        private static readonly ClientPlateStation[] NoServingStations = new ClientPlateStation[0];
        private static readonly ClientPlayerAttachmentCarrier[] NoCarriers = new ClientPlayerAttachmentCarrier[0];
        private ClientKitchenFlowControllerBase _flow;
        private ClientPlate[] _cachedPlates = NoPlates;
        private ClientPlateStation[] _cachedServingStations = NoServingStations;
        private ClientPlayerAttachmentCarrier[] _cachedCarriers = NoCarriers;
        private ClientPlate[] _planningPlates = NoPlates;
        private IList<MonoBehaviour> _planningBehaviours = NoBehaviours;
        private ClientPlayerAttachmentCarrier _planningCarrier;
        private bool _planningSnapshotActive;
        private bool _planningBehavioursLoaded;
        private float _nextServingStationRefreshTime;
        private float _nextCarrierRefreshTime;
        private float _nextDefinitionSliceTime;
        private int _nextDefinitionBucket;
        private int _definitionSlicesCompleted;
        private int _compositionRevision;
        private int _observedCompositionRevision;
        private int _observedOrderRevision;

        internal AutoServePlanner()
        {
            _orderDefinitionBuckets = new List<MonoBehaviour>[OrderDefinitionBucketCount];
            for (int i = 0; i < _orderDefinitionBuckets.Length; i++)
            {
                _orderDefinitionBuckets[i] = new List<MonoBehaviour>();
            }
            _compositionChangedCallback = OnOrderCompositionChanged;
            _observedOrderRevision = s_orderRevision;
        }

        internal static void NotifyOrdersChanged()
        {
            unchecked
            {
                s_orderRevision++;
            }
        }

        internal bool HasPendingPlanningSignal
        {
            get
            {
                return _observedOrderRevision != s_orderRevision
                    || _observedCompositionRevision != _compositionRevision;
            }
        }

        internal bool TickDiscovery()
        {
            if (Time.unscaledTime < _nextDefinitionSliceTime)
            {
                return false;
            }

            int bucket = _nextDefinitionBucket;
            bool changed;
            switch (bucket)
            {
                case 0:
                {
                    ClientPlate[] plates = UnityEngine.Object.FindObjectsOfType<ClientPlate>();
                    _cachedPlates = plates ?? NoPlates;
                    changed = ReplaceDefinitionBucket(bucket, plates);
                    break;
                }
                case 1:
                    changed = ScanDefinitionBucket<ClientCookableContainer>(bucket);
                    break;
                case 2:
                    changed = ScanDefinitionBucket<ClientPreparationContainer>(bucket);
                    break;
                case 3:
                    changed = ScanDefinitionBucket<ClientItemContainer>(bucket);
                    break;
                case 4:
                    changed = ScanDefinitionBucket<ClientMixableContainer>(bucket);
                    break;
                case 5:
                    changed = ScanDefinitionBucket<ClientLadleContainer>(bucket);
                    break;
                case 6:
                    changed = ScanDefinitionBucket<AssignableOrderDefinition>(bucket);
                    break;
                case 7:
                    changed = ScanDefinitionBucket<IngredientPropertiesComponent>(bucket);
                    break;
                default:
                    changed = ScanDefinitionBucket<ItemPropertiesComponent>(bucket);
                    break;
            }

            _nextDefinitionBucket = (bucket + 1) % OrderDefinitionBucketCount;
            bool warmingUp = _definitionSlicesCompleted < OrderDefinitionBucketCount;
            if (warmingUp)
            {
                _definitionSlicesCompleted++;
            }
            _nextDefinitionSliceTime = Time.unscaledTime
                + (warmingUp ? WarmupSliceInterval : SteadySliceInterval);
            return changed;
        }

        internal void Clear()
        {
            for (int i = 0; i < _orderDefinitionBuckets.Length; i++)
            {
                UnsubscribeDefinitionBucket(_orderDefinitionBuckets[i]);
                _orderDefinitionBuckets[i].Clear();
            }
            _orders.Clear();
            _heldObjects.Clear();
            _scannedDefinitionIds.Clear();
            _orderDefinitionCache.Clear();
            _flow = null;
            _cachedPlates = NoPlates;
            _cachedServingStations = NoServingStations;
            _cachedCarriers = NoCarriers;
            _nextServingStationRefreshTime = 0f;
            _nextCarrierRefreshTime = 0f;
            _nextDefinitionSliceTime = 0f;
            _nextDefinitionBucket = 0;
            _definitionSlicesCompleted = 0;
            _compositionRevision = 0;
            _observedCompositionRevision = 0;
            _observedOrderRevision = s_orderRevision;
            EndPlanningSnapshot();
        }

        internal bool TryBuildPlan(
            PlayerControls player,
            bool serveInOrder,
            out AutoServePlan plan,
            out string error)
        {
            plan = null;
            error = null;
            if (player == null)
            {
                return false;
            }

            _observedOrderRevision = s_orderRevision;
            _observedCompositionRevision = _compositionRevision;

            if (!TryGetActiveOrders(player, _orders, out error) || _orders.Count == 0)
            {
                return false;
            }

            ClientPlateStation station = FindNearestServingStation(player);
            if (station == null)
            {
                return false;
            }

            BeginPlanningSnapshot(player);
            try
            {
                RefreshHeldObjects();
                if (serveInOrder)
                {
                    return TryBuildPlanForOrder(player, _orders[0], station, out plan);
                }

                AutoServePlan bestPlated = null;
                float bestPlatedDistance = float.PositiveInfinity;
                for (int i = 0; i < _orders.Count; i++)
                {
                    ClientPlate readyPlate = FindNearestReadyPlate(player, _orders[i], out float readyDistance);
                    if (readyPlate != null && readyDistance < bestPlatedDistance)
                    {
                        bestPlatedDistance = readyDistance;
                        bestPlated = new AutoServePlan
                        {
                            Order = _orders[i],
                            ReadyPlate = readyPlate,
                            ServingStation = station
                        };
                    }
                }
                if (bestPlated != null)
                {
                    plan = bestPlated;
                    return true;
                }

                EnsurePlanningBehaviourSnapshot();
                AutoServePlan bestUnplated = null;
                float bestUnplatedDistance = float.PositiveInfinity;
                for (int i = 0; i < _orders.Count; i++)
                {
                    AutoServePlan candidate;
                    float distance;
                    if (TryBuildUnplatedPlan(player, _orders[i], station, out candidate, out distance)
                        && distance < bestUnplatedDistance)
                    {
                        bestUnplatedDistance = distance;
                        bestUnplated = candidate;
                    }
                }

                plan = bestUnplated;
                return plan != null;
            }
            finally
            {
                EndPlanningSnapshot();
            }
        }

        internal bool TryFindMatchingOrder(
            PlayerControls player,
            ClientPlate plate,
            bool serveInOrder,
            out RecipeList.Entry order,
            out string error)
        {
            order = null;
            error = null;
            if (player == null || plate == null || !TryGetActiveOrders(player, _orders, out error))
            {
                return false;
            }

            int count = serveInOrder ? Math.Min(1, _orders.Count) : _orders.Count;
            for (int i = 0; i < count; i++)
            {
                if (PlateMatchesOrder(plate, _orders[i]))
                {
                    order = _orders[i];
                    return true;
                }
            }
            return false;
        }

        internal ClientPlateStation FindServingStation(PlayerControls player)
        {
            return player == null ? null : FindNearestServingStation(player);
        }

        internal bool MealStillMatches(GameObject meal, RecipeList.Entry order)
        {
            if (meal == null || order == null || order.m_order == null || !meal.activeInHierarchy)
            {
                return false;
            }

            MonoBehaviour[] behaviours = meal.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                IClientOrderDefinition definition = behaviours[i] as IClientOrderDefinition;
                if (definition == null)
                {
                    continue;
                }
                try
                {
                    if (CompositionMatchesOrder(definition.GetOrderComposition(), order))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        internal static bool IsEmptyPlate(ClientPlate plate)
        {
            if (plate == null)
            {
                return false;
            }
            ClientIngredientContainer contents = plate.GetComponent<ClientIngredientContainer>();
            return contents != null && contents.GetContentsCount() == 0;
        }

        private bool TryBuildPlanForOrder(
            PlayerControls player,
            RecipeList.Entry order,
            ClientPlateStation station,
            out AutoServePlan plan)
        {
            plan = null;
            float ignored;
            ClientPlate readyPlate = FindNearestReadyPlate(player, order, out ignored);
            if (readyPlate != null)
            {
                plan = new AutoServePlan
                {
                    Order = order,
                    ReadyPlate = readyPlate,
                    ServingStation = station
                };
                return true;
            }

            return TryBuildUnplatedPlan(player, order, station, out plan, out ignored);
        }

        private bool TryBuildUnplatedPlan(
            PlayerControls player,
            RecipeList.Entry order,
            ClientPlateStation station,
            out AutoServePlan plan,
            out float distance)
        {
            plan = null;
            distance = float.PositiveInfinity;
            GameObject meal = FindNearestUnplatedMeal(player, order, out float mealDistance);
            if (meal == null)
            {
                return false;
            }

            ClientPlate emptyPlate = FindBestEmptyPlate(player, meal, order, out float routeDistance);
            if (emptyPlate == null)
            {
                return false;
            }

            distance = routeDistance + mealDistance * 0.05f;
            plan = new AutoServePlan
            {
                Order = order,
                EmptyPlate = emptyPlate,
                UnplatedMeal = meal,
                ServingStation = station
            };
            return true;
        }

        private ClientPlate FindNearestReadyPlate(
            PlayerControls player,
            RecipeList.Entry order,
            out float bestDistance)
        {
            bestDistance = float.PositiveInfinity;
            ClientPlate best = null;
            ClientPlayerAttachmentCarrier carrier = _planningSnapshotActive
                ? _planningCarrier
                : player.GetComponent<ClientPlayerAttachmentCarrier>();
            ClientPlate[] plates = _planningSnapshotActive
                ? _planningPlates
                : GetPlateSnapshot();
            for (int i = 0; i < plates.Length; i++)
            {
                ClientPlate plate = plates[i];
                if (!IsAvailablePlate(plate, carrier) || !PlateMatchesOrder(plate, order))
                {
                    continue;
                }

                float distance = HorizontalSqrDistance(player.transform.position, plate.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = plate;
                }
            }
            return best;
        }

        private ClientPlate FindBestEmptyPlate(
            PlayerControls player,
            GameObject meal,
            RecipeList.Entry order,
            out float bestDistance)
        {
            bestDistance = float.PositiveInfinity;
            ClientPlate best = null;
            ClientPlayerAttachmentCarrier carrier = _planningSnapshotActive
                ? _planningCarrier
                : player.GetComponent<ClientPlayerAttachmentCarrier>();
            ClientPlate[] plates = _planningSnapshotActive
                ? _planningPlates
                : GetPlateSnapshot();
            for (int i = 0; i < plates.Length; i++)
            {
                ClientPlate plate = plates[i];
                if (!IsAvailablePlate(plate, carrier)
                    || !IsEmptyPlate(plate)
                    || !PlateTypeMatchesOrder(plate, order))
                {
                    continue;
                }

                float toPlate = Mathf.Sqrt(HorizontalSqrDistance(player.transform.position, plate.transform.position));
                float plateToMeal = Mathf.Sqrt(HorizontalSqrDistance(plate.transform.position, meal.transform.position));
                float distance = toPlate + plateToMeal;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = plate;
                }
            }
            return best;
        }

        private GameObject FindNearestUnplatedMeal(
            PlayerControls player,
            RecipeList.Entry order,
            out float bestDistance)
        {
            bestDistance = float.PositiveInfinity;
            GameObject best = null;
            if (_planningSnapshotActive)
            {
                EnsurePlanningBehaviourSnapshot();
            }
            IList<MonoBehaviour> behaviours;
            if (_planningSnapshotActive)
            {
                behaviours = _planningBehaviours;
            }
            else
            {
                behaviours = _orderDefinitionCache;
            }
            for (int i = 0; i < behaviours.Count; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                IClientOrderDefinition definition = behaviour as IClientOrderDefinition;
                if (definition == null
                    || behaviour == null
                    || !behaviour.enabled
                    || !behaviour.gameObject.activeInHierarchy
                    || behaviour.GetComponentInParent<ClientPlate>() != null
                    || _heldObjects.Contains(behaviour.gameObject.GetInstanceID()))
                {
                    continue;
                }

                try
                {
                    if (!CompositionMatchesOrder(definition.GetOrderComposition(), order)
                        || PlayerControlsHelper.GetControllingPlacementHandler_Client(behaviour.gameObject) == null)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                float distance = HorizontalSqrDistance(player.transform.position, behaviour.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = behaviour.gameObject;
                }
            }
            return best;
        }

        private static bool IsAvailablePlate(ClientPlate plate, ClientPlayerAttachmentCarrier carrier)
        {
            if (plate == null || !plate.enabled || !plate.gameObject.activeInHierarchy)
            {
                return false;
            }

            try
            {
                IClientHandlePickup pickup = PlayerControlsHelper.GetControllingPickupHandler_Client(plate.gameObject);
                return pickup != null && carrier != null && pickup.CanHandlePickup(carrier);
            }
            catch
            {
                return false;
            }
        }

        private static bool PlateMatchesOrder(ClientPlate plate, RecipeList.Entry order)
        {
            if (plate == null || IsEmptyPlate(plate) || !PlateTypeMatchesOrder(plate, order))
            {
                return false;
            }
            try
            {
                return CompositionMatchesOrder(plate.GetOrderComposition(), order);
            }
            catch
            {
                return false;
            }
        }

        private static bool PlateTypeMatchesOrder(ClientPlate plate, RecipeList.Entry order)
        {
            if (plate == null || order == null || order.m_order == null)
            {
                return false;
            }
            Plate plateData = plate.GetComponent<Plate>();
            return plateData != null && plateData.m_platingStep == order.m_order.m_platingStep;
        }

        private static bool CompositionMatchesOrder(AssembledDefinitionNode composition, RecipeList.Entry order)
        {
            if (composition == null
                || composition == AssembledDefinitionNode.NullNode
                || order == null
                || order.m_order == null)
            {
                return false;
            }

            if (order.m_order.GetType() == typeof(WildcardOrderNode))
            {
                return AssembledDefinitionNode.Matching(order.m_order, composition);
            }
            return AssembledDefinitionNode.Matching(composition, order.m_order);
        }

        private ClientPlateStation FindNearestServingStation(PlayerControls player)
        {
            ClientPlateStation best = null;
            float bestDistance = float.PositiveInfinity;
            TeamID team = player.PlayerIDProvider.GetTeam();
            ClientPlateStation[] stations = GetServingStationSnapshot();
            for (int i = 0; i < stations.Length; i++)
            {
                ClientPlateStation station = stations[i];
                PlateStation stationData = station == null ? null : station.GetComponent<PlateStation>();
                if (station == null
                    || !station.enabled
                    || !station.gameObject.activeInHierarchy
                    || stationData == null
                    || stationData.m_teamId != team)
                {
                    continue;
                }

                float distance = HorizontalSqrDistance(player.transform.position, station.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = station;
                }
            }
            return best;
        }

        private void RefreshHeldObjects()
        {
            _heldObjects.Clear();
            ClientPlayerAttachmentCarrier[] carriers = GetCarrierSnapshot();
            for (int i = 0; i < carriers.Length; i++)
            {
                if (carriers[i] == null)
                {
                    continue;
                }
                GameObject item = carriers[i].InspectCarriedItem();
                if (item != null)
                {
                    _heldObjects.Add(item.GetInstanceID());
                }
            }
        }

        private void BeginPlanningSnapshot(PlayerControls player)
        {
            _planningSnapshotActive = true;
            _planningCarrier = player == null
                ? null
                : player.GetComponent<ClientPlayerAttachmentCarrier>();
            _planningPlates = GetPlateSnapshot();
            _planningBehaviours = NoBehaviours;
            _planningBehavioursLoaded = false;
        }

        private void EnsurePlanningBehaviourSnapshot()
        {
            if (_planningSnapshotActive && !_planningBehavioursLoaded)
            {
                _planningBehaviours = _orderDefinitionCache;
                _planningBehavioursLoaded = true;
            }
        }

        private void EndPlanningSnapshot()
        {
            _planningSnapshotActive = false;
            _planningCarrier = null;
            _planningPlates = NoPlates;
            _planningBehaviours = NoBehaviours;
            _planningBehavioursLoaded = false;
        }

        private ClientPlate[] GetPlateSnapshot()
        {
            return _cachedPlates ?? NoPlates;
        }

        private ClientPlateStation[] GetServingStationSnapshot()
        {
            if (Time.unscaledTime >= _nextServingStationRefreshTime)
            {
                _cachedServingStations = UnityEngine.Object.FindObjectsOfType<ClientPlateStation>();
                _nextServingStationRefreshTime = Time.unscaledTime + 2f;
            }
            return _cachedServingStations ?? NoServingStations;
        }

        private ClientPlayerAttachmentCarrier[] GetCarrierSnapshot()
        {
            if (Time.unscaledTime >= _nextCarrierRefreshTime)
            {
                _cachedCarriers = UnityEngine.Object.FindObjectsOfType<ClientPlayerAttachmentCarrier>();
                _nextCarrierRefreshTime = Time.unscaledTime + 1f;
            }
            return _cachedCarriers ?? NoCarriers;
        }

        private bool ScanDefinitionBucket<T>(int bucket)
            where T : MonoBehaviour, IClientOrderDefinition
        {
            T[] components = UnityEngine.Object.FindObjectsOfType<T>();
            return ReplaceDefinitionBucket(bucket, components);
        }

        private bool ReplaceDefinitionBucket<T>(int bucketIndex, T[] components)
            where T : MonoBehaviour, IClientOrderDefinition
        {
            List<MonoBehaviour> bucket = _orderDefinitionBuckets[bucketIndex];
            _scannedDefinitionIds.Clear();
            if (components != null)
            {
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null)
                    {
                        _scannedDefinitionIds.Add(components[i].GetInstanceID());
                    }
                }
            }

            bool unchanged = bucket.Count == _scannedDefinitionIds.Count;
            if (unchanged)
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    MonoBehaviour existing = bucket[i];
                    if (existing == null || !_scannedDefinitionIds.Contains(existing.GetInstanceID()))
                    {
                        unchanged = false;
                        break;
                    }
                }
            }
            if (unchanged)
            {
                return false;
            }

            UnsubscribeDefinitionBucket(bucket);
            bucket.Clear();
            if (components != null)
            {
                for (int i = 0; i < components.Length; i++)
                {
                    T component = components[i];
                    if (component == null)
                    {
                        continue;
                    }
                    bucket.Add(component);
                    try
                    {
                        component.RegisterOrderCompositionChangedCallback(_compositionChangedCallback);
                    }
                    catch
                    {
                    }
                }
            }

            RebuildOrderDefinitionCache();
            OnOrderCompositionChanged(null);
            return true;
        }

        private void UnsubscribeDefinitionBucket(List<MonoBehaviour> bucket)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                IClientOrderDefinition definition = bucket[i] as IClientOrderDefinition;
                if (definition == null)
                {
                    continue;
                }
                try
                {
                    definition.UnregisterOrderCompositionChangedCallback(_compositionChangedCallback);
                }
                catch
                {
                }
            }
        }

        private void RebuildOrderDefinitionCache()
        {
            _orderDefinitionCache.Clear();
            for (int bucketIndex = 0; bucketIndex < _orderDefinitionBuckets.Length; bucketIndex++)
            {
                List<MonoBehaviour> bucket = _orderDefinitionBuckets[bucketIndex];
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (bucket[i] != null)
                    {
                        _orderDefinitionCache.Add(bucket[i]);
                    }
                }
            }
        }

        private void OnOrderCompositionChanged(AssembledDefinitionNode ignored)
        {
            unchecked
            {
                _compositionRevision++;
            }
        }

        private bool TryGetActiveOrders(
            PlayerControls player,
            List<RecipeList.Entry> output,
            out string error)
        {
            output.Clear();
            error = null;
            if (ActiveOrdersField == null || ActiveOrderRecipeField == null)
            {
                error = "ClientOrderControllerBase active-order fields could not be resolved.";
                return false;
            }

            try
            {
                IList activeOrders = GetActiveOrderList(player);
                if (activeOrders == null)
                {
                    return true;
                }

                for (int orderIndex = 0; orderIndex < activeOrders.Count; orderIndex++)
                {
                    object activeOrder = activeOrders[orderIndex];
                    RecipeList.Entry recipe = activeOrder == null
                        ? null
                        : ActiveOrderRecipeField.GetValue(activeOrder) as RecipeList.Entry;
                    if (recipe != null && recipe.m_order != null)
                    {
                        output.Add(recipe);
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private IList GetActiveOrderList(PlayerControls player)
        {
            IList cachedOrders = GetOrderList(_flow, player);
            if (cachedOrders != null)
            {
                return cachedOrders;
            }

            _flow = null;
            ClientKitchenFlowControllerBase[] flows =
                UnityEngine.Object.FindObjectsOfType<ClientKitchenFlowControllerBase>();
            for (int i = 0; i < flows.Length; i++)
            {
                IList activeOrders = GetOrderList(flows[i], player);
                if (activeOrders == null)
                {
                    continue;
                }
                _flow = flows[i];
                return activeOrders;
            }
            return null;
        }

        private static IList GetOrderList(
            ClientKitchenFlowControllerBase flow,
            PlayerControls player)
        {
            if (!IsUsableFlow(flow) || player == null || player.PlayerIDProvider == null)
            {
                return null;
            }

            ClientTeamMonitor monitor = flow.GetMonitorForTeam(player.PlayerIDProvider.GetTeam());
            ClientOrderControllerBase controller = monitor == null ? null : monitor.OrdersController;
            return controller == null ? null : ActiveOrdersField.GetValue(controller) as IList;
        }

        private static bool IsUsableFlow(ClientKitchenFlowControllerBase flow)
        {
            return flow != null && flow.enabled && flow.gameObject.activeInHierarchy;
        }

        private static float HorizontalSqrDistance(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float z = left.z - right.z;
            return x * x + z * z;
        }
    }
}
