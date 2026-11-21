using System.Collections.Generic;
using System.Xml.Linq;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The vehicle the player was last seated in when TrackSeatedVehicle finished its seat-change effects
        // Compared by Handle, since Ped.CurrentVehicle constructs a new wrapper every access
        private Vehicle lastKnownSeatedVehicle;
        // True until the constructor's own Load* calls finish. TrackSeatedVehicle then only sets Current's target and leaves lastKnownSeatedVehicle unstamped
        // The first tick after startup still sees the seat change and runs the live Diff capture, merge, and trailer check after every Load* call
        // A capture running earlier would claim a handling before the disk-persisted true original loads
        private bool startupLoadInProgress = true;

        private class FlagState
        {
            public NativeSubmenuItem DamageFlagsItem;
            public NativeMenu ModelFlagsMenu, HandlingFlagsMenu, DamageFlagsMenu;
            public List<FlagCheckboxBinding> ModelFlagBindings = new List<FlagCheckboxBinding>();
            public List<FlagCheckboxBinding> HandlingFlagBindings = new List<FlagCheckboxBinding>();
            public List<FlagCheckboxBinding> DamageFlagBindings = new List<FlagCheckboxBinding>();
            public List<FlagCheckboxBinding> AdvancedFlagBindings = new List<FlagCheckboxBinding>();
            public List<FlagCheckboxBinding> SpecialFlightFlagBindings = new List<FlagCheckboxBinding>();
        }

        // Named WeaponMenuState rather than WeaponSlotState to avoid colliding with the unrelated per-slot undo and redo snapshot struct of that name in WeaponSlots.cs
        private class WeaponMenuState
        {
            public WeaponSlotItems[] WeaponSlots;
            public uint[] CustomWeaponHashes;
            public NativeItem WeaponAddSlotItem;
            public NativeItem[] CurrentWeaponSlotItems;
            // Anchors where SyncWeaponSlotVisibility inserts the weapon slot rows
            public NativeDynamicItem<DisplayFloat> WeaponDamageScaledToVehHealthMultItem;
        }

        // phantomSyncActionsByMenu and phantomMenuSelectedIndex (StaticSubData.cs) stay single shared dictionaries keyed by menu instance
        // Every tree's phantom menus are distinct objects, so one shared dictionary partitions correctly across all trees with no risk of collision
        private class PhantomState
        {
            // The element this tree's phantom menus edit, set by each phantom category menu's Shown handler
            public XElement CurrentPhantomElement;
            public PhantomWeaponSlotItems[] PhantomWeaponSlots;
            public NativeItem AddPhantomWeaponSlotItem;
            public int CurrentPhantomWeaponSlotCount;
        }

        // One AdvancedData list of a tree: The real array's, the pending block's, or the phantom block's
        private class AdvancedDataMenus
        {
            public NativeMenu ListMenu;
            // The row in a Car menu that opens ListMenu
            public NativeSubmenuItem OpenerItem;
            public AdvancedDataItemSet[] Entries = new AdvancedDataItemSet[ADVANCEDDATA_COUNT_SANITY_CEILING];
            // Listed only while the store offers adding, and null until its builder makes it
            public NativeItem AddSlotItem;
            // How many entry rows ListMenu lists now, so a visibility sync diffs against it instead of removing and re-adding every row
            public int ListedCount;
        }

        // The 3 AdvancedData lists indexed by AdvancedDataMenuKind, and the occupants of the 2 Car menus' AdvancedData slots
        // The live Car menu's slot is shared by the real and pending lists, and the phantom Car menu has its own
        private class AdvancedDataState
        {
            public readonly AdvancedDataMenus[] Lists = { new AdvancedDataMenus(), new AdvancedDataMenus(), new AdvancedDataMenus() };
            public NativeItem CarPlaceholderItem;
            // Which of the real list's opener, the pending list's opener, and the placeholder sits in the live Car menu's AdvancedData slot now
            public NativeItem CarSlotOccupant;
            public NativeItem PhantomPlaceholderItem;
            // Which of the phantom list's opener and the placeholder sits in the phantom Car menu's AdvancedData slot now
            public NativeItem PhantomSlotOccupant;
        }

        // Failed is final: A tree whose build threw keeps its partial menus hidden and is never mounted
        private enum EditorBuildState { NotBuilt, Building, Built, Failed }

        // Startup builds Current's tree and the warm-up builds the extra editors' after it
        // No tree is torn down, so an unmounted extra editor is a context whose TargetVehicle is null
        private class EditorContext
        {
            // Position in editorContexts: 0 is Current, 1 to MAX_ADDITIONAL_EDITORS are the extra editors
            public readonly int Index;
            // Ends every fieldRegistry key this tree registers, so identically titled fields in the 4 trees never collide. Null for Current, whose keys stay bare
            public readonly string KeySuffix;
            // Current's target follows the seated vehicle and stays scoped through menu depth and leaving it, until another vehicle is entered or it dies
            // An extra editor's target is the vehicle mounted into it, null while unmounted
            public Vehicle TargetVehicle;
            public NativeMenu RootMenu;
            public NativeSubmenuItem MainMenuItem;

            public NativeDynamicItem<string> AiHandlingItem;

            public FlagState Flags = new FlagState();
            public WeaponMenuState Weapon = new WeaponMenuState();

            // One row per category definition, in definition order, which owns that category's live and export-only menus. Null until the build finishes its rows
            public SubHandlingCategory[] Categories;
            // One tracked slot per sub-handling category preserves canonical order during incremental synchronization
            // Sized and populated from Categories at the end of BuildEditorContext's own table construction, never a hand-maintained literal
            public NativeItem[] CurrentSubDataSlotItems;
            // The base rows shown only while the vehicle has rocket boost, and one tracked occupant per row, same shape as CurrentSubDataSlotItems
            public List<NativeItem> RocketBoostItems = new List<NativeItem>();
            public NativeItem[] CurrentRocketBoostSlotItems;

            public PhantomState Phantom = new PhantomState();
            public AdvancedDataState AdvancedData = new AdvancedDataState();

            // Every menu belonging to this tree, live and phantom, computed once at the end of construction, and a menu created later appends to it
            // The live menus come first, in the same order in every tree, which is what lets a menu in one tree be matched to the same position in another
            public NativeMenu[] AllOwnMenus;
            // The live menus only (the first entries of AllOwnMenus), for the edit-menu visibility check
            public NativeMenu[] OwnLiveMenus;
            // AllOwnMenus.Length when construction ended: A menu at or past this position was created at runtime, in the order it was first shown in this tree
            public int StartupMenuCount;

            // What the tick stages resolved for this tree this tick. TickVehicle is null for an unmounted tree, so the tree's later stages draw, sync, and reassert nothing
            public Vehicle TickVehicle;
            public HandlingData TickHandling;
            // Vehicle handle TickHandling was cached for
            // Vehicle.HandlingData reads the vehicle's own handling pointer on every read, so the cache refreshes only when the handle changes
            public int HandlingCacheHandle;
            public HandlingData HandlingCache;
            // The vehicles the field sync and the drift reassertion last ran against, so each resets when the tree's vehicle changes
            public Vehicle LastSyncedVehicle;
            public Vehicle LastReassertedVehicle;
            // The collider state last logged for LastReassertedVehicle while its centers disagree, so the reassert logs once per change
            // Unknown while they agree, so the next mismatch logs its state fresh
            public ColliderState LastColliderState;
            // The menu of this tree that is visible, set by the MenuState stage and null while none is
            public NativeMenu VisibleMenuThisTick;

            public EditorBuildState BuildState;
            // The build in progress while BuildState is Building. Each MoveNext runs one step: A menu, a row, or one part of the tree's finish
            public IEnumerator<bool> BuildSteps;
            // How many steps the build has run, for the ERROR logged when it fails
            public int BuildStepCount;
            // rawOffsetItems' and specialFlightFlagItems' counts when this tree's build started. Its finish disables only the items past them
            // The ranges stay contiguous, since only one tree is ever part-built
            public int RawOffsetItemsStart;
            public int SpecialFlightFlagItemsStart;

            public EditorContext(int index)
            {
                Index = index;
                KeySuffix = index == 0 ? null : "[AdditionalEditor" + index + "]";
            }
        }

        // The array exists before the constructor body runs, so tick stage closures built there capture real contexts that each tree's build fills later
        private readonly EditorContext[] editorContexts = CreateEditorContexts();
        private static EditorContext[] CreateEditorContexts()
        {
            EditorContext[] contexts = new EditorContext[MAX_ADDITIONAL_EDITORS + 1];
            for (int i = 0; i < contexts.Length; i++) contexts[i] = new EditorContext(i);
            return contexts;
        }

        private EditorContext currentContext;
        // The editor entered last. Respawn, Save, Exports, and the other main menu actions act on its vehicle, and its main menu row turns green
        private EditorContext activeContext;
    }
}
