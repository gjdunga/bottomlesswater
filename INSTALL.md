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
