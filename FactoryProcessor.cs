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

            // Eitr Refinery
            if (name.Contains("eitrrefinery") || name.Contains("eitr_refinery"))
                return StationType.EitrRefinery;

            return null;
        }

        public static bool IsStationManaged(StationType type) => IsStationEnabled(type);

        private static bool IsStationEnabled(StationType type)
        {
            switch (type)
            {
                case StationType.Kiln: return MegaFactoryPlugin.EnableKiln.Value;
                case StationType.Smelter: return MegaFactoryPlugin.EnableSmelter.Value;
                case StationType.BlastFurnace: return MegaFactoryPlugin.EnableBlastFurnace.Value;
                case StationType.Windmill: return MegaFactoryPlugin.EnableWindmill.Value;
                case StationType.SpinningWheel: return MegaFactoryPlugin.EnableSpinningWheel.Value;
                case StationType.EitrRefinery: return MegaFactoryPlugin.EnableEitrRefinery.Value;
                default: return false;
            }
        }

        private static void ProcessSmelter(Smelter smelter, ZNetView nview, StationType stationType, List<Container> containers)
        {
            // Get station capacity info
            int maxOre = smelter.m_maxOre;
            int maxFuel = smelter.m_maxFuel;

            // Use s_queued (authoritative) — NOT slot scanning which overcounts
            // due to stale values left by Smelter.RemoveOneOre's shift logic
            int currentOre = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
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

            // Note: we deliberately do NOT drain the spawn stack. Stations should behave
            // like vanilla — output pops out physically at m_outputPoint when the buffer
            // fills (spawnStack stations) or per-cycle (non-spawnStack). Users wanted the
            // "just like a normal refinery" feel. DrainSpawnStack is kept in the file as a
            // reference implementation in case it ever needs to come back behind a config.

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

                // Account for items already in the station queue + spawn buffer
                // so we don't overfeed (critical for m_spawnStack stations like Eitr Refinery,
                // where output only ejects when the queue empties)
                int queuedOfType = CountQueuedOfType(nview, smelter, input.PrefabName);
                int spawnBuffered = GetSpawnBuffered(nview, smelter, input.PrefabName);
                remaining = remaining - queuedOfType - spawnBuffered;

                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogInfo($"[ProcessSmelter] {stationType} | {input.PrefabName}: orderRemaining={remaining + queuedOfType + spawnBuffered}, queued={queuedOfType}, spawnBuf={spawnBuffered}, effectiveRemaining={remaining}, currentOre={currentOre}/{maxOre}");

                if (remaining <= 0) continue;

                int toFeed = Mathf.Min(slotsAvailable, remaining);

                int taken = TakeFromContainers(containers, input.PrefabName, toFeed);
                if (taken > 0)
                {
                    // Append items after the current queue tail (not random empty slots)
                    nview.ClaimOwnership();
                    int queueSize = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
                    int added = 0;
                    for (int s = queueSize; s < maxOre && added < taken; s++)
                    {
                        nview.GetZDO().Set($"item{s}", input.PrefabName);
                        added++;
                    }
                    // Update the authoritative queued count
                    nview.GetZDO().Set(ZDOVars.s_queued, queueSize + added);
                    slotsAvailable -= added;

                    if (MegaFactoryPlugin.DebugMode.Value)
                        MegaFactoryPlugin.Log?.LogInfo($"[ProcessSmelter] {stationType} | Fed {added} {input.PrefabName} (queue: {queueSize}→{queueSize + added}/{maxOre})");
                }
            }
        }

        /// <summary>
        /// For m_spawnStack stations (e.g. Eitr Refinery), output accumulates in ZDO
        /// fields s_spawnOre/s_spawnAmount instead of dropping on the ground.
        /// This method drains that stack into nearby containers.
        ///
        /// IMPORTANT: Valheim's Smelter stores the INPUT prefab name in s_spawnOre
        /// (see Smelter.QueueProcessed), not the output. Spawn(ore, num) later looks
        /// up m_conversion.m_to to instantiate the actual output item. We must do
        /// the same conversion here or we'd try to deposit Sap instead of Eitr.
        /// </summary>
        private static void DrainSpawnStack(Smelter smelter, ZNetView nview, List<Container> containers)
        {
            string spawnOre = nview.GetZDO().GetString(ZDOVars.s_spawnOre, "");
            int spawnAmount = nview.GetZDO().GetInt(ZDOVars.s_spawnAmount, 0);
            if (string.IsNullOrEmpty(spawnOre) || spawnAmount <= 0) return;

            // Convert INPUT prefab (what's stored in the ZDO) → OUTPUT prefab (what we deposit).
            string outputPrefab = GetOutputForInput(smelter, spawnOre);
            if (string.IsNullOrEmpty(outputPrefab))
            {
                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogWarning($"[DrainSpawnStack] No conversion found for input '{spawnOre}' on {smelter.gameObject.name} — skipping drain");
                return;
            }

            int deposited = ContainerHelper.DepositToContainers(containers, outputPrefab, spawnAmount);
            if (deposited <= 0)
            {
                DiagnosticsHud.RecordEvent($"DrainSpawnStack: 0 deposited ({spawnAmount} {outputPrefab} stuck in buffer)");
                return;
            }

            nview.ClaimOwnership();
            int remaining = spawnAmount - deposited;
            nview.GetZDO().Set(ZDOVars.s_spawnAmount, remaining);
            if (remaining <= 0)
                nview.GetZDO().Set(ZDOVars.s_spawnOre, "");

            // Credit the work order (keyed by INPUT prefab, same as feeding).
            WorkOrderManager.RecordProduction(nview, spawnOre, deposited);

            string msg = $"DrainSpawnStack: {deposited} {outputPrefab} from '{spawnOre}' buffer → containers ({remaining} left in stack)";
            MegaFactoryPlugin.Log?.LogInfo($"[MegaFactory] {msg}");
            DiagnosticsHud.RecordEvent($"DRAIN OK: {msg}");
        }

        private static int GetQueuedOreCount(ZNetView nview, Smelter smelter)
        {
            // Use s_queued — the authoritative count managed by the Smelter.
            // Do NOT scan slots; Smelter.RemoveOneOre shifts items down but
            // doesn't clear the tail, leaving stale values that cause overcounting.
            return nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
        }

        private static int CountQueuedOfType(ZNetView nview, Smelter smelter, string prefabName)
        {
            // Only scan within the actual queue range (s_queued), not all slots
            int queueSize = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            int count = 0;
            for (int i = 0; i < queueSize && i < smelter.m_maxOre; i++)
            {
                string ore = nview.GetZDO().GetString($"item{i}", "");
                if (ore.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private static int GetSpawnBuffered(ZNetView nview, Smelter smelter, string inputPrefab)
        {
            string spawnOre = nview.GetZDO().GetString(ZDOVars.s_spawnOre, "");
            if (string.IsNullOrEmpty(spawnOre)) return 0;

            // s_spawnOre stores the INPUT prefab name (e.g. "Sap"), not the output —
            // see Smelter.QueueProcessed. Compare directly against the input we're feeding.
            if (!spawnOre.Equals(inputPrefab, System.StringComparison.OrdinalIgnoreCase)) return 0;
            return nview.GetZDO().GetInt(ZDOVars.s_spawnAmount, 0);
        }

        /// <summary>
        /// Maps an input prefab to its output prefab using the Smelter's conversion table.
        /// e.g. "Sap" → "Eitr", "CopperOre" → "Copper", "Wood" → "Coal"
        /// </summary>
        public static string GetOutputForInput(Smelter smelter, string inputPrefab)
        {
            if (smelter?.m_conversion == null) return null;
            foreach (var conv in smelter.m_conversion)
            {
                if (conv.m_from != null && conv.m_from.gameObject.name.Equals(inputPrefab, System.StringComparison.OrdinalIgnoreCase))
                    return conv.m_to?.gameObject.name;
            }
            return null;
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

    // ==================== OUTPUT INTERCEPT PATCH ====================
    // Intercept Smelter.Spawn — instead of dropping items on the ground at m_outputPoint,
    // deposit them directly into nearby containers. Works for BOTH m_spawnStack modes:
    //   - m_spawnStack=false (e.g. Smelter, Blast Furnace, Eitr Refinery): Spawn fires
    //     once per produced item with stack=1.
    //   - m_spawnStack=true: Spawn fires when the buffer hits maxStackSize OR when
    //     SpawnProcessed is invoked (queue empty / fuel out / player hit Empty).
    //
    // The vanilla `ore` parameter is the INPUT prefab (e.g. "Sap"); Spawn looks up
    // m_conversion.m_to to find the output. We do the same conversion to know what
    // to deposit.

    [HarmonyPatch]
    public static class Smelter_Spawn_Patch
    {
        [HarmonyTargetMethod]
        static System.Reflection.MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(Smelter), "Spawn", new System.Type[] { typeof(string), typeof(int) });
            if (method != null) return method;
            return AccessTools.Method(typeof(Smelter), "Spawn", new System.Type[] { typeof(string) });
        }

        // Postfix — let vanilla Spawn drop the item physically at m_outputPoint like any
        // other station. We just record the work-order progress so the GUI stays accurate.
        // (Earlier builds used a Prefix that intercepted Spawn and teleported the output
        // into nearby containers, but the user prefers stations to behave like vanilla —
        // pop the item out, pick it up manually.)
        [HarmonyPostfix]
        public static void Postfix(Smelter __instance, string ore, int stack = 1)
        {
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (!FactoryProcessor.AllSmelters.Contains(__instance)) return;

            var stationType = ClassifyByName(__instance.gameObject.name);
            if (stationType != null && !FactoryProcessor.IsStationManaged(stationType.Value)) return;

            // Guard against the Spawn(string) no-conversion no-op path — only credit when
            // vanilla actually could/did spawn something. GetItemConversion searches m_from
            // by name; if it matches, vanilla instantiated the m_to prefab at the output point.
            if (FactoryProcessor.GetOutputForInput(__instance, ore) == null) return;

            WorkOrderManager.RecordProduction(nview, ore, stack);

            string msg = $"{__instance.gameObject.name} produced {stack} from '{ore}' (vanilla drop at output point).";
            if (MegaFactoryPlugin.DebugMode.Value)
                MegaFactoryPlugin.Log?.LogInfo($"[MegaFactory] {msg}");
            DiagnosticsHud.RecordEvent($"PRODUCED: {msg}");
        }

        private static StationType? ClassifyByName(string rawName)
        {
            string name = rawName.ToLower();
            if (name.Contains("charcoal_kiln") || name.Contains("charcoalkiln")) return StationType.Kiln;
            if (name.Contains("blastfurnace") || name.Contains("blast_furnace")) return StationType.BlastFurnace;
            if (name.Contains("eitrrefinery") || name.Contains("eitr_refinery")) return StationType.EitrRefinery;
            if (name.Contains("windmill")) return StationType.Windmill;
            if (name.Contains("spinningwheel") || name.Contains("spinning_wheel")) return StationType.SpinningWheel;
            if (name.Contains("smelter")) return StationType.Smelter;
            return null;
        }
    }
}
