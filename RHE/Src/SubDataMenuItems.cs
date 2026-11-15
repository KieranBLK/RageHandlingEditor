using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The rows' data type is never touched, since phantom fields read and write owner's phantom element, not a live struct
        private IEnumerable<bool> BuildPhantomCategoryMenuItems(NativeMenu menu, EditorContext owner, string category, FieldEntry[] fields)
        {
            foreach (FieldEntry entry in fields)
            {
                if (entry.IsInt)
                {
                    WirePhantomInt(menu, owner, category, entry);
                    yield return true;
                }
                else if (entry.IsVector)
                {
                    WirePhantomVectorComponent(menu, owner, category, entry, "x");
                    yield return true;
                    WirePhantomVectorComponent(menu, owner, category, entry, "y");
                    yield return true;
                    WirePhantomVectorComponent(menu, owner, category, entry, "z");
                    yield return true;
                }
                else
                {
                    WirePhantomFloat(menu, owner, category, entry);
                    yield return true;
                }
            }
        }

        // targetSlot is always a real EditorContext (currentContext for Current), and every terminal field and menu assignment writes directly into it
        private IEnumerable<bool> BuildPhantomCategoryMenu(EditorContext targetSlot, SubHandlingCategory row)
        {
            CategoryDefinition definition = row.Definition;
            NativeMenu menu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(SUBTITLE_EXPORT_ONLY, definition.LongName));
            row.PhantomMenu = menu;
            foreach (bool step in BuildPhantomCategoryMenuItems(menu, targetSlot, definition.Name, definition.Fields)) yield return step;
            foreach (bool step in BuildPhantomCategoryMenuItems(menu, targetSlot, definition.Name, definition.ExtraFields)) yield return step;
            if (definition.FlagsMenu != null) foreach (bool step in BuildPhantomFlagsMenuItems(menu, targetSlot, row)) yield return step;
            if (definition.HasAdvancedData) foreach (bool step in BuildPhantomAdvancedDataMenuItems(menu, targetSlot)) yield return step;
            if (definition.HasWeaponSlots) foreach (bool step in BuildPhantomWeaponSlotMenuItems(menu, targetSlot)) yield return step;
        }

        // An always-present nested submenu directly inside menu, matching the live editor's own structure
        private IEnumerable<bool> BuildPhantomFlagsMenuItems(NativeMenu menu, EditorContext targetSlot, SubHandlingCategory row)
        {
            CategoryDefinition definition = row.Definition;
            CategoryFlagsFacts flagsFacts = definition.FlagsMenu;
            NativeMenu flagsMenu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(SUBTITLE_EXPORT_ONLY, flagsFacts.RowTitle));
            row.PhantomFlagsMenu = flagsMenu;
            if (flagsFacts.MenuDescriptionKey != null)
            {
                flagsMenu.Description = ResolveDescriptor(definition.Name, flagsFacts.MenuDescriptionKey);
                TrackMenuDescription(flagsMenu, definition.Name, flagsFacts.MenuDescriptionKey);
            }
            var flagBindings = new List<FlagCheckboxBinding>();
            // Shared by every per-toggle checkbox and the periodic resync below, so both paths update the exact same subtitle the exact same way
            Action<uint> updateFlagsName = flagsValue => flagsMenu.Name = FlagsMenuName(flagsFacts.SubtitleTemplate, flagsValue, flagsFacts.Entries);
            for (int i = 0; i < flagsFacts.Entries.Length; i++)
            {
                WirePhantomFlagCheckbox(flagsMenu, targetSlot, flagsFacts.DescriptorCategory, definition.Name, flagsFacts.Entries[i].Name, flagsFacts.Entries[i].BitIndex, flagBindings, updateFlagsName);
                yield return true;
            }
            WireFlagConflicts(flagBindings, flagsFacts.Entries);
            AddPhantomSyncAction(flagsMenu, () => SyncPhantomFlagCheckboxes(targetSlot, flagBindings, flagsFacts.ElementName, updateFlagsName));
            AddStyledSubmenu(menu, flagsMenu, flagsFacts.RowTitle);
            SubscribeShown(flagsMenu, "BuildPhantomFlagsMenuItems.Shown", () => SyncPhantomFieldValues(flagsMenu));
            yield return true;
        }

        // AdvancedData is a create-gated nested submenu, constructed directly instead of through AddStyledSubmenu, which would add it to menu immediately
        // SyncPhantomCarAdvancedDataSlot swaps it in for the disabled placeholder only once the category is actually activated
        private IEnumerable<bool> BuildPhantomAdvancedDataMenuItems(NativeMenu menu, EditorContext targetSlot)
        {
            XmlAdvancedDataStore store = ResolveXmlAdvancedDataStore(AdvancedDataMenuKind.Phantom);
            targetSlot.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom].ListMenu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(SUBTITLE_EXPORT_ONLY, "AdvancedData"));
            foreach (bool step in BuildXmlAdvancedDataEntrySets(targetSlot, store, menu)) yield return step;
            NativeItem placeholderItem = new NativeItem("AdvancedData", PLACEHOLDER_CATEGORY_NOT_CREATED);
            DimItemAsDisabled(placeholderItem);
            SubscribeActivated(placeholderItem, "BuildPhantomAdvancedDataMenuItems.Activated", () => store.PressCreate(this, targetSlot));
            targetSlot.AdvancedData.PhantomPlaceholderItem = placeholderItem;
            AddPhantomSyncAction(menu, () => SyncPhantomCarAdvancedDataSlot(targetSlot));
        }

        // Turret fields stay excluded: They are raw-offset backed with no live MemoryAddress to read from a phantom vehicle
        private IEnumerable<bool> BuildPhantomWeaponSlotMenuItems(NativeMenu menu, EditorContext targetSlot)
        {
            var slots = new PhantomWeaponSlotItems[weaponSlotCount];
            for (int slot = 0; slot < weaponSlotCount; slot++)
            {
                int slotIndex = slot;
                var set = new PhantomWeaponSlotItems();
                set.Menu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(SUBTITLE_WEAPON_SLOT_EXPORT_ONLY, (slotIndex + 1)));
                // Built without AddStyledSubmenu's own immediate parent.Add, since every slot beyond the live count would otherwise already sit in menu.Items at build time
                // SyncPhantomWeaponSlotVisibility's own first Add() would then throw "item already part of the menu" the moment it tries to reveal a slot
                set.SubmenuItem = new StyledSubmenuItem(set.Menu, menu);
                set.SubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT, (slotIndex + 1));
                set.Menu.Description = DESC_REORDER_SLOT_PHANTOM;
                var hashItem = new NativeDynamicItem<string>("WeaponHash", WeaponHashDescription(0), WeaponHashTable[0].DisplayName);
                stringCyclerItems.Add(hashItem);
                AddTreeRow(set.Menu, hashItem);
                set.HashItem = hashItem;
                WirePhantomWeaponHashCycler(hashItem, targetSlot, slotIndex);
                yield return true;
                var seatItem = new NativeDynamicItem<string>("WeaponSeat", ResolveDescriptor(CATEGORY_WEAPON, "WeaponSeat"), VehicleSeatNames[0]);
                stringCyclerItems.Add(seatItem);
                AddTreeRow(set.Menu, seatItem);
                TrackDescription(seatItem, CATEGORY_WEAPON);
                set.SeatItem = seatItem;
                WirePhantomWeaponSeatCycler(seatItem, targetSlot, slotIndex);
                yield return true;
                if (weaponModTypeSupported)
                {
                    var modTypeItem = new NativeDynamicItem<string>("WeaponVehicleModType", ResolveDescriptor(CATEGORY_WEAPON, "WeaponVehicleModType"), VehicleModTypeNames[0]);
                    stringCyclerItems.Add(modTypeItem);
                    AddTreeRow(set.Menu, modTypeItem);
                    TrackDescription(modTypeItem, CATEGORY_WEAPON);
                    set.ModTypeItem = modTypeItem;
                    WirePhantomWeaponModTypeCycler(modTypeItem, targetSlot, slotIndex);
                    yield return true;
                }
                slots[slotIndex] = set;
            }
            targetSlot.Phantom.PhantomWeaponSlots = slots;
            NativeItem addSlotItem = new NativeItem(TITLE_ADD_SLOT, DESC_ADD_WEAPON_SLOT_PHANTOM);
            AddTreeRow(menu, addSlotItem);
            SubscribeActivated(addSlotItem, "BuildPhantomWeaponSlotMenuItems.Activated", () => AddPhantomWeaponSlot(targetSlot));
            targetSlot.Phantom.AddPhantomWeaponSlotItem = addSlotItem;
            yield return true;
        }

        // Index growth only ever happens via AddPhantomWeaponSlot, never through a field edit
        private static string GetPhantomWeaponHashText(XElement parent, int index)
        {
            XElement hashArrayElement = parent.Element(WEAPON_HASH_ARRAY_TAG);
            if (hashArrayElement == null || index < 0) return null;
            XElement[] items = ListItemElements(hashArrayElement);
            return index < items.Length ? items[index].Value : null;
        }

        private static bool SetPhantomWeaponHashText(XElement parent, int index, string text)
        {
            XElement hashArrayElement = parent.Element(WEAPON_HASH_ARRAY_TAG);
            if (hashArrayElement == null || index < 0) return false;
            XElement[] items = ListItemElements(hashArrayElement);
            if (index >= items.Length) return false;
            items[index].Value = text ?? "";
            return true;
        }

        // Shorter arrays read as 0 up to index, matching the missing-defaults-to-0 convention ApplyWeaponHandlingXml applies when it reads a document
        // Read from the element's text on every call, since the element is the only copy of the values
        private int GetPhantomWeaponIntArrayItem(XElement parent, string name, int index)
        {
            int[] values = ReadIntArrayElement(parent, name);
            return index >= 0 && index < values.Length ? values[index] : 0;
        }

        private bool SetPhantomWeaponIntArrayItem(XElement parent, string name, int index, string valueText)
        {
            int value;
            if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
            int[] values = ReadIntArrayElement(parent, name);
            if (index >= values.Length) Array.Resize(ref values, index + 1);
            values[index] = value;
            ReplaceIntArrayElement(parent, name, values, values.Length);
            return true;
        }

        // Phantom weapon slot deletion removes and shifts rather than clearing in place
        private void RemovePhantomWeaponSlotIntArrayItem(XElement parent, string name, int index)
        {
            if (parent.Element(name) == null) return;
            int[] values = ReadIntArrayElement(parent, name);
            if (index < 0 || index >= values.Length) return;
            ReplaceIntArrayElement(parent, name, RemoveArrayItem(values, index), values.Length - 1);
        }

        private void InsertPhantomWeaponSlotIntArrayItem(XElement parent, string name, int index, int value)
        {
            int[] existing = ReadIntArrayElement(parent, name);
            int[] values = InsertArrayItem(existing, Math.Max(0, Math.Min(index, existing.Length)), value);
            ReplaceIntArrayElement(parent, name, values, values.Length);
        }

        // Shorter arrays read as 0 up to index, matching GetPhantomWeaponIntArrayItem's missing-defaults-to-0 convention
        // Read fresh from XML rather than cached, since these 7 fields are never displayed through a live per-tick cycler
        // They are only touched by a slot swap, a delete, or the save-time trim
        private float GetPhantomWeaponFloatArrayItem(XElement parent, string name, int index)
        {
            float[] values = ReadFloatArrayElement(parent, name);
            return index >= 0 && index < values.Length ? values[index] : 0.0f;
        }

        private void SetPhantomWeaponFloatArrayItem(XElement parent, string name, int index, float value)
        {
            float[] values = ReadFloatArrayElement(parent, name);
            if (index >= values.Length) Array.Resize(ref values, index + 1);
            values[index] = value;
            ReplaceFloatArrayElement(parent, name, values, values.Length);
        }

        private void RemovePhantomWeaponSlotFloatArrayItem(XElement parent, string name, int index)
        {
            if (parent.Element(name) == null) return;
            float[] values = ReadFloatArrayElement(parent, name);
            if (index < 0 || index >= values.Length) return;
            ReplaceFloatArrayElement(parent, name, RemoveArrayItem(values, index), values.Length - 1);
        }

        // An array shorter than index is padded with 0 up to it first, so the value always lands back at the slot it was removed from
        private void InsertPhantomWeaponSlotFloatArrayItem(XElement parent, string name, int index, float value)
        {
            float[] existing = ReadFloatArrayElement(parent, name);
            float[] padded = PadOrTrim(existing, Math.Max(existing.Length, index), 0.0f);
            float[] values = InsertArrayItem(padded, Math.Max(0, index), value);
            ReplaceFloatArrayElement(parent, name, values, values.Length);
        }

        // A missing Item reads as the init defaults, matching how the game loads an omitted entry
        private float[] GetPhantomTurretPitchLimitItem(XElement parent, int index)
        {
            XElement element = parent.Element(TURRET_PITCH_LIMIT_TAG);
            XElement[] items = element != null ? ListItemElements(element) : new XElement[0];
            return ReadTurretPitchLimitItem(index >= 0 && index < items.Length ? items[index] : null);
        }

        private void SetPhantomTurretPitchLimitItem(XElement parent, int index, float[] values)
        {
            XElement element = ResolvePhantomTurretPitchLimitElement(parent);
            PadTurretPitchLimitItems(element, index + 1)[index].ReplaceWith(BuildTurretPitchLimitItem(values));
        }

        private void RemovePhantomTurretPitchLimitItem(XElement parent, int index)
        {
            XElement element = parent.Element(TURRET_PITCH_LIMIT_TAG);
            if (element == null) return;
            XElement[] items = ListItemElements(element);
            if (index >= 0 && index < items.Length) items[index].Remove();
        }

        private void InsertPhantomTurretPitchLimitItem(XElement parent, int index, float[] values)
        {
            XElement element = ResolvePhantomTurretPitchLimitElement(parent);
            XElement[] items = PadTurretPitchLimitItems(element, index);
            if (index < items.Length) items[index].AddBeforeSelf(BuildTurretPitchLimitItem(values));
            else element.Add(BuildTurretPitchLimitItem(values));
        }

        // Adds default Items until the element holds count of them, and returns every Item in order
        private static XElement[] PadTurretPitchLimitItems(XElement element, int count)
        {
            XElement[] items = ListItemElements(element);
            if (items.Length >= count) return items;
            for (int i = items.Length; i < count; i++) element.Add(BuildTurretPitchLimitItem(DefaultTurretPitchLimitValues()));
            return ListItemElements(element);
        }

        // A phantom element created before the block existed has none, so the block is added on first write
        private static XElement ResolvePhantomTurretPitchLimitElement(XElement parent)
        {
            XElement element = parent.Element(TURRET_PITCH_LIMIT_TAG);
            if (element == null)
            {
                element = new XElement(TURRET_PITCH_LIMIT_TAG);
                parent.Add(element);
            }
            return element;
        }

        // targetSlot is currentContext for Current's own phantom tree, or an AdditionalEditor, and the identical structure is built into that context's own fields
        // rows are that tree's rows, which this fills with the export-only menus
        // phantomMenus gets every menu it builds, in the order the tree's AllOwnMenus lists them
        private IEnumerable<bool> BuildPhantomSubDataMenuItems(EditorContext targetSlot, SubHandlingCategory[] rows, List<NativeMenu> phantomMenus)
        {
            // Each category's menu passes its own name to its rows, since categories can share field names
            foreach (SubHandlingCategory row in rows) foreach (bool step in BuildPhantomCategoryMenu(targetSlot, row)) yield return step;
            foreach (SubHandlingCategory row in rows)
            {
                NativeMenu menu = row.PhantomMenu;
                string category = row.Definition.Name;
                SubscribeShown(menu, "BuildPhantomSubDataMenuItems.Shown.CategoryElement", () =>
                {
                    Vehicle vehicle = targetSlot.TargetVehicle;
                    if (!Entity.IsNullOrNotExisting(vehicle)) targetSlot.Phantom.CurrentPhantomElement = GetOrCreatePhantomElement(ResolveHandlingKey(vehicle), category);
                    SyncPhantomFieldValues(menu);
                    // The menu does not retain its scroll position across a hide and show cycle, so it is restored here instead of resetting to the top item
                    // No saved position explicitly resets the menu to its first item
                    int savedIndex;
                    if (menu.Items.Count > 0) menu.SelectedIndex = phantomMenuSelectedIndex.TryGetValue(menu, out savedIndex) ? Math.Max(0, Math.Min(savedIndex, menu.Items.Count - 1)) : 0;
                });
                // Skipped during a forced back-out, since undoing a category's creation from inside its own submenu closes it
                // Recording that position would put the scroll straight back on the next creation, defeating the reset
                SubscribeClosed(menu, "BuildPhantomSubDataMenuItems.Closed", () => { if (navigation.Transition != NavigationTransition.BackOut) phantomMenuSelectedIndex[menu] = menu.SelectedIndex; });
                // Resyncs slot visibility on every entry, not just after AddPhantomWeaponSlot
                // A weapon slot replay refreshes only a tree showing the edited element at that moment, so this is the net for a tree that was showing another category
                if (row.Definition.HasWeaponSlots) SubscribeShown(menu, "BuildPhantomSubDataMenuItems.Shown.WeaponSlotVisibility", () => SyncPhantomWeaponSlotVisibility(targetSlot));
                // Refreshes the nested Flags submenu's subtitle the moment its parent category is shown, matching the live editor's refresh on every sync (MenuWiring.cs)
                // Without this the subtitle only updates once the player has entered the Flags submenu itself
                NativeMenu flagsMenu = row.PhantomFlagsMenu;
                if (flagsMenu != null) SubscribeShown(menu, "BuildPhantomSubDataMenuItems.Shown.FlagsSubtitle", () => SyncPhantomFieldValues(flagsMenu));
            }

            // The nested Flags and AdvancedData menus and the weapon slot submenus are appended after the category menus
            // They skip the per-category Shown and Closed wiring, since their element comes from the parent category menu's own Shown
            // The caller applies the shared pool, width, and field-sync setup to these nested menus too
            foreach (SubHandlingCategory row in rows) phantomMenus.Add(row.PhantomMenu);
            foreach (SubHandlingCategory row in rows)
            {
                if (row.PhantomFlagsMenu != null) phantomMenus.Add(row.PhantomFlagsMenu);
                if (row.Definition.HasAdvancedData) phantomMenus.Add(targetSlot.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom].ListMenu);
            }
            PhantomWeaponSlotItems[] weaponSlotSet = targetSlot.Phantom.PhantomWeaponSlots;
            foreach (var slot in weaponSlotSet) phantomMenus.Add(slot.Menu);
            yield return true;
        }
    }
}
