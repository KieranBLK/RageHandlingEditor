using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GTA;
using GTA.UI;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // How many menus BuildMenu registered: The global menus and Current's tree
        // An extra editor's tree joins allMenus later, in an order set by play, so its menus are addressed by their position in that tree instead
        // A menu created at runtime has no stable address of its own, so an AdvancedData entry is addressed through its list
        private int initialMenuCount;
        // Where a saved menu sits. Editor 0 means a menu BuildMenu registered, by its allMenus index
        // An extra editor's index means a menu of its tree, by its position in that tree's AllOwnMenus
        // That position is the same in every tree, since every tree is built by the same steps in the same order
        // EntryNumber is an AdvancedData entry's 1-based number inside the addressed list, or 0 for the list or any other menu
        private struct MenuAddress { public int Editor; public int Position; public int EntryNumber; }
        // The menu and row a previous load left open, read from its file. Position is -1 while nothing is pending, and the first tick consumes it
        private MenuAddress pendingMenuRestoreMenu = new MenuAddress { Editor = 0, Position = -1 };
        private int pendingMenuRestoreRow = -1;
        // The highlighted row of each parent above that menu, and the search text when Saved Handlings was listing results
        private readonly List<KeyValuePair<MenuAddress, int>> pendingMenuRestoreParentRows = new List<KeyValuePair<MenuAddress, int>>();
        private string pendingMenuRestoreSearch = "";
        private string pendingMenuRestoreSource = "";
        private PreviewPosition pendingMenuRestorePreview;
        // Menus whose position or row count changed while they were closed, each with True when it is a Preview or Diff menu
        // Each applies when it next opens, since every layout change runs a full library recalculation, and doing it for every closed menu at once stalls the game
        private readonly Dictionary<NativeMenu, bool> pendingLayoutMenus = new Dictionary<NativeMenu, bool>();
        // True while one of the context's live editor menus is visible
        // That covers Edit Handling, its sub-handling and flag submenus, AdvancedData, a weapon slot, and an AdvancedData entry
        // The pending AdvancedData list is skipped: It edits an export-only element, so the no-vehicle notification this gates does not apply to it
        private bool IsEditMenuVisible(EditorContext context)
        {
            for (int i = 0; i < context.OwnLiveMenus.Length; i++)
            {
                if (context.OwnLiveMenus[i] != context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Pending].ListMenu && context.OwnLiveMenus[i].Visible) return true;
            }
            return IsAnyAdvancedDataEntryMenuVisible(context.AdvancedData.Lists[(int)AdvancedDataMenuKind.Live].Entries);
        }

        private static bool IsAnyAdvancedDataEntryMenuVisible(AdvancedDataItemSet[] slots)
        {
            for (int i = 0; i < slots.Length; i++) if (slots[i].Menu != null && slots[i].Menu.Visible) return true;
            return false;
        }

        // The first visible menu of the context's tree, live or phantom, or null when none is, so a phantom category's own creation, editing, or exclusion
        // is treated exactly as real an edit as a live field's. A tree not built yet has no menu on screen, and its menu list does not exist yet
        private static NativeMenu ResolveVisibleTreeMenu(EditorContext context)
        {
            if (context.BuildState != EditorBuildState.Built) return null;
            NativeMenu[] ownMenus = context.AllOwnMenus;
            for (int i = 0; i < ownMenus.Length; i++) if (ownMenus[i].Visible) return ownMenus[i];
            return null;
        }

        // Checks every tree, so a key-event input gate and the hotkey-control suppression work whichever of the up to 4 trees is on screen
        private bool IsMenuOrSelectedEquivalentVisible(Func<EditorContext, NativeMenu> selector)
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                NativeMenu menu = selector(editorContexts[i]);
                if (menu != null && menu.Visible) return true;
            }
            return false;
        }

        private bool IsPreviewCategoryMenuVisible()
        {
            // Weapon preview's own menu and its per-slot submenus are registered into previewCategoryData too (via ConfigurePanelMenu),
            // so this single iteration already covers them alongside every other Preview category
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) if (data.Value.Menu.Visible) return true;
            return false;
        }

        private NativeMenu GetVisiblePreviewMenu()
        {
            if (previewMenu.Visible) return previewMenu;
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) if (data.Value.Menu.Visible) return data.Value.Menu;
            return null;
        }

        // Preview reopens only while it is wanted, since the Preview toggle ends it on purpose
        // It also needs Saved Handlings open, since Preview has no reason to reopen once the player has left Saved Handlings entirely
        private bool ShouldPreviewStayOpen()
        {
            return savedHandlingsMenu.Visible && navigation.PreviewWanted;
        }

        // Records the visible menu and its row, each parent's row so backing out lands where the player was, the browsed list's source, and the search text
        // A menu with no stable address climbs to its first parent that has one. The menu line is left out while no menu is visible
        // The Preview line is always written, since Preview's on or off state outlives the menus
        private void WriteMenuRestore(StringBuilder sb)
        {
            NativeMenu visibleMenu = null;
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible)
                {
                    visibleMenu = allMenus[i];
                    break;
                }
            }
            MenuAddress address = default(MenuAddress);
            while (visibleMenu != null && !ResolveMenuAddress(visibleMenu, out address)) visibleMenu = visibleMenu.Parent;
            if (visibleMenu != null)
            {
                sb.Append(initialMenuCount).Append('|').Append(FormatMenuAddress(address)).Append(':').Append(visibleMenu.SelectedIndex);
                for (NativeMenu parent = visibleMenu.Parent; parent != null; parent = parent.Parent)
                {
                    MenuAddress parentAddress;
                    if (!ResolveMenuAddress(parent, out parentAddress)) break;
                    sb.Append(',').Append(FormatMenuAddress(parentAddress)).Append(':').Append(parent.SelectedIndex);
                }
                sb.Append('|').Append(browsedSource.Id).Append('|').Append(visibleMenu == savedHandlingsMenu && browsedSource == searchSource ? searchQueryText : "").AppendLine();
            }
            sb.Append(PREVIEW_RESTORE_LINE_TAG).Append('|').Append(navigation.PreviewWanted ? 1 : 0);
            PreviewPosition position = CapturePreviewPosition();
            if (position != null)
            {
                sb.Append('|').Append(position.Focused ? 1 : 0).Append('|').Append(position.TopRow).Append('|');
                for (int i = 0; i < position.Path.Count; i++) sb.Append(i > 0 ? "," : "").Append(position.Path[i].Key).Append(':').Append(position.Path[i].Value);
                sb.Append('|').Append(position.Source.Id).Append('|').Append(position.Name);
            }
            sb.AppendLine();
            sb.Append(WIDTH_RESTORE_LINE_TAG).Append('|').Append(FormatRestoredWidth(visibleMenu)).Append('|').Append(FormatRestoredWidth(GetVisiblePreviewMenu())).Append('|').Append(FormatRestoredWidth(diffBaselineActive ? lastShownDiffMenu : null)).AppendLine();
        }

        private static string FormatRestoredWidth(NativeMenu menu) { return menu != null ? menu.Width.ToString("R", CultureInfo.InvariantCulture) : ""; }

        // The widths become the seeds each menu's Shown starts from, so the restore's menus appear at their old width with nothing left to animate
        private bool ConsumeWidthRestoreLine(string raw)
        {
            string[] parts = raw.Split('|');
            if (parts.Length != 4) return false;
            lastVisibleEditorWidth = ParseRestoredWidth(parts[1], lastVisibleEditorWidth);
            lastVisiblePreviewWidth = ParseRestoredWidth(parts[2], lastVisiblePreviewWidth);
            lastVisibleDiffWidth = ParseRestoredWidth(parts[3], lastVisibleDiffWidth);
            return true;
        }

        // An empty part is a menu that was not on screen, so its seed stays
        private static float ParseRestoredWidth(string text, float fallback)
        {
            float width;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out width) && width > 0.0f ? width : fallback;
        }

        // Keeps the saved menu address and row for the first tick when the menu count still matches this build
        private bool ConsumeMenuRestoreLine(string raw)
        {
            if (raw.StartsWith(PREVIEW_RESTORE_LINE_TAG + "|", StringComparison.Ordinal)) return ConsumePreviewRestoreLine(raw);
            if (raw.StartsWith(WIDTH_RESTORE_LINE_TAG + "|", StringComparison.Ordinal)) return ConsumeWidthRestoreLine(raw);
            string[] parts = raw.Split(new[] { '|' }, 4);
            int savedCount;
            if (parts.Length != 4 || !int.TryParse(parts[0], out savedCount)) return false;
            if (savedCount != initialMenuCount)
            {
                logger.Log(Dbg.Level.Info, Dbg.MenuRestoreSkipped(savedCount, initialMenuCount));
                return false;
            }
            bool hasMenu = false;
            MenuAddress menu = default(MenuAddress);
            int row = -1;
            var parentRows = new List<KeyValuePair<MenuAddress, int>>();
            foreach (string pair in parts[1].Split(','))
            {
                string[] halves = pair.Split(':');
                MenuAddress address;
                int menuRow;
                if (halves.Length != 2 || !ParseMenuAddress(halves[0], out address) || !int.TryParse(halves[1], out menuRow)) return false;
                if (!hasMenu)
                {
                    menu = address;
                    row = menuRow;
                    hasMenu = true;
                }
                else parentRows.Add(new KeyValuePair<MenuAddress, int>(address, menuRow));
            }
            if (!hasMenu) return false;
            pendingMenuRestoreMenu = menu;
            pendingMenuRestoreRow = row;
            pendingMenuRestoreParentRows.Clear();
            pendingMenuRestoreParentRows.AddRange(parentRows);
            pendingMenuRestoreSource = parts[2];
            pendingMenuRestoreSearch = parts[3];
            return true;
        }

        // A menu's stable address, or False for a menu with none: A runtime menu other than an AdvancedData entry, or a menu of a tree whose build has not finished
        private bool ResolveMenuAddress(NativeMenu menu, out MenuAddress address)
        {
            EditorContext entryOwner;
            AdvancedDataMenuKind entryKind;
            int entryIndex;
            if (FindAdvancedDataListOf(menu, out entryOwner, out entryKind, out entryIndex) && entryIndex >= 0)
            {
                if (!ResolveMenuAddress(entryOwner.AdvancedData.Lists[(int)entryKind].ListMenu, out address)) return false;
                address.EntryNumber = entryIndex + 1;
                return true;
            }
            int index = Array.IndexOf(allMenus, menu);
            if (index >= 0 && index < initialMenuCount)
            {
                address = new MenuAddress { Editor = 0, Position = index };
                return true;
            }
            for (int i = 1; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (context.BuildState != EditorBuildState.Built) continue;
                int position = Array.IndexOf(context.AllOwnMenus, menu);
                if (position < 0) continue;
                address = new MenuAddress { Editor = context.Index, Position = position };
                return position < context.StartupMenuCount;
            }
            address = default(MenuAddress);
            return false;
        }

        // An extra editor's address is written E, its index, a period, then the position, and a menu BuildMenu registered is its bare allMenus index
        // An AdvancedData entry follows its list's address with an at sign and the entry number
        private static string FormatMenuAddress(MenuAddress address)
        {
            string listText = address.Editor == 0 ? address.Position.ToString(CultureInfo.InvariantCulture) : "E" + address.Editor.ToString(CultureInfo.InvariantCulture) + "." + address.Position.ToString(CultureInfo.InvariantCulture);
            return address.EntryNumber > 0 ? listText + "@" + address.EntryNumber.ToString(CultureInfo.InvariantCulture) : listText;
        }

        // A tree position is checked against Current's StartupMenuCount, since the extra trees may not be built yet when the file loads
        private bool ParseMenuAddress(string text, out MenuAddress address)
        {
            address = default(MenuAddress);
            int editor = 0, position, entryNumber = 0;
            int entrySeparator = text.IndexOf('@');
            if (entrySeparator >= 0)
            {
                if (!int.TryParse(text.Substring(entrySeparator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out entryNumber) || entryNumber < 1 || entryNumber > ADVANCEDDATA_COUNT_SANITY_CEILING) return false;
                text = text.Substring(0, entrySeparator);
            }
            string positionText = text;
            if (text.StartsWith("E", StringComparison.Ordinal))
            {
                int separator = text.IndexOf('.');
                if (separator < 2 || !int.TryParse(text.Substring(1, separator - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out editor) || editor < 1 || editor >= editorContexts.Length) return false;
                positionText = text.Substring(separator + 1);
            }
            if (!int.TryParse(positionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out position) || position < 0 || position >= (editor == 0 ? initialMenuCount : currentContext.StartupMenuCount)) return false;
            address = new MenuAddress { Editor = editor, Position = position, EntryNumber = entryNumber };
            return true;
        }

        // The AdvancedData list a menu belongs to, with entryIndex -1 when the menu is the list itself, or False for any other menu
        // A tree not built yet holds no list menus to match
        private bool FindAdvancedDataListOf(NativeMenu menu, out EditorContext owner, out AdvancedDataMenuKind kind, out int entryIndex)
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (context.BuildState != EditorBuildState.Built) continue;
                for (int k = 0; k < context.AdvancedData.Lists.Length; k++)
                {
                    AdvancedDataMenus lists = context.AdvancedData.Lists[k];
                    int found = -2;
                    if (lists.ListMenu == menu) found = -1;
                    else
                    {
                        for (int j = 0; j < lists.Entries.Length && found == -2; j++) if (lists.Entries[j] != null && lists.Entries[j].Menu == menu) found = j;
                    }
                    if (found == -2) continue;
                    owner = context;
                    kind = (AdvancedDataMenuKind)k;
                    entryIndex = found;
                    return true;
                }
            }
            owner = null;
            kind = AdvancedDataMenuKind.Live;
            entryIndex = -1;
            return false;
        }

        // The entry menus are created by the list's sync, so the list is synced first, after PrimePhantomMenusAbove set a phantom list's element
        // An entry the list no longer holds, since the vehicle changed between loads, leaves the list as the menu to open
        private NativeMenu ResolveRestoredAdvancedDataEntry(NativeMenu listMenu, int entryNumber)
        {
            EditorContext owner;
            AdvancedDataMenuKind kind;
            int entryIndex;
            if (entryNumber < 1 || !FindAdvancedDataListOf(listMenu, out owner, out kind, out entryIndex) || entryIndex != -1) return listMenu;
            SyncAdvancedDataVisibility(owner, kind);
            AdvancedDataMenus lists = owner.AdvancedData.Lists[(int)kind];
            if (entryNumber > lists.ListedCount) return listMenu;
            listMenu.SelectedIndex = entryNumber - 1;
            return lists.Entries[entryNumber - 1].Menu;
        }

        // The menu an address names, or null for an extra editor that is not mounted, which is then not reachable from the main menu either
        // An entry address names its list here, and the restore resolves the entry once the list is synced
        private NativeMenu ResolveAddressedMenu(MenuAddress address)
        {
            if (address.Editor == 0) return address.Position < allMenus.Length ? allMenus[address.Position] : null;
            EditorContext context = editorContexts[address.Editor];
            // Mounting builds a tree first, so a mounted tree is always built
            if (!IsAdditionalEditorMounted(context) || address.Position >= context.AllOwnMenus.Length) return null;
            return context.AllOwnMenus[address.Position];
        }

        // Preview's on or off state applies at once, since nothing else sets it after BuildMenu. The position waits for the Saved Handlings reopen
        private bool ConsumePreviewRestoreLine(string raw)
        {
            string[] parts = raw.Split(new[] { '|' }, 7);
            if ((parts.Length != 2 && parts.Length != 7) || (parts[1] != "0" && parts[1] != "1")) return false;
            navigation.PreviewWanted = parts[1] == "1";
            if (parts.Length == 2) return true;
            PreviewPosition position = ParsePreviewPosition(parts);
            if (position == null) return false;
            pendingMenuRestorePreview = position;
            return true;
        }

        private PreviewPosition ParsePreviewPosition(string[] parts)
        {
            int topRow;
            SavedHandlingSource source = ResolveSavedHandlingSourceById(parts[5]);
            if ((parts[2] != "0" && parts[2] != "1") || !int.TryParse(parts[3], out topRow) || source == null || parts[6].Length == 0) return null;
            var path = new List<KeyValuePair<string, int>>();
            if (parts[4].Length > 0)
            {
                foreach (string pair in parts[4].Split(','))
                {
                    string[] halves = pair.Split(':');
                    int row;
                    if (halves.Length != 2 || halves[0].Length == 0 || !int.TryParse(halves[1], out row)) return null;
                    path.Add(new KeyValuePair<string, int>(halves[0], row));
                }
            }
            return new PreviewPosition { Source = source, Name = parts[6], Focused = parts[2] == "1", TopRow = topRow, Path = path };
        }

        private void ClearPendingMenuRestore()
        {
            pendingMenuRestoreMenu = new MenuAddress { Editor = 0, Position = -1 };
            pendingMenuRestoreRow = -1;
            pendingMenuRestoreParentRows.Clear();
            pendingMenuRestoreSearch = "";
            pendingMenuRestoreSource = "";
            pendingMenuRestorePreview = null;
        }

        // OpenFromEntryPoint lands on the nearest parent still reachable when the vehicle no longer supports the saved menu
        // Item lists are built first, since no menu has been open to sync them yet
        // Preview's position applies after the list row assignment, since that assignment is the last thing that refreshes Preview
        // An extra editor no longer mounted opens the main menu, the parent an unreachable tree menu lands on anyway
        private void RestoreMenuAfterReload()
        {
            MenuAddress address = pendingMenuRestoreMenu;
            int row = pendingMenuRestoreRow;
            string searchText = pendingMenuRestoreSearch;
            string sourceId = pendingMenuRestoreSource;
            PreviewPosition previewPosition = pendingMenuRestorePreview;
            var parentRows = new List<KeyValuePair<MenuAddress, int>>(pendingMenuRestoreParentRows);
            ClearPendingMenuRestore();
            if (address.Position < 0) return;
            SyncFieldValues();
            SyncAllMountedEditors();
            NativeMenu saved = ResolveAddressedMenu(address);
            if (saved == null)
            {
                if (address.Editor == 0) return;
                saved = mainMenu;
                row = -1;
            }
            if (saved == savedHandlingsMenu)
            {
                // A search turns Preview on, so the saved state is put back after it
                bool previewWanted = navigation.PreviewWanted;
                SavedHandlingSource savedSource = ResolveSavedHandlingSourceById(sourceId);
                if (savedSource == searchSource) RestoreVehicleSearch(searchText);
                else if (savedSource != null) browsedSource = savedSource;
                navigation.PreviewWanted = previewWanted;
            }
            foreach (KeyValuePair<MenuAddress, int> parentRow in parentRows)
            {
                NativeMenu parent = ResolveAddressedMenu(parentRow.Key);
                if (parent != null && parent.Items.Count > 0) parent.SelectedIndex = Math.Min(parentRow.Value, parent.Items.Count - 1);
            }
            PrimePhantomMenusAbove(saved);
            saved = ResolveRestoredAdvancedDataEntry(saved, address.EntryNumber);
            navigation.RememberedMenu = saved;
            OpenFromEntryPoint();
            if (!saved.Visible || row < 0 || saved.Items.Count == 0) return;
            saved.SelectedIndex = Math.Min(row, saved.Items.Count - 1);
            logger.Log(Dbg.Level.Info, Dbg.MenuRestoreApplied(DescribeMenu(saved), saved.SelectedIndex));
            if (saved != savedHandlingsMenu) return;
            ApplyPreviewPosition(previewPosition);
            // Ahead of the width stage, so a restored Diff category takes its seed before the top-level Diff menu steps away from it
            SyncDiffMirror(GetVisiblePreviewMenu());
        }

        // A phantom category menu sets its tree's phantom element and lists its AdvancedData and weapon slot rows only when shown
        // The reopen needs those rows listed to reach a menu below, so each active phantom category menu above the saved menu syncs the way its Shown handler would
        private void PrimePhantomMenusAbove(NativeMenu saved)
        {
            NativeMenu menu = saved;
            for (int depth = 0; depth < MENU_REACHABILITY_MAX_DEPTH && menu != null; depth++)
            {
                for (int i = 0; i < editorContexts.Length; i++)
                {
                    EditorContext context = editorContexts[i];
                    if (context.Categories == null || Entity.IsNullOrNotExisting(context.TargetVehicle)) continue;
                    foreach (SubHandlingCategory row in context.Categories)
                    {
                        XElement element;
                        if (row == null || row.PhantomMenu != menu || !ResolvePhantomCategory(ResolveHandlingKey(context.TargetVehicle), row.Definition.Name, out element)) continue;
                        context.Phantom.CurrentPhantomElement = element;
                        SyncPhantomFieldValues(menu);
                    }
                }
                menu = menu.Parent;
            }
        }

        // A menu counts as reachable when every parent up to the main menu still lists an enabled submenu entry for it
        // A category the vehicle lacks, a hidden weapon slot, a phantom placeholder, or an unmounted AdditionalEditor all fail this
        // Saved Handlings and Settings always hang off the main menu, and Saved Handlings' own Parent is blank for one tick after a hotkey visit
        private bool IsMenuReachableFromMainMenu(NativeMenu menu)
        {
            NativeMenu child = menu;
            for (int depth = 0; depth < MENU_REACHABILITY_MAX_DEPTH; depth++)
            {
                if (child == mainMenu || child == savedHandlingsMenu || child == settingsMenu) return true;
                NativeMenu parent = child.Parent;
                if (parent == null || !HasEnabledSubmenuEntry(parent, child)) return false;
                child = parent;
            }
            return false;
        }

        // Ignores Enabled, since Preview rows are disabled while Preview is unfocused
        private static int FindSubmenuEntryIndex(NativeMenu parent, NativeMenu child)
        {
            for (int i = 0; i < parent.Items.Count; i++)
            {
                NativeSubmenuItem submenuItem = parent.Items[i] as NativeSubmenuItem;
                if (submenuItem != null && submenuItem.Menu == child) return i;
            }
            return -1;
        }

        private static bool HasEnabledSubmenuEntry(NativeMenu parent, NativeMenu child)
        {
            int entryIndex = FindSubmenuEntryIndex(parent, child);
            return entryIndex >= 0 && parent.Items[entryIndex].Enabled;
        }

        // Banner plus name (the subtitle text), since several menus share a banner title (every editor submenu reads "Edit Handling")
        private static string DescribeMenu(NativeMenu menu)
        {
            return menu.BannerText.Text + " (" + menu.Name + ")";
        }

        private void SetNavHeldTime(int heldTime, bool spedUp)
        {
            foreach (NativeMenu menu in allMenus) menu.HeldTime = heldTime;
            previewMenu.HeldTime = heldTime;
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) data.Value.Menu.HeldTime = heldTime;
            navigationSpedUp = spedUp;
        }

        private void ApplyFastNavSpeed() { SetNavHeldTime(NAV_FAST_HELD_TIME_MS, true); }
        private void ResetNavSpeed() { SetNavHeldTime(NAV_DEFAULT_HELD_TIME_MS, false); }

        // The menu library's MaxItems setter rebuilds the visible rows but never positions them, so an open menu draws its new rows in the wrong place
        // until something recalculates it. Recalculate also resets the banner title to the library's fixed offset, so it is recentered after
        private void ApplyMenuMaxItems(NativeMenu menu, int maxItems)
        {
            if (menu.MaxItems == maxItems) return;
            FitMenuScrollWindow(menu, maxItems);
            menu.MaxItems = maxItems;
            menu.Recalculate();
            RecenterBannerText(menu);
        }

        // The INI load and reset paths also reach this before BuildMenu runs in the constructor, hence the null guard
        private void ApplyMenuPosition()
        {
            if (allMenus == null) return;
            ApplyOrQueueAllMenuLayouts();
        }

        // Applies the current position and row count to an open menu at once, and queues a closed one for its next open
        private void ApplyOrQueueMenuLayout(NativeMenu menu, bool isPanel)
        {
            if (menu == null) return;
            if (menu.Visible) ApplyMenuLayout(menu, isPanel);
            else pendingLayoutMenus[menu] = isPanel;
        }

        // A Preview or Diff menu sizes through its own width pass, since its rows are 2 measured columns
        private void ApplyMenuLayout(NativeMenu menu, bool isPanel)
        {
            pendingLayoutMenus.Remove(menu);
            menu.Alignment = menuAlignLeft ? Alignment.Left : Alignment.Right;
            ApplyMenuMaxItems(menu, menuMaxRows);
            if (isPanel) RecalculatePreviewTargetWidth(menu);
            else dirtyWidthMenus.Add(menu);
        }

        // Raised by every menu before it shows, ahead of its own Shown handlers
        private void ApplyPendingMenuLayout(NativeMenu menu)
        {
            bool isPanel;
            if (pendingLayoutMenus.TryGetValue(menu, out isPanel)) ApplyMenuLayout(menu, isPanel);
        }

        private void ApplyOrQueueAllMenuLayouts()
        {
            foreach (NativeMenu menu in allMenus) ApplyOrQueueMenuLayout(menu, false);
            ApplyOrQueueMenuLayout(previewMenu, true);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) ApplyOrQueueMenuLayout(data.Value.Menu, true);
            ApplyOrQueueMenuLayout(diffMenu, true);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) ApplyOrQueueMenuLayout(data.Value.Menu, true);
        }
    }
}
