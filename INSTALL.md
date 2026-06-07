# Installation Guide — BottomlessWater 3.4.4

This guide covers installing, updating, configuring, and removing BottomlessWater on a Rust server running Oxide / uMod.

---

## Prerequisites

- A Rust server with Oxide / uMod **2.0.7022 or newer** installed and running. (Verified through 2.0.7423.) If you don't have Oxide yet, follow the [official uMod install guide](https://umod.org/games/rust).
- File access to the server (FTP, SFTP, or shell).
- The ability to issue server console / RCON commands (in-game F1 console as an admin, or RCON tool of your choice).

---

## 1. Install the plugin

1. Download `oxide/plugins/BottomlessWater.cs` from this repository. (The `.cs` file *is* the deliverable — Oxide compiles it on the server. To compile-check changes before deploying, see [`BUILD.md`](BUILD.md).)
2. Upload it to your server at exactly:

   ```
   <server-root>/oxide/plugins/BottomlessWater.cs
   ```

3. Reload (or let the server pick it up on next start):

   ```
   oxide.reload BottomlessWater
   ```

On first load, the plugin creates:

| Path | Created on first load? | Purpose |
| --- | --- | --- |
| `oxide/config/BottomlessWater.json` | Yes | Configuration; edit and `bottomlesswater.reload` to apply. |
| `oxide/data/BottomlessWaterData.json` | Yes (empty) | Persistent per-player toggle state. |
| `oxide/logs/BottomlessWater.txt` | On first toggle event | Append-only toggle audit log. |

The plugin also registers two permissions (`bottomlesswater.use`, `bottomlesswater.admin`). It does **not** grant `bottomlesswater.use` to any group by default — grant it explicitly (see step 5), or set `AutoGrantUseToDefaultGroup` to `true` if you want all authenticated players in the `default` group to inherit it automatically.

---

## 2. Verify the install

In server console:

```
oxide.plugins
```

You should see `Bottomless Water (3.4.4) by gjdunga` in the list.

As a player with `bottomlesswater.use` permission, run:

```
/bw status
```

If the plugin replies with your current state, you're done.

---

## 3. Configure (optional)

Open `oxide/config/BottomlessWater.json`. See [README.md](README.md#configuration) for the full field reference and [`docs/config.sample.json`](docs/config.sample.json) for a copy-paste sample.

After editing, apply without restarting the server:

```
bottomlesswater.reload
```

The reload re-reads the config, re-classifies tracked containers against the new whitelist/exclude lists, and restarts the fill timer.

---

## 4. Update to a newer version

1. Replace `oxide/plugins/BottomlessWater.cs` with the new file.
2. Run `oxide.reload BottomlessWater`.
3. Read the [CHANGELOG](CHANGELOG.md) for any new configuration fields. New fields are written on next save with their defaults; existing fields are preserved.

To reset the configuration to defaults, delete `oxide/config/BottomlessWater.json` and reload.

---

## 5. Manage permissions

```
oxide.grant group default bottomlesswater.use
oxide.grant user <steamid64> bottomlesswater.admin
oxide.revoke user <steamid64> bottomlesswater.use
```

`bottomlesswater.use` controls who receives infinite water and who can run `/bw`. `bottomlesswater.admin` controls who can run the `bottomlesswater.*` console commands from in-game (the server console / RCON bypass this check).

---

## 6. Uninstall

```
oxide.unload BottomlessWater
```

Then delete:

- `oxide/plugins/BottomlessWater.cs`
- (Optional) `oxide/config/BottomlessWater.json`
- (Optional) `oxide/data/BottomlessWaterData.json`
- (Optional) `oxide/logs/BottomlessWater.txt`
- (Optional) `oxide/lang/{en,es,ru,la}/BottomlessWater.json`

Permissions registered by the plugin are released automatically when the plugin is unloaded.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `/bw` replies "You don't have permission to use this." | Player lacks `bottomlesswater.use`. | `oxide.grant user <steamid64> bottomlesswater.use` or grant to a group. |
| Console commands reply "You must be an admin to use this command." | In-game caller lacks `bottomlesswater.admin` and isn't a server admin. | Grant the permission, run from server console / RCON, or run as a server admin. |
| Water containers aren't filling. | Owner has no permission, has run `/bw off`, container is unowned (`OwnerID == 0`), or `AffectLiquidContainers` is `false`. | Check `bottomlesswater.status <steamid64>` and the config. |
| Non-water liquids (salt water, oil) are not being topped up. | Working as intended — only items matching the `water` `ItemDefinition` are filled. | Not a bug. |
| Plugin log says `Could not resolve 'water' ItemDefinition`. | Rust update changed the item shortname. | Open an issue with the Rust version. `FillEmptyContainers` will be disabled until resolved. |

For anything else, attach the contents of `oxide/logs/BottomlessWater.txt` and the line from `oxide.log` to a GitHub issue.
