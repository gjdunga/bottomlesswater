using System;
using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "Gabriel", "3.0.0")]
    [Description("Infinite water behavior for owned liquid containers with modern Rust API, security hardening, and performance improvements.")]
    public class BottomlessWater : RustPlugin
    {
        private const string DataFileName = "BottomlessWaterData";
        private const string LogFile = "BottomlessWater";
        private const string PermUse = "bottomlesswater.use";
        private const string PermAdmin = "bottomlesswater.admin";

        private PluginConfig _config;
        private StoredData _storedData;

        private readonly Dictionary<ulong, bool> _playerStates = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, float> _toggleCooldowns = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, List<float>> _rateLimitWindows = new Dictionary<ulong, List<float>>();

        private readonly HashSet<LiquidContainer> _liquidContainers = new HashSet<LiquidContainer>();
        private readonly List<LiquidContainer> _tickBuffer = new List<LiquidContainer>();
        private HashSet<string> _whitelistShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _excludeShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Timer _tickTimer;
        private Timer _saveTimer;
        private bool _dirty;
        private ItemDefinition _waterDefinition;

        private class PluginConfig
        {
            public float TickSeconds = 1f;
            public int MaxAddPerTick = 1000;
            public bool AffectLiquidContainers = true;
            public bool EnableByDefault = true;
            public bool AutoGrantUseToDefaultGroup = true;
            public List<string> WhiteListShortPrefabNames = new List<string>();
            public List<string> ExcludeShortPrefabNames = new List<string>();
            public float ChatCooldownSeconds = 2f;
            public int RateLimitMaxPerMinute = 5;
            public bool FillEmptyContainers = false;
            public bool ClearDataOnWipe = false;
            public float SaveDebounceSeconds = 2f;
        }

        private class StoredData
        {
            public Dictionary<string, bool> PlayerStates = new Dictionary<string, bool>();
        }

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    throw new Exception("Config was null after deserialization.");
                }
            }
            catch
            {
                PrintWarning("Invalid configuration detected; regenerating defaults.");
                LoadDefaultConfig();
            }

            _config.TickSeconds = Mathf.Max(0.25f, _config.TickSeconds);
            _config.MaxAddPerTick = Math.Max(1, _config.MaxAddPerTick);
            _config.ChatCooldownSeconds = Mathf.Max(0f, _config.ChatCooldownSeconds);
            _config.RateLimitMaxPerMinute = Math.Max(1, _config.RateLimitMaxPerMinute);
            _config.SaveDebounceSeconds = Mathf.Max(0.1f, _config.SaveDebounceSeconds);

            SaveConfig();
            RebuildPrefabSets();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void RebuildPrefabSets()
        {
            _whitelistShortPrefabNames = BuildPrefabSet(_config.WhiteListShortPrefabNames);
            _excludeShortPrefabNames = BuildPrefabSet(_config.ExcludeShortPrefabNames);
        }

        private static HashSet<string> BuildPrefabSet(List<string> values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
            {
                return set;
            }

            foreach (var entry in values)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    set.Add(entry.Trim());
                }
            }

            return set;
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

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);

            if (_config.AutoGrantUseToDefaultGroup && !permission.GroupHasPermission("default", PermUse))
            {
                permission.GrantGroupPermission("default", PermUse, this);
            }

            LoadData();
            _waterDefinition = ItemManager.FindItemDefinition("water");
        }

        private void OnServerInitialized()
        {
            RefreshLiquidContainers();
            _tickTimer = timer.Every(_config.TickSeconds, DoTick);
        }

        private void Unload()
        {
            _tickTimer?.Destroy();
            _saveTimer?.Destroy();
            SaveDataImmediate();
        }

        private void OnServerSave() => SaveDataImmediate();

        private void OnNewSave(string filename)
        {
            if (!_config.ClearDataOnWipe)
            {
                return;
            }

            _playerStates.Clear();
            MarkDirty();
            SaveDataImmediate();
            Puts($"Cleared {nameof(BottomlessWater)} player state due to wipe ({filename}).");
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
            {
                return;
            }

            _toggleCooldowns.Remove(player.userID);
            _rateLimitWindows.Remove(player.userID);
        }

        #endregion

        #region Data

        private void LoadData()
        {
            try
            {
                _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName) ?? new StoredData();
            }
            catch
            {
                _storedData = new StoredData();
            }

            _playerStates.Clear();
            foreach (var entry in _storedData.PlayerStates)
            {
                if (ulong.TryParse(entry.Key, out var userId))
                {
                    _playerStates[userId] = entry.Value;
                }
            }
        }

        private void SaveDataImmediate()
        {
            if (!_dirty && _storedData != null)
            {
                return;
            }

            _storedData = new StoredData();
            foreach (var entry in _playerStates)
            {
                _storedData.PlayerStates[entry.Key.ToString()] = entry.Value;
            }

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _storedData);
            _dirty = false;
            _saveTimer = null;
        }

        private void MarkDirty()
        {
            _dirty = true;
            _saveTimer?.Destroy();
            _saveTimer = timer.Once(_config.SaveDebounceSeconds, SaveDataImmediate);
        }

        #endregion

        #region Entity Tracking

        private void RefreshLiquidContainers()
        {
            _liquidContainers.Clear();

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var liquid = networkable as LiquidContainer;
                if (liquid != null)
                {
                    _liquidContainers.Add(liquid);
                }
            }
        }

        private void OnEntitySpawned(LiquidContainer liquid)
        {
            if (liquid != null)
            {
                _liquidContainers.Add(liquid);
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var liquid = entity as LiquidContainer;
            if (liquid != null)
            {
                _liquidContainers.Remove(liquid);
            }
        }

        #endregion

        #region Tick Logic

        private void DoTick()
        {
            if (!_config.AffectLiquidContainers || _liquidContainers.Count == 0)
            {
                return;
            }

            _tickBuffer.Clear();
            foreach (var liquid in _liquidContainers)
            {
                _tickBuffer.Add(liquid);
            }

            for (var i = 0; i < _tickBuffer.Count; i++)
            {
                var liquid = _tickBuffer[i];
                if (liquid == null || liquid.IsDestroyed)
                {
                    _liquidContainers.Remove(liquid);
                    continue;
                }

                var entity = liquid as BaseEntity;
                if (entity == null)
                {
                    continue;
                }

                if (!ShouldProcessPrefab(entity.ShortPrefabName))
                {
                    continue;
                }

                var ownerId = entity.OwnerID;
                if (ownerId == 0UL || !IsEnabledFor(ownerId))
                {
                    continue;
                }

                FillLiquidItems(liquid);
            }
        }

        private bool ShouldProcessPrefab(string shortPrefabName)
        {
            if (_whitelistShortPrefabNames.Count > 0)
            {
                return _whitelistShortPrefabNames.Contains(shortPrefabName);
            }

            return !_excludeShortPrefabNames.Contains(shortPrefabName);
        }

        private bool IsEnabledFor(ulong userId)
        {
            if (_playerStates.TryGetValue(userId, out var enabled))
            {
                return enabled;
            }

            _playerStates[userId] = _config.EnableByDefault;
            MarkDirty();
            return _config.EnableByDefault;
        }

        private void FillLiquidItems(LiquidContainer liquid)
        {
            var inventory = liquid.inventory;
            if (inventory == null)
            {
                return;
            }

            if (_config.FillEmptyContainers && inventory.itemList.Count == 0 && _waterDefinition != null)
            {
                var item = ItemManager.Create(_waterDefinition, Math.Min(_config.MaxAddPerTick, _waterDefinition.stackable));
                if (item != null && !item.MoveToContainer(inventory))
                {
                    item.Remove();
                }
            }

            var changed = false;
            var items = inventory.itemList;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                var maxStack = item.MaxStackable();
                if (maxStack <= 0 || item.amount >= maxStack)
                {
                    continue;
                }

                var add = Math.Min(_config.MaxAddPerTick, maxStack - item.amount);
                if (add <= 0)
                {
                    continue;
                }

                item.amount += add;
                item.MarkDirty();
                changed = true;
            }

            if (changed)
            {
                liquid.SendNetworkUpdateImmediate();
            }
        }

        #endregion

        #region Commands

        [ChatCommand("bw")]
        private void CmdBottomlessWater(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, PermUse))
            {
                SendReply(player, Msg(MsgNoPermission, player.UserIDString));
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, Msg(MsgUsage, player.UserIDString));
                return;
            }

            var action = args[0].ToLowerInvariant();
            var isMutating = action == "on" || action == "off" || action == "toggle";

            if (isMutating)
            {
                if (IsRateLimited(player.userID, player.UserIDString))
                {
                    return;
                }

                if (IsOnToggleCooldown(player.userID))
                {
                    SendReply(player, Msg(MsgCooldown, player.UserIDString));
                    return;
                }

                _toggleCooldowns[player.userID] = Time.realtimeSinceStartup;
            }

            var enabled = IsEnabledFor(player.userID);
            switch (action)
            {
                case "on":
                    SetPlayerState(player.userID, true);
                    SendReply(player, Msg(MsgEnabled, player.UserIDString));
                    LogToggle(player.displayName, player.UserIDString, true);
                    break;
                case "off":
                    SetPlayerState(player.userID, false);
                    SendReply(player, Msg(MsgDisabled, player.UserIDString));
                    LogToggle(player.displayName, player.UserIDString, false);
                    break;
                case "toggle":
                    enabled = !enabled;
                    SetPlayerState(player.userID, enabled);
                    SendReply(player, Msg(enabled ? MsgEnabled : MsgDisabled, player.UserIDString));
                    LogToggle(player.displayName, player.UserIDString, enabled);
                    break;
                case "status":
                    SendReply(player, Msg(enabled ? MsgStatusOn : MsgStatusOff, player.UserIDString));
                    break;
                default:
                    SendReply(player, Msg(MsgUsage, player.UserIDString));
                    break;
            }
        }

        [ConsoleCommand("bottomlesswater.toggle")]
        private void CmdToggleConsole(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg))
            {
                return;
            }

            if (arg.Args == null || arg.Args.Length < 2)
            {
                Reply(arg, "Usage: bottomlesswater.toggle <steamid64> <on|off|toggle>");
                return;
            }

            if (!TryFindPlayerBySteamId(arg.Args[0], out var target))
            {
                Reply(arg, "Player not found. Use full SteamID64.");
                return;
            }

            var action = arg.Args[1].ToLowerInvariant();
            var current = IsEnabledFor(target.userID);
            bool? next = null;

            switch (action)
            {
                case "on":
                    next = true;
                    break;
                case "off":
                    next = false;
                    break;
                case "toggle":
                    next = !current;
                    break;
            }

            if (!next.HasValue)
            {
                Reply(arg, "Usage: bottomlesswater.toggle <steamid64> <on|off|toggle>");
                return;
            }

            SetPlayerState(target.userID, next.Value);
            Reply(arg, $"Set {target.displayName} ({target.UserIDString}) to {(next.Value ? "ENABLED" : "DISABLED")}");

            var actor = arg.Player();
            LogToggle(actor?.displayName ?? "Console", actor?.UserIDString ?? "Console", next.Value, target.displayName, target.UserIDString, true);
        }

        [ConsoleCommand("bottomlesswater.status")]
        private void CmdStatusConsole(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg))
            {
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                foreach (var entry in _playerStates)
                {
                    var player = BasePlayer.FindAwakeOrSleeping(entry.Key);
                    var name = player != null ? player.displayName : entry.Key.ToString();
                    Reply(arg, $"{name} ({entry.Key}): {(entry.Value ? "ENABLED" : "DISABLED")}");
                }

                return;
            }

            if (!TryFindPlayerBySteamId(arg.Args[0], out var target))
            {
                Reply(arg, "Player not found. Use full SteamID64.");
                return;
            }

            var enabled = IsEnabledFor(target.userID);
            Reply(arg, $"{target.displayName} ({target.UserIDString}): {(enabled ? "ENABLED" : "DISABLED")}");
        }

        [ConsoleCommand("bottomlesswater.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg))
            {
                return;
            }

            LoadConfig();
            _tickTimer?.Destroy();
            _tickTimer = timer.Every(_config.TickSeconds, DoTick);
            Reply(arg, "BottomlessWater configuration reloaded.");
        }

        #endregion

        #region Command Helpers

        private bool IsRateLimited(ulong userId, string userIdString)
        {
            if (!_rateLimitWindows.TryGetValue(userId, out var window))
            {
                window = new List<float>();
                _rateLimitWindows[userId] = window;
            }

            var now = Time.realtimeSinceStartup;
            for (var i = window.Count - 1; i >= 0; i--)
            {
                if (now - window[i] > 60f)
                {
                    window.RemoveAt(i);
                }
            }

            if (window.Count >= _config.RateLimitMaxPerMinute)
            {
                var player = BasePlayer.FindByID(userId);
                if (player != null)
                {
                    SendReply(player, Msg(MsgRateLimited, userIdString));
                }

                return true;
            }

            window.Add(now);
            return false;
        }

        private bool IsOnToggleCooldown(ulong userId)
        {
            if (!_toggleCooldowns.TryGetValue(userId, out var lastToggleTime))
            {
                return false;
            }

            return Time.realtimeSinceStartup - lastToggleTime < _config.ChatCooldownSeconds;
        }

        private bool HasAdminAccess(ConsoleSystem.Arg arg)
        {
            if (arg == null)
            {
                return false;
            }

            if (arg.Connection == null)
            {
                return true;
            }

            var player = arg.Player();
            if (player != null && player.IsAdmin)
            {
                return true;
            }

            if (player != null && permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                return true;
            }

            Reply(arg, Msg(MsgAdminOnly, player?.UserIDString));
            return false;
        }

        private static bool TryFindPlayerBySteamId(string input, out BasePlayer player)
        {
            player = null;
            if (string.IsNullOrWhiteSpace(input) || !ulong.TryParse(input, out var userId))
            {
                return false;
            }

            player = BasePlayer.FindAwakeOrSleeping(userId);
            return player != null;
        }

        private void SetPlayerState(ulong userId, bool enabled)
        {
            _playerStates[userId] = enabled;
            MarkDirty();
        }

        private void Reply(ConsoleSystem.Arg arg, string text)
        {
            if (arg?.Connection != null)
            {
                SendReply(arg, text);
            }
            else
            {
                Puts(text);
            }
        }

        private void LogToggle(string actorName, string actorId, bool state, string targetName = null, string targetId = null, bool admin = false)
        {
            var targetDescription = targetName != null ? $"{targetName} ({targetId})" : $"{actorName} ({actorId})";
            var log = $"[{(admin ? "ADMIN" : "PLAYER")}] {actorName} {(state ? "ENABLED" : "DISABLED")} BottomlessWater for {targetDescription} at {DateTime.UtcNow:u}";
            Puts(log);
            LogToFile(LogFile, log, this);
        }

        #endregion
    }
}
