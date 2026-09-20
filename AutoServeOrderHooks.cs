using HarmonyLib;

namespace Overcooked2DishwasherBot
{
    [HarmonyPatch(typeof(ClientOrderControllerBase), "AddNewOrder")]
    internal static class AutoServeOrderAddedPatch
    {
        private static void Postfix()
        {
            AutoServePlanner.NotifyOrdersChanged();
        }
    }

    [HarmonyPatch(typeof(ClientOrderControllerBase), "OnFoodDelivered")]
    internal static class AutoServeOrderDeliveredPatch
    {
        private static void Postfix(bool _success)
        {
            if (_success)
            {
                AutoServePlanner.NotifyOrdersChanged();
            }
        }
    }

    [HarmonyPatch(typeof(ClientOrderControllerBase), "OnOrderExpired")]
    internal static class AutoServeOrderExpiredPatch
    {
        private static void Postfix()
        {
            AutoServePlanner.NotifyOrdersChanged();
        }
    }
}
