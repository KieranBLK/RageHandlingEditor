using System;
using System.Collections.Generic;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Why the script is hiding a menu
        // The navigator sets it around every visibility change it makes, so a Closed handler tells the script's own hide from the player's
        private enum NavigationTransition { None, ToggleClose, RefreshHide, BackOut, Programmatic }

        // The pause block after an Escape that disarmed a row. UntilRelease holds until Escape's release edge, and Released holds for one more tick
        private enum EscapeBlockPhase { None, UntilRelease, Released }

        // Picks a menu's Closed behavior. A GlobalOrTree menu does nothing on a real exit, since the library's Back reopens its parent
        private enum MenuFamily { GlobalOrTree, SavedHandlings, PreviewRoot, PreviewPanel }

        // A menu's place in the Closed dispatcher. ReopenTarget is the menu a Preview panel returns to when the player leaves it
        private class MenuNode
        {
            public readonly MenuFamily Family;
            public readonly NativeMenu ReopenTarget;
            public MenuNode(MenuFamily family, NativeMenu reopenTarget)
            {
                Family = family;
                ReopenTarget = reopenTarget;
            }
        }

        private class PreviewPosition
        {
            public SavedHandlingSource Source;
            public string Name;
            public bool Focused;
            public int TopRow;
            public List<KeyValuePair<string, int>> Path;
        }

        // Every piece of menu navigation state in one place. The key events set the expected close and the Escape block, and the navigator owns the rest
        private class MenuNavigationState
        {
            // Set by each navigator call around the visibility changes it makes, and restored to its earlier value afterward
            public NavigationTransition Transition;
            // The Preview menu whose submenu row was just activated, and the tick it happened. The library raises ItemActivated before it hides the parent,
            // so the parent's Closed handler can tell a descent from an exit only by this
            // It holds for its own tick, so a descent whose parent never closed cannot swallow a later exit
            public NativeMenu DescentFrom;
            public int DescentTick = -1;
            // True from an Escape, pause, or Backspace disarm key event until the end of the next tick
            // The game's pause trigger closes whatever menu is visible in that window, and the Closed dispatcher reopens it
            public bool NativeCloseExpected;
            // What the player wants Preview to do (the Preview toggle), apart from whether its menu is on screen
            public bool PreviewWanted;
            // True while a Preview menu, not Saved Handlings, takes input
            public bool PreviewFocused;
            public EscapeBlockPhase EscapeBlock;
            // The menu open when the toggle key last closed everything. The next open from the toggle key or cheat string returns to it
            public NativeMenu RememberedMenu;
            public PreviewPosition RememberedPreview;
            // Saved Handlings' return target when the toggle key closed it during a hotkey visit, kept so backing out after the reopen still returns there
            public NativeMenu RememberedShortcutReturn;
            // The menu Saved Handlings returns to on back-out after a hotkey visit
            public NativeMenu ShortcutReturn;
            // Set when Saved Handlings closes for real while a hotkey visit left its Parent blank. The next tick restores Parent
            public bool RestoreSavedHandlingsParent;
        }

        private readonly MenuNavigationState navigation = new MenuNavigationState();
        private readonly Dictionary<NativeMenu, MenuNode> menuNodes = new Dictionary<NativeMenu, MenuNode>();

        // Wires one menu into the Closed dispatcher. hasChildren marks a menu with submenu rows, whose activation is recorded as a descent
        // The record is gated on a submenu row, so a plain Enter on a field row still copies
        private void RegisterMenuNode(NativeMenu menu, MenuFamily family, NativeMenu reopenTarget, bool hasChildren)
        {
            menuNodes[menu] = new MenuNode(family, reopenTarget);
            if (hasChildren) SubscribeItemActivated(menu, "RegisterMenuNode.ItemActivated", e =>
            {
                if (e.Item is NativeSubmenuItem)
                {
                    navigation.DescentFrom = menu;
                    navigation.DescentTick = uptimeTicks;
                }
            });
            SubscribeClosed(menu, "RegisterMenuNode.Closed", () => OnMenuClosed(menu));
        }

        // The checks run in a fixed order: A set transition is the script's own hide and only settles bookkeeping
        // A descent from this menu this tick is not an exit
        // An expected native close reopens the menu (a Preview menu only while Preview is wanted)
        // Anything else is the player leaving, which runs the family's exit
        private void OnMenuClosed(NativeMenu menu)
        {
            MenuNode node = menuNodes[menu];
            if (navigation.Transition != NavigationTransition.None)
            {
                if (node.Family == MenuFamily.SavedHandlings) FinishSavedHandlingsClose();
                return;
            }
            if (navigation.DescentFrom == menu && navigation.DescentTick == uptimeTicks)
            {
                navigation.DescentFrom = null;
                return;
            }
            bool previewFamily = node.Family == MenuFamily.PreviewRoot || node.Family == MenuFamily.PreviewPanel;
            if (navigation.NativeCloseExpected && (!previewFamily || navigation.PreviewWanted))
            {
                ShowMenu(menu);
                if (previewFamily) FocusPreview(navigation.PreviewFocused);
                return;
            }
            switch (node.Family)
            {
                case MenuFamily.SavedHandlings:
                    ExitSavedHandlings();
                    break;
                case MenuFamily.PreviewRoot:
                    // A native Back while Preview is focused hands focus back to the list instead of closing it
                    FocusPreview(false);
                    if (ShouldPreviewStayOpen() && GetHighlightedPreviewKey().IsValid) ShowMenu(menu);
                    break;
                case MenuFamily.PreviewPanel:
                    if (ShouldPreviewStayOpen())
                    {
                        ShowMenu(node.ReopenTarget);
                        FocusPreview(navigation.PreviewFocused);
                    }
                    else FocusPreview(false);
                    break;
            }
        }

        // The script closed Saved Handlings itself (the toggle key, or a hide of its own). Nothing reopens, Preview closes with it, and a hotkey visit ends
        private void FinishSavedHandlingsClose()
        {
            if (savedHandlingsMenu.Parent == null) navigation.RestoreSavedHandlingsParent = true;
            ClosePreviewTree();
            navigation.ShortcutReturn = null;
        }

        private void ExitSavedHandlings()
        {
            bool wasArmed = armedItem != null;
            // A real Back inside Autosaves returns to Exports instead of leaving
            // It sits before the Parent restore below, so a hotkey visit's blank Parent stays blank
            // The library's Back skips reopening Parent because the menu is visible again when Closed returns
            bool backPressed = Game.IsControlJustPressed(GTA.Control.FrontendCancel) || Game.IsControlJustPressed(GTA.Control.CursorCancel);
            if (browsedSource == autosavesSource && backPressed && !wasArmed)
            {
                browsedSource = exportsSource;
                ShowMenu(savedHandlingsMenu);
                savedHandlingsMenu.SelectedIndex = 0;
                return;
            }
            DisarmArmed();
            ClosePreviewTree();
            // A Backspace disarm is caught earlier as an expected native close. This covers the inputs that disarm without a key event (a controller or a remapped cancel)
            if (wasArmed)
            {
                ShowMenu(savedHandlingsMenu);
                return;
            }
            // Restoring Parent here would race the library's Back, which reads Parent right after Closed returns, so the next tick does it
            // Only a real exit sets this, since the armed reopen keeps Saved Handlings open with Parent still blank
            if (savedHandlingsMenu.Parent == null) navigation.RestoreSavedHandlingsParent = true;
            NativeMenu returnTo = navigation.ShortcutReturn;
            navigation.ShortcutReturn = null;
            if (returnTo != null) ShowMenu(returnTo);
        }

        // Showing any menu of a tree makes that tree Active. Respawn and Save act on the Active editor, so their enablement and hints follow the switch
        private void MakeTreeActive(EditorContext tree)
        {
            if (activeContext == tree) return;
            activeContext = tree;
            ApplyActiveColors();
            SyncActiveActionEnablement();
        }

        private void ShowMenu(NativeMenu menu) { menu.Visible = true; }

        // Runs a visibility change under its transition and restores the earlier one afterward, even when the body throws
        private void RunUnderTransition(NavigationTransition transition, Action body)
        {
            NavigationTransition previous = navigation.Transition;
            navigation.Transition = transition;
            try { body(); }
            finally { navigation.Transition = previous; }
        }

        private void HideMenu(NativeMenu menu) { RunUnderTransition(NavigationTransition.Programmatic, () => menu.Visible = false); }

        // Backs a menu out to its parent when an undo, a removed row, or a vehicle change invalidates its content
        // The BackOut transition tells the export-only scroll memory to ignore the close
        private void BackOut(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            RunUnderTransition(NavigationTransition.BackOut, () =>
            {
                menu.Visible = false;
                if (menu.Parent != null) menu.Parent.Visible = true;
            });
        }

        // Closes every menu for the toggle key, remembering the one on screen so the next open returns to it
        private void CloseAllForToggle()
        {
            navigation.RememberedMenu = null;
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible)
                {
                    navigation.RememberedMenu = allMenus[i];
                    break;
                }
            }
            // Saved Handlings' close drops the hotkey visit's return target and closes the Preview tree, so both are kept here first
            navigation.RememberedShortcutReturn = navigation.RememberedMenu == savedHandlingsMenu ? navigation.ShortcutReturn : null;
            navigation.RememberedPreview = navigation.RememberedMenu == savedHandlingsMenu ? CapturePreviewPosition() : null;
            // A toggle key bound to Escape or P sets the expected close in the same key press, and this close is deliberate
            navigation.NativeCloseExpected = false;
            RunUnderTransition(NavigationTransition.ToggleClose, () => pool.HideAll());
        }

        // Shared by the toggle key and the cheat string: Reopens the remembered menu, or its nearest parent still reachable from the main menu
        private void OpenFromEntryPoint()
        {
            NativeMenu remembered = navigation.RememberedMenu;
            NativeMenu shortcutReturn = navigation.RememberedShortcutReturn;
            PreviewPosition rememberedPreview = navigation.RememberedPreview;
            navigation.RememberedMenu = null;
            navigation.RememberedShortcutReturn = null;
            navigation.RememberedPreview = null;
            NativeMenu target = remembered;
            while (target != null && !IsMenuReachableFromMainMenu(target)) target = target.Parent;
            if (target == null) target = mainMenu;
            // Rebuilds the state a hotkey visit leaves: A blank Parent so the library's Back cannot also show the main menu
            // It also restores the return target Saved Handlings reopens on back-out, skipped when that menu is no longer reachable
            if (target == savedHandlingsMenu && shortcutReturn != null && IsMenuReachableFromMainMenu(shortcutReturn))
            {
                savedHandlingsMenu.Parent = null;
                navigation.ShortcutReturn = shortcutReturn;
            }
            ShowMenu(target);
            // After the show, since Saved Handlings' Shown refresh rebuilds Preview from the per-saved-handling memory, which predates the close
            if (target == savedHandlingsMenu && remembered == target) ApplyPreviewPosition(rememberedPreview);
            if (remembered != null) logger.Log(Dbg.Level.Info, Dbg.MenuReopened(DescribeMenu(target), remembered == target ? null : DescribeMenu(remembered)));
        }

        // Hides every menu this script owns, then shows target, so a hotkey jump behaves like pressing the submenu row
        // The visit's return menu is recorded first
        // target.Parent is blanked because the library's Back reopens Parent whenever the Closed handler left the menu hidden
        // That would race the exit's own return and leave two menus visible at once
        private void JumpTo(NativeMenu target)
        {
            navigation.ShortcutReturn = null;
            for (int i = 0; i < allMenus.Length; i++)
            {
                if (allMenus[i].Visible && allMenus[i] != target)
                {
                    navigation.ShortcutReturn = allMenus[i];
                    break;
                }
            }
            target.Parent = null;
            if (target == savedHandlingsMenu) browsedSource = exportsSource;
            RunUnderTransition(NavigationTransition.Programmatic, () =>
            {
                foreach (NativeMenu menu in allMenus) if (menu != target) menu.Visible = false;
            });
            ShowMenu(target);
        }

        // Both hotkey visit return targets that sit in the tree move out of it, so Back never lands in a tree with no vehicle
        // They move to the main menu, or to Current's equivalent menu when a seat merge folds the tree into Current
        private void LeaveTree(EditorContext tree, NativeMenu returnTo, bool mapToCurrentTree)
        {
            navigation.ShortcutReturn = RedirectOutOfTree(tree, navigation.ShortcutReturn, mapToCurrentTree);
            navigation.RememberedShortcutReturn = RedirectOutOfTree(tree, navigation.RememberedShortcutReturn, mapToCurrentTree);
            bool treeWasVisible = false;
            RunUnderTransition(NavigationTransition.Programmatic, () =>
            {
                for (int i = 0; i < tree.AllOwnMenus.Length; i++)
                {
                    if (tree.AllOwnMenus[i].Visible) treeWasVisible = true;
                    tree.AllOwnMenus[i].Visible = false;
                }
            });
            if (treeWasVisible && returnTo != null) ShowMenu(returnTo);
        }

        private NativeMenu RedirectOutOfTree(EditorContext tree, NativeMenu menu, bool mapToCurrentTree)
        {
            int position = menu != null ? Array.IndexOf(tree.AllOwnMenus, menu) : -1;
            if (position < 0) return menu;
            return mapToCurrentTree ? (ResolveEquivalentCurrentMenu(tree, position) ?? mainMenu) : mainMenu;
        }

        // Fully closes the Preview tree, whichever menu is on screen at any depth plus previewMenu itself, and returns focus to Saved Handlings
        // Diff has no close of its own and would stay on screen with nothing behind it, so it hides here too
        private void ClosePreviewTree()
        {
            RememberPreviewPosition();
            RunUnderTransition(NavigationTransition.Programmatic, () =>
            {
                NativeMenu activePreviewMenu = GetVisiblePreviewMenu();
                if (activePreviewMenu != null) activePreviewMenu.Visible = false;
                previewMenu.Visible = false;
                HideDiffCategoryMenus();
                diffMenu.Visible = false;
            });
            FocusPreview(false);
            diffBaselineActive = false;
            lastShownDiffMenu = null;
        }

        // A stale panel hides with no side effects. The refresh is about to rebuild its content, so its Closed handler must not reopen it or move focus
        private void HidePreviewPanelForRefresh(NativeMenu panel)
        {
            RunUnderTransition(NavigationTransition.RefreshHide, () => panel.Visible = false);
        }

        // The one writer of Preview's focus. Saved Handlings and the Preview menu on screen are set together
        // A refresh or an exit that changes which Preview menu is visible reapplies focus to the one now showing, so no state leaves every menu without input
        private void FocusPreview(bool focused)
        {
            NativeMenu visiblePreview = GetVisiblePreviewMenu();
            if (visiblePreview == null) focused = false;
            bool changed = navigation.PreviewFocused != focused;
            navigation.PreviewFocused = focused;
            savedHandlingsMenu.AcceptsInput = !focused;
            previewMenu.AcceptsInput = focused && visiblePreview == previewMenu;
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) data.Value.Menu.AcceptsInput = focused && data.Value.Menu == visiblePreview;
            if (changed) SyncPreviewEnabled();
        }

        private void HideDiffCategoryMenus()
        {
            RunUnderTransition(NavigationTransition.Programmatic, () =>
            {
                foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) data.Value.Menu.Visible = false;
            });
        }

        // Diff follows Preview without taking input, so the mirror shows the Diff twin of the Preview menu on screen and hides every other Diff menu first
        private void ShowDiffMirrorTarget(NativeMenu target)
        {
            HideDiffCategoryMenus();
            if (target == diffMenu) ShowMenu(diffMenu);
            else
            {
                HideMenu(diffMenu);
                ShowMenu(target);
            }
        }

        // The reopen and reload paths restore into Saved Handlings, so they capture only while it is open
        private PreviewPosition CapturePreviewPosition()
        {
            return savedHandlingsMenu.Visible ? BuildPreviewPosition() : null;
        }

        // Needs no open Saved Handlings, since its own close runs ClosePreviewTree after the list already hid
        private PreviewPosition BuildPreviewPosition()
        {
            NativeMenu visiblePreview = GetVisiblePreviewMenu();
            if (visiblePreview == null || !previewShownSavedHandlingKey.IsValid) return null;
            var path = new List<KeyValuePair<string, int>>();
            NativeMenu menu = visiblePreview;
            for (int depth = 0; menu != previewMenu; depth++)
            {
                MenuNode node;
                string key = FindPreviewCategoryKey(menu);
                if (depth >= MENU_REACHABILITY_MAX_DEPTH || key == null || !menuNodes.TryGetValue(menu, out node) || node.ReopenTarget == null) return null;
                path.Insert(0, new KeyValuePair<string, int>(key, menu.SelectedIndex));
                menu = node.ReopenTarget;
            }
            return new PreviewPosition { Source = previewShownSavedHandlingKey.Source, Name = previewShownSavedHandlingKey.Name, Focused = navigation.PreviewFocused, TopRow = previewMenu.SelectedIndex, Path = path };
        }

        private string FindPreviewCategoryKey(NativeMenu menu)
        {
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) if (data.Value.Menu == menu) return data.Key;
            return null;
        }

        private void ApplyPreviewPosition(PreviewPosition position) { ApplyPreviewPosition(position, position != null && position.Focused); }

        // Runs after a Preview refresh, since a refresh resets every category to its first row
        // A category menu already on screen hides first, so a reopen whose own refresh restored a remembered path still lands on the captured one
        // The walk stops at the first menu the saved handling no longer lists, and that menu takes its own saved row
        // Every row is set while its menu is hidden, since a visible menu plays the navigation sound and raises SelectedIndexChanged
        private void ApplyPreviewPosition(PreviewPosition position, bool focused)
        {
            if (position == null) return;
            NativeMenu shown = GetVisiblePreviewMenu();
            if (shown == null)
            {
                logger.Log(Dbg.Level.Info, Dbg.PreviewPositionSkipped("Preview is not open"));
                return;
            }
            if (!previewShownSavedHandlingKey.IsValid || previewShownSavedHandlingKey.Source != position.Source || previewShownSavedHandlingKey.Name != position.Name)
            {
                logger.Log(Dbg.Level.Info, Dbg.PreviewPositionSkipped("Preview shows a different saved handling"));
                return;
            }
            var menus = new List<NativeMenu> { previewMenu };
            var rows = new List<int> { position.TopRow };
            for (int i = 0; i < position.Path.Count; i++)
            {
                PreviewCategoryData data;
                if (!previewCategoryData.TryGetValue(position.Path[i].Key, out data)) break;
                int entryIndex = FindSubmenuEntryIndex(menus[menus.Count - 1], data.Menu);
                if (entryIndex < 0) break;
                rows[rows.Count - 1] = entryIndex;
                menus.Add(data.Menu);
                rows.Add(position.Path[i].Value);
            }
            NativeMenu deepest = menus[menus.Count - 1];
            if (shown != previewMenu || menus.Count > 1) HidePreviewPanelForRefresh(shown);
            for (int i = 0; i < menus.Count; i++)
            {
                if (menus[i].Items.Count == 0) continue;
                int row = Math.Max(0, Math.Min(rows[i], menus[i].Items.Count - 1));
                if (menus[i].SelectedIndex != row) menus[i].SelectedIndex = row;
            }
            if (!deepest.Visible) ShowMenu(deepest);
            FocusPreview(focused);
            logger.Log(Dbg.Level.Info, Dbg.PreviewPositionRestored(DescribeMenu(deepest), deepest.SelectedIndex));
        }
    }
}
