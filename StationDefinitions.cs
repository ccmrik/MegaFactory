using System.Collections.Generic;

namespace MegaFactory
{
    public enum StationType
    {
        Kiln,
        Smelter,
        BlastFurnace,
        Windmill,
        SpinningWheel,
        EitrRefinery
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
        // Scrap variants (BronzeScrap → Bronze, CopperScrap → Copper) live in
        // Valheim's Smelter.m_conversion table — adding them here makes them
        // selectable as work orders so the auto-feeder can salvage them too.
        public static readonly InputItem[] SmelterOres = new InputItem[]
        {
            new InputItem("CopperOre", "Copper Ore"),
            new InputItem("CopperScrap", "Scrap Copper"),
            new InputItem("TinOre", "Tin Ore"),
            new InputItem("BronzeScrap", "Scrap Bronze"),
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

        // Eitr Refinery: Sap is the FUEL, Soft Tissue is the ORE.
        // Confirmed by the v1.2.2 diagnostic HUD against a live refinery:
        //   m_fuelItem=Sap     m_conversion: Softtissue -> Eitr
        // (Every release before v1.2.3 had these swapped, which is why no Eitr
        // ever ejected — we fed Sap as ore, and Smelter.Spawn("Sap", ...)
        // couldn't find a matching conversion in m_conversion.)
        // Prefab name is "Softtissue" (lowercase 't') per valheim_data_dump.json.
        public static readonly InputItem[] EitrRefineryInputs = new InputItem[]
        {
            new InputItem("Softtissue", "Soft Tissue"),
        };
        public static readonly string EitrRefineryFuel = "Sap";

        public static InputItem[] GetInputs(StationType type)
        {
            switch (type)
            {
                case StationType.Kiln: return KilnInputs;
                case StationType.Smelter: return SmelterOres;
                case StationType.BlastFurnace: return BlastFurnaceOres;
                case StationType.Windmill: return WindmillInputs;
                case StationType.SpinningWheel: return SpinningWheelInputs;
                case StationType.EitrRefinery: return EitrRefineryInputs;
                default: return new InputItem[0];
            }
        }

        public static string GetFuel(StationType type)
        {
            switch (type)
            {
                case StationType.Smelter: return SmelterFuel;
                case StationType.BlastFurnace: return BlastFurnaceFuel;
                case StationType.EitrRefinery: return EitrRefineryFuel;
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
                case StationType.EitrRefinery: return "Eitr Refinery";
                default: return "Unknown";
            }
        }
    }
}
