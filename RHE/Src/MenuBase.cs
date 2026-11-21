using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using LemonUI;
using LemonUI.Elements;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The menu library's default disabled text color, shared by dimmed titles and badges so a disabled row's badge matches its title
        private static readonly Color DIMMED_TEXT_COLOR = Color.FromArgb(255, 163, 159, 148);

        // Menus
        private NativeMenu mainMenu;
        private NativeMenu savedHandlingsMenu;
        private NativeMenu previewMenu;
        // One entry per preview category (base sub-handling, nested AdvancedFlags, AdvancedData, and SpecialFlightFlags, and the Weapon slot submenus)
        // One object per category means one lookup per category, and no parallel per-category collections that could drift out of sync
        private class PreviewCategoryData
        {
            public NativeMenu Menu;
            public NativeSubmenuItem SubmenuItem;
            public List<NativeItem> ItemPool = new List<NativeItem>();
            // Only populated for flag-category menus (ModelFlags, HandlingFlags, DamageFlags, AdvancedFlags, SpecialFlightFlags)
            // Reused across refreshes instead of constructing a fresh NativeCheckboxItem per flag every time, like the scalar ItemPool above
            public List<NativeCheckboxItem> CheckboxPool = new List<NativeCheckboxItem>();
            // Stands in for this category on whichever panel lacks it while the other panel has it
            // Preview and Diff then always render the same number of top-level category rows, and the passive index-based mirror stays aligned
            // Colored green on Preview, red on Diff, disabled-look via focus dimming
            public NativeItem MissingPlaceholder;
        }

        private Dictionary<string, PreviewCategoryData> previewCategoryData = new Dictionary<string, PreviewCategoryData>();
        // Each category placeholder's focused tint (green on the Preview side, red on the Diff side)
        // A placeholder stays Enabled = false, so its color lives on the Disabled tier
        // SyncPreviewEnabled swaps it between this tint (focused) and its darker unfocused variant
        private Dictionary<NativeItem, Color> placeholderTints = new Dictionary<NativeItem, Color>();
        // Each flag checkbox whose bit differs between Preview's saved handling and Diff's baseline, mapped to its focused tint (green on Preview, red on Diff)
        // A checkbox stays Enabled = false, so the tint lives on the Disabled tier, applied by SyncPreviewFlagCheckboxColors
        private Dictionary<NativeCheckboxItem, Color> flagBitTints = new Dictionary<NativeCheckboxItem, Color>();
        // The category rows of diffMenu that FinalizeCategoryPlacement colored as baseline-only, rebuilt by every PopulateDiffMenu
        // The banner count reads it, since a colored category row keeps no other trace of why it is red
        private HashSet<NativeItem> diffBaselineOnlyCategoryRows = new HashSet<NativeItem>();
        // Canonical Preview category order: Every definition without weapon slots in the definition order, then the 3 top-level flag categories
        // The weapon category is deliberately absent since Weapon preview is its own 2-level structure with no single-row shape the other categories share
        // Shared by BuildMenu and RefreshPreviewMenu, so the two call sites can never drift out of sync
        // Built on first use and not by a static field initializer, since it reads the category definitions
        private static string[] previewCategoryOrder;
        private static string[] PreviewCategoryOrder
        {
            get
            {
                if (previewCategoryOrder == null) previewCategoryOrder = BuildPreviewCategoryOrder();
                return previewCategoryOrder;
            }
        }

        private static string[] BuildPreviewCategoryOrder()
        {
            var order = new List<string>();
            foreach (CategoryDefinition definition in CategoryDefinitions) if (!definition.HasWeaponSlots) order.Add(definition.LongName);
            order.AddRange(PreviewFlagFieldNames);
            return order.ToArray();
        }

        // The render loop runs Weapon's 3 whole-category scalars through the same generic path as every other category, so Weapon needs a position here
        // Only its per-slot rows are appended separately
        private static string[] previewCategoryRenderOrder;
        private static string[] PreviewCategoryRenderOrder
        {
            get
            {
                if (previewCategoryRenderOrder == null) previewCategoryRenderOrder = BuildPreviewCategoryRenderOrder();
                return previewCategoryRenderOrder;
            }
        }

        private static string[] BuildPreviewCategoryRenderOrder()
        {
            var order = new List<string>(PreviewCategoryOrder);
            foreach (CategoryDefinition definition in CategoryDefinitions) if (definition.HasWeaponSlots) order.Add(definition.LongName);
            return order.ToArray();
        }

        // Weapon preview has its own 2-level structure
        // previewWeaponMenu holds the 3 top-level scalars plus one NativeSubmenuItem per slot
        // Each leads into its own pooled row-menu in previewWeaponSlotMenus, sized to weaponSlotCount
        private NativeMenu previewWeaponMenu;
        private NativeMenu[] previewWeaponSlotMenus;
        private NativeSubmenuItem[] previewWeaponSlotSubmenuItems;
        // ShowPreviewDiff's tree is a passive mirror of previewMenu's tree structure
        // previewDiffCategoryData parallels previewCategoryData, and diffWeaponMenu and diffWeaponSlotMenus parallel previewWeaponMenu and previewWeaponSlotMenus
        // diffMenu never takes input focus, and its visible submenu is driven entirely by SyncDiffMirror
        private NativeMenu diffMenu;
        private List<NativeItem> diffItemPool = new List<NativeItem>();
        // Top-level placeholder rows for a base field or flag category present only on the other side, grown on demand like the base pools
        private List<NativeItem> previewBasePlaceholderPool = new List<NativeItem>();
        private List<NativeItem> diffBasePlaceholderPool = new List<NativeItem>();
        private Dictionary<string, PreviewCategoryData> previewDiffCategoryData = new Dictionary<string, PreviewCategoryData>();
        private NativeMenu diffWeaponMenu;
        private NativeMenu[] diffWeaponSlotMenus;
        private NativeSubmenuItem[] diffWeaponSlotSubmenuItems;
        // One "Entry N" submenu per AdvancedData entry inside each side's AdvancedData category, indexed by entry
        // Created on first use by EnsurePreviewAdvancedDataEntryMenu and kept, since a saved handling can list up to ADVANCEDDATA_COUNT_SANITY_CEILING entries
        private List<NativeMenu> previewAdvancedDataEntryMenus = new List<NativeMenu>();
        private List<NativeSubmenuItem> previewAdvancedDataEntrySubmenuItems = new List<NativeSubmenuItem>();
        private List<NativeMenu> diffAdvancedDataEntryMenus = new List<NativeMenu>();
        private List<NativeSubmenuItem> diffAdvancedDataEntrySubmenuItems = new List<NativeSubmenuItem>();
        // True whenever a baseline currently resolves for the highlighted saved handling, independent of which specific Diff-tree menu is on screen right now
        // It is SyncDiffMirror's top gate, so the mirror keeps running across a descend and ascend cycle instead of stopping the moment diffMenu itself hides
        private bool diffBaselineActive;
        private NativeItem respawnVehicleItem;
        private NativeItem unloadVehicleItem;
        private NativeItem selectVehicleItem;
        private NativeSubmenuItem savedHandlingsItem;
        private NativeItem saveHandlingItem;
        private NativeSubmenuItem settingsMenuItem;
        private NativeMenu settingsMenu;
        private NativeItem keyToggleMenuItem;
        private NativeItem enableCheatStringItem;
        private NativeItem menuActionModifierItem;
        // One entry per VisualizationCheckboxes row (MenuSettings.cs), same order
        private NativeCheckboxItem[] visualizationCheckboxItems;
        private NativeListItem<string> loggingModeItem;
        private NativeCheckboxItem alwaysClearLogItem;
        private NativeListItem<string> autoRespawnItem;
        private NativeCheckboxItem resetAllSettingsItem;
        private StyledSeparator settingsScriptSep;
        private StyledSeparator settingsExportingSep;
        private StyledSeparator settingsHandlingPreviewSep;
        private StyledSeparator settingsVisualizationSep;
        private StyledSeparator settingsAppearanceSep;
        private StyledSeparator settingsExperimentalSep;
        private StyledSeparator settingsDebugSep;
        private NativeCheckboxItem liveSubHandlingDataItem;
        private static readonly string[] LogModeLabels = { LABEL_LOGGING_WARN_ERROR, LABEL_LOGGING_ALL, LABEL_LOGGING_DEFAULT_PERF, LABEL_LOGGING_DEFAULT_INFO };
        // Named banner color presets. Rows 0-5 of BannerColorPresetRgb match indices 0-5
        // Custom (BANNER_COLOR_CUSTOM_INDEX) is user-defined with no corresponding row
        private static readonly string[] BannerColorPresetNames = { LABEL_BANNER_DEFAULT, LABEL_BANNER_BLUE, LABEL_BANNER_RED, LABEL_BANNER_PURPLE, LABEL_BANNER_GREEN, LABEL_BANNER_BLACK, LABEL_BANNER_CUSTOM };
        private static readonly int[,] BannerColorPresetRgb = { { BANNER_DEFAULT_RED, BANNER_DEFAULT_GREEN, BANNER_DEFAULT_BLUE }, { 0, 60, 120 }, { 140, 0, 0 }, { 100, 0, 150 }, { 82, 163, 84 }, { 0, 0, 0 } };
        private NativeListItem<string> bannerColorItem;
        private NativeListItem<string> menuAlignItem;
        private NativeListItem<string> maxRowsItem;
        private NativeCheckboxItem alwaysOverwriteItem;
        private NativeCheckboxItem promptOnSaveItem;
        private NativeCheckboxItem autosaveItem;
        private NativeCheckboxItem previewByDefaultItem;
        private NativeCheckboxItem showPreviewDiffItem;
        private NativeCheckboxItem isolatedDrawingItem;
        private NativeCheckboxItem reassertDriftedFieldsItem;
        private static readonly string[] AIHandlingNames = { "AVERAGE", "SPORTS_CAR", "TRUCK", "CRAP" };
        // Each name's hash, computed once, since the editor compares them on every sync
        // The API's string hash is pure managed code, so a static initializer may call it
        private static readonly uint[] AIHandlingHashes = Array.ConvertAll(AIHandlingNames, name => GTA.StringHash.AtStringHash(name));
        // Weapon handling slot and turret counts are fixed for the life of the game process, since the API derives both from Game.FileVersion once
        // The slot submenus are sized once in BuildMenu rather than per vehicle
        private static readonly Version GAME_VERSION_1180_2 = new Version(1, 0, 1180, 2);
        private static readonly Version GAME_VERSION_1103_2 = new Version(1, 0, 1103, 2);
        // SpecialFlightHandlingFlags' own offset only exists at this higher threshold, above RawMemory.RawOffsetsSupported's general floor
        private static readonly Version GAME_VERSION_1493_0 = new Version(1, 0, 1493, 0);
        private int weaponSlotCount;
        private int turretSlotCount;
        private bool weaponModTypeSupported;
        private class WeaponSlotItems
        {
            public NativeMenu Menu;
            public NativeSubmenuItem SubmenuItem;
            public NativeDynamicItem<string> WeaponHashItem;
            public NativeDynamicItem<string> WeaponSeatItem;
            public NativeDynamicItem<string> WeaponModTypeItem;
            // A turret slot's 6 pitch limit rows, and which of them the slot menu currently lists (all or none, by the vehicle's rear mounted turret flag)
            // Null past the turret slots
            public NativeItem[] PitchLimitItems;
            public NativeItem[] CurrentPitchLimitSlotItems;
        }

        // DisplayName is the friendly menu text, and RawName is the game's own slot name
        // Position *is* the raw Slot value, 0-49
        // DisplayName is cross-derived from the API's VehicleModType enum where a clear conceptual match exists
        // The remaining entries (numbered variants, tuning-only slots with no VehicleModType equivalent) are named directly from their RawName
        private class AdvancedDataSlotEntry { public string DisplayName; public string RawName; }
        private static readonly AdvancedDataSlotEntry[] AdvancedDataSlotTable = {
            new AdvancedDataSlotEntry { DisplayName = "Spoiler", RawName = "VMT_SPOILER" }, new AdvancedDataSlotEntry { DisplayName = "Front Bumper", RawName = "VMT_BUMPER_F" },
            new AdvancedDataSlotEntry { DisplayName = "Rear Bumper", RawName = "VMT_BUMPER_R" }, new AdvancedDataSlotEntry { DisplayName = "Side Skirt", RawName = "VMT_SKIRT" },
            new AdvancedDataSlotEntry { DisplayName = "Exhaust", RawName = "VMT_EXHAUST" }, new AdvancedDataSlotEntry { DisplayName = "Frame", RawName = "VMT_CHASSIS" },
            new AdvancedDataSlotEntry { DisplayName = "Grille", RawName = "VMT_GRILL" }, new AdvancedDataSlotEntry { DisplayName = "Hood", RawName = "VMT_BONNET" },
            new AdvancedDataSlotEntry { DisplayName = "Left Fender", RawName = "VMT_WING_L" }, new AdvancedDataSlotEntry { DisplayName = "Right Fender", RawName = "VMT_WING_R" },
            new AdvancedDataSlotEntry { DisplayName = "Roof", RawName = "VMT_ROOF" }, new AdvancedDataSlotEntry { DisplayName = "Plate Holder", RawName = "VMT_PLTHOLDER" },
            new AdvancedDataSlotEntry { DisplayName = "Vanity Plate", RawName = "VMT_PLTVANITY" }, new AdvancedDataSlotEntry { DisplayName = "Interior 1", RawName = "VMT_INTERIOR1" },
            new AdvancedDataSlotEntry { DisplayName = "Interior 2", RawName = "VMT_INTERIOR2" }, new AdvancedDataSlotEntry { DisplayName = "Interior 3", RawName = "VMT_INTERIOR3" },
            new AdvancedDataSlotEntry { DisplayName = "Interior 4", RawName = "VMT_INTERIOR4" }, new AdvancedDataSlotEntry { DisplayName = "Interior 5", RawName = "VMT_INTERIOR5" },
            new AdvancedDataSlotEntry { DisplayName = "Seats", RawName = "VMT_SEATS" }, new AdvancedDataSlotEntry { DisplayName = "Steering Wheel", RawName = "VMT_STEERING" },
            new AdvancedDataSlotEntry { DisplayName = "Shift Knob", RawName = "VMT_KNOB" }, new AdvancedDataSlotEntry { DisplayName = "Plaque", RawName = "VMT_PLAQUE" },
            new AdvancedDataSlotEntry { DisplayName = "Trim Design", RawName = "VMT_ICE" }, new AdvancedDataSlotEntry { DisplayName = "Trunk", RawName = "VMT_TRUNK" },
            new AdvancedDataSlotEntry { DisplayName = "Hydraulics", RawName = "VMT_HYDRO" }, new AdvancedDataSlotEntry { DisplayName = "Engine Bay 1", RawName = "VMT_ENGINEBAY1" },
            new AdvancedDataSlotEntry { DisplayName = "Engine Bay 2", RawName = "VMT_ENGINEBAY2" }, new AdvancedDataSlotEntry { DisplayName = "Engine Bay 3", RawName = "VMT_ENGINEBAY3" },
            new AdvancedDataSlotEntry { DisplayName = "Chassis 2", RawName = "VMT_CHASSIS2" }, new AdvancedDataSlotEntry { DisplayName = "Chassis 3", RawName = "VMT_CHASSIS3" },
            new AdvancedDataSlotEntry { DisplayName = "Chassis 4", RawName = "VMT_CHASSIS4" }, new AdvancedDataSlotEntry { DisplayName = "Chassis 5", RawName = "VMT_CHASSIS5" },
            new AdvancedDataSlotEntry { DisplayName = "Left Door", RawName = "VMT_DOOR_L" }, new AdvancedDataSlotEntry { DisplayName = "Right Door", RawName = "VMT_DOOR_R" },
            new AdvancedDataSlotEntry { DisplayName = "Livery", RawName = "VMT_LIVERY_MOD" }, new AdvancedDataSlotEntry { DisplayName = "Light Bar", RawName = "VMT_LIGHTBAR" },
            new AdvancedDataSlotEntry { DisplayName = "Engine", RawName = "VMT_ENGINE" }, new AdvancedDataSlotEntry { DisplayName = "Brakes", RawName = "VMT_BRAKES" },
            new AdvancedDataSlotEntry { DisplayName = "Transmission", RawName = "VMT_GEARBOX" }, new AdvancedDataSlotEntry { DisplayName = "Horn", RawName = "VMT_HORN" },
            new AdvancedDataSlotEntry { DisplayName = "Suspension", RawName = "VMT_SUSPENSION" }, new AdvancedDataSlotEntry { DisplayName = "Armor", RawName = "VMT_ARMOUR" },
            new AdvancedDataSlotEntry { DisplayName = "Nitrous", RawName = "VMT_NITROUS" }, new AdvancedDataSlotEntry { DisplayName = "Turbo", RawName = "VMT_TURBO" },
            new AdvancedDataSlotEntry { DisplayName = "Subwoofer", RawName = "VMT_SUBWOOFER" }, new AdvancedDataSlotEntry { DisplayName = "Tire Smoke", RawName = "VMT_TYRE_SMOKE" },
            new AdvancedDataSlotEntry { DisplayName = "Hydraulics Tuning", RawName = "VMT_HYDRAULICS" }, new AdvancedDataSlotEntry { DisplayName = "Xenon Lights", RawName = "VMT_XENON_LIGHTS" },
            new AdvancedDataSlotEntry { DisplayName = "Wheels", RawName = "VMT_WHEELS" }, new AdvancedDataSlotEntry { DisplayName = "Rear Wheels", RawName = "VMT_WHEELS_REAR_OR_HYDRAULICS" },
        };

        // One AdvancedData entry's Slot, Index, and Value rows are built for every entry up to ADVANCEDDATA_COUNT_SANITY_CEILING at startup
        // That way their field keys always exist
        // Its own "Entry N" submenu and list row are created by EnsureAdvancedDataEntryMenu the first time the entry is listed (null until then)
        private class AdvancedDataItemSet
        {
            public NativeDynamicItem<string> SlotItem;
            public NativeDynamicItem<int> IndexItem;
            public NativeDynamicItem<DisplayFloat> ValueItem;
            public NativeMenu Menu;
            public NativeSubmenuItem SubmenuItem;
            // The undo focus keys of the 3 rows. The entry's list row registers under each, so an undo fired from the list lands on it
            public string[] FocusKeys;
        }

        // Reflects the live flags value on sync. BitIndex is the authoritative bit position, independent of the item's display order in its menu
        // BaseDescription is the row's description without its hex prefix, which a row lock leads with its reason
        private class FlagCheckboxBinding { public NativeCheckboxItem Item; public int BitIndex; public string BaseDescription; }
        // Decouples a flag's display order (its position in a FlagNameEntry[] array) from its bit index
        // Related flags can then be grouped together in the menu without disturbing the underlying bitmask, and array position never implies bit index
        private struct FlagNameEntry
        {
            public string Name;
            public int BitIndex;
            // -1 (no group) for the vast majority of flags. Entries sharing the same non-negative group within one BuildFlagCategory call conflict
            // Ticking one unticks the others
            // A group with a hub is the exception: 2 members conflict only when one of them is the hub, so the rest stay combinable
            public int FlagConflictGroup;
            public bool FlagConflictHub;
            // The game reads the flag only when the vehicle spawns, so an edit requests an auto respawn
            // HF_EXT_WHEEL_BOUNDS_COL counts because the wheel bounds steer only when it is set with HF_STEER_ALL_WHEELS at spawn
            // SF_FORCE_SPECIAL_FLIGHT_WHEN_DRIVEN counts because its wheels become ground probes at spawn
            public bool RequiresRespawn;
            // The offroad flags set the vehicle's gravity, which the game copies at spawn. An edit re-reads it through SET_VEHICLE_GRAVITY
            public bool RefreshesVehicle;
            public FlagNameEntry(string name, int bitIndex) : this(name, bitIndex, -1, false) { }
            public FlagNameEntry(string name, int bitIndex, int flagConflictGroup) : this(name, bitIndex, flagConflictGroup, false) { }
            public FlagNameEntry(string name, int bitIndex, int flagConflictGroup, bool flagConflictHub) { Name = name; BitIndex = bitIndex; FlagConflictGroup = flagConflictGroup; FlagConflictHub = flagConflictHub; RequiresRespawn = false; RefreshesVehicle = false; }
        }

        private NativeMenu[] allMenus;
        private bool navigationSpedUp;
        private float navHoldSeconds;

        // Opening the menu syncs the rows of context, which is Current for the menus no tree owns (main, Saved Handlings, Settings)
        // The width-transition seed (lastVisibleEditorWidth) stays shared across every tree deliberately
        // It only affects the starting point of a cosmetic fade on first open, never the final target width, which RecalculateTargetWidth always computes per menu
        private void ConfigureMenu(NativeMenu menu, EditorContext context)
        {
            pool.Add(menu);
            menu.HeaderBehavior = HeaderBehavior.AlwaysShow;
            menu.Width = MENU_WIDTH;
            menu.MaxItems = menuMaxRows;
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            menu.RotateCamera = false;
            menu.DisableControls = false;
            SubscribeShown(menu, "ConfigureMenu.Shown.WidthSeed", () =>
            {
                currentMenuWidths[menu] = lastVisibleEditorWidth;
                menu.Width = lastVisibleEditorWidth;
                RecenterBannerText(menu);
            });
            SubscribeClosed(menu, "ConfigureMenu.Closed", () => { lastVisibleEditorWidth = menu.Width; });
            SubscribeShown(menu, "ConfigureMenu.Shown.FieldSync", () => SyncEditorContext(context, menu));
            // Showing any menu of a tree makes that tree Active
            // This subscribes before the hints do, so a Respawn or Save hint reads the new Active editor on the menu's first frame
            if (ResolveHintHostKind(menu) == HintHosts.EditorTree) SubscribeShown(menu, "ConfigureMenu.Shown.ActiveTree", () => MakeTreeActive(context));
            // The native pause trigger closes whatever menu is visible, and the dispatcher reopens it within the same tick
            RegisterMenuNode(menu, menu == savedHandlingsMenu ? MenuFamily.SavedHandlings : MenuFamily.GlobalOrTree, null, false);
            RegisterHintMenu(menu, context);
        }

        // Applies every per-menu step startup gives its menus, each in its current state, so a menu created after BuildMenu matches its siblings
        private void RegisterRuntimeMenu(NativeMenu menu, EditorContext context)
        {
            ConfigureMenu(menu, context);
            Array.Resize(ref allMenus, allMenus.Length + 1);
            allMenus[allMenus.Length - 1] = menu;
            Array.Resize(ref context.AllOwnMenus, context.AllOwnMenus.Length + 1);
            context.AllOwnMenus[context.AllOwnMenus.Length - 1] = menu;
            ApplyLateMenuLayout(menu);
            SetMenuToTargetWidth(menu);
        }

        // The layout BuildMenu's startup passes give every menu, for a menu that exists only after them. Each value is the current setting
        private void ApplyLateMenuLayout(NativeMenu menu)
        {
            menu.Alignment = menuAlignLeft ? GTA.UI.Alignment.Left : GTA.UI.Alignment.Right;
            ApplyMenuMaxItems(menu, menuMaxRows);
            Color color, textColor;
            ResolveMenuThemeColors(out color, out textColor);
            ApplyMenuThemeTo(menu, color, textColor);
            menu.HeldTime = navigationSpedUp ? NAV_FAST_HELD_TIME_MS : NAV_DEFAULT_HELD_TIME_MS;
        }

        // Snaps the menu to its measured width with no animation, since it is not on screen
        private void SetMenuToTargetWidth(NativeMenu menu)
        {
            RecalculateTargetWidth(menu);
            menu.Width = targetMenuWidths[menu];
            currentMenuWidths[menu] = targetMenuWidths[menu];
            RecenterBannerText(menu);
        }

        // Menu construction
        private void BuildMenu()
        {
            // Sets Current's target before the SelectedSlots section loads. With startupLoadInProgress set it only assigns the target
            TrackSeatedVehicle(Game.LocalPlayerPed);
            // The startup value only. The live in-session (P) toggle overrides it for the rest of the session
            navigation.PreviewWanted = previewByDefault;
            ValidateFieldEntries();
            ValidateFlagEntryTables();
            pool = new ObjectPool();
            mainMenu = MakeMenu(LABEL_SCRIPT_NAME, ScriptVersion);
            savedHandlingsMenu = MakeMenu(LABEL_SAVED_HANDLINGS, LABEL_EXPORTS);
            previewMenu = MakeMenu(LABEL_PREVIEW, "");
            settingsMenu = MakeMenu(LABEL_SETTINGS, ScriptVersion);
            // Fixed for the life of the game process, derived from Game.FileVersion once
            weaponSlotCount = Game.FileVersion >= GAME_VERSION_1180_2 ? WEAPON_SLOT_COUNT_CURRENT : WEAPON_SLOT_COUNT_OLDER;
            turretSlotCount = Game.FileVersion >= GAME_VERSION_1180_2 ? TURRET_SLOT_COUNT_CURRENT : TURRET_SLOT_COUNT_OLDER;
            weaponModTypeSupported = Game.FileVersion >= GAME_VERSION_1103_2;
            // On older builds the offset table's fields land wrong, so raw reads/writes are withheld entirely
            // SpecialFlightHandlingFlags' own offset does not exist at all on an older struct layout, so its threshold is higher than the general one
            RawMemory.ConfigureBuild(Game.isEnhanced || Game.FileVersion >= GAME_VERSION_1180_2, Game.isEnhanced || Game.FileVersion >= GAME_VERSION_1493_0);
            if (!RawMemory.RawOffsetsSupported) logger.Log(Dbg.Level.Warn, Dbg.RawOffsetsUnsupported(Game.FileVersion.ToString(), GAME_VERSION_1180_2.ToString()));
            if (!RawMemory.SpecialFlightFlagsSupported) logger.Log(Dbg.Level.Warn, Dbg.SpecialFlightFlagsUnsupported(Game.FileVersion.ToString(), GAME_VERSION_1493_0.ToString()));
            string modelFlagOffsetFailure;
            int apiModelFlagOffset;
            int modelFlagOffset = RawMemory.ResolveModelFlagBaseOffset(out apiModelFlagOffset, out modelFlagOffsetFailure);
            if (modelFlagOffset > 0) logger.Log(Dbg.Level.Info, Dbg.ModelFlagOffsetResolved(modelFlagOffset, apiModelFlagOffset));
            else logger.Log(Dbg.Level.Warn, Dbg.ModelFlagOffsetUnresolved(modelFlagOffsetFailure));
            logger.Log(Dbg.Level.Info, Dbg.GearRatioPatchResolved(gearRatioPatchState.ToString(), gearRatioPatchState == GearRatioPatchState.Active ? INITIAL_DRIVE_GEARS_MAX_WITH_PATCH : INITIAL_DRIVE_GEARS_MAX));
            // Only the genuinely global, singleton menus with no per-editor-context equivalent at all
            // Every editor tree (Current's included, built by BuildEditorContext below) registers its own menus into allMenus itself
            allMenus = new[] { mainMenu, savedHandlingsMenu, settingsMenu };
            foreach (NativeMenu menu in allMenus) ConfigureMenu(menu, currentContext);
            // previewMenu is kept out of allMenus deliberately, since it reads a saved file, not a vehicle
            // It does not want the shared per-menu Shown hooks above (SyncFieldValues, width-animation snapshotting)
            // It also stays out of the IsEditMenuVisible and ResolveVisibleTreeMenu gates elsewhere
            pool.Add(previewMenu);
            ApplyPanelMenuDefaults(previewMenu);
            // Same transition-seed pair ConfigurePanelMenu gives every category submenu
            // Re-materializing previewMenu after backing out of a category then animates in from that category's settled width instead of snapping
            SubscribeShown(previewMenu, "previewMenu.Shown", () =>
            {
                currentMenuWidths[previewMenu] = lastVisiblePreviewWidth;
                previewMenu.Width = lastVisiblePreviewWidth;
                RecenterBannerText(previewMenu);
            });
            SubscribeClosed(previewMenu, "previewMenu.Closed", () => { lastVisiblePreviewWidth = previewMenu.Width; });
            // previewMenu is kept out of allMenus, so it is registered here instead of by ConfigureMenu
            RegisterMenuNode(previewMenu, MenuFamily.PreviewRoot, null, true);
            for (int i = 0; i < PreviewCategoryOrder.Length; i++)
            {
                NativeMenu catMenu = ConfigurePanelMenu(PreviewCategoryOrder[i], false, previewMenu, true);
                var catSubmenuItem = new StyledSubmenuItem(catMenu, previewMenu);
                catSubmenuItem.Title = PreviewCategoryOrder[i];
                previewCategoryData[PreviewCategoryOrder[i]].SubmenuItem = catSubmenuItem;
                CaptureOriginalItemColor(catSubmenuItem);
                // No-op for categories with no checkboxes. Keeps a flag category's highlighted row correctly contrasted as the cursor moves
                SubscribeSelectedIndexChanged(catMenu, "catMenu.SelectedIndexChanged", e => SyncPreviewFlagCheckboxColors(catMenu));
            }
            NativeMenu carPreviewMenu = previewCategoryData[ResolveCategoryDefinition(CATEGORY_CAR).LongName].Menu;
            // AddNestedPreviewCategory itself creates the previewCategoryData entry (via ConfigurePanelMenu)
            // The call must fully complete and be captured in a local *before* indexing previewCategoryData, or the indexer evaluates against a not-yet-created key
            NativeSubmenuItem advancedFlagsSubmenuItem = AddNestedPreviewCategory("AdvancedFlags", carPreviewMenu, false);
            previewCategoryData["AdvancedFlags"].SubmenuItem = advancedFlagsSubmenuItem;
            NativeSubmenuItem advancedDataSubmenuItem = AddNestedPreviewCategory("AdvancedData", carPreviewMenu, true);
            previewCategoryData["AdvancedData"].SubmenuItem = advancedDataSubmenuItem;
            NativeMenu specialFlightPreviewMenu = previewCategoryData[ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT).LongName].Menu;
            NativeSubmenuItem specialFlightFlagsSubmenuItem = AddNestedPreviewCategory("SpecialFlightFlags", specialFlightPreviewMenu, false);
            previewCategoryData["SpecialFlightFlags"].SubmenuItem = specialFlightFlagsSubmenuItem;

            // Weapon preview has its own 2-level structure, mirroring the editor's Weapon menu and weapon slot structure exactly
            // previewWeaponMenu (top-level scalars plus one submenu entry per slot) is entered from previewMenu, and each slot leads into its own pooled row-menu
            string weaponLongName = ResolveCategoryDefinition(CATEGORY_WEAPON).LongName;
            previewWeaponMenu = ConfigurePanelMenu(weaponLongName, false, previewMenu, true);
            var weaponPreviewSubmenuItem = new StyledSubmenuItem(previewWeaponMenu, previewMenu);
            weaponPreviewSubmenuItem.Title = weaponLongName;
            previewCategoryData[weaponLongName].SubmenuItem = weaponPreviewSubmenuItem;
            CaptureOriginalItemColor(weaponPreviewSubmenuItem);
            previewWeaponSlotMenus = new NativeMenu[weaponSlotCount];
            previewWeaponSlotSubmenuItems = new NativeSubmenuItem[weaponSlotCount];
            for (int slot = 0; slot < weaponSlotCount; slot++)
            {
                // A weapon-slot preview menu is a leaf with no further nesting of its own, so it records no descent
                NativeMenu slotMenu = ConfigurePanelMenu(string.Format(TITLE_WEAPON_SLOT_PREVIEW, (slot + 1)), false, previewWeaponMenu, false);
                previewWeaponSlotMenus[slot] = slotMenu;
                var slotSubmenuItem = new StyledSubmenuItem(slotMenu, previewWeaponMenu);
                slotSubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT, (slot + 1));
                previewWeaponSlotSubmenuItems[slot] = slotSubmenuItem;
            }
            // ShowPreviewDiff's tree is a passive mirror of the Preview blocks just above
            // It is kept out of allMenus for the same reason previewMenu is: It reads a captured baseline, not a vehicle
            // It wants none of the shared per-menu Shown hooks or visibility gates elsewhere
            diffMenu = MakeMenu(LABEL_DIFF, "");
            pool.Add(diffMenu);
            ApplyPanelMenuDefaults(diffMenu);
            diffMenu.Buttons.Visible = false;
            SubscribeShown(diffMenu, "diffMenu.Shown", () =>
            {
                currentMenuWidths[diffMenu] = lastVisibleDiffWidth;
                diffMenu.Width = lastVisibleDiffWidth;
                RecenterBannerText(diffMenu);
            });
            SubscribeClosed(diffMenu, "diffMenu.Closed", () => { lastVisibleDiffWidth = diffMenu.Width; });
            for (int i = 0; i < PreviewCategoryOrder.Length; i++)
            {
                NativeMenu diffCatMenu = ConfigurePanelMenu(PreviewCategoryOrder[i], true);
                var diffCatSubmenuItem = new StyledSubmenuItem(diffCatMenu, diffMenu);
                diffCatSubmenuItem.Title = PreviewCategoryOrder[i];
                previewDiffCategoryData[PreviewCategoryOrder[i]].SubmenuItem = diffCatSubmenuItem;
                CaptureOriginalItemColor(diffCatSubmenuItem);
                // No-op for categories with no checkboxes. Keeps a flag category's highlighted row correctly contrasted as the cursor moves
                SubscribeSelectedIndexChanged(diffCatMenu, "diffCatMenu.SelectedIndexChanged", e => SyncPreviewFlagCheckboxColors(diffCatMenu));
            }
            NativeMenu carDiffMenu = previewDiffCategoryData[ResolveCategoryDefinition(CATEGORY_CAR).LongName].Menu;
            NativeSubmenuItem diffAdvFlagsSubmenuItem = AddDiffNestedCategory("AdvancedFlags", carDiffMenu);
            previewDiffCategoryData["AdvancedFlags"].SubmenuItem = diffAdvFlagsSubmenuItem;
            NativeSubmenuItem diffAdvancedDataSubmenuItem = AddDiffNestedCategory("AdvancedData", carDiffMenu);
            previewDiffCategoryData["AdvancedData"].SubmenuItem = diffAdvancedDataSubmenuItem;
            NativeMenu specialFlightDiffMenu = previewDiffCategoryData[ResolveCategoryDefinition(CATEGORY_SPECIAL_FLIGHT).LongName].Menu;
            NativeSubmenuItem diffSpecialFlightFlagsSubmenuItem = AddDiffNestedCategory("SpecialFlightFlags", specialFlightDiffMenu);
            previewDiffCategoryData["SpecialFlightFlags"].SubmenuItem = diffSpecialFlightFlagsSubmenuItem;
            diffWeaponMenu = ConfigurePanelMenu(weaponLongName, true);
            var diffWeaponSubmenuItem = new StyledSubmenuItem(diffWeaponMenu, diffMenu);
            diffWeaponSubmenuItem.Title = weaponLongName;
            previewDiffCategoryData[weaponLongName].SubmenuItem = diffWeaponSubmenuItem;
            CaptureOriginalItemColor(diffWeaponSubmenuItem);
            diffWeaponSlotMenus = new NativeMenu[weaponSlotCount];
            diffWeaponSlotSubmenuItems = new NativeSubmenuItem[weaponSlotCount];
            for (int slot = 0; slot < weaponSlotCount; slot++)
            {
                NativeMenu diffSlotMenu = ConfigurePanelMenu(string.Format(TITLE_WEAPON_SLOT_DIFF, (slot + 1)), true);
                diffWeaponSlotMenus[slot] = diffSlotMenu;
                var diffSlotSubmenuItem = new StyledSubmenuItem(diffSlotMenu, diffWeaponMenu);
                diffSlotSubmenuItem.Title = string.Format(TITLE_WEAPON_SLOT, (slot + 1));
                diffWeaponSlotSubmenuItems[slot] = diffSlotSubmenuItem;
            }
            // Startup builds Current's tree at once, through the same steps the warm-up runs for each extra editor's tree after startup
            // Its finish runs its binding owners and raw item disable passes, and the startup passes below give its menus their layout
            AdvanceEditorBuild(currentContext, long.MaxValue);
            // mainMenu's own remaining top-level rows, which are genuinely global with no per-editor-context equivalent
            BuildMainMenuItems();
            BuildSettingsMenuItems();
            // Separators count toward Items.Count, which the API's own paginator divides by directly with no way to exclude specific items from it
            // Settings is the only menu with separators, so its counter is disabled outright rather than showing an inflated total
            settingsMenu.ItemCount = CountVisibility.Never;
            HidePreviewInstructionalButtons();
            ApplyMenuPosition();
            ApplyMenuTheme();
            foreach (NativeMenu menu in allMenus) SetMenuToTargetWidth(menu);
            startupMenusLaidOut = true;
            initialMenuCount = allMenus.Length;
            logger.Log(Dbg.Level.Info, Dbg.MenuBuilt(allMenus.Length));
        }

        // Banner. Every menu applies a layout change it missed while closed as it opens
        private NativeMenu MakeMenu(string title, string subtitle)
        {
            var rect = new ScaledRectangle(PointF.Empty, new SizeF(0, BANNER_HEIGHT));
            rect.Color = Color.FromArgb(255, menuBannerR, menuBannerG, menuBannerB);
            var menu = new FrameGatedMenu(title, subtitle, "", rect);
            // The first Shown handler of every menu, so the shown frame is recorded before any later handler runs
            SubscribeShown(menu, "MakeMenu.Shown.FrameGate", menu.RecordShownFrame);
            SubscribeOpening(menu, "MakeMenu.Opening", () => ApplyPendingMenuLayout(menu));
            return menu;
        }

        // The menu library draws a menu before it handles that menu's input, so Enter on a submenu row has already drawn the parent when the child opens,
        // and the child, later in the pool, draws in the same frame. A menu shown during this frame's pool pass waits until the next frame to draw,
        // so the two never show together. ProcessFrame is set once per tick by the first tick stage, so no native runs per menu
        private class FrameGatedMenu : NativeMenu
        {
            internal static int ProcessFrame;
            private int shownFrame = -1;

            public FrameGatedMenu(string title, string subtitle, string description, LemonUI.Elements.I2Dimensional banner) : base(title, subtitle, description, banner)
            {
            }

            public void RecordShownFrame() { shownFrame = ProcessFrame; }
            public void ReleaseGate() { shownFrame = -1; }

            public override void Process()
            {
                if (Visible && shownFrame == ProcessFrame) return;
                base.Process();
            }
        }

        // Shared property block for previewMenu, diffMenu, and every Preview/Diff category menu
        private void ApplyPanelMenuDefaults(NativeMenu menu)
        {
            menu.HeaderBehavior = HeaderBehavior.AlwaysShow;
            menu.Width = MENU_WIDTH;
            menu.MaxItems = menuMaxRows;
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            // Keyboard-driven panel, so never move the hardware cursor when shown, which would otherwise fire on every mirror-driven visibility change
            menu.ResetCursorWhenOpened = false;
            menu.RotateCamera = false;
            menu.DisableControls = false;
            // Defaults unfocused, and a panel only processes input once focus is deliberately handed to it
            menu.AcceptsInput = false;
        }

        // A Diff menu is passive. It never takes input focus, and its visible submenu is driven entirely by SyncDiffMirror
        // Each side keeps its own width-transition seed, so a category switch never seeds from the other panel's settled width
        // The banner always reads its top menu (Preview or Diff) and the subtitle names the category. Preview.cs renames the subtitle on refresh where it carries more
        // A Preview menu joins the Closed dispatcher as a panel that reopens reopenTarget on leaving, and its Shown applies Preview's focus
        private NativeMenu ConfigurePanelMenu(string category, bool isDiff, NativeMenu reopenTarget = null, bool hasChildren = false)
        {
            NativeMenu catMenu = MakeMenu(isDiff ? LABEL_DIFF : LABEL_PREVIEW, category);
            ApplyPanelMenuDefaults(catMenu);
            // No action becomes available at any Preview nesting level beyond what the top-level Preview panel already lacks
            // So every level stays hint-free rather than only the top level
            catMenu.Buttons.Visible = false;
            // Seeds the width transition like the live editor's ConfigureMenu does
            // Switching between differently-sized categories then animates instead of snapping to the width this menu last settled at
            // Offset is seeded too, not just Width: RepositionPreviewMenu's per-tick loop only touches a submenu while it is already Visible
            // Without it, the first tick a submenu becomes visible it draws once at whatever Offset it last held, before self-correcting
            SubscribeShown(catMenu, "ConfigurePanelMenu.Shown.WidthSeed", () =>
            {
                float seedWidth = isDiff ? lastVisibleDiffWidth : lastVisiblePreviewWidth;
                currentMenuWidths[catMenu] = seedWidth;
                catMenu.Width = seedWidth;
                catMenu.Offset = isDiff ? diffMenu.Offset : previewMenu.Offset;
                RecenterBannerText(catMenu);
            });
            SubscribeClosed(catMenu, "ConfigurePanelMenu.Closed", () =>
            {
                if (isDiff) lastVisibleDiffWidth = catMenu.Width;
                else lastVisiblePreviewWidth = catMenu.Width;
            });
            if (!isDiff)
            {
                SubscribeShown(catMenu, "ConfigurePanelMenu.Shown.PreviewFocus", () => FocusPreview(navigation.PreviewFocused));
                RegisterMenuNode(catMenu, MenuFamily.PreviewPanel, reopenTarget, hasChildren);
            }
            pool.Add(catMenu);
            PreviewCategoryData categoryData = new PreviewCategoryData { Menu = catMenu };
            categoryData.MissingPlaceholder = new NativeItem(category);
            categoryData.MissingPlaceholder.Enabled = false;
            categoryData.MissingPlaceholder.Description = isDiff ? PLACEHOLDER_DIFF_NOT_PRESENT : "";
            placeholderTints[categoryData.MissingPlaceholder] = isDiff ? DIFF_BASELINE_COLOR : DIFF_PREVIEW_COLOR;
            if (isDiff) previewDiffCategoryData[category] = categoryData;
            else previewCategoryData[category] = categoryData;
            return catMenu;
        }

        // No Shown or Closed focus wiring, matching the passive shape ConfigurePanelMenu gives every Diff menu
        private NativeSubmenuItem AddDiffNestedCategory(string category, NativeMenu parentDiffMenu)
        {
            NativeMenu categoryMenu = ConfigurePanelMenu(category, true);
            // No-op for AdvancedData, which has no checkboxes. Keeps AdvancedFlags' highlighted row correctly contrasted as the cursor moves
            SubscribeSelectedIndexChanged(categoryMenu, "AddDiffNestedCategory.SelectedIndexChanged", e => SyncPreviewFlagCheckboxColors(categoryMenu));
            var submenuItem = new StyledSubmenuItem(categoryMenu, parentDiffMenu);
            submenuItem.Title = category;
            return submenuItem;
        }

        // A Preview category nested one level under a parent category (Car's AdvancedFlags/AdvancedData, SpecialFlight's Flags), which it reopens on exit
        // hasChildren is True only for AdvancedData, whose entries are submenus of their own, so entering one is recorded as a descent
        // The flag categories are leaves and pass False
        private NativeSubmenuItem AddNestedPreviewCategory(string category, NativeMenu parentPreviewMenu, bool hasChildren)
        {
            NativeMenu categoryMenu = ConfigurePanelMenu(category, false, parentPreviewMenu, hasChildren);
            // No-op for categories with no checkboxes (AdvancedData). Keeps a flag category's highlighted row correctly contrasted as the cursor moves
            SubscribeSelectedIndexChanged(categoryMenu, "AddNestedPreviewCategory.SelectedIndexChanged", e => SyncPreviewFlagCheckboxColors(categoryMenu));
            var submenuItem = new StyledSubmenuItem(categoryMenu, parentPreviewMenu);
            submenuItem.Title = category;
            return submenuItem;
        }

        // The previewCategoryData/previewDiffCategoryData key of one AdvancedData entry menu
        private static string PreviewAdvancedDataEntryKey(int index, bool isDiff) { return string.Format(TITLE_ADVANCEDDATA_ENTRY_KEY, (index + 1)) + (isDiff ? SUFFIX_DIFF : SUFFIX_PREVIEW); }

        // Creates every AdvancedData entry menu up to and including index on one side, the first time a saved handling or baseline lists that many, then keeps them
        // Built after startup, so each takes the menu position, theme, and navigation hold time here that the other Preview menus already have
        // A Preview entry menu is a leaf: It hands input back to the AdvancedData category on exit, the same shape as a weapon slot preview menu
        private NativeSubmenuItem EnsurePreviewAdvancedDataEntryMenu(int index, bool isDiff)
        {
            List<NativeMenu> entryMenus = isDiff ? diffAdvancedDataEntryMenus : previewAdvancedDataEntryMenus;
            List<NativeSubmenuItem> entryItems = isDiff ? diffAdvancedDataEntrySubmenuItems : previewAdvancedDataEntrySubmenuItems;
            NativeMenu listMenu = (isDiff ? previewDiffCategoryData : previewCategoryData)["AdvancedData"].Menu;
            while (entryMenus.Count <= index)
            {
                int entryIndex = entryMenus.Count;
                NativeMenu entryMenu = ConfigurePanelMenu(PreviewAdvancedDataEntryKey(entryIndex, isDiff), isDiff, listMenu, false);
                // The lookup key only needs to be unique, and the subtitle names the entry
                entryMenu.Name = string.Format(TITLE_ENTRY, (entryIndex + 1));
                entryMenu.Alignment = menuAlignLeft ? GTA.UI.Alignment.Left : GTA.UI.Alignment.Right;
                Color color, textColor;
                ResolveMenuThemeColors(out color, out textColor);
                ApplyMenuThemeTo(entryMenu, color, textColor);
                entryMenu.HeldTime = navigationSpedUp ? NAV_FAST_HELD_TIME_MS : NAV_DEFAULT_HELD_TIME_MS;
                var entryItem = new StyledSubmenuItem(entryMenu, listMenu);
                entryItem.Title = string.Format(TITLE_ENTRY, (entryIndex + 1));
                CaptureOriginalItemColor(entryItem);
                entryMenus.Add(entryMenu);
                entryItems.Add(entryItem);
            }
            return entryItems[index];
        }

        // The API's RightBadge hardcodes 45x45 with no size control, so this draws its own ScaledTexture instead
        private class StyledSubmenuItem : NativeSubmenuItem
        {
            internal readonly ScaledTexture arrow = CreateArrow();

            public StyledSubmenuItem(NativeMenu menu, NativeMenu parent) : base(menu, parent, "")
            {
            }

            internal static ScaledTexture CreateArrow() { return new ScaledTexture(PointF.Empty, new SizeF(ARROW_SIZE, ARROW_SIZE), "commonmenu", "arrowright"); }

            // Shared with StyledChevronItem, so every submenu-style arrow sits and tints the same way
            internal static void PlaceArrow(ScaledTexture arrow, PointF pos, SizeF size)
            {
                arrow.Position = new PointF(pos.X + size.Width - ARROW_SIZE - ARROW_MARGIN_X, pos.Y + ARROW_MARGIN_Y);
            }

            // Tinted from UpdateColors, which runs on every recalculation and on every Enabled change, so a disabled row's arrow dims with its title
            internal static void TintArrow(ScaledTexture arrow, bool selected, bool enabled)
            {
                if (!enabled) arrow.Color = DIMMED_TEXT_COLOR;
                else arrow.Color = selected ? Color.Black : Color.White;
            }

            public override void Recalculate(PointF pos, SizeF size, bool selected)
            {
                base.Recalculate(pos, size, selected);
                PlaceArrow(arrow, pos, size);
            }

            public override void UpdateColors()
            {
                base.UpdateColors();
                TintArrow(arrow, lastSelected, Enabled);
            }

            public override void Draw()
            {
                base.Draw();
                arrow.Draw();
            }
        }

        // A plain item drawn with the submenu arrow, for a row whose Enter changes what the same menu lists instead of opening a child menu
        private class StyledChevronItem : NativeItem
        {
            private readonly ScaledTexture arrow = StyledSubmenuItem.CreateArrow();

            public StyledChevronItem(string title, string description) : base(title, description)
            {
            }

            public override void Recalculate(PointF pos, SizeF size, bool selected)
            {
                base.Recalculate(pos, size, selected);
                StyledSubmenuItem.PlaceArrow(arrow, pos, size);
            }

            public override void UpdateColors()
            {
                base.UpdateColors();
                StyledSubmenuItem.TintArrow(arrow, lastSelected, Enabled);
            }

            public override void Draw()
            {
                base.Draw();
                arrow.Draw();
            }
        }

        private NativeSubmenuItem AddStyledSubmenu(NativeMenu parent, NativeMenu child)
        {
            var item = new StyledSubmenuItem(child, parent);
            AddTreeRow(parent, item);
            return item;
        }

        private NativeSubmenuItem AddStyledSubmenu(NativeMenu parent, NativeMenu child, string title)
        {
            NativeSubmenuItem item = AddStyledSubmenu(parent, child);
            item.Title = title;
            return item;
        }

        private class StyledSeparator : NativeSeparatorItem
        {
            public StyledSeparator(string text, GTA.UI.Font font, float scale) : base(text)
            {
                TitleFont = font;
                title.Scale = scale;
            }
        }
    }
}
