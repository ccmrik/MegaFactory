using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MegaFactory
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaFactoryPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.rik.megafactory";
        public const string PluginName = "Mega Factory";
        public const string PluginVersion = "1.4.2";

        internal static ManualLogSource Log;
        internal static MegaFactoryPlugin Instance;
        private static Harmony _harmony;
        private static ConfigFile _config;
        private static FileSystemWatcher _configWatcher;

        // ── General ──
        public static ConfigEntry<float> SearchRadius;
        public static ConfigEntry<float> ProcessInterval;
        public static ConfigEntry<bool> UseChests;
        public static ConfigEntry<bool> UseReinforcedChests;
        public static ConfigEntry<bool> UseBlackMetalChests;
        public static ConfigEntry<bool> UseBarrels;
        public static ConfigEntry<bool> ShowProductionMessage;
        public static ConfigEntry<float> BackgroundCatchupHours;

        // ── Charcoal Kiln ──
        public static ConfigEntry<bool> EnableKiln;

        // ── Smelter ──
        public static ConfigEntry<bool> EnableSmelter;

        // ── Blast Furnace ──
        public static ConfigEntry<bool> EnableBlastFurnace;

        // ── Windmill ──
        public static ConfigEntry<bool> EnableWindmill;

        // ── Spinning Wheel ──
        public static ConfigEntry<bool> EnableSpinningWheel;

        // ── Eitr Refinery ──
        public static ConfigEntry<bool> EnableEitrRefinery;

        // ── Work Order GUI ──
        public static ConfigEntry<KeyCode> WorkOrderKey;

        // ── Diagnostics ──
        public static ConfigEntry<KeyCode> DiagnosticsHotkey;

        // ── Debug ──
        public static ConfigEntry<bool> DebugMode;

        // Timers
        private static float _processTimer;

        /// <summary>Gated diagnostic log. Silent unless DebugMode = true.</summary>
        public static void DebugLog(string msg)
        {
            if (DebugMode?.Value == true) Log?.LogInfo(msg);
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            MigrateConfig(Config.ConfigFilePath);
            Config.Reload();

            // 1. General
            SearchRadius = Config.Bind("1. General", "SearchRadius", 15f,
                new ConfigDescription("Radius to search for containers (meters)", new AcceptableValueRange<float>(1f, 100f)));
            ProcessInterval = Config.Bind("1. General", "ProcessInterval", 3f,
                new ConfigDescription("How often stations process items (seconds)", new AcceptableValueRange<float>(0.5f, 30f)));
            UseChests = Config.Bind("1. General", "UseChests", true, "Pull from regular Chests");
            UseReinforcedChests = Config.Bind("1. General", "UseReinforcedChests", true, "Pull from Reinforced Chests");
            UseBlackMetalChests = Config.Bind("1. General", "UseBlackMetalChests", true, "Pull from Black Metal Chests");
            UseBarrels = Config.Bind("1. General", "UseBarrels", true, "Pull from Barrels");
            ShowProductionMessage = Config.Bind("1. General", "ShowProductionMessage", true,
                "Pop a top-left message (with icon) when a managed station produces an item");
            BackgroundCatchupHours = Config.Bind("1. General", "BackgroundCatchupHours", 24f,
                new ConfigDescription(
                    "Vanilla caps offline catch-up at 1 hour per station — anything beyond that is silently dropped. " +
                    "This raises the cap (in hours) so factories actually pay out the time you spent away. Min 1h, max 168h (1 week).",
                    new AcceptableValueRange<float>(1f, 168f)));

            // 2. Charcoal Kiln
            EnableKiln = Config.Bind("2. Charcoal Kiln", "Enable", true,
                "Auto-feed Charcoal Kilns with Wood from nearby containers");

            // 3. Smelter
            EnableSmelter = Config.Bind("3. Smelter", "Enable", true,
                "Auto-feed Smelters with ore and coal from nearby containers");

            // 4. Blast Furnace
            EnableBlastFurnace = Config.Bind("4. Blast Furnace", "Enable", true,
                "Auto-feed Blast Furnaces with ore and coal from nearby containers");

            // 5. Windmill
            EnableWindmill = Config.Bind("5. Windmill", "Enable", true,
                "Auto-feed Windmills with barley from nearby containers");

            // 6. Spinning Wheel
            EnableSpinningWheel = Config.Bind("6. Spinning Wheel", "Enable", true,
                "Auto-feed Spinning Wheels with flax from nearby containers");

            // 7. Eitr Refinery
            EnableEitrRefinery = Config.Bind("7. Eitr Refinery", "Enable", true,
                "Auto-feed Eitr Refineries with sap and soft tissue from nearby containers");

            // 8. Work Order GUI
            WorkOrderKey = Config.Bind("8. Work Orders", "InteractKey", KeyCode.LeftShift,
                "Hold this key + interact (E) with a station to open the Work Order panel");

            // 9. Diagnostics (for troubleshooting production issues)
            DiagnosticsHotkey = Config.Bind("9. Diagnostics", "ToggleHotkey", KeyCode.F8,
                "Toggle the on-screen diagnostic overlay AND dump the nearest station's state to the log.");

            // 99. Debug — standardised section name across all Mega mods (v1.3.0+)
            DebugMode = Config.Bind("99. Debug", "DebugMode", false,
                "Enable verbose debug logging to BepInEx console/log");

            _config = Config;
            SetupConfigWatcher();

            _harmony = new Harmony(PluginGUID);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                DebugLog($"Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony PatchAll FAILED: {ex.Message}");
                Log.LogError(ex.ToString());
            }

            // Create the diagnostics HUD singleton eagerly so F8 works immediately.
            _ = DiagnosticsHud.Instance;

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded! Press {DiagnosticsHotkey.Value} near a station for diagnostics HUD.");
        }

        private void SetupConfigWatcher()
        {
            string configPath = Path.GetDirectoryName(Config.ConfigFilePath);
            string configFile = Path.GetFileName(Config.ConfigFilePath);

            _configWatcher = new FileSystemWatcher(configPath, configFile);
            _configWatcher.Changed += OnConfigChanged;
            _configWatcher.Created += OnConfigChanged;
            _configWatcher.Renamed += OnConfigChanged;
            _configWatcher.IncludeSubdirectories = false;
            _configWatcher.SynchronizingObject = null;
            _configWatcher.EnableRaisingEvents = true;
        }

        private static void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                System.Threading.Thread.Sleep(100);
                _config.Reload();
                DebugLog("Config reloaded!");
                if (Player.m_localPlayer != null)
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "MegaFactory Config Reloaded!");
            }
            catch (Exception ex)
            {
                Log.LogError($"Config reload failed: {ex.Message}");
            }
        }

        private void Update()
        {
            // Gate on local player so we don't tick during the main menu / loading,
            // but processing itself no longer cares where the player is — every
            // loaded smelter ticks via FactoryProcessor.ProcessAllStations.
            if (Player.m_localPlayer == null) return;

            _processTimer += Time.deltaTime;
            if (_processTimer >= ProcessInterval.Value)
            {
                _processTimer = 0f;
                FactoryProcessor.ProcessAllStations();
            }
        }

        private void OnDestroy()
        {
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
                _configWatcher = null;
            }
            _harmony?.UnpatchSelf();
        }

        private static void MigrateConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                string text = File.ReadAllText(configPath);
                bool changed = false;

                // Migrate old v1.0.x section (Work Orders was 7, now 8)
                changed |= MigrateCfgSection(ref text, "7. Work Orders", "8. Work Orders");
                // v1.3.0 standardises debug section to "99. Debug"
                changed |= MigrateCfgSection(ref text, "10. Debug", "99. Debug");

                if (changed)
                    File.WriteAllText(configPath, text.TrimEnd() + "\n");
            }
            catch { }
        }

        private static bool MigrateCfgSection(ref string text, string oldName, string newName)
        {
            string oldHeader = "[" + oldName + "]";
            int idx = text.IndexOf(oldHeader, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            int sectionEnd = text.IndexOf("\n[", idx + oldHeader.Length, StringComparison.Ordinal);

            if (newName == null || text.IndexOf("[" + newName + "]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (sectionEnd < 0)
                    text = text.Substring(0, idx).TrimEnd('\r', '\n');
                else
                    text = text.Substring(0, idx) + text.Substring(sectionEnd + 1);
            }
            else
            {
                text = text.Remove(idx, oldHeader.Length).Insert(idx, "[" + newName + "]");
            }
            return true;
        }
    }
}
