using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The index of the slot whose submenu row is highlighted, or -1
        private static int GetHighlightedSlotIndex<TSlot>(NativeMenu menu, TSlot[] slots, Func<TSlot, NativeItem> submenuItemOf)
        {
            NativeItem highlighted = menu.SelectedItem;
            for (int i = 0; i < slots.Length; i++) { if (submenuItemOf(slots[i]) == highlighted) return i; }
            return -1;
        }

        private int GetHighlightedWeaponSlotIndex(NativeMenu menu, WeaponSlotItems[] slots) { return GetHighlightedSlotIndex(menu, slots, slot => slot.SubmenuItem); }

        private void AddWeaponSlot(EditorContext owner)
        {
            NativeMenu menu = GetLiveMenuForCategory(owner, CATEGORY_WEAPON);
            NativeItem addSlotItem = owner.Weapon.WeaponAddSlotItem;
            Vehicle vehicle;
            HandlingData handling;
            bool resolved = ResolveContextVehicleHandling(owner, out vehicle, out handling);
            if (!resolved || handling.VehicleWeaponHandlingData == null) return;
            try
            {
                VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
                VehicleWeaponHash[] hashes = weaponData.WeaponHash;
                int freeSlot = -1;
                for (int i = 0; i < hashes.Length; i++)
                {
                    if (IsUnsetWeaponHash(hashes[i]))
                    {
                        freeSlot = i;
                        break;
                    }
                }
                if (freeSlot < 0)
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_WEAPON_FULL, NOTIF_WEAPON_FULL);
                    return;
                }
                uint assignedHash = WeaponHashTable[DEFAULT_LIVE_WEAPON_SLOT_HASH_INDEX].Hash;
                WeaponSlotState oldState = CaptureWeaponSlotState(weaponData, freeSlot);
                WeaponSlotState newState = DefaultWeaponSlotState(assignedHash);
                ApplyWeaponSlotState(weaponData, freeSlot, newState);
                PushStructuralEditForContext(owner, new WeaponSlotStateEdit(StructuralKind.WeaponSlotAdd, freeSlot, PackWeaponSlotState(oldState), PackWeaponSlotState(newState)));
                SyncEditorContext(owner);
                // SyncWeaponSlotVisibility (called via the sync above) inserts the new slot's item before addSlotItem's own position, shifting the menu's raw index
                // The highlight does not move to compensate, so it silently lands on whatever item now occupies the old index instead of staying on Add Slot
                // Restoring it explicitly keeps repeated presses landing on the same control
                int addSlotItemIndex = menu.Items.IndexOf(addSlotItem);
                if (addSlotItemIndex >= 0) menu.SelectedIndex = addSlotItemIndex;
                notifications.Notify(NOTIFICATION_KEY_WEAPON_SLOT_ACTION, NOTIF_WEAPON_SLOT_ADDED);
                RequestAutoRespawn(vehicle, "WeaponSlotAdd");
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.WeaponSlotActionFailed("Add", ex.Message)); }
        }

        // The confirm acts on the tree that was armed, even when another tree is on screen by then
        private void TriggerDeleteWeaponSlot(EditorContext owner)
        {
            NativeMenu menu = GetLiveMenuForCategory(owner, CATEGORY_WEAPON);
            WeaponSlotItems[] slots = owner.Weapon.WeaponSlots;
            if (!menu.Visible) return;
            int slotIndex = GetHighlightedWeaponSlotIndex(menu, slots);
            if (slotIndex < 0) return;
            ArmOrConfirm(slots[slotIndex].SubmenuItem, menu, "WeaponSlotDelete", () => DeleteWeaponSlot(slotIndex, owner));
        }

        private void DeleteWeaponSlot(int slotIndex, EditorContext owner)
        {
            Vehicle vehicle;
            HandlingData handling;
            if (!ResolveContextVehicleHandling(owner, out vehicle, out handling) || handling.VehicleWeaponHandlingData == null) return;
            try
            {
                VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
                VehicleWeaponHash[] hashes = weaponData.WeaponHash;
                if (slotIndex < 0 || slotIndex >= hashes.Length || IsUnsetWeaponHash(hashes[slotIndex])) return;
                WeaponSlotState oldState = CaptureWeaponSlotState(weaponData, slotIndex);
                WeaponSlotState newState = DefaultWeaponSlotState(0);
                ApplyWeaponSlotState(weaponData, slotIndex, newState);
                PushStructuralEditForContext(owner, new WeaponSlotStateEdit(StructuralKind.WeaponSlotDelete, slotIndex, PackWeaponSlotState(oldState), PackWeaponSlotState(newState)));
                SyncEditorContext(owner);
                notifications.Notify(NOTIFICATION_KEY_WEAPON_SLOT_ACTION, NOTIF_WEAPON_SLOT_REMOVED);
                RequestAutoRespawn(vehicle, "WeaponSlotDelete");
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.WeaponSlotActionFailed("Delete", ex.Message)); }
        }

        // Turret fields are sized to the build's fixed turretSlotCount, which is smaller than weaponSlotCount on some builds
        // A weapon-only slot beyond it has no turret storage, so a swap between a turret slot and a weapon-only slot could only move part of the slot
        private bool CrossesTurretBoundary(int fromSlot, int toSlot) { return (fromSlot < turretSlotCount) != (toSlot < turretSlotCount); }

        // It is self-inverse, so both undo and redo call it the same way
        // Refuses a swap that is out of range or crosses the turret boundary, so a slot always moves whole
        private bool SwapWeaponSlotData(VehicleWeaponHandlingData weaponData, int fromSlot, int toSlot)
        {
            if (fromSlot < 0 || toSlot < 0 || fromSlot >= weaponData.WeaponHash.Length || toSlot >= weaponData.WeaponHash.Length) return false;
            if (CrossesTurretBoundary(fromSlot, toSlot)) return false;
            VehicleWeaponHash[] hashes = weaponData.WeaponHash;
            SwapArrayItems(hashes, fromSlot, toSlot);
            weaponData.WeaponHash = hashes;
            VehicleSeat[] seats = weaponData.WeaponSeats;
            SwapArrayItems(seats, fromSlot, toSlot);
            weaponData.WeaponSeats = seats;
            if (weaponModTypeSupported)
            {
                VehicleModType[] mods = weaponData.WeaponVehicleModType;
                SwapArrayItems(mods, fromSlot, toSlot);
                weaponData.WeaponVehicleModType = mods;
            }
            // Both slots sit on the same side of the turret boundary here, and 2 weapon-only slots have no turret fields to swap
            if (fromSlot < turretSlotCount)
            {
                foreach (TurretFieldEntry field in TurretFields) SwapTurretSlotFloats(field, weaponData, fromSlot, toSlot);
                IntPtr address = weaponData.MemoryAddress;
                float[] fromPitchLimits = RawMemory.ReadTurretPitchLimits(address, fromSlot);
                RawMemory.WriteTurretPitchLimits(address, fromSlot, RawMemory.ReadTurretPitchLimits(address, toSlot));
                RawMemory.WriteTurretPitchLimits(address, toSlot, fromPitchLimits);
                ReleaseWeaponSlotAssertions();
            }
            return true;
        }

        private static void SwapArrayItems<T>(T[] array, int first, int second)
        {
            T temp = array[first];
            array[first] = array[second];
            array[second] = temp;
        }

        private static void SwapRawFloat(IntPtr address, int offset, int first, int second)
        {
            float temp = RawMemory.ReadRawFloat(address, offset + first * FLOAT_FIELD_STRIDE);
            RawMemory.WriteRawFloat(address, offset + first * FLOAT_FIELD_STRIDE, RawMemory.ReadRawFloat(address, offset + second * FLOAT_FIELD_STRIDE));
            RawMemory.WriteRawFloat(address, offset + second * FLOAT_FIELD_STRIDE, temp);
        }

        private static float ReadTurretSlotFloat(TurretFieldEntry field, VehicleWeaponHandlingData weaponData, int slotIndex)
        {
            return field.RawOffset ? RawMemory.ReadRawFloat(weaponData.MemoryAddress, field.Offset + slotIndex * FLOAT_FIELD_STRIDE) : field.GetArray(weaponData)[slotIndex];
        }

        private static void WriteTurretSlotFloat(TurretFieldEntry field, VehicleWeaponHandlingData weaponData, int slotIndex, float value)
        {
            if (field.RawOffset)
            {
                RawMemory.WriteRawFloat(weaponData.MemoryAddress, field.Offset + slotIndex * FLOAT_FIELD_STRIDE, value);
                return;
            }
            float[] values = field.GetArray(weaponData);
            values[slotIndex] = value;
            field.SetArray(weaponData, values);
        }

        private static void SwapTurretSlotFloats(TurretFieldEntry field, VehicleWeaponHandlingData weaponData, int first, int second)
        {
            if (field.RawOffset)
            {
                SwapRawFloat(weaponData.MemoryAddress, field.Offset, first, second);
                return;
            }
            float[] values = field.GetArray(weaponData);
            SwapArrayItems(values, first, second);
            field.SetArray(weaponData, values);
        }

        // The per-slot fields: The hash, seat, and mod type, then one float per TurretFields row, then the TurretPitchLimitData members
        // Loops over the tables instead of named fields keep the capture, default, apply, pack, and unpack functions in lockstep with them
        private struct WeaponSlotState
        {
            public uint Hash;
            public VehicleSeat Seat;
            public VehicleModType ModType;
            // TurretFields order
            public float[] TurretFloats;
            // TurretPitchLimitFields order
            public float[] PitchLimits;
        }

        // Where a packed WeaponSlotState's pitch limits start, after the hash, seat, mod type, and turret floats
        private static int WeaponSlotStatePitchLimitStart { get { return WEAPON_SLOT_STATE_LEADING_FIELD_COUNT + TurretFields.Length; } }
        private static int WeaponSlotStateFieldCount { get { return WeaponSlotStatePitchLimitStart + TurretPitchLimitFields.Length; } }

        // A WeaponSlotStateEdit's StateBefore and StateAfter (and the persisted undo-history file) can only hold strings
        // So every producer and consumer of a packed weapon-slot record goes through this pair
        private static string PackWeaponSlotState(WeaponSlotState state)
        {
            var parts = new List<string> { state.Hash.ToString(CultureInfo.InvariantCulture), state.Seat.ToString(), state.ModType.ToString() };
            foreach (float value in state.TurretFloats) parts.Add(value.ToString("R", CultureInfo.InvariantCulture));
            foreach (float value in state.PitchLimits) parts.Add(value.ToString("R", CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }

        // Every field is parsed before state is populated, so a malformed packed string (a hand-edited or corrupted undo-history line) reports failure
        // A half-populated struct is never returned
        private static bool UnpackWeaponSlotState(string packed, out WeaponSlotState state)
        {
            state = default(WeaponSlotState);
            string[] parts = packed.Split(',');
            if (parts.Length != WeaponSlotStateFieldCount) return false;
            uint hash;
            VehicleSeat seat;
            VehicleModType modType;
            if (!uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hash)) return false;
            if (!Enum.TryParse(parts[1], out seat)) return false;
            if (!Enum.TryParse(parts[2], out modType)) return false;
            var turretFloats = new float[TurretFields.Length];
            for (int i = 0; i < turretFloats.Length; i++) { if (!ParseFiniteFloat(parts[WEAPON_SLOT_STATE_LEADING_FIELD_COUNT + i], out turretFloats[i])) return false; }
            var pitchLimits = new float[TurretPitchLimitFields.Length];
            for (int i = 0; i < pitchLimits.Length; i++) { if (!ParseFiniteFloat(parts[WeaponSlotStatePitchLimitStart + i], out pitchLimits[i])) return false; }
            state.Hash = hash;
            state.Seat = seat;
            state.ModType = modType;
            state.TurretFloats = turretFloats;
            state.PitchLimits = pitchLimits;
            return true;
        }

        // Captures the whole slot so a StructuralEdit can restore it atomically through ApplyWeaponSlotState instead of only WeaponHash
        private WeaponSlotState CaptureWeaponSlotState(VehicleWeaponHandlingData weaponData, int slotIndex)
        {
            // Turret fields are sized to the build's fixed turretSlotCount, so a weapon-only slot beyond it has no turret storage to read and captures at default
            bool hasTurret = slotIndex < turretSlotCount;
            var turretFloats = new float[TurretFields.Length];
            if (hasTurret) { for (int i = 0; i < turretFloats.Length; i++) turretFloats[i] = ReadTurretSlotFloat(TurretFields[i], weaponData, slotIndex); }
            return new WeaponSlotState
            {
                Hash = (uint)weaponData.WeaponHash[slotIndex],
                Seat = weaponData.WeaponSeats[slotIndex],
                ModType = weaponModTypeSupported ? weaponData.WeaponVehicleModType[slotIndex] : VehicleModType.None,
                TurretFloats = turretFloats,
                PitchLimits = hasTurret ? RawMemory.ReadTurretPitchLimits(weaponData.MemoryAddress, slotIndex) : DefaultTurretPitchLimitValues(),
            };
        }

        // A freshly emptied or freshly activated slot's state: The given hash (0 for Delete, the new weapon for Add)
        // Every other field takes its own type's zero or None default, except the pitch limits, which take the game's own init defaults for an entry a file omits
        private WeaponSlotState DefaultWeaponSlotState(uint hash)
        {
            return new WeaponSlotState
            {
                Hash = hash,
                Seat = VehicleSeat.None,
                ModType = VehicleModType.None,
                TurretFloats = new float[TurretFields.Length],
                PitchLimits = DefaultTurretPitchLimitValues(),
            };
        }

        // The single write path for Add, Delete, and their Undo and Redo
        private void ApplyWeaponSlotState(VehicleWeaponHandlingData weaponData, int slotIndex, WeaponSlotState state)
        {
            VehicleWeaponHash[] hashes = weaponData.WeaponHash;
            hashes[slotIndex] = (VehicleWeaponHash)state.Hash;
            weaponData.WeaponHash = hashes;
            VehicleSeat[] seats = weaponData.WeaponSeats;
            seats[slotIndex] = state.Seat;
            weaponData.WeaponSeats = seats;
            if (weaponModTypeSupported)
            {
                VehicleModType[] mods = weaponData.WeaponVehicleModType;
                mods[slotIndex] = state.ModType;
                weaponData.WeaponVehicleModType = mods;
            }
            // Turret fields are sized to the build's fixed turretSlotCount, so a weapon-only slot beyond it has no turret storage to write into
            if (slotIndex >= turretSlotCount) return;
            for (int i = 0; i < TurretFields.Length; i++) WriteTurretSlotFloat(TurretFields[i], weaponData, slotIndex, state.TurretFloats[i]);
            RawMemory.WriteTurretPitchLimits(weaponData.MemoryAddress, slotIndex, state.PitchLimits);
            ReleaseWeaponSlotAssertions();
        }

        // Mirrors the resolution SyncEditorContextFieldValues uses for the content-aware slot title
        // So two different unresolved "Custom" hashes read as the identical name here too, matching what the player sees on both slots' titles
        private static string ResolveWeaponSlotDisplayName(VehicleWeaponHash hash)
        {
            int hashIndex = Array.FindIndex(WeaponHashTable, entry => entry.Hash == (uint)hash);
            if (hashIndex >= 0) return WeaponHashTable[hashIndex].DisplayName;
            return IsUnsetWeaponHash(hash) ? WeaponHashTable[0].DisplayName : CUSTOM_WEAPON_HASH_LABEL;
        }

        // Called only when a weapon slot's hash write crosses the unset boundary (a real weapon clearing to none, or an unset slot receiving one)
        // It zeroes or assigns every other field in lockstep so a cleared slot never leaves stale Seat, VehicleModType, or turret data behind
        // It reuses the WeaponSlotAdd and WeaponSlotDelete kinds AddWeaponSlot and DeleteWeaponSlot push, so Undo and Redo restore the whole slot, not just the hash
        // The handling key is explicit, since the interactive cycler and Selective Copy resolve their own target vehicle two different ways
        private void ApplyWeaponHashBoundaryCross(VehicleWeaponHandlingData weaponData, int slot, uint newHash, HandlingKey handling)
        {
            WeaponSlotState oldState = CaptureWeaponSlotState(weaponData, slot);
            WeaponSlotState newState = DefaultWeaponSlotState(newHash);
            ApplyWeaponSlotState(weaponData, slot, newState);
            string kind = IsUnsetWeaponHash((VehicleWeaponHash)newHash) ? StructuralKind.WeaponSlotDelete : StructuralKind.WeaponSlotAdd;
            PushStructuralEdit(new WeaponSlotStateEdit(kind, slot, PackWeaponSlotState(oldState), PackWeaponSlotState(newState)) { Handling = handling });
        }

        // Returns True only when the swap happened, so the caller moves the highlight only then
        private bool ReorderWeaponSlot(int fromSlot, int toSlot, EditorContext owner)
        {
            Vehicle vehicle;
            HandlingData handling;
            if (!ResolveContextVehicleHandling(owner, out vehicle, out handling) || handling.VehicleWeaponHandlingData == null) return false;
            VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
            if (fromSlot < 0 || toSlot < 0 || fromSlot >= weaponData.WeaponHash.Length || toSlot >= weaponData.WeaponHash.Length) return false;
            if (CrossesTurretBoundary(fromSlot, toSlot))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_WEAPON_SLOT_TURRET_BOUNDARY, NOTIF_WEAPON_TURRET_BOUNDARY);
                return false;
            }
            try
            {
                VehicleWeaponHash[] preSwapHashes = weaponData.WeaponHash;
                // Both slots resolving to the identical displayed name means the content-aware title fix (SyncEditorContextFieldValues) produces no visible change on reorder
                // The flash below is this swap's only visible cue in that case
                bool identicalTitles = ResolveWeaponSlotDisplayName(preSwapHashes[fromSlot]) == ResolveWeaponSlotDisplayName(preSwapHashes[toSlot]);
                if (!SwapWeaponSlotData(weaponData, fromSlot, toSlot)) return false;
                PushStructuralEditForContext(owner, new WeaponSlotReorderEdit(StructuralKind.WeaponSlotReorder, fromSlot, toSlot));
                SyncEditorContext(owner);
                notifications.Notify(NOTIFICATION_KEY_WEAPON_SLOT_ACTION, NOTIF_WEAPON_SLOT_REORDERED);
                RequestAutoRespawn(vehicle, "WeaponSlotReorder");
                if (identicalTitles)
                {
                    WeaponSlotItems[] slots = owner.Weapon.WeaponSlots;
                    StartResultFlash(slots[toSlot].SubmenuItem, Color.LightGreen);
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Warn, Dbg.WeaponSlotActionFailed("Reorder", ex.Message));
                return false;
            }
        }
    }
}
