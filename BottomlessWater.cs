// File: BottomlessWater.cs
// uMod / Oxide plugin for Rust
// Bottomless water containers with permissions, admin controls, logging, and config reload.
// Author: Gabriel Dungan, Github:Gjdunga
// Version: 2.2.0
// License: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Bottomless Water", "gjdunga", "1.0.0")]
    [Description("Keeps supported water containers full using LiquidContainer; per-player toggle with permissions and admin controls.")]
    public class BottomlessWater : CovalencePlugin
    {
        // --- Configuration ---------------------------------------------------
        private PluginConfig ConfigData;
        private const string DataFileName = "BottomlessWaterData";
        private const string LogFileName = "BottomlessWater";

        private class PluginConfig
        {
            public float TickSeconds = 1.0f;
            public int MaxAddPerTick = 5000;
            public bool AffectLiquidContainers = true;
            public bool AutoGrantUseToDefaultGroup = true;
            public bool EnableByDefault = true;
            public bool RequireAdminForRcon = false;
            public List<string> WhitelistShortPrefabNames = new List<string>
            {
                "water_catcher_small","water_catcher_large","waterbarrel","water_barrel","large_water_catcher"
            };
            public List<string> ExcludeShortPrefabNames = new List<string>();
        }

        protected override void LoadDefaultConfig()
        {
            ConfigData = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { ConfigData = Config.ReadObject<PluginConfig>() ?? new PluginConfig(); }
            catch { PrintWarning("Invalid config, regenerating."); LoadDefaultConfig(); }
            SanitizeConfig();
            SaveConfig();
        }

        private void SanitizeConfig()
        {
            if (ConfigData.TickSeconds < 0.25f) ConfigData.TickSeconds = 0.25f;
            ConfigData.MaxAddPerTick = Mathf.Clamp(ConfigData.MaxAddPerTick, 1, 100000);
            ConfigData.WhitelistShortPrefabNames ??= new List<string>();
            ConfigData.ExcludeShortPrefabNames ??= new List<string>();
        }

        // --- Permissions and Data -------------------------------------------
        private const string PermUse = "bottomlesswater.use";
        private const string PermAdmin = "bottomlesswater.admin";

        private class PluginData
        {
            public HashSet<ulong> Enabled = new();
            public HashSet<ulong> Disabled = new();
        }
        private PluginData _data;

        private void LoadData() =>
            _data = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(DataFileName) ?? new PluginData();
        private void SaveData() =>
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);

        // --- State -----------------------------------------------------------
        private readonly Dictionary<BaseEntity, Timer> _entityTimers = new();
        private readonly Dictionary<BaseEntity, double> _lastUpdate = new();
        private Type _liquidContainerType;
        private const string LiquidContainerTypeName = "LiquidContainer";

        // --- Initialization --------------------------------------------------
        private void OnServerInitialized()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            EnsureDefaultGroupBinding();

            foreach (var net in BaseNetworkable.serverEntities)
                TryStartManaging(net as BaseEntity);
        }

        private void EnsureDefaultGroupBinding()
        {
            if (!ConfigData.AutoGrantUseToDefaultGroup) return;
            if (!permission.GroupExists("default"))
                permission.CreateGroup("default", "Default", 0);
            if (!permission.GroupHasPermission("default", PermUse))
            {
                permission.GrantGroupPermission("default", PermUse, this);
                Puts("Granted bottomlesswater.use to group 'default'.");
            }
        }

        private void OnServerSave() => SaveData();
        private void Unload()
        {
            foreach (var t in _entityTimers.Values) t.Destroy();
            _entityTimers.Clear();
            _lastUpdate.Clear();
            SaveData();
        }

        // --- Chat Commands ---------------------------------------------------
        [ChatCommand("bw")]
        private void CmdBw(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;
            var iplayer = covalence.Players.FindPlayerById(player.userID.ToString());
            if (iplayer == null || !iplayer.HasPermission(PermUse))
            {
                player.ChatMessage("<color=#ff6666>You lack permission.</color>");
                return;
            }

            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
            bool newState;
            switch (sub)
            {
                case "on": case "enable": newState = true; break;
                case "off": case "disable": newState = false; break;
                case "toggle": default: newState = !IsEnabled(iplayer); break;
            }

            SetPlayerEnabled(iplayer, newState);
            player.ChatMessage($"<color=#77c6ff>[BottomlessWater]</color> {(newState ? "Enabled" : "Disabled")} for you.");
            LogAction(iplayer.Name, iplayer.Id, newState, false);
            RefreshPlayerEntities(player.userID);
        }

        // --- Console/RCON Commands ------------------------------------------
        [ConsoleCommand("bottomlesswater.toggle")]
        private void CcmdToggle(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAuth(arg)) return;
            if (arg.Args == null || arg.Args.Length < 2)
            {
                arg.ReplyWith("Usage: bottomlesswater.toggle <steamID|name> <on|off|toggle>");
                return;
            }

            var target = FindIPlayer(arg.Args[0]);
            if (target == null) { arg.ReplyWith("Player not found."); return; }
            string op = arg.Connection?.username ?? "CONSOLE";

            bool? set = arg.Args[1].ToLowerInvariant() switch
            {
                "on" or "enable" => true,
                "off" or "disable" => false,
                _ => null
            };
            bool newState = set ?? !IsEnabled(target);
            SetPlayerEnabled(target, newState);
            arg.ReplyWith($"BottomlessWater: {target.Name} -> {(newState ? "ON" : "OFF")}");
            LogAction(op, target.Id, newState, true);

            if (ulong.TryParse(target.Id, out var uid))
                RefreshPlayerEntities(uid);
        }

        [ConsoleCommand("bottomlesswater.status")]
        private void CcmdStatus(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAuth(arg)) return;

            if (arg.Args != null && arg.Args.Length >= 1)
            {
                var target = FindIPlayer(arg.Args[0]);
                if (target == null) { arg.ReplyWith("Player not found."); return; }
                arg.ReplyWith($"{target.Name} ({target.Id}) -> {(IsEnabled(target) ? "ON" : "OFF")}");
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("BottomlessWater player states:");
                foreach (var id in _data.Enabled) sb.AppendLine($"  {id} -> ON");
                foreach (var id in _data.Disabled) sb.AppendLine($"  {id} -> OFF");
                arg.ReplyWith(sb.ToString());
            }
        }

        [ConsoleCommand("bottomlesswater.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            if (!HasAdminAuth(arg)) return;
            LoadConfig(); EnsureDefaultGroupBinding();
            foreach (var t in _entityTimers.Values) t.Destroy();
            _entityTimers.Clear(); _lastUpdate.Clear();
            foreach (var net in BaseNetworkable.serverEntities)
                TryStartManaging(net as BaseEntity);
            arg.ReplyWith("BottomlessWater: Config reloaded.");
        }

        // --- Auth Helpers ----------------------------------------------------
        private bool HasAdminAuth(ConsoleSystem.Arg arg)
        {
            var pl = arg.Connection?.player as BasePlayer;
            if (pl == null) return !ConfigData.RequireAdminForRcon; // server console / RCON
            var ipl = covalence.Players.FindPlayerById(pl.userID.ToString());
            return ipl != null && ipl.HasPermission(PermAdmin);
        }

        private IPlayer FindIPlayer(string idOrName)
        {
            if (ulong.TryParse(idOrName, out var uid))
                return covalence.Players.FindPlayerById(uid.ToString());
            var all = covalence.Players.FindPlayers(idOrName).ToList();
            if (all.Count == 0) return null;
            var exact = all.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            return exact ?? all.FirstOrDefault(p => p.IsConnected) ?? all[0];
        }

        // --- Enable / Disable Logic -----------------------------------------
        private void SetPlayerEnabled(IPlayer p, bool state)
        {
            var id = ulong.Parse(p.Id);
            _data.Enabled.Remove(id);
            _data.Disabled.Remove(id);
            if (state) _data.Enabled.Add(id); else _data.Disabled.Add(id);
            SaveData();
        }

        private bool IsEnabled(IPlayer p)
        {
            if (p == null || !p.HasPermission(PermUse)) return false;
            var id = ulong.Parse(p.Id);
            if (_data.Disabled.Contains(id)) return false;
            if (_data.Enabled.Contains(id)) return true;
            return ConfigData.EnableByDefault;
        }

        // --- Logging ---------------------------------------------------------
        private void LogAction(string actor, string targetId, bool enabled, bool adminAction)
        {
            string tag = adminAction ? "[ADMIN]" : "[PLAYER]";
            string message = $"{tag} {actor} {(enabled ? "ENABLED" : "DISABLED")} BottomlessWater for {targetId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            Puts(message);
            LogToFile(LogFileName, message, this);
        }

        // --- Management / Fill Logic ----------------------------------------
        private void TryStartManaging(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return;
            ulong owner = entity.OwnerID; if (owner == 0) return;
            var p = covalence.Players.FindPlayerById(owner.ToString());
            if (p == null || !IsEnabled(p)) return;
            if (!IsWaterRelevant(entity) || _entityTimers.ContainsKey(entity)) return;

            var t = timer.Every(Mathf.Max(0.25f, ConfigData.TickSeconds), () =>
            {
                if (entity == null || entity.IsDestroyed) { StopManaging(entity); return; }
                var ownerPlayer = covalence.Players.FindPlayerById(entity.OwnerID.ToString());
                if (ownerPlayer == null || !IsEnabled(ownerPlayer)) { StopManaging(entity); return; }

                try
                {
                    bool changed = false;
                    if (entity is WaterCatcher wc) changed = KeepCatcherFull(wc);
                    else if (ConfigData.AffectLiquidContainers)
                    {
                        var type = GetLiquidContainerType();
                        var lc = entity.GetComponent(type);
                        if (lc != null) changed = KeepLiquidContainerFull(lc, entity);
                    }
                    if (changed) DebouncedUpdate(entity);
                }
                catch (Exception ex)
                {
                    PrintWarning($"Error updating {entity.ShortPrefabName}: {ex.Message}");
                    StopManaging(entity);
                }
            });
            _entityTimers[entity] = t;
        }

        private void StopManaging(BaseEntity entity)
        {
            if (entity == null) return;
            if (_entityTimers.TryGetValue(entity, out var t)) t.Destroy();
            _entityTimers.Remove(entity);
            _lastUpdate.Remove(entity);
        }

        private void RefreshPlayerEntities(ulong uid)
        {
            foreach (var net in BaseNetworkable.serverEntities)
            {
                if (net is BaseEntity be && be.OwnerID == uid)
                {
                    StopManaging(be);
                    TryStartManaging(be);
                }
            }
        }

        private bool KeepCatcherFull(WaterCatcher e)
        {
            var inv = e.inventory;
            if (inv == null) return false;
            int target = inv.maxStackSize <= 0 ? 20000 : inv.maxStackSize;
            int current = inv.itemList.Count > 0 && inv.itemList[0] != null ? inv.itemList[0].amount : 0;
            if (current < target)
            {
                int delta = Mathf.Clamp(target - current, 1, ConfigData.MaxAddPerTick);
                e.AddResource(delta);
                return true;
            }
            return false;
        }

        private bool KeepLiquidContainerFull(Component lc, BaseEntity e)
        {
            var t = lc.GetType();
            float cur = ReadFloat(lc, t.GetField("amount") ?? (object)t.GetProperty("amount")?.GetGetMethod());
            float cap = ReadFloat(lc, t.GetField("capacity") ?? (object)t.GetProperty("capacity")?.GetGetMethod());
            if (cap <= 0f) cap = ReadFloat(lc, t.GetField("maxAmount") ?? (object)t.GetProperty("maxAmount")?.GetGetMethod());
            if (cap <= 0f) return false;
            if (cur < cap)
            {
                float delta = Mathf.Min(cap - cur, ConfigData.MaxAddPerTick);
                float newAmt = Mathf.Clamp(cur + delta, 0f, cap);
                WriteFloat(lc, "amount", newAmt);
                return true;
            }
            return false;
        }

        private void DebouncedUpdate(BaseEntity e, double interval = 0.5)
        {
            double now = Time.realtimeSinceStartup;
            if (_lastUpdate.TryGetValue(e, out var last) && (now - last) < interval) return;
            _lastUpdate[e] = now;
            e.SendNetworkUpdate();
        }

        private bool IsWaterRelevant(BaseEntity e)
        {
            string spn = e.ShortPrefabName ?? "";
            if (ConfigData.ExcludeShortPrefabNames.Contains(spn)) return false;
            if (e is WaterCatcher) return true;
            if (ConfigData.WhitelistShortPrefabNames.Contains(spn)) return true;
            if (spn.Contains("water", StringComparison.OrdinalIgnoreCase)) return true;
            if (ConfigData.AffectLiquidContainers && GetLiquidContainerType() != null &&
                e.GetComponent(GetLiquidContainerType()) != null) return true;
            return false;
        }

        private Type GetLiquidContainerType()
        {
            if (_liquidContainerType != null) return _liquidContainerType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == LiquidContainerTypeName);
                    if (t != null) { _liquidContainerType = t; break; }
                }
                catch { }
            }
            return _liquidContainerType;
        }

        private static float ReadFloat(object obj, object member)
        {
            if (obj == null || member == null) return 0f;
            try
            {
                if (member is System.Reflection.FieldInfo fi)
                {
                    var v = fi.GetValue(obj);
                    if (v is float f1) return f1;
                    if (v is double d1) return (float)d1;
                    if (v is int i1) return i1;
                }
                else if (member is System.Reflection.MethodInfo mi && mi.GetParameters().Length == 0)
                {
                    var v = mi.Invoke(obj, null);
                    if (v is float f2) return f2;
                    if (v is double d2) return (float)d2;
                    if (v is int i2) return i2;
                }
            }
            catch { }
            return 0f;
        }

        private static void WriteFloat(object obj, string name, float val)
        {
            var t = obj.GetType();
            var prop = t.GetProperty(name);
            if (prop != null && prop.CanWrite)
                try { prop.SetValue(obj, Convert.ChangeType(val, prop.PropertyType)); return; } catch { }
            var field = t.GetField(name);
            if (field != null)
                try { field.SetValue(obj, Convert.ChangeType(val, field.FieldType)); } catch { }
        }
    }
}
