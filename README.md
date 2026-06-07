Bottomless Water keeps every owned liquid container topped up with fresh water — automatically, efficiently, and only for the players you allow.

Drop a water purifier, water catcher, or any liquid container and Bottomless Water keeps it full so your players never run dry. It's built for busy servers: a round-robin scheduler spreads the work across ticks, permissions are re-checked every tick, and disk writes are debounced.

Features

- Per-player opt-in — players toggle their own infinite water with /bw on|off|toggle|status; the choice persists across restarts and wipes.
- Water only — only items whose ItemDefinition is water are refilled. Salt water, crude oil, and other liquids that share the same container type are never touched.
- Permission-gated, re-checked every tick — revoking bottomlesswater.use takes effect on the very next tick; no waiting for the player to log off.
- Prefab whitelist / exclude lists — limit the effect to (or away from) specific container prefabs. Filtering happens at spawn time, so the per-tick loop stays cheap.
- Round-robin tick scheduler — TickBucketCount slices hundreds of containers across multiple ticks to keep frame time low on large servers.
- Rate limit   cooldown — /bw actions are bounded by a per-player cooldown and a sliding 60-second window.
- Debounced saves & structured audit log — player state is flushed lazily and on server save / unload; every toggle is logged (actor, target, state, UTC timestamp).
- Localized — ships in 8 languages.

Permissions

- bottomlesswater.use — receive infinite water and use the /bw command.
- bottomlesswater.admin — use the bottomlesswater.* console / RCON commands (the server console and RCON bypass this check).

Example: oxide.grant group default bottomlesswater.use

Chat commands

- /bw on — enable infinite water on your owned containers
- /bw off — disable it
- /bw toggle — flip your current state
- /bw status — show your current state

Console / RCON commands (admin)

- bottomlesswater.toggle <steamid64> <on|off|toggle> — set another player's state
- bottomlesswater.status [steamid64] — show one player, or every tracked player when omitted
- bottomlesswater.reload — reload the config and restart the tick timer

Configuration

Default oxide/config/BottomlessWater.json:

{
  "TickSeconds": 1.0,
  "MaxAddPerTick": 1000,
  "AffectLiquidContainers": true,
  "EnableByDefault": true,
  "AutoGrantUseToDefaultGroup": false,
  "WhiteListShortPrefabNames": [],
  "ExcludeShortPrefabNames": [],
  "ChatCooldownSeconds": 2.0,
  "RateLimitMaxPerMinute": 5,
  "FillEmptyContainers": false,
  "ClearDataOnWipe": false,
  "SaveDebounceSeconds": 2.0,
  "TickBucketCount": 1
}

- TickSeconds — how often the fill loop runs (clamped to ≥ 0.25).
- MaxAddPerTick — max water units added per item per tick.
- AffectLiquidContainers — master on/off switch.
- EnableByDefault — the state a permitted owner gets the first time they're seen.
- AutoGrantUseToDefaultGroup — off by default; set true to auto-grant bottomlesswater.use to the default group on load.
- WhiteListShortPrefabNames / ExcludeShortPrefabNames — prefab filters (whitelist wins when non-empty).
- ChatCooldownSeconds / RateLimitMaxPerMinute — anti-spam for /bw.
- FillEmptyContainers — also create a fresh water stack in completely empty owned containers.
- ClearDataOnWipe — wipe stored player toggles on a new save.
- SaveDebounceSeconds — delay before flushing dirty state to disk.
- TickBucketCount — round-robin slicing for large servers (raise MaxAddPerTick by the same factor to keep the effective fill rate).

After editing, run bottomlesswater.reload.

Localization

English, Spanish, Russian, Latin, Simplified Chinese, German, French, and Portuguese are bundled. Edit any message under oxide/lang/<locale>/BottomlessWater.json.

Notes

Only owned containers are affected (OwnerID != 0). Requires Oxide/uMod 2.0.7022  (verified through 2.0.7423).