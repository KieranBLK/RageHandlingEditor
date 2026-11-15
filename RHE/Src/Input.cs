using System;
using System.Windows.Forms;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // A direct key-state poll, since Tab never appears in KeyEventArgs.Modifiers, which only carries the Control, Shift, and Alt flag bits
        private bool ModifierHeld()
        {
            if (menuActionModifier == Keys.Control) return (GetAsyncKeyState((int)Keys.ControlKey) & KEY_PRESSED_MASK) != 0;
            if (menuActionModifier == Keys.Shift) return (GetAsyncKeyState((int)Keys.ShiftKey) & KEY_PRESSED_MASK) != 0;
            if (menuActionModifier == Keys.Alt) return (GetAsyncKeyState((int)Keys.Menu) & KEY_PRESSED_MASK) != 0;
            return (GetAsyncKeyState((int)Keys.Tab) & KEY_PRESSED_MASK) != 0;
        }

        // Returns True when an armed Settings key capture (ArmKeyCapture, MenuSettings.cs) consumes the keypress, so KeyDownBody skips every other handler for it
        // Escape and Back cancel either capture kind and are never bound, and Enter falls through to the typed-entry prompt instead of being captured
        // ToggleKey's capture is resolved here, since a poll cannot enumerate every ordinary key. Up and Down pass through and cancel it by moving the highlight
        // ActionModifier's capture is resolved by PollActionModifierCapture below, since Control, Shift, Alt, and Tab need direct key-state polling
        private bool ConsumeKeyCapture(KeyEventArgs e)
        {
            if (pendingKeyCapture == PendingKeyCapture.None) return false;
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)
            {
                CancelKeyCapture(e.KeyCode == Keys.Escape);
                return true;
            }
            if (e.KeyCode == Keys.Enter)
            {
                NativeItem item = pendingCaptureItem;
                Func<Keys, bool> validator = pendingCaptureValidator;
                Action<Keys> assign = pendingCaptureAssign;
                string acceptedValuesText = pendingCaptureAcceptedValuesText;
                PendingKeyCapture settingKind = pendingKeyCapture;
                ClearKeyCapture();
                ParseAndAssignKeysSetting(item, validator, assign, acceptedValuesText, settingKind);
                return true;
            }
            if (pendingKeyCapture == PendingKeyCapture.ToggleKey)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) return false;
                ApplyCapturedKey(e.KeyCode);
                return true;
            }
            return false;
        }

        // None of the 4 valid modifier keys can be reliably detected as a bare OnKeyDown press (see PendingKeyCapture, MenuSettings.cs), so each is polled directly
        // Skipped entirely while no capture is armed, so this adds no per-tick cost otherwise
        private void PollActionModifierCapture()
        {
            if (pendingKeyCapture != PendingKeyCapture.ActionModifier) return;
            if ((GetAsyncKeyState((int)Keys.ControlKey) & KEY_PRESSED_MASK) != 0) ApplyCapturedKey(Keys.Control);
            else if ((GetAsyncKeyState((int)Keys.ShiftKey) & KEY_PRESSED_MASK) != 0) ApplyCapturedKey(Keys.Shift);
            else if ((GetAsyncKeyState((int)Keys.Menu) & KEY_PRESSED_MASK) != 0) ApplyCapturedKey(Keys.Alt);
            else if ((GetAsyncKeyState((int)Keys.Tab) & KEY_PRESSED_MASK) != 0) ApplyCapturedKey(Keys.Tab);
        }

        // True on the press of control with the modifier held, then again after HELD_HOTKEY_FIRST_REPEAT_DELAY_MS of game time
        // Later repeats come every NAV_DEFAULT_HELD_TIME_MS while both stay held, with no acceleration
        // The repeat arms only from a press made with the modifier down
        private static bool IsHeldHotkeyFiring(GTA.Control control, bool modifierHeld, ref int nextRepeatTime)
        {
            if (!modifierHeld || !Game.IsControlPressed(control))
            {
                nextRepeatTime = 0;
                return false;
            }
            int now = Game.GameTime;
            if (Game.IsControlJustPressed(control))
            {
                nextRepeatTime = now + HELD_HOTKEY_FIRST_REPEAT_DELAY_MS;
                return true;
            }
            if (nextRepeatTime == 0 || now < nextRepeatTime) return false;
            nextRepeatTime = now + NAV_DEFAULT_HELD_TIME_MS;
            return true;
        }

        // The rows come from the mask the host's hints were drawn from, so a hint that is not shown can never be acted on and a remapped control cannot desync the two
        // A tick runs at most one action
        // Every other hotkey check has no hint of its own and stays on the KeyEventArgs-driven path
        private void PollGlobalHotkeys()
        {
            NativeMenu host = tickHintHost;
            if (host == null) return;
            HintMenuState state = hintMenuStates[host];
            HintScope scope = ResolveHintScope(host, state);
            bool modifierHeld = ModifierHeld();
            for (int i = 0; i < hotkeyTable.Length; i++)
            {
                HotkeyDef row = hotkeyTable[i];
                if ((state.AppliedMask & (1 << i)) == 0)
                {
                    if (row.Held) row.NextRepeatTime = 0;
                    continue;
                }
                GTA.Control control = row.Button.Control;
                bool pressed = row.Held ? IsHeldHotkeyFiring(control, modifierHeld, ref row.NextRepeatTime) : modifierHeld && Game.IsControlJustPressed(control);
                if (!pressed) continue;
                row.Action(scope);
                // An action can change what the host shows (a deleted row, a respawned vehicle), so the hints recompute before the next frame draws
                NativeMenu hostAfterAction = ResolveVisibleHintHost();
                if (hostAfterAction != null) ApplyHints(hostAfterAction);
                return;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (startupFailed) return;
            keyDownEventCount++;
            try { KeyDownBody(e); }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.KeyDownFailed(ex.GetType().Name, ex.Message));
                logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
            }
        }

        // Incremented once per real keypress (OnKeyDown), never per tick, so a key handler's cooldown scales to keypress frequency instead of frame rate
        private int keyDownEventCount;

        private void SafeCallKey(Action action, Subsystem subsystem) { SafeCall(action, subsystem, keyDownEventCount, KEYDOWN_ERROR_COOLDOWN, BoundarySource.KeyPress); }

        // Split from OnKeyDown so the entire per-press body sits behind one exception boundary, with the outer OnKeyDown try and catch as the final backstop
        // Each independently gated block below has its own SafeCallKey, so one block's exception never suppresses the others' handling of the same keypress
        private void KeyDownBody(KeyEventArgs e)
        {
            if (e.Modifiers == Keys.Alt && e.KeyCode == Keys.F4) return;
            if (ConsumeKeyCapture(e)) return;
            // Depends only on the physical key state, which does not change for the rest of this call, so it is resolved once here instead of inside every block below
            bool modOk = ModifierHeld();
            SafeCallKey(() => HandleGlobalHotkeys(e), Subsystem.GlobalHotkeys);
            SafeCallKey(() => HandleArmedActionBackspace(e), Subsystem.ArmedActionBackspace);
            SafeCallKey(() => HandleWeaponSlotReorderKeys(e, modOk), Subsystem.WeaponSlotReorder);
            SafeCallKey(() => HandlePhantomWeaponSlotReorderKeys(e, modOk), Subsystem.PhantomWeaponSlotReorder);
            SafeCallKey(() => HandlePreviewFocusSwitchKeys(e), Subsystem.PreviewFocusSwitch);
        }

        // Available regardless of which menu, if any, is visible: The expected native close record for Escape and P, the menu toggle key, and Select mode's Back cancel
        // None of these has an instructional-button hint, and every hotkey that does is polled per tick in PollGlobalHotkeys above
        private void HandleGlobalHotkeys(KeyEventArgs e)
        {
            // Escape has no API control binding, so this direct key event records the pause-trigger side effect for the Closed dispatcher
            // Keys.P is FrontendPause, which the API's own Back and Cancel processing also reacts to, so a bare press with no modifier must record it too
            // Escape on an armed row only disarms it, and the pause menu is blocked until that press is released
            // Keys.P never disarms, since it is a hotkey, not a cancel gesture
            if ((e.KeyCode == Keys.Escape || e.KeyCode == Keys.P) && pool != null && pool.AreAnyVisible)
            {
                navigation.NativeCloseExpected = true;
                if (e.KeyCode == Keys.Escape && armedItem != null)
                {
                    navigation.EscapeBlock = EscapeBlockPhase.UntilRelease;
                    DisarmArmed();
                }
            }
            // Guarded so a disabled toggle (Keys.None) can never match a real keypress, since some OEM or laptop Fn combos deliver e.KeyCode as Keys.None itself
            if (keyToggleMenu != Keys.None && e.KeyCode == keyToggleMenu)
            {
                // Select mode hides mainMenu deliberately, so pool.AreAnyVisible reads False throughout it
                // Without this check the toggle key would fall to the branch that opens mainMenu directly, leaving selectingVehicle stuck True with mainMenu also visible
                if (selectingVehicle) CancelVehicleSelection();
                else if (pool != null && pool.AreAnyVisible) CloseAllForToggle();
                else if (mainMenu != null) OpenFromEntryPoint();
            }
            // Select mode hides every menu this script owns, so this sits outside the pool.AreAnyVisible-gated block above
            if (selectingVehicle && e.KeyCode == Keys.Back) CancelVehicleSelection();
        }

        // One handler serves every menu with an armed action, since armedMenu already tracks which menu the armed row lives in
        private void HandleArmedActionBackspace(KeyEventArgs e)
        {
            if (armedItem != null && armedMenu != null && armedMenu.Visible && e.KeyCode == Keys.Back)
            {
                navigation.NativeCloseExpected = true;
                DisarmArmed();
            }
        }

        // PageUp and PageDown reorder the live weapon slot list. It has no hint, so it stays on the key event
        private void HandleWeaponSlotReorderKeys(KeyEventArgs e, bool modOk)
        {
            if (!IsMenuOrSelectedEquivalentVisible(context => GetLiveMenuForCategory(context, CATEGORY_WEAPON))) return;
            EditorContext reorderOwner = ResolveVisibleEditorContext();
            NativeMenu reorderMenu = GetLiveMenuForCategory(reorderOwner, CATEGORY_WEAPON);
            WeaponSlotItems[] reorderSlots = reorderOwner.Weapon.WeaponSlots;
            // PageUp and PageDown, not Up and Down, because the API's own menu navigation reads Up and Down every frame the key stays held
            // That includes a repeat pass no single-frame AcceptsInput block can suppress, while PageUp and PageDown share no control with that navigation at all
            if (!modOk || (e.KeyCode != Keys.PageUp && e.KeyCode != Keys.PageDown)) return;
            int fromSlot = GetHighlightedWeaponSlotIndex(reorderMenu, reorderSlots);
            int toSlot = fromSlot + (e.KeyCode == Keys.PageUp ? -1 : 1);
            if (fromSlot < 0 || toSlot < 0 || toSlot >= reorderSlots.Length || !ReorderWeaponSlot(fromSlot, toSlot, reorderOwner)) return;
            // ReorderWeaponSlot's own sync can reflow which rows are visible (a swap into a previously unoccupied slot both hides and shows a row)
            // The target's actual post-sync position is re-resolved explicitly, since a blind step may not land correctly after that reflow
            int targetIndex = reorderMenu.Items.IndexOf(reorderSlots[toSlot].SubmenuItem);
            if (targetIndex >= 0) reorderMenu.SelectedIndex = targetIndex;
        }

        // Same PageUp/PageDown reorder as the live weapon slot list above, scoped to the phantom Weapon category's own slots
        private void HandlePhantomWeaponSlotReorderKeys(KeyEventArgs e, bool modOk)
        {
            if (!IsMenuOrSelectedEquivalentVisible(context => GetPhantomMenuForCategory(context, CATEGORY_WEAPON))) return;
            EditorContext reorderOwner = ResolveVisibleEditorContext();
            if (modOk && e.KeyCode == Keys.PageUp) ReorderPhantomWeaponSlot(reorderOwner, -1);
            else if (modOk && e.KeyCode == Keys.PageDown) ReorderPhantomWeaponSlot(reorderOwner, 1);
        }

        // The key that points toward Preview's side flips with menu alignment: Right under Left, Left under Right
        // Unfocusing from a nested category submenu never re-shows previewMenu, so previewMenu.Visible can stay False indefinitely afterward:
        // FocusPreview hands input back to whichever Preview menu is actually still on screen, not always previewMenu itself
        private void HandlePreviewFocusSwitchKeys(KeyEventArgs e)
        {
            if (savedHandlingsMenu == null || !savedHandlingsMenu.Visible) return;
            Keys focusPreviewKey = menuAlignLeft ? Keys.Right : Keys.Left;
            Keys focusListKey = menuAlignLeft ? Keys.Left : Keys.Right;
            if (e.KeyCode == focusPreviewKey && !navigation.PreviewFocused) FocusPreview(true);
            else if (e.KeyCode == focusListKey && navigation.PreviewFocused) FocusPreview(false);
        }

        // Set instead of calling RecalculateVisibleMenuWidth directly from a string or float cycler's ItemChanged handler
        // GoLeft and GoRight assign SelectedItem only after ItemChanged returns, so measuring inside the handler reads the value one cycle step behind
        // The WidthRecalc tick stage consumes it right after pool.Process() returns, once GoLeft and GoRight have fully completed for the frame
        private bool pendingWidthRecalc;
        // The HandlingData a deferred float-field recalculation measures its extended-width tier against. It is null for string cyclers, which have no float binding
        private HandlingData pendingWidthRecalcHandling;
    }
}
