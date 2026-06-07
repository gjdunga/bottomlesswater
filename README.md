# BottomlessWater

[![Compile](https://github.com/gjdunga/bottomlesswater/actions/workflows/compile.yml/badge.svg)](https://github.com/gjdunga/bottomlesswater/actions/workflows/compile.yml)

**Version:** 3.4.2
**Compatibility:** Oxide / uMod 2.0.7022+ — verified through Oxide 2.0.7423.
**Author:** Gabriel Dungan of DunganSoft Technologies.
**License:** MIT

An Oxide/uMod plugin for Rust (Facepunch) that keeps owned liquid containers topped up with fresh water. Per-player toggle, admin controls, persistent state, prefab whitelist/exclude lists, rate-limited chat commands, and a round-robin tick scheduler for busy servers.

---

## Features

- **Per-player toggle.** Players opt in or out with `/bw on|off|toggle|status`; state is persisted across restarts.
- **Water-only fill.** Only items whose `ItemDefinition` matches `water` are topped up — salt water, crude oil, and other liquids that share `LiquidContainer` storage are never modified.
- **Permission re-checked every tick.** Revoking `bottomlesswater.use` takes effect on the next tick — no need to wait for the player to `/bw off`.
- **Prefab whitelist / exclude lists.** Containers are filtered at spawn time, so the per-tick loop never revisits the prefab check.
- **Round-robin tick scheduler.** Optional `TickBucketCount` slices the workload across N ticks for servers with hundreds of tracked containers.
- **Rate limit + cooldown.** `/bw` mutating actions are bounded by a 60-second sliding window AND a per-player cooldown.
- **Debounced disk writes.** Player state is flushed lazily (default 2s after the last change) and on `OnServerSave` / plugin unload.
- **Structured logging.** Every enable/disable event is written to console and `oxide/logs/BottomlessWater.txt` with actor, target, state, and UTC timestamp.
- **Lazy rate-limit window cleanup.** Sleeping players who never disconnect no longer leak rate-limit entries.

---

## Installation

See [`INSTALL.md`](INSTALL.md) for the full install / update / permissions walkthrough.

Short version:

1. Drop `oxide/plugins/BottomlessWater.cs` into your server's `oxide/plugins/` directory.
2. Reload: `oxide.reload BottomlessWater`.
3. Edit `oxide/config/BottomlessWater.json` if you want to change defaults, then `bottomlesswater.reload`.

---

## Building from source

You don't need to build to deploy — the server's Oxide runtime compiles
`BottomlessWater.cs` on load. But you can type-check the plugin against the real
Oxide/Rust/Unity assemblies before shipping a change:

```bash
make references-managed   # one-time: fetch the Oxide/Rust reference DLLs
make build                # compile-check the plugin
```

CI runs the same check on every push and PR (see the Compile badge above). Full
prerequisites, Windows instructions, and how to reuse an existing server install
are in [`BUILD.md`](BUILD.md).

---

## Permissions

| Permission | Description |
| --- | --- |
| `bottomlesswater.use` | Required for `/bw` chat commands and to actually receive infinite water. Auto-granted to the `default` group on first load (configurable). |
| `bottomlesswater.admin` | Required for the `bottomlesswater.*` console commands when invoked by an in-game player. Server-console / RCON callers bypass this check. |

---

## Commands

### Chat (players)

```
/bw on        Enable bottomless water on your owned containers
/bw off       Disable bottomless water
/bw toggle    Flip the current state
/bw status    Show your current state
```

Mutating actions (`on`, `off`, `toggle`) are subject to `ChatCooldownSeconds` and `RateLimitMaxPerMinute`. `status` is unrestricted.

### Console / RCON (admins)

```
bottomlesswater.toggle <steamid64> <on|off|toggle>   Set another player's state
bottomlesswater.status [steamid64]                    Show one player, or every tracked player when omitted
bottomlesswater.reload                                 Re-read oxide/config/BottomlessWater.json
```

Console/RCON callers from the server console are always trusted. In-game callers must be a server admin OR hold `bottomlesswater.admin`.

---

## Configuration

The plugin creates `oxide/config/BottomlessWater.json` on first load. See [`docs/config.sample.json`](docs/config.sample.json) for a copy-paste-ready example.

| Field | Default | Notes |
| --- | --- | --- |
| `TickSeconds` | `1.0` | How often the fill loop runs. Clamped to `>= 0.25`. |
| `MaxAddPerTick` | `1000` | Maximum water units added per item per tick. Clamped to `>= 1`. |
| `AffectLiquidContainers` | `true` | Master switch. When false, the timer keeps running but does no work. |
| `EnableByDefault` | `true` | The toggle state assigned the first time a permitted owner is seen. |
| `AutoGrantUseToDefaultGroup` | `true` | Grants `bottomlesswater.use` to Oxide's `default` group on load. |
| `WhiteListShortPrefabNames` | `[]` | If non-empty, ONLY containers with these `ShortPrefabName`s are tracked. |
| `ExcludeShortPrefabNames` | `[]` | Containers with these `ShortPrefabName`s are dropped. Ignored when the whitelist is non-empty. |
| `ChatCooldownSeconds` | `2.0` | Minimum delay between mutating `/bw` actions per player. Clamped to `>= 0`. |
| `RateLimitMaxPerMinute` | `5` | Sliding 60-second window cap on mutating `/bw` actions per player. Clamped to `>= 1`. |
| `FillEmptyContainers` | `false` | If true, create a fresh water stack in completely empty owned containers (respects stack caps). |
| `ClearDataOnWipe` | `false` | If true, wipe stored player toggle state on `OnNewSave` (map wipe). |
| `SaveDebounceSeconds` | `2.0` | Delay before a dirty player-state dictionary is flushed to disk. Clamped to `>= 0.1`. |
| `TickBucketCount` | `1` | Round-robin slicing. Each container is visited once every `TickSeconds * TickBucketCount` seconds. Clamped to `>= 1`. See below. |

### Tuning `TickBucketCount` for large servers

For most servers, leave `TickBucketCount` at `1` — that preserves the pre-3.4.0 behaviour where every tracked container is processed every tick.

On a server with hundreds of liquid containers, raise it to 2, 4, or 8 to amortise the tick cost. The trade-off is that a given container is topped up only once every `TickSeconds * TickBucketCount` seconds. To preserve the same effective fill rate per container, raise `MaxAddPerTick` by the same factor:

| `TickBucketCount` | Effective per-container fill | Suggested `MaxAddPerTick` |
| --- | --- | --- |
| 1 (default) | every 1 s | 1000 |
| 2 | every 2 s | 2000 |
| 4 | every 4 s | 4000 |
| 8 | every 8 s | 8000 |

(All assuming `TickSeconds = 1.0`.) Items are still capped at their max stack size, so over-shooting `MaxAddPerTick` is safe.

---

## Logging

Toggle events are written to both the server console and `oxide/logs/BottomlessWater.txt`. Each entry includes:

- Source (`[PLAYER]` or `[ADMIN]`)
- Actor display name and SteamID
- New state (ENABLED / DISABLED)
- Target display name and SteamID (when an admin acts on another player)
- UTC timestamp in `yyyy-MM-dd HH:mm:ssZ` format

---

## Data files

| Path | Purpose |
| --- | --- |
| `oxide/config/BottomlessWater.json` | Configuration (see above). Editable; reload with `bottomlesswater.reload`. |
| `oxide/data/BottomlessWaterData.json` | Per-player toggle state, keyed by SteamID64. Hand-edit at your own risk. |
| `oxide/logs/BottomlessWater.txt` | Append-only toggle event log. |
| `oxide/lang/{en,es,ru,la}/BottomlessWater.json` | Localised message strings. Add additional locales by mirroring this layout. |

---

## Compatibility

Targets the latest Facepunch Rust + Oxide builds (Oxide 2.0.7423 as of release 3.4.2). All hooks used by the plugin (`OnEntitySpawned(LiquidContainer)`, `OnEntityKill(LiquidContainer)`, `OnPlayerDisconnected`, `OnServerSave`, `OnNewSave`, `Init`, `Unload`) and APIs (`LiquidContainer`, `ItemManager.FindItemDefinition`, `item.MaxStackable()`, `BasePlayer.FindAwakeOrSleeping(string)`) have stable signatures across the supported Oxide range.

If Facepunch ships a Rust update that changes any of these signatures, open an issue.

---

## License

MIT — see [`License.md`](License.md).
