Changelog

All notable changes to BottomlessWater are documented in this file. The format is based on Keep a Changelog
. Dates are in UTC.

[2.3.1] - 2025‑12‑06
Added

Introduced whitelist (WhiteListShortPrefabNames) and exclude (ExcludeShortPrefabNames) lists to explicitly include or exclude certain prefab short names. When the whitelist is non‑empty, only those prefabs are affected.

Added a chat toggle cooldown (ChatCooldownSeconds) to limit how frequently players can change their bottomless water state via /bw.

Added RCON/console lockdown support via RequireAdminForRcon. When enabled, console commands require the bottomlesswater.admin permission.

Added logging of enable/disable events to oxide/logs/BottomlessWater.txt, including actor, target, state and timestamp.

Added saving on OnServerSave and plugin unload to reduce state loss.

Changed

Performance improvement: liquid containers are now cached and processed directly, avoiding iteration over every server entity each tick.

Updated [Info] metadata to author “Gabriel” and version 2.3.1.

Updated documentation and sample config to reflect new fields.

Fixed

Fixed potential NREs by checking entity and owner validity when processing containers.

Fixed rate limiting not applying to repeated /bw commands.

[2.2.0] - 2025‑10‑06
Added

Logging for enable/disable events.

bottomlesswater.status console command.

Improved configuration sanitization.

Security

Safer player lookup and better error messages.

Debounced network updates; periodic save on world save.

[2.1.0] - 2025‑10‑06
Added

Exclude list, chat cooldown, optional RCON lockdown.

README and docs sample config.

Security

Hardened command authentication and player resolution.

[2.0.0] - 2025‑10‑06
Added

Permissions (bottomlesswater.use, bottomlesswater.admin).

/bw chat toggle; console toggle, status, reload commands.

Config-driven behaviour and persistence.

[1.0.0] - 2025‑11‑09
Added

Initial uMod submission: added manifest files (manifest.json, .umod.yaml), localization (oxide/lang/en.json), INSTALL.md and docs sample config. Clarified README permissions/commands and linked install docs.
