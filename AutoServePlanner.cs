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

            if (!TryGetActiveOrders(player, _orders, out error) || _orders.Count == 0)
            {
                return false;
            }

            ClientPlateStation station = FindNearestServingStation(player);
            if (station == null)
            {
                return false;
            }

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
            ClientPlayerAttachmentCarrier carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
            ClientPlate[] plates = UnityEngine.Object.FindObjectsOfType<ClientPlate>();
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
            ClientPlayerAttachmentCarrier carrier = player.GetComponent<ClientPlayerAttachmentCarrier>();
            ClientPlate[] plates = UnityEngine.Object.FindObjectsOfType<ClientPlate>();
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
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
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

        private static ClientPlateStation FindNearestServingStation(PlayerControls player)
        {
            ClientPlateStation best = null;
            float bestDistance = float.PositiveInfinity;
            TeamID team = player.PlayerIDProvider.GetTeam();
            ClientPlateStation[] stations = UnityEngine.Object.FindObjectsOfType<ClientPlateStation>();
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
            ClientPlayerAttachmentCarrier[] carriers = UnityEngine.Object.FindObjectsOfType<ClientPlayerAttachmentCarrier>();
            for (int i = 0; i < carriers.Length; i++)
            {
                GameObject item = carriers[i].InspectCarriedItem();
                if (item != null)
                {
                    _heldObjects.Add(item.GetInstanceID());
                }
            }
        }

        private static bool TryGetActiveOrders(
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
                ClientKitchenFlowControllerBase[] flows = UnityEngine.Object.FindObjectsOfType<ClientKitchenFlowControllerBase>();
                for (int i = 0; i < flows.Length; i++)
                {
                    ClientKitchenFlowControllerBase flow = flows[i];
                    if (flow == null || !flow.enabled || !flow.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    ClientTeamMonitor monitor = flow.GetMonitorForTeam(player.PlayerIDProvider.GetTeam());
                    ClientOrderControllerBase controller = monitor == null ? null : monitor.OrdersController;
                    IList activeOrders = controller == null ? null : ActiveOrdersField.GetValue(controller) as IList;
                    if (activeOrders == null)
                    {
                        continue;
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
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static float HorizontalSqrDistance(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float z = left.z - right.z;
            return x * x + z * z;
        }
    }
}
