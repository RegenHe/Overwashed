using System;
using System.Reflection;
using InControl;
using UnityEngine;

namespace Overcooked2DishwasherBot
{
    internal sealed class VirtualBotController : IDisposable
    {
        internal const string DeviceMeta = "Overwashed.VirtualBotController";

        private static readonly MethodInfo GetActionSetMethod = typeof(PCPadInputProvider).GetMethod(
            "GetActionSet",
            BindingFlags.Static | BindingFlags.NonPublic);

        private OverwashedInputDevice _device;
        private bool _joinPending;
        private bool _removeWhenJoined;

        internal string Status { get; private set; }

        internal bool IsAttached
        {
            get { return _device != null && _device.IsAttached; }
        }

        internal bool IsEngaged
        {
            get
            {
                EngagementSlot ignored;
                return TryGetEngagedSlot(out ignored);
            }
        }

        internal bool HasVirtualPlayer
        {
            get { return _device != null || _joinPending || IsEngaged; }
        }

        internal bool CanModifyVirtualPlayer
        {
            get
            {
                if (OvercookedEngagementController.GetLevelType() == OvercookedEngagementController.LevelType.WithChefs)
                {
                    return false;
                }

                IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
                if (playerManager == null || playerManager.IsBusy())
                {
                    return false;
                }
                return HasVirtualPlayer || playerManager.HasFreeEngagementSlot();
            }
        }

        internal bool TryAddPlayer(out string error)
        {
            error = null;
            if (_joinPending)
            {
                Status = "Virtual player join is already pending.";
                return true;
            }

            EngagementSlot engagedSlot;
            if (TryGetEngagedSlot(out engagedSlot))
            {
                Status = "Virtual bot player is already added as Player " + ((int)engagedSlot + 1) + ".";
                return true;
            }

            if (OvercookedEngagementController.GetLevelType() == OvercookedEngagementController.LevelType.WithChefs)
            {
                error = "Return to a lobby or chef-selection screen before adding the virtual player.";
                Status = error;
                return false;
            }

            IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
            if (playerManager == null)
            {
                error = "The game's local player manager is not available on this screen.";
                Status = error;
                return false;
            }
            if (playerManager.IsBusy())
            {
                error = "The game's player manager is busy; try again in a moment.";
                Status = error;
                return false;
            }
            if (!playerManager.HasFreeEngagementSlot())
            {
                error = "All four local player slots are already occupied.";
                Status = error;
                return false;
            }

            if (!TryAttach(out error))
            {
                Status = error;
                return false;
            }

            ControlPadInput.PadNum sourcePad;
            if (!TryFindVirtualPad(out sourcePad))
            {
                error = "The game attached the virtual controller but did not expose an input slot for it.";
                Status = error;
                Detach();
                return false;
            }

            _joinPending = true;
            Status = "Adding virtual bot player...";
            try
            {
                playerManager.StartPadEngagement(sourcePad, null, OnEngagementFinished);
                return true;
            }
            catch (Exception exception)
            {
                _joinPending = false;
                Detach();
                error = exception.GetType().Name + ": " + exception.Message;
                Status = "Could not add virtual player: " + error;
                return false;
            }
        }

        internal bool TryRemovePlayer(out string error)
        {
            error = null;
            if (OvercookedEngagementController.GetLevelType() == OvercookedEngagementController.LevelType.WithChefs)
            {
                error = "Return to a lobby before removing the virtual player.";
                Status = error;
                return false;
            }

            if (_joinPending)
            {
                _removeWhenJoined = true;
                Status = "Removing virtual bot player...";
                return true;
            }

            IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
            if (playerManager == null)
            {
                error = "The game's local player manager is not available on this screen.";
                Status = error;
                return false;
            }
            if (playerManager.IsBusy())
            {
                error = "The game's player manager is busy; try again in a moment.";
                Status = error;
                return false;
            }
            EngagementSlot slot;
            if (TryGetEngagedSlot(playerManager, out slot))
            {
                playerManager.DisengagePad(slot);
            }
            Detach();
            Status = "Virtual bot controller removed.";
            return true;
        }

        internal bool TryGetVirtualPlayer(out PlayerInputLookup.Player player)
        {
            player = PlayerInputLookup.Player.Count;
            IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
            if (playerManager == null)
            {
                return false;
            }

            for (int index = 0; index < (int)PlayerInputLookup.Player.Count; index++)
            {
                PlayerInputLookup.Player candidate = (PlayerInputLookup.Player)index;
                ControlPadInput.PadNum pad = PlayerInputLookup.GetPadForPlayer(candidate);
                if (pad == ControlPadInput.PadNum.Count || (int)pad >= (int)EngagementSlot.Count)
                {
                    continue;
                }
                if (IsVirtualUser(playerManager.GetUser((EngagementSlot)pad)))
                {
                    player = candidate;
                    return true;
                }
            }
            return false;
        }

        internal bool IsVirtualPlayer(PlayerInputLookup.Player player)
        {
            IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
            if (playerManager == null)
            {
                return false;
            }
            ControlPadInput.PadNum pad = PlayerInputLookup.GetPadForPlayer(player);
            if (pad == ControlPadInput.PadNum.Count || (int)pad >= (int)EngagementSlot.Count)
            {
                return false;
            }
            return IsVirtualUser(playerManager.GetUser((EngagementSlot)pad));
        }

        public void Dispose()
        {
            _joinPending = false;
            _removeWhenJoined = false;
            Detach();
        }

        private bool TryAttach(out string error)
        {
            error = null;
            if (IsAttached)
            {
                return true;
            }
            if (!InputManager.IsSetup)
            {
                error = "The game's input system is not ready yet.";
                return false;
            }

            try
            {
                // Force the provider's static constructor to subscribe to InControl's
                // device events before attaching our device.
                Singleton<PCPadInputProvider>.Get();
                _device = new OverwashedInputDevice();
                InputManager.AttachDevice(_device);
                Status = "Virtual bot controller attached.";
                return true;
            }
            catch (Exception exception)
            {
                _device = null;
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private void OnEngagementFinished(GamepadUser user)
        {
            _joinPending = false;
            if (_removeWhenJoined)
            {
                _removeWhenJoined = false;
                IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
                EngagementSlot joinedSlot;
                if (playerManager != null && TryGetEngagedSlot(playerManager, out joinedSlot))
                {
                    playerManager.DisengagePad(joinedSlot);
                }
                Detach();
                Status = "Virtual bot controller removed.";
                return;
            }
            if (!IsVirtualUser(user))
            {
                Status = "The game rejected the virtual player. Try again from the local chef-selection screen.";
                return;
            }

            EngagementSlot slot;
            if (!TryGetEngagedSlot(out slot))
            {
                Status = "The virtual controller joined, but its local player slot could not be resolved.";
                return;
            }
            Status = "Virtual bot player added as Player " + ((int)slot + 1) + ".";
            // Confirm the randomly selected chef (and colour in versus mode). Extra
            // confirmation pulses are ignored once the card reaches Confirmed state.
            if (_device != null)
            {
                _device.QueueConfirmPulses(3, 0.45f, 0.45f);
            }
        }

        private bool TryFindVirtualPad(out ControlPadInput.PadNum pad)
        {
            pad = ControlPadInput.PadNum.Count;
            if (_device == null || GetActionSetMethod == null)
            {
                return false;
            }

            int maximum = PlayerInputLookup.GetSystemControllerMaximum();
            for (int index = 0; index < maximum; index++)
            {
                ControlPadInput.PadNum candidate = (ControlPadInput.PadNum)index;
                PlayerActionSet actionSet = GetActionSetMethod.Invoke(null, new object[] { candidate }) as PlayerActionSet;
                if (actionSet != null && actionSet.Device == _device)
                {
                    pad = candidate;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetEngagedSlot(out EngagementSlot slot)
        {
            IPlayerManager playerManager = GameUtils.RequestManagerInterface<IPlayerManager>();
            return TryGetEngagedSlot(playerManager, out slot);
        }

        private static bool TryGetEngagedSlot(IPlayerManager playerManager, out EngagementSlot slot)
        {
            slot = EngagementSlot.Count;
            if (playerManager == null)
            {
                return false;
            }
            for (int index = 0; index < (int)EngagementSlot.Count; index++)
            {
                EngagementSlot candidate = (EngagementSlot)index;
                if (IsVirtualUser(playerManager.GetUser(candidate)))
                {
                    slot = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool IsVirtualUser(GamepadUser user)
        {
            return user != null && string.Equals(user.UID, DeviceMeta, StringComparison.Ordinal);
        }

        private void Detach()
        {
            if (_device == null)
            {
                return;
            }
            if (_device.IsAttached)
            {
                InputManager.DetachDevice(_device);
            }
            _device = null;
        }
    }

    internal sealed class OverwashedInputDevice : InputDevice
    {
        private int _confirmPulsesRemaining;
        private float _nextConfirmTime;
        private float _confirmUpTime;
        private float _confirmInterval;
        private bool _confirmDown;

        internal OverwashedInputDevice()
            : base("Overwashed Virtual Controller")
        {
            Meta = VirtualBotController.DeviceMeta;
            AddStandardControl(InputControlType.Action1);
            AddStandardControl(InputControlType.Action2);
            AddStandardControl(InputControlType.Action3);
            AddStandardControl(InputControlType.Action4);
            AddStandardControl(InputControlType.LeftBumper);
            AddStandardControl(InputControlType.RightBumper);
            AddStandardControl(InputControlType.LeftTrigger);
            AddStandardControl(InputControlType.RightTrigger);
            AddStandardControl(InputControlType.LeftStickButton);
            AddStandardControl(InputControlType.RightStickButton);
            AddStandardControl(InputControlType.LeftStickUp);
            AddStandardControl(InputControlType.LeftStickDown);
            AddStandardControl(InputControlType.LeftStickLeft);
            AddStandardControl(InputControlType.LeftStickRight);
            AddStandardControl(InputControlType.RightStickUp);
            AddStandardControl(InputControlType.RightStickDown);
            AddStandardControl(InputControlType.RightStickLeft);
            AddStandardControl(InputControlType.RightStickRight);
            AddStandardControl(InputControlType.DPadUp);
            AddStandardControl(InputControlType.DPadDown);
            AddStandardControl(InputControlType.DPadLeft);
            AddStandardControl(InputControlType.DPadRight);
            AddStandardControl(InputControlType.Start);
            AddStandardControl(InputControlType.Options);
            AddStandardControl(InputControlType.Back);
        }

        internal void QueueConfirmPulses(int count, float firstDelay, float interval)
        {
            _confirmPulsesRemaining = Math.Max(0, count);
            _confirmDown = false;
            _nextConfirmTime = Time.unscaledTime + Mathf.Max(0f, firstDelay);
            _confirmInterval = Mathf.Max(0.2f, interval);
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            float now = Time.unscaledTime;
            if (_confirmDown && now >= _confirmUpTime)
            {
                _confirmDown = false;
                _confirmPulsesRemaining--;
                _nextConfirmTime = now + _confirmInterval;
            }
            else if (!_confirmDown && _confirmPulsesRemaining > 0 && now >= _nextConfirmTime)
            {
                _confirmDown = true;
                _confirmUpTime = now + 0.12f;
            }
            GetControl(InputControlType.Action1).UpdateWithState(_confirmDown, updateTick, deltaTime);
        }

        private void AddStandardControl(InputControlType type)
        {
            AddControl(type, type.ToString());
        }
    }
}
