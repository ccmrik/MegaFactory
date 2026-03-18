using System;
using System.Collections.Generic;
using UnityEngine;

namespace MegaFactory
{
    /// <summary>
    /// A work order represents a request to produce N items at a specific station.
    /// Work orders are stored per-station via ZDO custom data so they persist across sessions.
    /// </summary>
    public class WorkOrder
    {
        public string PrefabName;  // ore/input prefab name (e.g. "CopperOre", "Wood")
        public string DisplayName; // friendly name for GUI
        public int Requested;      // total requested
        public int Produced;       // how many have been produced so far

        public int Remaining => Mathf.Max(0, Requested - Produced);
        public bool IsComplete => Remaining <= 0;
    }

    /// <summary>
    /// Manages work orders per station instance (keyed by ZDO UID).
    /// Persists to ZDO custom data so work orders survive logouts.
    /// </summary>
    public static class WorkOrderManager
    {
        // Key: ZDO ZDOID → work orders for that station
        private static readonly Dictionary<ZDOID, List<WorkOrder>> _orders = new Dictionary<ZDOID, List<WorkOrder>>();

        private const string ZDO_KEY = "MegaFactory_WorkOrders";

        public static List<WorkOrder> GetOrders(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return null;
            var id = nview.GetZDO().m_uid;

            if (!_orders.TryGetValue(id, out var orders))
            {
                orders = LoadFromZDO(nview);
                _orders[id] = orders;
            }
            return orders;
        }

        public static void SetOrders(ZNetView nview, List<WorkOrder> orders)
        {
            if (nview == null || !nview.IsValid()) return;
            var id = nview.GetZDO().m_uid;
            _orders[id] = orders;
            SaveToZDO(nview, orders);
        }

        public static void RecordProduction(ZNetView nview, string prefabName, int amount)
        {
            var orders = GetOrders(nview);
            if (orders == null) return;

            foreach (var order in orders)
            {
                if (order.PrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase) && !order.IsComplete)
                {
                    order.Produced += amount;
                    SaveToZDO(nview, orders);
                    return;
                }
            }
        }

        /// <summary>
        /// Returns how many of this input item the station should still produce.
        /// -1 means no work order (unlimited / use station toggle to control).
        /// 0 means the order is complete — stop processing.
        /// </summary>
        public static int GetRemaining(ZNetView nview, string prefabName)
        {
            var orders = GetOrders(nview);
            if (orders == null || orders.Count == 0) return -1;

            foreach (var order in orders)
            {
                if (order.PrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase))
                    return order.Remaining;
            }
            // Station has work orders but NOT for this item → don't auto-process it
            return 0;
        }

        public static void ClearOrders(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return;
            var id = nview.GetZDO().m_uid;
            _orders.Remove(id);
            nview.GetZDO().Set(ZDO_KEY, "");
        }

        public static bool HasActiveOrders(ZNetView nview)
        {
            var orders = GetOrders(nview);
            if (orders == null || orders.Count == 0) return false;
            foreach (var o in orders)
                if (!o.IsComplete) return true;
            return false;
        }

        // ── Serialization ──

        private static void SaveToZDO(ZNetView nview, List<WorkOrder> orders)
        {
            if (nview == null || !nview.IsValid()) return;

            // Format: "PrefabName:Requested:Produced|PrefabName:Requested:Produced|..."
            var parts = new List<string>();
            foreach (var o in orders)
                parts.Add($"{o.PrefabName}:{o.DisplayName}:{o.Requested}:{o.Produced}");

            nview.GetZDO().Set(ZDO_KEY, string.Join("|", parts));
        }

        private static List<WorkOrder> LoadFromZDO(ZNetView nview)
        {
            var result = new List<WorkOrder>();
            if (nview == null || !nview.IsValid()) return result;

            string data = nview.GetZDO().GetString(ZDO_KEY, "");
            if (string.IsNullOrEmpty(data)) return result;

            foreach (var entry in data.Split('|'))
            {
                var fields = entry.Split(':');
                if (fields.Length < 4) continue;
                if (!int.TryParse(fields[2], out int requested)) continue;
                if (!int.TryParse(fields[3], out int produced)) continue;

                result.Add(new WorkOrder
                {
                    PrefabName = fields[0],
                    DisplayName = fields[1],
                    Requested = requested,
                    Produced = produced
                });
            }
            return result;
        }

        public static void UnloadStation(ZDOID id)
        {
            _orders.Remove(id);
        }
    }
}
