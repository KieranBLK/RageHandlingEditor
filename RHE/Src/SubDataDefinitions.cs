using System;
using System.Collections.Generic;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // When a newly created category starts to take effect
        // Live reads at once, Respawn needs the vehicle respawned, and ModelLoad is baked when the model loads, so only Unload Vehicle applies it
        private enum CreationEffect
        {
            Live,
            Respawn,
            ModelLoad
        }

        // The facts of one category's flags submenu, which only Car and SpecialFlight have
        private class CategoryFlagsFacts
        {
            // The XML element that holds the flags value
            public string ElementName;
            public FlagNameEntry[] Entries;
            // The descriptor category of the flag rows, which differs from the category's own name
            public string DescriptorCategory;
            // Takes the flags value in hex and the number of ticked rows
            public string SubtitleTemplate;
            // The title of the submenu row inside the category's menu
            public string RowTitle;
            // The descriptor key of the flags menu's own description, or null for a menu with none
            public string MenuDescriptionKey;
        }

        private static CategoryFlagsFacts BuildFlagsFacts(string menuKey, string menuDescriptionKey)
        {
            FlagFamily family = FindFlagFamilyByMenuKey(menuKey);
            return new CategoryFlagsFacts { ElementName = family.XmlTag, Entries = family.Entries, DescriptorCategory = family.DescriptorCategory, SubtitleTemplate = family.LiveSubtitleTemplate, RowTitle = family.ResidualTitle, MenuDescriptionKey = menuDescriptionKey };
        }

        // The one owner of every fact about a sub-handling category, which every table and branch over the 10 categories reads
        // The typed subclass carries the field table and the resolver, since those depend on the category's data type
        private abstract class CategoryDefinition
        {
            // The position in the definition table, which is also the one order every list of categories follows
            public int Index;
            // The short name every editor key, undo record, and Preview ref uses, which is one of the CATEGORY_ consts
            public string Name;
            public string LongName;
            public string XmlType;
            // Flying and VerticalFlying share one XML type, and only the handlingType child tells them apart
            public bool IsVerticalFlying;
            public PluginCategory PluginCategory;
            public FieldEntry[] Fields;
            // Rows that sit beside the category's table but are not part of its data object (Weapon's base-level damage scale)
            public FieldEntry[] ExtraFields = new FieldEntry[0];
            // The vehicle types whose game code reads the category unguarded, so a delete only excludes it from the save
            public VehicleType[] RequiredTypes;
            public string[] RequiredModelNames;
            // The model hashes of RequiredModelNames, set when the definitions are built, or null with no names
            public HashSet<int> RequiredModelHashes;
            // The vehicle types phantom and live creation is allowed on, or null for any type
            public VehicleType[] CandidateTypes;
            // A test a candidate type's vehicle must also pass, or null for none
            public Func<Vehicle, bool> CandidateCheck;
            // Candidate types the category is created and deleted on only as phantom, never live, or null for none
            public VehicleType[] PhantomOnlyTypes;
            // An off-road car is offered the category too, for an off-road car made amphibious
            public bool OfferedOnOffRoadCars;
            public CreationEffect CreationEffect;
            // Null for a category with no flags submenu
            public CategoryFlagsFacts FlagsMenu;
            public bool HasAdvancedData;
            public bool HasWeaponSlots;

            public abstract bool IsPresent(HandlingData handling);
            public abstract XElement BuildDefaultElement();
            public abstract IEnumerable<bool> BuildLiveRows(RageHandlingEditor script, NativeMenu menu, string keySuffix, EditorContext owner);

            // A saved handling with no handlingType element at all reads as the ordinary non-vertical case
            public bool Matches(XElement sub)
            {
                return (string)sub.Attribute("type") == XmlType && (XmlType != "CFlyingHandlingData" || ((string)sub.Element("handlingType") == "HANDLING_TYPE_VERTICAL_FLYING") == IsVerticalFlying);
            }
        }

        private sealed class CategoryDefinition<TData> : CategoryDefinition
        {
            public readonly HandlingFieldEntry<TData>[] TypedFields;
            // The category's data object on a handling, null while the handling lacks the category
            public readonly Func<HandlingData, TData> Resolve;
            // Null for a category whose default element is just its table at the default values
            private readonly Func<XElement> defaultElementBuilder;

            public CategoryDefinition(HandlingFieldEntry<TData>[] fields, Func<HandlingData, TData> resolver, Func<XElement> defaultBuilder)
            {
                TypedFields = fields;
                Fields = fields;
                Resolve = resolver;
                defaultElementBuilder = defaultBuilder;
            }

            public override bool IsPresent(HandlingData handling) { return Resolve(handling) != null; }
            public override XElement BuildDefaultElement() { return defaultElementBuilder != null ? defaultElementBuilder() : BuildDefaultCategoryXml(XmlType, TypedFields); }
            public override IEnumerable<bool> BuildLiveRows(RageHandlingEditor script, NativeMenu menu, string keySuffix, EditorContext owner) { return script.BuildCategoryMenuItems(menu, Name, TypedFields, Resolve, keySuffix, owner); }
        }

        // Built on first use and not by a static field initializer, since it reads the field tables in another file
        // Static field initializers across partial-class files run in no fixed order, so no static field initializer may read it either
        private static CategoryDefinition[] categoryDefinitions;
        private static CategoryDefinition[] CategoryDefinitions
        {
            get
            {
                if (categoryDefinitions == null) categoryDefinitions = BuildCategoryDefinitions();
                return categoryDefinitions;
            }
        }

        // The definition order is the editor's one order, and it is also the order a saved file writes the categories in
        private static CategoryDefinition[] BuildCategoryDefinitions()
        {
            var definitions = new CategoryDefinition[]
            {
                new CategoryDefinition<CarHandlingData>(CarFields, h => h.CarHandlingData, BuildDefaultCarHandlingXml)
                {
                    Name = CATEGORY_CAR,
                    LongName = "CarHandlingData",
                    XmlType = "CCarHandlingData",
                    PluginCategory = PluginCategory.Car,
                    // The Viseris and the tuner vehicles write into their Car data when they initialize physics, so it can never be removed from them
                    RequiredModelNames = new[]
                    {
                        "viseris", "calico", "comet6", "cypher", "dominator7", "dominator8", "euros", "freightcar2", "futo2", "growler", "jester4", "previon", "remus", "rt3000", "sultan3", "tailgater2", "vectre", "warrener2", "zr350"
                    },
                    CandidateTypes = new[] { VehicleType.Automobile, VehicleType.QuadBike, VehicleType.SubmarineCar, VehicleType.AmphibiousAutomobile, VehicleType.AmphibiousQuadBike },
                    CreationEffect = CreationEffect.Live,
                    FlagsMenu = BuildFlagsFacts("AdvancedFlags", null),
                    HasAdvancedData = true
                },
                new CategoryDefinition<BikeHandlingData>(BikeFields, h => h.BikeHandlingData, null)
                {
                    Name = CATEGORY_BIKE,
                    LongName = "BikeHandlingData",
                    XmlType = "CBikeHandlingData",
                    PluginCategory = PluginCategory.Bike,
                    RequiredTypes = new[] { VehicleType.Motorcycle, VehicleType.Bicycle },
                    CandidateTypes = new[] { VehicleType.Motorcycle, VehicleType.Bicycle, VehicleType.QuadBike, VehicleType.AmphibiousQuadBike },
                    CreationEffect = CreationEffect.Live
                },
                new CategoryDefinition<FlyingHandlingData>(FlyingFields, h => h.FlyingHandlingData, () => BuildDefaultFlyingHandlingXml())
                {
                    Name = CATEGORY_FLYING,
                    LongName = "FlyingHandlingData",
                    XmlType = "CFlyingHandlingData",
                    PluginCategory = PluginCategory.Flying,
                    // Helicopters and blimps require this category otherwise they crash the game once they touch water
                    RequiredTypes = new[] { VehicleType.Helicopter, VehicleType.Blimp },
                    CandidateTypes = new[] { VehicleType.Plane, VehicleType.Helicopter, VehicleType.Blimp },
                    CreationEffect = CreationEffect.Live
                },
                new CategoryDefinition<FlyingHandlingData>(FlyingFields, h => h.VerticalFlyingHandlingData, () => BuildDefaultFlyingHandlingXml(true))
                {
                    Name = CATEGORY_VERTICAL_FLYING,
                    LongName = "VerticalFlyingHandlingData",
                    XmlType = "CFlyingHandlingData",
                    IsVerticalFlying = true,
                    PluginCategory = PluginCategory.VerticalFlying,
                    // Only the plane flight model reads it, and only on a plane with vertical flight mode
                    CandidateTypes = new[] { VehicleType.Plane },
                    CandidateCheck = HasVerticalFlightMode,
                    CreationEffect = CreationEffect.Live
                },
                new CategoryDefinition<SpecialFlightHandlingData>(SpecialFlightFields, h => h.SpecialFlightHandlingData, BuildDefaultSpecialFlightHandlingXml)
                {
                    Name = CATEGORY_SPECIAL_FLIGHT,
                    LongName = "SpecialFlightHandlingData",
                    XmlType = "CSpecialFlightHandlingData",
                    PluginCategory = PluginCategory.SpecialFlight,
                    CandidateTypes = new[] { VehicleType.Automobile, VehicleType.AmphibiousAutomobile, VehicleType.Motorcycle, VehicleType.Bicycle, VehicleType.QuadBike, VehicleType.AmphibiousQuadBike },
                    CreationEffect = CreationEffect.Live,
                    FlagsMenu = BuildFlagsFacts("SpecialFlightFlags", "Flags")
                },
                new CategoryDefinition<BoatHandlingData>(BoatFields, h => h.BoatHandlingData, null)
                {
                    Name = CATEGORY_BOAT,
                    LongName = "BoatHandlingData",
                    XmlType = "CBoatHandlingData",
                    PluginCategory = PluginCategory.Boat,
                    RequiredTypes = new[] { VehicleType.Boat, VehicleType.AmphibiousAutomobile, VehicleType.AmphibiousQuadBike },
                    CandidateTypes = new[] { VehicleType.Boat, VehicleType.AmphibiousAutomobile, VehicleType.AmphibiousQuadBike },
                    OfferedOnOffRoadCars = true,
                    CreationEffect = CreationEffect.ModelLoad
                },
                new CategoryDefinition<SeaPlaneHandlingData>(SeaPlaneFields, h => h.SeaPlaneHandlingData, null)
                {
                    Name = CATEGORY_SEA_PLANE,
                    LongName = "SeaPlaneHandlingData",
                    XmlType = "CSeaPlaneHandlingData",
                    PluginCategory = PluginCategory.SeaPlane,
                    CandidateTypes = new[] { VehicleType.Plane, VehicleType.Helicopter },
                    // A helicopter gets its seaplane state only when it spawns with SeaPlane data, and its physics reads that state unchecked whenever the data exists
                    // So SeaPlane created live on a helicopter spawned without it, an undo of a live delete included, crashes the game on the next frame
                    PhantomOnlyTypes = new[] { VehicleType.Helicopter },
                    CreationEffect = CreationEffect.ModelLoad
                },
                new CategoryDefinition<SubmarineHandlingData>(SubmarineFields, h => h.SubmarineHandlingData, null)
                {
                    Name = CATEGORY_SUBMARINE,
                    LongName = "SubmarineHandlingData",
                    XmlType = "CSubmarineHandlingData",
                    PluginCategory = PluginCategory.Submarine,
                    CandidateTypes = new[] { VehicleType.SubmarineCar, VehicleType.Submarine },
                    CreationEffect = CreationEffect.ModelLoad
                },
                new CategoryDefinition<TrailerHandlingData>(TrailerFields, h => h.TrailerHandlingData, null)
                {
                    Name = CATEGORY_TRAILER,
                    LongName = "TrailerHandlingData",
                    XmlType = "CTrailerHandlingData",
                    PluginCategory = PluginCategory.Trailer,
                    CandidateTypes = new[] { VehicleType.Trailer },
                    CreationEffect = CreationEffect.Respawn
                },
                new CategoryDefinition<VehicleWeaponHandlingData>(WeaponScalarFields, h => h.VehicleWeaponHandlingData, BuildDefaultWeaponHandlingXml)
                {
                    Name = CATEGORY_WEAPON,
                    LongName = "WeaponHandlingData",
                    XmlType = "CVehicleWeaponHandlingData",
                    PluginCategory = PluginCategory.Weapon,
                    ExtraFields = new[] { WeaponDamageScaledField },
                    CreationEffect = CreationEffect.Respawn,
                    HasWeaponSlots = true
                }
            };
            for (int i = 0; i < definitions.Length; i++)
            {
                definitions[i].Index = i;
                if (definitions[i].RequiredModelNames == null) continue;
                definitions[i].RequiredModelHashes = new HashSet<int>();
                foreach (string modelName in definitions[i].RequiredModelNames) definitions[i].RequiredModelHashes.Add(unchecked((int)GTA.StringHash.AtStringHash(modelName)));
            }
            return definitions;
        }

        private static CategoryDefinition ResolveCategoryDefinition(string name)
        {
            CategoryDefinition[] definitions = CategoryDefinitions;
            for (int i = 0; i < definitions.Length; i++) if (definitions[i].Name == name) return definitions[i];
            return null;
        }

        // The definition a saved handling's SubHandlingData item belongs to, or null for a type none of them reads
        private static CategoryDefinition ResolveCategoryDefinitionOfElement(XElement sub)
        {
            CategoryDefinition[] definitions = CategoryDefinitions;
            for (int i = 0; i < definitions.Length; i++) if (definitions[i].Matches(sub)) return definitions[i];
            return null;
        }

        // The game's own test for a plane's vertical flight mode, as the retail executable runs it
        // The Titan 2 never has the mode, and any other plane has it with the model flag, the front nozzle bone, or as the Tula or the Raiju
        private static bool HasVerticalFlightMode(Vehicle vehicle)
        {
            uint modelHash = (uint)vehicle.Model.Hash;
            if (modelHash == StringHash.AtStringHashUtf8(VERTICAL_FLIGHT_EXCLUDED_MODEL_NAME)) return false;
            if (modelHash == StringHash.AtStringHashUtf8(VERTICAL_FLIGHT_TULA_MODEL_NAME) || modelHash == StringHash.AtStringHashUtf8(VERTICAL_FLIGHT_RAIJU_MODEL_NAME)) return true;
            return RawMemory.HasVerticalFlightModeFlag(vehicle) || vehicle.Bones.Contains(VERTICAL_FLIGHT_NOZZLE_BONE_NAME);
        }

        // A plain loop, since Array.IndexOf over an enum array goes through a boxing comparer on .NET Framework 4.8
        // These tests run on every vehicle swap while an edit or handling menu is visible
        private static bool ContainsVehicleType(VehicleType[] types, VehicleType type)
        {
            for (int i = 0; i < types.Length; i++) if (types[i] == type) return true;
            return false;
        }

        // A category the vehicle lacks is not present, and so is a name no definition carries
        private static bool IsCategoryPresent(HandlingData handling, string category)
        {
            CategoryDefinition definition = ResolveCategoryDefinition(category);
            return definition != null && definition.IsPresent(handling);
        }
    }
}
