# Overcooked 2 Dishwasher Bot

BepInEx 5 plugin for the local Mono build of **Overcooked! 2**. Press **F8** to toggle it.

Current version: **1.1.0**.

### 1.1.0 configurable automatic player avoidance

- Press `F7` to open the built-in settings panel, or click the active bot icon in the top-right corner.
- `Auto avoidance` enables or disables moving away from nearby chefs.
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

The bot logs keyboard-player detection, target selection, state changes, waits, and exceptions to `BepInEx\LogOutput.log`. Its loop is:

1. Find an active `ClientDirtyPlateStack` with `GetCount() > 0`.
2. Navigate to an adjacent walkable grid cell using `GridManager` occupancy and physics ground checks.
3. Feed the normal pickup button through `ILogicalButton`.
4. Find the nearest active `ClientWashingStation`, navigate, and feed the normal placement button.
5. Hold the normal workstation-interact input until `m_plateCount` reaches zero.

If the keyboard chef is carrying any non-dirty item when enabled, the bot waits instead of throwing away the player's item.
