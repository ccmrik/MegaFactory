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
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;
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

        // Timers
        private static float _processTimer;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            // 1. General
            SearchRadius = Config.Bind("1. General", "SearchRadius", 15f,
                new ConfigDescription("Radius to search for containers (meters)", new AcceptableValueRange<float>(1f, 100f)));
            ProcessInterval = Config.Bind("1. General", "ProcessInterval", 3f,
                new ConfigDescription("How often stations process items (seconds)", new AcceptableValueRange<float>(0.5f, 30f)));
            UseChests = Config.Bind("1. General", "UseChests", true, "Pull from regular Chests");
            UseReinforcedChests = Config.Bind("1. General", "UseReinforcedChests", true, "Pull from Reinforced Chests");
            UseBlackMetalChests = Config.Bind("1. General", "UseBlackMetalChests", true, "Pull from Black Metal Chests");
            UseBarrels = Config.Bind("1. General", "UseBarrels", true, "Pull from Barrels");

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

            _config = Config;
            SetupConfigWatcher();

            _harmony = new Harmony(PluginGUID);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo($"Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony PatchAll FAILED: {ex.Message}");
                Log.LogError(ex.ToString());
            }

            Log.LogInfo($"{PluginName} loaded successfully!");
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
                Log.LogInfo("Config reloaded!");
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
            Player player = Player.m_localPlayer;
            if (player == null) return;

            _processTimer += Time.deltaTime;
            if (_processTimer >= ProcessInterval.Value)
            {
                _processTimer = 0f;
                FactoryProcessor.ProcessAllStations(player.transform.position, SearchRadius.Value);
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
    }
}
