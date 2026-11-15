using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GTA;
using GTA.Math;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly IHandlingMetaXmlMapper handlingMetaXml;

        // Unit-conversion noise is snapped off first, so a stored 10 never saves as 9.999999
        private static string FormatSaveFloat(float value)
        {
            double snapped = Math.Round((double)value, 4);
            double saved = Math.Abs(snapped - value) <= Math.Abs((double)value) * SAVE_SNAP_RELATIVE_TOLERANCE + SAVE_SNAP_ABSOLUTE_TOLERANCE ? snapped : value;
            return saved.ToString("F6", CultureInfo.InvariantCulture);
        }

        private static void SetVal(XElement parent, string name, float value)
        {
            parent.Add(new XElement(name, new XAttribute("value", FormatSaveFloat(value))));
        }

        private static void SetVal(XElement parent, string name, int value)
        {
            parent.Add(new XElement(name, new XAttribute("value", value.ToString(CultureInfo.InvariantCulture))));
        }

        private static void SetVec(XElement parent, string name, Vector3 value)
        {
            parent.Add(new XElement(name, new XAttribute("x", FormatSaveFloat(value.X)), new XAttribute("y", FormatSaveFloat(value.Y)), new XAttribute("z", FormatSaveFloat(value.Z))));
        }

        // Returns the trimmed count, which WeaponSeats and vehicleModType share instead of trimming independently
        private static int SetHashArray(XElement parent, string name, VehicleWeaponHash[] values)
        {
            int count = values.Length;
            while (count > 0 && IsUnsetWeaponHash(values[count - 1])) count--;
            var element = new XElement(name);
            for (int i = 0; i < count; i++)
            {
                if (IsUnsetWeaponHash(values[i]))
                {
                    element.Add(new XElement(ITEM_TAG));
                    continue;
                }
                int index = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)values[i]);
                string text = index >= 0 && WeaponHashTable[index].NativeName != null ? WeaponHashTable[index].NativeName : values[i].ToString();
                element.Add(new XElement(ITEM_TAG, text));
            }
            parent.Add(element);
            return count;
        }

        private static readonly char[] ArrayValueDelimiters = { ' ', '\t', '\r', '\n' };

        // The one writer of an array element's text, which every int and float array shares
        private static XElement BuildArrayElement(string name, string content, List<string> texts)
        {
            var element = new XElement(name, new XAttribute("content", content));
            var sb = new StringBuilder();
            for (int i = 0; i < texts.Count; i++) sb.Append(Environment.NewLine).Append(ARRAY_ITEM_INDENT).Append(texts[i]);
            if (texts.Count > 0) sb.Append(Environment.NewLine).Append(ARRAY_CLOSE_INDENT);
            element.Value = sb.ToString();
            return element;
        }

        private static List<string> FormatFloatTexts(float[] values, int count)
        {
            var texts = new List<string>();
            for (int i = 0; i < count; i++) texts.Add(FormatSaveFloat(values[i]));
            return texts;
        }

        private static void SetIntArray(XElement parent, string name, int[] values, int count)
        {
            var texts = new List<string>();
            for (int i = 0; i < count; i++) texts.Add(values[i].ToString(CultureInfo.InvariantCulture));
            parent.Add(BuildArrayElement(name, "int_array", texts));
        }

        // The lenient readers every phantom helper shares: A missing element reads as an empty array, and a token that fails to parse reads as 0
        private static int[] ReadIntArrayElement(XElement parent, string tag)
        {
            XElement element = parent.Element(tag);
            string[] tokens = element != null ? element.Value.Split(ArrayValueDelimiters, StringSplitOptions.RemoveEmptyEntries) : new string[0];
            var values = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++) int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]);
            return values;
        }

        private static float[] ReadFloatArrayElement(XElement parent, string tag)
        {
            XElement element = parent.Element(tag);
            string[] tokens = element != null ? element.Value.Split(ArrayValueDelimiters, StringSplitOptions.RemoveEmptyEntries) : new string[0];
            var values = new float[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!ParseFiniteFloat(tokens[i], out values[i])) values[i] = 0.0f;
            }
            return values;
        }

        private static void ReplaceIntArrayElement(XElement parent, string name, int[] values, int count)
        {
            XElement existing = parent.Element(name);
            if (existing != null) existing.Remove();
            SetIntArray(parent, name, values, count);
        }

        // No trailing default-value trim, unlike SetFloatArray: An interim phantom edit (swap or delete) must keep the exact count
        private static void SetFloatArrayExact(XElement parent, string name, float[] values, int count)
        {
            parent.Add(BuildArrayElement(name, "float_array", FormatFloatTexts(values, count)));
        }

        private static void ReplaceFloatArrayElement(XElement parent, string name, float[] values, int count)
        {
            XElement existing = parent.Element(name);
            if (existing != null) existing.Remove();
            SetFloatArrayExact(parent, name, values, count);
        }

        // A phantom weapon array grows lazily on any out-of-bounds write (SetPhantomWeaponIntArrayItem) and never shrinks on its own
        // This cuts it back to its real slot count before it is written into a saved handling
        private static void TrimIntArrayElement(XElement parent, string name, int maxCount)
        {
            if (parent.Element(name) == null) return;
            int[] values = ReadIntArrayElement(parent, name);
            ReplaceIntArrayElement(parent, name, values, Math.Min(maxCount, values.Length));
        }

        private static void TrimFloatArrayElement(XElement parent, string name, int maxCount)
        {
            if (parent.Element(name) == null) return;
            float[] values = ReadFloatArrayElement(parent, name);
            ReplaceFloatArrayElement(parent, name, values, Math.Min(maxCount, values.Length));
        }

        private static float[] DefaultTurretPitchLimitValues()
        {
            var values = new float[TurretPitchLimitFields.Length];
            for (int i = 0; i < values.Length; i++) values[i] = TurretPitchLimitFields[i].DefaultFloat;
            return values;
        }

        // A missing Item, or a member that is missing or fails to parse, keeps its init default, matching how the game loads an incomplete entry
        private static float[] ReadTurretPitchLimitItem(XElement item)
        {
            float[] values = DefaultTurretPitchLimitValues();
            if (item == null) return values;
            for (int i = 0; i < values.Length; i++)
            {
                XElement member = item.Element(TurretPitchLimitFields[i].Tag);
                XAttribute valueAttribute = member != null ? member.Attribute("value") : null;
                float parsed;
                if (valueAttribute != null && ParseFiniteFloat(valueAttribute.Value, out parsed)) values[i] = parsed;
            }
            return values;
        }

        private static XElement BuildTurretPitchLimitItem(float[] values)
        {
            var item = new XElement(ITEM_TAG);
            for (int i = 0; i < TurretPitchLimitFields.Length; i++) SetVal(item, TurretPitchLimitFields[i].Tag, values[i]);
            return item;
        }

        // Every entry is written in full, defaults included, so the file always shows each turret's 6 values
        private static XElement BuildTurretPitchLimitElement(List<float[]> entries)
        {
            var element = new XElement(TURRET_PITCH_LIMIT_TAG);
            for (int i = 0; i < entries.Count; i++) element.Add(BuildTurretPitchLimitItem(entries[i]));
            return element;
        }

        private static void ResizeTurretPitchLimitItems(XElement parent, int slotCount)
        {
            XElement existing = parent.Element(TURRET_PITCH_LIMIT_TAG);
            var entries = new List<float[]>();
            if (existing != null)
            {
                foreach (XElement item in existing.Elements(ITEM_TAG))
                {
                    if (entries.Count >= slotCount) break;
                    entries.Add(ReadTurretPitchLimitItem(item));
                }
            }
            while (entries.Count < slotCount) entries.Add(DefaultTurretPitchLimitValues());
            if (existing != null) existing.ReplaceWith(BuildTurretPitchLimitElement(entries));
            else parent.Add(BuildTurretPitchLimitElement(entries));
        }

        // maxCount caps the array to a real slot count (e.g. weaponCount) before the trailing-default trim runs
        // A slot beyond that count never survives into the saved file, even when its stray value happens to be non-default
        private static void SetFloatArray(XElement parent, string name, float[] values, float defaultValue, int maxCount = int.MaxValue)
        {
            int count = Math.Min(values.Length, maxCount);
            while (count > 0 && values[count - 1] == defaultValue) count--;
            SetFloatArrayExact(parent, name, values, count);
        }

        private static float[] ReadRawFloatArray(IntPtr baseAddress, int offset, int count)
        {
            var result = new float[count];
            for (int i = 0; i < count; i++) result[i] = RawMemory.ReadRawFloat(baseAddress, offset + i * FLOAT_FIELD_STRIDE);
            return result;
        }

        private static void WriteRawFloatArray(IntPtr baseAddress, int offset, float[] values)
        {
            for (int i = 0; i < values.Length; i++) RawMemory.WriteRawFloat(baseAddress, offset + i * FLOAT_FIELD_STRIDE, values[i]);
        }

        // A turret float array of a weapon slot set, whichever way the game exposes it: The API's own array, or only a raw offset
        private static float[] ReadTurretFloatArray(TurretFieldEntry field, VehicleWeaponHandlingData data, int count)
        {
            return field.RawOffset ? ReadRawFloatArray(data.MemoryAddress, field.Offset, count) : field.GetArray(data);
        }

        private static void WriteTurretFloatArray(TurretFieldEntry field, VehicleWeaponHandlingData data, float[] values)
        {
            if (field.RawOffset) WriteRawFloatArray(data.MemoryAddress, field.Offset, values);
            else field.SetArray(data, values);
        }

        // A saved handling stores every float as FormatSaveFloat text, so a live value only counts as changed when it differs at that resolution
        // An exact comparison flags a value that merely carries more precision than the file holds, or picked up rounding through a unit conversion
        private static bool SameAtSaveResolution(float a, float b) { return FormatSaveFloat(a) == FormatSaveFloat(b); }
        private static bool SameAtSaveResolution(Vector3 a, Vector3 b) { return SameAtSaveResolution(a.X, b.X) && SameAtSaveResolution(a.Y, b.Y) && SameAtSaveResolution(a.Z, b.Z); }

        private static string GetValAttr(XElement parent, string name)
        {
            XElement element = parent.Element(name);
            if (element == null) return null;
            XAttribute attribute = element.Attribute("value");
            return attribute != null ? attribute.Value : null;
        }

        // Unlike the mapper's GetFloat and GetInt, these never touch the handling apply counters or log
        private static bool ReadFloatAttr(XElement parent, string name, out float value)
        {
            value = 0.0f;
            string s = GetValAttr(parent, name);
            return s != null && ParseFiniteFloat(s, out value);
        }

        private static bool ReadIntAttr(XElement parent, string name, out int value)
        {
            value = 0;
            string s = GetValAttr(parent, name);
            return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        // A raw row reads 0 on a build whose raw offsets are unsupported, which a save would write out as the real value
        private static void BuildEntryField<TData>(XElement parent, HandlingFieldEntry<TData> entry, TData data)
        {
            if (entry.RawOffset && !RawMemory.RawOffsetsSupported) return;
            if (entry.RequiresSpecialFlightFlagsBuild && !RawMemory.SpecialFlightFlagsSupported) return;
            if (entry.GetFloat != null) SetVal(parent, entry.Tag, entry.GetFloat(data));
            else if (entry.GetVec != null) SetVec(parent, entry.Tag, entry.GetVec(data));
            else if (entry.GetInt != null) SetVal(parent, entry.Tag, entry.GetInt(data));
        }

        private static void BuildDefaultEntryField<TData>(XElement parent, HandlingFieldEntry<TData> entry)
        {
            if (entry.GetFloat != null || entry.SetFloat != null) SetVal(parent, entry.Tag, ApplyHardBounds(entry, entry.DefaultFloat, 0));
            else if (entry.GetVec != null || entry.SetVec != null) SetVec(parent, entry.Tag, entry.DefaultVector);
            else if (entry.GetInt != null || entry.SetInt != null) SetVal(parent, entry.Tag, ApplyHardBounds(entry, (int)entry.DefaultFloat));
        }

        // Car, Flying, SpecialFlight, and Weapon keep their own Build, Apply, and BuildDefault functions, since each carries extra logic around its scalar loop
        private static XElement BuildCategoryXml<TData>(string xmlTypeName, HandlingFieldEntry<TData>[] fields, TData data)
        {
            if (data == null) return NullItem();
            var element = new XElement(ITEM_TAG, new XAttribute("type", xmlTypeName));
            foreach (var entry in fields) BuildEntryField(element, entry, data);
            return element;
        }

        private static XElement BuildDefaultCategoryXml<TData>(string xmlTypeName, HandlingFieldEntry<TData>[] fields)
        {
            var element = new XElement(ITEM_TAG, new XAttribute("type", xmlTypeName));
            foreach (var entry in fields) BuildDefaultEntryField(element, entry);
            return element;
        }

        // Base-level field, never part of the adopted sub-item clone, so the phantom element's own storage child is created here either way
        // WirePhantomFloat's setter (SetPhantomFloatAttr) needs an existing child and silently does nothing without one
        private void SeedAdoptedWeaponDamageField(HandlingKey handling, XElement handlingItem)
        {
            XElement adoptedWeaponElement = GetOrCreatePhantomElement(handling, CATEGORY_WEAPON);
            XElement appliedWeaponDamageElement = handlingItem.Element("fWeaponDamageScaledToVehHealthMult");
            if (appliedWeaponDamageElement != null) adoptedWeaponElement.Add(new XElement(appliedWeaponDamageElement));
            else SetVal(adoptedWeaponElement, "fWeaponDamageScaledToVehHealthMult", WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT_DEFAULT);
        }

        // Which vehicle-gated fields a save writes and an applied handling covers: The rocket boost base fields, and the Weapon category's TurretPitchLimitData
        private struct HandlingFieldGates { public bool RocketBoost; public bool RearMountedTurret; }
        private HandlingFieldGates ResolveHandlingFieldGates(Vehicle vehicle)
        {
            return new HandlingFieldGates { RocketBoost = !Entity.IsNullOrNotExisting(vehicle) && vehicle.HasRocketBoost, RearMountedTurret = RawMemory.HasRearMountedTurret(vehicle) };
        }

        // What a save adds to the live handling's own values: The export-only categories the player toggled and the pending AdvancedData of a Car with no real entries
        // A snapshot of the genuine live state passes None, which adds nothing
        private class ExportOverlay
        {
            public static readonly ExportOverlay None = new ExportOverlay(new HashSet<string>(), null, null);
            public readonly HashSet<string> Toggled;
            public readonly XElement PendingAdvancedData;
            public readonly Func<string, XElement> GetOrCreateCategoryElement;
            public ExportOverlay(HashSet<string> toggled, XElement pendingAdvancedData, Func<string, XElement> getOrCreateCategoryElement)
            {
                Toggled = toggled;
                PendingAdvancedData = pendingAdvancedData;
                GetOrCreateCategoryElement = getOrCreateCategoryElement;
            }
        }

        private ExportOverlay ResolveExportOverlay(HandlingKey handling)
        {
            HashSet<string> toggled;
            if (!pendingSubDataToggle.TryGetValue(handling, out toggled) || toggled == null) toggled = ExportOverlay.None.Toggled;
            XElement pendingBlock;
            pendingAdvancedData.TryGetValue(handling, out pendingBlock);
            return new ExportOverlay(toggled, pendingBlock, category => GetOrCreatePhantomElement(handling, category));
        }

        private static XElement NullItem() { return new XElement(ITEM_TAG, new XAttribute("type", "NULL")); }

        // isVertical distinguishes the two SubHandlingData slots a vehicle can carry against the same underlying FlyingHandlingData type (e.g. a VTOL plane)
        // It is written as the file's own handlingType element, the game's own schema field for it, since the type attribute alone cannot tell them apart
        private static XElement BuildFlyingHandlingXml(FlyingHandlingData data, bool isVertical)
        {
            if (data == null) return NullItem();
            var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(isVertical ? CATEGORY_VERTICAL_FLYING : CATEGORY_FLYING).XmlType));
            foreach (var entry in FlyingFields) BuildEntryField(element, entry, data);
            element.Add(new XElement("handlingType", isVertical ? "HANDLING_TYPE_VERTICAL_FLYING" : "HANDLING_TYPE_FLYING"));
            return element;
        }

        private static XElement BuildDefaultCarHandlingXml()
        {
            var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(CATEGORY_CAR).XmlType));
            foreach (var entry in CarFields) BuildDefaultEntryField(element, entry);
            element.Add(new XElement(ADVANCED_FLAGS_TAG, ((uint)0).ToString("X8")));

            // AdvancedData is create-gated (AdvancedData.cs, XmlAdvancedDataStore.PressCreate), so a freshly created phantom Car element starts with no AdvancedData child
            // The element's presence is the activation signal, so it is deliberately *not* added here
            return element;
        }

        // Starts with one zero-default entry, and further entries are appended one at a time from Add Slot
        private static XElement BuildDefaultAdvancedDataElement()
        {
            return new XElement(ADVANCED_DATA_TAG, BuildDefaultAdvancedDataSlot());
        }

        // Slot starts at -1, the parser schema's init value
        private static XElement BuildDefaultAdvancedDataSlot()
        {
            return new XElement(ITEM_TAG, new XAttribute("type", "CAdvancedData"), new XElement("Slot", new XAttribute("value", "-1")), new XElement("Index", new XAttribute("value", "0")), new XElement("Value", new XAttribute("value", "0.000000")));
        }

        private static XElement BuildDefaultFlyingHandlingXml(bool isVertical = false)
        {
            var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(isVertical ? CATEGORY_VERTICAL_FLYING : CATEGORY_FLYING).XmlType));
            foreach (var entry in FlyingFields) BuildDefaultEntryField(element, entry);
            // Matches BuildFlyingHandlingXml's own always-write convention for round-trip consistency
            // Flying and VerticalFlying share this identical struct, disambiguated only by this element
            element.Add(new XElement("handlingType", isVertical ? "HANDLING_TYPE_VERTICAL_FLYING" : "HANDLING_TYPE_FLYING"));
            return element;
        }

        // strFlags is hand-built on the live tree, never part of SpecialFlightFields, so a new phantom element needs it from the start
        // It matches BuildHandlingXml's live SpecialFlight save branch, which appends it after the table's rows
        private static XElement BuildDefaultSpecialFlightHandlingXml()
        {
            var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT).XmlType));
            foreach (var entry in SpecialFlightFields) BuildDefaultEntryField(element, entry);
            element.Add(new XElement(SPECIAL_FLIGHT_FLAGS_TAG, ((uint)0).ToString("X8")));
            return element;
        }

        // Every array starts empty, since there is no live vehicle to derive the weapon and turret slot counts from
        private static XElement BuildDefaultWeaponHandlingXml()
        {
            var element = new XElement(ITEM_TAG, new XAttribute("type", ResolveCategoryDefinition(CATEGORY_WEAPON).XmlType));
            SetHashArray(element, WEAPON_HASH_ARRAY_TAG, new VehicleWeaponHash[0]);
            SetIntArray(element, WEAPON_SEAT_ARRAY_TAG, new int[0], 0);
            foreach (TurretFieldEntry field in TurretFields) SetFloatArray(element, field.Tag, new float[0], 0.0f);
            element.Add(new XElement(TURRET_PITCH_LIMIT_TAG));
            foreach (var entry in WeaponScalarFields) BuildDefaultEntryField(element, entry);
            // Base-level field, not part of WeaponScalarFields, stored here as an extra child purely for WirePhantomFloat's own storage shape
            // SetPhantomFloatAttr only sets an existing child, never creates one. BuildPhantomWeaponOutput strips it before the real sub-block is written
            SetVal(element, "fWeaponDamageScaledToVehHealthMult", WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT_DEFAULT);
            return element;
        }

        // What one handling document's walk leaves for the script to finish, filled as the walk goes, so a throw partway leaves exactly what it reached
        private sealed class HandlingApplyReport
        {
            public int FieldsInvalid;
            public int FieldsAbsent;
            // The document's handling item, which Weapon's adoption seed reads its base-level damage field from
            public XElement HandlingItem;
            // Each category the document carries for a handling that lacks it, with the element it carries, in document order
            public readonly List<KeyValuePair<string, XElement>> AbsentCategoriesCarried = new List<KeyValuePair<string, XElement>>();
            // Each category the handling lacks that the document does not carry, read only once SubHandlingWalked is True
            public readonly List<string> AbsentCategoriesNotCarried = new List<string>();
            // Each category the handling holds that the document does not carry, read only once SubHandlingWalked is True
            public readonly List<string> PresentCategoriesNotCarried = new List<string>();
            // The Car's AdvancedData items when the document carries more than the array's capacity holds, with how many of them the walk wrote
            // Null when every item fit
            public XElement[] AdvancedDataItems;
            public int AdvancedDataWrittenCount;
            // True once every SubHandlingData item was walked. A document with no handling item or no SubHandlingData, or a throw before then, leaves it False
            public bool SubHandlingWalked;
            public bool AdvancedDataCountChanged;
            // True when the walk turned SF_WORKS_UPSIDE_DOWN back on for a vehicle with no hover bone
            public bool WorksUpsideDownForced;
        }

        // Each step runs in a finally, so a throw partway still adopts exactly what the walk reached, keyed by the handling being applied
        // Returns how many live requests it queued: Categories the vehicle lacks that the plugin creates a frame later, and AdvancedData growth
        // presentCategoriesNotCarried, when given, receives each category the handling holds that the document does not carry
        // origin decides whether the setting gates the live requests, and an Undo All counts an adopted category as not reverted
        private int ApplyHandlingDocument(XDocument doc, HandlingData handling, Vehicle vehicle, HandlingFieldGates gates, LiveRequestOrigin origin, ref ReplayFlags replayFlags, out int fieldsInvalid, out int fieldsAbsent, List<string> presentCategoriesNotCarried = null)
        {
            HandlingKey handlingKey = ResolveHandlingKey(handling);
            ReleaseAssertionsForHandling(handlingKey);
            var report = new HandlingApplyReport();
            int liveRequestsQueued = 0;
            try { handlingMetaXml.ApplyHandlingXml(doc, handling, vehicle, gates, ref replayFlags, report); }
            finally
            {
                // With live creation offered the category is requested from the plugin with the applied element as its values
                // Any other case adopts it as export-only, as does a request that fails later
                foreach (KeyValuePair<string, XElement> carried in report.AbsentCategoriesCarried)
                {
                    if (RequestAppliedCategoryCreation(handlingKey, handling, vehicle, carried.Key, carried.Value, report.HandlingItem, origin))
                    {
                        liveRequestsQueued++;
                        continue;
                    }
                    if (AdoptPhantomFromAppliedXml(handlingKey, carried.Key, carried.Value, vehicle)) report.WorksUpsideDownForced = true;
                    if (origin == LiveRequestOrigin.UndoAll) AddUndoAllFailure(undoAllRunInProgress, carried.Key + " creation", REASON_LIVE_UNAVAILABLE);
                    if (carried.Key == CATEGORY_WEAPON) SeedAdoptedWeaponDamageField(handlingKey, report.HandlingItem);
                }
                if (report.AdvancedDataItems != null && RequestAppliedAdvancedDataGrowth(handlingKey, handling, vehicle, report.AdvancedDataItems, report.AdvancedDataWrittenCount, origin))
                {
                    liveRequestsQueued++;
                    report.FieldsAbsent -= Math.Min(report.AdvancedDataItems.Length, ADVANCEDDATA_COUNT_SANITY_CEILING) - report.AdvancedDataWrittenCount;
                }
                if (presentCategoriesNotCarried != null && report.SubHandlingWalked) presentCategoriesNotCarried.AddRange(report.PresentCategoriesNotCarried);
                // A document that no longer carries an adopted category (an undo of the apply that adopted it) reverses that adoption
                // Otherwise the category stays export-only with nothing left to have adopted it from
                // A category the player created through its placeholder was never adopted, so it stays
                HashSet<string> toggledCategories;
                HashSet<string> adopted;
                if (report.SubHandlingWalked && pendingSubDataToggle.TryGetValue(handlingKey, out toggledCategories) && adoptedPhantomCategories.TryGetValue(handlingKey, out adopted) && adopted.Count > 0)
                {
                    foreach (string category in report.AbsentCategoriesNotCarried) if (adopted.Remove(category)) RemoveAdoptedPhantomCategory(handlingKey, toggledCategories, category);
                }
                if (report.AdvancedDataCountChanged) ReleaseAdvancedDataAssertions();
                if (report.WorksUpsideDownForced) ReportWorksUpsideDownForced(vehicle);
            }
            fieldsInvalid = report.FieldsInvalid;
            fieldsAbsent = report.FieldsAbsent;
            return liveRequestsQueued;
        }

        // The seed is a detached clone, so the phantom submenu shows the applied values rather than defaults, held to the same hard bounds as a live write
        // Returns True when the stored element's SF_WORKS_UPSIDE_DOWN had to be turned on, which the caller reports
        private bool AdoptPhantomFromAppliedXml(HandlingKey handling, string category, XElement appliedElement, Vehicle vehicle)
        {
            HashSet<string> toggled = GetOrAdd(pendingSubDataToggle, handling);
            toggled.Add(category);
            HashSet<string> adopted = GetOrAdd(adoptedPhantomCategories, handling);
            adopted.Add(category);
            StoreAppliedCategoryElement(handling, category, appliedElement);
            return ForcePhantomWorksUpsideDown(ResolvePhantomElement(handling, category), category, vehicle);
        }

        // Keeps the applied element as the category's element, so both an adoption and a live creation start from the applied values
        private void StoreAppliedCategoryElement(HandlingKey handling, string category, XElement appliedElement)
        {
            Dictionary<string, XElement> categoryMap = GetOrAdd(pendingSubDataElements, handling);
            XElement storedElement = new XElement(appliedElement);
            ClampElementToHardBounds(category, storedElement);
            categoryMap[category] = storedElement;
        }

        // A file written by another tool can carry a value the editor never writes. A child with no row, or a row with no bound, is left as it is
        private static void ClampElementToHardBounds(string category, XElement element)
        {
            foreach (XElement child in element.Elements()) ClampChildToHardBounds(ResolveFieldEntry(category, child.Name.LocalName), child);
            XElement advancedData = element.Element(ADVANCED_DATA_TAG);
            if (advancedData == null) return;
            foreach (XElement entryItem in advancedData.Elements(ITEM_TAG))
            {
                foreach (XElement member in entryItem.Elements()) ClampChildToHardBounds(ResolveAdvancedDataField(member.Name.LocalName), member);
            }
        }

        // An int row holds one value attribute, and a float or vector row holds value or x, y, and z, so the row's own bound type picks which attributes are read
        private static void ClampChildToHardBounds(FieldEntry row, XElement child)
        {
            if (row == null) return;
            if (row.HardMinInt.HasValue || row.HardMaxInt.HasValue)
            {
                XAttribute intAttribute = child.Attribute("value");
                int parsedInt;
                if (intAttribute == null || !int.TryParse(intAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt)) return;
                int boundedInt = ApplyHardBounds(row, parsedInt);
                if (boundedInt != parsedInt) child.SetAttributeValue("value", boundedInt.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (!row.HardMin.HasValue && !row.MinimumMagnitude.HasValue) return;
            foreach (string name in new[] { "value", "x", "y", "z" })
            {
                XAttribute floatAttribute = child.Attribute(name);
                float parsedFloat;
                if (floatAttribute == null || !ParseFiniteFloat(floatAttribute.Value, out parsedFloat)) continue;
                float boundedFloat = ApplyHardBounds(row, parsedFloat, 0);
                if (boundedFloat != parsedFloat) child.SetAttributeValue(name, FormatSaveFloat(boundedFloat));
            }
        }

        // The adopted element is removed with the toggle, so creating the category later through its placeholder starts from defaults
        private void RemoveAdoptedPhantomCategory(HandlingKey handling, HashSet<string> toggledCategories, string category)
        {
            toggledCategories.Remove(category);
            Dictionary<string, XElement> categoryMap;
            if (pendingSubDataElements.TryGetValue(handling, out categoryMap)) categoryMap.Remove(category);
        }

        // The Item children of an array or block element, in document order
        private static XElement[] ListItemElements(XElement parent)
        {
            return new List<XElement>(parent.Elements(ITEM_TAG)).ToArray();
        }

        private static int CountItemElements(XElement parent)
        {
            int count = 0;
            foreach (XElement item in parent.Elements(ITEM_TAG)) count++;
            return count;
        }

        private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key) where TValue : new()
        {
            return GetOrAdd(map, key, () => new TValue());
        }

        // The create form serves a value whose constructor takes arguments, such as a comparer
        private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key, Func<TValue> create)
        {
            TValue value;
            if (map.TryGetValue(key, out value)) return value;
            value = create();
            map[key] = value;
            return value;
        }

        private static bool ArraysEqual<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a.Length; i++) { if (!comparer.Equals(a[i], b[i])) return false; }
            return true;
        }

        // Float arrays compare at save resolution instead, for the same reason SameAtSaveResolution exists. Overload resolution picks this over the generic form
        private static bool ArraysEqual(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) { if (!SameAtSaveResolution(a[i], b[i])) return false; }
            return true;
        }

        // A saved handling's array length is never guaranteed to match the current build's slot count
        private static T[] PadOrTrim<T>(T[] saved, int count, T defaultValue)
        {
            var result = new T[count];
            for (int i = 0; i < count; i++) result[i] = i < saved.Length ? saved[i] : defaultValue;
            return result;
        }

        private static T[] RemoveArrayItem<T>(T[] values, int index)
        {
            var result = new T[values.Length - 1];
            Array.Copy(values, 0, result, 0, index);
            Array.Copy(values, index + 1, result, index, values.Length - index - 1);
            return result;
        }

        // The index is already clamped to the array's length
        private static T[] InsertArrayItem<T>(T[] values, int index, T value)
        {
            var result = new T[values.Length + 1];
            Array.Copy(values, 0, result, 0, index);
            result[index] = value;
            Array.Copy(values, index, result, index + 1, values.Length - index);
            return result;
        }
    }
}
