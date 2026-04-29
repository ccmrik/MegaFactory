using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
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

        // Background processing: iterate every loaded smelter regardless of player
        // distance. Each station finds containers within SearchRadius of itself
        // (not of the player) so factories keep producing while the player is on
        // the other side of the map.
        public static void ProcessAllStations()
        {
            if (AllSmelters.Count == 0) return;

            float radius = MegaFactoryPlugin.SearchRadius.Value;
            foreach (var smelter in AllSmelters)
            {
                if (smelter == null) continue;

                var nview = smelter.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                var stationType = ClassifySmelter(smelter);
                if (stationType == null) continue;
                if (!IsStationEnabled(stationType.Value)) continue;

                var containers = ContainerHelper.FindNearbyContainers(smelter.transform.position, radius);
                if (containers.Count == 0) continue;

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
            MegaFactoryPlugin.DebugLog($"[MegaFactory] {msg}");
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
            => GetOutputItemDrop(smelter, inputPrefab)?.gameObject.name;

        public static ItemDrop GetOutputItemDrop(Smelter smelter, string inputPrefab)
        {
            if (smelter?.m_conversion == null) return null;
            foreach (var conv in smelter.m_conversion)
            {
                if (conv.m_from != null && conv.m_from.gameObject.name.Equals(inputPrefab, System.StringComparison.OrdinalIgnoreCase))
                    return conv.m_to;
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
            var outputDrop = FactoryProcessor.GetOutputItemDrop(__instance, ore);
            if (outputDrop == null) return;

            // EVERYTHING after vanilla Spawn() runs in this Postfix MUST be wrapped — if we
            // throw here, vanilla's QueueProcessed never reaches its post-Spawn ZDO clear
            // (s_spawnAmount = 0 for spawn-stack stations) AND UpdateSmelter never reaches
            // SetAccumulator. That manifests as: spawn-stack stations infinite-respawning
            // (Lady Emz's "thousands of barley flour"), and non-spawn-stack stations losing
            // all catch-up backlog (Milord's "no production after coming back").
            // Pre-v1.4.4 the postfix threw a TypeLoadException on every call due to a
            // missing System.ValueTuple dependency in the Aggregator's Dictionary key.
            try
            {
                WorkOrderManager.RecordProduction(nview, ore, stack);

                // Aggregate spawn events within a short window so a chunk-reload catch-up
                // (potentially many Spawn(ore,1) calls in one frame for non-spawnStack stations)
                // collapses to one "Factory: Iron +57" toast instead of 57 individual ones.
                if (MegaFactoryPlugin.ShowProductionMessage.Value)
                    ProductionToastAggregator.Record(__instance, ore, stack);

                string msg = $"{__instance.gameObject.name} produced {stack} from '{ore}' (vanilla drop at output point).";
                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogInfo($"[MegaFactory] {msg}");
                DiagnosticsHud.RecordEvent($"PRODUCED: {msg}");
            }
            catch (System.Exception ex)
            {
                MegaFactoryPlugin.Log?.LogError($"[Smelter_Spawn_Patch.Postfix] swallowed exception (would break vanilla state): {ex.Message}");
            }
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

    // ==================== PRODUCTION TOAST AGGREGATOR ====================
    // Collapses bursts of Smelter.Spawn calls into a single "Factory: <item> +N" toast.
    // Critical for chunk-reload catch-up: vanilla's UpdateSmelter runs `while (accumulator >= 1f)`
    // and may call Spawn(ore,1) dozens of times in one frame for non-spawnStack stations. Without
    // aggregation that's a wave of identical toasts; with it, one clean line per (station,output).
    public static class ProductionToastAggregator
    {
        private const float FlushDelaySeconds = 0.4f;

        // We deliberately AVOID `Dictionary<(int, string), ...>` here — `(int, string)` is a
        // System.ValueTuple<int, string>, which on .NET 4.6.2 lives in System.ValueTuple.dll
        // (not in mscorlib until 4.7+). End users don't have that DLL, BepInEx doesn't ship
        // it, and the type fails to load at runtime → TypeLoadException on every Postfix call.
        // Pre-v1.4.4 that broke vanilla Smelter state in two nasty ways (see Postfix comment).
        // String key is boring and bulletproof.
        private static readonly Dictionary<string, AggregateEntry> _pending
            = new Dictionary<string, AggregateEntry>();
        private static bool _flushScheduled;

        private struct AggregateEntry
        {
            public Smelter Station;
            public string Ore;
            public int TotalStack;
        }

        private static string MakeKey(int instanceId, string ore)
            => instanceId.ToString() + "|" + ore;

        public static void Record(Smelter station, string ore, int stack)
        {
            if (station == null || string.IsNullOrEmpty(ore) || stack <= 0) return;

            string key = MakeKey(station.GetInstanceID(), ore);
            if (_pending.TryGetValue(key, out var entry))
            {
                entry.TotalStack += stack;
                _pending[key] = entry;
            }
            else
            {
                _pending[key] = new AggregateEntry { Station = station, Ore = ore, TotalStack = stack };
            }

            if (!_flushScheduled && MegaFactoryPlugin.Instance != null)
            {
                _flushScheduled = true;
                MegaFactoryPlugin.Instance.StartCoroutine(FlushAfterDelay());
            }
        }

        private static IEnumerator FlushAfterDelay()
        {
            yield return new WaitForSeconds(FlushDelaySeconds);
            EmitToasts();
        }

        private static void EmitToasts()
        {
            try
            {
                if (Player.m_localPlayer == null) return;

                foreach (var kv in _pending)
                {
                    var entry = kv.Value;
                    if (entry.Station == null) continue;

                    var outputDrop = FactoryProcessor.GetOutputItemDrop(entry.Station, entry.Ore);
                    if (outputDrop == null) continue;

                    var shared = outputDrop.m_itemData?.m_shared;
                    if (shared == null) continue;

                    Sprite icon = (shared.m_icons != null && shared.m_icons.Length > 0) ? shared.m_icons[0] : null;
                    Player.m_localPlayer.Message(
                        MessageHud.MessageType.TopLeft,
                        "Factory: " + shared.m_name,
                        entry.TotalStack,
                        icon);
                }
            }
            finally
            {
                _pending.Clear();
                _flushScheduled = false;
            }
        }
    }

    // ==================== ACCUMULATOR CAP TRANSPILER ====================
    // Vanilla `Smelter.UpdateSmelter` caps the offline-catchup accumulator at 3600s (1 hour):
    //
    //   if (accumulator > 3600f) { accumulator = 3600f; }
    //
    // That's why factories silently lose long away-times. We swap both `3600f` literals for a
    // call to GetCap(), backed by the BackgroundCatchupHours config (default 24h, max 168h).
    // Verified against assembly_valheim 2026-04-29: only two `3600f` occurrences in the entire
    // Smelter type, both inside UpdateSmelter, both this cap.
    [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
    public static class Smelter_UpdateSmelter_CapTranspiler
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getCap = AccessTools.Method(typeof(Smelter_UpdateSmelter_CapTranspiler), nameof(GetCap));
            int swaps = 0;
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && Mathf.Approximately(f, 3600f))
                {
                    swaps++;
                    yield return new CodeInstruction(OpCodes.Call, getCap);
                }
                else
                {
                    yield return ins;
                }
            }
            // Always log the swap count (LogAlways path — not gated by DebugMode) so users
            // and devs can confirm the cap was raised on launch without flipping any flags.
            if (swaps == 2)
                MegaFactoryPlugin.Log?.LogInfo($"[CapTranspiler] OK: raised Smelter offline-catchup cap from 1h to {GetCap() / 3600f:0.#}h ({swaps} IL swaps applied).");
            else
                MegaFactoryPlugin.Log?.LogWarning($"[CapTranspiler] FAIL: expected 2 cap swaps, made {swaps} — vanilla Smelter.UpdateSmelter may have changed. Offline catch-up will fall back to vanilla behaviour.");
        }

        public static float GetCap()
        {
            float hours = MegaFactoryPlugin.BackgroundCatchupHours?.Value ?? 24f;
            return Mathf.Max(60f, hours * 3600f);
        }
    }

    // ==================== CATCH-UP DIAGNOSTIC ====================
    // When a chunk reloads, vanilla Smelter.UpdateSmelter sees a big GetDeltaTime() and runs
    // the production loop hard. If catch-up isn't happening, this Prefix/Postfix pair makes
    // the symptom visible in the log: pre-state (queued/fuel/spawnAmount) + post-state +
    // the delta. Always-on (LogAlways) when delta > 30s — that's the "interesting" path.
    [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
    public static class Smelter_UpdateSmelter_Diagnostic
    {
        public struct PreState
        {
            public bool Capture;
            public int Queued;
            public float Fuel;
            public string SpawnOre;
            public int SpawnAmount;
            public long StartTimeTicks;
            public float Accumulator;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Smelter __instance, out PreState __state)
        {
            __state = default;
            var nview = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            var zdo = nview.GetZDO();
            __state = new PreState
            {
                Capture = true,
                Queued = zdo.GetInt(ZDOVars.s_queued, 0),
                Fuel = zdo.GetFloat(ZDOVars.s_fuel, 0f),
                SpawnOre = zdo.GetString(ZDOVars.s_spawnOre, ""),
                SpawnAmount = zdo.GetInt(ZDOVars.s_spawnAmount, 0),
                StartTimeTicks = zdo.GetLong(ZDOVars.s_startTime, 0L),
                Accumulator = zdo.GetFloat(ZDOVars.s_accTime, 0f),
            };
        }

        [HarmonyPostfix]
        public static void Postfix(Smelter __instance, PreState __state)
        {
            try
            {
                if (!__state.Capture) return;
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                // Estimate delta the way vanilla does: now - s_startTime BEFORE this tick.
                // GetDeltaTime overwrites s_startTime, so we read what we captured.
                double deltaSec = 0;
                try
                {
                    if (ZNet.instance != null && __state.StartTimeTicks > 0L)
                    {
                        var now = ZNet.instance.GetTime();
                        var prev = new System.DateTime(__state.StartTimeTicks);
                        deltaSec = (now - prev).TotalSeconds;
                    }
                }
                catch { }

                // Only chatter on big deltas (real catch-up events) so we don't spam every second.
                if (deltaSec < 30) return;

                var zdo = nview.GetZDO();
                int queuedAfter = zdo.GetInt(ZDOVars.s_queued, 0);
                float fuelAfter = zdo.GetFloat(ZDOVars.s_fuel, 0f);
                int spawnAfter = zdo.GetInt(ZDOVars.s_spawnAmount, 0);

                int qDelta = __state.Queued - queuedAfter;
                float fDelta = __state.Fuel - fuelAfter;
                int sDelta = spawnAfter - __state.SpawnAmount;

                float capUsed = Smelter_UpdateSmelter_CapTranspiler.GetCap();
                string name = __instance.gameObject.name;
                MegaFactoryPlugin.Log?.LogInfo(
                    $"[Catchup] {name} delta={deltaSec / 60:0.0}min cap={capUsed / 60:0.0}min " +
                    $"| pre: q={__state.Queued} f={__state.Fuel:0.0} buf={__state.SpawnAmount} " +
                    $"| post: q={queuedAfter} f={fuelAfter:0.0} buf={spawnAfter} " +
                    $"| consumed: -{qDelta}ore -{fDelta:0.0}fuel +{sDelta}buf");
            }
            catch (System.Exception ex)
            {
                MegaFactoryPlugin.Log?.LogError($"[CatchupDiag] swallowed exception: {ex.Message}");
            }
        }
    }
}
