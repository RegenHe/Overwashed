# Overcooked 2 Dishwasher Bot

BepInEx 5 plugin for the local Mono build of **Overcooked! 2**. Press **F8** to toggle it.

Current version: **1.4.1**.

### 1.4.1 dash-aware replanning and chef detours

- Rebuilds the route after the chef moves approximately one grid tile from the last plan, rather than relying only on a timer.
- Entering a dash requests an immediate route refresh; while dash motion continues, the route is rebuilt after each `0.45` tile of displacement so overshoot is incorporated quickly.
- Detects when momentum has already carried the chef beyond a non-final waypoint and advances the path cursor instead of turning around to chase that old point.
- Treats other active chefs' current grid cells as temporary BFS obstacles. If another chef blocks the next movement segment, the bot immediately requests a route around them when the kitchen has an alternate passage.
- A genuinely single-width passage still waits for the blocking chef, because no collision-free detour exists.

### 1.4.0 optional speed modes

- Adds `Fast interactions`, disabled by default and saved through BepInEx configuration.
- Fast mode reduces target scans, pickup/action retries, interaction-cell rejection, sink recovery, and serving acknowledgement waits to approximately `0.1`–`0.15` seconds. Its pickup pulse is shortened so repeated attempts still generate distinct button presses.
- Adds `Extreme dash mode`, also disabled by default and independently configurable.
- Extreme mode starts repeated normal dash-input pulses whenever more than `1.1` route tiles remain. It intentionally ignores upcoming turns for maximum movement speed, but still requires the chef to face generally along the route and refuses to dash into an immediate collider or during the final interaction approach.
- Normal mode retains the conservative three-tile straight-route dash and all prior timings.

### 1.3.3 interaction-facing and sink recovery

- Faces from the selected interaction-cell centre toward the target grid cell instead of aiming at a potentially offset model or attachment Transform.
- Uses the game's confirmed `PlayerControlsHelper.TurnTowardsDirection` for in-place local alignment while retaining normal movement input as the network-synchronised fallback.
- Removes the close-range shortcut that could enter interaction mode without recording a recoverable interaction cell.
- Applies rejected-cell handling to cardinal sink approaches as well as eight-way pickup approaches.
- If placement or washing is still not selected after `0.65` seconds at the sink, the bot rejects that side and routes to another reachable side; exhausted sides automatically begin a fresh search cycle rather than leaving the bot idle.

### 1.3.2 movement and interaction speed

- Brakes only to a safe turning speed at sharp grid corners instead of waiting until the chef is almost stationary.
- Replaces the single-frame target-facing cycle with a bounded `0.12`-second continuous input pulse, greatly reducing the pause before pickup, placement, and washing.
- Uses the game's normal dash input only when at least three grid tiles of the current route are straight and a forward collision sweep is clear. Dashing is disabled near turns, targets, and avoidance routes.
- Reduces failed pickup/placement action retry cooldown from `0.65` to `0.35` seconds without changing the initial action timing.

### 1.3.1 route-following stability fix

- Restores the proven `1.2.2` connected-kitchen BFS navigator after the native `GridNavSpace` rewrite caused regressions on previously working layouts.
- Keeps the selected interaction side stable across periodic replans instead of alternating between equal-length goals around a station.
- Requires the chef to brake at grid corners and at the final interaction cell before changing direction, preventing movement inertia from cutting into counters.
- Removes free-form left/right steering from normal route following. A blocked route now stops and replans through the grid instead of orbiting a table edge.
- Final target alignment alternates normal input turning pulses with braking frames, so facing is corrected through the host/client input path without continuously pushing into a counter.

### 1.3.0 native navigation rewrite

- Replaces the Mod's accumulated custom BFS, ground-ray, local-grid projection, static-edge blacklist, and steering heuristics with the game's confirmed `GridNavSpace.GetNavPoint` and `GridNavSpace.FindPath` APIs.
- Uses the same global static kitchen navigation map that the game's `GridNavigator` consumes, avoiding disagreements between local station grids and the actual connected floor layout.
- The Mod now only chooses a reachable interaction cell, follows the native route, waits for temporary chef obstructions, and asks the game for a fresh route when forward progress stops.
- Adds an explicit final facing phase. At the interaction cell the bot sends a maximum `0.22`-second normal movement-input burst toward the target, then stops; this rotates through the normal host/client input pipeline without returning to unlimited straight-line pushing.
- Keeps adaptive interaction-cell rejection: if the game's pickup/placement scan still does not select the target, another reachable side is tried.

### 1.2.2 connected-kitchen navigation fix

- Projects every target onto the chef's current walking grid when the target's `StaticGridLocation` belongs to a different local grid, rather than treating different `GridManager` instances as physically disconnected kitchens.
- Replaces the eager physics scan of every BFS edge from 1.2.1, which could mistake protruding scenery for a sealed passage and divide a connected map into left/right regions.
- Static obstacles are now learned only when the chef actually reaches a blocked path edge. That edge is rejected for the current target and BFS immediately searches for another route.
- Keeps the stricter floor-height and final-approach checks from 1.2.1, so counter tops are still excluded and the bot still stops instead of walking straight into an obstruction.

### 1.2.1 static-obstacle pathfinding fix

- Uses the chef's actual `GroundCast` contact height as the walking surface and rejects counter tops that the previous `1.1`-unit height tolerance could mistake for floor.
- Validates every BFS edge with a horizontal physics sweep, so unregistered tables, walls, and fixed props cannot be crossed just because both grid-cell centres have ground beneath them.
- Static collision checks now apply while following ordinary waypoints; the eventual target is exempted only during the final interaction-facing step.
- When the final approach is blocked by an unrelated object, the bot stops and rejects that interaction cell instead of abandoning the route and steering straight into the obstruction.

### 1.2.0 optional automatic serving

- Adds `Auto serve completed orders`, disabled by default. When enabled, the bot reads the team's live order list and serves an already completed matching plate before returning to dishwashing.
- If the completed meal is not plated, the bot finds a compatible empty plate, picks it up through the game's normal input path, plates the meal, and delivers it to the team's serving station.
- Adds `Serve in order`, enabled by default. When enabled only the oldest active order is eligible; when disabled the bot prefers the nearest ready active order.
- Recipe matching uses the game's `AssembledDefinitionNode.Matching` logic, including wildcard orders. No recipe names or map coordinates are hard-coded.
- Both options are available in the panel opened by clicking the top-right bot icon and are saved by BepInEx.

### 1.1.1 settings access and default

- The settings panel is opened and closed only by clicking the active bot icon; the `F7` shortcut was removed.
- Automatic avoidance is disabled by default.

### 1.1.0 configurable automatic player avoidance

- Click the active bot icon in the top-right corner to open the built-in settings panel.
- `Auto avoidance` enables or disables moving away from nearby chefs and is disabled by default.
- `Avoidance distance` is adjustable from `0.5` to `4.0` grid tiles and defaults to `1.5`.
- Avoidance uses reachable grid cells rather than moving blindly away, pauses the current dishwashing task, and resumes it when the area is clear.
- A `0.35`-tile release margin prevents repeated toggling when another chef stands at the configured boundary.
- Settings are stored through the standard BepInEx configuration system in `BepInEx/config/local.overcooked2.dishwasherbot.cfg`. They also appear automatically if BepInEx Configuration Manager is installed later.

### 1.0.10 remote-client input synchronisation

- Sends the bot's movement, pickup, interaction, and dash state through the game's existing `ClientInputTransmitter` when the keyboard chef belongs to a non-host player.
- Uses the same logical input objects for local prediction and host-authoritative simulation, preventing the host from repeatedly correcting the bot back to its previous position.
- Restores the transmitter's original keyboard inputs when F8 disables the bot, the chef is rebuilt, or the plugin unloads.

### 1.0.9 adaptive pickup approach

- Searches all eight walkable cells around the dirty-plate attachment point, including diagonal approaches.
- Rejects an approach cell when the game's native pickup scan still does not select the target after 0.75 seconds, then paths to the next candidate.
- Replaces the previous no-op timer that could leave the chef permanently standing at the wrong side of a plate return.

### 1.0.8 lower plate-return fix

- Uses `AttachStation.m_attachPoint`, the exact position where the dirty stack is mounted, as the navigation and facing target.
- Does not inherit a parent `StaticGridLocation` for an offset attach point; lower-facing serving hatches can place that point one cell to the side of the station root.

### 1.0.7 plate-return alignment fix

- Navigates to the `ClientAttachStation` that actually handles pickup instead of the attached dirty-stack model pivot.
- Uses the target's registered `StaticGridLocation.GridIndex` when available rather than reconstructing its cell from a potentially offset transform.
- Applies the same interaction target when choosing the nearest dirty stack, fixing lower-facing plate returns whose attachment point is one cell away from the station root.

### 1.0.6 indicator placement

- Anchors the enabled indicator to the top-right corner instead of the top-left.
- Shows only the compact plate-and-bubbles icon; the text label has been removed.

### 1.0.5 enabled indicator

- Shows a compact plate-and-bubbles `DISH BOT ON` badge in the top-left corner while the bot is enabled.
- Hides the badge immediately when F8 disables the bot.
- Generates the icon in memory and uses draw-only IMGUI calls, so no image asset is required and the badge does not consume mouse input.

### 1.0.4 production logging

- Removes routine state, target, navigation, waiting, and pickup-diagnostic log messages.
- Keeps only one load message, F8 enable/disable messages, startup compatibility warnings, and genuine errors.
- Deduplicates runtime errors for the lifetime of the process so a persistent failure cannot flood `LogOutput.log`.

### 1.0.3 unexpected-item recovery

- Drops any non-dirty item placed in the bot chef's hands onto the floor instead of waiting forever.
- Uses the game's native no-target `ChefEventType.Take` path, so a nearby sink, pot, or worktop cannot receive the unwanted item accidentally.
- Stops movement and releases pickup/use input while waiting for the server carry-state update.

### 1.0.2 pickup fix

- Recognises plate-return stations that forward pickup handling through `ClientAttachStation`.
- Verifies the attached item is the selected dirty stack and that the game's pickup handler accepts the chef before pressing pickup.
- Logs the original scanned object, forwarded handler, attached item, and pickup eligibility when the chef reaches the target but cannot pick it up.

### 1.0.1 navigation fix

- Projects elevated worktop targets onto the chef's current walking-grid Y layer before pathfinding.
- Stops safely when no adjacent cell is reachable instead of steering directly through counters.
- Filters the chef and carried objects out of ground probes.
- Treats only other chefs and moving rigidbodies as local dynamic obstacles.
- Locks an avoidance choice briefly so left/right decisions cannot flip every frame.
- Logs `playerCell`, `targetCell`, and the navigation reason when movement is paused.

The plugin finds the existing locally controlled keyboard chef; it never creates a player or a virtual controller. While active, it replaces that chef's existing `ControlSchemeData` logical axes/buttons with wrappers that feed the game's normal `PlayerControlsImpl_Default` pipeline. Disabling restores the original logical inputs and explicitly ends any current sticky interaction.

## Build and deploy

From this directory:

```powershell
.\build.ps1 -Configuration Release
```

The project and build script reference the game's own Mono/.NET, Unity, `Assembly-CSharp.dll`, and BepInEx assemblies. The script invokes the installed SDK's Roslyn compiler with `nostdlib`, so it does not download .NET 3.5 reference packs or create files outside the game directory. A successful build copies `Overcooked2.DishwasherBot.dll` to `BepInEx\plugins` automatically.

## Runtime states and logging

Production logging is limited to plugin load, F8 enable/disable, compatibility warnings, and deduplicated errors. Its normal dishwashing loop is:

1. Find an active `ClientDirtyPlateStack` with `GetCount() > 0`.
2. Navigate to an adjacent walkable grid cell using `GridManager` occupancy and physics ground checks.
3. Feed the normal pickup button through `ILogicalButton`.
4. Find the nearest active `ClientWashingStation`, navigate, and feed the normal placement button.
5. Hold the normal workstation-interact input until `m_plateCount` reaches zero.

If automatic serving is disabled, or a carried item is not a plate matching an eligible active order, the bot drops the unexpected item on the floor and resumes work.
