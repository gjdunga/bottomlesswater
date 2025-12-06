BottomlessWater (Oxide/uMod, Rust)

Keep your players hydrated with ease! BottomlessWater is an Oxide/uMod plugin for the game Rust that automatically tops up any liquid container owned by a player. It’s persistent across restarts, per‑player toggled, admin‑controllable, and now includes optional whitelist and exclude lists, logging, chat cooldowns and better performance.

Features

✅ Per‑player toggle with persistence – players can enable or disable bottomless water on their own items and the state is saved.

✅ Improved performance – containers are cached and processed directly rather than iterating every server entity each tick.

✅ Whitelist/Exclude lists – explicitly include or exclude prefab short names when deciding which containers to fill.

✅ Configurable chat cooldown – avoid abuse of the /bw toggle with a cooldown between toggles.

✅ Admin console commands – manage bottomless water for any player via RCON/console (bottomlesswater.toggle, bottomlesswater.status, bottomlesswater.reload).

✅ Logging – player and admin enable/disable actions are written to the console and oxide/logs/BottomlessWater.txt.

✅ Config reload & RCON lockdown – reload the configuration at runtime and optionally require bottomlesswater.admin for console commands.

Installation

Copy BottomlessWater.cs to your Rust server at:

oxide/plugins/BottomlessWater.cs


Start or reload your server. The plugin will automatically generate:

A configuration file: oxide/config/BottomlessWater.json

A data file: oxide/data/BottomlessWaterData.json

A log file: oxide/logs/BottomlessWater.txt

To (re)load configuration after editing the JSON file, run:

bottomlesswater.reload


To recreate the default config, delete oxide/config/BottomlessWater.json and reload the plugin.

Permissions

The plugin registers two permissions:

Permission	Description
bottomlesswater.use	Allows a player to use /bw and receive bottomless water.
bottomlesswater.admin	Allows running admin console/RCON commands.

By default, bottomlesswater.use is granted to the default group. You can disable this in the config by setting "AutoGrantUseToDefaultGroup": false.

Commands
Chat (players)

Players can manage their own bottomless water state:

/bw on       – Enable bottomless water on your containers
/bw off      – Disable bottomless water
/bw toggle   – Toggle your current state
/bw status   – Show your current state


There is a short cooldown between toggles to prevent spamming. You must have the bottomlesswater.use permission to use these commands.

Console / RCON (admins)

Administrators (with bottomlesswater.admin) can control or query other players’ states and reload the configuration:

bottomlesswater.toggle <player> <on|off|toggle>  – Set a player’s state
bottomlesswater.status [player]                  – Show one or all players’ states
bottomlesswater.reload                          – Reload configuration from file


If "RequireAdminForRcon" is set to true, console/RCON commands will also require bottomlesswater.admin.

Configuration

The plugin creates oxide/config/BottomlessWater.json with the following fields (see docs/config.sample.json for a full example):

Field	Default	Description
TickSeconds	1.0	How often to process containers (minimum 0.25 s).
MaxAddPerTick	1000	Maximum water to add to each container per tick.
AffectLiquidContainers	true	Whether to top up containers at all.
EnableByDefault	true	If a player with permission has no recorded preference, enable them.
AutoGrantUseToDefaultGroup	true	Auto‑grant bottomlesswater.use to the default group.
RequireAdminForRcon	false	If true, console/RCON commands require bottomlesswater.admin.
WhiteListShortPrefabNames	[]	Explicit list of prefab short names to include. When non‑empty, only these names will be affected.
ExcludeShortPrefabNames	[]	List of prefab short names to exclude, used only if the whitelist is empty.
ChatCooldownSeconds	2.0	Minimum seconds between consecutive /bw toggles by a player.

If both WhiteListShortPrefabNames and ExcludeShortPrefabNames are empty, all LiquidContainer prefabs are included. See Facepunch’s entity list
 for prefab short names.

Logging

When players or admins enable or disable bottomless water, the plugin writes entries to both the server console and oxide/logs/BottomlessWater.txt. Each entry contains the actor, target, state and timestamp.

Compatibility

This plugin targets the latest Rust and uMod/Oxide builds (December 2025) and any deployable with a LiquidContainer component. New water containers can be supported by adding their short prefab names to the whitelist or leaving AffectLiquidContainers enabled.

License

MIT — see LICENSE for details.
