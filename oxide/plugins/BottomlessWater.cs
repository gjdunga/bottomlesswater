using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "gjdunga", "1.0.0")]
    [Description("Allows players to enable infinite water behavior on LiquidContainer entities they own. Admins can control it globally or per-player. Fully uMod compliant.")]
    public class BottomlessWater : CovalencePlugin
    {
        // ----------------------------
        // CONFIG SECTION
        // ----------------------------

        private PluginConfig _config;

        private class PluginConfig
        {
            public float TickSeconds = 1.0f;
            public int MaxAddPerTick = 1000;
            public bool AffectLiquidContainers = true;
            public bool EnableByDefault = true;
            public bool AutoGrantUseToDefaultGroup = true;
            public bool RequireAdminForRcon = false;

            public List<string> WhitelistShortPrefabNames = new List<string>();
            public List<string> ExcludeShortPrefabNames = new List<string>();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        private void SaveConfig() => Config.WriteObject(_config, true);

        private void LoadConfigValues()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config null");
            }
            catch
            {
                PrintWarning("Invalid config, regenerating defaults.");
                LoadDefaultConfig();
            }

            // sanity limits
            if (_config.TickSeconds < 0.25f)
                _config.TickSeconds = 0.25f;

            if (_config.MaxAddPerTick < 1)
                _config.MaxAddPerTick = 1;
        }

        // ----------------------------
        // LOCALIZATION
        // ----------------------------

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this.",
                ["Usage"] = "Usage: /bw <on|off|toggle|status>",
                ["Enabled"] = "Bottomless water is now <color=#77ff77>ENABLED</color> for you.",
                ["Disabled"] = "Bottomless water is now <color=#ff7777>DISABLED</color> for you.",
                ["StatusOn"] = "Your bottomless water setting: <color=#77ff77>ENABLED</color>.",
                ["StatusOff"] = "Your bottomless water setting: <color=#ff7777>DISABLED</color>.",
                ["AdminOnly"] = "You must be an admin to use this command.",
                ["PlayerNotFound"] = "Player not found.",
                ["Reloaded"] = "BottomlessWater configuration reloaded.",
                ["ConsoleToggled"] = "[ADMIN] {ACTOR} set BottomlessWater {STATE} for {TARGET} ({STEAMID}).",
                ["RateLimited"] = "You're using that too often; try again in a moment."
            }, this);
        }

        private string Msg(string key, string playerId = null, params object[] args)
        {
            var t = lang.GetMessage(key, this, playerId);
            return args.Length > 0 ? string.Format(t, args) : t;
        }

        // ----------------------------
        // DATA
        // ----------------------------

        private Dictionary<string, bool> _playerStates;
        private const string DataName = "BottomlessWaterData";

        private void LoadData()
        {
            _playerStates = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, bool>>(DataName);
            if (_playerStates == null)
                _playerStates = new Dictionary<string, bool>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataName, _playerStates);
        }

        // ----------------------------
        // PERMISSIONS
        // ----------------------------

        private const string PermUse = "bottomlesswater.use";
        private const string PermAdmin = "bottomlesswater.admin";

        private void RegisterPermissions()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);

            if (_config.AutoGrantUseToDefaultGroup)
                permission.GrantGroupPermission("default", PermUse, this);
        }

        // ----------------------------
        // INIT
        // ----------------------------

        private float _tickInterval;
        private float _nextTick;
        private readonly Dictionary<string, List<DateTime>> _rateLimit = new();

        private void Init()
        {
            LoadConfigValues();
            RegisterPermissions();
            LoadData();

            _tickInterval = _config.TickSeconds;
            _nextTick = Time.realtimeSinceStartup + _tickInterval;

            AddCovalenceCommand("bw", "CmdBW");
            AddCovalenceCommand("bottomlesswater.toggle", "CmdToggleConsole");
            AddCovalenceCommand("bottomlesswater.status", "CmdStatusConsole");
            AddCovalenceCommand("bottomlesswater.reload", "CmdReloadConfig");
        }

        private void Unload() => SaveData();

        // ----------------------------
        // MAIN TICK LOOP
        // ----------------------------

        private void OnServerInitialized()
        {
            timer.Every(_config.TickSeconds, DoTick);
        }

        private void DoTick()
        {
            if (!_config.AffectLiquidContainers) return;

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity == null || entity.IsDestroyed) continue;

                var liquid = entity.GetComponent<LiquidContainer>();
                if (liquid == null) continue;

                string ownerId = entity.OwnerID.ToString();
                if (!_playerStates.TryGetValue(ownerId, out bool enabled))
                {
                    enabled = _config.EnableByDefault;
                    _playerStates[ownerId] = enabled;
                }

                if (!enabled) continue;

                if (liquid.amount < liquid.maxStackSize)
                {
                    var add = Mathf.Min(_config.MaxAddPerTick, liquid.maxStackSize - liquid.amount);
                    liquid.amount += add;
                    liquid.SendNetworkUpdate();
                }
            }
        }

        // ----------------------------
        // PLAYER COMMAND
        // ----------------------------

        private bool RateLimited(IPlayer player)
        {
            if (!_rateLimit.TryGetValue(player.Id, out var list))
                list = _rateLimit[player.Id] = new List<DateTime>();

            var now = DateTime.UtcNow;
            list.RemoveAll(t => (now - t).TotalSeconds > 60);

            if (list.Count >= 5)
            {
                player.Reply(Msg("RateLimited", player.Id));
                return true;
            }

            list.Add(now);
            return false;
        }

        private void CmdBW(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(PermUse))
            {
                player.Reply(Msg("NoPermission", player.Id));
                return;
            }

            if (RateLimited(player)) return;

            if (args.Length == 0)
            {
                player.Reply(Msg("Usage", player.Id));
                return;
            }

            string key = player.Id;

            if (!_playerStates.TryGetValue(key, out bool enabled))
                enabled = _config.EnableByDefault;

            string sub = args[0].ToLower();

            switch (sub)
            {
                case "on":
                    _playerStates[key] = true;
                    SaveData();
                    player.Reply(Msg("Enabled", player.Id));
                    break;

                case "off":
                    _playerStates[key] = false;
                    SaveData();
                    player.Reply(Msg("Disabled", player.Id));
                    break;

                case "toggle":
                    enabled = !enabled;
                    _playerStates[key] = enabled;
                    SaveData();
                    player.Reply(enabled ? Msg("Enabled", player.Id) : Msg("Disabled", player.Id));
                    break;

                case "status":
                    player.Reply(enabled ? Msg("StatusOn", player.Id) : Msg("StatusOff", player.Id));
                    break;

                default:
                    player.Reply(Msg("Usage", player.Id));
                    break;
            }
        }

        // ----------------------------
        // CONSOLE COMMANDS
        // ----------------------------

        private void CmdToggleConsole(IPlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player)) return;

            if (args.Length < 2)
            {
                player?.Reply("Usage: bottomlesswater.toggle <player> <on|off|toggle>");
                return;
            }

            var target = FindPlayer(args[0]);
            if (target == null)
            {
                player?.Reply(Msg("PlayerNotFound"));
                return;
            }

            string action = args[1].ToLower();
            string tid = target.Id;

            if (!_playerStates.TryGetValue(tid, out bool enabled))
                enabled = _config.EnableByDefault;

            switch (action)
            {
                case "on":
                    enabled = true;
                    break;
                case "off":
                    enabled = false;
                    break;
                case "toggle":
                    enabled = !enabled;
                    break;
                default:
                    player?.Reply("Usage: bottomlesswater.toggle <player> <on|off|toggle>");
                    return;
            }

            _playerStates[tid] = enabled;
            SaveData();

            var line = Msg("ConsoleToggled", null,
                "{ACTOR}", player?.Name ?? "CONSOLE",
                "{STATE}", enabled ? "ON" : "OFF",
                "{TARGET}", target.Name,
                "{STEAMID}", tid
            );

            Puts(line);
            player?.Reply(line);
        }

        private void CmdStatusConsole(IPlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player)) return;

            if (args.Length == 0)
            {
                foreach (var kvp in _playerStates)
                {
                    var p = players.FindPlayer(kvp.Key);
                    var n = p?.Name ?? kvp.Key;
                    player?.Reply($"{n} ({kvp.Key}): {(kvp.Value ? "ENABLED" : "DISABLED")}");
                }
                return;
            }

            var target = FindPlayer(args[0]);
            if (target == null)
            {
                player?.Reply(Msg("PlayerNotFound"));
                return;
            }

            if (!_playerStates.TryGetValue(target.Id, out bool enabled))
                enabled = _config.EnableByDefault;

            player?.Reply($"{target.Name} ({target.Id}): {(enabled ? "ENABLED" : "DISABLED")}");
        }

        private void CmdReloadConfig(IPlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player)) return;

            LoadConfigValues();
            player?.Reply(Msg("Reloaded", null));
        }

        // ----------------------------
        // UTILITIES
        // ----------------------------

        private bool HasAdminAccess(IPlayer player)
        {
            if (player == null) return true; // console
            if (player.HasPermission(PermAdmin)) return true;
            player.Reply(Msg("AdminOnly", player.Id));
            return false;
        }

        private IPlayer FindPlayer(string ident)
        {
            var p = players.FindPlayer(ident);
            return p;
        }
    }
}
