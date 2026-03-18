using System.Collections.Generic;

namespace MegaFactory
{
    public enum StationType
    {
        Kiln,
        Smelter,
        BlastFurnace,
        Windmill,
        SpinningWheel
    }

    /// <summary>
    /// Defines inputs for each station type. Prefab names match Valheim's internal names.
    /// </summary>
    public static class StationDefinitions
    {
        public struct InputItem
        {
            public string PrefabName;
            public string DisplayName;

            public InputItem(string prefab, string display)
            {
                PrefabName = prefab;
                DisplayName = display;
            }
        }

        // Charcoal Kiln: only regular Wood (not FineWood, RoundLog, etc.)
        public static readonly InputItem[] KilnInputs = new InputItem[]
        {
            new InputItem("Wood", "Wood"),
        };
        public static readonly string KilnFuel = null; // Kiln doesn't use fuel — wood IS the input

        // Smelter: Coal fuel + ore inputs
        public static readonly InputItem[] SmelterOres = new InputItem[]
        {
            new InputItem("CopperOre", "Copper Ore"),
            new InputItem("TinOre", "Tin Ore"),
            new InputItem("IronScrap", "Iron Scrap"),
            new InputItem("SilverOre", "Silver Ore"),
        };
        public static readonly string SmelterFuel = "Coal";

        // Blast Furnace: Coal fuel + ore inputs
        public static readonly InputItem[] BlastFurnaceOres = new InputItem[]
        {
            new InputItem("BlackMetalScrap", "Black Metal Scrap"),
            new InputItem("FlametalOreNew", "Flametal Ore"),
        };
        public static readonly string BlastFurnaceFuel = "Coal";

        // Windmill: no fuel, just barley
        public static readonly InputItem[] WindmillInputs = new InputItem[]
        {
            new InputItem("Barley", "Barley"),
        };
        public static readonly string WindmillFuel = null;

        // Spinning Wheel: no fuel, just flax / linen thread inputs
        public static readonly InputItem[] SpinningWheelInputs = new InputItem[]
        {
            new InputItem("Flax", "Flax"),
        };
        public static readonly string SpinningWheelFuel = null;

        public static InputItem[] GetInputs(StationType type)
        {
            switch (type)
            {
                case StationType.Kiln: return KilnInputs;
                case StationType.Smelter: return SmelterOres;
                case StationType.BlastFurnace: return BlastFurnaceOres;
                case StationType.Windmill: return WindmillInputs;
                case StationType.SpinningWheel: return SpinningWheelInputs;
                default: return new InputItem[0];
            }
        }

        public static string GetFuel(StationType type)
        {
            switch (type)
            {
                case StationType.Smelter: return SmelterFuel;
                case StationType.BlastFurnace: return BlastFurnaceFuel;
                default: return null;
            }
        }

        public static string GetStationDisplayName(StationType type)
        {
            switch (type)
            {
                case StationType.Kiln: return "Charcoal Kiln";
                case StationType.Smelter: return "Smelter";
                case StationType.BlastFurnace: return "Blast Furnace";
                case StationType.Windmill: return "Windmill";
                case StationType.SpinningWheel: return "Spinning Wheel";
                default: return "Unknown";
            }
        }
    }
}
