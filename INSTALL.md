Installation Guide for BottomlessWater

This document describes how to install and configure BottomlessWater, an Oxide/uMod plugin for Rust that keeps liquid containers full on a per‑player basis.

Prerequisites

A Rust server with uMod/Oxide installed and running. See the uMod installation guide
 if you need help setting up uMod.

You should be familiar with connecting to your server’s file system (FTP/SFTP) and issuing console/RCON commands.

Installing the Plugin

Download or build the BottomlessWater.cs file.

Upload the file to your Rust server under the oxide/plugins/ directory. The full path should be:

oxide/plugins/BottomlessWater.cs


Reload or restart your server. You can reload only this plugin by running:

oxide.reload BottomlessWater


Upon first load, the plugin will generate:

oxide/config/BottomlessWater.json — Configuration file. You can edit this to customize behavior.

oxide/data/BottomlessWaterData.json — Persistent per‑player toggle states.

oxide/logs/BottomlessWater.txt — Log file for enable/disable actions.

Updating the Plugin

To update to a newer version:

Replace oxide/plugins/BottomlessWater.cs with the latest version.

Run oxide.reload BottomlessWater to compile and load the new code.

Review CHANGELOG.md for any new configuration fields or features. Update oxide/config/BottomlessWater.json accordingly — you can delete the existing file to regenerate the default config.

Editing the Configuration

Open oxide/config/BottomlessWater.json in a text editor. The available fields are documented in the README and sample config (docs/config.sample.json). After editing, run:

bottomlesswater.reload


to apply the changes without restarting the server.

Permissions

Grant the bottomlesswater.use permission to players who should have access to /bw commands. Grant bottomlesswater.admin to administrators who need to manage other players or reload the configuration. By default, bottomlesswater.use is granted to the default group and bottomlesswater.admin is not granted to any group.

You can modify group permissions using standard uMod commands, for example:

oxide.grant group default bottomlesswater.use
oxide.grant user steamid64 bottomlesswater.admin


Refer to the uMod documentation for more details on managing permissions.
