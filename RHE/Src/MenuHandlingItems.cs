using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // category is the field's descriptor category (null for base fields) and also prefixes its fieldRegistry key
        // keySuffix disambiguates an AdditionalEditor's field-registry key from Current's own identically-titled entry
        // owner tags the binding (currentContext for Current, else the owning EditorContext) so ReassertDriftedFields never bleeds one tree's edit onto another's
        private IEnumerable<bool> BuildFieldMenuItem<TData>(NativeMenu menu, string category, HandlingFieldEntry<TData> entry, Func<HandlingData, TData> dataResolver, string keySuffix, EditorContext owner)
        {
            if (entry.GetFloat != null)
            {
                string baseDescription = ResolveDescriptor(category, entry.Title);
                var item = new NativeDynamicItem<DisplayFloat>(entry.Title, baseDescription);
                AddTreeRow(menu, item);
                if (entry.RequiresRocketBoost) owner.RocketBoostItems.Add(item);
                FieldStore<float> floatStore = BuildLiveFloatStore(item, category, entry, h =>
                {
                    TData data = dataResolver(h);
                    return data != null ? entry.GetFloat(data) : entry.DefaultFloat;
                }, (vehicle, h, v) =>
                {
                    TData data = dataResolver(h);
                    if (data == null) return false;
                    entry.SetFloat(data, v);
                    if (entry.Recompute != null) entry.Recompute(data, vehicle);
                    return true;
                }, owner, keySuffix == null ? null : entry.Title + keySuffix, baseDescription);
                floatStore.Reapply = BuildReapply(entry, dataResolver, owner);
                WireFloatField(item, entry, floatStore);
                yield return true;
            }
            else if (entry.GetVec != null)
            {
                BuildVectorFieldMenuItem(menu, category, entry, dataResolver, 'X', keySuffix, owner);
                yield return true;
                BuildVectorFieldMenuItem(menu, category, entry, dataResolver, 'Y', keySuffix, owner);
                yield return true;
                BuildVectorFieldMenuItem(menu, category, entry, dataResolver, 'Z', keySuffix, owner);
                yield return true;
            }
            else if (entry.GetInt != null)
            {
                var item = new NativeDynamicItem<int>(entry.Title, ResolveDescriptor(category, entry.Title));
                AddTreeRow(menu, item);
                WireIntField(item, entry, BuildLiveIntStore(item, category, entry, h =>
                {
                    TData data = dataResolver(h);
                    return data != null ? entry.GetInt(data) : 0;
                }, (vehicle, h, v) =>
                {
                    TData data = dataResolver(h);
                    if (data == null) return false;
                    entry.SetInt(data, v);
                    if (entry.Recompute != null) entry.Recompute(data, vehicle);
                    return true;
                }, owner, keySuffix == null ? null : entry.Title + keySuffix));
                yield return true;
            }
        }

        // The suffixed title is also the descriptor key (CenterOfMassOffsetX, CenterOfMassOffsetY, CenterOfMassOffsetZ)
        private void BuildVectorFieldMenuItem<TData>(NativeMenu menu, string category, HandlingFieldEntry<TData> entry, Func<HandlingData, TData> dataResolver, char component, string keySuffix, EditorContext owner)
        {
            string title = entry.Title + component;
            string baseDescription = ResolveDescriptor(category, title);
            var item = new NativeDynamicItem<DisplayFloat>(title, baseDescription);
            AddTreeRow(menu, item);
            FieldStore<float> floatStore = BuildLiveFloatStore(item, category, entry, h =>
            {
                TData data = dataResolver(h);
                Vector3 vec = data != null ? entry.GetVec(data) : entry.DefaultVector;
                return component == 'X' ? vec.X : component == 'Y' ? vec.Y : vec.Z;
            }, (vehicle, h, v) =>
            {
                TData data = dataResolver(h);
                if (data == null) return false;
                Vector3 vec = entry.GetVec(data);
                if (component == 'X') vec.X = v;
                else if (component == 'Y') vec.Y = v;
                else vec.Z = v;
                entry.SetVec(data, vec);
                if (entry.Recompute != null) entry.Recompute(data, vehicle);
                return true;
            }, owner, keySuffix == null ? null : title + keySuffix, baseDescription);
            floatStore.Reapply = BuildReapply(entry, dataResolver, owner);
            WireFloatField(item, entry, floatStore);
        }

        // Null for a row with no follow-up. The vehicle is resolved when it runs, like every other handler of the row
        private Action BuildReapply<TData>(HandlingFieldEntry<TData> entry, Func<HandlingData, TData> dataResolver, EditorContext owner)
        {
            if (entry.Recompute == null) return null;
            return () =>
            {
                Vehicle vehicle;
                HandlingData handling;
                if (!ResolveContextVehicleHandling(owner, out vehicle, out handling)) return;
                TData data = dataResolver(handling);
                if (data != null) entry.Recompute(data, vehicle);
            };
        }

        // Base and every sub-handling category drive their live tree from this one walk
        // Only Car (AdvancedFlags/AdvancedData), SpecialFlight (Mode/Flags), and Weapon (its slot family) add their own structural items beside it
        private IEnumerable<bool> BuildCategoryMenuItems<TData>(NativeMenu menu, string category, HandlingFieldEntry<TData>[] fields, Func<HandlingData, TData> dataResolver, string keySuffix, EditorContext owner)
        {
            foreach (var entry in fields) foreach (bool step in BuildFieldMenuItem(menu, category, entry, dataResolver, keySuffix, owner)) yield return step;
        }

        // Edit Current Handling is built by BuildEditorContext, since that row is the sole point of contact between mainMenu and Current's own editor tree
        // Every row here is genuinely global, with no per-editor-context equivalent at all
        private void BuildMainMenuItems()
        {
            // Select Vehicle's own item is constructed but not directly added
            // SyncSelectVehicleSlot reveals it through SyncMenuSlot, the growing-list pattern every trailing Add control uses, which can also remove it once full
            selectVehicleItem = new NativeItem(TITLE_SELECT_VEHICLE, DESC_SELECT_VEHICLE);
            SubscribeActivated(selectVehicleItem, "selectVehicleItem.Activated", () => EnterVehicleSelectionMode());
            respawnVehicleItem = new NativeItem(TITLE_RESPAWN_VEHICLE, DESC_RESPAWN_VEHICLE);
            SubscribeActivated(respawnVehicleItem, "respawnVehicleItem.Activated", () => RespawnVehicle());
            mainMenu.Add(respawnVehicleItem);
            unloadVehicleItem = new NativeItem(TITLE_UNLOAD_VEHICLE, DESC_UNLOAD_VEHICLE);
            SubscribeActivated(unloadVehicleItem, "unloadVehicleItem.Activated", () => TriggerUnloadVehicle());
            mainMenu.Add(unloadVehicleItem);
            // Select Vehicle always sits immediately above Respawn Vehicle. Mounted AdditionalEditors accumulate above Select Vehicle's own row
            SyncSelectVehicleSlot();
            saveHandlingItem = new NativeItem(LABEL_SAVE, DESC_SAVE);
            SubscribeActivated(saveHandlingItem, "saveHandlingItem.Activated", () => SaveHandling());
            mainMenu.Add(saveHandlingItem);
            savedHandlingsItem = AddStyledSubmenu(mainMenu, savedHandlingsMenu);
            savedHandlingsItem.Title = LABEL_SAVED_HANDLINGS;
            savedHandlingsMenu.Description = DESC_SAVED_HANDLINGS;
            // A reopen after an expected native close (Escape, P, Backspace) keeps the list and selection as they were
            // It still disarms, since the reopen raises SelectedIndexChanged
            SubscribeShown(savedHandlingsMenu, "savedHandlingsMenu.Shown", () => { if (!navigation.NativeCloseExpected) RefreshSavedHandlingsMenu(); });
            SubscribeSelectedIndexChanged(savedHandlingsMenu, "savedHandlingsMenu.SelectedIndexChanged", e =>
            {
                DisarmArmed();
                RecordSearchHighlight();
                if (!listRowAssignmentSkipsPreviewRefresh) RefreshPreviewMenu();
            });
            // Fires before the submenu item's own Activated opens Saved Handlings, so an entry from the main menu always starts on Exports
            SubscribeItemActivated(mainMenu, "mainMenu.ItemActivated", e => { if (e.Item == savedHandlingsItem) browsedSource = exportsSource; });
            searchItem = new StyledChevronItem(TITLE_SEARCH, DESC_SEARCH);
            SubscribeActivated(searchItem, "searchItem.Activated", () => OpenVehicleSearch());
            mainMenu.Add(searchItem);
            settingsMenuItem = AddStyledSubmenu(mainMenu, settingsMenu);
            settingsMenuItem.Title = LABEL_SETTINGS;
        }

        // Neither item is added to the root menu here, since SyncSubHandlingVisibility adds whichever one applies on every sync
        // The row's PhantomMenu must exist already, since the export-only submenu entry opens it
        private void BuildSubHandlingCategory(EditorContext owner, SubHandlingCategory row)
        {
            string category = row.Definition.Name;
            // Same title as the live submenu entry, so a phantom category reads like its native counterpart and differs only by state
            string displayTitle = row.Definition.LongName;
            NativeItem placeholder = new NativeItem(displayTitle, PLACEHOLDER_SUBDATA_NOT_PRESENT);
            DimItemAsDisabled(placeholder);
            // Present-but-excluded shows this same placeholder (SyncSubHandlingVisibility)
            // Enter there just re-includes it, matching the Delete control's own toggle, since the category already exists live and there is nothing to create
            SubscribeActivated(placeholder, "BuildSubHandlingCategory.Activated", () =>
            {
                Vehicle vehicle = owner.TargetVehicle;
                if (Entity.IsNullOrNotExisting(vehicle)) return;
                HandlingData handling = vehicle.HandlingData;
                if (!handling.IsValid) return;
                if (IsCategoryPresent(handling, category)) ToggleSubDataExclusion(owner, category, null);
                else if (!BeginLiveCategoryCreation(owner, category, vehicle, LiveRequestOrigin.Player)) ActivatePhantomCategory(owner, category);
            });
            row.Placeholder = placeholder;
            row.PhantomSubmenu = new StyledSubmenuItem(row.PhantomMenu, owner.RootMenu);
            row.PhantomSubmenu.Title = displayTitle;
        }

        // Matches the category subtitle's own "(0x........)" hex convention, applied per bit instead of the combined 32-bit value
        private static string FlagHashPrefix(int bitIndex) { return "(0x" + (1u << bitIndex).ToString("X8") + ") "; }

        // A set bit with no row is not counted, so the number always matches the ticked checkboxes
        private static int CountTickedFlagRows(uint flags, FlagNameEntry[] entries)
        {
            int ticked = 0;
            for (int i = 0; i < entries.Length; i++) if ((flags & (1u << entries[i].BitIndex)) != 0) ticked++;
            return ticked;
        }

        private static string FlagsMenuName(string template, uint flags, FlagNameEntry[] entries)
        {
            return string.Format(template, flags.ToString("X8"), CountTickedFlagRows(flags, entries));
        }

        // Display only: The full prefixed name stays the lookup key everywhere else (bindings, descriptor resolution, the undo field key)
        private static string StripFlagPrefix(string name)
        {
            if (name.StartsWith("MF_") || name.StartsWith("HF_") || name.StartsWith("DF_") || name.StartsWith("CF_") || name.StartsWith("SF_")) return name.Substring(3);
            return name;
        }

        // The hash shown reflects the name currently selected in the cycler, matching the vehicle's real AIHandlingHash immediately after the repoint applies
        // Recomputed on every cycle so it tracks the selection instead of only updating on the next full menu sync
        private string AIHandlingDescription(string name) { return "(0x" + GTA.StringHash.AtStringHash(name).ToString("X8") + ") " + ResolveDescriptor("Flags", "AIHandling"); }

        // WeaponHash also writes live on cycle, so the raw hash passed in is always the value that was just written, not a preview of what would be written
        private string WeaponHashDescription(uint rawHash) { return "(0x" + rawHash.ToString("X8") + ") " + WeaponHashDescription(); }
        // The typing hint is appended here, never stored in the compiled description. A description whose last sentence has no end mark gets a period first
        private string WeaponHashDescription()
        {
            string descriptor = ResolveDescriptor(CATEGORY_WEAPON, "WeaponHash").TrimEnd();
            if (descriptor.Length == 0) return DESC_WEAPON_HASH_TYPING_HINT.TrimStart();
            char last = descriptor[descriptor.Length - 1];
            return (last == '.' || last == '!' || last == '?' ? descriptor : descriptor + ".") + DESC_WEAPON_HASH_TYPING_HINT;
        }

        // isRawOffset routes the category through ReadRawUInt and WriteRawUIntBit instead of the always-available ReadFlagBits and WriteFlagBit
        // It also registers each item for the narrower RawMemory.SpecialFlightFlagsSupported disable pass on top of the general RawOffsetItem marker
        // The only consumer is SpecialFlightHandlingFlags, the sole flag category version-gated above RawMemory.RawOffsetsSupported's own general floor
        private IEnumerable<bool> BuildFlagCategory(EditorContext owner, NativeMenu menu, List<FlagCheckboxBinding> bindings, FlagNameEntry[] entries, string category, Func<HandlingData, IntPtr> addressGetter, int offset, Action<uint> onFlagsChanged, string keySuffix = null, bool isRawOffset = false)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                // Hex prefix on the description, not the Title, since a Title suffix would widen every flag menu
                // RecalculateTargetWidth measures Title text like any other item
                string baseDescription = ResolveDescriptor(category, entries[i].Name);
                var item = new NativeCheckboxItem(StripFlagPrefix(entries[i].Name), FlagHashPrefix(entries[i].BitIndex) + baseDescription, false);
                DimCheckboxWhileDisabled(item);
                AddTreeRow(menu, item);
                bindings.Add(new FlagCheckboxBinding { Item = item, BitIndex = entries[i].BitIndex, BaseDescription = baseDescription });
                string fieldKeyOverride = keySuffix == null ? null : entries[i].Name + keySuffix;
                if (isRawOffset)
                {
                    WireFlagCheckbox(item, category, entries[i], owner, addressGetter, offset, onFlagsChanged, RawMemory.ReadRawUInt, RawMemory.WriteRawUIntBit, fieldKeyOverride, EnforceWorksUpsideDown);
                    specialFlightFlagItems.Add(RawOffsetItem(item));
                }
                else WireFlagCheckbox(item, category, entries[i], owner, addressGetter, offset, onFlagsChanged, fieldKeyOverride);
                yield return true;
            }
            WireFlagConflicts(bindings, entries);
        }

        private static void SyncFlagCheckboxes(List<FlagCheckboxBinding> bindings, uint flags)
        {
            for (int i = 0; i < bindings.Count; i++) bindings[i].Item.Checked = (flags & (1u << bindings[i].BitIndex)) != 0;
        }

        // A bit that does not exist has no entry
        // Alphabetical by word, except the ABS family, which follows its chain order
        // A front/rear pair of the same type stays adjacent, and a family sorts by its first flag
        // BitIndex, not array position, is the authoritative bit
        private static readonly FlagNameEntry[] ModelFlagEntries = {
            new FlagNameEntry("MF_ABS_STD", 4, 0) { RequiresRespawn = true }, new FlagNameEntry("MF_ABS_OPTION", 5, 0) { RequiresRespawn = true }, new FlagNameEntry("MF_ABS_ALT_STD", 6, 0) { RequiresRespawn = true },
            new FlagNameEntry("MF_ABS_ALT_OPTION", 7, 0) { RequiresRespawn = true }, new FlagNameEntry("MF_ATTACH_PED_TO_BODYSHELL", 19), new FlagNameEntry("MF_AXLE_F_MCPHERSON", 18, 1) { RequiresRespawn = true },
            new FlagNameEntry("MF_AXLE_R_MCPHERSON", 22, 2) { RequiresRespawn = true }, new FlagNameEntry("MF_AXLE_F_SOLID", 17, 1) { RequiresRespawn = true }, new FlagNameEntry("MF_AXLE_R_SOLID", 21, 2) { RequiresRespawn = true },
            new FlagNameEntry("MF_AXLE_F_TORSION", 16, 1), new FlagNameEntry("MF_AXLE_R_TORSION", 20, 2), new FlagNameEntry("MF_CAN_ENTER_IF_NO_DOOR", 15),
            new FlagNameEntry("MF_DONT_FORCE_GRND_CLEARANCE", 23), new FlagNameEntry("MF_DONT_RENDER_STEER", 24) { RequiresRespawn = true }, new FlagNameEntry("MF_DOUBLE_EXHAUST", 13),
            new FlagNameEntry("MF_DOUBLE_FRONT_WHEELS", 27), new FlagNameEntry("MF_DOUBLE_REAR_WHEELS", 29), new FlagNameEntry("MF_EXTRA_CAMBER", 31),
            new FlagNameEntry("MF_HAS_TRACKS", 11) { RequiresRespawn = true }, new FlagNameEntry("MF_INDESTRUCTIBLE", 26) { RequiresRespawn = true }, new FlagNameEntry("MF_IS_BIG", 3) { RequiresRespawn = true },
            new FlagNameEntry("MF_IS_BUS", 1) { RequiresRespawn = true }, new FlagNameEntry("MF_IS_LOW", 2), new FlagNameEntry("MF_IS_RC", 28), new FlagNameEntry("MF_IS_VAN", 0) { RequiresRespawn = true },
            new FlagNameEntry("MF_NO_1STPERSON_LOOKBEHIND", 14), new FlagNameEntry("MF_NO_DOORS", 8), new FlagNameEntry("MF_NO_EXHAUST", 12),
            new FlagNameEntry("MF_NO_WHEEL_BREAK", 30) { RequiresRespawn = true }, new FlagNameEntry("MF_NO_WHEEL_BURST", 25) { RequiresRespawn = true }, new FlagNameEntry("MF_SIT_IN_BOAT", 10),
            new FlagNameEntry("MF_TANDEM_SEATING", 9),
        };

        // HF_LAST_AVAILABLE_FLAG is absent entirely (confirmed nullsub)
        private static readonly FlagNameEntry[] HandlingFlagEntries = {
            new FlagNameEntry("HF_ALT_EXT_WHEEL_BOUNDS_BEH", 13, 3), new FlagNameEntry("HF_ALT_EXT_WHEEL_BOUNDS_SHRINK", 19, 3), new FlagNameEntry("HF_EXT_WHEEL_BOUNDS_COL", 15, 3, true) { RequiresRespawn = true },
            new FlagNameEntry("HF_ARMOURED", 27), new FlagNameEntry("HF_CVT", 12), new FlagNameEntry("HF_DONT_RAISE_BOUNDS_AT_SPEED", 14),
            new FlagNameEntry("HF_ENABLE_LEAN", 24) { RequiresRespawn = true }, new FlagNameEntry("HF_FORCE_NO_TC_OR_SC", 25), new FlagNameEntry("HF_FREEWHEEL_NO_GAS", 8),
            new FlagNameEntry("HF_HANDBRAKE_REARWHEELSTEER", 6, 0) { RequiresRespawn = true }, new FlagNameEntry("HF_STEER_ALL_WHEELS", 7, 0) { RequiresRespawn = true },
            new FlagNameEntry("HF_STEER_NO_WHEELS", 11, 0, true) { RequiresRespawn = true }, new FlagNameEntry("HF_STEER_REARWHEELS", 5, 0) { RequiresRespawn = true }, new FlagNameEntry("HF_HAS_KERS", 2),
            new FlagNameEntry("HF_HAS_RALLY_TYRES", 3), new FlagNameEntry("HF_HEAVYARMOUR", 26), new FlagNameEntry("HF_IMPROVED_RIGHTING_FORCE", 29, 1),
            new FlagNameEntry("HF_REDUCED_RIGHTING_FORCE", 10, 1), new FlagNameEntry("HF_LESS_SNOW_SINK", 16), new FlagNameEntry("HF_LOW_SPEED_WHEELIES", 30),
            new FlagNameEntry("HF_NO_HANDBRAKE", 4), new FlagNameEntry("HF_NO_REVERSE", 9), new FlagNameEntry("HF_OFFROAD_ABILITIES", 20, 2) { RefreshesVehicle = true },
            new FlagNameEntry("HF_OFFROAD_ABILITIES_X2", 21, 2) { RefreshesVehicle = true }, new FlagNameEntry("HF_OFFROAD_INCREASED_GRAVITY_NO_FOLIAGE_DRAG", 23, 2) { RefreshesVehicle = true },
            new FlagNameEntry("HF_REDUCED_DRIVE_OVER_DAMAGE", 18), new FlagNameEntry("HF_REDUCED_MOD_MASS", 1), new FlagNameEntry("HF_SELF_RIGHTING_IN_WATER", 28),
            new FlagNameEntry("HF_SMOOTHED_COMPRESSION", 0), new FlagNameEntry("HF_TYRES_CAN_CLIP", 17),
            new FlagNameEntry("HF_TYRES_RAISE_SIDE_IMPACT_THRESHOLD", 22),
        };

        private static readonly FlagNameEntry[] DamageFlagEntries = {
            new FlagNameEntry("DF_BONNET", 4) { RequiresRespawn = true }, new FlagNameEntry("DF_BOOT", 5) { RequiresRespawn = true }, new FlagNameEntry("DF_DRIVER_PASSENGER_SIDE_FRONT_DOOR", 2) { RequiresRespawn = true },
            new FlagNameEntry("DF_DRIVER_PASSENGER_SIDE_REAR_DOOR", 3) { RequiresRespawn = true }, new FlagNameEntry("DF_DRIVER_SIDE_FRONT_DOOR", 0) { RequiresRespawn = true },
            new FlagNameEntry("DF_DRIVER_SIDE_REAR_DOOR", 1) { RequiresRespawn = true },
        };

        private static readonly FlagNameEntry[] AdvancedFlagEntries = {
            new FlagNameEntry("CF_ALLOW_REDUCED_SUSPENSION_FORCE", 15), new FlagNameEntry("CF_ALLOW_TURN_ON_SPOT", 23),
            new FlagNameEntry("CF_ALLOWS_EXTENDED_MODS", 29), new FlagNameEntry("CF_ASSIST_STABILITY_CONTROL", 14),
            new FlagNameEntry("CF_ASSIST_TRACTION_CONTROL", 13), new FlagNameEntry("CF_BLOCK_INCREASED_ROT_VELOCITY_WITH_DRIVE_FORCE", 19),
            new FlagNameEntry("CF_CAN_WHEELIE", 24), new FlagNameEntry("CF_CLOSE_RATIO_GEARBOX", 21) { RequiresRespawn = true }, new FlagNameEntry("CF_DIFF_CENTRE", 2),
            new FlagNameEntry("CF_DIFF_FRONT", 0), new FlagNameEntry("CF_DIFF_REAR", 1), new FlagNameEntry("CF_DIFF_LIMITED_CENTRE", 5),
            new FlagNameEntry("CF_DIFF_LIMITED_FRONT", 3), new FlagNameEntry("CF_DIFF_LIMITED_REAR", 4), new FlagNameEntry("CF_DIFF_LOCKING_CENTRE", 8),
            new FlagNameEntry("CF_DIFF_LOCKING_FRONT", 6), new FlagNameEntry("CF_DIFF_LOCKING_REAR", 7), new FlagNameEntry("CF_ENABLE_WHEEL_BLOCKER_SIDE_IMPACTS", 25),
            new FlagNameEntry("CF_FIX_OLD_BUGS", 26), new FlagNameEntry("CF_FORCE_SMOOTH_RPM", 22), new FlagNameEntry("CF_GEARBOX_DIRECT_SHIFT", 11),
            new FlagNameEntry("CF_GEARBOX_ELECTRIC", 12), new FlagNameEntry("CF_GEARBOX_FULL_AUTO", 9), new FlagNameEntry("CF_GEARBOX_MANUAL", 10),
            new FlagNameEntry("CF_HARD_REV_LIMIT", 16), new FlagNameEntry("CF_HOLD_GEAR_WITH_WHEELSPIN", 17),
            new FlagNameEntry("CF_INCREASE_SUSPENSION_FORCE_WITH_SPEED", 18) { RequiresRespawn = true }, new FlagNameEntry("CF_REDUCE_BODY_ROLL_WITH_SUSPENSION_MODS", 28),
            new FlagNameEntry("CF_REDUCED_SELF_RIGHTING_SPEED", 20), new FlagNameEntry("CF_USE_DOWNFORCE_BIAS", 27),
        };

        // Raw names match the real handling.meta flag names, not the API's own SpecialFlightHandlingFlags enum member names
        // The enum renames them away from the handling.meta convention every other flag category keeps
        private static readonly FlagNameEntry[] SpecialFlightFlagEntries = {
            new FlagNameEntry("SF_DONT_RETRACT_WHEELS", 1), new FlagNameEntry("SF_FORCE_MIN_THROTTLE_WHEN_TURNING", 5),
            new FlagNameEntry("SF_FORCE_SPECIAL_FLIGHT_WHEN_DRIVEN", 7) { RequiresRespawn = true }, new FlagNameEntry("SF_LIMIT_FORCE_DELTA", 6),
            new FlagNameEntry("SF_MAINTAIN_HOVER_HEIGHT", 3), new FlagNameEntry("SF_REDUCE_DRAG_WITH_SPEED", 2), new FlagNameEntry("SF_STEER_TOWARDS_VELOCITY", 4),
            new FlagNameEntry("SF_WORKS_UPSIDE_DOWN", 0),
        };

        // Every live registry key is built from the row title, so each builder and Selective Copy's key name must produce it the same way
        private static string AdvancedDataRowTitle(int entryIndex, string member) { return "[" + (entryIndex + 1) + "] " + member; }

        // The real list's and the pending list's menus are already constructed in the tree's AdvancedData lists, alongside carMenu
        // targetSlot is always a real EditorContext (currentContext for Current)
        // keySuffix disambiguates the live AdvancedData Slot, Index, and Value fieldRegistry keys across trees, since every one of these fields shares the same title
        private IEnumerable<bool> BuildCarAdvancedDataMenuItems(NativeMenu carMenu, EditorContext targetSlot, string keySuffix)
        {
            AdvancedDataMenus liveLists = targetSlot.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live];
            // A per-slot array (atArray<CAdvancedData>) with one Entry N submenu row per real entry, listed to the vehicle's live entry count by SyncAdvancedDataVisibility
            // Every entry's own rows are built here up front, and the entry's submenu is created on first listing (EnsureAdvancedDataEntryMenu)
            liveLists.ListMenu.Description = ResolveDescriptor(CATEGORY_CAR, "AdvancedData");
            // Constructed directly instead of through AddStyledSubmenu, so it stays out of the menu until the vehicle's real entry count is nonzero
            // SyncLiveAdvancedDataSlot swaps it in for the placeholder or pending submenu then
            liveLists.OpenerItem = new StyledSubmenuItem(liveLists.ListMenu, carMenu);
            liveLists.OpenerItem.Title = "AdvancedData";
            var liveEntries = new AdvancedDataItemSet[ADVANCEDDATA_COUNT_SANITY_CEILING];
            for (int i = 0; i < ADVANCEDDATA_COUNT_SANITY_CEILING; i++)
            {
                int slotIndex = i;
                var set = new AdvancedDataItemSet();
                set.SlotItem = new NativeDynamicItem<string>(AdvancedDataRowTitle(slotIndex, "Slot"), ResolveDescriptor(CATEGORY_CAR, "AdvancedDataSlot"));
                stringCyclerItems.Add(set.SlotItem);
                // Every getter and setter below guards slotIndex against the vehicle's real live entry count, read once per access, before calling into the raw-memory path
                // A slot beyond that count has no real element to read (GetAdvancedDataElementAddress deliberately returns IntPtr.Zero for it)
                // Reading or writing through it anyway trips GetRawMemoryAddress's own zero-address anomaly warning for an entirely expected case
                WireAdvancedDataSlotCycler(RawOffsetItem(set.SlotItem), CATEGORY_CAR, h =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    return slotIndex < count ? RawMemory.ReadRawInt(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementSlot) : 0;
                }, (h, v) =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    if (slotIndex < count) RawMemory.WriteRawInt(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementSlot, v);
                }, keySuffix == null ? null : AdvancedDataRowTitle(slotIndex, "Slot") + keySuffix, targetSlot);
                set.IndexItem = new NativeDynamicItem<int>(AdvancedDataRowTitle(slotIndex, "Index"), ResolveDescriptor(CATEGORY_CAR, "AdvancedDataIndex"));
                WireIntField(set.IndexItem, AdvancedDataIndexField, BuildLiveIntStore(set.IndexItem, CATEGORY_CAR, AdvancedDataIndexField, h =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    return slotIndex < count ? RawMemory.ReadRawInt(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementIndex) : 0;
                }, (vehicle, h, v) =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    if (slotIndex < count) RawMemory.WriteRawInt(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementIndex, v);
                    return true;
                }, targetSlot, keySuffix == null ? null : AdvancedDataRowTitle(slotIndex, "Index") + keySuffix));
                set.ValueItem = new NativeDynamicItem<DisplayFloat>(AdvancedDataRowTitle(slotIndex, "Value"), ResolveDescriptor(CATEGORY_CAR, "AdvancedDataValue"));
                WireFloatField(set.ValueItem, AdvancedDataValueField, BuildLiveFloatStore(set.ValueItem, CATEGORY_CAR, AdvancedDataValueField, h =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    return slotIndex < count ? RawMemory.ReadRawFloat(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementValue) : 0.0f;
                }, (vehicle, h, v) =>
                {
                    int count = RawMemory.GetAdvancedDataCount(h);
                    if (slotIndex < count) RawMemory.WriteRawFloat(RawMemory.GetAdvancedDataElementAddress(h, slotIndex, count), AdvancedDataLayout.ElementValue, v);
                    return true;
                }, targetSlot, keySuffix == null ? null : AdvancedDataRowTitle(slotIndex, "Value") + keySuffix, null));
                advancedDataIntBindings.Add(intBindingByItem[set.IndexItem]);
                advancedDataFloatBindings.Add(floatBindingByItem[set.ValueItem]);
                set.FocusKeys = new[] { BuildFieldKey(CATEGORY_CAR, set.SlotItem.Title + keySuffix), BuildFieldKey(CATEGORY_CAR, set.IndexItem.Title + keySuffix), BuildFieldKey(CATEGORY_CAR, set.ValueItem.Title + keySuffix) };
                // Owned by the list until the entry's own submenu exists, so no other menu's scoped field sync reads these rows
                SetAdvancedDataRowOwner(set, liveLists.ListMenu);
                liveEntries[slotIndex] = set;
                yield return true;
            }
            liveLists.Entries = liveEntries;
            liveLists.AddSlotItem = new NativeItem(TITLE_ADD_SLOT, DESC_ADD_ADVANCEDDATA_ENTRY_LIVE);
            SubscribeActivated(liveLists.AddSlotItem, "BuildCarAdvancedDataMenuItems.AddSlotItem.Activated", () => AdvancedDataStores[(int)AdvancedDataMenuKind.Live].PressAddSlot(this, targetSlot));

            // A live Car with a real AdvancedData count of 0 gets a disabled placeholder in the opener's slot instead of an enabled empty submenu entry
            // The press grows the real array through the plugin when live growth is offered, and otherwise creates the pending block
            // The pending list shares the phantom list's XML store and builder, over a block keyed by the live vehicle's handling instead of the tree's phantom element
            NativeItem placeholderItem = new NativeItem("AdvancedData", PLACEHOLDER_CATEGORY_NOT_PRESENT);
            DimItemAsDisabled(placeholderItem);
            SubscribeActivated(placeholderItem, "BuildCarAdvancedDataMenuItems.placeholderItem.Activated", () => ActivateLiveAdvancedData(targetSlot));
            targetSlot.AdvancedData.CarPlaceholderItem = placeholderItem;
            foreach (bool step in BuildXmlAdvancedDataEntrySets(targetSlot, ResolveXmlAdvancedDataStore(AdvancedDataMenuKind.Pending), carMenu)) yield return step;
        }

        // The scalar rows are already built by the Weapon definition's BuildLiveRows, so this adds only the base-level damage row
        // The row is kept on the tree, since SyncWeaponSlotVisibility anchors the weapon slot submenus' insert position off it
        private IEnumerable<bool> BuildWeaponCategoryMenuItems(NativeMenu menu, Func<HandlingData, VehicleWeaponHandlingData> dataResolver, string keySuffix, EditorContext targetSlot)
        {
            // Base CHandlingData field, not part of CVehicleWeaponHandlingData's layout, so it reads and writes h.MemoryAddress (base), not dataResolver(h).MemoryAddress
            NativeDynamicItem<DisplayFloat> weaponDamageItem = new NativeDynamicItem<DisplayFloat>("WeaponDamageScaledToVehHealthMult", ResolveDescriptor(CATEGORY_WEAPON, "WeaponDamageScaledToVehHealthMult"));
            targetSlot.Weapon.WeaponDamageScaledToVehHealthMultItem = weaponDamageItem;
            AddTreeRow(menu, weaponDamageItem);
            WireFloatField(weaponDamageItem, WeaponDamageScaledField, BuildLiveFloatStore(weaponDamageItem, CATEGORY_WEAPON, WeaponDamageScaledField, h => dataResolver(h) != null ? RawMemory.ReadRawFloat(h.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult) : 0.0f, (vehicle, h, v) =>
            {
                if (dataResolver(h) == null) return false;
                RawMemory.WriteRawFloat(h.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult, v);
                return true;
            }, targetSlot, keySuffix == null ? null : WeaponDamageScaledField.Title + keySuffix, null));
            yield return true;
        }

        // One row per turret field, read from a live float[] array or a raw offset (the API exposes no property for the raw-offset ones)
        // The pitch fields are radians in both handling.meta and memory, so no row converts units
        private class TurretFieldEntry : FieldEntry
        {
            public int Offset;
            public Func<VehicleWeaponHandlingData, float[]> GetArray;
            public Action<VehicleWeaponHandlingData, float[]> SetArray;
        }

        private static readonly TurretFieldEntry[] TurretFields =
        {
            new TurretFieldEntry { Tag = "fTurretSpeed", Title = "TurretSpeed", GetArray = w => w.TurretSpeed, SetArray = (w, a) => w.TurretSpeed = a, Step = STEP_WEAPON_TURRET_SPEED, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fTurretPitchMin", Title = "TurretPitchMin", GetArray = w => w.TurretPitchMin, SetArray = (w, a) => w.TurretPitchMin = a, Step = STEP_WEAPON_TURRET_PITCH_MIN, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fTurretPitchMax", Title = "TurretPitchMax", GetArray = w => w.TurretPitchMax, SetArray = (w, a) => w.TurretPitchMax = a, Step = STEP_WEAPON_TURRET_PITCH_MAX, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fTurretCamPitchMin", Title = "TurretCamPitchMin", RawOffset = true, Offset = WeaponLayout.TurretCamPitchMin, Step = STEP_WEAPON_TURRET_CAM_PITCH_MIN, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fTurretCamPitchMax", Title = "TurretCamPitchMax", RawOffset = true, Offset = WeaponLayout.TurretCamPitchMax, Step = STEP_WEAPON_TURRET_CAM_PITCH_MAX, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fBulletVelocityForGravity", Title = "BulletVelocityForGravity", RawOffset = true, Offset = WeaponLayout.BulletVelocityForGravity, Step = STEP_WEAPON_BULLET_VELOCITY_FOR_GRAVITY, RequiresRespawn = true },
            new TurretFieldEntry { Tag = "fTurretPitchForwardMin", Title = "TurretPitchForwardMin", RawOffset = true, Offset = WeaponLayout.TurretPitchForwardMin, Step = STEP_WEAPON_TURRET_PITCH_FORWARD_MIN, RequiresRespawn = true },
        };

        // One row per TurretPitchLimitData member in struct order, so a row's array position is its member index
        // DefaultFloat is the game's value for an entry or member a file omits
        // The angles are degrees and BackwardForcedPitch is radians, in both handling.meta and memory
        // CycleMin and CycleMax are the schema's own ranges, which Left/Right cycling stops at
        private static readonly FieldEntry[] TurretPitchLimitFields =
        {
            new FieldEntry { Title = "ForwardAngleMin", Tag = "fForwardAngleMin", DefaultFloat = 0.0f, CycleMin = 0.0f, CycleMax = 180.0f, Step = STEP_WEAPON_FORWARD_ANGLE_MIN, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
            new FieldEntry { Title = "ForwardAngleMax", Tag = "fForwardAngleMax", DefaultFloat = 80.844345f, CycleMin = 0.0f, CycleMax = 180.0f, Step = STEP_WEAPON_FORWARD_ANGLE_MAX, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
            new FieldEntry { Title = "BackwardAngleMin", Tag = "fBackwardAngleMin", DefaultFloat = 125.30587f, CycleMin = 0.0f, CycleMax = 180.0f, Step = STEP_WEAPON_BACKWARD_ANGLE_MIN, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
            new FieldEntry { Title = "BackwardAngleMid", Tag = "fBackwardAngleMid", DefaultFloat = 139.51522f, CycleMin = 0.0f, CycleMax = 180.0f, Step = STEP_WEAPON_BACKWARD_ANGLE_MID, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
            new FieldEntry { Title = "BackwardAngleMax", Tag = "fBackwardAngleMax", DefaultFloat = 169.53821f, CycleMin = 0.0f, CycleMax = 180.0f, Step = STEP_WEAPON_BACKWARD_ANGLE_MAX, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
            new FieldEntry { Title = "BackwardForcedPitch", Tag = "fBackwardForcedPitch", DefaultFloat = -0.597f, CycleMin = -1.5f, CycleMax = 1.5f, Step = STEP_WEAPON_BACKWARD_FORCED_PITCH, RawOffset = true, RequiresRespawn = true, RequiresRearMountedTurret = true },
        };

        // keySuffix disambiguates an AdditionalEditor's undo/redo field keys, matching every other per-slot builder's own convention
        private IEnumerable<bool> BuildWeaponSlotMenuItems(NativeMenu menu, WeaponSlotItems[] slots, EditorContext owner, string keySuffix)
        {
            // Seeded to the real post-build state below, since every submenu entry is added here
            // SyncMenuSlot's cache has to agree with that, or its first call adds an item the menu already holds
            NativeItem[] currentSlotItems = new NativeItem[slots.Length];
            owner.Weapon.CurrentWeaponSlotItems = currentSlotItems;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                int slotIndex = slot;
                NativeMenu slotMenu = slots[slotIndex].Menu;
                NativeSubmenuItem slotItem = AddStyledSubmenu(menu, slotMenu);
                slotItem.Title = string.Format(TITLE_WEAPON_SLOT, (slotIndex + 1));
                slotMenu.Description = DESC_REORDER_SLOT_LIVE;
                slots[slotIndex].SubmenuItem = slotItem;
                currentSlotItems[slotIndex] = slotItem;
                yield return true;
                slots[slotIndex].WeaponHashItem = new NativeDynamicItem<string>("WeaponHash", WeaponHashDescription(0), WeaponHashTable[0].DisplayName);
                stringCyclerItems.Add(slots[slotIndex].WeaponHashItem);
                AddTreeRow(slotMenu, slots[slotIndex].WeaponHashItem);
                WireWeaponHashCycler(slots[slotIndex].WeaponHashItem, CATEGORY_WEAPON, slotIndex, keySuffix, owner);
                yield return true;
                slots[slotIndex].WeaponSeatItem = new NativeDynamicItem<string>("WeaponSeat", ResolveDescriptor(CATEGORY_WEAPON, "WeaponSeat"), VehicleSeatNames[0]);
                stringCyclerItems.Add(slots[slotIndex].WeaponSeatItem);
                AddTreeRow(slotMenu, slots[slotIndex].WeaponSeatItem);
                WireWeaponSeatCycler(slots[slotIndex].WeaponSeatItem, CATEGORY_WEAPON, slotIndex, keySuffix, owner);
                yield return true;
                if (weaponModTypeSupported)
                {
                    slots[slotIndex].WeaponModTypeItem = new NativeDynamicItem<string>("WeaponVehicleModType", ResolveDescriptor(CATEGORY_WEAPON, "WeaponVehicleModType"), VehicleModTypeNames[0]);
                    stringCyclerItems.Add(slots[slotIndex].WeaponModTypeItem);
                    AddTreeRow(slotMenu, slots[slotIndex].WeaponModTypeItem);
                    WireWeaponModTypeCycler(slots[slotIndex].WeaponModTypeItem, CATEGORY_WEAPON, slotIndex, keySuffix, owner);
                    yield return true;
                }
                if (slotIndex >= turretSlotCount) continue;
                for (int t = 0; t < TurretFields.Length; t++)
                {
                    TurretFieldEntry field = TurretFields[t];
                    var turretItem = new NativeDynamicItem<DisplayFloat>(field.Title, ResolveDescriptor(CATEGORY_WEAPON, field.Title));
                    AddTreeRow(slotMenu, turretItem);
                    if (field.RawOffset)
                    {
                        int offset = field.Offset;
                        WireFloatField(turretItem, field, BuildLiveFloatStore(turretItem, CATEGORY_WEAPON, field, h => h.VehicleWeaponHandlingData != null ? RawMemory.ReadRawFloat(h.VehicleWeaponHandlingData.MemoryAddress, offset + slotIndex * FLOAT_FIELD_STRIDE) : 0.0f, (vehicle, h, v) =>
                        {
                            if (h.VehicleWeaponHandlingData == null) return false;
                            RawMemory.WriteRawFloat(h.VehicleWeaponHandlingData.MemoryAddress, offset + slotIndex * FLOAT_FIELD_STRIDE, v);
                            return true;
                        }, owner, field.Title + "[" + slotIndex + "]" + keySuffix, null));
                        weaponSlotFloatBindings.Add(floatBindingByItem[turretItem]);
                    }
                    else
                    {
                        Func<VehicleWeaponHandlingData, float[]> getArray = field.GetArray;
                        Action<VehicleWeaponHandlingData, float[]> setArray = field.SetArray;
                        WireFloatField(turretItem, field, BuildLiveFloatStore(turretItem, CATEGORY_WEAPON, field, h =>
                        {
                            float[] turretValues = h.VehicleWeaponHandlingData != null ? getArray(h.VehicleWeaponHandlingData) : null;
                            return turretValues != null && turretValues.Length > slotIndex ? turretValues[slotIndex] : 0.0f;
                        }, (vehicle, h, v) =>
                        {
                            if (h.VehicleWeaponHandlingData == null) return false;
                            float[] turretValues = getArray(h.VehicleWeaponHandlingData);
                            if (turretValues != null && slotIndex < turretValues.Length)
                            {
                                turretValues[slotIndex] = v;
                                setArray(h.VehicleWeaponHandlingData, turretValues);
                            }
                            return true;
                        }, owner, field.Title + "[" + slotIndex + "]" + keySuffix, null));
                        weaponSlotFloatBindings.Add(floatBindingByItem[turretItem]);
                    }
                    yield return true;
                }
                // The slot menu starts listing every pitch limit row. SyncTurretPitchLimitVisibility removes them for a vehicle with no rear mounted turret
                slots[slotIndex].PitchLimitItems = new NativeItem[TurretPitchLimitFields.Length];
                slots[slotIndex].CurrentPitchLimitSlotItems = new NativeItem[TurretPitchLimitFields.Length];
                for (int p = 0; p < TurretPitchLimitFields.Length; p++)
                {
                    FieldEntry field = TurretPitchLimitFields[p];
                    int fieldIndex = p;
                    var limitItem = new NativeDynamicItem<DisplayFloat>(field.Title, ResolveDescriptor(CATEGORY_WEAPON, field.Title));
                    AddTreeRow(slotMenu, limitItem);
                    slots[slotIndex].PitchLimitItems[p] = limitItem;
                    slots[slotIndex].CurrentPitchLimitSlotItems[p] = limitItem;
                    WireFloatField(limitItem, field, BuildLiveFloatStore(limitItem, CATEGORY_WEAPON, field, h => h.VehicleWeaponHandlingData != null ? RawMemory.ReadRawFloat(h.VehicleWeaponHandlingData.MemoryAddress, RawMemory.TurretPitchLimitOffset(slotIndex, fieldIndex)) : 0.0f, (vehicle, h, v) =>
                    {
                        if (h.VehicleWeaponHandlingData == null) return false;
                        RawMemory.WriteRawFloat(h.VehicleWeaponHandlingData.MemoryAddress, RawMemory.TurretPitchLimitOffset(slotIndex, fieldIndex), v);
                        return true;
                    }, owner, field.Title + "[" + slotIndex + "]" + keySuffix, null));
                    weaponSlotFloatBindings.Add(floatBindingByItem[limitItem]);
                    yield return true;
                }
            }
            NativeItem addSlotItem = new NativeItem(TITLE_ADD_SLOT, string.Format(DESC_ADD_WEAPON_SLOT_LIVE, WeaponHashTable[DEFAULT_LIVE_WEAPON_SLOT_HASH_INDEX].DisplayName));
            owner.Weapon.WeaponAddSlotItem = addSlotItem;
            AddTreeRow(menu, addSlotItem);
            SubscribeActivated(addSlotItem, "BuildWeaponSlotMenuItems.addSlotItem.Activated", () => AddWeaponSlot(owner));
            yield return true;
        }

        // While set, AddTreeRow and ShowRowArrows skip the per-row layout work, and the tree's finish lays out each menu once
        private EditorContext buildingContext;
        // The library's private arrows flag, found once. A missing field leaves it null, and ShowRowArrows then uses the property with one WARN
        private static readonly System.Reflection.FieldInfo ArrowsAlwaysVisibleField = typeof(NativeSlidableItem).GetField("alwaysVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private bool arrowsFieldMissingWarned;
        // Set once BuildMenu's startup layout and width pass ran, so a tree that finishes later applies that layout itself
        private bool startupMenusLaidOut;
        // Gates the startup warm-up stage, which clears it once the trees, the Search model list, and the precompile are all done
        private bool startupWarmUpPending = true;

        // During a build a row joins the list directly, since each Add reruns the menu's whole row layout
        // FinishTreeMenuLayout then lays the menu out once after its last row
        // Outside a build the row is added at once, so AddStyledSubmenu's global callers still lay out immediately
        private void AddTreeRow(NativeMenu menu, NativeItem item)
        {
            if (buildingContext == null) menu.Add(item);
            else
            {
                // The same refusals Add makes
                if (item == null) throw new ArgumentNullException("item");
                if (menu.Items.Contains(item)) throw new InvalidOperationException("The item is already part of the menu.");
                menu.Items.Add(item);
            }
        }

        // The property setter recalculates the row on every assignment, so a build writes the private flag instead, which the menu's one layout pass reads
        // A row built now but listed later (an AdvancedData entry's rows) reads it when its entry menu adds it
        private void ShowRowArrows(NativeSlidableItem item)
        {
            if (buildingContext != null && ArrowsAlwaysVisibleField != null) ArrowsAlwaysVisibleField.SetValue(item, true);
            else
            {
                if (buildingContext != null && !arrowsFieldMissingWarned)
                {
                    arrowsFieldMissingWarned = true;
                    logger.Log(Dbg.Level.Warn, Dbg.ArrowsFieldMissing());
                }
                item.ArrowsAlwaysVisible = true;
            }
        }

        // A menu the build filled directly gets its one layout pass here: The SelectedIndex setter runs the row list and row layout once, as Add's first row did
        private static void FinishTreeMenuLayout(NativeMenu menu)
        {
            if (menu.Items.Count > 0) menu.SelectedIndex = 0;
        }

        // Current's build failing is a startup failure and rethrows, while an extra tree's marks it Failed with one ERROR
        // Always runs at least one step. Returns True once the tree is Built or Failed
        private bool AdvanceEditorBuild(EditorContext context, long budgetTicks)
        {
            if (context.BuildState == EditorBuildState.Built || context.BuildState == EditorBuildState.Failed) return true;
            if (context.BuildState == EditorBuildState.NotBuilt)
            {
                context.BuildState = EditorBuildState.Building;
                context.BuildSteps = BuildEditorContext(context).GetEnumerator();
            }
            long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool finished = false;
            buildingContext = context;
            try
            {
                do
                {
                    if (!context.BuildSteps.MoveNext())
                    {
                        finished = true;
                        break;
                    }
                    context.BuildStepCount++;
                } while (System.Diagnostics.Stopwatch.GetTimestamp() - tickStart < budgetTicks);
            }
            catch (Exception ex)
            {
                context.BuildState = EditorBuildState.Failed;
                context.BuildSteps = null;
                if (context == currentContext) throw;
                logger.Log(Dbg.Level.Error, Dbg.EditorTreeBuildFailed(context.Index, context.BuildStepCount, ex.GetType().Name, ex.Message));
                logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
                return true;
            }
            finally { buildingContext = null; }
            if (!finished) return false;
            context.BuildState = EditorBuildState.Built;
            context.BuildSteps = null;
            return true;
        }

        // An extra tree the warm-up left part-built finishes first, so only one tree is ever part-built and each tree's raw item ranges stay contiguous
        // Returns False for a tree whose build failed
        private bool EnsureEditorBuilt(EditorContext context)
        {
            for (int i = 1; i < editorContexts.Length; i++)
            {
                if (editorContexts[i] != context && editorContexts[i].BuildState == EditorBuildState.Building) AdvanceEditorBuild(editorContexts[i], long.MaxValue);
            }
            AdvanceEditorBuild(context, long.MaxValue);
            return context.BuildState == EditorBuildState.Built;
        }

        // One tree advances per call, in index order. Returns True once no extra tree is left to build
        private bool WarmUpEditorTrees()
        {
            for (int i = 1; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (context.BuildState == EditorBuildState.Built || context.BuildState == EditorBuildState.Failed) continue;
                AdvanceEditorBuild(context, WARM_UP_FRAME_BUDGET_MS * System.Diagnostics.Stopwatch.Frequency / 1000);
                return false;
            }
            return true;
        }

        // Every tree exists for the life of the script once built, mounted or not
        // mainMenu's own top-level rows and Settings' tree belong to no context, since they have no per-editor equivalent, so BuildMenu builds them directly
        private IEnumerable<bool> BuildEditorContext(EditorContext context)
        {
            // Recorded before any row exists, so the finish disables only the raw items this tree adds
            context.RawOffsetItemsStart = rawOffsetItems.Count;
            context.SpecialFlightFlagItemsStart = specialFlightFlagItems.Count;
            // Current's keys stay bare (null) and the extra editors' carry a suffix, since every one of these fields shares the same title across all 4 trees
            string keySuffix = context.KeySuffix;
            context.RootMenu = MakeMenu(TITLE_EDIT_HANDLING, SUBTITLE_HANDLING_DATA);
            if (context == currentContext)
            {
                // Stays fixed at the top, never repositioned by any later logic, including the growing AdditionalEditor list below it
                // mainMenu is no tree's menu, so its row is added through Add, which lays it out at once
                context.MainMenuItem = new StyledSubmenuItem(context.RootMenu, mainMenu);
                mainMenu.Add(context.MainMenuItem);
                context.MainMenuItem.Title = TITLE_EDIT_CURRENT_HANDLING;
            }
            else
            {
                // Constructed here but not added to mainMenu. ConfirmVehicleSelection inserts it on mount, and unmounting removes it again
                context.MainMenuItem = new StyledSubmenuItem(context.RootMenu, mainMenu);
            }
            CaptureOriginalItemColor(context.MainMenuItem);
            // The rows are filled as the build goes and assigned to context.Categories at the end, so a reader of an unfinished tree sees none
            CategoryDefinition[] definitions = CategoryDefinitions;
            var rows = new SubHandlingCategory[definitions.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new SubHandlingCategory { Definition = definitions[i] };
                rows[i].LiveMenu = MakeMenu(TITLE_EDIT_HANDLING, definitions[i].LongName);
                if (definitions[i].FlagsMenu != null) rows[i].LiveFlagsMenu = MakeMenu(TITLE_EDIT_HANDLING, definitions[i].FlagsMenu.RowTitle);
            }
            yield return true;
            context.Flags.ModelFlagsMenu = MakeMenu(TITLE_EDIT_HANDLING, "ModelFlags");
            context.Flags.HandlingFlagsMenu = MakeMenu(TITLE_EDIT_HANDLING, "HandlingFlags");
            context.Flags.DamageFlagsMenu = MakeMenu(TITLE_EDIT_HANDLING, "DamageFlags");
            context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].ListMenu = MakeMenu(TITLE_EDIT_HANDLING, "AdvancedData");
            context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending].ListMenu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(SUBTITLE_EXPORT_ONLY, "AdvancedData"));
            context.Weapon.WeaponSlots = new WeaponSlotItems[weaponSlotCount];
            context.Weapon.CustomWeaponHashes = new uint[weaponSlotCount];
            for (int i = 0; i < weaponSlotCount; i++) context.Weapon.WeaponSlots[i] = new WeaponSlotItems { Menu = MakeMenu(TITLE_EDIT_HANDLING, string.Format(TITLE_WEAPON_SLOT, (i + 1))) };
            yield return true;
            var ownLiveMenus = new List<NativeMenu> { context.RootMenu };
            foreach (SubHandlingCategory row in rows)
            {
                ownLiveMenus.Add(row.LiveMenu);
                if (row.LiveFlagsMenu != null) ownLiveMenus.Add(row.LiveFlagsMenu);
            }
            ownLiveMenus.AddRange(new[] { context.Flags.ModelFlagsMenu, context.Flags.HandlingFlagsMenu, context.Flags.DamageFlagsMenu, context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].ListMenu, context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending].ListMenu });
            for (int i = 0; i < context.Weapon.WeaponSlots.Length; i++) ownLiveMenus.Add(context.Weapon.WeaponSlots[i].Menu);
            foreach (NativeMenu menu in ownLiveMenus)
            {
                ConfigureMenu(menu, context);
                yield return true;
            }
            // CenterOfMassOffset writes through the shared BaseFields row's own Recompute (SyncCenterOfGravityOffset), which receives the vehicle the write resolved
            foreach (bool step in BuildCategoryMenuItems(context.RootMenu, null, BaseFields, h => h, keySuffix, context)) yield return step;
            // The rocket boost rows start in the menu. SyncRocketBoostVisibility removes them for a vehicle with no rocket boost
            context.CurrentRocketBoostSlotItems = context.RocketBoostItems.ToArray();
            context.AiHandlingItem = new NativeDynamicItem<string>("AIHandling", AIHandlingDescription(AIHandlingNames[0]), AIHandlingNames[0]);
            ShowRowArrows(context.AiHandlingItem);
            stringCyclerItems.Add(context.AiHandlingItem);
            AddTreeRow(context.RootMenu, context.AiHandlingItem);
            RegisterUndoFocusItem(AiHandlingEdit.BuildFocusKey(context.Index), context.AiHandlingItem);
            NativeDynamicItem<string> aiHandlingItemForSlot = context.AiHandlingItem;
            SubscribeItemChanged(aiHandlingItemForSlot, "BuildEditorContext.AiHandlingItem.ItemChanged", e =>
            {
                // The display always mirrors the live handling, so it only steps once the repoint succeeds
                if (Entity.IsNullOrNotExisting(context.TargetVehicle)) return;
                string oldName = (string)e.Object;
                int aiIndex = Array.IndexOf(AIHandlingNames, oldName);
                if (aiIndex < 0) aiIndex = 0;
                aiIndex = e.Direction == LemonUI.Menus.Direction.Left ? (aiIndex - 1 + AIHandlingNames.Length) % AIHandlingNames.Length : (aiIndex + 1) % AIHandlingNames.Length;
                string newName = AIHandlingNames[aiIndex];
                CAIHandlingInfo slotInfo = CAIHandlingInfo.GetByHash((AIHandlingHash)AIHandlingHashes[aiIndex]);
                if (!slotInfo.IsValid) return;
                context.TargetVehicle.HandlingData.CAIHandlingInfo = slotInfo;
                e.Object = newName;
                aiHandlingItemForSlot.Description = AIHandlingDescription(newName);
                pendingWidthRecalc = true;
                RecordStructuralCycleEdit(new AiHandlingEdit(context.Index, oldName, newName) { Handling = ResolveHandlingKey(context.TargetVehicle) }, null);
            });
            // Registered once, under the bare key every base field's Selective Copy resolves against regardless of which tree is Active
            // The closure only repoints the passed-in vehicle's HandlingData, so it stays correct for whichever vehicle ApplySelectiveCopyField resolves
            // fieldRegistry is one shared dictionary, so the last editor context built simply overwrites this same key with an identical closure, which is harmless
            fieldRegistry["AIHandling"] = (vehicle, text) =>
            {
                CAIHandlingInfo aiInfo = CAIHandlingInfo.GetByHash((AIHandlingHash)GTA.StringHash.AtStringHash(text));
                if (!aiInfo.IsValid) return ApplyResult.Rejected();
                vehicle.HandlingData.CAIHandlingInfo = aiInfo;
                return ApplyResult.Applied(ReplayFlags.None);
            };
            yield return true;
            AddStyledSubmenu(context.RootMenu, context.Flags.ModelFlagsMenu);
            yield return true;
            AddStyledSubmenu(context.RootMenu, context.Flags.HandlingFlagsMenu);
            yield return true;
            context.Flags.DamageFlagsItem = AddStyledSubmenu(context.RootMenu, context.Flags.DamageFlagsMenu);
            yield return true;
            foreach (SubHandlingCategory row in rows)
            {
                row.LiveSubmenu = AddStyledSubmenu(context.RootMenu, row.LiveMenu, row.Definition.LongName);
                yield return true;
            }

            // Registered into allMenus: Both this context's own live menus (ownLiveMenus, built above) and its phantom tree
            // Every allMenus-driven consumer (Fast Nav, the width and banner-recenter pass, ApplyMenuPosition) depends on this context's menus being present here
            var phantomMenus = new List<NativeMenu>();
            foreach (bool step in BuildPhantomSubDataMenuItems(context, rows, phantomMenus)) yield return step;
            int knownMenuCount = allMenus.Length;
            Array.Resize(ref allMenus, knownMenuCount + ownLiveMenus.Count + phantomMenus.Count);
            ownLiveMenus.CopyTo(allMenus, knownMenuCount);
            phantomMenus.CopyTo(allMenus, knownMenuCount + ownLiveMenus.Count);
            foreach (NativeMenu menu in phantomMenus)
            {
                ConfigureMenu(menu, context);
                yield return true;
            }
            foreach (SubHandlingCategory row in rows)
            {
                CategoryDefinition definition = row.Definition;
                foreach (bool step in definition.BuildLiveRows(this, row.LiveMenu, keySuffix, context)) yield return step;
                if (definition.FlagsMenu != null)
                {
                    if (definition.FlagsMenu.MenuDescriptionKey != null) row.LiveFlagsMenu.Description = ResolveDescriptor(definition.Name, definition.FlagsMenu.MenuDescriptionKey);
                    AddStyledSubmenu(row.LiveMenu, row.LiveFlagsMenu);
                    yield return true;
                }
                if (definition.HasAdvancedData) foreach (bool step in BuildCarAdvancedDataMenuItems(row.LiveMenu, context, keySuffix)) yield return step;
                if (definition.HasWeaponSlots)
                {
                    foreach (bool step in BuildWeaponCategoryMenuItems(row.LiveMenu, h => h.VehicleWeaponHandlingData, keySuffix, context)) yield return step;
                    foreach (bool step in BuildWeaponSlotMenuItems(row.LiveMenu, context.Weapon.WeaponSlots, context, keySuffix)) yield return step;
                }
            }
            foreach (bool step in BuildFlagsMenuItemsForSlot(context, keySuffix, rows[ResolveCategoryDefinition(CATEGORY_CAR).Index].LiveFlagsMenu, rows[ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT).Index].LiveFlagsMenu)) yield return step;
            foreach (SubHandlingCategory row in rows) BuildSubHandlingCategory(context, row);
            context.Categories = rows;
            context.CurrentSubDataSlotItems = new NativeItem[rows.Length];
            for (int i = 0; i < rows.Length; i++) context.CurrentSubDataSlotItems[i] = rows[i].LiveSubmenu;
            var ownAllMenus = new List<NativeMenu>(ownLiveMenus);
            ownAllMenus.AddRange(phantomMenus);
            context.AllOwnMenus = ownAllMenus.ToArray();
            context.OwnLiveMenus = ownLiveMenus.ToArray();
            context.StartupMenuCount = context.AllOwnMenus.Length;
            yield return true;
            foreach (bool step in FinishEditorContext(context)) yield return step;
        }

        // A tree that finishes after BuildMenu's startup layout then takes that layout itself, each value read from the current setting
        private IEnumerable<bool> FinishEditorContext(EditorContext context)
        {
            NativeMenu[] menus = context.AllOwnMenus;
            for (int i = 0; i < menus.Length; i++)
            {
                FinishTreeMenuLayout(menus[i]);
                yield return true;
            }
            ResolveBindingMenus(context);
            yield return true;
            if (!RawMemory.RawOffsetsSupported) DisableRawOffsetItems(context.RawOffsetItemsStart);
            // Independent of the pass above: A build can satisfy RawMemory.RawOffsetsSupported's lower floor while still predating this specific offset's own layout
            DisableSpecialFlightFlagItemsIfUnsupported(context.SpecialFlightFlagItemsStart);
            // The menu restore and the seat merge map a menu by its position in its tree, which holds only while every tree lists the same menus
            if (context != currentContext && menus.Length != currentContext.StartupMenuCount) logger.Log(Dbg.Level.Error, Dbg.EditorTreeMenuCountMismatch(context.Index, menus.Length, currentContext.StartupMenuCount));
            yield return true;
            if (!startupMenusLaidOut) yield break;
            for (int i = 0; i < menus.Length; i++)
            {
                ApplyLateMenuLayout(menus[i]);
                yield return true;
            }
            // The cold text measurements run one row per step, so the warm width pass after them reads only cached widths
            for (int i = 0; i < menus.Length; i++)
            {
                for (int j = 0; j < menus[i].Items.Count; j++)
                {
                    MeasureRowWidth(menus[i].Items[j], null);
                    yield return true;
                }
            }
            for (int i = 0; i < menus.Length; i++)
            {
                SetMenuToTargetWidth(menus[i]);
                yield return true;
            }
        }

        // The caller passes the 2 category flags menus in, since the tree's rows are assigned to it only when its build ends
        private IEnumerable<bool> BuildFlagsMenuItemsForSlot(EditorContext slot, string keySuffix, NativeMenu advancedFlagsMenu, NativeMenu specialFlightFlagsMenu)
        {
            // The menus and bindings follow FlagFamilies' order, which the loop below indexes all 3 by
            NativeMenu[] menus = { slot.Flags.ModelFlagsMenu, slot.Flags.HandlingFlagsMenu, slot.Flags.DamageFlagsMenu, advancedFlagsMenu, specialFlightFlagsMenu };
            List<FlagCheckboxBinding>[] bindings = { slot.Flags.ModelFlagBindings, slot.Flags.HandlingFlagBindings, slot.Flags.DamageFlagBindings, slot.Flags.AdvancedFlagBindings, slot.Flags.SpecialFlightFlagBindings };
            FlagFamily[] families = FlagFamilies;
            for (int i = 0; i < families.Length; i++)
            {
                FlagFamily family = families[i];
                NativeMenu menu = menus[i];
                foreach (bool step in BuildFlagCategory(slot, menu, bindings[i], family.Entries, family.DescriptorCategory, family.ResolveAddress, family.Offset, flags => menu.Name = FlagsMenuName(family.LiveSubtitleTemplate, flags, family.Entries), keySuffix, family.IsVersionGated)) yield return step;
            }
        }
    }
}
