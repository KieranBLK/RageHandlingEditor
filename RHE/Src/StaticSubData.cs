using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Testing flag: True offers every phantom category on every vehicle that lacks it, regardless of type or class
        private static readonly bool OFFER_EVERY_PHANTOM_CATEGORY = false;

        // Export-only creation/exclusion intent per handling, never applied to the live vehicle. Persists across a reload (see ExportOverlay)
        private Dictionary<HandlingKey, HashSet<string>> pendingSubDataToggle = new Dictionary<HandlingKey, HashSet<string>>();

        // Export-only SubHandlingData creation, keyed by handling. Edits land on an in-memory XElement, never the live vehicle
        // Only fields the save pipeline captures are exposed, and weapon per-slot/turret fields are excluded
        private Dictionary<HandlingKey, Dictionary<string, XElement>> pendingSubDataElements = new Dictionary<HandlingKey, Dictionary<string, XElement>>();

        // Phantom categories per handling that an applied handling created, as opposed to ones the player created through their placeholder
        // Only these are removed when a later applied document no longer mentions them. Persists across a reload in the PhantomSubData section of the reload state
        private Dictionary<HandlingKey, HashSet<string>> adoptedPhantomCategories = new Dictionary<HandlingKey, HashSet<string>>();

        private static string[] GetSubDataCreationCandidates(Vehicle vehicle)
        {
            var candidates = new List<string>();
            VehicleType type = vehicle.Type;
            foreach (CategoryDefinition definition in CategoryDefinitions)
            {
                bool offered = OFFER_EVERY_PHANTOM_CATEGORY || definition.CandidateTypes == null;
                if (!offered) offered = ContainsVehicleType(definition.CandidateTypes, type) && (definition.CandidateCheck == null || definition.CandidateCheck(vehicle));
                if (!offered) offered = definition.OfferedOnOffRoadCars && type == VehicleType.Automobile && vehicle.ClassType == VehicleClass.OffRoad;
                if (offered) candidates.Add(definition.Name);
            }
            return candidates.ToArray();
        }

        // Per-phantom-menu scroll position, restored on re-entry
        private Dictionary<NativeMenu, int> phantomMenuSelectedIndex = new Dictionary<NativeMenu, int>();

        // One item bundle per possible weapon slot (sized to weaponSlotCount), grown into view one slot at a time from Add Slot like the AdvancedData entries
        private class PhantomWeaponSlotItems
        {
            public NativeMenu Menu;
            public NativeSubmenuItem SubmenuItem;
            public NativeDynamicItem<string> HashItem;
            public NativeDynamicItem<string> SeatItem;
            public NativeDynamicItem<string> ModTypeItem; // null when !weaponModTypeSupported
        }

        private XElement ResolvePhantomElement(HandlingKey handling, string category)
        {
            Dictionary<string, XElement> categoryMap;
            if (!pendingSubDataElements.TryGetValue(handling, out categoryMap)) return null;
            XElement element;
            return categoryMap.TryGetValue(category, out element) ? element : null;
        }

        private static string EncodeElement(XElement element) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(element.ToString())); }
        private XElement DecodeElement(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return null;
            try { return XElement.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))); }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Warn, Dbg.StructuralEditDecodeFailed(ex.Message));
                return null;
            }
        }

        // Every apply returns False when the target no longer exists and always sets reason to why
        // The WeaponSlot*, AiHandlingOverride, and Live AdvancedData families mutate the live vehicle's handling directly
        // Field replays through fieldRegistry and HandlingApply through its snapshots
        // Every other family resolves its own target through an XElement dictionary keyed by the edit's Handling
        private bool ApplySubDataToggle(CategoryToggleEdit edit, bool redo, out string reason)
        {
            reason = null;
            bool toggled = redo ? edit.ToggledAfter : edit.ToggledBefore;
            SetSubDataToggle(edit.Handling, edit.Category, toggled);
            // A category switched back off while the player is standing in its phantom submenu, or one nested in it, leaves them looking at a menu backed by nothing
            // The direct trigger paths cannot reach that state, only an undo or redo can
            if (!toggled)
            {
                EditorContext context = ResolveEditorContextOfHandling(edit.Handling);
                SubHandlingCategory row = context != null ? FindSubHandlingCategory(context, edit.Category) : null;
                if (row != null) BackOutPhantomCategoryMenus(context, row);
            }
            RefreshForHandling(edit.Handling);
            return true;
        }

        // The tree editing handling when its phantom menus are editing element, else null, which is the only case where a replay has phantom rows to refresh
        private EditorContext ResolveContextShowingPhantomElement(HandlingKey handling, XElement element)
        {
            EditorContext context = ResolveEditorContextOfHandling(handling);
            return context != null && context.Phantom.CurrentPhantomElement == element ? context : null;
        }

        private bool ApplyPhantomFieldEdit(PhantomFieldEdit edit, bool redo, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, edit.Category);
            if (element == null)
            {
                reason = "phantom category does not exist";
                return false;
            }
            if (!ApplyPhantomValue(edit, element, redo ? edit.NewValue : edit.OldValue, out reason)) return false;
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null) SyncPhantomFieldValues(GetPhantomMenuForCategory(context, edit.Category));
            return true;
        }

        private bool ApplyPhantomFlagEdit(PhantomFlagEdit edit, bool redo, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, edit.Category);
            if (element == null)
            {
                reason = "phantom category does not exist";
                return false;
            }
            if (edit.BitIndex < 0 || edit.BitIndex >= FLAG_BIT_COUNT)
            {
                reason = "flag bit index out of range";
                return false;
            }
            CategoryDefinition definition = ResolveCategoryDefinition(edit.Category);
            if (definition == null || definition.FlagsMenu == null)
            {
                reason = "category has no flags";
                return false;
            }
            SetPhantomFlagBit(element, edit.BitIndex, redo ? edit.CheckedAfter : edit.CheckedBefore, definition.FlagsMenu.ElementName);
            Vehicle vehicle = ResolveLiveVehicleOfHandling(edit.Handling);
            if (ForcePhantomWorksUpsideDown(element, edit.Category, vehicle)) ReportWorksUpsideDownForced(vehicle);
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null)
            {
                SubHandlingCategory row = FindSubHandlingCategory(context, edit.Category);
                SyncPhantomFieldValues(GetPhantomMenuForCategory(context, edit.Category));
                SyncPhantomFieldValues(row != null ? row.PhantomFlagsMenu : null);
            }
            return true;
        }

        private bool ApplyPhantomWeaponSlotAdd(PhantomWeaponSlotEdit edit, bool redo, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, CATEGORY_WEAPON);
            XElement hashArray = element != null ? element.Element(WEAPON_HASH_ARRAY_TAG) : null;
            if (hashArray == null)
            {
                reason = "phantom category has no weapon hash array";
                return false;
            }
            XElement[] items = ListItemElements(hashArray);
            if (redo)
            {
                if (items.Length >= weaponSlotCount)
                {
                    reason = "weapon slot capacity full (" + weaponSlotCount + "/" + weaponSlotCount + ")";
                    return false;
                }
                hashArray.Add(new XElement(ITEM_TAG, edit.SlotData));
            }
            else
            {
                // History replays in strict reverse order, so by the time this entry is undone every later add or delete has already been reverted
                // The index again names the slot this added
                if (edit.SlotIndex < 0 || edit.SlotIndex >= items.Length)
                {
                    reason = "slot index out of range";
                    return false;
                }
                edit.SlotData = items[edit.SlotIndex].Value;
                items[edit.SlotIndex].Remove();
            }
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null) SyncPhantomWeaponSlotVisibility(context);
            return true;
        }

        private bool ApplyPhantomWeaponSlotDelete(PhantomWeaponSlotEdit edit, bool redo, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, CATEGORY_WEAPON);
            XElement hashArray = element != null ? element.Element(WEAPON_HASH_ARRAY_TAG) : null;
            if (hashArray == null)
            {
                reason = "phantom category has no weapon hash array";
                return false;
            }
            if (redo)
            {
                XElement[] items = ListItemElements(hashArray);
                if (edit.SlotIndex < 0 || edit.SlotIndex >= items.Length)
                {
                    reason = "slot index out of range";
                    return false;
                }
                items[edit.SlotIndex].Remove();
                RemovePhantomWeaponSlotIntArrayItem(element, WEAPON_SEAT_ARRAY_TAG, edit.SlotIndex);
                if (weaponModTypeSupported) RemovePhantomWeaponSlotIntArrayItem(element, WEAPON_MOD_TYPE_ARRAY_TAG, edit.SlotIndex);
                foreach (TurretFieldEntry field in TurretFields) RemovePhantomWeaponSlotFloatArrayItem(element, field.Tag, edit.SlotIndex);
                RemovePhantomTurretPitchLimitItem(element, edit.SlotIndex);
            }
            else
            {
                string[] parts = (edit.SlotData ?? "").Split(',');
                if (parts.Length != WeaponSlotStateFieldCount)
                {
                    reason = "stored slot data malformed";
                    return false;
                }
                int seatRaw, modTypeRaw;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seatRaw))
                {
                    reason = "stored seat value did not parse as an int";
                    return false;
                }
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out modTypeRaw))
                {
                    reason = "stored mod type value did not parse as an int";
                    return false;
                }
                var turretValues = new float[TurretFields.Length];
                for (int i = 0; i < turretValues.Length; i++)
                {
                    if (!ParseFiniteFloat(parts[WEAPON_SLOT_STATE_LEADING_FIELD_COUNT + i], out turretValues[i]))
                    {
                        reason = "stored turret value did not parse as a float";
                        return false;
                    }
                }
                var pitchLimits = new float[TurretPitchLimitFields.Length];
                for (int i = 0; i < pitchLimits.Length; i++)
                {
                    if (!ParseFiniteFloat(parts[WEAPON_SLOT_STATE_LEADING_FIELD_COUNT + turretValues.Length + i], out pitchLimits[i]))
                    {
                        reason = "stored pitch limit value did not parse as a float";
                        return false;
                    }
                }
                XElement[] items = ListItemElements(hashArray);
                XElement restored = new XElement(ITEM_TAG, parts[0]);
                if (edit.SlotIndex >= items.Length) hashArray.Add(restored);
                else items[edit.SlotIndex].AddBeforeSelf(restored);
                InsertPhantomWeaponSlotIntArrayItem(element, WEAPON_SEAT_ARRAY_TAG, edit.SlotIndex, seatRaw);
                if (weaponModTypeSupported) InsertPhantomWeaponSlotIntArrayItem(element, WEAPON_MOD_TYPE_ARRAY_TAG, edit.SlotIndex, modTypeRaw);
                for (int i = 0; i < turretValues.Length; i++) InsertPhantomWeaponSlotFloatArrayItem(element, TurretFields[i].Tag, edit.SlotIndex, turretValues[i]);
                InsertPhantomTurretPitchLimitItem(element, edit.SlotIndex, pitchLimits);
            }
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null)
            {
                SyncPhantomWeaponSlotVisibility(context);
                RefreshPhantomWeaponSlotFieldValuesFrom(context, edit.SlotIndex);
            }
            return true;
        }

        private bool ApplyPhantomWeaponField(PhantomWeaponFieldEdit edit, bool redo, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, CATEGORY_WEAPON);
            if (element == null)
            {
                reason = "phantom category does not exist";
                return false;
            }
            string value = redo ? edit.NewValue : edit.OldValue;
            bool applied = edit.Member == "Hash" ? SetPhantomWeaponHashText(element, edit.SlotIndex, value) : SetPhantomWeaponIntArrayItem(element, edit.Member == "Seat" ? WEAPON_SEAT_ARRAY_TAG : WEAPON_MOD_TYPE_ARRAY_TAG, edit.SlotIndex, value);
            if (!applied)
            {
                reason = "value did not parse for this field";
                return false;
            }
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null) SyncPhantomWeaponSlotFieldValues(context, edit.SlotIndex);
            return true;
        }

        // Self-inverse, so swapping the same two slots again is both the undo and the redo, matching WeaponSlotReorder's own live-side pattern
        private bool ApplyPhantomWeaponSlotReorder(WeaponSlotReorderEdit edit, out string reason)
        {
            reason = null;
            XElement element = ResolvePhantomElement(edit.Handling, CATEGORY_WEAPON);
            if (element == null)
            {
                reason = "phantom category does not exist";
                return false;
            }
            if (!SwapPhantomWeaponSlotData(element, edit.FromIndex, edit.ToIndex))
            {
                reason = "slot swap failed; index out of range";
                return false;
            }
            EditorContext context = ResolveContextShowingPhantomElement(edit.Handling, element);
            if (context != null)
            {
                SyncPhantomWeaponSlotFieldValues(context, edit.FromIndex);
                SyncPhantomWeaponSlotFieldValues(context, edit.ToIndex);
            }
            return true;
        }

        // StateBefore and StateAfter each pack every per-slot field the struct carries (WeaponSlotState, WeaponSlots.cs)
        // So undo and redo of a slot add or delete restore every field the live action touched, not just WeaponHash
        // Every live slot change adds the respawn flag to replayFlags, since the game sets up vehicle weapons at spawn
        private bool ApplyWeaponSlotAdd(WeaponSlotStateEdit edit, bool redo, HandlingData handling, ref ReplayFlags replayFlags, out string reason)
        {
            reason = null;
            if (handling == null || handling.VehicleWeaponHandlingData == null || edit.SlotIndex < 0)
            {
                reason = "vehicle has no weapon handling data";
                return false;
            }
            VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
            if (edit.SlotIndex >= weaponData.WeaponHash.Length)
            {
                reason = "slot index out of range";
                return false;
            }
            WeaponSlotState state;
            if (!UnpackWeaponSlotState(redo ? edit.StateAfter : edit.StateBefore, out state))
            {
                reason = "stored slot data failed to unpack";
                return false;
            }
            ApplyWeaponSlotState(weaponData, edit.SlotIndex, state);
            replayFlags |= ReplayFlags.Respawn;
            RefreshForHandling(edit.Handling);
            return true;
        }

        // Self-inverse, so swapping the same two slots again is both the undo and the redo
        private bool ApplyWeaponSlotReorder(WeaponSlotReorderEdit edit, HandlingData handling, ref ReplayFlags replayFlags, out string reason)
        {
            reason = null;
            if (handling == null || handling.VehicleWeaponHandlingData == null)
            {
                reason = "vehicle has no weapon handling data";
                return false;
            }
            if (CrossesTurretBoundary(edit.FromIndex, edit.ToIndex))
            {
                reason = "slot swap crosses the turret slots on this game build";
                return false;
            }
            if (!SwapWeaponSlotData(handling.VehicleWeaponHandlingData, edit.FromIndex, edit.ToIndex))
            {
                reason = "slot swap failed, index out of range";
                return false;
            }
            replayFlags |= ReplayFlags.Respawn;
            RefreshForHandling(edit.Handling);
            return true;
        }

        // HandlingData.CAIHandlingInfo's setter repoints which of the 4 shared AI-behavior entries this vehicle's own HandlingData references
        // The cycler's display re-reads it from the live handling on the next sync
        private bool ApplyAiHandlingOverride(AiHandlingEdit edit, bool redo, HandlingData handling, out string reason)
        {
            reason = null;
            if (handling == null)
            {
                reason = "no live vehicle";
                return false;
            }
            string value = redo ? edit.NewValue : edit.OldValue;
            CAIHandlingInfo info = CAIHandlingInfo.GetByHash((AIHandlingHash)GTA.StringHash.AtStringHash(value));
            if (!info.IsValid)
            {
                reason = "unknown AI handling " + value;
                return false;
            }
            handling.CAIHandlingInfo = info;
            return true;
        }

        private bool ApplyPhantomValue(PhantomFieldEdit edit, XElement element, string value, out string reason)
        {
            reason = null;
            FieldEntry row = ResolveFieldEntry(edit.Category, edit.Tag);
            if (edit.Kind == StructuralKind.PhantomInt)
            {
                int parsedInt;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
                {
                    reason = "value did not parse as an int";
                    return false;
                }
                SetPhantomIntAttr(element, edit.Tag, row != null ? ApplyHardBounds(row, parsedInt) : parsedInt);
                return true;
            }
            float parsedFloat;
            if (!ParseFiniteFloat(value, out parsedFloat))
            {
                reason = "value did not parse as a float";
                return false;
            }
            float bounded = row != null ? ApplyHardBounds(row, parsedFloat, 0) : parsedFloat;
            if (edit.Kind == StructuralKind.PhantomVector) SetPhantomVecComponent(element, edit.Tag, edit.Component, bounded);
            else SetPhantomFloatAttr(element, edit.Tag, bounded);
            return true;
        }

        // Refreshes the tree editing the handling, since undo and redo are global hotkeys that can fire from anywhere, and does nothing when no tree edits it
        private void RefreshForHandling(HandlingKey handling)
        {
            EditorContext context = ResolveEditorContextOfHandling(handling);
            if (context != null) SyncEditorContext(context);
        }

        // Creates on first entry, reuses on every later entry so edits persist across visits
        private XElement GetOrCreatePhantomElement(HandlingKey handling, string category)
        {
            Dictionary<string, XElement> categoryMap = GetOrAdd(pendingSubDataElements, handling);
            XElement element;
            if (!categoryMap.TryGetValue(category, out element))
            {
                CategoryDefinition definition = ResolveCategoryDefinition(category);
                element = definition != null ? definition.BuildDefaultElement() : null;
                categoryMap[category] = element;
            }
            return element;
        }

        // Creating and entering a category are two distinct presses
        private void ActivatePhantomCategory(EditorContext context, string category)
        {
            Vehicle vehicle = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_SUBDATA_CREATE, NOTIF_NO_VEHICLE_SUBDATA_CREATE);
                return;
            }
            HandlingKey handling = ResolveHandlingKey(vehicle);
            HashSet<string> toggled = GetOrAdd(pendingSubDataToggle, handling);
            toggled.Add(category);
            HashSet<string> adopted;
            if (adoptedPhantomCategories.TryGetValue(handling, out adopted)) adopted.Remove(category);
            if (ForcePhantomWorksUpsideDown(GetOrCreatePhantomElement(handling, category), category, vehicle)) ReportWorksUpsideDownForced(vehicle);
            SyncSubHandlingVisibility(context, vehicle, vehicle.HandlingData);
            // Fields start at the game's parser schema init values, or 0 where the schema sets none, which rarely match the vehicle being built
            // SpecialFlight is the exception, since it starts as a Deluxo
            // This is by design, not a bug, so the disclosure is a one-time reminder, not a fix
            notifications.PostNotificationOnce(NOTIFICATION_KEY_PREFIX_PHANTOM_DEFAULTS + category, NOTIF_PHANTOM_DEFAULTS);

            // Redo never needs a fresh GetOrCreatePhantomElement call, since the element created above stays in pendingSubDataElements regardless of toggle state
            // Re-toggling to True is the whole redo
            // Undo likewise only untoggles and keeps the element, for the same reuse reason
            PushStructuralEdit(new CategoryToggleEdit(StructuralKind.PhantomCreate, category, false, true) { Handling = handling });
        }

        // An adopted category from a file written by another tool can lack a field element, and a write to it changes nothing
        private static void SetPhantomFloatAttr(XElement parent, string name, float value) { WriteChildAttribute(parent, name, "value", FormatSaveFloat(value)); }
        private static void SetPhantomIntAttr(XElement parent, string name, int value) { WriteChildAttribute(parent, name, "value", value.ToString(CultureInfo.InvariantCulture)); }
        private static float GetPhantomVecComponent(XElement parent, string elementName, string component)
        {
            XElement element = parent.Element(elementName);
            if (element == null) return 0.0f;
            XAttribute attribute = element.Attribute(component);
            float parsedValue;
            return attribute != null && float.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue) ? parsedValue : 0.0f;
        }

        private static void SetPhantomVecComponent(XElement parent, string elementName, string component, float value) { WriteChildAttribute(parent, elementName, component, FormatSaveFloat(value)); }

        // The title comes from the table row the live editor builds from, and the row's steps and bounds apply as they do live
        private void WirePhantomFloat(NativeMenu menu, EditorContext owner, string category, FieldEntry entry)
        {
            string title = ResolveFieldTitle(category, entry.Tag);
            var item = new NativeDynamicItem<DisplayFloat>(title, ResolveDescriptor(category, title));
            AddTreeRow(menu, item);
            var template = new PhantomFieldEdit(StructuralKind.PhantomFloat, category, entry.Tag, null, null, null);
            WireFloatField(item, entry, BuildElementFloatStore(menu, item, owner, () => owner.Phantom.CurrentPhantomElement, entry.Tag, "value", template));
        }

        private void WirePhantomInt(NativeMenu menu, EditorContext owner, string category, FieldEntry entry)
        {
            string title = ResolveFieldTitle(category, entry.Tag);
            var item = new NativeDynamicItem<int>(title, ResolveDescriptor(category, title));
            AddTreeRow(menu, item);
            var template = new PhantomFieldEdit(StructuralKind.PhantomInt, category, entry.Tag, null, null, null);
            WireIntField(item, entry, BuildElementIntStore(menu, item, owner, () => owner.Phantom.CurrentPhantomElement, entry.Tag, "value", template));
        }

        // Parses interactive float text and rejects non-finite values before they can enter handling XML
        private static bool ParseFiniteFloat(string candidate, out float value)
        {
            return float.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // Titled like the live editor's own per-axis item: The row's title with the uppercased axis letter appended
        private void WirePhantomVectorComponent(NativeMenu menu, EditorContext owner, string category, FieldEntry entry, string component)
        {
            string title = ResolveFieldTitle(category, entry.Tag) + component.ToUpperInvariant();
            var item = new NativeDynamicItem<DisplayFloat>(title, ResolveDescriptor(category, title));
            AddTreeRow(menu, item);
            var template = new PhantomFieldEdit(StructuralKind.PhantomVector, category, entry.Tag, component, null, null);
            WireFloatField(item, entry, BuildElementFloatStore(menu, item, owner, () => owner.Phantom.CurrentPhantomElement, entry.Tag, component, template));
        }

        // Reads-modifies-writes one bit so a LIFO undo sequence never clobbers a sibling bit's own state
        private void SetPhantomFlagBit(XElement element, int bitIndex, bool value, string elementName)
        {
            XElement flagsElement = element.Element(elementName);
            if (flagsElement == null) return;
            uint current;
            if (!uint.TryParse(flagsElement.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out current))
            {
                logger.Log(Dbg.Level.Warn, Dbg.PhantomFlagsUnparseable(flagsElement.Value));
                return;
            }
            uint next = value ? current | (1u << bitIndex) : current & ~(1u << bitIndex);
            flagsElement.Value = next.ToString("X8");
        }

        // The export-only side of EnforceWorksUpsideDown, since a saved file carries these flags into the game and the vehicle would crash in hover mode once it loads
        // Judged by the vehicle in hand, like the live guard. Returns True only when it turned the flag on, so the caller reports it
        private bool ForcePhantomWorksUpsideDown(XElement element, string category, Vehicle vehicle)
        {
            if (category != CATEGORY_SPECIAL_FLIGHT || element == null || Entity.IsNullOrNotExisting(vehicle) || vehicle.Bones.Contains(HOVER_SLIDE_BONE_NAME)) return false;
            XElement flagsElement = element.Element(SPECIAL_FLIGHT_FLAGS_TAG);
            uint flags;
            if (flagsElement == null || !uint.TryParse(flagsElement.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags)) return false;
            if ((flags & (1u << SF_WORKS_UPSIDE_DOWN_BIT)) != 0) return false;
            SetPhantomFlagBit(element, SF_WORKS_UPSIDE_DOWN_BIT, true, SPECIAL_FLIGHT_FLAGS_TAG);
            return true;
        }

        // True while a display refresh sets the phantom flag checkboxes, so their handlers and the conflict group cascade never treat it as a user action
        private bool syncingPhantomFlagCheckboxes;

        // descriptorCategory keys the descriptor and elementCategory keys the export-only element, which are different names for the same flags
        // onFlagsChanged mirrors the live WireFlagCheckbox's parameter of the same name and is invoked immediately after every toggle, not just on a periodic resync
        // So the owning submenu's hex-suffixed Name updates live as the player checks or unchecks a box
        private void WirePhantomFlagCheckbox(NativeMenu menu, EditorContext owner, string descriptorCategory, string elementCategory, string fieldName, int bitIndex, List<FlagCheckboxBinding> bindings, Action<uint> onFlagsChanged = null)
        {
            string elementName = ResolveCategoryDefinition(elementCategory).FlagsMenu.ElementName;
            string baseDescription = ResolveDescriptor(descriptorCategory, fieldName);
            var item = new NativeCheckboxItem(StripFlagPrefix(fieldName), FlagHashPrefix(bitIndex) + baseDescription, false);
            DimCheckboxWhileDisabled(item);
            AddTreeRow(menu, item);
            bindings.Add(new FlagCheckboxBinding { Item = item, BitIndex = bitIndex, BaseDescription = baseDescription });
            RegisterUndoFocusItem(PhantomFlagEdit.BuildFocusKey(elementCategory, fieldName, bitIndex), item);
            SubscribeCheckboxChanged(item, "WirePhantomFlagCheckbox.CheckboxChanged", () =>
            {
                XElement targetElement = owner.Phantom.CurrentPhantomElement;
                if (syncingPhantomFlagCheckboxes || targetElement == null) return;
                XElement flagsElement = targetElement.Element(elementName);
                if (flagsElement == null) return;
                bool previousChecked = !item.Checked;
                bool newChecked = item.Checked;
                SetPhantomFlagBit(targetElement, bitIndex, newChecked, elementName);
                PushStructuralEditForContext(owner, new PhantomFlagEdit(elementCategory, fieldName, bitIndex, previousChecked, newChecked));
                if (ForcePhantomWorksUpsideDown(targetElement, elementCategory, owner.TargetVehicle))
                {
                    ReportWorksUpsideDownForced(owner.TargetVehicle);
                    SyncPhantomFlagCheckboxes(owner, bindings, elementName, onFlagsChanged);
                }
                if (onFlagsChanged != null)
                {
                    uint flags;
                    uint.TryParse(flagsElement.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags);
                    onFlagsChanged(flags);
                }
            });
        }

        // Registered once for the whole bindings list so a conflict group syncs atomically
        // onFlagsChanged mirrors the live WireFlagCheckbox's parameter of the same name, for the hex-suffixed menu Name update
        private void SyncPhantomFlagCheckboxes(EditorContext owner, List<FlagCheckboxBinding> bindings, string elementName, Action<uint> onFlagsChanged = null)
        {
            uint flags = 0;
            XElement phantomElement = owner.Phantom.CurrentPhantomElement;
            if (phantomElement != null)
            {
                XElement flagsElement = phantomElement.Element(elementName);
                if (flagsElement != null) uint.TryParse(flagsElement.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags);
            }
            try
            {
                syncingPhantomFlagCheckboxes = true;
                for (int i = 0; i < bindings.Count; i++) bindings[i].Item.Checked = (flags & (1u << bindings[i].BitIndex)) != 0;
            }
            finally { syncingPhantomFlagCheckboxes = false; }
            if (elementName == SPECIAL_FLIGHT_FLAGS_TAG) ApplyWorksUpsideDownLock(bindings, flags, owner.TargetVehicle);
            if (onFlagsChanged != null) onFlagsChanged(flags);
        }

        private NativeMenu GetPhantomMenuForCategory(EditorContext context, string category)
        {
            SubHandlingCategory row = FindSubHandlingCategory(context, category);
            return row != null ? row.PhantomMenu : null;
        }

        // Keyed per menu so syncing one phantom category never touches another's items
        private Dictionary<NativeMenu, List<Action>> phantomSyncActionsByMenu = new Dictionary<NativeMenu, List<Action>>();
        private void AddPhantomSyncAction(NativeMenu menu, Action action)
        {
            List<Action> actions = GetOrAdd(phantomSyncActionsByMenu, menu);
            actions.Add(action);
        }

        // Clears the saved scroll position of the tree editing handling, so a recreated category opens at the top
        private void ForgetPhantomScrollPosition(HandlingKey handling, string category)
        {
            EditorContext context = ResolveEditorContextOfHandling(handling);
            NativeMenu menu = context != null ? GetPhantomMenuForCategory(context, category) : null;
            if (menu != null) phantomMenuSelectedIndex.Remove(menu);
        }

        private void SyncPhantomFieldValues(NativeMenu menu)
        {
            // A structural entry restored from disk can name a category this build no longer resolves to a menu
            // The dictionary lookup below throws on a null key rather than missing, so the guard has to come first
            if (menu == null) return;
            List<Action> actions;
            if (!phantomSyncActionsByMenu.TryGetValue(menu, out actions)) return;
            for (int i = 0; i < actions.Count; i++) actions[i]();
        }

        // ==========================
        // Persistence across reloads
        // ==========================
        // A handling whose name hash does not verify is left out
        // No post-load resync is needed, since Tick.cs calls SyncFieldValues() the first tick a vehicle becomes current
        private void WritePhantomSubDataHistory(StringBuilder sb)
        {
            foreach (KeyValuePair<HandlingKey, HashSet<string>> handlingToggles in pendingSubDataToggle)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(handlingToggles.Key);
                if (handlingHash == null) continue;
                foreach (string category in handlingToggles.Value) sb.Append(handlingHash).Append('|').Append(TOGGLE_LINE_TAG).Append('|').Append(category).AppendLine();
            }
            foreach (KeyValuePair<HandlingKey, HashSet<string>> handlingAdopted in adoptedPhantomCategories)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(handlingAdopted.Key);
                if (handlingHash == null) continue;
                foreach (string category in handlingAdopted.Value) sb.Append(handlingHash).Append('|').Append(ADOPTED_LINE_TAG).Append('|').Append(category).AppendLine();
            }
            foreach (KeyValuePair<HandlingKey, Dictionary<string, XElement>> handlingCategories in pendingSubDataElements)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(handlingCategories.Key);
                if (handlingHash == null) continue;
                foreach (KeyValuePair<string, XElement> categoryElement in handlingCategories.Value)
                {
                    string encodedElement = EncodeElement(categoryElement.Value);
                    sb.Append(handlingHash).Append('|').Append(ELEMENT_LINE_TAG).Append('|').Append(categoryElement.Key).Append('|').Append(encodedElement).AppendLine();
                }
            }
            foreach (KeyValuePair<HandlingKey, XElement> handlingAdvancedData in pendingAdvancedData)
            {
                string handlingHash = reloadState.FormatReloadStateHandlingHash(handlingAdvancedData.Key);
                if (handlingHash == null) continue;
                string encodedElement = EncodeElement(handlingAdvancedData.Value);
                sb.Append(handlingHash).Append('|').Append(PENDING_ADVANCED_DATA_LINE_TAG).Append('|').Append(encodedElement).AppendLine();
            }
        }

        private bool ConsumePhantomSubDataLine(string raw)
        {
            string[] parts = raw.Split('|');
            HandlingKey handling;
            if (parts.Length < 3 || !reloadState.ResolveReloadStateHandling(ReloadStateSection.PhantomSubData, parts[0], out handling)) return false;
            if (parts.Length == 3 && parts[1] == TOGGLE_LINE_TAG)
            {
                HashSet<string> toggled = GetOrAdd(pendingSubDataToggle, handling);
                toggled.Add(parts[2]);
                return true;
            }
            if (parts.Length == 3 && parts[1] == ADOPTED_LINE_TAG)
            {
                HashSet<string> adopted = GetOrAdd(adoptedPhantomCategories, handling);
                adopted.Add(parts[2]);
                return true;
            }
            if (parts.Length == 4 && parts[1] == ELEMENT_LINE_TAG)
            {
                XElement element = DecodeElement(parts[3]);
                if (element == null) return false;
                Dictionary<string, XElement> categoryMap = GetOrAdd(pendingSubDataElements, handling);
                categoryMap[parts[2]] = element;
                return true;
            }
            if (parts.Length == 3 && parts[1] == PENDING_ADVANCED_DATA_LINE_TAG)
            {
                XElement element = DecodeElement(parts[2]);
                if (element == null) return false;
                pendingAdvancedData[handling] = element;
                return true;
            }
            return false;
        }
    }
}
