using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MegaFactory
{
    public enum ContainerType
    {
        Unknown,
        WoodChest,
        ReinforcedChest,
        BlackMetalChest,
        Barrel,
        Private
    }

    public static class ContainerHelper
    {
        public static readonly HashSet<Container> AllContainers = new HashSet<Container>();
        private static readonly Dictionary<Container, ContainerType> _typeCache = new Dictionary<Container, ContainerType>();

        private static float _lastPruneTime;
        private const float PRUNE_INTERVAL = 30f;

        private static readonly MethodInfo _loadMethod;
        private static readonly MethodInfo _saveMethod;

        static ContainerHelper()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            _loadMethod = typeof(Container).GetMethod("Load", flags)
                       ?? typeof(Container).GetMethod("LoadInventory", flags)
                       ?? typeof(Container).GetMethod("ReadInventory", flags);
            _saveMethod = typeof(Container).GetMethod("Save", flags)
                       ?? typeof(Container).GetMethod("SaveInventory", flags);
        }

        public static ContainerType ClassifyContainer(Container container)
        {
            string name = container.gameObject.name.ToLower();
            if (name.Contains("private"))
                return ContainerType.Private;
            if (name.Contains("piece_chest_blackmetal") || name.StartsWith("blackmetalchest"))
                return ContainerType.BlackMetalChest;
            if (name.Contains("barrel") || name.Contains("incinerator") || name.Contains("obliterator"))
                return ContainerType.Barrel;
            if ((name.Contains("piece_chest") || name.StartsWith("reinforcedchest")) &&
                !name.Contains("wood") && !name.Contains("blackmetal"))
                return ContainerType.ReinforcedChest;
            if (name.Contains("chest"))
                return ContainerType.WoodChest;
            return ContainerType.Unknown;
        }

        public static void Register(Container container)
        {
            AllContainers.Add(container);
            _typeCache[container] = ClassifyContainer(container);
        }

        public static void Unregister(Container container)
        {
            AllContainers.Remove(container);
            _typeCache.Remove(container);
        }

        public static ContainerType GetContainerType(Container container)
        {
            if (_typeCache.TryGetValue(container, out var type))
                return type;
            return ContainerType.Unknown;
        }

        public static bool IsAllowedContainer(Container container)
        {
            var type = GetContainerType(container);
            switch (type)
            {
                case ContainerType.WoodChest: return MegaFactoryPlugin.UseChests.Value;
                case ContainerType.ReinforcedChest: return MegaFactoryPlugin.UseReinforcedChests.Value;
                case ContainerType.BlackMetalChest: return MegaFactoryPlugin.UseBlackMetalChests.Value;
                case ContainerType.Barrel: return MegaFactoryPlugin.UseBarrels.Value;
                default: return false;
            }
        }

        public static void EnsureLoaded(Container container, Inventory inventory)
        {
            if (inventory.GetAllItems().Count > 0) return;

            var nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            if (_loadMethod != null)
            {
                try
                {
                    _loadMethod.Invoke(container, null);
                    if (inventory.GetAllItems().Count > 0) return;
                }
                catch { }
            }

            try
            {
                string data = zdo.GetString(ZDOVars.s_items, "");
                if (!string.IsNullOrEmpty(data))
                {
                    ZPackage pkg = new ZPackage(data);
                    inventory.Load(pkg);
                }
            }
            catch { }
        }

        public static List<Container> FindNearbyContainers(Vector3 position, float radius)
        {
            float now = Time.time;
            if (now - _lastPruneTime > PRUNE_INTERVAL)
            {
                _lastPruneTime = now;
                AllContainers.RemoveWhere(c => c == null);
                var staleKeys = new List<Container>();
                foreach (var kvp in _typeCache)
                    if (kvp.Key == null) staleKeys.Add(kvp.Key);
                foreach (var k in staleKeys) _typeCache.Remove(k);
            }

            var result = new List<Container>();
            float radiusSq = radius * radius;
            foreach (var container in AllContainers)
            {
                if (container == null) continue;
                if ((position - container.transform.position).sqrMagnitude > radiusSq) continue;
                if (!IsAllowedContainer(container)) continue;
                result.Add(container);
            }
            return result;
        }

        /// <summary>
        /// Take items from a container, always leaving at least 1 of that item type.
        /// Returns the number of items actually taken.
        /// </summary>
        public static int TakeFromContainer(Container container, string prefabName, int amount)
        {
            var inventory = container.GetInventory();
            if (inventory == null) return 0;
            EnsureLoaded(container, inventory);

            int taken = 0;
            foreach (var item in inventory.GetAllItems())
            {
                if (item == null) continue;
                string itemPrefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
                if (!itemPrefab.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase)) continue;

                // Always leave 1
                int available = item.m_stack - 1;
                if (available <= 0) continue;

                int toTake = Mathf.Min(available, amount - taken);
                if (toTake <= 0) continue;

                inventory.RemoveItem(item, toTake);
                taken += toTake;

                if (taken >= amount) break;
            }

            if (taken > 0)
                SaveContainerToZDO(container);

            return taken;
        }

        /// <summary>
        /// Count items of a given prefab across all items in a container (respecting leave-1 rule).
        /// </summary>
        public static int CountAvailable(Container container, string prefabName)
        {
            var inventory = container.GetInventory();
            if (inventory == null) return 0;
            EnsureLoaded(container, inventory);

            int count = 0;
            foreach (var item in inventory.GetAllItems())
            {
                if (item == null) continue;
                string itemPrefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
                if (!itemPrefab.Equals(prefabName, System.StringComparison.OrdinalIgnoreCase)) continue;
                // Leave 1
                int available = item.m_stack - 1;
                if (available > 0) count += available;
            }
            return count;
        }

        /// <summary>
        /// Deposit items into the nearest containers with space.
        /// Returns the number of items actually deposited.
        /// </summary>
        public static int DepositToContainers(List<Container> containers, string prefabName, int amount)
        {
            int totalDeposited = 0;
            foreach (var container in containers)
            {
                if (totalDeposited >= amount) break;
                int deposited = DepositToContainer(container, prefabName, amount - totalDeposited);
                totalDeposited += deposited;
            }
            return totalDeposited;
        }

        /// <summary>
        /// Deposit items into a single container using Valheim's standard AddItem(prefab, amount)
        /// which handles worldLevel/stacking/grid placement correctly, then persist via
        /// Container.Save (reflection fallback to raw ZPackage if the method isn't found).
        /// Returns the number of items actually deposited.
        /// </summary>
        public static int DepositToContainer(Container container, string prefabName, int amount)
        {
            if (container == null || amount <= 0) return 0;

            var nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            var inventory = container.GetInventory();
            if (inventory == null) return 0;
            EnsureLoaded(container, inventory);

            GameObject prefab = ObjectDB.instance?.GetItemPrefab(prefabName);
            if (prefab == null) prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogWarning($"[Deposit] Prefab '{prefabName}' not in ObjectDB or ZNetScene — cannot deposit");
                return 0;
            }
            if (prefab.GetComponent<ItemDrop>() == null)
            {
                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogWarning($"[Deposit] Prefab '{prefabName}' has no ItemDrop component");
                return 0;
            }

            // We MUST own the container ZDO or the write won't stick (another peer's
            // authoritative state would overwrite us on next sync).
            nview.ClaimOwnership();

            int deposited = 0;
            while (deposited < amount)
            {
                if (!inventory.AddItem(prefab, 1))
                    break; // container full
                deposited++;
            }

            if (deposited > 0)
            {
                SaveContainerToZDO(container);
                if (MegaFactoryPlugin.DebugMode.Value)
                    MegaFactoryPlugin.Log?.LogInfo($"[Deposit] +{deposited} {prefabName} → {container.gameObject.name} (slots now: {inventory.NrOfItems()})");
            }

            return deposited;
        }

        /// <summary>
        /// Persist a container's inventory. Prefer Container.Save() (vanilla method — updates
        /// ZDO key and fires change events). Fall back to writing the ZPackage directly if
        /// the method name ever changes.
        /// </summary>
        public static void SaveContainerToZDO(Container container)
        {
            try
            {
                var nview = container.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                // Must be owner to persist — claim first if we're not.
                if (!nview.IsOwner()) nview.ClaimOwnership();
                if (!nview.IsOwner()) return;

                if (_saveMethod != null)
                {
                    _saveMethod.Invoke(container, null);
                    return;
                }

                // Fallback: raw serialization
                var inv = container.GetInventory();
                if (inv == null) return;
                ZPackage pkg = new ZPackage();
                inv.Save(pkg);
                nview.GetZDO().Set(ZDOVars.s_items, pkg.GetBase64());
            }
            catch (System.Exception ex)
            {
                MegaFactoryPlugin.Log.LogError($"[SaveContainerToZDO] {ex.Message}");
            }
        }
    }

    // ==================== CONTAINER REGISTRY PATCHES ====================

    [HarmonyPatch(typeof(Container), "Awake")]
    public static class Container_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Container __instance)
        {
            ContainerHelper.Register(__instance);
            MegaFactoryPlugin.Log?.LogDebug($"[Container_Awake] Registered: {__instance.gameObject.name} as {ContainerHelper.GetContainerType(__instance)} (total: {ContainerHelper.AllContainers.Count})");
            if (__instance.gameObject.GetComponent<ContainerDestroyTracker>() == null)
                __instance.gameObject.AddComponent<ContainerDestroyTracker>();
        }
    }

    [HarmonyPatch(typeof(Container), "OnDestroyed")]
    public static class Container_OnDestroyed_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Container __instance)
        {
            ContainerHelper.Unregister(__instance);
        }
    }

    public class ContainerDestroyTracker : MonoBehaviour
    {
        private void OnDestroy()
        {
            var c = GetComponent<Container>();
            if (c != null) ContainerHelper.Unregister(c);
        }
    }
}
