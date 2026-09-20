using UnityEngine;

namespace Overcooked2DishwasherBot
{
    internal sealed class RoundTimeReader
    {
        private ClientKitchenFlowControllerBase _flow;

        internal bool TryRead(out bool inRound, out float remainingSeconds)
        {
            inRound = false;
            remainingSeconds = 0f;

            ClientKitchenFlowControllerBase flow = GetFlow();
            if (flow == null)
            {
                return false;
            }

            inRound = flow.InRound;
            if (!inRound)
            {
                return true;
            }

            IClientRoundTimer timer = flow.RoundTimer;
            if (timer == null || timer is ClientUnlimitedRoundTimer)
            {
                return false;
            }

            if (timer is ClientModifiableRoundTimer)
            {
                // Survival mode counts this value down even though the common interface
                // retains the historical TimeElapsed property name.
                remainingSeconds = Mathf.Max(0f, timer.TimeElapsed);
                return true;
            }

            KitchenLevelConfigBase level = GameUtils.GetLevelConfig() as KitchenLevelConfigBase;
            if (level == null)
            {
                return false;
            }

            float timeLimit = level.GetTimeLimit();
            if (timeLimit <= 0f)
            {
                return false;
            }

            remainingSeconds = Mathf.Max(0f, timeLimit - timer.TimeElapsed);
            return true;
        }

        internal void Clear()
        {
            _flow = null;
        }

        private ClientKitchenFlowControllerBase GetFlow()
        {
            if (IsUsable(_flow) && _flow.InRound)
            {
                return _flow;
            }

            ClientKitchenFlowControllerBase fallback = IsUsable(_flow) ? _flow : null;
            _flow = null;
            ClientKitchenFlowControllerBase[] flows =
                Object.FindObjectsOfType<ClientKitchenFlowControllerBase>();
            for (int i = 0; i < flows.Length; i++)
            {
                if (!IsUsable(flows[i]))
                {
                    continue;
                }
                if (flows[i].InRound)
                {
                    _flow = flows[i];
                    return _flow;
                }
                if (_flow == null)
                {
                    _flow = flows[i];
                }
            }
            if (_flow == null)
            {
                _flow = fallback;
            }
            return _flow;
        }

        private static bool IsUsable(ClientKitchenFlowControllerBase flow)
        {
            return flow != null && flow.enabled && flow.gameObject.activeInHierarchy;
        }
    }
}
