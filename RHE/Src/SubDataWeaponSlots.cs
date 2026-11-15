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
        // Required after any shift (delete or its undo/redo), since a tree's phantom weapon slot rows are fixed per index and bound by closure
        // The XML shift never moves them
        private void RefreshPhantomWeaponSlotFieldValuesFrom(EditorContext context, int startIndex)
        {
            for (int i = Math.Max(0, startIndex); i < context.Phantom.CurrentPhantomWeaponSlotCount; i++) SyncPhantomWeaponSlotFieldValues(context, i);
        }

        private void WirePhantomWeaponHashCycler(NativeDynamicItem<string> item, EditorContext owner, int slotIndex)
        {
            ShowRowArrows(item);
            RegisterUndoFocusItem(PhantomWeaponFieldEdit.BuildFocusKey(slotIndex, "Hash"), item);
            SubscribeItemChanged(item, "WirePhantomWeaponHashCycler.ItemChanged", e =>
            {
                XElement element = owner.Phantom.CurrentPhantomElement;
                if (element == null) return;
                string previous = (string)e.Object;
                int index = Array.FindIndex(WeaponHashTable, entry => entry.DisplayName == previous);
                // The cycle skips None (table index 0), so it only ever lands on a real weapon. From Custom or None, Right lands on the first weapon and Left on the last
                int weaponCount = WeaponHashTable.Length - 1;
                if (index < 1) index = e.Direction == LemonUI.Menus.Direction.Left ? weaponCount : 1;
                else index = e.Direction == LemonUI.Menus.Direction.Left ? (index - 2 + weaponCount) % weaponCount + 1 : index % weaponCount + 1;
                string nativeText = WeaponHashTable[index].NativeName;
                string displayText = WeaponHashTable[index].DisplayName;
                string oldText = GetPhantomWeaponHashText(element, slotIndex) ?? "";
                if (!SetPhantomWeaponHashText(element, slotIndex, nativeText)) return;
                e.Object = displayText;
                item.Description = WeaponHashDescription(WeaponHashTable[index].Hash);
                // Same content-aware title refresh as the live cycler, without which the top-level list only updates on reorder, not on the weapon actually changing
                owner.Phantom.PhantomWeaponSlots[slotIndex].SubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT_NAMED, (slotIndex + 1), displayText);
                RecordStructuralCycleEdit(new PhantomWeaponFieldEdit(slotIndex, "Hash", oldText, nativeText), owner);
                pendingWidthRecalc = true;
            });
            // A raw hash typed here is stored as its number
            SubscribeActivated(item, "WirePhantomWeaponHashCycler.Activated", () =>
            {
                XElement element = owner.Phantom.CurrentPhantomElement;
                if (element == null) return;
                string oldText = GetPhantomWeaponHashText(element, slotIndex) ?? "";
                string currentDisplay = item.SelectedItem;
                TypedWeaponHashResult typed = PromptForWeaponHash(currentDisplay == CUSTOM_WEAPON_HASH_LABEL ? oldText : currentDisplay);
                if (typed == null)
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                string newText = typed.Kind == TypedWeaponHashKind.Table ? WeaponHashTable[typed.TableIndex].NativeName : ((VehicleWeaponHash)typed.RawHash).ToString();
                if (newText == oldText || !SetPhantomWeaponHashText(element, slotIndex, newText)) return;
                SyncPhantomWeaponSlotFieldValues(owner, slotIndex);
                PushStructuralEditForContext(owner, new PhantomWeaponFieldEdit(slotIndex, "Hash", oldText, newText));
                pendingWidthRecalc = true;
            });
        }

        // toRaw converts the parsed name to its stored int form (Seat's own +1 offset lives entirely in its own toRaw closure)
        // WirePhantomWeaponHashCycler keeps its own body, since it writes a hash-array element, not this int-array shape
        private void WirePhantomNameTableCycler(NativeDynamicItem<string> item, EditorContext owner, int slotIndex, string arrayName, string[] names, string structuralMember, Func<string, int> toRaw)
        {
            ShowRowArrows(item);
            RegisterUndoFocusItem(PhantomWeaponFieldEdit.BuildFocusKey(slotIndex, structuralMember), item);
            SubscribeItemChanged(item, "WirePhantomNameTableCycler.ItemChanged", e =>
            {
                XElement element = owner.Phantom.CurrentPhantomElement;
                if (element == null) return;
                string previous = (string)e.Object;
                int index = Array.IndexOf(names, previous);
                if (index < 0) index = 0;
                index = e.Direction == LemonUI.Menus.Direction.Left ? (index - 1 + names.Length) % names.Length : (index + 1) % names.Length;
                int oldRaw = GetPhantomWeaponIntArrayItem(element, arrayName, slotIndex);
                int newRaw = toRaw(names[index]);
                if (!SetPhantomWeaponIntArrayItem(element, arrayName, slotIndex, newRaw.ToString(CultureInfo.InvariantCulture))) return;
                e.Object = names[index];
                RecordStructuralCycleEdit(new PhantomWeaponFieldEdit(slotIndex, structuralMember, oldRaw.ToString(CultureInfo.InvariantCulture), newRaw.ToString(CultureInfo.InvariantCulture)), owner);
                pendingWidthRecalc = true;
            });
        }

        private void WirePhantomWeaponSeatCycler(NativeDynamicItem<string> item, EditorContext owner, int slotIndex)
        {
            WirePhantomNameTableCycler(item, owner, slotIndex, WEAPON_SEAT_ARRAY_TAG, VehicleSeatNames, "Seat", name => (int)(VehicleSeat)Enum.Parse(typeof(VehicleSeat), name) + 1);
        }

        private void WirePhantomWeaponModTypeCycler(NativeDynamicItem<string> item, EditorContext owner, int slotIndex)
        {
            WirePhantomNameTableCycler(item, owner, slotIndex, WEAPON_MOD_TYPE_ARRAY_TAG, VehicleModTypeNames, "ModType", name => (int)(VehicleModType)Enum.Parse(typeof(VehicleModType), name));
        }

        // No slot is ever auto-created, only grown one at a time from this trailing item
        // The cap at weaponSlotCount matches the live editor's own ceiling
        private void AddPhantomWeaponSlot(EditorContext context)
        {
            XElement element = context.Phantom.CurrentPhantomElement;
            if (element == null) return;
            XElement hashArray = element.Element(WEAPON_HASH_ARRAY_TAG);
            if (hashArray == null) return;
            int currentCount = CountItemElements(hashArray);
            if (currentCount >= weaponSlotCount)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_WEAPON_FULL, NOTIF_WEAPON_FULL);
                return;
            }
            string text = WeaponHashTable[DEFAULT_PHANTOM_WEAPON_SLOT_HASH_INDEX].NativeName;
            hashArray.Add(new XElement(ITEM_TAG, text));
            SyncPhantomWeaponSlotVisibility(context);
            PushStructuralEditForContext(context, new PhantomWeaponSlotEdit(StructuralKind.PhantomWeaponSlotAdd, currentCount, text));
        }

        private bool SwapPhantomWeaponSlotData(XElement phantomElement, int fromSlot, int toSlot)
        {
            if (phantomElement == null) return false;
            string fromHash = GetPhantomWeaponHashText(phantomElement, fromSlot);
            string toHash = GetPhantomWeaponHashText(phantomElement, toSlot);
            if (fromHash == null || toHash == null) return false;
            if (!SetPhantomWeaponHashText(phantomElement, fromSlot, toHash) || !SetPhantomWeaponHashText(phantomElement, toSlot, fromHash)) return false;
            string fromSeat = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, fromSlot).ToString(CultureInfo.InvariantCulture);
            string toSeat = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, toSlot).ToString(CultureInfo.InvariantCulture);
            SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, fromSlot, toSeat);
            SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_SEAT_ARRAY_TAG, toSlot, fromSeat);
            if (weaponModTypeSupported)
            {
                string fromMod = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, fromSlot).ToString(CultureInfo.InvariantCulture);
                string toMod = GetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, toSlot).ToString(CultureInfo.InvariantCulture);
                SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, fromSlot, toMod);
                SetPhantomWeaponIntArrayItem(phantomElement, WEAPON_MOD_TYPE_ARRAY_TAG, toSlot, fromMod);
            }
            foreach (TurretFieldEntry field in TurretFields)
            {
                float fromValue = GetPhantomWeaponFloatArrayItem(phantomElement, field.Tag, fromSlot);
                float toValue = GetPhantomWeaponFloatArrayItem(phantomElement, field.Tag, toSlot);
                SetPhantomWeaponFloatArrayItem(phantomElement, field.Tag, fromSlot, toValue);
                SetPhantomWeaponFloatArrayItem(phantomElement, field.Tag, toSlot, fromValue);
            }
            float[] fromPitchLimits = GetPhantomTurretPitchLimitItem(phantomElement, fromSlot);
            float[] toPitchLimits = GetPhantomTurretPitchLimitItem(phantomElement, toSlot);
            SetPhantomTurretPitchLimitItem(phantomElement, fromSlot, toPitchLimits);
            SetPhantomTurretPitchLimitItem(phantomElement, toSlot, fromPitchLimits);
            return true;
        }

        // direction is -1 or +1
        private void ReorderPhantomWeaponSlot(EditorContext context, int direction)
        {
            NativeMenu menu = GetPhantomMenuForCategory(context, CATEGORY_WEAPON);
            PhantomWeaponSlotItems[] slots = context.Phantom.PhantomWeaponSlots;
            int count = context.Phantom.CurrentPhantomWeaponSlotCount;
            XElement element = context.Phantom.CurrentPhantomElement;
            if (element == null) return;
            int fromSlot = GetHighlightedPhantomWeaponSlotIndex(menu, slots);
            int toSlot = fromSlot + direction;
            if (fromSlot < 0 || toSlot < 0 || toSlot >= count) return;
            if (!SwapPhantomWeaponSlotData(element, fromSlot, toSlot)) return;
            SyncPhantomWeaponSlotFieldValues(context, fromSlot);
            SyncPhantomWeaponSlotFieldValues(context, toSlot);
            menu.SelectedIndex = Math.Max(0, Math.Min(menu.Items.Count - 1, menu.SelectedIndex + direction));
            PushStructuralEditForContext(context, new WeaponSlotReorderEdit(StructuralKind.PhantomWeaponSlotReorder, fromSlot, toSlot));
            notifications.Notify(NOTIFICATION_KEY_WEAPON_SLOT_ACTION, NOTIF_WEAPON_SLOT_REORDERED);
        }

        private int GetHighlightedPhantomWeaponSlotIndex(NativeMenu menu, PhantomWeaponSlotItems[] slots) { return GetHighlightedSlotIndex(menu, slots, slot => slot.SubmenuItem); }

        // Same arm-then-confirm shape as TriggerDeleteWeaponSlot (WeaponSlots.cs), and the confirm acts on the tree that was armed
        private void TriggerDeletePhantomWeaponSlot(EditorContext context)
        {
            NativeMenu menu = GetPhantomMenuForCategory(context, CATEGORY_WEAPON);
            PhantomWeaponSlotItems[] slots = context.Phantom.PhantomWeaponSlots;
            if (!menu.Visible) return;
            int slotIndex = GetHighlightedPhantomWeaponSlotIndex(menu, slots);
            if (slotIndex < 0) return;
            NativeItem highlighted = slots[slotIndex].SubmenuItem;
            ArmOrConfirm(highlighted, menu, "PhantomWeaponSlotDelete", () => DeletePhantomWeaponSlot(context, slotIndex));
        }

        // Removal is outright with a shift of every later slot, matching AddPhantomWeaponSlot's compact, gap-free array model
        // The live editor clears a slot to None in place, but phantom slot list visibility is driven by array Count, not by scanning for unset hashes
        private void DeletePhantomWeaponSlot(EditorContext context, int slotIndex)
        {
            XElement element = context.Phantom.CurrentPhantomElement;
            if (element == null) return;
            XElement hashArray = element.Element(WEAPON_HASH_ARRAY_TAG);
            if (hashArray == null) return;
            XElement[] items = ListItemElements(hashArray);
            if (slotIndex < 0 || slotIndex >= items.Length) return;
            string hashText = items[slotIndex].Value;
            int seatRaw = GetPhantomWeaponIntArrayItem(element, WEAPON_SEAT_ARRAY_TAG, slotIndex);
            int modTypeRaw = weaponModTypeSupported ? GetPhantomWeaponIntArrayItem(element, WEAPON_MOD_TYPE_ARRAY_TAG, slotIndex) : 0;
            var packedParts = new List<string> { hashText, seatRaw.ToString(CultureInfo.InvariantCulture), modTypeRaw.ToString(CultureInfo.InvariantCulture) };
            foreach (TurretFieldEntry field in TurretFields) packedParts.Add(GetPhantomWeaponFloatArrayItem(element, field.Tag, slotIndex).ToString("R", CultureInfo.InvariantCulture));
            foreach (float limit in GetPhantomTurretPitchLimitItem(element, slotIndex)) packedParts.Add(limit.ToString("R", CultureInfo.InvariantCulture));
            items[slotIndex].Remove();
            RemovePhantomWeaponSlotIntArrayItem(element, WEAPON_SEAT_ARRAY_TAG, slotIndex);
            if (weaponModTypeSupported) RemovePhantomWeaponSlotIntArrayItem(element, WEAPON_MOD_TYPE_ARRAY_TAG, slotIndex);
            foreach (TurretFieldEntry field in TurretFields) RemovePhantomWeaponSlotFloatArrayItem(element, field.Tag, slotIndex);
            RemovePhantomTurretPitchLimitItem(element, slotIndex);
            SyncPhantomWeaponSlotVisibility(context);
            RefreshPhantomWeaponSlotFieldValuesFrom(context, slotIndex);
            PushStructuralEditForContext(context, new PhantomWeaponSlotEdit(StructuralKind.PhantomWeaponSlotDelete, slotIndex, string.Join(",", packedParts.ToArray())));
        }

        // Diffs against the tree's CurrentPhantomWeaponSlotCount instead of rebuilding the menu every sync, like SyncAdvancedDataVisibilityCore
        // A shrink path is required (not pure growth), since excluding then recreating the Weapon category resets the element back to zero slots
        private void SyncPhantomWeaponSlotVisibility(EditorContext context)
        {
            XElement element = context.Phantom.CurrentPhantomElement;
            if (element == null) return;
            NativeMenu menu = GetPhantomMenuForCategory(context, CATEGORY_WEAPON);
            PhantomWeaponSlotItems[] slots = context.Phantom.PhantomWeaponSlots;
            NativeItem addSlotItem = context.Phantom.AddPhantomWeaponSlotItem;
            int currentCount = context.Phantom.CurrentPhantomWeaponSlotCount;
            XElement hashArray = element.Element(WEAPON_HASH_ARRAY_TAG);
            int desired = hashArray != null ? Math.Min(CountItemElements(hashArray), weaponSlotCount) : 0;
            int previousIndex = menu.SelectedIndex;
            // Unlike the AdvancedData siblings there is no currentCount > 0 guard, since uWeaponHash exists from the moment the Weapon category is created
            // So growing from 0 to 1 slot is a genuine Add Slot event, not a separate whole-category-creation event
            // A shrink keeps the highlighted row instead, which is the next slot after a delete
            bool preferAddSlot = desired > currentCount;
            if (desired > currentCount)
            {
                int insertPosition = menu.Items.IndexOf(addSlotItem);
                if (insertPosition < 0) insertPosition = menu.Items.Count;
                for (int i = currentCount; i < desired; i++)
                {
                    menu.Add(insertPosition, slots[i].SubmenuItem);
                    insertPosition++;
                    SyncPhantomWeaponSlotFieldValues(context, i);
                }
            }
            else if (desired < currentCount)
            {
                // A removed slot's own submenu has to be force-closed here, not just delisted from menu.Items
                // The player can be inside it when an undo, redo, or Delete shrinks the count, and nothing else would back them out of a menu whose parent entry is gone
                for (int i = currentCount - 1; i >= desired; i--)
                {
                    menu.Items.Remove(slots[i].SubmenuItem);
                    BackOut(slots[i].Menu);
                }
            }
            context.Phantom.CurrentPhantomWeaponSlotCount = desired;
            // Add Slot hides at full capacity instead of staying selectable and rejecting the press, and reappears the moment a slot is undone or deleted
            menu.Items.Remove(addSlotItem);
            if (desired < weaponSlotCount) menu.Add(addSlotItem);
            RestoreAdvancedDataSelection(menu, addSlotItem, previousIndex, preferAddSlot);
        }

        private void SyncPhantomWeaponSlotFieldValues(EditorContext context, int slotIndex)
        {
            XElement element = context.Phantom.CurrentPhantomElement;
            if (element == null || slotIndex < 0 || slotIndex >= weaponSlotCount) return;
            PhantomWeaponSlotItems[] slots = context.Phantom.PhantomWeaponSlots;
            string hashText = GetPhantomWeaponHashText(element, slotIndex);
            if (hashText != null)
            {
                int index = Array.FindIndex(WeaponHashTable, entry => entry.NativeName == hashText);
                VehicleWeaponHash parsed;
                bool parsedOk = Enum.TryParse(hashText, out parsed);
                if (index < 0 && parsedOk) index = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)parsed);
                // Same content-aware title as the live editor's own weapon slot list
                // Phantom slots have no empty-slot state to drive a visible list reflow, so the title is the only signal a reorder actually happened
                if (index >= 0)
                {
                    slots[slotIndex].HashItem.SelectedItem = WeaponHashTable[index].DisplayName;
                    slots[slotIndex].HashItem.Description = WeaponHashDescription(WeaponHashTable[index].Hash);
                    slots[slotIndex].SubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT_NAMED, (slotIndex + 1), WeaponHashTable[index].DisplayName);
                }
                // A hash outside the table shows Custom, matching the live cycler, with its raw value in the description when the text parses
                else
                {
                    slots[slotIndex].HashItem.SelectedItem = CUSTOM_WEAPON_HASH_LABEL;
                    slots[slotIndex].HashItem.Description = parsedOk ? WeaponHashDescription((uint)parsed) : WeaponHashDescription();
                    slots[slotIndex].SubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT_CUSTOM, (slotIndex + 1));
                }
            }
            int rawSeat = GetPhantomWeaponIntArrayItem(element, WEAPON_SEAT_ARRAY_TAG, slotIndex);
            VehicleSeat seat = (VehicleSeat)(rawSeat - 1);
            string seatName = Enum.IsDefined(typeof(VehicleSeat), seat) ? seat.ToString() : null;
            if (seatName != null && Array.IndexOf(VehicleSeatNames, seatName) >= 0) slots[slotIndex].SeatItem.SelectedItem = seatName;
            if (weaponModTypeSupported && slots[slotIndex].ModTypeItem != null)
            {
                int rawModType = GetPhantomWeaponIntArrayItem(element, WEAPON_MOD_TYPE_ARRAY_TAG, slotIndex);
                VehicleModType modType = (VehicleModType)rawModType;
                string modTypeName = Enum.IsDefined(typeof(VehicleModType), modType) ? modType.ToString() : null;
                if (modTypeName != null && Array.IndexOf(VehicleModTypeNames, modTypeName) >= 0) slots[slotIndex].ModTypeItem.SelectedItem = modTypeName;
            }
        }
    }
}
