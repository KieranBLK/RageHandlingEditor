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
        // Text color of a flashing row, the menu library's own highlighted title color, so a row drawn in a green or red state color stays readable on the tinted bar
        private static readonly Color RESULT_FLASH_TEXT_COLOR = Color.Black;
        // An armed row disarms after ARMED_ACTION_EXPIRY_MS (UpdateArmedFlash), or at once when SelectedIndex shifts off it
        // Navigating away and later returning to the row never risks an immediate accidental confirm from a press meant to arm it fresh
        private NativeItem armedItem;
        // The menu armedItem's own row lives in, so UpdateArmedFlash can tell when SelectedIndex has shifted off it
        private NativeMenu armedMenu;
        // Game.GameTime at the moment of arming
        private int armedTime;
        private Color armedOriginalColor;
        private Color armedOriginalArrowColor;
        // Keeps a press for a different action from confirming this one
        private string armedActionKey;
        private Action armedConfirmAction;

        // First press arms and a second press on the same armed row confirms. The stored action runs, so a value captured at arm time is the one confirmed
        // Every arm-then-confirm action in the script funnels through here
        private void ArmOrConfirm(NativeItem item, NativeMenu menu, string actionKey, Action confirmAction)
        {
            if (armedItem == item && armedActionKey == actionKey)
            {
                Action confirm = armedConfirmAction;
                DisarmArmed();
                confirm();
                return;
            }
            DisarmArmed();
            armedItem = item;
            armedMenu = menu;
            armedTime = Game.GameTime;
            armedOriginalColor = item.Colors.TitleHovered;
            StyledSubmenuItem styled = item as StyledSubmenuItem;
            armedOriginalArrowColor = styled != null ? styled.arrow.Color : Color.Empty;
            armedActionKey = actionKey;
            armedConfirmAction = confirmAction;
            // Fires on every arm rather than once per session, key-based replace-in-place so repeated arms never stack duplicate notifications on screen
            notifications.Notify(NOTIFICATION_KEY_ARMED_ACTION_HINT, string.Format(NOTIF_ARMED_CONFIRM_HINT, ARMED_ACTION_EXPIRY_MS / 1000));
        }

        private void TriggerDeleteHandling()
        {
            NativeItem highlighted = savedHandlingsMenu.SelectedItem;
            SavedHandlingKey key = GetHighlightedSavedHandlingKey();
            if (highlighted == null || !key.IsValid) return;
            ArmOrConfirm(highlighted, savedHandlingsMenu, "Delete", () => DeleteHandlingFile(key));
        }

        private void TriggerUnmountAdditionalEditor()
        {
            NativeItem highlighted = mainMenu.SelectedItem;
            EditorContext unmountedContext = ResolveAdditionalEditorForMainMenuItem(highlighted);
            if (unmountedContext == null) return;
            ArmOrConfirm(highlighted, mainMenu, "UnmountAdditionalEditor", () => ConfirmUnmountAdditionalEditor(unmountedContext));
        }

        private void DisarmArmed()
        {
            if (armedItem == null) return;
            notifications.RemoveNotification(NOTIFICATION_KEY_ARMED_ACTION_HINT);
            armedItem.Colors.TitleHovered = armedOriginalColor;
            // UpdateColors also retints a submenu row's arrow from its current selection, so a row disarmed by deselection never keeps the red or black
            armedItem.UpdateColors();
            armedItem = null;
            armedMenu = null;
            armedActionKey = null;
            armedConfirmAction = null;
        }

        // Item identity is unique per tree, so whichever tree's table contains the item is the correct answer
        private string CategoryForSubmenu(NativeItem item)
        {
            if (item == null) return null;
            for (int i = 0; i < editorContexts.Length; i++)
            {
                string found = CategoryForSubmenuInTable(item, editorContexts[i].Categories);
                if (found != null) return found;
            }
            return null;
        }

        private static string CategoryForSubmenuInTable(NativeItem item, SubHandlingCategory[] table)
        {
            if (table == null) return null;
            for (int i = 0; i < table.Length; i++) { if (item == table[i].LiveSubmenu || item == table[i].PhantomSubmenu) return table[i].Definition.Name; }
            return null;
        }

        // The hotkey row that calls this (IsCategoryRowHighlighted, MenuHints.cs) already guarantees that menu is visible
        private void TriggerSubDataExclusion(EditorContext context)
        {
            NativeMenu menu = context.RootMenu;
            NativeItem highlighted = menu.SelectedItem;
            string category = CategoryForSubmenu(highlighted);
            if (category == null) return;
            ArmOrConfirm(highlighted, menu, "SubDataExclusion", () => DeleteLiveCategoryOrExclude(context, category));
        }

        // With live deletion on, a live category the game never reads as required is removed from the running game
        // Every other press keeps the exclusion from the next save, which also covers a plugin that is missing or refuses
        // A category already excluded is included back instead, since that press undoes the exclusion
        private void DeleteLiveCategoryOrExclude(EditorContext context, string category)
        {
            Vehicle vehicle = context.TargetVehicle;
            HashSet<string> toggledSet;
            bool excluded = !Entity.IsNullOrNotExisting(vehicle) && pendingSubDataToggle.TryGetValue(ResolveHandlingKey(vehicle), out toggledSet) && toggledSet.Contains(category);
            bool refusedLiveDeletion = false;
            if (!excluded && !Entity.IsNullOrNotExisting(vehicle) && vehicle.HandlingData.IsValid)
            {
                string reason;
                if (BeginLiveCategoryDeletion(context, category, vehicle, null, LiveRequestOrigin.Player, out reason)) return;
                // Live deletion was on offer but the game reads this category as required, so the toast says why it only excludes
                refusedLiveDeletion = IsLiveCreationOffered(category, vehicle.Type) && IsLiveDeletionRefused(category, vehicle);
            }
            ToggleSubDataExclusion(context, category, refusedLiveDeletion);
        }

        private void ConfirmOverwriteHandling()
        {
            // An autosave is only ever written by Autosave itself
            NativeItem highlighted = savedHandlingsMenu.SelectedItem;
            SavedHandlingKey key = GetHighlightedSavedHandlingKey();
            if (highlighted == null || !key.IsValid || !key.Source.Has(SavedHandlingCapabilities.Overwrite)) return;
            ArmOrConfirm(highlighted, savedHandlingsMenu, "Overwrite", () => OverwriteHandling(highlighted, key));
        }

        // Pure data mutation. Every call site resyncs afterward
        private void SetSubDataToggle(HandlingKey handling, string category, bool toggled)
        {
            HashSet<string> set = GetOrAdd(pendingSubDataToggle, handling);
            if (toggled)
            {
                set.Add(category);
                return;
            }
            set.Remove(category);
            ForgetPhantomScrollPosition(handling, category);
        }

        private void ToggleSubDataExclusion(EditorContext context, string category, bool refusedLiveDeletion)
        {
            Vehicle vehicle = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            HandlingData handling = vehicle.HandlingData;
            if (!handling.IsValid) return;
            bool isPresent = IsCategoryPresent(handling, category);
            HandlingKey handlingKey = ResolveHandlingKey(handling);
            try
            {
                // Routed through SetSubDataToggle so the toggle has exactly one mutation point
                HashSet<string> toggledSet;
                bool nowToggled = !(pendingSubDataToggle.TryGetValue(handlingKey, out toggledSet) && toggledSet.Contains(category));
                SetSubDataToggle(handlingKey, category, nowToggled);
                // Self-inverse: Flipping the same toggle again is both the undo and the redo
                PushStructuralEdit(new CategoryToggleEdit(StructuralKind.SubDataToggle, category, !nowToggled, nowToggled) { Handling = handlingKey });
                SyncEditorContext(context);

                // Posted only after the resync above completes without throwing, so the user is never told a toggle succeeded while the field list may not reflect it
                // Present: Toggled means excluded. Absent (a created phantom category being reverted): Toggled means removed again
                string message;
                if (isPresent && !nowToggled) message = string.Format(NOTIF_CATEGORY_INCLUDED, category);
                else if (isPresent && refusedLiveDeletion) message = string.Format(NOTIF_LIVE_CATEGORY_REQUIRED_EXCLUDED, category);
                else if (isPresent) message = string.Format(NOTIF_CATEGORY_EXCLUDED, category);
                else if (nowToggled) message = string.Format(NOTIF_CATEGORY_WILL_BE_CREATED, category);
                else message = string.Format(NOTIF_CATEGORY_REMOVED, category);
                notifications.Notify(NOTIFICATION_KEY_SUBDATA_EXCLUSION, message);
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.SubDataToggleFailed(category, ex.Message)); }
        }

        private void UpdateArmedFlash()
        {
            if (armedItem == null) return;
            if (armedMenu != null && armedMenu.SelectedItem != armedItem)
            {
                DisarmArmed();
                return;
            }
            if (Game.IsControlJustPressed(GTA.Control.CursorCancel))
            {
                DisarmArmed();
                return;
            }
            int elapsed = Game.GameTime - armedTime;
            if (elapsed >= ARMED_ACTION_EXPIRY_MS)
            {
                DisarmArmed();
                return;
            }
            bool flashOn = (elapsed / ARMED_FLASH_INTERVAL_MS) % 2 == 0;
            armedItem.Colors.TitleHovered = flashOn ? Color.Red : armedOriginalColor;
            armedItem.UpdateColors();
            StyledSubmenuItem styledFlash = armedItem as StyledSubmenuItem;
            if (styledFlash != null) styledFlash.arrow.Color = flashOn ? Color.Red : armedOriginalArrowColor;
        }

        // A result flash tints the highlight bar the menu draws behind its selected row and paints the row's text black, leaving the row's ColorSet alone
        // A recolor during the flash lands in the ColorSet, so the text paint is renewed every tick before the draw, and the recolor shows once the flash ends
        // The bar belongs to the menu and follows the selection, so a flash ends the moment the selection leaves its row, or the next row would inherit the tint
        // Menu and Highlight stay null until the entry resolves, which waits for a caller that moves the highlight after starting the flash
        private class ResultFlashEntry
        {
            public NativeItem Item;
            public NativeMenu Menu;
            public ScaledTexture Highlight;
            public int EndTime;
            public Color FlashColor;
            public Color OriginalColor;
        }

        private List<ResultFlashEntry> resultFlashEntries = new List<ResultFlashEntry>();

        private void StartResultFlash(NativeItem item, Color color)
        {
            for (int i = resultFlashEntries.Count - 1; i >= 0; i--)
            {
                if (resultFlashEntries[i].Item == item) EndResultFlashEntryAt(i);
            }
            ResultFlashEntry entry = new ResultFlashEntry { Item = item, EndTime = Game.GameTime + RESULT_FLASH_DURATION_MS, FlashColor = color };
            resultFlashEntries.Add(entry);
            ResolveResultFlashEntry(entry);
        }

        // An item has no link back to its menu, so the menu showing it is found by walking the pool for a visible menu that has the item selected
        private NativeMenu ResolveMenuShowingItem(NativeItem item)
        {
            if (pool == null || item == null) return null;
            foreach (IProcessable processable in pool)
            {
                NativeMenu menu = processable as NativeMenu;
                if (menu != null && menu.Visible && menu.SelectedItem == item) return menu;
            }
            return null;
        }

        // Any other entry tinting the same menu ends first, so the color captured here is always the untinted one
        private bool ResolveResultFlashEntry(ResultFlashEntry entry)
        {
            NativeMenu menu = ResolveMenuShowingItem(entry.Item);
            if (menu == null) return false;
            ScaledTexture highlight = ReadMenuHighlight(menu);
            if (highlight == null) return false;
            for (int i = resultFlashEntries.Count - 1; i >= 0; i--)
            {
                if (resultFlashEntries[i] != entry && resultFlashEntries[i].Menu == menu) EndResultFlashEntryAt(i);
            }
            entry.Menu = menu;
            entry.Highlight = highlight;
            entry.OriginalColor = highlight.Color;
            highlight.Color = entry.FlashColor;
            PaintItemText(entry.Item, RESULT_FLASH_TEXT_COLOR);
            return true;
        }

        private void EndResultFlashEntryAt(int index)
        {
            ResultFlashEntry entry = resultFlashEntries[index];
            resultFlashEntries.RemoveAt(index);
            if (entry.Highlight == null) return;
            entry.Highlight.Color = entry.OriginalColor;
            entry.Item.UpdateColors();
        }

        // Resolving an entry can end earlier entries and shorten the list, so the loop index is checked against it
        private void UpdateResultFlash()
        {
            int now = Game.GameTime;
            for (int i = resultFlashEntries.Count - 1; i >= 0; i--)
            {
                if (i >= resultFlashEntries.Count) continue;
                ResultFlashEntry entry = resultFlashEntries[i];
                if (entry.Highlight == null)
                {
                    if (!ResolveResultFlashEntry(entry)) resultFlashEntries.RemoveAt(i);
                    continue;
                }
                if (now >= entry.EndTime || !entry.Menu.Visible || entry.Menu.SelectedItem != entry.Item) EndResultFlashEntryAt(i);
                else PaintItemText(entry.Item, RESULT_FLASH_TEXT_COLOR);
            }
        }
    }
}
