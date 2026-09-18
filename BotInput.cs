using System;
using System.Reflection;

namespace Overcooked2DishwasherBot
{
    internal sealed class BotLogicalValue : ILogicalValue
    {
        private readonly ILogicalValue _original;

        internal BotLogicalValue(ILogicalValue original)
        {
            _original = original;
        }

        internal bool Enabled { get; set; }
        internal float Value { get; set; }

        public float GetValue()
        {
            return Enabled ? Value : (_original == null ? 0f : _original.GetValue());
        }

        public void GetLogicTreeData(
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo> graph,
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node head)
        {
            _original.GetLogicTreeData(out graph, out head);
        }
    }

    internal sealed class BotLogicalButton : LogicalButtonBase
    {
        private readonly ILogicalButton _original;

        internal BotLogicalButton(ILogicalButton original)
        {
            _original = original;
        }

        internal bool Enabled { get; set; }
        internal bool Down { get; set; }

        public override bool IsDown()
        {
            return Enabled ? Down : (_original != null && _original.IsDown());
        }

        public override void GetLogicTreeData(
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo> graph,
            out AcyclicGraph<ILogicalElement, LogicalLinkInfo>.Node head)
        {
            _original.GetLogicTreeData(out graph, out head);
        }
    }

    internal sealed class BotInputBinding
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo NetworkPadField = typeof(ClientInputTransmitter).GetField(
            "m_Pad",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type NetworkPadType = typeof(ClientInputTransmitter).GetNestedType(
            "Pad",
            BindingFlags.NonPublic);
        private static readonly FieldInfo NetworkMoveXField = GetNetworkPadField("m_X");
        private static readonly FieldInfo NetworkMoveYField = GetNetworkPadField("m_Y");
        private static readonly FieldInfo NetworkPickupField = GetNetworkPadField("m_Pickup");
        private static readonly FieldInfo NetworkUseField = GetNetworkPadField("m_Worksurface");
        private static readonly FieldInfo NetworkDashField = GetNetworkPadField("m_Dash");

        private readonly PlayerControls.ControlSchemeData _scheme;
        private readonly ILogicalButton _originalPickup;
        private readonly ILogicalButton _originalUse;
        private readonly ILogicalButton _originalDash;
        private readonly ILogicalValue _originalMoveX;
        private readonly ILogicalValue _originalMoveY;

        private readonly BotLogicalButton _pickup;
        private readonly BotLogicalButton _use;
        private readonly BotLogicalButton _dash;
        private readonly BotLogicalValue _moveX;
        private readonly BotLogicalValue _moveY;

        private object _networkPad;
        private ILogicalButton _originalNetworkPickup;
        private ILogicalButton _originalNetworkUse;
        private ILogicalButton _originalNetworkDash;
        private ILogicalValue _originalNetworkMoveX;
        private ILogicalValue _originalNetworkMoveY;

        internal BotInputBinding(PlayerControls.ControlSchemeData scheme)
        {
            if (scheme == null)
            {
                throw new ArgumentNullException("scheme");
            }

            _scheme = scheme;
            _originalPickup = scheme.m_pickupButton;
            _originalUse = scheme.m_worksurfaceUseButton;
            _originalDash = scheme.m_dashButton;
            _originalMoveX = scheme.m_moveX;
            _originalMoveY = scheme.m_moveY;

            _pickup = new BotLogicalButton(_originalPickup);
            _use = new BotLogicalButton(_originalUse);
            _dash = new BotLogicalButton(_originalDash);
            _moveX = new BotLogicalValue(_originalMoveX);
            _moveY = new BotLogicalValue(_originalMoveY);

            scheme.m_pickupButton = _pickup;
            scheme.m_worksurfaceUseButton = _use;
            scheme.m_dashButton = _dash;
            scheme.m_moveX = _moveX;
            scheme.m_moveY = _moveY;
            SetEnabled(true);
            ReleaseAll();
        }

        internal bool IsInstalled
        {
            get
            {
                return ReferenceEquals(_scheme.m_pickupButton, _pickup)
                    && ReferenceEquals(_scheme.m_worksurfaceUseButton, _use)
                    && ReferenceEquals(_scheme.m_dashButton, _dash)
                    && ReferenceEquals(_scheme.m_moveX, _moveX)
                    && ReferenceEquals(_scheme.m_moveY, _moveY);
            }
        }

        // A remote client's local PlayerControls and ClientInputTransmitter do not read
        // from the same references after setup. Bind both to the same bot values so the
        // host receives exactly the input used by client-side prediction.
        internal bool TryInstallNetworkInput(PlayerControls player, out string error)
        {
            error = null;
            if (player == null)
            {
                return false;
            }

            // The host/server simulates its local chef directly. An offline chef also has
            // no transmitter, so neither case needs a second input binding.
            if (player.GetComponent<ServerInputReceiver>() != null)
            {
                RestoreNetworkInput();
                return true;
            }

            ClientInputTransmitter transmitter = player.GetComponent<ClientInputTransmitter>();
            if (transmitter == null)
            {
                RestoreNetworkInput();
                return true;
            }

            if (NetworkPadField == null
                || NetworkMoveXField == null
                || NetworkMoveYField == null
                || NetworkPickupField == null
                || NetworkUseField == null
                || NetworkDashField == null)
            {
                error = "ClientInputTransmitter.Pad fields could not be resolved.";
                return false;
            }

            try
            {
                object pad = NetworkPadField.GetValue(transmitter);
                if (pad == null)
                {
                    // ClientInputTransmitter.Setup has not run yet. Keep the chef still
                    // and retry on the next update instead of creating an unsynchronised
                    // local movement frame.
                    return false;
                }

                if (ReferenceEquals(_networkPad, pad)
                    && ReferenceEquals(NetworkMoveXField.GetValue(pad), _moveX)
                    && ReferenceEquals(NetworkMoveYField.GetValue(pad), _moveY)
                    && ReferenceEquals(NetworkPickupField.GetValue(pad), _pickup)
                    && ReferenceEquals(NetworkUseField.GetValue(pad), _use)
                    && ReferenceEquals(NetworkDashField.GetValue(pad), _dash))
                {
                    return true;
                }

                RestoreNetworkInput();

                ILogicalValue originalMoveX = NetworkMoveXField.GetValue(pad) as ILogicalValue;
                ILogicalValue originalMoveY = NetworkMoveYField.GetValue(pad) as ILogicalValue;
                ILogicalButton originalPickup = NetworkPickupField.GetValue(pad) as ILogicalButton;
                ILogicalButton originalUse = NetworkUseField.GetValue(pad) as ILogicalButton;
                ILogicalButton originalDash = NetworkDashField.GetValue(pad) as ILogicalButton;
                if (originalMoveX == null
                    || originalMoveY == null
                    || originalPickup == null
                    || originalUse == null
                    || originalDash == null)
                {
                    error = "ClientInputTransmitter.Pad has not finished initialising.";
                    return false;
                }

                _networkPad = pad;
                _originalNetworkMoveX = originalMoveX;
                _originalNetworkMoveY = originalMoveY;
                _originalNetworkPickup = originalPickup;
                _originalNetworkUse = originalUse;
                _originalNetworkDash = originalDash;

                NetworkMoveXField.SetValue(pad, _moveX);
                NetworkMoveYField.SetValue(pad, _moveY);
                NetworkPickupField.SetValue(pad, _pickup);
                NetworkUseField.SetValue(pad, _use);
                NetworkDashField.SetValue(pad, _dash);
                return true;
            }
            catch (Exception exception)
            {
                RestoreNetworkInput();
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        internal void SetEnabled(bool enabled)
        {
            _pickup.Enabled = enabled;
            _use.Enabled = enabled;
            _dash.Enabled = enabled;
            _moveX.Enabled = enabled;
            _moveY.Enabled = enabled;
        }

        internal void SetWorldDirection(Vector3Like direction, PlayerControls.MovementData movement)
        {
            float xSign = movement.XAxisAllignment == PlayerControls.InversionType.Normal ? 1f : -1f;
            float ySign = movement.YAxisAllignment == PlayerControls.InversionType.Normal ? 1f : -1f;
            _moveX.Value = direction.X * xSign;
            _moveY.Value = -direction.Z * ySign;
        }

        internal void SetPickup(bool down)
        {
            _pickup.Down = down;
        }

        internal void SetUse(bool down)
        {
            _use.Down = down;
        }

        internal void ReleaseAll()
        {
            _moveX.Value = 0f;
            _moveY.Value = 0f;
            _pickup.Down = false;
            _use.Down = false;
            _dash.Down = false;
        }

        internal void Restore()
        {
            ReleaseAll();
            SetEnabled(false);
            RestoreNetworkInput();

            if (ReferenceEquals(_scheme.m_pickupButton, _pickup))
            {
                _scheme.m_pickupButton = _originalPickup;
            }
            if (ReferenceEquals(_scheme.m_worksurfaceUseButton, _use))
            {
                _scheme.m_worksurfaceUseButton = _originalUse;
            }
            if (ReferenceEquals(_scheme.m_dashButton, _dash))
            {
                _scheme.m_dashButton = _originalDash;
            }
            if (ReferenceEquals(_scheme.m_moveX, _moveX))
            {
                _scheme.m_moveX = _originalMoveX;
            }
            if (ReferenceEquals(_scheme.m_moveY, _moveY))
            {
                _scheme.m_moveY = _originalMoveY;
            }
        }

        private void RestoreNetworkInput()
        {
            if (_networkPad == null)
            {
                return;
            }

            try
            {
                if (NetworkPickupField != null && ReferenceEquals(NetworkPickupField.GetValue(_networkPad), _pickup))
                {
                    NetworkPickupField.SetValue(_networkPad, _originalNetworkPickup);
                }
                if (NetworkUseField != null && ReferenceEquals(NetworkUseField.GetValue(_networkPad), _use))
                {
                    NetworkUseField.SetValue(_networkPad, _originalNetworkUse);
                }
                if (NetworkDashField != null && ReferenceEquals(NetworkDashField.GetValue(_networkPad), _dash))
                {
                    NetworkDashField.SetValue(_networkPad, _originalNetworkDash);
                }
                if (NetworkMoveXField != null && ReferenceEquals(NetworkMoveXField.GetValue(_networkPad), _moveX))
                {
                    NetworkMoveXField.SetValue(_networkPad, _originalNetworkMoveX);
                }
                if (NetworkMoveYField != null && ReferenceEquals(NetworkMoveYField.GetValue(_networkPad), _moveY))
                {
                    NetworkMoveYField.SetValue(_networkPad, _originalNetworkMoveY);
                }
            }
            catch
            {
                // The old network object may already have been destroyed during a scene
                // transition. There is nothing useful left to restore in that case.
            }

            _networkPad = null;
            _originalNetworkPickup = null;
            _originalNetworkUse = null;
            _originalNetworkDash = null;
            _originalNetworkMoveX = null;
            _originalNetworkMoveY = null;
        }

        private static FieldInfo GetNetworkPadField(string name)
        {
            return NetworkPadType == null ? null : NetworkPadType.GetField(name, InstanceFields);
        }
    }

    // Keeps BotInput independent from UnityEngine so it can be tested and reasoned about simply.
    internal struct Vector3Like
    {
        internal float X;
        internal float Z;

        internal Vector3Like(float x, float z)
        {
            X = x;
            Z = z;
        }
    }
}
