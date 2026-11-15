using System;
using System.Collections.Generic;
using GTA;
using LemonUI.Menus;
using LemonUI.Scaleform;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The menus that host a hint bar, as flags so a row can list every host it shows in. Each menu is exactly one of them
        [Flags]
        private enum HintHosts { None = 0, MainMenu = 1, SavedHandlings = 2, Settings = 4, EditorTree = 8 }

        // The tree is the one the row acts on, and a menu no tree owns acts on the Active editor
        private struct HintScope
        {
            public NativeMenu Menu;
            public HintHosts Kind;
            public EditorContext Tree;
        }

        // A row's action fires only while its hint shows, so the one condition gates both the hint and the hotkey
        private class HotkeyDef
        {
            public readonly InstructionalButton Button;
            public readonly HintHosts Hosts;
            public readonly Func<HintScope, bool> Condition;
            public readonly Action<HintScope> Action;
            // Fires again while the control and the Action Modifier stay held, the first repeat after a longer delay than the rest
            public bool Held;
            public readonly bool SuppressesControl;
            // The game time a held row repeats next, 0 while it is not held
            public int NextRepeatTime;
            public HotkeyDef(InstructionalButton button, HintHosts hosts, Func<HintScope, bool> condition, Action<HintScope> action)
            {
                Button = button;
                Hosts = hosts;
                Condition = condition;
                Action = action;
                SuppressesControl = Array.IndexOf(DrivingControls, button.Control) < 0;
            }
        }

        // Controls that also steer, brake, or strafe. Their hotkeys stay unsuppressed, so driving keeps working while their hint shows
        private static readonly GTA.Control[] DrivingControls = { GTA.Control.VehicleBrake, GTA.Control.MoveLeftOnly, GTA.Control.MoveRightOnly };

        // The rows a host's Buttons show now are kept as a mask, so the poll and the suppression read exactly what was drawn
        private class HintMenuState
        {
            public readonly HintHosts Kind;
            public readonly EditorContext Owner;
            public int AppliedMask;
            public HintMenuState(HintHosts kind, EditorContext owner)
            {
                Kind = kind;
                Owner = owner;
            }
        }

        private readonly Dictionary<NativeMenu, HintMenuState> hintMenuStates = new Dictionary<NativeMenu, HintMenuState>();

        // Preview sits beside Saved Handlings, and both draw to the one native hint slate every frame, so Preview's own Buttons stay hidden
        private void HidePreviewInstructionalButtons() { previewMenu.Buttons.Visible = false; }

        // Each distinct hint button, shared by every table row that shows it. A menu's Buttons throws on a second equal button
        private static readonly InstructionalButton OverwriteHint = new InstructionalButton(HINT_OVERWRITE, GTA.Control.NextCamera);
        private static readonly InstructionalButton DuplicateHint = new InstructionalButton(HINT_DUPLICATE, GTA.Control.MoveRightOnly);
        private static readonly InstructionalButton RenameHint = new InstructionalButton(HINT_RENAME, GTA.Control.PushToTalk);
        // Duplicate's control, which inside Autosaves copies the autosave into Exports and on a focused Preview category row copies the whole category
        private static readonly InstructionalButton CopyHint = new InstructionalButton(HINT_COPY, GTA.Control.MoveRightOnly);
        private static readonly InstructionalButton DeleteHint = new InstructionalButton(HINT_DELETE, GTA.Control.VehicleDuck);
        private static readonly InstructionalButton ClearInputHint = new InstructionalButton(HINT_CLEAR_INPUT, GTA.Control.VehicleDuck);
        // Clear Input's control, which in a narrowed search list clears the typed query
        private static readonly InstructionalButton ClearSearchHint = new InstructionalButton(HINT_CLEAR_SEARCH, GTA.Control.VehicleDuck);
        private static readonly InstructionalButton DeleteVehicleHint = new InstructionalButton(HINT_DELETE_VEHICLE, GTA.Control.VehicleDuck);
        private static readonly InstructionalButton PreviewHint = new InstructionalButton(LABEL_PREVIEW, GTA.Control.FrontendPause);
        private static readonly InstructionalButton TeleportHint = new InstructionalButton(HINT_TELEPORT, GTA.Control.MpTextChatAll);
        private static readonly InstructionalButton ResetToDefaultHint = new InstructionalButton(HINT_RESET_TO_DEFAULT, GTA.Control.Reload);
        private static readonly InstructionalButton RespawnHint = new InstructionalButton(HINT_RESPAWN, GTA.Control.Reload);
        private static readonly InstructionalButton SaveHint = new InstructionalButton(LABEL_SAVE, GTA.Control.VehicleBrake);
        private static readonly InstructionalButton ExportsHint = new InstructionalButton(HINT_EXPORTS, GTA.Control.CinematicSlowMo);
        private static readonly InstructionalButton UndoHint = new InstructionalButton(HINT_UNDO, GTA.Control.HUDSpecial);
        private static readonly InstructionalButton RedoHint = new InstructionalButton(HINT_REDO, GTA.Control.MpTextChatTeam);
        private static readonly InstructionalButton UndoAllHint = new InstructionalButton(HINT_UNDO_ALL, GTA.Control.MoveLeftOnly);

        private HotkeyDef[] hotkeyTable;

        // Built in the constructor, since each row's condition and action are instance methods
        // Rows read left to right on the hint bar and are added to a menu in reverse, because the first button added draws rightmost
        // Rows on one control are in priority order: Only the first whose condition holds shows, so one press runs one action
        private void InitHotkeyTable()
        {
            HintHosts treeHost = HintHosts.EditorTree;
            HintHosts historyHosts = HintHosts.SavedHandlings | HintHosts.EditorTree;
            HintHosts anyHost = HintHosts.MainMenu | HintHosts.SavedHandlings | HintHosts.Settings | HintHosts.EditorTree;
            HintHosts anyHostButSavedHandlings = anyHost & ~HintHosts.SavedHandlings;
            hotkeyTable = new[]
            {
                BuildSavedHandlingHotkey(OverwriteHint, SavedHandlingCapabilities.Overwrite, key => ConfirmOverwriteHandling()),
                new HotkeyDef(CopyHint, HintHosts.SavedHandlings, scope => ResolveHighlightedPreviewCategoryRow() != null, scope => ApplySelectiveCopyCategory(ResolveHighlightedPreviewCategoryRow())),
                BuildSavedHandlingHotkey(DuplicateHint, SavedHandlingCapabilities.Duplicate, DuplicateHandling),
                BuildSavedHandlingHotkey(RenameHint, SavedHandlingCapabilities.Rename, RenameHandling),
                BuildSavedHandlingHotkey(CopyHint, SavedHandlingCapabilities.CopyToExports, CopyAutosaveToExports),
                BuildSavedHandlingHotkey(DeleteHint, SavedHandlingCapabilities.Delete, key => TriggerDeleteHandling()),
                new HotkeyDef(ClearInputHint, HintHosts.Settings, scope => IsClearableRowHighlighted(), scope => ClearHighlightedEntryPoint()),
                new HotkeyDef(ClearSearchHint, HintHosts.SavedHandlings, scope => IsSearchNarrowed(), scope => ClearVehicleSearch()),
                new HotkeyDef(DeleteHint, HintHosts.MainMenu, IsMountedEditorRowHighlighted, scope => TriggerUnmountAdditionalEditor()),
                new HotkeyDef(DeleteHint, treeHost, IsCategoryRowHighlighted, scope => TriggerSubDataExclusion(scope.Tree)),
                new HotkeyDef(DeleteHint, treeHost, IsWeaponSlotRowHighlighted, scope => TriggerDeleteWeaponSlot(scope.Tree)),
                new HotkeyDef(DeleteHint, treeHost, IsPhantomWeaponSlotRowHighlighted, scope => TriggerDeletePhantomWeaponSlot(scope.Tree)),
                new HotkeyDef(DeleteHint, treeHost, IsCarAdvancedDataRowHighlighted, scope => TriggerAdvancedDataBlockDelete(scope.Tree)),
                new HotkeyDef(DeleteHint, treeHost, IsPhantomAdvancedDataRowHighlighted, scope => TriggerAdvancedDataBlockDelete(scope.Tree)),
                new HotkeyDef(DeleteHint, treeHost, IsAdvancedDataEntryRowHighlighted, scope => TriggerAdvancedDataEntryDelete(scope.Tree)),
                new HotkeyDef(DeleteVehicleHint, treeHost, CanActOnEditedVehicle, scope => DeleteEditedVehicle(scope.Tree)),
                new HotkeyDef(PreviewHint, HintHosts.SavedHandlings, scope => GetHighlightedPreviewKey().IsValid, scope => TogglePreviewWanted()),
                new HotkeyDef(TeleportHint, treeHost, CanActOnEditedVehicle, scope => TeleportToVehicleDriverSeat(scope.Tree)),
                new HotkeyDef(ResetToDefaultHint, HintHosts.Settings, scope => IsResettableRowHighlighted(), scope => ResetHighlightedRowToDefault()),
                new HotkeyDef(RespawnHint, anyHost, scope => HasActiveVehicle(), scope => RespawnVehicle()),
                new HotkeyDef(SaveHint, anyHost, scope => HasActiveVehicle(), scope => SaveHandling()),
                new HotkeyDef(ExportsHint, anyHostButSavedHandlings, scope => true, scope => JumpTo(savedHandlingsMenu)),
                new HotkeyDef(UndoHint, historyHosts, scope => true, scope => StepUndoHistory(scope.Tree, false)) { Held = true },
                new HotkeyDef(RedoHint, historyHosts, scope => true, scope => StepUndoHistory(scope.Tree, true)) { Held = true },
                new HotkeyDef(UndoAllHint, historyHosts, scope => true, scope => UndoAllEdits(scope.Tree)),
            };
        }

        // A Saved Handlings row that shows while the saved handling under the highlight belongs to a source holding the capability
        // A search result belongs to a source that holds only Apply, so no file hint shows for it
        // None shows while Preview has focus, since the list's highlight is not what the player is looking at then
        private HotkeyDef BuildSavedHandlingHotkey(InstructionalButton button, SavedHandlingCapabilities capability, Action<SavedHandlingKey> action)
        {
            return new HotkeyDef(button, HintHosts.SavedHandlings, scope => HighlightedKeyAllows(capability), scope => action(GetHighlightedSavedHandlingKey()));
        }

        private bool HighlightedKeyAllows(SavedHandlingCapabilities capability)
        {
            if (navigation.PreviewFocused) return false;
            SavedHandlingKey key = GetHighlightedPreviewKey();
            return key.IsValid && key.Source.Has(capability);
        }

        private bool HasActiveVehicle() { return !Entity.IsNullOrNotExisting(activeContext.TargetVehicle); }
        private bool IsMountedEditorRowHighlighted(HintScope scope) { return ResolveAdditionalEditorForMainMenuItem(scope.Menu.SelectedItem) != null; }

        // A menu event applies the hints before this tick's player resolves, so the seat check can read the previous tick's player
        private bool CanActOnEditedVehicle(HintScope scope)
        {
            Vehicle target = scope.Tree.TargetVehicle;
            return !Entity.IsNullOrNotExisting(target) && !IsPlayerAlreadyInEditedVehicle(tickResolvedPlayer, target);
        }

        private bool IsCategoryRowHighlighted(HintScope scope)
        {
            return scope.Menu == scope.Tree.RootMenu && CategoryForSubmenu(scope.Menu.SelectedItem) != null;
        }

        private bool IsWeaponSlotRowHighlighted(HintScope scope)
        {
            return scope.Menu == GetLiveMenuForCategory(scope.Tree, CATEGORY_WEAPON) && GetHighlightedWeaponSlotIndex(scope.Menu, scope.Tree.Weapon.WeaponSlots) >= 0;
        }

        private bool IsPhantomWeaponSlotRowHighlighted(HintScope scope)
        {
            PhantomState phantom = scope.Tree.Phantom;
            return scope.Menu == GetPhantomMenuForCategory(scope.Tree, CATEGORY_WEAPON) && GetHighlightedPhantomWeaponSlotIndex(scope.Menu, phantom.PhantomWeaponSlots) >= 0;
        }

        // The Car menu's AdvancedData row while it is the pending entry, or the live entry on a build where the array can be written
        private bool IsCarAdvancedDataRowHighlighted(HintScope scope)
        {
            NativeItem selected = scope.Menu.SelectedItem;
            if (scope.Menu != GetLiveMenuForCategory(scope.Tree, CATEGORY_CAR) || selected == null) return false;
            AdvancedDataState advancedData = scope.Tree.AdvancedData;
            return selected == advancedData.Lists[(int)AdvancedDataMenuKind.Pending].OpenerItem || (RawMemory.RawOffsetsSupported && selected == advancedData.Lists[(int)AdvancedDataMenuKind.Live].OpenerItem);
        }

        // Only the real AdvancedData entry counts, not the placeholder that stands in before the block exists
        private bool IsPhantomAdvancedDataRowHighlighted(HintScope scope)
        {
            NativeItem selected = scope.Menu.SelectedItem;
            return scope.Menu == GetPhantomMenuForCategory(scope.Tree, CATEGORY_CAR) && selected != null && selected == scope.Tree.AdvancedData.Lists[(int)AdvancedDataMenuKind.Phantom].OpenerItem;
        }

        private bool IsAdvancedDataEntryRowHighlighted(HintScope scope)
        {
            AdvancedDataStore store;
            NativeMenu entryMenu;
            int entryIndex;
            if (!ResolveHighlightedAdvancedDataEntry(scope.Tree, out store, out entryMenu, out entryIndex) || entryMenu != scope.Menu) return false;
            return store.Kind != AdvancedDataMenuKind.Live || RawMemory.RawOffsetsSupported;
        }

        // The rows shown for a host, as a bit per table row. A control an earlier row claimed hides every later row on it
        private int ComputeHintMask(HintScope scope)
        {
            int mask = 0;
            for (int i = 0; i < hotkeyTable.Length; i++)
            {
                HotkeyDef row = hotkeyTable[i];
                if ((row.Hosts & scope.Kind) == 0 || IsControlShown(mask, row.Button.Control) || !row.Condition(scope)) continue;
                mask |= 1 << i;
            }
            return mask;
        }

        private bool IsControlShown(int mask, GTA.Control control)
        {
            for (int i = 0; i < hotkeyTable.Length; i++)
            {
                if ((mask & (1 << i)) != 0 && hotkeyTable[i].Button.Control == control) return true;
            }
            return false;
        }

        private HintScope ResolveHintScope(NativeMenu menu, HintMenuState state)
        {
            HintScope scope;
            scope.Menu = menu;
            scope.Kind = state.Kind;
            scope.Tree = state.Kind == HintHosts.EditorTree ? state.Owner : activeContext;
            return scope;
        }

        // Only the table's own buttons are removed, since the menu library's Select and Back entries must survive
        // Adding in reverse table order puts the first row leftmost, and rebuilding only on a change keeps the per-tick cost to one mask compare
        private void ApplyHints(NativeMenu menu)
        {
            HintMenuState state = hintMenuStates[menu];
            int mask = ComputeHintMask(ResolveHintScope(menu, state));
            if (mask == state.AppliedMask) return;
            for (int i = 0; i < hotkeyTable.Length; i++) menu.Buttons.Remove(hotkeyTable[i].Button);
            for (int i = hotkeyTable.Length - 1; i >= 0; i--)
            {
                if ((mask & (1 << i)) != 0) menu.Buttons.Add(hotkeyTable[i].Button);
            }
            state.AppliedMask = mask;
        }

        private HintHosts ResolveHintHostKind(NativeMenu menu)
        {
            if (menu == mainMenu) return HintHosts.MainMenu;
            if (menu == savedHandlingsMenu) return HintHosts.SavedHandlings;
            if (menu == settingsMenu) return HintHosts.Settings;
            return HintHosts.EditorTree;
        }

        // Shown and SelectedIndexChanged fire during the menu's own processing, before it draws its Buttons, so a highlight move swaps its hints in the same frame
        private void RegisterHintMenu(NativeMenu menu, EditorContext context)
        {
            hintMenuStates[menu] = new HintMenuState(ResolveHintHostKind(menu), context);
            SubscribeShown(menu, "RegisterHintMenu.Shown", () => ApplyHints(menu));
            SubscribeSelectedIndexChanged(menu, "RegisterHintMenu.SelectedIndexChanged", e => ApplyHints(menu));
        }

        // The first visible menu of allMenus, which is the one drawing the hint bar. Preview and Diff hide their own Buttons and sit outside allMenus
        private NativeMenu ResolveVisibleHintHost()
        {
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible) return allMenus[i];
            }
            return null;
        }

        // Runs after the seat tracking, deleted-target, and seat merge stages, so a vehicle appearing or dying this tick shows in the hints the poll reads
        private void SyncHostHints()
        {
            tickHintHost = ResolveVisibleHintHost();
            if (tickHintHost != null) ApplyHints(tickHintHost);
        }

        private void SuppressHotkeyControls()
        {
            // Up is bound to both the phone and menu navigation. The menu library reads the disabled control, so navigation keeps working and only the phone loses it
            Game.DisableControlThisFrame(GTA.Control.Phone);
            // Escape is FrontendPauseAlternate, and the game opens the pause menu on its release, not its press. The block holds from the disarming
            // press through one tick past the release edge, which covers either order of this tick and the pause menu's own input read
            if (navigation.EscapeBlock != EscapeBlockPhase.None)
            {
                Game.DisableControlThisFrame(GTA.Control.FrontendPauseAlternate);
                if (navigation.EscapeBlock == EscapeBlockPhase.Released) navigation.EscapeBlock = EscapeBlockPhase.None;
                else if (Game.IsControlJustReleased(GTA.Control.FrontendPauseAlternate)) navigation.EscapeBlock = EscapeBlockPhase.Released;
            }
            if (tickHintHost == null) return;
            int mask = hintMenuStates[tickHintHost].AppliedMask;
            for (int i = 0; i < hotkeyTable.Length; i++)
            {
                if ((mask & (1 << i)) != 0 && hotkeyTable[i].SuppressesControl) Game.DisableControlThisFrame(hotkeyTable[i].Button.Control);
            }
        }
    }
}
