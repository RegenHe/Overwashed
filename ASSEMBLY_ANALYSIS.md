# Confirmed Assembly-CSharp symbols

These names and signatures were taken from the locally installed `Overcooked2_Data/Managed/Assembly-CSharp.dll` with `dnSpy.Console.exe`; they are not guessed APIs.

- `PlayerControls.ControlSchemeData`: public fields `m_pickupButton`, `m_worksurfaceUseButton`, `m_dashButton`, `m_moveX`, `m_moveY`; property `Player`.
- `ILogicalButton`: `JustPressed`, `JustReleased`, `IsDown`, claim/event methods, and `GetLogicTreeData`.
- `ILogicalValue`: `GetValue` and `GetLogicTreeData`.
- `PlayerIDProvider`: `IsLocallyControlled()` and `GetID()`.
- `KeyboardUtils.IsKeyboard(PlayerInputLookup.Player)` determines whether the assigned engaged user has `ControlTypeEnum.Keyboard`.
- `ClientPlayerAttachmentCarrier.InspectCarriedItem()` returns the current held item.
- `DirtyPlateStack` is the dirty stack entity; `ClientDirtyPlateStack.GetCount()` exposes the live count.
- `WashingStation.CanHandlePlacement(...)` accepts a carried `DirtyPlateStack`.
- `ClientWashingStation` implements `IClientHandlePlacement`; its confirmed private `m_plateCount` mirrors server add/clean messages.
- `ClientPlayerControlsImpl_Default.Update_Carry()` reacts to `m_pickupButton.JustPressed()` and uses the normal `ClientMessenger.ChefEventMessage` pickup/place flow.
- `ClientPlayerControlsImpl_Default.Update_Interact()` reacts to the workstation-use input and starts/ends the normal interaction flow.
- `ClientInputTransmitter.Setup()` stores the locally controlled player's original movement and action inputs in its private nested `Pad` object (`m_X`, `m_Y`, `m_Pickup`, `m_Worksurface`, `m_Dash`).
- `ClientInputTransmitter.UpdateSynchronising()` reads directly from that `Pad`, builds a `ControllerStateMessage`, and calls `ClientMessenger.ControllerState(...)`; replacing only `PlayerControls.ControlSchemeData` therefore does not transmit bot input from a non-host client.
- `ControllerStateMessage.IsDifferent(...)` compares both movement axes and button state, so the existing transmitter immediately sends bot direction/action changes once its `Pad` references are bound.
- `ServerInputReceiver` applies the transmitted axes/buttons to server-side `NetworkLogicalValue`/`NetworkLogicalButton` objects.
- `ClientChefSynchroniser.FixedUpdate()` calls `RunCorrection()` for the locally controlled remote chef and corrects client prediction toward the server-authoritative position. `ClientOnTheServerChefSynchroniser` does not have this remote correction path.
- `GridManager`: `GetGridLocationFromPos`, `GetUnclampedGridLocationFromPos`, `GetPosFromGridLocation`, `GetGridOccupant`, `GetGridHalfSize`.
- `InteractWithItemHelper` confirms kitchen interaction scans use the four cardinal grid neighbours and a one-unit interaction radius.
