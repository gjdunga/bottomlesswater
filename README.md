# BottomlessWater (Oxide/uMod, Rust)

**BottomlessWater** lets players place water containers that **stay full**: water catchers, barrels, and anything with a `LiquidContainer`. It’s permission-based, per-player toggled, admin-controllable, and safe to run long-term.

- ✅ Per-player toggle with persistence
- ✅ Permissions: `bottomlesswater.use`, `bottomlesswater.admin`
- ✅ Chat + Console/RCON commands
- ✅ Separate JSON config with live reload
- ✅ Logging for enable/disable (player & admin actions)
- ✅ Heuristic support for future water types via `LiquidContainer`

## Installation

1. Copy `src/BottomlessWater.cs` to your server at:
   ```
   oxide/plugins/BottomlessWater.cs
   ```
2. Start/reload your server; the plugin will auto-generate:
   ```
   oxide/config/BottomlessWater.json
   oxide/data/BottomlessWaterData.json
   oxide/logs/BottomlessWater*.txt
   ```

To recreate the default config, delete `oxide/config/BottomlessWater.json` and run:
```
bottomlesswater.reload
```

## Permissions

- `bottomlesswater.use` — allows a player to use `/bw` and get bottomless water on their placed items.
- `bottomlesswater.admin` — allows running admin console/RCON commands.

By default, the plugin **grants `bottomlesswater.use` to the `default` group**. Disable this in config if you want it opt-in:
```json
"AutoGrantUseToDefaultGroup": false
```

## Commands

### Chat (players)
```
/bw on
/bw off
/bw toggle
/bw status
```

### Console / RCON (admins)
```
bottomlesswater.toggle <steamID|name> <on|off|toggle>
bottomlesswater.status [steamID|name]
bottomlesswater.reload
```

Optional: set `"RequireAdminForRcon": true` to **disallow RCON** for these commands and accept only in-game admins.

## Config

Config path: `oxide/config/BottomlessWater.json`

See `docs/config.sample.json` for the full schema. Key fields:

- `TickSeconds` — how often to top up each entity (min 0.25s)
- `MaxAddPerTick` — per-tick fill ceiling (1..100000)
- `AffectLiquidContainers` — include barrels/others that expose `LiquidContainer`
- `EnableByDefault` — if a player with permission has no recorded preference, treat as enabled
- `WhitelistShortPrefabNames` — explicit list of short prefab names to include
- `ExcludeShortPrefabNames` — explicit list to *exclude* even if detected heuristically

After editing config:
```
bottomlesswater.reload
```

## Logging

Enable/disable events are written to console and to:
```
oxide/logs/BottomlessWater.txt
```
Examples:
```
[PLAYER] Alice ENABLED BottomlessWater for 76561198012345678 at 2025-10-06 21:32:11
[ADMIN] CONSOLE DISABLED BottomlessWater for 76561198087654321 at 2025-10-06 21:33:12
```

## Compatibility

- Rust (latest)
- Oxide/uMod (current)
- Any deployable with a `LiquidContainer` component is likely supported. Add new prefabs to the whitelist or leave `AffectLiquidContainers=true` to catch them heuristically.

## License

MIT — see [LICENSE](LICENSE).

