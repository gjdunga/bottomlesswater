# Changelog

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
