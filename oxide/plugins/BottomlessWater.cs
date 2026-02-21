// BottomlessWater - Oxide/uMod plugin for Rust (Facepunch)
// Provides infinite-water behaviour for owned liquid containers.
// Repository: https://github.com/gjdunga/bottomlesswater
// License: MIT

using System;
using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "Gabriel", "3.3.0")]
    [Description("Infinite water behaviour for owned liquid containers with per-player toggles, admin controls, security hardening, and verbose logging.")]
    public class BottomlessWater : RustPlugin
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Name used for the Oxide data file (no extension).</summary>
        private const string DataFileName = "BottomlessWaterData";

        /// <summary>Name used for the plugin log file (no extension).</summary>
        private const string LogFile = "BottomlessWater";

        /// <summary>Permission required for a player to use /bw commands and receive infinite water.</summary>
        private const string PermUse = "bottomlesswater.use";

        /// <summary>Permission required to execute admin console commands targeting other players.</summary>
        private const string PermAdmin = "bottomlesswater.admin";

        /// <summary>
        /// Maximum number of characters accepted from the first chat command argument.
        /// Prevents unnecessarily long strings reaching switch/log paths.
        /// </summary>
        private const int MaxArgLength = 32;

        // ─────────────────────────────────────────────────────────────────────
        // PlayerState class
        //
        // Replaces the raw bool used in earlier versions. Using a dedicated class
        // rather than a value tuple avoids ValueTuple assembly dependencies that the
        // uMod build server may not resolve, and provides a stable serialisation
        // boundary for future fields (e.g. per-player quota, last-toggle time).
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Persistent per-player state stored in <see cref="_playerStates"/>.
        ///
        /// Design note: this is intentionally a plain class (reference type) rather than
        /// a struct or value tuple. uMod's build server historically rejects
        /// System.ValueTuple usages; plain private classes compile cleanly across all
        /// supported target frameworks.
        /// </summary>
        private class PlayerState
        {
            /// <summary>
            /// True when the player has infinite water enabled; false when explicitly
            /// disabled. Seeded from PluginConfig.EnableByDefault on first encounter.
            /// </summary>
            public bool Enabled;

            /// <summary>Parameterless constructor required for JSON deserialisation.</summary>
            public PlayerState() { }

            /// <summary>Convenience constructor.</summary>
            public PlayerState(bool enabled) { Enabled = enabled; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fields
        // ─────────────────────────────────────────────────────────────────────

        private PluginConfig _config;
        private StoredData   _storedData;

        /// <summary>
        /// Per-player infinite-water state keyed by SteamID64.
        /// Each value is a PlayerState class instance (not a raw bool or value tuple)
        /// to satisfy uMod build-server compilation requirements.
        /// </summary>
        private readonly Dictionary<ulong, PlayerState> _playerStates = new Dictionary<ulong, PlayerState>();

        /// <summary>
        /// Tracks the realtime timestamp of the most recent accepted toggle per player.
        /// Used to enforce PluginConfig.ChatCooldownSeconds between successive
        /// on/off/toggle actions.
        /// </summary>
        private readonly Dictionary<ulong, float> _toggleCooldowns = new Dictionary<ulong, float>();

        /// <summary>
        /// Sliding 60-second window of accepted command timestamps per player.
        /// Used to enforce PluginConfig.RateLimitMaxPerMinute.
        /// </summary>
        private readonly Dictionary<ulong, List<float>> _rateLimitWindows = new Dictionary<ulong, List<float>>();

        /// <summary>All tracked LiquidContainer entities currently on the server.</summary>
        private readonly HashSet<LiquidContainer> _liquidContainers = new HashSet<LiquidContainer>();

        /// <summary>
        /// Snapshot buffer filled at the start of each tick to avoid mutating
        /// _liquidContainers while iterating.
        /// </summary>
        private readonly List<LiquidContainer> _tickBuffer = new List<LiquidContainer>();

        /// <summary>
        /// Per-tick cache of ownerIds whose PermUse permission has already been verified
        /// this tick. Avoids repeated string allocations and hash lookups when multiple
        /// containers share the same owner.
        /// </summary>
        private readonly HashSet<ulong> _tickPermittedOwners = new HashSet<ulong>();

        /// <summary>Resolved short-prefab whitelist from config. Empty means all allowed.</summary>
        private HashSet<string> _whitelistShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolved short-prefab exclusion list from config.</summary>
        private HashSet<string> _excludeShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Timer _tickTimer;
        private Timer _saveTimer;
        private bool  _dirty;

        /// <summary>
        /// Cached ItemDefinition for "water"; resolved once in Init.
        /// Used in FillLiquidItems to restrict filling to water-type items only,
        /// preventing unintended top-up of salt water or other liquids.
        /// </summary>
        private ItemDefinition _waterDefinition;

        // ─────────────────────────────────────────────────────────────────────
        // Configuration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Serialised configuration for BottomlessWater.</summary>
        private class PluginConfig
        {
            /// <summary>Interval in seconds between fill ticks. Minimum clamped to 0.25 s.</summary>
            public float TickSeconds = 1f;

            /// <summary>Maximum water units added to a single item stack per tick. Clamped to >= 1.</summary>
            public int MaxAddPerTick = 1000;

            /// <summary>Whether to process LiquidContainer entities at all.</summary>
            public bool AffectLiquidContainers = true;

            /// <summary>
            /// Default on/off state written the first time an unknown ownerId is
            /// encountered during a tick.
            /// </summary>
            public bool EnableByDefault = true;

            /// <summary>
            /// If true, grants PermUse to Oxide's "default" group on load so all
            /// authenticated players inherit it immediately.
            /// </summary>
            public bool AutoGrantUseToDefaultGroup = true;

            /// <summary>
            /// Optional whitelist. If non-empty, ONLY containers whose ShortPrefabName
            /// appears here are processed. Comparisons are case-insensitive.
            /// </summary>
            public List<string> WhiteListShortPrefabNames = new List<string>();

            /// <summary>
            /// Optional exclusion list. Containers whose ShortPrefabName appears here are
            /// skipped. Ignored when WhiteListShortPrefabNames is non-empty.
            /// </summary>
            public List<string> ExcludeShortPrefabNames = new List<string>();

            /// <summary>
            /// Minimum seconds a player must wait between successive on/off/toggle actions.
            /// Clamped to >= 0.
            /// </summary>
            public float ChatCooldownSeconds = 2f;

            /// <summary>
            /// Maximum on/off/toggle actions a player may perform within any 60-second
            /// window. Clamped to >= 1.
            /// </summary>
            public int RateLimitMaxPerMinute = 5;

            /// <summary>
            /// If true, create a new water item in completely empty containers owned by
            /// eligible players (subject to MaxAddPerTick and the item's stackable cap).
            /// </summary>
            public bool FillEmptyContainers = false;

            /// <summary>
            /// If true, wipe all stored player states when the server detects a map wipe
            /// via OnNewSave.
            /// </summary>
            public bool ClearDataOnWipe = false;

            /// <summary>
            /// Debounce delay in seconds before a pending dirty-state write is flushed.
            /// Clamped to >= 0.1 s.
            /// </summary>
            public float SaveDebounceSeconds = 2f;
        }

        /// <summary>
        /// On-disk data model. Player states are keyed by SteamID64 string for JSON
        /// compatibility; the value is a plain bool to keep the file human-readable.
        /// The bool is wrapped in PlayerState only in memory.
        /// </summary>
        private class StoredData
        {
            public Dictionary<string, bool> PlayerStates = new Dictionary<string, bool>();
        }

        /// <inheritdoc/>
        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        /// <inheritdoc/>
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config was null after deserialisation.");
            }
            catch
            {
                PrintWarning("Invalid configuration detected; regenerating defaults.");
                LoadDefaultConfig();
            }

            _config.TickSeconds           = Mathf.Max(0.25f, _config.TickSeconds);
            _config.MaxAddPerTick         = Math.Max(1, _config.MaxAddPerTick);
            _config.ChatCooldownSeconds   = Mathf.Max(0f, _config.ChatCooldownSeconds);
            _config.RateLimitMaxPerMinute = Math.Max(1, _config.RateLimitMaxPerMinute);
            _config.SaveDebounceSeconds   = Mathf.Max(0.1f, _config.SaveDebounceSeconds);

            SaveConfig();
            RebuildPrefabSets();
        }

        /// <inheritdoc/>
        protected override void SaveConfig() => Config.WriteObject(_config, true);

        /// <summary>
        /// Rebuilds the in-memory HashSets for whitelist/exclusion from config.
        /// Called after every config load or hot-reload.
        /// </summary>
        private void RebuildPrefabSets()
        {
            _whitelistShortPrefabNames = BuildPrefabSet(_config.WhiteListShortPrefabNames);
            _excludeShortPrefabNames   = BuildPrefabSet(_config.ExcludeShortPrefabNames);
        }

        /// <summary>
        /// Converts a List of raw prefab name strings into a trimmed, case-insensitive
        /// HashSet, ignoring null/whitespace entries.
        /// </summary>
        private static HashSet<string> BuildPrefabSet(List<string> values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null) return set;
            foreach (var entry in values)
                if (!string.IsNullOrWhiteSpace(entry))
                    set.Add(entry.Trim());
            return set;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Localization keys
        // ─────────────────────────────────────────────────────────────────────

        private const string MsgNoPermission = "NoPermission";
        private const string MsgAdminOnly    = "AdminOnly";
        private const string MsgUsage        = "Usage";
        private const string MsgEnabled      = "Enabled";
        private const string MsgDisabled     = "Disabled";
        private const string MsgStatusOn     = "StatusOn";
        private const string MsgStatusOff    = "StatusOff";
        private const string MsgRateLimited  = "RateLimited";
        private const string MsgCooldown     = "Cooldown";

        /// <inheritdoc/>
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [MsgNoPermission] = "You don't have permission to use this.",
                [MsgAdminOnly]    = "You must be an admin to use this command.",
                [MsgUsage]        = "Usage: /bw on|off|toggle|status",
                [MsgEnabled]      = "Bottomless water is now <color=#77FF77>ENABLED</color> for you.",
                [MsgDisabled]     = "Bottomless water is now <color=#FF7777>DISABLED</color> for you.",
                [MsgStatusOn]     = "Your bottomless water setting: <color=#77FF77>ENABLED</color>.",
                [MsgStatusOff]    = "Your bottomless water setting: <color=#FF7777>DISABLED</color>.",
                [MsgRateLimited]  = "You're doing that too often; try again later.",
                [MsgCooldown]     = "Please wait a moment before toggling again."
            }, this);
        }

        /// <summary>
        /// Returns the localised message for key, optionally formatted with args.
        /// Passes null to lang.GetMessage when playerId is empty so the server
        /// default language is used rather than the implicit null path.
        /// </summary>
        private string Msg(string key, string playerId = null, params object[] args)
        {
            var template = lang.GetMessage(key, this, string.IsNullOrEmpty(playerId) ? null : playerId);
            return args.Length > 0 ? string.Format(template, args) : template;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Oxide lifecycle hook: called before the server has finished initialising.
        /// Registers permissions, optionally auto-grants the use permission to the
        /// default group, and loads persisted player state.
        /// </summary>
        private void Init()
        {
            permission.RegisterPermission(PermUse,   this);
            permission.RegisterPermission(PermAdmin, this);

            if (_config.AutoGrantUseToDefaultGroup && !permission.GroupHasPermission("default", PermUse))
                permission.GrantGroupPermission("default", PermUse, this);

            LoadData();

            _waterDefinition = ItemManager.FindItemDefinition("water");
            if (_waterDefinition == null)
                PrintWarning("Could not resolve 'water' ItemDefinition. FillEmptyContainers will not function.");
        }

        /// <summary>
        /// Oxide lifecycle hook: called once the server world is fully initialised.
        /// Populates the initial container set and starts the periodic fill tick.
        /// </summary>
        private void OnServerInitialized()
        {
            RefreshLiquidContainers();
            _tickTimer = timer.Every(_config.TickSeconds, DoTick);
        }

        /// <summary>
        /// Oxide lifecycle hook: called when the plugin is unloaded (server shutdown or
        /// explicit unload). Destroys timers and flushes any unsaved state to disk.
        /// </summary>
        private void Unload()
        {
            _tickTimer?.Destroy();
            _saveTimer?.Destroy();
            SaveDataImmediate();
        }

        /// <summary>Oxide hook: flushes state to disk whenever the server autosaves.</summary>
        private void OnServerSave() => SaveDataImmediate();

        /// <summary>
        /// Oxide hook: fires when a new save file is detected (map wipe).
        /// Clears all stored player states if PluginConfig.ClearDataOnWipe is true.
        /// </summary>
        private void OnNewSave(string filename)
        {
            if (!_config.ClearDataOnWipe) return;
            _playerStates.Clear();
            MarkDirty();
            SaveDataImmediate();
            Puts($"Cleared BottomlessWater player state due to wipe ({filename}).");
        }

        /// <summary>
        /// Oxide hook: fires when a player disconnects.
        /// Removes ephemeral per-player rate-limit and cooldown entries to prevent
        /// unbounded dictionary growth over long server uptime.
        /// </summary>
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _toggleCooldowns.Remove(player.userID);
            _rateLimitWindows.Remove(player.userID);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Data persistence
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads stored player states from the Oxide data file and hydrates
        /// _playerStates. The on-disk format is Dictionary-string-bool for human
        /// readability; each bool is wrapped in a PlayerState instance when loaded
        /// into memory. Silently resets to empty on any read/parse error.
        /// </summary>
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
                    _playerStates[userId] = new PlayerState(entry.Value);
            }
        }

        /// <summary>
        /// Writes _playerStates to disk immediately.
        /// No-ops when the dirty flag is not set, preventing unnecessary I/O.
        /// Direct callers: Unload and OnServerSave only. All other callers should use
        /// MarkDirty to debounce writes.
        /// </summary>
        private void SaveDataImmediate()
        {
            if (!_dirty) return;

            _storedData = new StoredData();
            foreach (var entry in _playerStates)
                _storedData.PlayerStates[entry.Key.ToString()] = entry.Value.Enabled;

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _storedData);
            _dirty     = false;
            _saveTimer = null;
        }

        /// <summary>
        /// Marks the in-memory state as modified and schedules a debounced disk write
        /// after PluginConfig.SaveDebounceSeconds to coalesce rapid changes.
        /// </summary>
        private void MarkDirty()
        {
            _dirty = true;
            _saveTimer?.Destroy();
            _saveTimer = timer.Once(_config.SaveDebounceSeconds, SaveDataImmediate);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Entity tracking
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scans all server entities and populates _liquidContainers.
        /// Called once during OnServerInitialized; subsequent additions and removals
        /// are tracked via OnEntitySpawned and OnEntityKill.
        /// </summary>
        private void RefreshLiquidContainers()
        {
            _liquidContainers.Clear();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var liquid = networkable as LiquidContainer;
                if (liquid != null)
                    _liquidContainers.Add(liquid);
            }
        }

        /// <summary>Oxide hook: fires when any LiquidContainer entity spawns.</summary>
        private void OnEntitySpawned(LiquidContainer liquid)
        {
            if (liquid != null)
                _liquidContainers.Add(liquid);
        }

        /// <summary>
        /// Oxide hook: fires when any networked entity is destroyed.
        /// Removes the entity from the tracked set if it was a LiquidContainer.
        /// </summary>
        private void OnEntityKill(BaseNetworkable entity)
        {
            var liquid = entity as LiquidContainer;
            if (liquid != null)
                _liquidContainers.Remove(liquid);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tick logic
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Core periodic fill tick. Iterates all tracked containers, validates each one,
        /// checks ownership and permission, and calls FillLiquidItems for eligible containers.
        ///
        /// Permission model enforced here (defence-in-depth beyond stored state):
        ///   1. Container must not be destroyed.
        ///   2. ShortPrefabName must pass the whitelist/exclusion filter.
        ///   3. OwnerID must be non-zero (unowned containers are never filled).
        ///   4. Owner must hold PermUse (checked once per unique owner per tick via
        ///      _tickPermittedOwners cache; permission revocation takes effect next tick).
        ///   5. Owner's stored state must be enabled.
        /// </summary>
        private void DoTick()
        {
            if (!_config.AffectLiquidContainers || _liquidContainers.Count == 0)
                return;

            _tickBuffer.Clear();
            foreach (var liquid in _liquidContainers)
                _tickBuffer.Add(liquid);

            _tickPermittedOwners.Clear();

            for (var i = 0; i < _tickBuffer.Count; i++)
            {
                var liquid = _tickBuffer[i];
                if (liquid == null || liquid.IsDestroyed)
                {
                    _liquidContainers.Remove(liquid);
                    continue;
                }

                var entity = liquid as BaseEntity;
                if (entity == null) continue;

                if (!ShouldProcessPrefab(entity.ShortPrefabName)) continue;

                var ownerId = entity.OwnerID;
                if (ownerId == 0UL) continue;

                // SECURITY: always re-verify PermUse each tick via the per-tick cache.
                // Revocation of the permission takes effect on the next tick without
                // requiring the player to explicitly run /bw off.
                if (!_tickPermittedOwners.Contains(ownerId))
                {
                    if (!permission.UserHasPermission(ownerId.ToString(), PermUse))
                        continue;
                    _tickPermittedOwners.Add(ownerId);
                }

                if (!IsEnabledFor(ownerId)) continue;

                FillLiquidItems(liquid);
            }
        }

        /// <summary>
        /// Returns true if a container with the given shortPrefabName should be processed.
        /// If the whitelist is non-empty the name must appear in it; otherwise the name
        /// must not appear in the exclusion list.
        /// </summary>
        private bool ShouldProcessPrefab(string shortPrefabName)
        {
            if (_whitelistShortPrefabNames.Count > 0)
                return _whitelistShortPrefabNames.Contains(shortPrefabName);
            return !_excludeShortPrefabNames.Contains(shortPrefabName);
        }

        /// <summary>
        /// Returns the stored infinite-water state for userId.
        /// If the player has no stored state, seeds EnableByDefault and calls MarkDirty.
        /// Note: this has a write side-effect on first access; only call it after the
        /// permission check so non-permitted players do not pollute the dictionary.
        /// </summary>
        private bool IsEnabledFor(ulong userId)
        {
            PlayerState state;
            if (_playerStates.TryGetValue(userId, out state))
                return state.Enabled;

            var newState = new PlayerState(_config.EnableByDefault);
            _playerStates[userId] = newState;
            MarkDirty();
            return newState.Enabled;
        }

        /// <summary>
        /// Fills water-type items in the container's inventory up to their stack cap,
        /// adding at most MaxAddPerTick units per item per tick.
        ///
        /// SECURITY: Only items whose info matches _waterDefinition are filled.
        /// Without this guard the plugin would also top up salt water, crude oil,
        /// or other liquids that share a LiquidContainer, which is exploitable if
        /// those items have economic value in the game economy.
        ///
        /// If FillEmptyContainers is true and the inventory is completely empty,
        /// attempts to create a new water item and move it into the container.
        ///
        /// A network update is sent only when at least one item actually changed.
        /// </summary>
        private void FillLiquidItems(LiquidContainer liquid)
        {
            var inventory = liquid.inventory;
            if (inventory == null) return;

            if (_config.FillEmptyContainers && inventory.itemList.Count == 0 && _waterDefinition != null)
            {
                var stackable = _waterDefinition.stackable;
                // Guard stackable > 0 to prevent creating a zero-amount item, which
                // produces a broken/invisible entry in Rust's inventory system.
                if (stackable > 0)
                {
                    var item = ItemManager.Create(_waterDefinition, Math.Min(_config.MaxAddPerTick, stackable));
                    if (item != null && !item.MoveToContainer(inventory))
                        item.Remove();
                }
            }

            var changed = false;
            var items   = inventory.itemList;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                // SECURITY FIX: only top up fresh water items. This prevents inadvertent
                // or exploitable filling of salt water, crude oil, or other liquid item
                // types that may coexist in a LiquidContainer inventory.
                if (_waterDefinition != null && item.info != _waterDefinition) continue;

                var maxStack = item.MaxStackable();
                if (maxStack <= 0 || item.amount >= maxStack) continue;

                var add = Math.Min(_config.MaxAddPerTick, maxStack - item.amount);
                if (add <= 0) continue;

                item.amount += add;
                item.MarkDirty();
                changed = true;
            }

            if (changed)
                liquid.SendNetworkUpdateImmediate();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Chat command
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Chat command /bw: lets a permitted player enable, disable, toggle, or query
        /// their infinite-water state.
        ///
        /// Subcommands: on | off | toggle | status
        ///
        /// Rate-limiting and cooldown apply only to mutating actions (on/off/toggle).
        /// The cooldown timestamp is recorded only after BOTH guards pass to prevent a
        /// failed rate-limit check from advancing the cooldown clock.
        /// </summary>
        [ChatCommand("bw")]
        private void CmdBottomlessWater(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

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

            // Cap argument length before any further string work to limit attack surface.
            var rawAction = args[0].Length > MaxArgLength ? args[0].Substring(0, MaxArgLength) : args[0];
            var action    = rawAction.ToLowerInvariant();
            var isMutating = action == "on" || action == "off" || action == "toggle";

            if (isMutating)
            {
                // Rate-limit check must precede the cooldown check so a rate-limited player
                // does not have their cooldown clock advanced by a rejected command.
                if (IsRateLimited(player)) return;

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

        // ─────────────────────────────────────────────────────────────────────
        // Console commands
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Console command: bottomlesswater.toggle &lt;steamid64&gt; &lt;on|off|toggle&gt;
        /// Admin command to override the infinite-water state for another player.
        /// Accepts server console (no Connection) or an in-game admin with PermAdmin.
        /// </summary>
        [ConsoleCommand("bottomlesswater.toggle")]
        private void CmdToggleConsole(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg)) return;

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

            var action  = arg.Args[1].ToLowerInvariant();
            var current = IsEnabledFor(target.userID);
            bool? next  = null;

            switch (action)
            {
                case "on":     next = true;     break;
                case "off":    next = false;    break;
                case "toggle": next = !current; break;
            }

            if (!next.HasValue)
            {
                Reply(arg, "Usage: bottomlesswater.toggle <steamid64> <on|off|toggle>");
                return;
            }

            SetPlayerState(target.userID, next.Value);
            Reply(arg, $"Set {target.displayName} ({target.UserIDString}) to {(next.Value ? "ENABLED" : "DISABLED")}");

            var actor = arg.Player();
            LogToggle(actor?.displayName ?? "Console", actor?.UserIDString ?? "Console",
                      next.Value, target.displayName, target.UserIDString, isAdmin: true);
        }

        /// <summary>
        /// Console command: bottomlesswater.status [steamid64]
        /// Reports the infinite-water state for one player, or all known players if
        /// no argument is provided. Requires admin access.
        /// </summary>
        [ConsoleCommand("bottomlesswater.status")]
        private void CmdStatusConsole(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg)) return;

            if (arg.Args == null || arg.Args.Length == 0)
            {
                foreach (var entry in _playerStates)
                {
                    var p    = BasePlayer.FindAwakeOrSleeping(entry.Key);
                    var name = p != null ? p.displayName : entry.Key.ToString();
                    Reply(arg, $"{name} ({entry.Key}): {(entry.Value.Enabled ? "ENABLED" : "DISABLED")}");
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

        /// <summary>
        /// Console command: bottomlesswater.reload
        /// Hot-reloads plugin configuration from disk and restarts the tick timer.
        /// Requires admin access.
        /// </summary>
        [ConsoleCommand("bottomlesswater.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAccess(arg)) return;
            LoadConfig();
            _tickTimer?.Destroy();
            _tickTimer = timer.Every(_config.TickSeconds, DoTick);
            Reply(arg, "BottomlessWater configuration reloaded.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Command helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the player has performed too many mutating actions within the
        /// last 60 seconds (sliding window rate limit). Sends MsgRateLimited and returns
        /// true without adding an entry if the limit is already reached.
        ///
        /// The player reference is passed directly to avoid a redundant BasePlayer.FindByID
        /// lookup that earlier implementations performed inside this method.
        /// </summary>
        private bool IsRateLimited(BasePlayer player)
        {
            var userId = player.userID;
            List<float> window;
            if (!_rateLimitWindows.TryGetValue(userId, out window))
            {
                window = new List<float>();
                _rateLimitWindows[userId] = window;
            }

            var now = Time.realtimeSinceStartup;

            for (var i = window.Count - 1; i >= 0; i--)
                if (now - window[i] > 60f)
                    window.RemoveAt(i);

            if (window.Count >= _config.RateLimitMaxPerMinute)
            {
                SendReply(player, Msg(MsgRateLimited, player.UserIDString));
                return true;
            }

            window.Add(now);
            return false;
        }

        /// <summary>
        /// Returns true if the player last performed a mutating action less than
        /// ChatCooldownSeconds ago.
        /// </summary>
        private bool IsOnToggleCooldown(ulong userId)
        {
            float lastToggleTime;
            if (!_toggleCooldowns.TryGetValue(userId, out lastToggleTime))
                return false;
            return Time.realtimeSinceStartup - lastToggleTime < _config.ChatCooldownSeconds;
        }

        /// <summary>
        /// Returns true if the console argument represents an authorised admin call.
        /// Server-console connections (null Connection) are always trusted.
        /// In-game callers must be flagged as server admin OR hold PermAdmin.
        /// On denial, sends MsgAdminOnly and returns false.
        /// </summary>
        private bool HasAdminAccess(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.Connection == null) return true;

            var player = arg.Player();
            if (player != null && player.IsAdmin) return true;
            if (player != null && permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;

            // Pass string.Empty (not null) so lang.GetMessage uses the server default
            // language rather than an implicit null fallback.
            Reply(arg, Msg(MsgAdminOnly, player?.UserIDString ?? string.Empty));
            return false;
        }

        /// <summary>
        /// Attempts to locate a connected or sleeping player by their SteamID64 string.
        /// Returns false if input is null/whitespace, not parseable as a ulong, or no
        /// matching player is found in the awake/sleeping lists.
        /// </summary>
        private static bool TryFindPlayerBySteamId(string input, out BasePlayer player)
        {
            player = null;
            ulong userId;
            if (string.IsNullOrWhiteSpace(input) || !ulong.TryParse(input, out userId))
                return false;
            player = BasePlayer.FindAwakeOrSleeping(userId);
            return player != null;
        }

        /// <summary>
        /// Sets the stored infinite-water state for userId via its PlayerState instance,
        /// creating one if it does not yet exist, and schedules a debounced save.
        /// </summary>
        private void SetPlayerState(ulong userId, bool enabled)
        {
            PlayerState state;
            if (_playerStates.TryGetValue(userId, out state))
                state.Enabled = enabled;
            else
                _playerStates[userId] = new PlayerState(enabled);

            MarkDirty();
        }

        /// <summary>
        /// Sends text to the appropriate destination: SendReply for in-game console
        /// connections, or Puts for the server console.
        /// </summary>
        private void Reply(ConsoleSystem.Arg arg, string text)
        {
            if (arg?.Connection != null)
                SendReply(arg, text);
            else
                Puts(text);
        }

        /// <summary>
        /// Writes a structured toggle event to the server console and the plugin's log file.
        ///
        /// Format: [ADMIN|PLAYER] actorName ENABLED|DISABLED BottomlessWater for target at utc
        /// </summary>
        private void LogToggle(string actorName, string actorId, bool state,
                               string targetName = null, string targetId = null, bool isAdmin = false)
        {
            var targetDesc = targetName != null
                ? $"{targetName} ({targetId})"
                : $"{actorName} ({actorId})";
            var log = $"[{(isAdmin ? "ADMIN" : "PLAYER")}] {actorName} {(state ? "ENABLED" : "DISABLED")} BottomlessWater for {targetDesc} at {DateTime.UtcNow:u}";
            Puts(log);
            LogToFile(LogFile, log, this);
        }
    }
}
