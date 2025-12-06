using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "Gabriel", "2.3.1")]
    [Description("Allows players to enable infinite water behavior on LiquidContainer entities they own. Includes whitelist/exclude filtering, logging, chat cooldowns and improved performance.")]
    public class BottomlessWater : CovalencePlugin
    {
        private PluginConfig _config;
        private readonly Dictionary<string, bool> _playerStates = new Dictionary<string, bool>();
        private readonly Dictionary<string, float> _toggleCooldowns = new Dictionary<string, float>();
        private readonly Dictionary<string, List<DateTime>> _rateLimit = new Dictionary<string, List<DateTime>>();

        private const string DataFileName = "BottomlessWaterData";
        private const string LogFile = "BottomlessWater";
        private const string PermUse = "bottomlesswater.use";
        private const string PermAdmin = "bottomlesswater.admin";

        // Cache of all LiquidContainer components in the world
        private HashSet<LiquidContainer> _liquidContainers = new HashSet<LiquidContainer>();
        private float _tickInterval;

        /// <summary>
        /// Configuration structure. Includes various tweakable settings and lists.
        /// </summary>
        private class PluginConfig
        {
            public float TickSeconds = 1.0f;
            public int MaxAddPerTick = 1000;
            public bool AffectLiquidContainers = true;
            public bool EnableByDefault = true;
            public bool AutoGrantUseToDefaultGroup = true;
            public bool RequireAdminForRcon = false;
            public List<string> WhiteListShortPrefabNames = new List<string>();
            public List<string> ExcludeShortPrefabNames = new List<string>();
            public float ChatCooldownSeconds = 2.0f;
        }

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating default configuration...");
            _config = new PluginConfig();
            SaveConfig();
        }

        private void SaveConfig() => Config.WriteObject(_config, true);

        private void LoadConfigValues()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config deserialized as null.");
            }
            catch
            {
                PrintWarning("Invalid configuration detected; regenerating default config.");
                _config = new PluginConfig();
                SaveConfig();
            }
            // Sanity checks
            if (_config.TickSeconds < 0.25f) _config.TickSeconds = 0.25f;
            if (_config.MaxAddPerTick < 1) _config.MaxAddPerTick = 1;
            if (_config.ChatCooldownSeconds < 0f) _config.ChatCooldownSeconds = 0f;
        }

        #endregion

        #region Localization

        private const string MsgNoPermission = "NoPermission";
        private const string MsgAdminOnly = "AdminOnly";
        private const string MsgUsage = "Usage";
        private const string MsgEnabled = "Enabled";
        private const string MsgDisabled = "Disabled";
        private const string MsgStatusOn = "StatusOn";
        private const string MsgStatusOff = "StatusOff";
        private const string MsgRateLimited = "RateLimited";
        private const string MsgCooldown = "Cooldown";

        protected override void LoadDefaultMessages()
        {
            // English (en) is the default language
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [MsgNoPermission] = "You don't have permission to use this.",
                [MsgAdminOnly] = "You must be an admin to use this command.",
                [MsgUsage] = "Usage: /bw on|off|toggle|status",
                [MsgEnabled] = "Bottomless water is now <color=#77FF77>ENABLED</color> for you.",
                [MsgDisabled] = "Bottomless water is now <color=#FF7777>DISABLED</color> for you.",
                [MsgStatusOn] = "Your bottomless water setting: <color=#77FF77>ENABLED</color>.",
                [MsgStatusOff] = "Your bottomless water setting: <color=#FF7777>DISABLED</color>.",
                [MsgRateLimited] = "You're doing that too often; try again later.",
                [MsgCooldown] = "Please wait a moment before toggling again."
            }, this);
        }

        private string Msg(string key, string playerId = null, params object[] args)
        {
            var template = lang.GetMessage(key, this, playerId);
            return args.Length > 0 ? string.Format(template, args) : template;
        }

        #endregion

        #region Data & Permissions

        private void RegisterPermissions()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            if (_config.AutoGrantUseToDefaultGroup)
            {
                // Grant use permission to default group if not already
                if (!permission.GroupHasPermission("default", PermUse))
                    permission.GrantGroupPermission("default", PermUse, this);
            }
        }

        private void LoadData()
        {
            var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, bool>>(DataFileName);
            _playerStates.Clear();
            if (data != null)
            {
                foreach (var kv in data)
                    _playerStates[kv.Key] = kv.Value;
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _playerStates);
        }

        #endregion

        #region Lifecycle Hooks

        private void Init()
        {
            LoadConfigValues();
            RegisterPermissions();
            LoadData();
            _tickInterval = _config.TickSeconds;
            // Register chat command and console commands
            AddCovalenceCommand("bw", nameof(CmdBW));
            AddCovalenceCommand("bottomlesswater.toggle", nameof(CmdToggleConsole));
            AddCovalenceCommand("bottomlesswater.status", nameof(CmdStatusConsole));
            AddCovalenceCommand("bottomlesswater.reload", nameof(CmdReloadConfig));
        }

        private void OnServerInitialized()
        {
            RefreshLiquidContainers();
            timer.Every(_tickInterval, DoTick);
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        #endregion

        #region Entity Tracking

        private void RefreshLiquidContainers()
        {
            _liquidContainers.Clear();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var liquid = networkable.GetComponent<LiquidContainer>();
                if (liquid != null)
                    _liquidContainers.Add(liquid);
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var liquid = entity.GetComponent<LiquidContainer>();
            if (liquid != null)
                _liquidContainers.Add(liquid);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var liquid = entity.GetComponent<LiquidContainer>();
            if (liquid != null)
                _liquidContainers.Remove(liquid);
        }

        #endregion

        #region Main Tick

        private void DoTick()
        {
            if (!_config.AffectLiquidContainers) return;
            if (_liquidContainers.Count == 0) return;

            // Iterate over a snapshot to avoid modification exceptions
            foreach (var liquid in _liquidContainers.ToArray())
            {
                if (liquid == null || liquid.IsDestroyed) continue;
                var entity = liquid as BaseEntity;
                if (entity == null) continue;

                // Prefab filtering: apply whitelist or exclude list
                string spn = entity.ShortPrefabName;
                if (_config.WhiteListShortPrefabNames != null && _config.WhiteListShortPrefabNames.Count > 0)
                {
                    if (!_config.WhiteListShortPrefabNames.Contains(spn))
                        continue;
                }
                else if (_config.ExcludeShortPrefabNames != null && _config.ExcludeShortPrefabNames.Contains(spn))
                {
                    continue;
                }

                // Determine owner
                string ownerId = entity.OwnerID.ToString();
                if (string.IsNullOrEmpty(ownerId)) continue;
                if (!_playerStates.TryGetValue(ownerId, out bool enabled))
                {
                    enabled = _config.EnableByDefault;
                    _playerStates[ownerId] = enabled;
                }
                if (!enabled) continue;

                // Top up liquid amount
                if (liquid.amount < liquid.maxStackSize)
                {
                    int add = Math.Min(_config.MaxAddPerTick, (int)(liquid.maxStackSize - liquid.amount));
                    liquid.amount += add;
                    liquid.SendNetworkUpdate();
                }
            }
        }

        #endregion

        #region Command Rate Limiting

        private bool RateLimited(IPlayer player)
        {
            if (player == null || player.IsServer) return false;
            if (!_rateLimit.TryGetValue(player.Id, out var list))
            {
                list = new List<DateTime>();
                _rateLimit[player.Id] = list;
            }
            var now = DateTime.UtcNow;
            list.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (list.Count >= 5)
            {
                player.Reply(Msg(MsgRateLimited, player.Id));
                return true;
            }
            list.Add(now);
            return false;
        }

        #endregion

        #region Chat Command (/bw)

        private void CmdBW(IPlayer player, string command, string[] args)
        {
            if (player == null || !player.IsConnected) return;
            if (!player.HasPermission(PermUse))
            {
                player.Reply(Msg(MsgNoPermission, player.Id));
                return;
            }
            if (RateLimited(player)) return;

            // Cooldown check to prevent spamming toggles
            if (_toggleCooldowns.TryGetValue(player.Id, out float last) && UnityEngine.Time.realtimeSinceStartup - last < _config.ChatCooldownSeconds)
            {
                player.Reply(Msg(MsgCooldown, player.Id));
                return;
            }
            _toggleCooldowns[player.Id] = UnityEngine.Time.realtimeSinceStartup;

            if (args.Length == 0)
            {
                player.Reply(Msg(MsgUsage, player.Id));
                return;
            }

            // Ensure state exists
            if (!_playerStates.TryGetValue(player.Id, out bool enabled))
            {
                enabled = _config.EnableByDefault;
                _playerStates[player.Id] = enabled;
            }
            var sub = args[0].ToLower();
            switch (sub)
            {
                case "on":
                    enabled = true;
                    _playerStates[player.Id] = true;
                    SaveData();
                    player.Reply(Msg(MsgEnabled, player.Id));
                    LogToggle(player.Name, player.Id, true);
                    break;
                case "off":
                    enabled = false;
                    _playerStates[player.Id] = false;
                    SaveData();
                    player.Reply(Msg(MsgDisabled, player.Id));
                    LogToggle(player.Name, player.Id, false);
                    break;
                case "toggle":
                    enabled = !enabled;
                    _playerStates[player.Id] = enabled;
                    SaveData();
                    player.Reply(Msg(enabled ? MsgEnabled : MsgDisabled, player.Id));
                    LogToggle(player.Name, player.Id, enabled);
                    break;
                case "status":
                    player.Reply(Msg(enabled ? MsgStatusOn : MsgStatusOff, player.Id));
                    break;
                default:
                    player.Reply(Msg(MsgUsage, player.Id));
                    break;
            }
        }

        #endregion

        #region Console/RCON Commands

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
                player?.Reply("Player not found.");
                return;
            }
            // Determine target state
            var action = args[1].ToLower();
            if (!_playerStates.TryGetValue(target.Id, out bool enabled))
            {
                enabled = _config.EnableByDefault;
            }
            bool newState = enabled;
            switch (action)
            {
                case "on":
                    newState = true; break;
                case "off":
                    newState = false; break;
                case "toggle":
                    newState = !enabled; break;
                default:
                    player?.Reply("Usage: bottomlesswater.toggle <player> <on|off|toggle>");
                    return;
            }
            _playerStates[target.Id] = newState;
            SaveData();
            player?.Reply($"Set {target.Name} to {(newState ? "ENABLED" : "DISABLED")}");
            LogToggle(player?.Name ?? "Console", player?.Id ?? "Console", newState, target.Name, target.Id, true);
        }

        private void CmdStatusConsole(IPlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player)) return;
            if (args.Length == 0)
            {
                foreach (var kv in _playerStates)
                {
                    var pl = players.FindPlayer(kv.Key);
                    var name = pl?.Name ?? kv.Key;
                    player?.Reply($"{name}: {(kv.Value ? "ENABLED" : "DISABLED")}");
                }
                return;
            }
            var target = FindPlayer(args[0]);
            if (target == null)
            {
                player?.Reply("Player not found.");
                return;
            }
            if (!_playerStates.TryGetValue(target.Id, out bool enabled))
                enabled = _config.EnableByDefault;
            player?.Reply($"{target.Name}: {(enabled ? "ENABLED" : "DISABLED")}");
        }

        private void CmdReloadConfig(IPlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player)) return;
            LoadConfigValues();
            SaveConfig();
            player?.Reply("Configuration reloaded.");
        }

        #endregion

        #region Helpers

        private bool HasAdminAccess(IPlayer player)
        {
            // Console or RCON: null player
            if (player == null)
            {
                return !_config.RequireAdminForRcon;
            }
            if (player.IsServer) return true;
            if (player.HasPermission(PermAdmin)) return true;
            player.Reply(Msg(MsgAdminOnly, player.Id));
            return false;
        }

        private void LogToggle(string actorName, string actorId, bool state, string targetName = null, string targetId = null, bool admin = false)
        {
            string targetDesc = targetName != null ? $"{targetName} ({targetId})" : $"{actorName} ({actorId})";
            string log = $"[{(admin ? "ADMIN" : "PLAYER")}] {actorName} {(state ? "ENABLED" : "DISABLED")} BottomlessWater for {targetDesc} at {DateTime.UtcNow:u}";
            Puts(log);
            LogToFile(LogFile, log, this);
        }

        private IPlayer FindPlayer(string ident)
        {
            if (string.IsNullOrWhiteSpace(ident)) return null;
            // Attempt ID or exact match
            var p = players.FindPlayer(ident);
            if (p != null) return p;
            // Partial name search among connected players
            foreach (var pl in players.Connected)
            {
                if (pl.Name != null && pl.Name.IndexOf(ident, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pl;
            }
            return null;
        }

        #endregion
    }
}
