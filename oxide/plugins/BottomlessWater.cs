// BottomlessWater - Oxide/uMod plugin for Rust (Facepunch)
// Provides infinite-water behavior for owned liquid containers.
// Repository: https://github.com/gjdunga/bottomlesswater
// License: MIT

using System;
using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "Gabriel", "3.1.0")]
    [Description("Infinite water behavior for owned liquid containers with security hardening, verbose logging, and performance improvements.")]
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
        // Fields
        // ─────────────────────────────────────────────────────────────────────

        private PluginConfig _config;
        private StoredData   _storedData;

        /// <summary>
        /// Per-player infinite-water on/off state.
        /// Key: SteamID64. Value: true = enabled, false = disabled.
        /// Source of truth is this dictionary; <see cref="_storedData"/> is the serialised mirror.
        /// </summary>
        private readonly Dictionary<ulong, bool> _playerStates = new Dictionary<ulong, bool>();

        /// <summary>
        /// Tracks the realtime timestamp of the most recent accepted toggle per player.
        /// Used to enforce <see cref="PluginConfig.ChatCooldownSeconds"/> between successive
        /// on/off/toggle actions.
        /// </summary>
        private readonly Dictionary<ulong, float> _toggleCooldowns = new Dictionary<ulong, float>();

        /// <summary>
        /// Sliding 60-second window of accepted command timestamps per player.
        /// Used to enforce <see cref="PluginConfig.RateLimitMaxPerMinute"/>.
        /// </summary>
        private readonly Dictionary<ulong, List<float>> _rateLimitWindows = new Dictionary<ulong, List<float>>();

        /// <summary>All tracked LiquidContainer entities currently on the server.</summary>
        private readonly HashSet<LiquidContainer> _liquidContainers = new HashSet<LiquidContainer>();

        /// <summary>
        /// Snapshot buffer filled at the start of each tick to avoid mutating
        /// <see cref="_liquidContainers"/> while iterating.
        /// </summary>
        private readonly List<LiquidContainer> _tickBuffer = new List<LiquidContainer>();

        /// <summary>
        /// Per-tick cache of ownerIds whose <see cref="PermUse"/> permission has already
        /// been verified this tick. Avoids repeated string allocations and hash lookups
        /// when multiple containers share the same owner.
        /// </summary>
        private readonly HashSet<ulong> _tickPermittedOwners = new HashSet<ulong>();

        /// <summary>Resolved short-prefab whitelist from config. Empty means "all allowed".</summary>
        private HashSet<string> _whitelistShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolved short-prefab exclusion list from config.</summary>
        private HashSet<string> _excludeShortPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Timer _tickTimer;
        private Timer _saveTimer;
        private bool  _dirty;

        /// <summary>Cached ItemDefinition for "water"; resolved once in <see cref="Init"/>.</summary>
        private ItemDefinition _waterDefinition;

        // ─────────────────────────────────────────────────────────────────────
        // Configuration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Serialised configuration for BottomlessWater.</summary>
        private class PluginConfig
        {
            /// <summary>
            /// Interval in seconds between fill ticks.
            /// Minimum clamped to 0.25 s to prevent frame-rate-level spam.
            /// </summary>
            public float TickSeconds = 1f;

            /// <summary>
            /// Maximum water units added to a single item stack per tick.
            /// Clamped to >= 1.
            /// </summary>
            public int MaxAddPerTick = 1000;

            /// <summary>Whether to process LiquidContainer entities at all.</summary>
            public bool AffectLiquidContainers = true;

            /// <summary>
            /// Default on/off state written the first time an unknown ownerId is encountered
            /// during a tick. Requires <see cref="AutoGrantUseToDefaultGroup"/> to be useful
            /// server-wide.
            /// </summary>
            public bool EnableByDefault = true;

            /// <summary>
            /// If true, grants <see cref="PermUse"/> to Oxide's "default" group on load,
            /// so all authenticated players inherit it immediately.
            /// </summary>
            public bool AutoGrantUseToDefaultGroup = true;

            /// <summary>
            /// Optional whitelist. If non-empty, ONLY containers whose ShortPrefabName appears
            /// here are processed. Comparisons are case-insensitive.
            /// </summary>
            public List<string> WhiteListShortPrefabNames = new List<string>();

            /// <summary>
            /// Optional exclusion list. Containers whose ShortPrefabName appears here are
            /// skipped. Ignored when <see cref="WhiteListShortPrefabNames"/> is non-empty.
            /// </summary>
            public List<string> ExcludeShortPrefabNames = new List<string>();

            /// <summary>
            /// Minimum seconds a player must wait between successive on/off/toggle actions.
            /// Clamped to >= 0.
            /// </summary>
            public float ChatCooldownSeconds = 2f;

            /// <summary>
            /// Maximum on/off/toggle actions a player may perform within any 60-second window.
            /// Clamped to >= 1.
            /// </summary>
            public int RateLimitMaxPerMinute = 5;

            /// <summary>
            /// If true, create a new water item in completely empty containers owned by
            /// eligible players (subject to <see cref="MaxAddPerTick"/> and the item's
            /// stackable cap).
            /// </summary>
            public bool FillEmptyContainers = false;

            /// <summary>
            /// If true, wipe all stored player states when the server detects a map wipe
            /// via <c>OnNewSave</c>.
            /// </summary>
            public bool ClearDataOnWipe = false;

            /// <summary>
            /// Debounce delay in seconds before a pending dirty-state write is flushed.
            /// Clamped to >= 0.1 s.
            /// </summary>
            public float SaveDebounceSeconds = 2f;
        }

        /// <summary>On-disk data model; player states keyed by SteamID64 string for JSON compatibility.</summary>
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
                {
                    throw new Exception("Config was null after deserialization.");
                }
            }
            catch
            {
                PrintWarning("Invalid configuration detected; regenerating defaults.");
                LoadDefaultConfig();
            }

            // Clamp all config values to valid ranges.
            _config.TickSeconds            = Mathf.Max(0.25f, _config.TickSeconds);
            _config.MaxAddPerTick          = Math.Max(1, _config.MaxAddPerTick);
            _config.ChatCooldownSeconds    = Mathf.Max(0f, _config.ChatCooldownSeconds);
            _config.RateLimitMaxPerMinute  = Math.Max(1, _config.RateLimitMaxPerMinute);
            _config.SaveDebounceSeconds    = Mathf.Max(0.1f, _config.SaveDebounceSeconds);

            SaveConfig();
            RebuildPrefabSets();
        }

        /// <inheritdoc/>
        protected override void SaveConfig() => Config.WriteObject(_config, true);

        /// <summary>
        /// Rebuilds the in-memory HashSets for whitelist/exclusion from the current config lists.
        /// Called after every config load or hot-reload.
        /// </summary>
        private void RebuildPrefabSets()
        {
            _whitelistShortPrefabNames = BuildPrefabSet(_config.WhiteListShortPrefabNames);
            _excludeShortPrefabNames   = BuildPrefabSet(_config.ExcludeShortPrefabNames);
        }

        /// <summary>
        /// Converts a <see cref="List{T}"/> of raw prefab name strings into a trimmed,
        /// case-insensitive <see cref="HashSet{T}"/>, ignoring null/whitespace entries.
        /// </summary>
        private static HashSet<string> BuildPrefabSet(List<string> values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null) return set;
            foreach (var entry in values)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    set.Add(entry.Trim());
            }
            return set;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Localization
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
        /// Returns the localised message for <paramref name="key"/>, optionally formatted
        /// with <paramref name="args"/>.
        /// </summary>
        /// <param name="key">Message key constant (e.g. <see cref="MsgNoPermission"/>).</param>
        /// <param name="playerId">
        /// SteamID64 string used to select the player's preferred language.
        /// Pass <c>null</c> or empty string to fall back to the server default language.
        /// </param>
        /// <param name="args">Optional format arguments passed to <see cref="string.Format"/>.</param>
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
        /// Clears all stored player states if <see cref="PluginConfig.ClearDataOnWipe"/> is true.
        /// </summary>
        private void OnNewSave(string filename)
        {
            if (!_config.ClearDataOnWipe) return;
            _playerStates.Clear();
            MarkDirty();
            SaveDataImmediate();
            Puts($"Cleared {nameof(BottomlessWater)} player state due to wipe ({filename}).");
        }

        /// <summary>
        /// Oxide hook: fires when a player disconnects.
        /// Removes ephemeral per-player rate-limit and cooldown entries to prevent
        /// unbounded dictionary growth.
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
        /// <see cref="_playerStates"/>. Silently resets to empty on any read/parse error.
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
                    _playerStates[userId] = entry.Value;
            }
        }

        /// <summary>
        /// Writes <see cref="_playerStates"/> to disk immediately.
        /// No-ops if the dirty flag is not set, preventing unnecessary I/O.
        /// Should be called directly only from <see cref="Unload"/> and <see cref="OnServerSave"/>;
        /// all other callers should use <see cref="MarkDirty"/> to debounce writes.
        /// </summary>
        private void SaveDataImmediate()
        {
            // FIX: original condition was `if (!_dirty && _storedData != null) return;`
            // which would proceed with a write when _dirty=false and _storedData=null.
            // Correct guard: skip write unconditionally when not dirty.
            if (!_dirty) return;

            _storedData = new StoredData();
            foreach (var entry in _playerStates)
                _storedData.PlayerStates[entry.Key.ToString()] = entry.Value;

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _storedData);
            _dirty     = false;
            _saveTimer = null;
        }

        /// <summary>
        /// Marks the in-memory state as modified and schedules a debounced disk write
        /// after <see cref="PluginConfig.SaveDebounceSeconds"/> to coalesce rapid changes.
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
        /// Scans all server entities and populates <see cref="_liquidContainers"/>.
        /// Called once during <see cref="OnServerInitialized"/>; subsequent additions
        /// and removals are tracked via <see cref="OnEntitySpawned"/> and
        /// <see cref="OnEntityKill"/>.
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

        /// <summary>
        /// Oxide hook: fires when any LiquidContainer entity spawns.
        /// Adds it to the tracked set so it is eligible for filling.
        /// </summary>
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
        /// checks ownership and permission, and calls <see cref="FillLiquidItems"/> for
        /// eligible containers.
        ///
        /// Permission model enforced here (defence-in-depth beyond stored state):
        ///   1. Container must not be destroyed.
        ///   2. ShortPrefabName must pass the whitelist/exclusion filter.
        ///   3. OwnerID must be non-zero.
        ///   4. Owner must hold <see cref="PermUse"/> (checked once per unique owner per tick).
        ///   5. Owner's stored state must be enabled (explicit opt-in/opt-out).
        ///
        /// Using <see cref="_tickPermittedOwners"/> to cache permission results avoids
        /// O(n * containers_per_owner) string allocations when one player owns many containers.
        /// </summary>
        private void DoTick()
        {
            if (!_config.AffectLiquidContainers || _liquidContainers.Count == 0)
                return;

            // Snapshot to avoid modifying the HashSet during iteration.
            _tickBuffer.Clear();
            foreach (var liquid in _liquidContainers)
                _tickBuffer.Add(liquid);

            // Clear the per-tick permission cache.
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

                // SECURITY FIX: always verify the owner still holds PermUse before filling.
                // Without this check, revoking a player's permission has no effect until
                // they explicitly disable via /bw off.
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
        /// Returns true if a container with the given <paramref name="shortPrefabName"/>
        /// should be processed this tick.
        ///
        /// Logic:
        ///   - If the whitelist is non-empty, the name must be present in it.
        ///   - Otherwise, the name must NOT be in the exclusion list.
        /// </summary>
        private bool ShouldProcessPrefab(string shortPrefabName)
        {
            if (_whitelistShortPrefabNames.Count > 0)
                return _whitelistShortPrefabNames.Contains(shortPrefabName);
            return !_excludeShortPrefabNames.Contains(shortPrefabName);
        }

        /// <summary>
        /// Returns the stored infinite-water state for <paramref name="userId"/>.
        /// If the player has no stored state, writes <see cref="PluginConfig.EnableByDefault"/>
        /// as the initial value and schedules a save via <see cref="MarkDirty"/>.
        ///
        /// NOTE: This method has a write side-effect on first access per player.
        /// This is intentional (lazy initialisation of default state) but callers in the
        /// tick path should guard against calling this for non-permitted owners to avoid
        /// populating the dictionary with users who will never use the plugin.
        /// The tick already performs the permission check before reaching this call.
        /// </summary>
        private bool IsEnabledFor(ulong userId)
        {
            if (_playerStates.TryGetValue(userId, out var enabled))
                return enabled;

            // First encounter: seed the default state.
            _playerStates[userId] = _config.EnableByDefault;
            MarkDirty();
            return _config.EnableByDefault;
        }

        /// <summary>
        /// Fills all water-type items in <paramref name="liquid"/>'s inventory up to their
        /// stack cap, adding at most <see cref="PluginConfig.MaxAddPerTick"/> units per item.
        ///
        /// If <see cref="PluginConfig.FillEmptyContainers"/> is true and the inventory is
        /// completely empty, attempts to create a new water item and move it into the container.
        ///
        /// A network update is sent only when at least one item actually changed, minimising
        /// unnecessary bandwidth.
        /// </summary>
        private void FillLiquidItems(LiquidContainer liquid)
        {
            var inventory = liquid.inventory;
            if (inventory == null) return;

            // Optionally seed a new water item into a completely empty container.
            if (_config.FillEmptyContainers && inventory.itemList.Count == 0 && _waterDefinition != null)
            {
                // EDGE-CASE FIX: guard stackable > 0 to avoid creating a zero-amount item,
                // which produces a broken/invisible item in Rust's inventory system.
                var stackable = _waterDefinition.stackable;
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
        /// Chat command <c>/bw</c>: lets a permitted player enable, disable, toggle, or
        /// query their infinite-water state.
        ///
        /// Subcommands:
        ///   on     - enable infinite water for the caller.
        ///   off    - disable infinite water for the caller.
        ///   toggle - flip the caller's current state.
        ///   status - report the caller's current state without changing it.
        ///
        /// Rate-limiting and cooldown are applied to mutating actions (on/off/toggle).
        /// The cooldown timestamp is only recorded after BOTH guards pass to prevent a
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

            // Defensive: cap argument length before any further string work.
            var rawAction = args[0].Length > MaxArgLength ? args[0].Substring(0, MaxArgLength) : args[0];
            var action    = rawAction.ToLowerInvariant();

            var isMutating = action == "on" || action == "off" || action == "toggle";

            if (isMutating)
            {
                // Rate-limit check must precede the cooldown check so a rate-limited player
                // does not have their cooldown clock advanced.
                if (IsRateLimited(player.userID, player.UserIDString)) return;

                if (IsOnToggleCooldown(player.userID))
                {
                    SendReply(player, Msg(MsgCooldown, player.UserIDString));
                    return;
                }

                // Record the accepted toggle timestamp AFTER both guards pass.
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
        /// Console command <c>bottomlesswater.toggle &lt;steamid64&gt; &lt;on|off|toggle&gt;</c>:
        /// admin command to override the infinite-water state for another player.
        /// Accepts server console (no connection) or an in-game admin with <see cref="PermAdmin"/>.
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
        /// Console command <c>bottomlesswater.status [steamid64]</c>:
        /// reports the infinite-water state for a single player, or all known players
        /// if no argument is provided.
        /// Requires admin access.
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

        /// <summary>
        /// Console command <c>bottomlesswater.reload</c>:
        /// hot-reloads the plugin configuration from disk and restarts the tick timer.
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
        /// last 60 seconds (sliding window rate limit). Sends the <see cref="MsgRateLimited"/>
        /// message to the player and returns true without adding an entry if the limit is
        /// already reached.
        /// </summary>
        private bool IsRateLimited(ulong userId, string userIdString)
        {
            if (!_rateLimitWindows.TryGetValue(userId, out var window))
            {
                window = new List<float>();
                _rateLimitWindows[userId] = window;
            }

            var now = Time.realtimeSinceStartup;

            // Expire entries older than 60 seconds (iterate backwards to allow safe RemoveAt).
            for (var i = window.Count - 1; i >= 0; i--)
            {
                if (now - window[i] > 60f)
                    window.RemoveAt(i);
            }

            if (window.Count >= _config.RateLimitMaxPerMinute)
            {
                var player = BasePlayer.FindByID(userId);
                if (player != null)
                    SendReply(player, Msg(MsgRateLimited, userIdString));
                return true;
            }

            window.Add(now);
            return false;
        }

        /// <summary>
        /// Returns true if the player last performed a mutating action less than
        /// <see cref="PluginConfig.ChatCooldownSeconds"/> ago.
        /// </summary>
        private bool IsOnToggleCooldown(ulong userId)
        {
            if (!_toggleCooldowns.TryGetValue(userId, out var lastToggleTime))
                return false;
            return Time.realtimeSinceStartup - lastToggleTime < _config.ChatCooldownSeconds;
        }

        /// <summary>
        /// Returns true if the console argument represents an authorised admin call.
        /// Server-console connections (null Connection) are always trusted.
        /// In-game callers must be flagged as server admin OR hold <see cref="PermAdmin"/>.
        /// On denial, sends <see cref="MsgAdminOnly"/> and returns false.
        /// </summary>
        private bool HasAdminAccess(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.Connection == null) return true;  // server console

            var player = arg.Player();
            if (player != null && player.IsAdmin) return true;
            if (player != null && permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;

            // Use empty string fallback so lang.GetMessage returns server default language
            // instead of potentially null playerId, which is safe but implicit.
            Reply(arg, Msg(MsgAdminOnly, player?.UserIDString ?? string.Empty));
            return false;
        }

        /// <summary>
        /// Attempts to locate a connected or sleeping player by their SteamID64 string.
        /// Returns false if <paramref name="input"/> is null/whitespace, not parseable as
        /// a ulong, or no matching player is found in the awake/sleeping lists.
        /// </summary>
        private static bool TryFindPlayerBySteamId(string input, out BasePlayer player)
        {
            player = null;
            if (string.IsNullOrWhiteSpace(input) || !ulong.TryParse(input, out var userId))
                return false;
            player = BasePlayer.FindAwakeOrSleeping(userId);
            return player != null;
        }

        /// <summary>
        /// Sets the stored infinite-water state for <paramref name="userId"/> and
        /// schedules a debounced save via <see cref="MarkDirty"/>.
        /// </summary>
        private void SetPlayerState(ulong userId, bool enabled)
        {
            _playerStates[userId] = enabled;
            MarkDirty();
        }

        /// <summary>
        /// Sends <paramref name="text"/> to the appropriate destination: uses
        /// <see cref="SendReply(ConsoleSystem.Arg, string)"/> for in-game console connections,
        /// or <see cref="Puts"/> for the server console.
        /// </summary>
        private void Reply(ConsoleSystem.Arg arg, string text)
        {
            if (arg?.Connection != null)
                SendReply(arg, text);
            else
                Puts(text);
        }

        /// <summary>
        /// Writes a structured toggle event to both the server console and the plugin's
        /// dedicated log file.
        ///
        /// Format: <c>[ADMIN|PLAYER] {actorName} ENABLED|DISABLED BottomlessWater for {target} at {utc}</c>
        /// </summary>
        /// <param name="actorName">Display name of the player or console performing the action.</param>
        /// <param name="actorId">SteamID64 string of the actor (use "Console" for server console).</param>
        /// <param name="state">The new state being applied (true = enabled).</param>
        /// <param name="targetName">Display name of the target player, or null if the actor is the target.</param>
        /// <param name="targetId">SteamID64 string of the target player, or null if the actor is the target.</param>
        /// <param name="isAdmin">True when the action was performed via an admin console command.</param>
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
