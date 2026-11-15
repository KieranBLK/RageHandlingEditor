using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Which field a Preview row shows, independent of its display text. Category is the short name every editor key and undo record uses (null for a base field),
        // Tag is the XML tag (a weapon slot row carries its slot array's tag), Component is x, y, or z for a vector axis, Index is the AdvancedData entry or
        // weapon slot, and Member is Slot, Index, or Value on an AdvancedData row. Two rows show the same field exactly when their refs are equal
        private struct FieldRef : IEquatable<FieldRef>
        {
            public readonly string Category;
            public readonly string Tag;
            public readonly string Component;
            public readonly int Index;
            public readonly string Member;
            public FieldRef(string category, string tag) : this(category, tag, null, -1, null) { }
            public FieldRef(string category, string tag, string component, int index, string member)
            {
                Category = category;
                Tag = tag;
                Component = component;
                Index = index;
                Member = member;
            }

            public bool Equals(FieldRef other) { return Category == other.Category && Tag == other.Tag && Component == other.Component && Index == other.Index && Member == other.Member; }
            public override bool Equals(object obj) { return obj is FieldRef && Equals((FieldRef)obj); }
            public override int GetHashCode()
            {
                int hash = Tag != null ? Tag.GetHashCode() : 0;
                hash = hash * 31 + (Category != null ? Category.GetHashCode() : 0);
                hash = hash * 31 + (Component != null ? Component.GetHashCode() : 0);
                hash = hash * 31 + Index;
                return hash * 31 + (Member != null ? Member.GetHashCode() : 0);
            }
        }

        // One Preview or Diff row's field: Its ref, its table row (null for AIHandling, mode, a flag, or a name table row), and the title and value the row displays
        // Each pooled row stores its PreviewField in NativeItem.Tag, so Selective Copy and Diff read what the row shows from the ref and never from its text
        private class PreviewField
        {
            public readonly FieldRef Ref;
            public readonly FieldEntry Entry;
            public readonly string Title;
            public readonly string Value;
            public PreviewField(FieldRef reference, FieldEntry entry, string title, string value)
            {
                Ref = reference;
                Entry = entry;
                Title = title;
                Value = value;
            }
        }

        // A name no definition carries comes back unchanged, so a category the definitions do not list keeps its own name instead of reading as a base field
        // Preview's menus use the long name, and every editor key and undo record uses the short one
        private static string ResolveCategoryLongName(string shortName)
        {
            CategoryDefinition definition = ResolveCategoryDefinition(shortName);
            return definition != null ? definition.LongName : shortName;
        }

        // Everything Preview reads from one saved handling file, filled from a single parse. A failed parse caches empty data until the file changes
        private class HandlingPreviewCache
        {
            public DateTime WriteTime;
            public List<PreviewField> Fields = new List<PreviewField>();
            public string HandlingName;
            public List<List<PreviewField>> WeaponSlots = new List<List<PreviewField>>();
            // True when the file carries fRocketBoostCapacity. The load-time Diff baseline sweep has no vehicle to check, so it matches the saved handling instead
            public bool HasRocketBoostFields;
        }

        // Cached per saved handling file, invalidated by write time. Avoids re-parsing the full XML on every navigation step
        private Dictionary<SavedHandlingKey, HandlingPreviewCache> previewFieldListCache = new Dictionary<SavedHandlingKey, HandlingPreviewCache>();

        private HandlingPreviewCache GetHandlingPreviewCache(SavedHandlingKey key) { return key.Source.LoadPreview(key); }

        // Returns the cached parse for a saved handling file, parsing it once whenever its write time changed since the last parse
        private HandlingPreviewCache LoadFilePreview(SavedHandlingKey key)
        {
            string path = SavedHandlingPath(key);
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Warn, Dbg.PreviewBuildFailed(key.Name, ex.Message));
                return new HandlingPreviewCache();
            }
            HandlingPreviewCache cached;
            if (previewFieldListCache.TryGetValue(key, out cached) && cached.WriteTime == writeTime) return cached;
            cached = new HandlingPreviewCache { WriteTime = writeTime };
            try
            {
                XDocument doc = XDocument.Load(path);
                cached.Fields = BuildFullFieldListFromDocument(doc);
                cached.HandlingName = ReadHandlingNameFromDocument(doc);
                cached.WeaponSlots = BuildWeaponPreviewSlotsFromDocument(doc);
                XElement handlingDataElement = doc.Root.Element(HANDLING_DATA_TAG);
                XElement item = handlingDataElement != null ? handlingDataElement.Element(ITEM_TAG) : null;
                cached.HasRocketBoostFields = item != null && item.Element("fRocketBoostCapacity") != null;
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.PreviewBuildFailed(key.Name, ex.Message)); }
            previewFieldListCache[key] = cached;
            return cached;
        }

        private void PrunePreviewFieldListCache(SavedHandlingKey[] savedHandlingKeys)
        {
            var staleKeys = new List<SavedHandlingKey>();
            foreach (SavedHandlingKey cachedKey in previewFieldListCache.Keys)
            {
                if (Array.IndexOf(savedHandlingKeys, cachedKey) < 0) staleKeys.Add(cachedKey);
            }
            for (int i = 0; i < staleKeys.Count; i++) previewFieldListCache.Remove(staleKeys[i]);
        }

        // ShowPreviewDiff's own baseline capture: One per handling struct, holding its original values, captured unconditionally the moment guaranteed to
        // precede any editor change, gated only on "not yet captured for this handling", so the first scoped model of a shared handling owns the one baseline
        // Used only by Diff's own display lookup, which finds it through the highlighted saved handling's handlingName, never re-captured once present,
        // and never gated on a matching saved handling name
        private Dictionary<HandlingKey, XDocument> previewDiffSnapshots = new Dictionary<HandlingKey, XDocument>();

        private static string ReadHandlingNameFromDocument(XDocument doc)
        {
            XElement handlingDataElement = doc.Root.Element(HANDLING_DATA_TAG);
            XElement item = handlingDataElement != null ? handlingDataElement.Element(ITEM_TAG) : null;
            XElement nameElement = item != null ? item.Element(HANDLING_NAME_TAG) : null;
            return nameElement != null ? nameElement.Value : null;
        }

        // The single mutation point both the init sweep and the scope-event capture funnel through, gated only on "not yet captured for this handling",
        // never on a matching saved handling name
        // Gates follow the same rule as a save, so the baseline and a handling saved from the same vehicle keep the same row count
        private void CapturePreviewDiffSnapshot(HandlingKey key, HandlingData handling, string handlingName, HandlingFieldGates gates)
        {
            if (previewDiffSnapshots.ContainsKey(key)) return;
            previewDiffSnapshots[key] = handlingMetaXml.BuildHandlingXml(handling, handlingName, gates, ExportOverlay.None);
            logger.Log(Dbg.Level.Info, Dbg.PreviewDiffSnapshotCaptured(modelNames.DescribeHandling(key)));
        }

        // Init-time sweep, covering every saved handling already on disk at script load with no live vehicle required
        // It runs *after* the PreviewDiffSnapshot section loads, so a handling restored from disk is never re-derived from a possibly edited live HandlingData
        // A saved handling whose handlingName resolves to no valid HandlingData is left uncaptured until a vehicle on it is scoped
        private void InitializePreviewDiffSnapshots()
        {
            SavedHandlingKey[] savedHandlings = exportsSource.ListKeys();
            for (int i = 0; i < savedHandlings.Length; i++)
            {
                HandlingPreviewCache cache = GetHandlingPreviewCache(savedHandlings[i]);
                string handlingName = cache.HandlingName;
                HandlingData resolved = modelNames.ResolveHandlingFromName(handlingName);
                if (resolved == null) continue;
                CapturePreviewDiffSnapshot(ResolveHandlingKey(resolved), resolved, handlingName, new HandlingFieldGates { RocketBoost = cache.HasRocketBoostFields, RearMountedTurret = true });
            }
        }

        // Covers every handling the init sweep didn't already reach, unconditional and name-agnostic, gated only on "not yet captured"
        // Called the moment a vehicle becomes an editor's target, which always precedes any editor change
        // The Diff visibility re-evaluates right away, so Diff can appear immediately if the matching saved handling is already highlighted at that point
        // Persistence goes through OnAborted only, like UndoHistory, so a crash that skips it starts a fresh game session and needs no handling
        private void CheckPreviewDiffSnapshotCapture(Vehicle scopedVehicle)
        {
            HandlingKey key = ResolveHandlingKey(scopedVehicle);
            if (!key.IsValid) return;
            // The same handling name a save of this vehicle writes, so the baseline matches that saved handling
            string handlingName = modelNames.ResolveHandlingName(scopedVehicle);
            bool alreadyCaptured = previewDiffSnapshots.ContainsKey(key);
            logger.Log(Dbg.Level.Info, Dbg.PreviewDiffSnapshotGateCheck(modelNames.DescribeHandling(key), modelNames.ResolveModelName(scopedVehicle), alreadyCaptured));
            if (alreadyCaptured) return;
            CapturePreviewDiffSnapshot(key, scopedVehicle.HandlingData, handlingName, ResolveHandlingFieldGates(scopedVehicle));
            SyncDiffVisibility();
        }

        // Each line starts with the handling's name hash as 8 hex digits, and a handling whose name hash does not verify is left out
        private void WritePreviewDiffSnapshot(StringBuilder sb)
        {
            foreach (KeyValuePair<HandlingKey, XDocument> snapshot in previewDiffSnapshots)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(snapshot.Key);
                if (handlingHash == null) continue;
                sb.Append(handlingHash).Append('|').Append(EncodeSnapshot(snapshot.Value)).AppendLine();
            }
        }

        // Loads *before* InitializePreviewDiffSnapshots, not after
        // A script reload does not reset the game's process-lifetime HandlingData
        // A handling edited earlier in the session would read back its edited state, not the true original, if the live sweep ran first
        // Loading the disk-persisted original first lets the sweep's "not yet captured" gate skip every handling already recovered from disk
        private bool ConsumePreviewDiffSnapshotLine(string raw)
        {
            string[] parts = raw.Split('|');
            if (parts.Length != 2) return false;
            HandlingKey handling;
            if (!reloadState.ResolveReloadStateHandling(ReloadStateSection.PreviewDiffSnapshot, parts[0], out handling) || previewDiffSnapshots.ContainsKey(handling)) return false;
            try
            {
                XDocument snapshot = DecodeSnapshot(parts[1]);
                if (snapshot == null) return false;
                previewDiffSnapshots[handling] = snapshot;
                return true;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Warn, Dbg.ReloadStateSectionFailed(ReloadStateSection.PreviewDiffSnapshot.ToString(), ex.Message));
                return false;
            }
        }

        private static List<PreviewField> BuildFullFieldListFromDocument(XDocument doc)
        {
            var fields = new List<PreviewField>();
            XElement handlingDataElement = doc.Root.Element(HANDLING_DATA_TAG);
            XElement item = handlingDataElement != null ? handlingDataElement.Element(ITEM_TAG) : null;
            if (item != null)
            {
                AppendXmlFields(fields, null, item);
                XElement subHandling = item.Element(SUB_HANDLING_DATA_TAG);
                if (subHandling != null)
                {
                    foreach (XElement subItem in subHandling.Elements(ITEM_TAG))
                    {
                        XAttribute typeAttr = subItem.Attribute("type");
                        if (typeAttr == null || typeAttr.Value == "NULL") continue;
                        // A type no definition reads keeps its own name without the leading C
                        CategoryDefinition definition = ResolveCategoryDefinitionOfElement(subItem);
                        string category = definition != null ? definition.Name : typeAttr.Value.StartsWith("C", StringComparison.Ordinal) ? typeAttr.Value.Substring(1) : typeAttr.Value;
                        AppendXmlFields(fields, category, subItem);
                        // Base-level siblings of SubHandlingData, injected here so they render nested under their category instead of Preview's top-level category list
                        if (definition == null) continue;
                        foreach (FieldEntry extraField in definition.ExtraFields)
                        {
                            string extraValue = GetValAttr(item, extraField.Tag);
                            if (extraValue != null) fields.Add(NewTagField(category, extraField.Tag, null, FormatPreviewValue(extraValue)));
                        }
                    }
                }
            }
            return fields;
        }

        // Weapon's array-indexed per-slot shape doesn't fit AppendXmlFields' generic single-row-per-tag walk, so it is skipped there and parsed here instead
        // One field list per real saved slot, trimmed to the uWeaponHash count like the save side
        // It never touches the mapper's handlingFieldsInvalid or handlingFieldsAbsent counts, since this is a Preview read and not an actual handling apply
        private List<List<PreviewField>> BuildWeaponPreviewSlotsFromDocument(XDocument doc)
        {
            var slots = new List<List<PreviewField>>();
            XElement handlingDataElement = doc.Root.Element(HANDLING_DATA_TAG);
            XElement item = handlingDataElement != null ? handlingDataElement.Element(ITEM_TAG) : null;
            XElement subHandling = item != null ? item.Element(SUB_HANDLING_DATA_TAG) : null;
            if (subHandling == null) return slots;
            XElement weaponElement = null;
            CategoryDefinition weaponDefinition = ResolveCategoryDefinition(CATEGORY_WEAPON);
            foreach (XElement subItem in subHandling.Elements(ITEM_TAG))
            {
                if (weaponDefinition.Matches(subItem))
                {
                    weaponElement = subItem;
                    break;
                }
            }
            if (weaponElement == null) return slots;
            XElement hashArrayElement = weaponElement.Element(WEAPON_HASH_ARRAY_TAG);
            if (hashArrayElement == null) return slots;
            XElement[] hashItems = ListItemElements(hashArrayElement);
            int[] rawSeats = ReadIntArrayElement(weaponElement, WEAPON_SEAT_ARRAY_TAG);
            int[] rawModTypes = ReadIntArrayElement(weaponElement, WEAPON_MOD_TYPE_ARRAY_TAG);
            // The angle arrays here are radians, the same unit handling.meta and live memory use
            var turretValues = new float[TurretFields.Length][];
            for (int t = 0; t < TurretFields.Length; t++) turretValues[t] = ReadFloatArrayElement(weaponElement, TurretFields[t].Tag);
            XElement pitchLimitElement = weaponElement.Element(TURRET_PITCH_LIMIT_TAG);
            XElement[] pitchLimitItems = pitchLimitElement != null ? ListItemElements(pitchLimitElement) : new XElement[0];
            for (int i = 0; i < hashItems.Length; i++)
            {
                var slotFields = new List<PreviewField>();
                slotFields.Add(NewWeaponSlotField(i, WEAPON_HASH_ARRAY_TAG, null, "WeaponHash", ResolveWeaponHashDisplayName(hashItems[i].Value)));
                slotFields.Add(NewWeaponSlotField(i, WEAPON_SEAT_ARRAY_TAG, null, "WeaponSeat", ResolveVehicleSeatDisplayName(i < rawSeats.Length ? rawSeats[i] : 0)));
                if (weaponModTypeSupported) slotFields.Add(NewWeaponSlotField(i, WEAPON_MOD_TYPE_ARRAY_TAG, null, "WeaponVehicleModType", ResolveVehicleModTypeDisplayName(i < rawModTypes.Length ? rawModTypes[i] : 0)));
                if (i < turretSlotCount)
                {
                    for (int t = 0; t < TurretFields.Length; t++) slotFields.Add(NewWeaponSlotField(i, TurretFields[t].Tag, TurretFields[t], TurretFields[t].Title, FormatPreviewValue((i < turretValues[t].Length ? turretValues[t][i] : 0.0f).ToString("F6", CultureInfo.InvariantCulture))));
                    float[] pitchLimits = ReadTurretPitchLimitItem(i < pitchLimitItems.Length ? pitchLimitItems[i] : null);
                    for (int p = 0; p < TurretPitchLimitFields.Length; p++) slotFields.Add(NewWeaponSlotField(i, TurretPitchLimitFields[p].Tag, TurretPitchLimitFields[p], TurretPitchLimitFields[p].Title, FormatPreviewValue(pitchLimits[p].ToString("F6", CultureInfo.InvariantCulture))));
                }
                slots.Add(slotFields);
            }
            return slots;
        }

        private static PreviewField NewWeaponSlotField(int slotIndex, string tag, FieldEntry entry, string title, string value)
        {
            return new PreviewField(new FieldRef(CATEGORY_WEAPON, tag, null, slotIndex, null), entry, title, value);
        }

        private static PreviewField NewTagField(string category, string tag, string component, string value)
        {
            string title = ResolveFieldTitle(category, tag) + (component != null ? component.ToUpperInvariant() : "");
            return new PreviewField(new FieldRef(category, tag, component, -1, null), ResolveFieldEntry(category, tag), title, value);
        }

        private static PreviewField NewAdvancedDataField(string category, int entryIndex, string member, string value)
        {
            return new PreviewField(new FieldRef(category, ADVANCED_DATA_TAG, null, entryIndex, member), ResolveAdvancedDataField(member), AdvancedDataRowTitle(entryIndex, member), value);
        }

        // A recognized native string or enum name resolves to its table entry
        // Any other hash the handling loader would accept reads "Custom (0xXXXXXXXX)", carrying the raw hash
        // That lets Selective Copy write it and Diff tell custom hashes apart
        // Unparseable text reads plain "Custom"
        private static string ResolveWeaponHashDisplayName(string nativeText)
        {
            if (string.IsNullOrEmpty(nativeText)) return WeaponHashTable[0].DisplayName;
            int index = Array.FindIndex(WeaponHashTable, entry => entry.NativeName == nativeText);
            if (index >= 0) return WeaponHashTable[index].DisplayName;
            VehicleWeaponHash parsed;
            if (Enum.TryParse(nativeText, out parsed))
            {
                index = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)parsed);
                if (index >= 0) return WeaponHashTable[index].DisplayName;
                return CUSTOM_WEAPON_HASH_DISPLAY_PREFIX + ((uint)parsed).ToString("X8", CultureInfo.InvariantCulture) + ")";
            }
            return CUSTOM_WEAPON_HASH_LABEL;
        }

        private static bool ParseCustomWeaponHashDisplay(string displayText, out uint hash)
        {
            hash = 0;
            if (displayText == null || !displayText.StartsWith(CUSTOM_WEAPON_HASH_DISPLAY_PREFIX, StringComparison.Ordinal) || !displayText.EndsWith(")", StringComparison.Ordinal)) return false;
            string hexText = displayText.Substring(CUSTOM_WEAPON_HASH_DISPLAY_PREFIX.Length, displayText.Length - CUSTOM_WEAPON_HASH_DISPLAY_PREFIX.Length - 1);
            return uint.TryParse(hexText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }

        private static string ResolveVehicleSeatDisplayName(int rawSeat)
        {
            string name = ((VehicleSeat)(rawSeat - 1)).ToString();
            return Array.IndexOf(VehicleSeatNames, name) >= 0 ? name : VehicleSeatNames[0];
        }

        private static string ResolveVehicleModTypeDisplayName(int rawModType)
        {
            string name = ((VehicleModType)rawModType).ToString();
            return Array.IndexOf(VehicleModTypeNames, name) >= 0 ? name : VehicleModTypeNames[0];
        }

        // Saved floats always carry a decimal point (F6 serialization) and ints never do, so containing a '.' tells them apart and Preview can reuse FormatDisplayFloat
        private static string FormatPreviewValue(string raw)
        {
            if (raw == null || raw.IndexOf('.') < 0) return raw;
            float parsed;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return FormatDisplayFloat(parsed);
            return raw;
        }

        private static string FormatPreviewArrayValue(string raw)
        {
            string[] values = raw.Split(ArrayValueDelimiters, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < values.Length; i++) values[i] = FormatPreviewValue(values[i]);
            return string.Join(", ", values);
        }

        // Weapon's array-shaped per-slot fields. BuildWeaponPreviewSlotsFromDocument parses these independently, so the generic AppendXmlFields walk skips them
        private static readonly string[] WeaponArrayTags =
        {
            WEAPON_SEAT_ARRAY_TAG, WEAPON_MOD_TYPE_ARRAY_TAG, "fTurretSpeed", "fTurretPitchMin", "fTurretPitchMax",
            "fTurretCamPitchMin", "fTurretCamPitchMax", "fBulletVelocityForGravity", "fTurretPitchForwardMin", TURRET_PITCH_LIMIT_TAG,
        };

        // Built once from the field tables, so one map serves a tag's title and its rules, and a row's title can never drift from the live editor's own
        // A weapon slot's name table rows are not in it, and its turret and pitch limit rows come from their own tables where the slots are built
        // Lazily built rather than a field initializer, so it never depends on static-field-initialization order against the field tables across partial-class files
        private static Dictionary<string, Dictionary<string, FieldEntry>> fieldEntriesByCategory;
        private static Dictionary<string, FieldEntry> ResolveCategoryFieldEntries(string category)
        {
            if (fieldEntriesByCategory == null)
            {
                fieldEntriesByCategory = new Dictionary<string, Dictionary<string, FieldEntry>>();
                AddCategoryFieldEntries(null, BaseFields);
                foreach (CategoryDefinition definition in CategoryDefinitions)
                {
                    foreach (string key in new[] { definition.LongName, definition.Name })
                    {
                        AddCategoryFieldEntries(key, definition.Fields);
                        AddCategoryFieldEntries(key, definition.ExtraFields);
                    }
                }
            }
            Dictionary<string, FieldEntry> fields;
            return fieldEntriesByCategory.TryGetValue(category ?? "", out fields) ? fields : null;
        }

        private static void AddCategoryFieldEntries(string category, FieldEntry[] fields)
        {
            string key = category ?? "";
            Dictionary<string, FieldEntry> categoryEntries = GetOrAdd(fieldEntriesByCategory, key);
            for (int i = 0; i < fields.Length; i++) categoryEntries[fields[i].Tag] = fields[i];
        }

        // The row a category's tag names, or null for a tag with no row
        private static FieldEntry ResolveFieldEntry(string category, string tag)
        {
            Dictionary<string, FieldEntry> categoryEntries = ResolveCategoryFieldEntries(category);
            FieldEntry entry;
            return categoryEntries != null && categoryEntries.TryGetValue(tag, out entry) ? entry : null;
        }

        // Category-qualified lookup with a fallback to the flag families (the only tags with no field-table entry at all), then the raw tag itself
        private static string ResolveFieldTitle(string category, string tag)
        {
            FieldEntry entry = ResolveFieldEntry(category, tag);
            if (entry != null) return entry.Title;
            FlagFamily family = FindFlagFamilyByTag(tag);
            return family != null ? family.ResidualTitle : tag;
        }

        // category is the short name every ref carries, null for the base item
        // SubHandlingData is walked separately by the caller, one category per sub-item. AdvancedData is a nested Slot, Index, and Value array, not a flat field
        private static void AppendXmlFields(List<PreviewField> fields, string category, XElement item)
        {
            foreach (XElement child in item.Elements())
            {
                if (child.Name.LocalName == SUB_HANDLING_DATA_TAG) continue;
                if (child.Name.LocalName == ADVANCED_DATA_TAG)
                {
                    int slotIndex = 0;
                    foreach (XElement slotItem in child.Elements(ITEM_TAG))
                    {
                        // 3 separate rows per entry, which Preview groups into one "Entry N" submenu per entry (PopulateCategoryRows)
                        fields.Add(NewAdvancedDataField(category, slotIndex, "Slot", GetValAttr(slotItem, "Slot")));
                        fields.Add(NewAdvancedDataField(category, slotIndex, "Index", GetValAttr(slotItem, "Index")));
                        fields.Add(NewAdvancedDataField(category, slotIndex, "Value", FormatPreviewValue(GetValAttr(slotItem, "Value"))));
                        slotIndex++;
                    }
                    continue;
                }
                string tag = child.Name.LocalName;
                // The saved handling's identity, not an editable field
                if (tag == HANDLING_NAME_TAG) continue;
                // Flying and VerticalFlying disambiguation only, already consumed by the caller to pick the category key
                if (tag == "handlingType") continue;
                // Base-level sibling of SubHandlingData, injected separately under the Weapon category
                if (tag == WeaponDamageScaledField.Tag) continue;
                // Weapon's per-slot array fields are read per real slot by BuildWeaponPreviewSlotsFromDocument instead of this generic single-row-per-tag walk
                // uWeaponHash is not in the skip list, since its Item children leave it with nothing to show below. Only the 3 whole-category scalars flow through here
                if (category == CATEGORY_WEAPON && Array.IndexOf(WeaponArrayTags, tag) >= 0) continue;
                XAttribute valueAttr = child.Attribute("value");
                if (valueAttr != null)
                {
                    fields.Add(NewTagField(category, tag, null, FormatPreviewValue(valueAttr.Value)));
                    continue;
                }
                XAttribute xAttr = child.Attribute("x");
                if (xAttr != null)
                {
                    XAttribute yAttr = child.Attribute("y"), zAttr = child.Attribute("z");
                    fields.Add(NewTagField(category, tag, "x", FormatPreviewValue(xAttr.Value)));
                    fields.Add(NewTagField(category, tag, "y", FormatPreviewValue(yAttr != null ? yAttr.Value : "0")));
                    fields.Add(NewTagField(category, tag, "z", FormatPreviewValue(zAttr != null ? zAttr.Value : "0")));
                    continue;
                }
                if (!child.HasElements && !string.IsNullOrEmpty(child.Value))
                {
                    XAttribute content = child.Attribute("content");
                    bool isContentArray = content != null && content.Value.EndsWith("_array", StringComparison.Ordinal);
                    string displayValue = FindFlagFamilyByTag(tag) != null ? "0x" + child.Value : isContentArray ? FormatPreviewArrayValue(child.Value) : FormatPreviewValue(child.Value);
                    fields.Add(NewTagField(category, tag, null, displayValue));
                }
            }
        }
    }
}
