# Installation — Bottomless Water

**BottomlessWater** makes supported water containers effectively endless by topping up any entity with a `LiquidContainer` component (e.g., water catchers, barrels).

## Requirements
- Rust server with Oxide/uMod (latest)
- No external dependencies

## Install
1. Copy `BottomlessWater.cs` to `oxide/plugins/`.
2. Start/reload the server (config and data files will be created):
   - `oxide/config/BottomlessWater.json`
   - `oxide/data/BottomlessWaterData.json`

## Permissions
- `bottomlesswater.use` — allow players to use `/bw` to enable/disable for their own placed entities.
- `bottomlesswater.admin` — allow admin console/RCON control and reload.

Grant usage to default group (optional):
oxide.grant group default bottomlesswater.use

shell
Copy code

## Player Commands
/bw on
/bw off
/bw toggle
/bw status

shell
Copy code

## Admin / Console / RCON
bottomlesswater.toggle <steamID|name> <on|off|toggle>
bottomlesswater.status [steamID|name]
bottomlesswater.reload

arduino
Copy code

## Config
The config lives at `oxide/config/BottomlessWater.json`. See `docs/config.sample.json` for the full schema.
After editing:
bottomlesswater.reload

Copy code
