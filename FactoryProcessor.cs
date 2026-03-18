using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace MegaFactory
{
    /// <summary>
    /// Core processing logic — finds stations near the player and auto-feeds them
    /// from nearby containers, respecting work orders and the leave-1 rule.
    /// </summary>
    public static class FactoryProcessor
    {
        // Registries populated by Harmony patches
        public static readonly HashSet<Smelter> AllSmelters = new HashSet<Smelter>();

        public static void ProcessAllStations(Vector3 playerPos, float radius)
        {
            float radiusSq = radius * radius;
            var containers = ContainerHelper.FindNearbyContainers(playerPos, radius);
            if (containers.Count == 0) return;

            foreach (var smelter in AllSmelters)
            {
                if (smelter == null) continue;
                if ((playerPos - smelter.transform.position).sqrMagnitude > radiusSq) continue;

                var nview = smelter.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                var stationType = ClassifySmelter(smelter);
                if (stationType == null) continue;
                if (!IsStationEnabled(stationType.Value)) continue;

                ProcessSmelter(smelter, nview, stationType.Value, containers);
            }
        }

        private static StationType? ClassifySmelter(Smelter smelter)
        {
            string name = smelter.gameObject.name.ToLower();

            // Charcoal Kiln
            if (name.Contains("charcoal_kiln") || name.Contains("charcoalkiln"))
                return StationType.Kiln;

            // Blast Furnace (check before generic "smelter" since blast furnace name may contain "smelter")
            if (name.Contains("blastfurnace") || name.Contains("blast_furnace"))
                return StationType.BlastFurnace;

            // Smelter
            if (name.Contains("smelter"))
                return StationType.Smelter;

            // Windmill
            if (name.Contains("windmill"))
                return StationType.Windmill;

            // Spinning Wheel
            if (name.Contains("spinningwheel") || name.Contains("spinning_wheel"))
                return StationType.SpinningWheel;

            return null;
        }

        private static bool IsStationEnabled(StationType type)
        {
            switch (type)
            {
                case StationType.Kiln: return MegaFactoryPlugin.EnableKiln.Value;
                case StationType.Smelter: return MegaFactoryPlugin.EnableSmelter.Value;
                case StationType.BlastFurnace: return MegaFactoryPlugin.EnableBlastFurnace.Value;
                case StationType.Windmill: return MegaFactoryPlugin.EnableWindmill.Value;
                case StationType.SpinningWheel: return MegaFactoryPlugin.EnableSpinningWheel.Value;
                default: return false;
            }
        }

        private static void ProcessSmelter(Smelter smelter, ZNetView nview, StationType stationType, List<Container> containers)
        {
            // Get station capacity info
            int maxOre = smelter.m_maxOre;
            int maxFuel = smelter.m_maxFuel;

            int currentOre = GetQueuedOreCount(nview, smelter);
            float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);

            // ── Feed fuel first (Coal for Smelter/BlastFurnace) ──
            string fuelPrefab = StationDefinitions.GetFuel(stationType);
            if (fuelPrefab != null && currentFuel < maxFuel)
            {
                int fuelNeeded = maxFuel - Mathf.FloorToInt(currentFuel);
                int fuelTaken = TakeFromContainers(containers, fuelPrefab, fuelNeeded);
                if (fuelTaken > 0)
                {
                    nview.GetZDO().Set(ZDOVars.s_fuel, currentFuel + fuelTaken);
                }
            }

            // ── Feed ore/inputs ──
            if (currentOre >= maxOre) return;

            int slotsAvailable = maxOre - currentOre;
            var inputs = StationDefinitions.GetInputs(stationType);

            foreach (var input in inputs)
            {
                if (slotsAvailable <= 0) break;

                // Check work order — stations only process with an active order
                int remaining = WorkOrderManager.GetRemaining(nview, input.PrefabName);
                if (remaining <= 0) continue; // No work order or order complete — skip

                int toFeed = Mathf.Min(slotsAvailable, remaining);

                int taken = TakeFromContainers(containers, input.PrefabName, toFeed);
                if (taken > 0)
                {
                    // Write ore directly to ZDO queue slots (bypass RPC which expects player inventory)
                    nview.ClaimOwnership();
                    int added = 0;
                    for (int s = 0; s < maxOre && added < taken; s++)
                    {
                        string existing = nview.GetZDO().GetString($"item{s}", "");
                        if (string.IsNullOrEmpty(existing))
                        {
                            nview.GetZDO().Set($"item{s}", input.PrefabName);
                            added++;
                        }
                    }
                    // Update the queued count to match
                    int newTotal = GetQueuedOreCount(nview, smelter);
                    nview.GetZDO().Set(ZDOVars.s_queued, newTotal);
                    slotsAvailable -= added;
                }
            }
        }

        private static int GetQueuedOreCount(ZNetView nview, Smelter smelter)
        {
            int count = 0;
            for (int i = 0; i < smelter.m_maxOre; i++)
            {
                string ore = nview.GetZDO().GetString($"item{i}", "");
                if (!string.IsNullOrEmpty(ore))
                    count++;
            }
            return count;
        }

        private static int TakeFromContainers(List<Container> containers, string prefabName, int amount)
        {
            int totalTaken = 0;
            foreach (var container in containers)
            {
                if (totalTaken >= amount) break;
                int taken = ContainerHelper.TakeFromContainer(container, prefabName, amount - totalTaken);
                totalTaken += taken;
            }
            return totalTaken;
        }
    }

    // ==================== SMELTER REGISTRY PATCHES ====================
    // Smelter is the base class for Kiln, Smelter, Blast Furnace, Windmill, Spinning Wheel in Valheim

    [HarmonyPatch(typeof(Smelter), "Awake")]
    public static class Smelter_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Smelter __instance)
        {
            FactoryProcessor.AllSmelters.Add(__instance);
            MegaFactoryPlugin.Log?.LogDebug($"[Smelter_Awake] Registered: {__instance.gameObject.name} (total: {FactoryProcessor.AllSmelters.Count})");
            if (__instance.gameObject.GetComponent<SmelterDestroyTracker>() == null)
                __instance.gameObject.AddComponent<SmelterDestroyTracker>();
        }
    }

    [HarmonyPatch(typeof(Smelter), "OnDestroyed")]
    public static class Smelter_OnDestroyed_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Smelter __instance)
        {
            FactoryProcessor.AllSmelters.Remove(__instance);
            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
                WorkOrderManager.UnloadStation(nview.GetZDO().m_uid);
        }
    }

    public class SmelterDestroyTracker : MonoBehaviour
    {
        private void OnDestroy()
        {
            var s = GetComponent<Smelter>();
            if (s != null)
            {
                FactoryProcessor.AllSmelters.Remove(s);
                var nview = s.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                    WorkOrderManager.UnloadStation(nview.GetZDO().m_uid);
            }
        }
    }

    // ==================== PRODUCTION TRACKING PATCH ====================
    // When a smelter finishes producing an item, record it against the work order

    [HarmonyPatch]
    public static class Smelter_Spawn_Patch
    {
        [HarmonyTargetMethod]
        static System.Reflection.MethodBase TargetMethod()
        {
            // Try (string, int) first, fall back to (string)
            var method = AccessTools.Method(typeof(Smelter), "Spawn", new System.Type[] { typeof(string), typeof(int) });
            if (method != null)
            {
                MegaFactoryPlugin.Log?.LogDebug($"[Smelter_Spawn_Patch] Resolved Spawn(string, int)");
                return method;
            }
            method = AccessTools.Method(typeof(Smelter), "Spawn", new System.Type[] { typeof(string) });
            if (method != null)
            {
                MegaFactoryPlugin.Log?.LogDebug($"[Smelter_Spawn_Patch] Resolved Spawn(string)");
                return method;
            }
            MegaFactoryPlugin.Log?.LogWarning("[Smelter_Spawn_Patch] Could not find any Spawn method on Smelter!");
            return null;
        }

        [HarmonyPostfix]
        public static void Postfix(Smelter __instance, string ore)
        {
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            MegaFactoryPlugin.Log?.LogDebug($"[Smelter_Spawn_Patch] Produced: {ore}");
            WorkOrderManager.RecordProduction(nview, ore, 1);
        }
    }
}
