using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using GTA;
using GTA.Math;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private interface IHandlingMetaXmlMapper
        {
            XDocument BuildHandlingXml(HandlingData handling, string handlingName, HandlingFieldGates gates, ExportOverlay overlay);
            void ApplyHandlingXml(XDocument doc, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, ref ReplayFlags replayFlags, HandlingApplyReport report);
            bool ApplyCategoryElement(string categoryName, XElement element, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, ref ReplayFlags replayFlags, out bool worksUpsideDownForced);
            XElement BuildCategoryElement(string categoryName, HandlingData handling);
        }

        // Owns the handling.meta save and apply walk and its per-apply counts, so no other file can read a count mid-walk or reset it
        // The walk writes only the live handling, and the script finishes the export-only side from its report
        private sealed class HandlingMetaXmlMapper : IHandlingMetaXmlMapper
        {
            private readonly ILogger logger;
            // Both are set by BuildMenu after this class is built, so each is read at the moment it is needed
            private readonly Func<bool> isWeaponModTypeSupported;
            private readonly Func<int> readTurretSlotCount;
            // Per-apply tallies for invalid and absent fields, reported together after the walk
            private int handlingFieldsInvalid;
            private int handlingFieldsAbsent;
            // Set when a Car's AdvancedData entry count changes, before the count write, so the caller releases the AdvancedData assertions after the walk
            private bool advancedDataCountChanged;
            // Set by the Car walk when the document's AdvancedData holds more items than the array's capacity, for the script to request the room for
            private XElement[] advancedDataOverflowItems;
            private int advancedDataWrittenCount;
            // Set when a SpecialFlight apply had to turn SF_WORKS_UPSIDE_DOWN back on for a vehicle with no hover bone, so the caller reports it
            private bool worksUpsideDownForced;

            // Applies one sub-handling category's element to a handling, adding the follow-up work every changed value asks for to replayFlags as it goes
            private delegate void ApplyCategoryHandler(XElement element, HandlingData handling, Vehicle vehicle, ref ReplayFlags replayFlags);

            // One row per sub-handling category, stored at its definition's index, so the write order is the definition order
            // The single registration point for BuildHandlingXml's category block, ApplyHandlingXml's type dispatch, and the absent categories its report lists
            private class SubHandlingXmlCategory
            {
                public CategoryDefinition Definition;
                public Func<HandlingData, ExportOverlay, XElement> Build;
                public ApplyCategoryHandler Apply;
                // Optional, replaces the plain phantom element clone in a save (Weapon strips and trims its phantom output)
                public Func<ExportOverlay, XElement> BuildPhantomOutput;
            }

            // A definition left without a row has a null slot, and every walk skips it
            private readonly SubHandlingXmlCategory[] subHandlingXmlCategories;

            private SubHandlingXmlCategory FindSubHandlingXmlCategory(XElement sub)
            {
                CategoryDefinition definition = ResolveCategoryDefinitionOfElement(sub);
                return definition != null ? subHandlingXmlCategories[definition.Index] : null;
            }

            private SubHandlingXmlCategory FindSubHandlingXmlCategoryByName(string categoryName)
            {
                CategoryDefinition definition = ResolveCategoryDefinition(categoryName);
                return definition != null ? subHandlingXmlCategories[definition.Index] : null;
            }

            private static bool DocumentHasCategory(XElement[] subs, SubHandlingXmlCategory category)
            {
                foreach (XElement sub in subs) if (category.Definition.Matches(sub)) return true;
                return false;
            }

            // The category table is built here, since a static table cannot hold delegates that call instance methods
            public HandlingMetaXmlMapper(ILogger mapperLogger, Func<bool> weaponModTypeSupportedReader, Func<int> turretSlotCountReader)
            {
                logger = mapperLogger;
                isWeaponModTypeSupported = weaponModTypeSupportedReader;
                readTurretSlotCount = turretSlotCountReader;
                var rows = new[]
                {
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_CAR),
                        Build = (h, overlay) => BuildCarHandlingXml(h.CarHandlingData, overlay),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCarHandlingXml(element, h.CarHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_BIKE),
                        Build = (h, overlay) => BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_BIKE).XmlType, BikeFields, h.BikeHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCategoryXml(element, BikeFields, h.BikeHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_FLYING),
                        Build = (h, overlay) => BuildFlyingHandlingXml(h.FlyingHandlingData, false),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyFlyingHandlingXml(element, h.FlyingHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_VERTICAL_FLYING),
                        Build = (h, overlay) => BuildFlyingHandlingXml(h.VerticalFlyingHandlingData, true),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyFlyingHandlingXml(element, h.VerticalFlyingHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT),
                        Build = (h, overlay) => BuildSpecialFlightHandlingXml(h.SpecialFlightHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplySpecialFlightHandlingXml(element, h.SpecialFlightHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_BOAT),
                        Build = (h, overlay) => BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_BOAT).XmlType, BoatFields, h.BoatHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCategoryXml(element, BoatFields, h.BoatHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_SEA_PLANE),
                        Build = (h, overlay) => BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_SEA_PLANE).XmlType, SeaPlaneFields, h.SeaPlaneHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCategoryXml(element, SeaPlaneFields, h.SeaPlaneHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_SUBMARINE),
                        Build = (h, overlay) => BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_SUBMARINE).XmlType, SubmarineFields, h.SubmarineHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCategoryXml(element, SubmarineFields, h.SubmarineHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_TRAILER),
                        Build = (h, overlay) => BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_TRAILER).XmlType, TrailerFields, h.TrailerHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyCategoryXml(element, TrailerFields, h.TrailerHandlingData, vehicle, ref replayFlags) },
                    new SubHandlingXmlCategory { Definition = ResolveCategoryDefinition(CATEGORY_WEAPON),
                        Build = (h, overlay) => BuildWeaponHandlingXml(h.VehicleWeaponHandlingData),
                        Apply = (XElement element, HandlingData h, Vehicle vehicle, ref ReplayFlags replayFlags) => ApplyWeaponHandlingXml(element, h.VehicleWeaponHandlingData, vehicle, ref replayFlags),
                        BuildPhantomOutput = BuildPhantomWeaponOutput },
                };
                CategoryDefinition[] definitions = CategoryDefinitions;
                subHandlingXmlCategories = new SubHandlingXmlCategory[definitions.Length];
                for (int i = 0; i < rows.Length; i++) subHandlingXmlCategories[rows[i].Definition.Index] = rows[i];
                for (int i = 0; i < definitions.Length; i++) if (subHandlingXmlCategories[i] == null) logger.Log(Dbg.Level.Error, Dbg.HandlingCategoryRowMissing(definitions[i].Name));
            }

            // gates.RocketBoost False leaves out every RequiresRocketBoost base field
            // gates.RearMountedTurret False leaves out TurretPitchLimitData, live or export-only
            public XDocument BuildHandlingXml(HandlingData handling, string handlingName, HandlingFieldGates gates, ExportOverlay overlay)
            {
                var item = new XElement(ITEM_TAG, new XAttribute("type", "CHandlingData"));
                item.Add(new XElement(HANDLING_NAME_TAG, handlingName));
                foreach (var entry in BaseFields)
                {
                    if (entry.RequiresRocketBoost && !gates.RocketBoost) continue;
                    BuildEntryField(item, entry, handling);
                }
                item.Add(new XElement("AIHandling", handling.AIHandlingHash.ToString()));
                item.Add(new XElement(MODEL_FLAGS_TAG, ((uint)handling.ModelFlags).ToString("X8")));
                item.Add(new XElement(HANDLING_FLAGS_TAG, ((uint)handling.HandlingFlags).ToString("X8")));
                item.Add(new XElement(DAMAGE_FLAGS_TAG, ((uint)handling.DamageFlags).ToString("X8")));

                // Only categories the vehicle actually has are written, then NULL items pad the block to SUBHANDLING_ITEM_COUNT, and 4 or more categories are written as-is
                // A category in the overlay's toggled set flips from this default: Present-and-toggled is omitted, same as absent
                // Absent-and-toggled is created with plain type-default values instead of the vehicle's own live data
                HashSet<string> toggled = overlay.Toggled;
                var subHandling = new XElement(SUB_HANDLING_DATA_TAG);
                foreach (SubHandlingXmlCategory category in subHandlingXmlCategories)
                {
                    if (category == null) continue;
                    string categoryName = category.Definition.Name;
                    if (category.Definition.IsPresent(handling)) { if (!toggled.Contains(categoryName)) subHandling.Add(category.Build(handling, overlay)); }
                    else if (toggled.Contains(categoryName)) subHandling.Add(category.BuildPhantomOutput != null ? category.BuildPhantomOutput(overlay) : overlay.GetOrCreateCategoryElement(categoryName));
                }
                if (!gates.RearMountedTurret)
                {
                    string weaponXmlType = ResolveCategoryDefinition(CATEGORY_WEAPON).XmlType;
                    foreach (XElement sub in subHandling.Elements(ITEM_TAG))
                    {
                        XElement pitchLimits = (string)sub.Attribute("type") == weaponXmlType ? sub.Element(TURRET_PITCH_LIMIT_TAG) : null;
                        if (pitchLimits != null) pitchLimits.Remove();
                    }
                }
                while (CountItemElements(subHandling) < SUBHANDLING_ITEM_COUNT) subHandling.Add(NullItem());
                item.Add(subHandling);
                // Base CHandlingData field immediately following m_subHandlingData in the real struct, written here to match
                // It is gated on the vehicle's live Weapon subdata, since the field only matters in that context, and excluded the same way the Weapon submenu itself is
                // An unsupported build reads 0 for this raw field, which would save in place of the real value and write it back on a supported build
                if (handling.VehicleWeaponHandlingData != null && !toggled.Contains(CATEGORY_WEAPON) && RawMemory.RawOffsetsSupported) SetVal(item, "fWeaponDamageScaledToVehHealthMult", RawMemory.ReadRawFloat(handling.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult));
                else if (handling.VehicleWeaponHandlingData == null && toggled.Contains(CATEGORY_WEAPON))
                {
                    string storedDamageText = GetValAttr(overlay.GetOrCreateCategoryElement(CATEGORY_WEAPON), "fWeaponDamageScaledToVehHealthMult");
                    float phantomWeaponDamageValue = 0.0f;
                    if (storedDamageText != null) ParseFiniteFloat(storedDamageText, out phantomWeaponDamageValue);
                    SetVal(item, "fWeaponDamageScaledToVehHealthMult", phantomWeaponDamageValue);
                }
                return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("CHandlingDataMgr", new XElement(HANDLING_DATA_TAG, item)));
            }

            // The overlay's pending AdvancedData stands in for the real entries of a Car that has none
            private XElement BuildCarHandlingXml(CarHandlingData data, ExportOverlay overlay)
            {
                if (data == null) return NullItem();
                var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(CATEGORY_CAR).XmlType));
                foreach (var entry in CarFields) BuildEntryField(element, entry, data);
                element.Add(new XElement(ADVANCED_FLAGS_TAG, ((uint)data.AdvancedFlags).ToString("X8")));
                // Every real entry, since GetAdvancedDataCount already rejects a count past the sanity ceiling or the capacity
                int advancedDataCount = RawMemory.GetAdvancedDataCount(data.Parent);
                if (advancedDataCount == 0 && overlay.PendingAdvancedData != null) element.Add(new XElement(overlay.PendingAdvancedData));
                else if (advancedDataCount > 0)
                {
                    var advancedDataElement = new XElement(ADVANCED_DATA_TAG);
                    for (int i = 0; i < advancedDataCount; i++)
                    {
                        IntPtr elementAddress = RawMemory.GetAdvancedDataElementAddress(data.Parent, i);
                        if (elementAddress == IntPtr.Zero) continue;
                        var item = new XElement(ITEM_TAG, new XAttribute("type", "CAdvancedData"));
                        SetVal(item, "Slot", RawMemory.ReadRawInt(elementAddress, AdvancedDataLayout.ElementSlot));
                        SetVal(item, "Index", RawMemory.ReadRawInt(elementAddress, AdvancedDataLayout.ElementIndex));
                        SetVal(item, "Value", RawMemory.ReadRawFloat(elementAddress, AdvancedDataLayout.ElementValue));
                        advancedDataElement.Add(item);
                    }
                    if (advancedDataElement.HasElements) element.Add(advancedDataElement);
                }
                return element;
            }

            private XElement BuildWeaponHandlingXml(VehicleWeaponHandlingData data)
            {
                if (data == null) return NullItem();
                var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(CATEGORY_WEAPON).XmlType));
                int weaponCount = SetHashArray(element, WEAPON_HASH_ARRAY_TAG, data.WeaponHash);
                VehicleSeat[] seats = data.WeaponSeats;
                int[] rawSeats = new int[seats.Length];
                for (int i = 0; i < seats.Length; i++) rawSeats[i] = (int)seats[i] + 1;
                SetIntArray(element, WEAPON_SEAT_ARRAY_TAG, rawSeats, Math.Min(weaponCount, rawSeats.Length));
                if (isWeaponModTypeSupported())
                {
                    VehicleModType[] modTypes = data.WeaponVehicleModType;
                    var rawModTypes = new int[modTypes.Length];
                    for (int i = 0; i < modTypes.Length; i++) rawModTypes[i] = (int)modTypes[i];
                    SetIntArray(element, WEAPON_MOD_TYPE_ARRAY_TAG, rawModTypes, Math.Min(weaponCount, rawModTypes.Length));
                }
                int turretCount = data.TurretPitchMin.Length;
                // Capped to weaponCount, matching WeaponSeats and vehicleModType above
                // The live turret arrays are sized to the build's fixed turretSlotCount regardless of how many weapon slots the vehicle uses
                // An unused trailing slot's stray non-zero value would otherwise survive as a no-op saved entry
                foreach (TurretFieldEntry field in TurretFields)
                {
                    // An unsupported build reads 0 for every raw field, which would save a block of zeros in place of the real values and write them back on a supported build
                    if (field.RawOffset && !RawMemory.RawOffsetsSupported) continue;
                    SetFloatArray(element, field.Tag, ReadTurretFloatArray(field, data, turretCount), 0.0f, weaponCount);
                }
                if (RawMemory.RawOffsetsSupported)
                {
                    var pitchLimits = new List<float[]>();
                    for (int i = 0; i < Math.Min(weaponCount, turretCount); i++) pitchLimits.Add(RawMemory.ReadTurretPitchLimits(data.MemoryAddress, i));
                    element.Add(BuildTurretPitchLimitElement(pitchLimits));
                }
                foreach (var entry in WeaponScalarFields) BuildEntryField(element, entry, data);
                return element;
            }

            private XElement BuildSpecialFlightHandlingXml(SpecialFlightHandlingData data)
            {
                XElement specialFlightElement = BuildCategoryXml(ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT).XmlType, SpecialFlightFields, data);
                // An unsupported build reads 0, which would save cleared flags in place of the real value
                if (RawMemory.SpecialFlightFlagsSupported) specialFlightElement.Add(new XElement(SPECIAL_FLIGHT_FLAGS_TAG, RawMemory.ReadRawUInt(data.MemoryAddress, SpecialFlightLayout.Flags).ToString("X8")));
                return specialFlightElement;
            }

            private void ApplySpecialFlightHandlingXml(XElement element, SpecialFlightHandlingData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                ApplyCategoryXml(element, SpecialFlightFields, data, vehicle, ref replayFlags);
                ApplyFlagFamily(element, FindFlagFamilyByElementCategory(CATEGORY_SPECIAL_FLIGHT), data.MemoryAddress, ref replayFlags);
                if (RawMemory.ForceWorksUpsideDownWithoutHoverBone(data.MemoryAddress, vehicle)) worksUpsideDownForced = true;
            }

            private XElement BuildPhantomWeaponOutput(ExportOverlay overlay)
            {
                // fWeaponDamageScaledToVehHealthMult is stored as an extra child of the phantom element (WirePhantomFloat's own storage shape), though it is base-level
                // It is stripped from a clone here so the real sub-block output never carries it
                // BuildHandlingXml reads it back separately for the base-level write
                XElement weaponPhantomOutput = new XElement(overlay.GetOrCreateCategoryElement(CATEGORY_WEAPON));
                XElement weaponDamageChild = weaponPhantomOutput.Element("fWeaponDamageScaledToVehHealthMult");
                if (weaponDamageChild != null) weaponDamageChild.Remove();
                // WeaponSeats and vehicleModType grow lazily to whatever index was last written and never shrink back on their own
                // They are trimmed to the real slot count (uWeaponHash's item count, always gap-free), so an unused trailing slot's stray value never reaches the saved file
                XElement weaponPhantomHashArray = weaponPhantomOutput.Element(WEAPON_HASH_ARRAY_TAG);
                int weaponPhantomSlotCount = weaponPhantomHashArray != null ? CountItemElements(weaponPhantomHashArray) : 0;
                TrimIntArrayElement(weaponPhantomOutput, WEAPON_SEAT_ARRAY_TAG, weaponPhantomSlotCount);
                if (isWeaponModTypeSupported()) TrimIntArrayElement(weaponPhantomOutput, WEAPON_MOD_TYPE_ARRAY_TAG, weaponPhantomSlotCount);
                foreach (TurretFieldEntry field in TurretFields) TrimFloatArrayElement(weaponPhantomOutput, field.Tag, weaponPhantomSlotCount);
                ResizeTurretPitchLimitItems(weaponPhantomOutput, Math.Min(weaponPhantomSlotCount, readTurretSlotCount()));
                return weaponPhantomOutput;
            }

            // A changed field the game reads only at spawn adds the respawn flag to replayFlags, and a changed drivetrain field or offroad flag adds the spawn refresh flag
            // gates.RocketBoost False skips every RequiresRocketBoost base field, and gates.RearMountedTurret False skips TurretPitchLimitData on a live Weapon category
            // Neither is counted as absent or invalid, and each logs one INFO line when the document carried what it skipped
            public void ApplyHandlingXml(XDocument doc, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, ref ReplayFlags replayFlags, HandlingApplyReport report)
            {
                handlingFieldsInvalid = 0;
                handlingFieldsAbsent = 0;
                advancedDataCountChanged = false;
                advancedDataOverflowItems = null;
                advancedDataWrittenCount = 0;
                worksUpsideDownForced = false;
                try
                {
                    XElement handlingDataElement = doc.Root.Element(HANDLING_DATA_TAG);
                    XElement item = handlingDataElement != null ? handlingDataElement.Element(ITEM_TAG) : null;
                    if (item == null) return;
                    report.HandlingItem = item;
                    bool rocketBoostSkipped = false;
                    foreach (var entry in BaseFields)
                    {
                        if (entry.RequiresRocketBoost && !gates.RocketBoost)
                        {
                            if (item.Element(entry.Tag) != null) rocketBoostSkipped = true;
                            continue;
                        }
                        ApplyEntryField(item, entry, handling, vehicle, ref replayFlags);
                    }
                    if (rocketBoostSkipped) logger.Log(Dbg.Level.Info, Dbg.HandlingRocketBoostSkipped());
                    foreach (FlagFamily family in FlagFamilies) if (family.ElementCategory == null) ApplyFlagFamily(item, family, family.ResolveAddress(handling), ref replayFlags);
                    string aiHandlingValue = (string)item.Element("AIHandling");
                    if (aiHandlingValue == null) handlingFieldsAbsent++;
                    else
                    {
                        AIHandlingHash targetHash = (AIHandlingHash)GTA.StringHash.AtStringHash(aiHandlingValue);
                        CAIHandlingInfo aiHandlingInfo = CAIHandlingInfo.GetByHash(targetHash);
                        if (aiHandlingInfo.IsValid) handling.CAIHandlingInfo = aiHandlingInfo;
                        else
                        {
                            handlingFieldsInvalid++;
                            logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid("AIHandling", aiHandlingValue));
                        }
                    }
                    XElement subHandling = item.Element(SUB_HANDLING_DATA_TAG);
                    if (subHandling == null) return;
                    XElement[] subs = ListItemElements(subHandling);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        XAttribute typeAttr = subs[i].Attribute("type");
                        if (typeAttr == null)
                        {
                            handlingFieldsInvalid++;
                            logger.Log(Dbg.Level.Warn, Dbg.HandlingSubHandlingTypeUnrecognized("(missing type attribute)"));
                            continue;
                        }
                        // NULL items are the padding BuildHandlingXml writes to fill the block to SUBHANDLING_ITEM_COUNT, and they carry no data
                        if (typeAttr.Value == "NULL") continue;
                        SubHandlingXmlCategory category = FindSubHandlingXmlCategory(subs[i]);
                        if (category == null)
                        {
                            handlingFieldsInvalid++;
                            logger.Log(Dbg.Level.Warn, Dbg.HandlingSubHandlingTypeUnrecognized(typeAttr.Value));
                            continue;
                        }
                        if (category.Definition.IsPresent(handling)) ApplyCategoryElementWithGates(category, subs[i], handling, vehicle, gates, ref replayFlags);
                        else report.AbsentCategoriesCarried.Add(new KeyValuePair<string, XElement>(category.Definition.Name, subs[i]));
                    }
                    foreach (SubHandlingXmlCategory category in subHandlingXmlCategories)
                    {
                        if (category == null || DocumentHasCategory(subs, category)) continue;
                        if (category.Definition.IsPresent(handling)) report.PresentCategoriesNotCarried.Add(category.Definition.Name);
                        else report.AbsentCategoriesNotCarried.Add(category.Definition.Name);
                    }
                    report.SubHandlingWalked = true;
                    // Base CHandlingData field written after SubHandlingData in BuildHandlingXml, read back here
                    // It is gated the same way, only when the target vehicle has live Weapon subdata
                    if (handling.VehicleWeaponHandlingData != null)
                    {
                        float weaponDamageValue;
                        if (GetFloat(item, "fWeaponDamageScaledToVehHealthMult", out weaponDamageValue)) RawMemory.WriteRawFloat(handling.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult, weaponDamageValue);
                    }
                }
                finally
                {
                    report.FieldsInvalid = handlingFieldsInvalid;
                    report.FieldsAbsent = handlingFieldsAbsent;
                    report.AdvancedDataCountChanged = advancedDataCountChanged;
                    report.AdvancedDataItems = advancedDataOverflowItems;
                    report.AdvancedDataWrittenCount = advancedDataWrittenCount;
                    report.WorksUpsideDownForced = worksUpsideDownForced;
                }
            }

            private void ApplyCategoryElementWithGates(SubHandlingXmlCategory category, XElement element, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, ref ReplayFlags replayFlags)
            {
                XElement applied = element;
                if (!gates.RearMountedTurret && category.Definition.Name == CATEGORY_WEAPON && applied.Element(TURRET_PITCH_LIMIT_TAG) != null)
                {
                    // An absent block leaves the live limits untouched, so a copy without it applies everything else
                    applied = new XElement(applied);
                    applied.Element(TURRET_PITCH_LIMIT_TAG).Remove();
                    logger.Log(Dbg.Level.Info, Dbg.HandlingTurretPitchLimitsSkipped());
                }
                category.Apply(applied, handling, vehicle, ref replayFlags);
            }

            // Applies one category's element to a handling that holds that category, under the same gates as a whole applied document
            // Returns False and applies nothing when the name is unknown or the handling lacks the category
            public bool ApplyCategoryElement(string categoryName, XElement element, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, ref ReplayFlags replayFlags, out bool worksUpsideDownForcedOut)
            {
                handlingFieldsInvalid = 0;
                handlingFieldsAbsent = 0;
                worksUpsideDownForced = false;
                worksUpsideDownForcedOut = false;
                SubHandlingXmlCategory category = FindSubHandlingXmlCategoryByName(categoryName);
                if (category == null || !category.Definition.IsPresent(handling)) return false;
                ApplyCategoryElementWithGates(category, element, handling, vehicle, gates, ref replayFlags);
                worksUpsideDownForcedOut = worksUpsideDownForced;
                return true;
            }

            // The live category as one element with no export-only state mixed in, or null when the name is unknown or the handling lacks the category
            public XElement BuildCategoryElement(string categoryName, HandlingData handling)
            {
                SubHandlingXmlCategory category = FindSubHandlingXmlCategoryByName(categoryName);
                return category != null && category.Definition.IsPresent(handling) ? category.Build(handling, ExportOverlay.None) : null;
            }

            // Applying a handling only: Missing elements increment the absent count, and malformed values log a WARN and increment the invalid count
            private bool GetFloat(XElement parent, string name, out float value)
            {
                value = 0.0f;
                string s = GetValAttr(parent, name);
                if (s == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                if (ParseFiniteFloat(s, out value)) return true;
                handlingFieldsInvalid++;
                logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, s));
                return false;
            }

            private bool GetInt(XElement parent, string name, out int value)
            {
                value = 0;
                string s = GetValAttr(parent, name);
                if (s == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
                handlingFieldsInvalid++;
                logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, s));
                return false;
            }

            private bool GetVec(XElement parent, string name, out Vector3 value)
            {
                value = Vector3.Zero;
                XElement element = parent.Element(name);
                if (element == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                float x, y, z;
                XAttribute xAttribute = element.Attribute("x"), yAttribute = element.Attribute("y"), zAttribute = element.Attribute("z");
                if (xAttribute == null || yAttribute == null || zAttribute == null || !ParseFiniteFloat(xAttribute.Value, out x) || !ParseFiniteFloat(yAttribute.Value, out y) || !ParseFiniteFloat(zAttribute.Value, out z))
                {
                    handlingFieldsInvalid++;
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, element.ToString()));
                    return false;
                }
                value = new Vector3(x, y, z);
                return true;
            }

            // All-or-nothing on a malformed entry, matching GetIntArray and GetFloatArray: The whole array is discarded and the live value stays untouched
            // A corrupted sentinel (VehicleWeaponHash.Invalid) is never substituted into a live weapon slot while still reporting success
            // A single typo or corrupted hash string in a saved handling must never silently disable a weapon slot
            private bool GetHashArray(XElement parent, string name, out VehicleWeaponHash[] values)
            {
                values = null;
                XElement element = parent.Element(name);
                if (element == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                XElement[] items = ListItemElements(element);
                var parsedValues = new VehicleWeaponHash[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    string text = items[i].Value;
                    if (string.IsNullOrEmpty(text))
                    {
                        parsedValues[i] = (VehicleWeaponHash)0;
                        continue;
                    }
                    int index = Array.FindIndex(WeaponHashTable, entry => entry.NativeName == text);
                    if (index >= 0)
                    {
                        parsedValues[i] = (VehicleWeaponHash)WeaponHashTable[index].Hash;
                        continue;
                    }
                    VehicleWeaponHash parsed;
                    if (Enum.TryParse(text, out parsed))
                    {
                        parsedValues[i] = parsed;
                        continue;
                    }
                    handlingFieldsInvalid++;
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, text));
                    return false;
                }
                values = parsedValues;
                return true;
            }

            private bool GetIntArray(XElement parent, string name, out int[] values)
            {
                values = null;
                XElement element = parent.Element(name);
                if (element == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                string[] tokens = element.Value.Split(ArrayValueDelimiters, StringSplitOptions.RemoveEmptyEntries);
                values = new int[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) continue;
                    handlingFieldsInvalid++;
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, tokens[i]));
                    values = null;
                    return false;
                }
                return true;
            }

            private bool GetFloatArray(XElement parent, string name, out float[] values)
            {
                values = null;
                XElement element = parent.Element(name);
                if (element == null)
                {
                    handlingFieldsAbsent++;
                    return false;
                }
                string[] tokens = element.Value.Split(ArrayValueDelimiters, StringSplitOptions.RemoveEmptyEntries);
                values = new float[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (ParseFiniteFloat(tokens[i], out values[i])) continue;
                    handlingFieldsInvalid++;
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(name, tokens[i]));
                    values = null;
                    return false;
                }
                return true;
            }

            // A changed field adds the replay flags its row asks for, and the caller acts on them with its own resolved vehicle (ConsumeReplayRefreshAndRespawn)
            // A written field runs its row's Recompute with that same vehicle
            // On a build whose raw offsets are unsupported a raw row reads 0 and its write is skipped, so applying it would only report a change that never happened
            private void ApplyEntryField<TData>(XElement element, HandlingFieldEntry<TData> entry, TData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                if (entry.RawOffset && !RawMemory.RawOffsetsSupported) return;
                if (entry.RequiresSpecialFlightFlagsBuild && !RawMemory.SpecialFlightFlagsSupported) return;
                float f;
                int n;
                Vector3 v;
                bool changed = false;
                bool written = false;
                if (entry.SetFloat != null)
                {
                    if (GetFloat(element, entry.Tag, out f))
                    {
                        f = ApplyHardBounds(entry, f, 0);
                        changed = !SameAtSaveResolution(entry.GetFloat(data), f);
                        entry.SetFloat(data, f);
                        written = true;
                    }
                }
                else if (entry.SetVec != null)
                {
                    if (GetVec(element, entry.Tag, out v))
                    {
                        v = new Vector3(ApplyHardBounds(entry, v.X, 0), ApplyHardBounds(entry, v.Y, 0), ApplyHardBounds(entry, v.Z, 0));
                        changed = !SameAtSaveResolution(entry.GetVec(data), v);
                        entry.SetVec(data, v);
                        written = true;
                    }
                }
                else if (entry.SetInt != null)
                {
                    if (GetInt(element, entry.Tag, out n))
                    {
                        n = ApplyHardBounds(entry, n);
                        changed = entry.GetInt(data) != n;
                        entry.SetInt(data, n);
                        written = true;
                    }
                }
                if (written && entry.Recompute != null) entry.Recompute(data, vehicle);
                if (changed) replayFlags |= ResolveReplayFlags(entry.RefreshesVehicle, entry.RequiresRespawn);
            }

            private void ApplyCategoryXml<TData>(XElement element, HandlingFieldEntry<TData>[] fields, TData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                if (data == null) return;
                foreach (var entry in fields) ApplyEntryField(element, entry, data, vehicle, ref replayFlags);
            }

            private void ApplyFlagFamily(XElement parent, FlagFamily family, IntPtr address, ref ReplayFlags replayFlags)
            {
                if (family.IsVersionGated) ApplyFlagsField(parent, family.XmlTag, address, family.Offset, family.Entries, family.ReadFlags, RawMemory.WriteRawUInt, ref replayFlags);
                else ApplyFlagsField(parent, family.XmlTag, address, family.Offset, family.Entries, ref replayFlags);
            }

            // entries names the category's flags, so a changed bit that needs a respawn or a spawn refresh adds that replay flag to replayFlags
            private void ApplyFlagsField(XElement parent, string tag, IntPtr address, int offset, FlagNameEntry[] entries, ref ReplayFlags replayFlags) { ApplyFlagsField(parent, tag, address, offset, entries, RawMemory.ReadFlagBits, RawMemory.WriteFlagBits, ref replayFlags); }

            // SpecialFlightHandlingFlags is version-gated separately, so its strFlags element reads and writes through ReadRawUInt and WriteRawUInt
            // Every other flag category uses the always-available ReadFlagBits and WriteFlagBits
            private void ApplyFlagsField(XElement parent, string tag, IntPtr address, int offset, FlagNameEntry[] entries, Func<IntPtr, int, uint> reader, Action<IntPtr, int, uint> writer, ref ReplayFlags replayFlags)
            {
                XElement element = parent.Element(tag);
                if (element != null)
                {
                    uint parsedFlags;
                    if (uint.TryParse(element.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsedFlags))
                    {
                        uint changedBits = reader(address, offset) ^ parsedFlags;
                        if ((changedBits & BuildFlagMask(entries, entry => entry.RequiresRespawn)) != 0) replayFlags |= ReplayFlags.Respawn;
                        if ((changedBits & BuildFlagMask(entries, entry => entry.RefreshesVehicle)) != 0) replayFlags |= ReplayFlags.SpawnRefresh;
                        writer(address, offset, parsedFlags);
                    }
                    else
                    {
                        handlingFieldsInvalid++;
                        logger.Log(Dbg.Level.Warn, Dbg.HandlingFieldInvalid(tag, element.Value));
                    }
                }
                else handlingFieldsAbsent++;
            }

            private void ApplyCarHandlingXml(XElement element, CarHandlingData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                if (data == null) return;
                foreach (var entry in CarFields) ApplyEntryField(element, entry, data, vehicle, ref replayFlags);
                // Without this the flags would stay whatever the live vehicle already had, regardless of what the applied handling specified
                ApplyFlagFamily(element, FindFlagFamilyByElementCategory(CATEGORY_CAR), data.MemoryAddress, ref replayFlags);
                // The live entry count follows the document within the array's capacity: A Car item with no AdvancedData element means no entries
                // That is also how BuildCarHandlingXml writes a car that has none. Items past its capacity count as absent unless the script grows the array for them
                XElement advancedDataElement = element.Element(ADVANCED_DATA_TAG);
                XElement[] advancedDataItems = advancedDataElement != null ? ListItemElements(advancedDataElement) : new XElement[0];
                HandlingData handling = data.Parent;
                int liveCount = RawMemory.GetAdvancedDataCount(handling);
                int targetCount = Math.Min(advancedDataItems.Length, RawMemory.GetAdvancedDataCapacity(handling));
                if (targetCount != liveCount)
                {
                    advancedDataCountChanged = true;
                    if (!RawMemory.SetAdvancedDataCount(handling, targetCount)) targetCount = Math.Min(targetCount, liveCount);
                }
                handlingFieldsAbsent += advancedDataItems.Length - targetCount;
                if (advancedDataItems.Length > targetCount)
                {
                    advancedDataOverflowItems = advancedDataItems;
                    advancedDataWrittenCount = targetCount;
                }
                for (int i = 0; i < targetCount; i++)
                {
                    XElement item = advancedDataItems[i];
                    IntPtr elementAddress = RawMemory.GetAdvancedDataElementAddress(handling, i);
                    // An entry past the old count holds whatever an earlier delete left behind
                    // So a member the file omits or garbles is written as the 0 its parse leaves, while an existing entry keeps its own value for that member
                    bool newEntry = i >= liveCount;
                    int slot, index;
                    float value;
                    if (GetInt(item, "Slot", out slot) || newEntry) RawMemory.WriteRawInt(elementAddress, AdvancedDataLayout.ElementSlot, slot);
                    if (GetInt(item, "Index", out index) || newEntry) RawMemory.WriteRawInt(elementAddress, AdvancedDataLayout.ElementIndex, index);
                    if (GetFloat(item, "Value", out value) || newEntry) RawMemory.WriteRawFloat(elementAddress, AdvancedDataLayout.ElementValue, value);
                }
            }

            private void ApplyFlyingHandlingXml(XElement element, FlyingHandlingData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                if (data == null) return;
                foreach (var entry in FlyingFields) ApplyEntryField(element, entry, data, vehicle, ref replayFlags);
            }

            // Trimmed or padded to the live array's length in both directions, so a handling saved on a different game version never overruns or underfills them
            // Weapon slots are 4 or 6 and turret slots 3 or 6 by version
            // The game sets up vehicle weapons at spawn, so every changed array adds the respawn flag to replayFlags
            private void ApplyWeaponHandlingXml(XElement element, VehicleWeaponHandlingData data, Vehicle vehicle, ref ReplayFlags replayFlags)
            {
                if (data == null) return;
                VehicleWeaponHash[] savedHashes;
                if (GetHashArray(element, WEAPON_HASH_ARRAY_TAG, out savedHashes))
                {
                    VehicleWeaponHash[] padded = PadOrTrim(savedHashes, data.WeaponHash.Length, (VehicleWeaponHash)0);
                    if (!ArraysEqual(data.WeaponHash, padded)) replayFlags |= ReplayFlags.Respawn;
                    data.WeaponHash = padded;
                }
                int[] savedRawSeats;
                if (GetIntArray(element, WEAPON_SEAT_ARRAY_TAG, out savedRawSeats))
                {
                    int[] padded = PadOrTrim(savedRawSeats, data.WeaponSeats.Length, 0);
                    VehicleSeat[] seats = new VehicleSeat[padded.Length];
                    for (int i = 0; i < seats.Length; i++) seats[i] = (VehicleSeat)(padded[i] - 1);
                    if (!ArraysEqual(data.WeaponSeats, seats)) replayFlags |= ReplayFlags.Respawn;
                    data.WeaponSeats = seats;
                }
                int[] savedRawModTypes;
                if (isWeaponModTypeSupported() && GetIntArray(element, WEAPON_MOD_TYPE_ARRAY_TAG, out savedRawModTypes))
                {
                    int[] padded = PadOrTrim(savedRawModTypes, data.WeaponVehicleModType.Length, 0);
                    VehicleModType[] modTypes = new VehicleModType[padded.Length];
                    for (int i = 0; i < modTypes.Length; i++) modTypes[i] = (VehicleModType)padded[i];
                    if (!ArraysEqual(data.WeaponVehicleModType, modTypes)) replayFlags |= ReplayFlags.Respawn;
                    data.WeaponVehicleModType = modTypes;
                }
                int turretCount = data.TurretPitchMin.Length;
                foreach (TurretFieldEntry field in TurretFields)
                {
                    // With raw offsets unsupported the reads return 0 and the writes are skipped, so comparing would request a respawn for a change that never happened
                    if (field.RawOffset && !RawMemory.RawOffsetsSupported) continue;
                    float[] savedFloats;
                    if (!GetFloatArray(element, field.Tag, out savedFloats)) continue;
                    float[] current = ReadTurretFloatArray(field, data, turretCount);
                    float[] padded = PadOrTrim(savedFloats, current.Length, 0.0f);
                    if (!ArraysEqual(current, padded)) replayFlags |= ReplayFlags.Respawn;
                    WriteTurretFloatArray(field, data, padded);
                }
                if (RawMemory.RawOffsetsSupported)
                {
                    // An absent block leaves the live limits untouched, matching the float arrays, and a present one pads missing entries with the init defaults
                    XElement pitchLimitElement = element.Element(TURRET_PITCH_LIMIT_TAG);
                    if (pitchLimitElement != null)
                    {
                        XElement[] limitItems = ListItemElements(pitchLimitElement);
                        for (int i = 0; i < turretCount; i++)
                        {
                            float[] saved = ReadTurretPitchLimitItem(i < limitItems.Length ? limitItems[i] : null);
                            if (!ArraysEqual(RawMemory.ReadTurretPitchLimits(data.MemoryAddress, i), saved)) replayFlags |= ReplayFlags.Respawn;
                            RawMemory.WriteTurretPitchLimits(data.MemoryAddress, i, saved);
                        }
                    }
                }
                foreach (var entry in WeaponScalarFields) ApplyEntryField(element, entry, data, vehicle, ref replayFlags);
            }
        }
    }
}
