# Changelog

All notable changes to **BottomlessWater** are documented here.

## [2.2.0] - 2025-10-06
### Added
- Logging for enable/disable events (player and admin actions).
- Console command `bottomlesswater.status` for quick auditing.

### Improved
- Strict config sanitization (tick clamp, max per-tick clamp).
- Safer player lookup; better error messages.
- Debounced network updates; periodic save on world save.

## [2.1.0] - 2025-10-06
### Added
- Exclude list, chat cooldown, optional RCON lockdown.
- README, docs sample config.

### Security
- Hardened command auth, strict player resolution.

## [2.0.0] - 2025-10-06
### Added
- Permissions (`bottomlesswater.use`, `bottomlesswater.admin`).
- `/bw` chat toggle; console `toggle`, `reload`.
- Config-driven behavior; persistence.


## 1.0.0 — uMod submission prep (2025-11-09)
- Added uMod manifest files (`manifest.json`, `.umod.yaml`).
- Added localization (`oxide/lang/en.json`) per uMod guidelines.
- Added `INSTALL.md` and `docs/config.sample.json`.
- Clarified README permissions/commands and linked install docs.
