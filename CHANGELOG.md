# Changelog

All notable changes to BottomlessWater are documented in this file.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Dates are UTC.

## [3.4.3] - 2026-06-06

### Changed (uMod submission prep)
- `[Info]` author is now the uMod username (`gjdunga`) to satisfy uMod's approval
  rule that the author attribute be the submitting account's username. Full credit
  (Gabriel Dungan, DunganSoft Technologies) is retained in the source header and the
  README.
- `AutoGrantUseToDefaultGroup` now defaults to **`false`**. The plugin no longer grants
  `bottomlesswater.use` to Oxide's `default` group out of the box; admins opt in by
  setting it `true` or granting the permission to a group/user. Existing installs are
  unaffected — their persisted config value is preserved on update. Updated
  `docs/config.sample.json`, README config table, and INSTALL accordingly.

## [3.4.2] - 2026-06-06

### Fixed (API compatibility — Oxide 2.0.7423 / current Rust)
- `ConsoleSystem.Arg.Args` changed from `string[]` to `Facepunch.StringView[]` in the
  current Rust assembly, which broke compilation of the admin console commands. The
  `bottomlesswater.toggle` and `bottomlesswater.status` handlers now read positional
  arguments via `arg.GetString(0)` / `arg.GetString(1)` instead of indexing
  `arg.Args[...]`. `GetString` returns a `string` and is stable across this change.
  Without the fix the plugin fails to compile — and therefore to load — on the
  affected build. Runtime behaviour is otherwise unchanged.

### Build / tooling
- Added an out-of-server compile-validation chain so API breaks like the one above are
  caught at build time instead of on a live server (this chain is what surfaced the
  break above):
  - `build/BottomlessWater.csproj` type-checks the plugin against the real Oxide, Rust
    and Unity assemblies (target `net48`).
  - `tools/fetch-references.sh` / `tools/fetch-references.ps1` stage the reference
    assemblies from a Rust dedicated server + Oxide install.
  - `.github/workflows/compile.yml` compiles on every push / PR, with the reference
    assemblies cached on a weekly key.
  - `Makefile` and `BUILD.md` document the local workflow.

## [3.4.1] - 2026-06-06

### Fixed (API compatibility — Oxide 2.0.7210+ / Rust CU270+)
- `RefreshLiquidContainers` now snapshots `BaseNetworkable.serverEntities` into a
  local list before iterating. The `ListHashSet` enumerator in the updated Facepunch
  assembly could throw `InvalidOperationException` when entities were concurrently
  registered during server startup.
- `FillLiquidItems` body wrapped in try/catch. A single bad entity state can no
  longer crash the entire tick loop; the offending container is removed from the
  tracked set and a one-line warning is logged.
- `SafeMaxStackable` helper wraps `item.MaxStackable()` and falls back to
  `item.info.stackable` if the call throws, guarding against the optional-parameter
  signature added to `MaxStackable` in the updated assembly.
- `liquid.inventory` null guard hardened: accesses the field via a null-conditional
  `?.` to handle async entity teardown in the updated server runtime.
- `OnEntityKill(LiquidContainer)` explicit null + `IsDestroyed` double-path guard
  for partially-torn-down entity references dispatched by Oxide's updated hook router.

### CI / workflow
- Added `.github/workflows/release.yml`. Fires on `v*.*.*` tag pushes; runs a
  two-stage version-sanity check (tag vs `manifest.json`, tag vs plugin `[Info]`
  attribute), then creates a draft GitHub Release using the matching
  `.github/release-notes/v*.md` file as the body and attaches the plugin `.cs` and
  all lang locale files as release assets. Falls back to a CHANGELOG excerpt if no
  notes file is present.
- Added `.github/release-notes/v3.4.1.md`.

## [3.4.0] - 2026-05-17

### Added
- `TickBucketCount` config field (default `1`, clamped `>= 1`). Slices the
  tracked-container workload across N round-robin sub-ticks. Each container is
  visited once every `TickSeconds * TickBucketCount` seconds, dropping per-tick
  cost by roughly the same factor on servers with hundreds of containers. The
  default value preserves pre-3.4.0 behaviour exactly. See README for the
  `MaxAddPerTick` scaling table.

### Changed (performance)
- Containers are now classified at spawn time. `OnEntitySpawned(LiquidContainer)`
  and `RefreshLiquidContainers` apply the whitelist/exclude lists once and only
  add eligible containers to `_liquidContainers`. The per-tick loop no longer
  re-runs `ShouldProcessPrefab` — one less hashset lookup per container per tick.
  `CmdReload` re-classifies the tracked set so config changes still take effect
  without restarting the server.
- Added `SweepRateLimitWindows`, called inline from `DoTick` at most once per 60
  seconds. Prunes `_rateLimitWindows` entries whose sliding 60-second window is
  empty or fully expired. Sleeping players who never explicitly disconnect no
  longer retain rate-limit entries for the server's full uptime.

### Deliberately not implemented (documented hedge)
Two performance options from the 3.3.2 review were intentionally NOT taken in
this release. Both are recorded in the source as design notes so future
contributors don't re-attempt them without weighing the same trade-offs.

- **Reactive permission cache** (replacing the per-tick `bottomlesswater.use`
  check with a `HashSet<ulong>` kept in sync via permission/group hooks). The
  reactive design would require subscribing to `OnUserPermissionGranted`,
  `OnUserPermissionRevoked`, `OnGroupPermissionGranted`,
  `OnGroupPermissionRevoked`, `OnUserGroupAdded`, `OnUserGroupRemoved`, AND
  re-resolving on plugin reload. A missed hook leaves a player with infinite
  water after their permission was revoked — exactly the security regression
  that 3.2.0's per-tick check was added to prevent. The lazy per-tick check is
  cheap enough; we keep it.
- **"Skip already-full containers"** (maintaining a `_hasRoom` subset of
  containers known to have headroom, re-adding from `OnItemUseConsume` /
  `CanMoveItem` when water drains). A missed drain event silently leaves a
  container un-filled, which is the exact user-visible bug class this plugin
  exists to prevent. Not worth the maintenance burden vs. the modest win from
  skipping full-container iterations.

### Documentation
- README rewritten with a clean structure, accurate config table including the
  new `TickBucketCount` field, and a tuning table for round-robin scheduling.
- INSTALL.md rewritten with prerequisites, verification, update path, uninstall,
  and a troubleshooting table.
- CONTRIBUTING.md rewritten with branching, PR checklist, code-style rules,
  explicit "things that get bounced" list (including the two hedged options
  above), localisation steps, and a release checklist.
- `docs/config.sample.json` is now an actual sample of the runtime
  `oxide/config/BottomlessWater.json` (it was previously a copy of the plugin
  manifest, which is what `manifest.json` is for).

## [3.3.2] - 2026-05-17

### Security review
- Full re-review against Facepunch Rust Community Update 269 and Oxide 2.0.7195.
  No new vulnerabilities identified. Existing hardening (per-tick PermUse cache,
  water-only fill filter, argument length cap, sliding rate-limit, toggle
  cooldown, debounced disk writes, ephemeral-state cleanup on disconnect)
  continues to hold.
- Confirmed no untrusted input reaches file paths, deserialisation sinks, or
  string-format vectors. Config and data files are admin-trusted.
- Confirmed admin gate on console commands: server console is trusted; in-game
  callers require IsAdmin OR bottomlesswater.admin. No bypass path.

### Changed
- Author updated to "Gabriel Dungan of DunganSoft Technologies." across
  the plugin [Info] attribute, manifest.json, .umod.yaml and the sample config.
- OnEntityKill now declares LiquidContainer directly so Oxide's hook router
  filters non-matching entities upstream. Eliminates a per-kill cast and a
  HashSet.Remove lookup for every non-liquid entity destruction.
- FillLiquidItems now calls SendNetworkUpdate (debounced) instead of
  SendNetworkUpdateImmediate. The immediate variant forced a full network
  flush per filled container per tick under load; the debounced call is the
  Facepunch-recommended path for non-urgent state replication.
- Compatibility note bumped to Oxide 2.0.7195 / Rust CU269 (verified).

### Documentation
- README: removed the "RequireAdminForRcon" config row. That field has never
  existed in PluginConfig; admin authentication is always enforced for
  non-console callers. Added the previously undocumented config fields
  (RateLimitMaxPerMinute, FillEmptyContainers, ClearDataOnWipe,
  SaveDebounceSeconds) to the configuration table.
- README and plugin header banner refreshed for v3.3.2.

## [3.3.1] - 2026-03-30

### Compatibility
- Verified compatible with Oxide 2.0.7182 (Rust Community Update 268). No hook
  signature changes affecting this plugin were introduced between Oxide 2.0.7022
  and 2.0.7182. OnEntitySpawned(LiquidContainer), OnEntityKill, OnPlayerDisconnected,
  OnServerSave, OnNewSave, Init, Unload, and the LiquidContainer/ItemManager/
  item.MaxStackable() APIs are all unchanged.
- Compatibility note added to plugin file header and manifest.

### Fixed (structure)
- Lang files were at wrong paths (oxide/lang/en.json, oxide/lang/es.json,
  oxide/lang/ru.json, oxide/lang/la.json). Oxide's lang system requires per-locale
  subdirectories: oxide/lang/{locale}/PluginName.json. Files at the flat path are
  silently ignored at runtime; es/ru/la translations were never loaded.
  Corrected to oxide/lang/en/BottomlessWater.json, oxide/lang/es/BottomlessWater.json,
  oxide/lang/ru/BottomlessWater.json, oxide/lang/la/BottomlessWater.json.
  Old flat files removed.
- Lang file format corrected: files had a wrapping "BottomlessWater": {} object.
  Oxide lang files must be flat key-value maps. Removed the wrapper from all
  four locale files.

### Documentation
- manifest.json: added oxide_minimum (2.0.7022) and oxide_verified (2.0.7182)
  to compatibility block.
- README.md: version added to H1 title, CU268 compatibility note added.


All notable changes to BottomlessWater are documented in this file.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Dates are UTC.

## [3.3.0] - 2026-02-21

### Changed
- Version bumped to 3.3.0 across all files (manifest.json, .umod.yaml, CHANGELOG, docs/config.sample.json).
- Replaced raw `bool` storage in `_playerStates` dictionary with a dedicated `PlayerState` private class.
  This eliminates any dependency on `System.ValueTuple` (which the uMod build server may reject) and
  provides a stable serialisation boundary for future per-player fields.
- `IsRateLimited` now accepts the already-resolved `BasePlayer` directly instead of performing a
  redundant `BasePlayer.FindByID` lookup internally.
- `SetPlayerState` now mutates an existing `PlayerState` instance in-place when possible to avoid
  unnecessary heap allocations.
- `LICENSE` renamed to `License.md` for umod.org submission compliance.

### Added
- `oxide/lang/la.json` Latin translation file.
- `PlayerState` private class with XML documentation explaining the value-tuple avoidance rationale.

### Security
- `FillLiquidItems` now filters by `_waterDefinition` item type. Previously the method filled ANY
  liquid item in the container inventory, which could inadvertently top up salt water, crude oil, or
  other liquids with economic value, constituting an exploitable resource duplication vector.

### Fixed
- `IsRateLimited` was calling `BasePlayer.FindByID` despite the player already being available at
  the call site in `CmdBottomlessWater`. Eliminated the redundant lookup.

## [3.2.0] - 2026-02-21

### Changed
- Version bumped to 3.2.0 across all support files.

### Security / Fixed
- `SaveDataImmediate` guard corrected: was `!_dirty && _storedData != null`; now `!_dirty` only.
- Tick loop verifies `bottomlesswater.use` per owner each tick via cached `_tickPermittedOwners`;
  permission revocation takes effect immediately.
- `FillEmptyContainers` guards `stackable > 0` before `ItemManager.Create` to prevent zero-amount
  broken items.
- Chat command argument capped at 32 chars (`MaxArgLength`) before string processing.
- `HasAdminAccess` passes `string.Empty` instead of `null` to `lang.GetMessage` for explicit
  language fallback.

### Documentation
- Full XML doc comments added to every class, field, and method.

## [2.3.1] - 2025-12-06

### Added
- Whitelist (`WhiteListShortPrefabNames`) and exclude (`ExcludeShortPrefabNames`) lists.
- Chat toggle cooldown (`ChatCooldownSeconds`).
- RCON/console lockdown support via `RequireAdminForRcon`.
- Logging of enable/disable events to `oxide/logs/BottomlessWater.txt`.
- Save on `OnServerSave` and plugin unload.

### Changed
- Performance improvement: liquid containers cached and processed directly.
- Updated `[Info]` metadata to author "Gabriel" and version 2.3.1.

### Fixed
- Potential NREs by checking entity and owner validity when processing containers.
- Rate limiting not applying to repeated `/bw` commands.

## [2.2.0] - 2025-10-06

### Added
- Logging for enable/disable events.
- `bottomlesswater.status` console command.
- Improved configuration sanitization.

### Security
- Safer player lookup and better error messages.
- Debounced network updates; periodic save on world save.

## [2.1.0] - 2025-10-06

### Added
- Exclude list, chat cooldown, optional RCON lockdown.
- README and docs sample config.

### Security
- Hardened command authentication and player resolution.

## [2.0.0] - 2025-10-06

### Added
- Permissions (`bottomlesswater.use`, `bottomlesswater.admin`).
- `/bw` chat toggle; console toggle, status, reload commands.
- Config-driven behaviour and persistence.

## [1.0.0] - 2025-11-09

### Added
- Initial uMod submission: manifest files, localization, INSTALL.md, docs sample config.
