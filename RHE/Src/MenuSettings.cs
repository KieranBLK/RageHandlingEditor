using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The notification cooldown key for a key-binding setting's own validation failure, so Action Modifier and Toggle Menu Key never throttle each other
        private static string KeySettingNotificationKey(PendingKeyCapture kind)
        {
            return kind == PendingKeyCapture.ActionModifier ? NOTIFICATION_KEY_FIELD_ENTRY_INVALID_ACTION_MODIFIER : NOTIFICATION_KEY_FIELD_ENTRY_INVALID_TOGGLE_KEY;
        }

        // The typed-entry path that Enter reaches during an armed key capture (ConsumeKeyCapture, Input.cs), shared by Action Modifier and Toggle Menu Key
        // The prompt opens empty so a new key can be typed without clearing the old one first. A blank or whitespace-only entry cancels with no change
        // A typed or captured None is rejected, since clearing Toggle Menu Key or the cheat string goes through Clear Input
        // acceptedValuesText names the accepted values in the validator failure message, and settingKind keeps each setting's notifications from throttling the other's
        private void ParseAndAssignKeysSetting(NativeItem item, Func<Keys, bool> validator, Action<Keys> assign, string acceptedValuesText, PendingKeyCapture settingKind)
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage20, "", INPUT_MAX_LENGTH);
            string trimmed = string.IsNullOrEmpty(input) ? "" : input.Trim();
            if (trimmed.Length == 0)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                return;
            }
            if (string.Equals(trimmed, "ctrl", StringComparison.OrdinalIgnoreCase)) trimmed = "Control";
            Keys parsed;
            try { parsed = (Keys)Enum.Parse(typeof(Keys), trimmed, true); }
            catch
            {
                notifications.PostNotificationOnce(KeySettingNotificationKey(settingKind), string.Format(NOTIF_KEY_INVALID_MUST_BE, acceptedValuesText));
                return;
            }
            if (!validator(parsed))
            {
                if (parsed == Keys.None) notifications.PostNotificationOnce(KeySettingNotificationKey(settingKind), NoneRejectedMessage(settingKind));
                else notifications.PostNotificationOnce(KeySettingNotificationKey(settingKind), string.Format(NOTIF_KEY_MUST_BE, acceptedValuesText));
                return;
            }
            assign(parsed);
            item.AltTitle = parsed.ToString();
            SyncEntryPointRows();
            notifications.RemoveNotification(NOTIFICATION_KEY_KEY_CAPTURE_ARMED);
            StartResultFlash(item, Color.LightGreen);
            WriteDefaultIni();
        }

        // Toggle Menu Key unbinds only through Clear Input. Action Modifier is always required
        private static string NoneRejectedMessage(PendingKeyCapture kind)
        {
            return kind == PendingKeyCapture.ToggleKey ? NOTIF_KEY_TOGGLE_USE_CLEAR_INPUT : NOTIF_KEY_MODIFIER_REQUIRED;
        }

        // The menu always needs an entry point, so a change that would leave both the toggle key and the cheat string empty is refused
        private bool WouldRemoveLastMenuEntryPoint(Keys toggleKey, string cheatString)
        {
            if (toggleKey != Keys.None || !string.IsNullOrEmpty(cheatString)) return false;
            notifications.PostNotificationOnce(NOTIFICATION_KEY_MENU_ENTRY_POINT_KEPT, NOTIF_ENTRY_POINT_REQUIRED);
            return true;
        }

        // The key-binding a "press the desired key now" capture is armed for (ArmKeyCapture), or None. Moving the highlight off its row cancels it
        // ActionModifier's capture is polled by the KeyCapturePoll stage (PollActionModifierCapture, Input.cs)
        // Control, Shift, Alt, and Tab cannot be reliably detected as a bare OnKeyDown press, and Alt fires WM_SYSKEYDOWN, which never reaches it
        // ToggleKey's capture is consumed by OnKeyDown's next real keypress (ConsumeKeyCapture, Input.cs), since a poll cannot enumerate every ordinary key
        private enum PendingKeyCapture { None, ActionModifier, ToggleKey }
        private PendingKeyCapture pendingKeyCapture;
        private NativeItem pendingCaptureItem;
        private Func<Keys, bool> pendingCaptureValidator;
        private Action<Keys> pendingCaptureAssign;
        private string pendingCaptureAcceptedValuesText;

        private void ArmKeyCapture(PendingKeyCapture kind, NativeItem item, Func<Keys, bool> validator, Action<Keys> assign, string acceptedValuesText)
        {
            pendingKeyCapture = kind;
            pendingCaptureItem = item;
            pendingCaptureValidator = validator;
            pendingCaptureAssign = assign;
            pendingCaptureAcceptedValuesText = acceptedValuesText;
            notifications.PostNotificationOnce(NOTIFICATION_KEY_KEY_CAPTURE_ARMED, NOTIF_KEY_CAPTURE_ARMED);
        }

        // Applies a captured value the same way the typed-entry path does, then clears every pending field regardless of outcome
        private void ApplyCapturedKey(Keys captured)
        {
            if (!pendingCaptureValidator(captured))
            {
                if (captured == Keys.None) notifications.PostNotificationOnce(KeySettingNotificationKey(pendingKeyCapture), NoneRejectedMessage(pendingKeyCapture));
                else notifications.PostNotificationOnce(KeySettingNotificationKey(pendingKeyCapture), string.Format(NOTIF_KEY_MUST_BE, pendingCaptureAcceptedValuesText));
                ClearKeyCapture();
                return;
            }
            pendingCaptureAssign(captured);
            pendingCaptureItem.AltTitle = captured.ToString();
            SyncEntryPointRows();
            notifications.RemoveNotification(NOTIFICATION_KEY_KEY_CAPTURE_ARMED);
            StartResultFlash(pendingCaptureItem, Color.LightGreen);
            WriteDefaultIni();
            ClearKeyCapture();
        }

        private void ClearKeyCapture()
        {
            pendingKeyCapture = PendingKeyCapture.None;
            pendingCaptureItem = null;
            pendingCaptureValidator = null;
            pendingCaptureAssign = null;
            pendingCaptureAcceptedValuesText = null;
        }

        // Escape and Back are never bindings: The press ends the capture and keeps Settings open through the menu's own close handling
        // Escape also blocks the pause menu until it is released
        private void CancelKeyCapture(bool escapePressed)
        {
            navigation.NativeCloseExpected = true;
            if (escapePressed) navigation.EscapeBlock = EscapeBlockPhase.UntilRelease;
            DiscardKeyCapture();
        }

        // The part of a cancel every path shares: Moving the highlight off the armed row ends the capture the same way Escape does
        private void DiscardKeyCapture()
        {
            notifications.RemoveNotification(NOTIFICATION_KEY_KEY_CAPTURE_ARMED);
            ClearKeyCapture();
            notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
        }

        // Each Description states what its own element draws, rather than the shared pipe syntax text the combined ShowHandlingVisualization INI comment carries
        private struct VisualizationCheckboxEntry
        {
            public string Title;
            public string Description;
            public HandlingVisualizationElements Flag;
        }

        private static readonly VisualizationCheckboxEntry[] VisualizationCheckboxes =
        {
            new VisualizationCheckboxEntry { Title = TITLE_VIS_BOUNDING_BOX, Description = DESC_VIS_BOUNDING_BOX, Flag = HandlingVisualizationElements.Bounding },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_WHEEL_BOUNDS, Description = DESC_VIS_WHEEL_BOUNDS, Flag = HandlingVisualizationElements.WheelBounds },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_SUBMERGED_HEIGHT, Description = DESC_VIS_SUBMERGED_HEIGHT, Flag = HandlingVisualizationElements.Submerged },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_CENTER_OF_MASS, Description = DESC_VIS_CENTER_OF_MASS, Flag = HandlingVisualizationElements.COM },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_CENTER_OF_GRAVITY, Description = DESC_VIS_CENTER_OF_GRAVITY, Flag = HandlingVisualizationElements.CenterOfGravity },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_ANTI_ROLL_BAR, Description = DESC_VIS_ANTI_ROLL_BAR, Flag = HandlingVisualizationElements.AntiRollBarBias },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_SUSPENSION_LOAD, Description = DESC_VIS_SUSPENSION_LOAD, Flag = HandlingVisualizationElements.SuspensionLoad },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_ROLL_CENTER_HEIGHTS, Description = DESC_VIS_ROLL_CENTER_HEIGHTS, Flag = HandlingVisualizationElements.RollCenter },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_WATER_SAMPLES, Description = DESC_VIS_WATER_SAMPLES, Flag = HandlingVisualizationElements.WaterSamples },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_RUDDER_OFFSETS, Description = DESC_VIS_RUDDER_OFFSETS, Flag = HandlingVisualizationElements.RudderOffsets },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_PROP_RADIUS, Description = DESC_VIS_PROP_RADIUS, Flag = HandlingVisualizationElements.PropRadius },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_KEEL_SPHERES, Description = DESC_VIS_KEEL_SPHERES, Flag = HandlingVisualizationElements.KeelSpheres },
            new VisualizationCheckboxEntry { Title = TITLE_VIS_IMPELLER, Description = DESC_VIS_IMPELLER, Flag = HandlingVisualizationElements.Impeller },
        };

        private static readonly string[] MaxRowsLabels = BuildMaxRowsLabels();
        private static string[] BuildMaxRowsLabels()
        {
            string[] labels = new string[MENU_MAX_ROWS_MAX - MENU_MAX_ROWS_MIN + 1];
            for (int i = 0; i < labels.Length; i++) labels[i] = (MENU_MAX_ROWS_MIN + i).ToString();
            return labels;
        }

        // Each item writes its field and calls WriteDefaultIni on change
        // Groups mirror the INI sections: The unlabeled key rows ([Controls]), Script, Exporting, Handling Preview, Visualization, Appearance, and Debug
        // The About row comes last
        private void BuildSettingsMenuItems()
        {
            // A capture armed from either item below is only meaningful while Settings stays open and its row stays highlighted
            // Any way of backing out (Back, the Escape or pause reopen, the toggle key hiding every menu) fires Closed and cancels it
            // Moving the highlight to another row cancels it too, with the canceled toast
            // This keeps a player who backs out mid-rebind from leaving a poll silently armed to hijack their next unrelated Ctrl, Shift, Alt, or Tab press
            SubscribeClosed(settingsMenu, "settingsMenu.Closed", () => { if (pendingKeyCapture != PendingKeyCapture.None) ClearKeyCapture(); });
            SubscribeSelectedIndexChanged(settingsMenu, "settingsMenu.SelectedIndexChanged", e => { if (pendingKeyCapture != PendingKeyCapture.None && settingsMenu.SelectedItem != pendingCaptureItem) DiscardKeyCapture(); });
            menuActionModifierItem = new NativeItem(TITLE_ACTION_MODIFIER, CMT_CTL_MenuActionModifier1 + ". " + CMT_CTL_MenuActionModifier2);
            menuActionModifierItem.AltTitle = menuActionModifier.ToString();
            settingsMenu.Add(menuActionModifierItem);
            SubscribeActivated(menuActionModifierItem, "menuActionModifierItem.Activated", () => ArmKeyCapture(PendingKeyCapture.ActionModifier, menuActionModifierItem, IsActionModifier, capturedKey => menuActionModifier = capturedKey, LABEL_ACCEPTED_ACTION_MODIFIER));
            keyToggleMenuItem = new NativeItem(TITLE_TOGGLE_MENU_KEY, CMT_CTL_KeyToggleMenu);
            settingsMenu.Add(keyToggleMenuItem);
            CaptureOriginalItemColor(keyToggleMenuItem);
            SubscribeActivated(keyToggleMenuItem, "keyToggleMenuItem.Activated", () => ArmKeyCapture(PendingKeyCapture.ToggleKey, keyToggleMenuItem, IsBindableToggleKey, capturedKey => keyToggleMenu = capturedKey, LABEL_ACCEPTED_TOGGLE_KEY));
            enableCheatStringItem = new NativeItem(TITLE_CHEAT_STRING, CMT_CTL_EnableCheatString);
            settingsMenu.Add(enableCheatStringItem);
            CaptureOriginalItemColor(enableCheatStringItem);
            // A blank or canceled prompt changes nothing, since only Clear Input empties the cheat string
            SubscribeActivated(enableCheatStringItem, "enableCheatStringItem.Activated", () =>
            {
                string input = Game.GetUserInput(WindowTitle.EnterMessage20, enableCheatString, INPUT_MAX_LENGTH);
                if (string.IsNullOrEmpty(input) || input.Trim().Length == 0)
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                enableCheatString = input.Trim();
                SyncEntryPointRows();
                WriteDefaultIni();
            });
            SyncEntryPointRows();
            settingsScriptSep = new StyledSeparator(TITLE_SECTION_SCRIPT, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsScriptSep);
            autoRespawnItem = new NativeListItem<string>(TITLE_AUTO_RESPAWN, CMT_SCR_AutoRespawn1 + ". " + AutoRespawnDelayedComment(), Enum.GetNames(typeof(AutoRespawnMode)));
            autoRespawnItem.SelectedItem = autoRespawnMode.ToString();
            autoRespawnItem.ArrowsAlwaysVisible = true;
            settingsMenu.Add(autoRespawnItem);
            SubscribeItemChanged(autoRespawnItem, "autoRespawnItem.ItemChanged", e =>
            {
                autoRespawnMode = (AutoRespawnMode)Enum.Parse(typeof(AutoRespawnMode), autoRespawnItem.SelectedItem);
                if (autoRespawnMode == AutoRespawnMode.Off) ClearPendingAutoRespawn();
                WriteDefaultIni();
            });
            checkForUpdatesItem = new NativeCheckboxItem(TITLE_CHECK_FOR_UPDATES, CMT_SCR_CheckForUpdates, checkForUpdates);
            settingsMenu.Add(checkForUpdatesItem);
            // An INI edit and a reset reach this handler too, through SyncSettingsMenuItems
            // So every way the setting turns on checks at once when this launch has no result yet
            SubscribeCheckboxChanged(checkForUpdatesItem, "checkForUpdatesItem.CheckboxChanged", () =>
            {
                checkForUpdates = checkForUpdatesItem.Checked;
                WriteDefaultIni();
                if (checkForUpdates) updateChecker.Start();
            });
            reassertDriftedFieldsItem = new NativeCheckboxItem(TITLE_REASSERT_DRIFTED_FIELDS, CMT_SCR_ReassertDriftedFields, reassertDriftedFields);
            settingsMenu.Add(reassertDriftedFieldsItem);
            SubscribeCheckboxChanged(reassertDriftedFieldsItem, "reassertDriftedFieldsItem.CheckboxChanged", () =>
            {
                reassertDriftedFields = reassertDriftedFieldsItem.Checked;
                WriteDefaultIni();
            });
            resetAllSettingsItem = new NativeCheckboxItem(TITLE_RESET_ALL_SETTINGS, CMT_SCR_ResetAllSettings, resetAllSettings);
            settingsMenu.Add(resetAllSettingsItem);
            // First press arms and second press confirms
            // The checkbox is never a persistent toggle: It always reverts to unchecked at once, since the API flips Checked before this handler runs
            // The arm or confirm decision reads armedItem and armedActionKey, never Checked, since Checked is already False by the time either branch is reached
            SubscribeCheckboxChanged(resetAllSettingsItem, "resetAllSettingsItem.CheckboxChanged", () =>
            {
                if (!resetAllSettingsItem.Checked) return;
                resetAllSettingsItem.Checked = false;
                ArmOrConfirm(resetAllSettingsItem, settingsMenu, "ResetAllSettings", PerformSettingsReset);
            });
            settingsExportingSep = new StyledSeparator(TITLE_SECTION_EXPORTING, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsExportingSep);
            alwaysOverwriteItem = new NativeCheckboxItem(TITLE_ALWAYS_OVERWRITE, CMT_EXG_AlwaysOverwrite, alwaysOverwrite);
            settingsMenu.Add(alwaysOverwriteItem);
            CaptureOriginalItemColor(alwaysOverwriteItem);
            SubscribeCheckboxChanged(alwaysOverwriteItem, "alwaysOverwriteItem.CheckboxChanged", () =>
            {
                alwaysOverwrite = alwaysOverwriteItem.Checked;
                WriteDefaultIni();
                SyncSettingsWarningColors();
            });
            promptOnSaveItem = new NativeCheckboxItem(TITLE_PROMPT_ON_SAVE, DESC_PROMPT_ON_SAVE, promptOnSave);
            settingsMenu.Add(promptOnSaveItem);
            CaptureOriginalItemColor(promptOnSaveItem);
            SubscribeCheckboxChanged(promptOnSaveItem, "promptOnSaveItem.CheckboxChanged", () =>
            {
                promptOnSave = promptOnSaveItem.Checked;
                WriteDefaultIni();
                SyncSettingsWarningColors();
            });
            SyncSettingsWarningColors();
            autosaveItem = new NativeCheckboxItem(TITLE_AUTOSAVE, CMT_EXG_Autosave, autosaveEnabled);
            settingsMenu.Add(autosaveItem);
            SubscribeCheckboxChanged(autosaveItem, "autosaveItem.CheckboxChanged", () =>
            {
                bool wasEnabled = autosaveEnabled;
                autosaveEnabled = autosaveItem.Checked;
                if (!autosaveEnabled) pendingAutosaveHandlings.Clear();
                // Ticking it on from the menu writes edited handlings' autosaves at once, while a sync after an INI reload already holds the new value and writes nothing
                else if (!wasEnabled) MarkEditedHandlingsAutosavePending();
                WriteDefaultIni();
            });
            settingsHandlingPreviewSep = new StyledSeparator(TITLE_SECTION_HANDLING_PREVIEW, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsHandlingPreviewSep);
            previewByDefaultItem = new NativeCheckboxItem(TITLE_PREVIEW_BY_DEFAULT, CMT_HPV_PreviewByDefault, previewByDefault);
            settingsMenu.Add(previewByDefaultItem);
            SubscribeCheckboxChanged(previewByDefaultItem, "previewByDefaultItem.CheckboxChanged", () =>
            {
                previewByDefault = previewByDefaultItem.Checked;
                WriteDefaultIni();
            });
            showPreviewDiffItem = new NativeCheckboxItem(TITLE_SHOW_PREVIEW_DIFF, CMT_HPV_ShowPreviewDiff, showPreviewDiff);
            settingsMenu.Add(showPreviewDiffItem);
            // Toggling off with Diff already on-screen must hide it immediately, not wait for the next unrelated Preview refresh
            SubscribeCheckboxChanged(showPreviewDiffItem, "showPreviewDiffItem.CheckboxChanged", () =>
            {
                showPreviewDiff = showPreviewDiffItem.Checked;
                WriteDefaultIni();
                SyncDiffVisibility();
            });
            settingsVisualizationSep = new StyledSeparator(TITLE_SECTION_VISUALIZATION, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsVisualizationSep);
            isolatedDrawingItem = new NativeCheckboxItem(TITLE_VIS_ISOLATED_DRAWING, CMT_VIS_IsolatedDrawing, isolatedDrawing);
            settingsMenu.Add(isolatedDrawingItem);
            SubscribeCheckboxChanged(isolatedDrawingItem, "isolatedDrawingItem.CheckboxChanged", () =>
            {
                isolatedDrawing = isolatedDrawingItem.Checked;
                WriteDefaultIni();
            });
            visualizationCheckboxItems = new NativeCheckboxItem[VisualizationCheckboxes.Length];
            for (int i = 0; i < VisualizationCheckboxes.Length; i++)
            {
                VisualizationCheckboxEntry entry = VisualizationCheckboxes[i];
                HandlingVisualizationElements flag = entry.Flag;
                var checkboxItem = new NativeCheckboxItem(entry.Title, entry.Description, (showHandlingVisualization & flag) != 0);
                settingsMenu.Add(checkboxItem);
                SubscribeCheckboxChanged(checkboxItem, "visualizationCheckboxItems.CheckboxChanged", () =>
                {
                    if (checkboxItem.Checked) showHandlingVisualization |= flag;
                    else showHandlingVisualization &= ~flag;
                    WriteDefaultIni();
                });
                visualizationCheckboxItems[i] = checkboxItem;
            }
            settingsAppearanceSep = new StyledSeparator(TITLE_SECTION_APPEARANCE, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsAppearanceSep);
            bannerColorItem = new NativeListItem<string>(TITLE_BANNER_COLOR, CMT_APR_BannerColorRGB, BannerColorPresetNames);
            bannerColorItem.SelectedIndex = ResolveBannerPresetIndex();
            bannerColorItem.ArrowsAlwaysVisible = true;
            settingsMenu.Add(bannerColorItem);
            // SyncSettingsMenuItems sets each appearance row after an INI reload, which fires its handler
            // A handler whose value already matches returns, so the reload's own single apply is the only one
            SubscribeItemChanged(bannerColorItem, "bannerColorItem.ItemChanged", e =>
            {
                int index = bannerColorItem.SelectedIndex;
                bool isPreset = index < BANNER_COLOR_CUSTOM_INDEX;
                int red = isPreset ? BannerColorPresetRgb[index, 0] : customBannerR;
                int green = isPreset ? BannerColorPresetRgb[index, 1] : customBannerG;
                int blue = isPreset ? BannerColorPresetRgb[index, 2] : customBannerB;
                if (red == menuBannerR && green == menuBannerG && blue == menuBannerB) return;
                menuBannerR = red;
                menuBannerG = green;
                menuBannerB = blue;
                ApplyMenuTheme();
                WriteDefaultIni();
            });
            SubscribeActivated(bannerColorItem, "bannerColorItem.Activated", () =>
            {
                if (bannerColorItem.SelectedItem != LABEL_BANNER_CUSTOM) return;
                string input = Game.GetUserInput(WindowTitle.EnterMessage20, customBannerR + ", " + customBannerG + ", " + customBannerB, RGB_INPUT_MAX_LENGTH);
                if (string.IsNullOrEmpty(input))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                int r, g, b;
                if (!ParseRgb(input, out r, out g, out b))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_INVALID_BANNER_COLOR, NOTIF_BANNER_COLOR_INVALID);
                    return;
                }
                menuBannerR = Math.Max(0, Math.Min(255, r));
                menuBannerG = Math.Max(0, Math.Min(255, g));
                menuBannerB = Math.Max(0, Math.Min(255, b));
                customBannerR = menuBannerR;
                customBannerG = menuBannerG;
                customBannerB = menuBannerB;
                ApplyMenuTheme();
                WriteDefaultIni();
            });
            menuAlignItem = new NativeListItem<string>(TITLE_MENU_POSITION, CMT_APR_MenuPosition, new string[] { LABEL_POSITION_RIGHT, LABEL_POSITION_LEFT });
            menuAlignItem.SelectedItem = menuAlignLeft ? LABEL_POSITION_LEFT : LABEL_POSITION_RIGHT;
            menuAlignItem.ArrowsAlwaysVisible = true;
            settingsMenu.Add(menuAlignItem);
            SubscribeItemChanged(menuAlignItem, "menuAlignItem.ItemChanged", e =>
            {
                bool alignLeft = menuAlignItem.SelectedItem == LABEL_POSITION_LEFT;
                if (alignLeft == menuAlignLeft) return;
                menuAlignLeft = alignLeft;
                ApplyMenuPosition();
                WriteDefaultIni();
            });
            maxRowsItem = new NativeListItem<string>(TITLE_MAX_ROWS, CMT_APR_MaxRows1 + ". " + CMT_APR_MaxRows2, MaxRowsLabels);
            maxRowsItem.SelectedIndex = menuMaxRows - MENU_MAX_ROWS_MIN;
            maxRowsItem.ArrowsAlwaysVisible = true;
            settingsMenu.Add(maxRowsItem);
            SubscribeItemChanged(maxRowsItem, "maxRowsItem.ItemChanged", e =>
            {
                int maxRows = MENU_MAX_ROWS_MIN + maxRowsItem.SelectedIndex;
                if (maxRows == menuMaxRows) return;
                menuMaxRows = maxRows;
                ApplyMenuPosition();
                WriteDefaultIni();
            });
            settingsDebugSep = new StyledSeparator(TITLE_SECTION_DEBUG, GTA.UI.Font.HouseScript, SEPARATOR_SCALE);
            settingsMenu.Add(settingsDebugSep);
            loggingModeItem = new NativeListItem<string>(TITLE_LOGGING_VERBOSITY, DESC_LOGGING_VERBOSITY, LogModeLabels);
            loggingModeItem.SelectedIndex = Math.Max(0, Math.Min(LogModeLabels.Length - 1, loggingMode));
            loggingModeItem.ArrowsAlwaysVisible = true;
            settingsMenu.Add(loggingModeItem);
            SubscribeItemChanged(loggingModeItem, "loggingModeItem.ItemChanged", e =>
            {
                loggingMode = loggingModeItem.SelectedIndex;
                WriteDefaultIni();
            });
            alwaysClearLogItem = new NativeCheckboxItem(TITLE_CLEAR_LOG_ON_RELOAD, CMT_DBG_AlwaysClearLogOnReload, alwaysClearLogOnReload);
            settingsMenu.Add(alwaysClearLogItem);
            SubscribeCheckboxChanged(alwaysClearLogItem, "alwaysClearLogItem.CheckboxChanged", () =>
            {
                alwaysClearLogOnReload = alwaysClearLogItem.Checked;
                WriteDefaultIni();
            });
            NativeItem aboutItem = new NativeItem(TITLE_ABOUT, DESC_ABOUT);
            aboutItem.Enabled = false;
            settingsMenu.Add(aboutItem);

            // Toggle Menu Key has an empty default and keeps only Clear Input, and Reset All Settings is an action
            // The visualization checkboxes reset through their own path (ResetHighlightedRowToDefault)
            resettableRowKeys = new Dictionary<NativeItem, string>
            {
                { menuActionModifierItem, KEY_CTL_MenuActionModifier }, { enableCheatStringItem, KEY_CTL_EnableCheatString },
                { autoRespawnItem, KEY_SCR_AutoRespawn }, { checkForUpdatesItem, KEY_SCR_CheckForUpdates }, { reassertDriftedFieldsItem, KEY_SCR_ReassertDriftedFields },
                { alwaysOverwriteItem, KEY_EXG_AlwaysOverwrite }, { promptOnSaveItem, KEY_EXG_PromptOnSave }, { autosaveItem, KEY_EXG_Autosave },
                { previewByDefaultItem, KEY_HPV_PreviewByDefault }, { showPreviewDiffItem, KEY_HPV_ShowPreviewDiff },
                { isolatedDrawingItem, KEY_VIS_IsolatedDrawing },
                { bannerColorItem, KEY_APR_BannerColorRGB }, { menuAlignItem, KEY_APR_MenuPosition }, { maxRowsItem, KEY_APR_MaxRows },
                { loggingModeItem, KEY_DBG_LoggingMode }, { alwaysClearLogItem, KEY_DBG_AlwaysClearLogOnReload }
            };
        }

        // Settings rows Reset To Default returns to their compiled default, each with its INI key. Null until the Settings rows are built
        private Dictionary<NativeItem, string> resettableRowKeys;

        // True while Settings has a resettable row highlighted and that row's value differs from its compiled default, compared with exact case
        // A visualization checkbox compares only its own element bit
        // INI rows read lastSettingsSnapshot, which every successful INI write refreshes, so the hint follows the file
        private bool IsResettableRowHighlighted()
        {
            if (resettableRowKeys == null || !settingsMenu.Visible || settingsMenu.SelectedItem == null) return false;
            NativeItem selected = settingsMenu.SelectedItem;
            // A disabled row offers no action, so it offers no reset either
            if (!selected.Enabled) return false;
            int visualizationIndex = Array.IndexOf(visualizationCheckboxItems, selected);
            if (visualizationIndex >= 0)
            {
                HandlingVisualizationElements flag = VisualizationCheckboxes[visualizationIndex].Flag;
                return (showHandlingVisualization & flag) != (defaultHandlingVisualization & flag);
            }
            string key, defaultValue;
            if (!resettableRowKeys.TryGetValue(selected, out key) || !defaultIniValues.TryGetValue(key, out defaultValue)) return false;
            if (lastSettingsSnapshot == null) return false;
            for (int i = 0; i < lastSettingsSnapshot.Count; i++) if (lastSettingsSnapshot[i].Key == key) return !string.Equals(lastSettingsSnapshot[i].Value, defaultValue, StringComparison.Ordinal);
            return false;
        }

        // Applies the highlighted row's default through ApplyValues, the same path an INI edit takes, so every validator runs
        // A visualization checkbox resets only its own element bit
        // The row sync below fires each row's own change handler, so only Autosave's marking of edited handlings needs its own call
        private void ResetHighlightedRowToDefault()
        {
            NativeItem selected = settingsMenu.SelectedItem;
            if (selected == null || resettableRowKeys == null) return;
            string key, defaultValue;
            int visualizationIndex = Array.IndexOf(visualizationCheckboxItems, selected);
            if (visualizationIndex >= 0)
            {
                HandlingVisualizationElements flag = VisualizationCheckboxes[visualizationIndex].Flag;
                key = KEY_VIS_ShowHandlingVisualization;
                defaultValue = ((showHandlingVisualization & ~flag) | (defaultHandlingVisualization & flag)).ToString().Replace(", ", " | ");
            }
            else if (!resettableRowKeys.TryGetValue(selected, out key) || !defaultIniValues.TryGetValue(key, out defaultValue)) return;
            bool autosaveWasEnabled = autosaveEnabled;
            string previousPositionKey = MenuPositionKey();
            string previousThemeKey = MenuThemeKey();
            iniRejectedKeys.Clear();
            ApplyValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { key, defaultValue } });
            ApplyChangedMenuAppearance(previousPositionKey, previousThemeKey);
            bool rejected = iniRejectedKeys.Count > 0;
            iniRejectedKeys.Clear();
            // ApplyValues already logged the refusal and left the value as it was
            if (rejected) return;
            logger.Log(Dbg.Level.Info, Dbg.ResetToDefault(key, defaultValue));
            if (autosaveEnabled && !autosaveWasEnabled) MarkEditedHandlingsAutosavePending();
            SyncSettingsMenuItems();
            StartResultFlash(selected, Color.LightGreen);
            WriteDefaultIni();
        }

        private void SyncSettingsMenuItems()
        {
            SyncEntryPointRows();
            if (menuActionModifierItem != null) menuActionModifierItem.AltTitle = menuActionModifier.ToString();
            if (visualizationCheckboxItems != null)
            {
                for (int i = 0; i < visualizationCheckboxItems.Length; i++) visualizationCheckboxItems[i].Checked = (showHandlingVisualization & VisualizationCheckboxes[i].Flag) != 0;
            }
            if (loggingModeItem != null) loggingModeItem.SelectedIndex = Math.Max(0, Math.Min(LogModeLabels.Length - 1, loggingMode));
            if (alwaysClearLogItem != null) alwaysClearLogItem.Checked = alwaysClearLogOnReload;
            if (alwaysOverwriteItem != null) alwaysOverwriteItem.Checked = alwaysOverwrite;
            if (promptOnSaveItem != null) promptOnSaveItem.Checked = promptOnSave;
            if (autosaveItem != null) autosaveItem.Checked = autosaveEnabled;
            if (previewByDefaultItem != null) previewByDefaultItem.Checked = previewByDefault;
            if (showPreviewDiffItem != null) showPreviewDiffItem.Checked = showPreviewDiff;
            if (isolatedDrawingItem != null) isolatedDrawingItem.Checked = isolatedDrawing;
            if (checkForUpdatesItem != null) checkForUpdatesItem.Checked = checkForUpdates;
            if (reassertDriftedFieldsItem != null) reassertDriftedFieldsItem.Checked = reassertDriftedFields;
            if (autoRespawnItem != null) autoRespawnItem.SelectedItem = autoRespawnMode.ToString();
            if (resetAllSettingsItem != null) resetAllSettingsItem.Checked = resetAllSettings;
            if (bannerColorItem != null) bannerColorItem.SelectedIndex = ResolveBannerPresetIndex();
            if (menuAlignItem != null) menuAlignItem.SelectedItem = menuAlignLeft ? LABEL_POSITION_LEFT : LABEL_POSITION_RIGHT;
            if (maxRowsItem != null) maxRowsItem.SelectedIndex = menuMaxRows - MENU_MAX_ROWS_MIN;
            SyncSettingsWarningColors();
        }

        // An empty entry point row is grayed in every state but stays Enabled, so Enter still opens capture or the prompt
        // Null-guarded for the constructor's INI load, which runs before the rows exist
        private void SyncEntryPointRows()
        {
            if (keyToggleMenuItem == null || enableCheatStringItem == null) return;
            keyToggleMenuItem.AltTitle = FormatToggleKey(keyToggleMenu);
            enableCheatStringItem.AltTitle = enableCheatString;
            ApplyItemStateColor(keyToggleMenuItem, keyToggleMenu == Keys.None, capturedItemColors[keyToggleMenuItem].TitleDisabled);
            ApplyItemStateColor(enableCheatStringItem, string.IsNullOrEmpty(enableCheatString), capturedItemColors[enableCheatStringItem].TitleDisabled);
        }

        // True while a highlighted entry point row holds a value and clearing it leaves the other entry point set, which ClearHighlightedEntryPoint accepts
        private bool IsClearableRowHighlighted()
        {
            if (keyToggleMenuItem == null || !settingsMenu.Visible) return false;
            NativeItem selected = settingsMenu.SelectedItem;
            if (selected == keyToggleMenuItem) return keyToggleMenu != Keys.None && !string.IsNullOrEmpty(enableCheatString);
            if (selected == enableCheatStringItem) return !string.IsNullOrEmpty(enableCheatString) && keyToggleMenu != Keys.None;
            return false;
        }

        // Empties the highlighted entry point, refused when the other one is already empty since the menu always needs an entry point
        private void ClearHighlightedEntryPoint()
        {
            NativeItem selected = settingsMenu.SelectedItem;
            if (selected == keyToggleMenuItem)
            {
                if (WouldRemoveLastMenuEntryPoint(Keys.None, enableCheatString)) return;
                keyToggleMenu = Keys.None;
            }
            else if (selected == enableCheatStringItem)
            {
                if (WouldRemoveLastMenuEntryPoint(keyToggleMenu, "")) return;
                enableCheatString = "";
            }
            else return;
            SyncEntryPointRows();
            StartResultFlash(selected, Color.LightGreen);
            WriteDefaultIni();
        }

        // Prompt On Save turns red only combined with Always Overwrite on, the dangerous combination of a silent overwrite with no prompt to catch it
        // Null-guarded for the constructor's reset path, which runs before the rows exist
        private void SyncSettingsWarningColors()
        {
            if (promptOnSaveItem == null || alwaysOverwriteItem == null) return;
            ApplyItemStateColor(promptOnSaveItem, !promptOnSave && alwaysOverwrite, SETTINGS_WARNING_COLOR);
            ApplyItemStateColor(alwaysOverwriteItem, alwaysOverwrite, SETTINGS_WARNING_COLOR);
        }

        private void ApplyMenuTheme()
        {
            if (allMenus == null) return;
            Color color, textColor;
            ResolveMenuThemeColors(out color, out textColor);
            foreach (NativeMenu menu in allMenus) ApplyMenuThemeTo(menu, color, textColor);
            if (previewMenu != null) ApplyMenuThemeTo(previewMenu, color, textColor);
            // previewCategoryData is always non-null (initialized inline), unlike previewMenu and previewWeaponSlotMenus, which stay null until BuildMenu runs
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewCategoryData) ApplyMenuThemeTo(data.Value.Menu, color, textColor);
            if (diffMenu != null) ApplyMenuThemeTo(diffMenu, color, textColor);
            foreach (KeyValuePair<string, PreviewCategoryData> data in previewDiffCategoryData) ApplyMenuThemeTo(data.Value.Menu, color, textColor);
        }

        // Shared by ApplyMenuTheme and a menu built after startup (RegisterRuntimeMenu), so both resolve the same colors
        private void ResolveMenuThemeColors(out Color color, out Color textColor)
        {
            color = Color.FromArgb(255, menuBannerR, menuBannerG, menuBannerB);
            // 0.2126/0.7152/0.0722 are the standard ITU-R BT.709 relative-luminance weighting coefficients
            float luminance = (0.2126f * menuBannerR + 0.7152f * menuBannerG + 0.0722f * menuBannerB) / 255.0f;
            textColor = luminance > BANNER_TEXT_LUMINANCE_THRESHOLD ? Color.Black : Color.White;
        }

        private static void ApplyMenuThemeTo(NativeMenu menu, Color bannerColor, Color textColor)
        {
            if (menu.Banner != null) menu.Banner.Color = bannerColor;
            if (menu.BannerText != null) menu.BannerText.Color = textColor;
        }

        private int ResolveBannerPresetIndex()
        {
            for (int i = 0; i < BannerColorPresetRgb.GetLength(0); i++) if (BannerColorPresetRgb[i, 0] == menuBannerR && BannerColorPresetRgb[i, 1] == menuBannerG && BannerColorPresetRgb[i, 2] == menuBannerB) return i;
            return BANNER_COLOR_CUSTOM_INDEX;
        }
    }
}
