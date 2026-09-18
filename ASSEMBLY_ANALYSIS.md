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
- `ClientKitchenFlowControllerBase.GetMonitorForTeam(TeamID)` provides the local team's `ClientTeamMonitor`; its `OrdersController` points to `ClientOrderControllerBase`.
- `ClientOrderControllerBase.m_activeOrders` contains the live display order, and each nested `ActiveOrder.RecipeListEntry` points to the required `OrderDefinitionNode`.
- `ClientPlate.GetOrderComposition()` and other `IClientOrderDefinition` implementations expose the assembled contents of plated and unplated meals.
- Server order validation first requires `OrderDefinitionNode.m_platingStep` to match the plate and then calls `AssembledDefinitionNode.Matching`; wildcard recipes reverse the normal matching argument order.
- `ClientPlateStation` is the client serving handler and its `PlateStation.m_teamId` identifies the station belonging to the keyboard chef's team.
- `ClientPlacementContainer.CanHandlePlacement(...)` performs symmetric combination, allowing an empty carried plate to collect a matching completed meal through the normal placement input.
- `GroundCast.GetGroundPoint()` exposes the chef's current physical walking-surface height; navigation uses this instead of the transform pivot when rejecting counter tops as floor.
- Grid occupancy is not sufficient on every map: when the chef physically encounters unregistered fixed scenery, the obstructed edge is excluded from the current BFS route and replanned.
- `GameUtils.GetGridManager(Transform)` may return a local manager from the object's parent hierarchy while the chef walks on the global floor manager. Different manager instances are not sufficient evidence that two world positions are disconnected.
- `GridNavSpace.GetNavPoint(Vector3)` maps world positions onto the game's global navigation map, and `GridNavSpace.FindPath(Point2, Point2)` returns the corresponding static kitchen route used by `GridNavigator`.
- `ClientPlayerControlsImpl_Default.Update_Rotation()` rotates the chef from the normal movement axes before applying movement. A short bounded input burst can therefore synchronise final interaction facing without directly mutating the transform.
- `PlayerControlsHelper.GetControlAxis(...)` normalises every non-zero movement vector, so reducing analogue magnitude does not slow the chef; route corners must use zero-input braking frames instead.
- `PlayerControls.Motion.GetVelocityXZ()` exposes the current horizontal speed used to wait for that braking to finish before a 90-degree turn or final-facing pulse.
