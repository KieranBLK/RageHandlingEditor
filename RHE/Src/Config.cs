using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // =============
        // Config fields
        // =============
        private Keys keyToggleMenu;
        private string enableCheatString;
        private Keys menuActionModifier;
        private AutoRespawnMode autoRespawnMode;
        private bool resetAllSettings;
        private HandlingVisualizationElements showHandlingVisualization;
        private bool isolatedDrawing;
        private int menuBannerR;
        private int menuBannerG;
        private int menuBannerB;
        private int customBannerR;
        private int customBannerG;
        private int customBannerB;
        private bool menuAlignLeft;
        private int menuMaxRows;
        private bool alwaysOverwrite;
        private bool promptOnSave;
        private bool autosaveEnabled;
        private bool previewByDefault;
        private bool showPreviewDiff;
        private bool reassertDriftedFields;
        private bool liveSubHandlingData;
        private int loggingMode;
        private bool alwaysClearLogOnReload;

        private string iniPath;
        private DateTime iniLastWriteTime;
        private string iniLastContent;
        private List<KeyValuePair<string, string>> lastSettingsSnapshot;
        private bool iniRejectionOccurred;
        private List<string> iniRejectedKeys = new List<string>();
        // Every key's compiled default as written to the INI. Captured once by CaptureIniDefaults, before the INI load replaces the fields
        private Dictionary<string, string> defaultIniValues;
        // ShowHandlingVisualization's compiled default, so a visualization checkbox resets only its own element bit
        private HandlingVisualizationElements defaultHandlingVisualization;

        // False until one full load has produced valid field values. A rejection before that has no last-good value to fall back to, so the compiled default stands
        private bool iniLastGoodAvailable;

        private void ApplyIniDefaults()
        {
            menuActionModifier = Keys.Tab;
            keyToggleMenu = Keys.None;
            enableCheatString = DEFAULT_CHEAT_STRING;
            autoRespawnMode = AutoRespawnMode.Delayed;
            resetAllSettings = false;
            showHandlingVisualization = HandlingVisualizationElements.All;
            isolatedDrawing = false;
            menuBannerR = BANNER_DEFAULT_RED;
            menuBannerG = BANNER_DEFAULT_GREEN;
            menuBannerB = BANNER_DEFAULT_BLUE;
            customBannerR = BANNER_DEFAULT_RED;
            customBannerG = BANNER_DEFAULT_GREEN;
            customBannerB = BANNER_DEFAULT_BLUE;
            menuAlignLeft = true;
            menuMaxRows = MENU_MAX_ROWS_DEFAULT;
            alwaysOverwrite = false;
            promptOnSave = true;
            autosaveEnabled = true;
            previewByDefault = true;
            showPreviewDiff = true;
            reassertDriftedFields = true;
            liveSubHandlingData = false;
            loggingMode = LOGGING_MODE_DEFAULT;
            alwaysClearLogOnReload = true;
        }

        // The constructor's first INI step
        private void CaptureIniDefaults()
        {
            ApplyIniDefaults();
            defaultHandlingVisualization = showHandlingVisualization;
            defaultIniValues = ParseIniScalarLines(SplitLines(BuildIniEntries()), true);
        }

        // Canonical INI text from current in-memory values. Structural source of truth for reconciliation
        private string BuildIniEntries()
        {
            var sb = new StringBuilder();
            sb.AppendLine(SECTION_Controls);
            AppendIniComment(sb, CMT_CTL_MenuActionModifier1);
            AppendIniComment(sb, CMT_CTL_MenuActionModifier2);
            sb.AppendLine(KEY_CTL_MenuActionModifier + " = " + menuActionModifier);
            AppendIniComment(sb, CMT_CTL_EnableCheatString);
            sb.AppendLine(KEY_CTL_EnableCheatString + " = " + enableCheatString);
            AppendIniComment(sb, CMT_CTL_KeyToggleMenu);
            sb.AppendLine(KEY_CTL_KeyToggleMenu + " = " + FormatToggleKey(keyToggleMenu));
            sb.AppendLine();
            sb.AppendLine(SECTION_Script);
            AppendIniComment(sb, CMT_SCR_AutoRespawn1);
            AppendIniComment(sb, AutoRespawnDelayedComment());
            sb.AppendLine(KEY_SCR_AutoRespawn + " = " + autoRespawnMode);
            AppendIniComment(sb, CMT_SCR_ReassertDriftedFields);
            sb.AppendLine(KEY_SCR_ReassertDriftedFields + " = " + FormatIniBool(reassertDriftedFields));
            sb.AppendLine();
            AppendIniComment(sb, CMT_SCR_ResetAllSettings);
            sb.AppendLine(KEY_SCR_ResetAllSettings + " = " + FormatIniBool(resetAllSettings));
            sb.AppendLine();
            sb.AppendLine(SECTION_Exporting);
            AppendIniComment(sb, CMT_EXG_AlwaysOverwrite);
            sb.AppendLine(KEY_EXG_AlwaysOverwrite + " = " + FormatIniBool(alwaysOverwrite));
            AppendIniComment(sb, CMT_EXG_Autosave);
            sb.AppendLine(KEY_EXG_Autosave + " = " + FormatIniBool(autosaveEnabled));
            AppendIniComment(sb, CMT_EXG_PromptOnSave);
            sb.AppendLine(KEY_EXG_PromptOnSave + " = " + FormatIniBool(promptOnSave));
            sb.AppendLine();
            sb.AppendLine(SECTION_HandlingPreview);
            AppendIniComment(sb, CMT_HPV_PreviewByDefault);
            sb.AppendLine(KEY_HPV_PreviewByDefault + " = " + FormatIniBool(previewByDefault));
            AppendIniComment(sb, CMT_HPV_ShowPreviewDiff);
            sb.AppendLine(KEY_HPV_ShowPreviewDiff + " = " + FormatIniBool(showPreviewDiff));
            sb.AppendLine();
            sb.AppendLine(SECTION_Visualization);
            AppendIniComment(sb, CMT_VIS_IsolatedDrawing);
            sb.AppendLine(KEY_VIS_IsolatedDrawing + " = " + FormatIniBool(isolatedDrawing));
            AppendIniComment(sb, CMT_VIS_ShowHandlingVisualization1);
            AppendIniComment(sb, CMT_VIS_ShowHandlingVisualization2);
            sb.AppendLine(KEY_VIS_ShowHandlingVisualization + " = " + showHandlingVisualization.ToString().Replace(", ", " | "));
            sb.AppendLine();
            sb.AppendLine(SECTION_Appearance);
            AppendIniComment(sb, CMT_APR_BannerColorRGB);
            sb.AppendLine(KEY_APR_BannerColorRGB + " = " + menuBannerR + ", " + menuBannerG + ", " + menuBannerB);
            AppendIniComment(sb, CMT_APR_MaxRows1);
            AppendIniComment(sb, CMT_APR_MaxRows2);
            sb.AppendLine(KEY_APR_MaxRows + " = " + menuMaxRows);
            AppendIniComment(sb, CMT_APR_MenuPosition);
            sb.AppendLine(KEY_APR_MenuPosition + " = " + (menuAlignLeft ? "Left" : "Right"));
            sb.AppendLine();
            sb.AppendLine(SECTION_Experimental);
            AppendIniComment(sb, CMT_EXP_LiveSubHandlingData);
            sb.AppendLine(KEY_EXP_LiveSubHandlingData + " = " + FormatIniBool(liveSubHandlingData));
            sb.AppendLine();
            sb.AppendLine(SECTION_Debug);
            AppendIniComment(sb, CMT_DBG_AlwaysClearLogOnReload);
            sb.AppendLine(KEY_DBG_AlwaysClearLogOnReload + " = " + FormatIniBool(alwaysClearLogOnReload));
            AppendIniComment(sb, CMT_DBG_LoggingMode);
            sb.AppendLine(KEY_DBG_LoggingMode + " = " + loggingMode);
            return sb.ToString();
        }

        // Every CMT_ constant holds plain text and gets its "; " prefix here
        private static void AppendIniComment(StringBuilder sb, string comment) { sb.Append("; ").AppendLine(comment); }

        // How every bool key is written, so the file reads True or False with a capital
        private static string FormatIniBool(bool value) { return value ? "True" : "False"; }

        // Splits on both line endings, since a file the player edits can hold either
        private static string[] SplitLines(string text) { return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None); }

        // CMT_SCR_AutoRespawn2 with the Delayed wait in seconds filled in from AUTO_RESPAWN_DELAY_MS, so the text can never drift from the real delay
        private static string AutoRespawnDelayedComment()
        {
            return string.Format(CMT_SCR_AutoRespawn2, (AUTO_RESPAWN_DELAY_MS / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        private void WriteDefaultIni()
        {
            try
            {
                List<KeyValuePair<string, string>> previousSnapshot = lastSettingsSnapshot;
                File.WriteAllText(iniPath, BuildIniEntries());
                try
                {
                    iniLastWriteTime = File.GetLastWriteTime(iniPath);
                    iniLastContent = File.ReadAllText(iniPath);
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.IniSnapshotFailed(ex.Message)); }
                List<KeyValuePair<string, string>> refreshedSnapshot = BuildSettingsSnapshot();
                LogSettingsDiff(previousSnapshot, refreshedSnapshot);
                lastSettingsSnapshot = refreshedSnapshot;
                logger.Log(Dbg.Level.Info, Dbg.IniWritten());
                iniLastGoodAvailable = true;
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.IniWriteFailed(ex.Message)); }
        }

        private static List<KeyValuePair<string, string>> ParseIniScalarPairs(IEnumerable<string> lines, bool keepEmptyValues)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0 && !keepEmptyValues) continue;
                pairs.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), value));
            }
            return pairs;
        }

        private static Dictionary<string, string> ParseIniScalarLines(IEnumerable<string> lines, bool keepEmptyValues)
        {
            var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> pairs = ParseIniScalarPairs(lines, keepEmptyValues);
            for (int i = 0; i < pairs.Count; i++) scalars[pairs[i].Key] = pairs[i].Value;
            return scalars;
        }

        private void ReadIni()
        {
            try { ApplyValues(ParseIniScalarLines(File.ReadAllLines(iniPath), false)); }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.IniReadFailed(ex.Message)); }
        }

        private void WarnIniRejection(string key, string message)
        {
            logger.Log(Dbg.Level.Warn, message);
            if (!iniRejectedKeys.Contains(key)) iniRejectedKeys.Add(key);
            iniRejectionOccurred = true;
        }

        // None is rejected on purpose, since every hinted hotkey is reachable only by holding this modifier
        // No GTA.Control exists for a held modifier alone, so the requirement cannot be surfaced as an instructional-button hint either
        private static bool IsActionModifier(Keys key) { return key == Keys.Control || key == Keys.Shift || key == Keys.Alt || key == Keys.Tab; }

        // Must be exactly one virtual key with no modifier flags, matching KeyEventArgs.KeyCode comparison
        // Tab is also excluded, matching IsActionModifier's own treatment of it as a modifier-class key elsewhere in Settings
        // Escape and Back are menu navigation keys, never bindings
        private static bool IsToggleKey(Keys key)
        {
            if (key == Keys.None) return true;
            if ((key & Keys.Modifiers) != Keys.None || (key & Keys.KeyCode) != key) return false;
            return key != Keys.Escape && key != Keys.Back &&
                key != Keys.ControlKey && key != Keys.LControlKey && key != Keys.RControlKey &&
                key != Keys.ShiftKey && key != Keys.LShiftKey && key != Keys.RShiftKey &&
                key != Keys.Menu && key != Keys.LMenu && key != Keys.RMenu && key != Keys.Tab;
        }

        // Capture and typed entry reject None. Unbinding goes through Clear Input instead
        private static bool IsBindableToggleKey(Keys key) { return key != Keys.None && IsToggleKey(key); }
        // An unbound toggle key is written and shown as an empty value
        private static string FormatToggleKey(Keys key) { return key == Keys.None ? "" : key.ToString(); }

        private static bool ParseRgb(string raw, out int r, out int g, out int b)
        {
            r = 0;
            g = 0;
            b = 0;
            string trimmed = raw.Trim();
            string hex = trimmed.TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return true;
                }
                catch { }
            }
            string[] parts = trimmed.Split(',');
            if (parts.Length == 3 && int.TryParse(parts[0].Trim(), out r) && int.TryParse(parts[1].Trim(), out g) && int.TryParse(parts[2].Trim(), out b)) return true;
            r = 0;
            g = 0;
            b = 0;
            return false;
        }

        // Parses a bool key onto its field. A value that is not True or False is rejected and leaves the field as it was
        private void ApplyIniBool(Dictionary<string, string> changedValues, string key, ref bool field)
        {
            string text;
            if (!changedValues.TryGetValue(key, out text)) return;
            bool parsed;
            if (bool.TryParse(text, out parsed)) field = parsed;
            else WarnIniRejection(key, Dbg.IniInvalidValue(key, text));
        }

        // Single registration point for all scalar config fields
        private void ApplyValues(Dictionary<string, string> changedValues)
        {
            string s;
            int n;
            if (changedValues.TryGetValue(KEY_CTL_KeyToggleMenu, out s))
            {
                // An empty value is the unbound state
                if (s.Length == 0) keyToggleMenu = Keys.None;
                else
                {
                    Keys parsedToggle;
                    try { parsedToggle = (Keys)Enum.Parse(typeof(Keys), s, true); }
                    catch
                    {
                        WarnIniRejection(KEY_CTL_KeyToggleMenu, Dbg.IniInvalidKeyName(KEY_CTL_KeyToggleMenu, s));
                        parsedToggle = keyToggleMenu;
                    }
                    if (IsToggleKey(parsedToggle)) keyToggleMenu = parsedToggle;
                    else WarnIniRejection(KEY_CTL_KeyToggleMenu, Dbg.IniInvalidValue(KEY_CTL_KeyToggleMenu, s));
                }
            }
            if (changedValues.TryGetValue(KEY_CTL_EnableCheatString, out s)) enableCheatString = s;
            if (changedValues.TryGetValue(KEY_CTL_MenuActionModifier, out s))
            {
                Keys parsedModifier;
                try { parsedModifier = (Keys)Enum.Parse(typeof(Keys), s, true); }
                catch
                {
                    WarnIniRejection(KEY_CTL_MenuActionModifier, Dbg.IniInvalidKeyName(KEY_CTL_MenuActionModifier, s));
                    parsedModifier = menuActionModifier;
                }
                if (IsActionModifier(parsedModifier)) menuActionModifier = parsedModifier;
                else WarnIniRejection(KEY_CTL_MenuActionModifier, Dbg.IniInvalidValue(KEY_CTL_MenuActionModifier, s));
            }
            if (changedValues.TryGetValue(KEY_SCR_AutoRespawn, out s))
            {
                // Only the 3 mode names parse, so a number or a saved True or False is rejected
                bool parsedModeValid = false;
                foreach (AutoRespawnMode mode in Enum.GetValues(typeof(AutoRespawnMode)))
                {
                    if (string.Equals(s, mode.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        autoRespawnMode = mode;
                        parsedModeValid = true;
                    }
                }
                if (!parsedModeValid) WarnIniRejection(KEY_SCR_AutoRespawn, Dbg.IniInvalidValue(KEY_SCR_AutoRespawn, s));
            }
            ApplyIniBool(changedValues, KEY_SCR_ResetAllSettings, ref resetAllSettings);
            ApplyIniBool(changedValues, KEY_VIS_IsolatedDrawing, ref isolatedDrawing);
            if (changedValues.TryGetValue(KEY_VIS_ShowHandlingVisualization, out s))
            {
                try
                {
                    HandlingVisualizationElements parsed = (HandlingVisualizationElements)Enum.Parse(typeof(HandlingVisualizationElements), s.Replace("|", ","), true);
                    if ((parsed & ~HandlingVisualizationElements.All) == 0) showHandlingVisualization = parsed;
                    else WarnIniRejection(KEY_VIS_ShowHandlingVisualization, Dbg.IniInvalidValue(KEY_VIS_ShowHandlingVisualization, s));
                }
                catch { WarnIniRejection(KEY_VIS_ShowHandlingVisualization, Dbg.IniInvalidValue(KEY_VIS_ShowHandlingVisualization, s)); }
            }
            if (changedValues.TryGetValue(KEY_APR_BannerColorRGB, out s))
            {
                int red, green, blue;
                if (ParseRgb(s, out red, out green, out blue) && red >= 0 && red <= 255 && green >= 0 && green <= 255 && blue >= 0 && blue <= 255)
                {
                    menuBannerR = red;
                    menuBannerG = green;
                    menuBannerB = blue;
                    customBannerR = menuBannerR;
                    customBannerG = menuBannerG;
                    customBannerB = menuBannerB;
                }
                else WarnIniRejection(KEY_APR_BannerColorRGB, Dbg.IniInvalidValue(KEY_APR_BannerColorRGB, s));
            }
            if (changedValues.TryGetValue(KEY_APR_MenuPosition, out s))
            {
                if (string.Equals(s, "Left", StringComparison.OrdinalIgnoreCase)) menuAlignLeft = true;
                else if (string.Equals(s, "Right", StringComparison.OrdinalIgnoreCase)) menuAlignLeft = false;
                else WarnIniRejection(KEY_APR_MenuPosition, Dbg.IniInvalidValue(KEY_APR_MenuPosition, s));
            }
            if (changedValues.TryGetValue(KEY_APR_MaxRows, out s))
            {
                if (int.TryParse(s, out n) && n >= MENU_MAX_ROWS_MIN && n <= MENU_MAX_ROWS_MAX) menuMaxRows = n;
                else WarnIniRejection(KEY_APR_MaxRows, Dbg.IniInvalidValueRange(KEY_APR_MaxRows, s, MENU_MAX_ROWS_MIN + "-" + MENU_MAX_ROWS_MAX));
            }
            ApplyIniBool(changedValues, KEY_EXG_AlwaysOverwrite, ref alwaysOverwrite);
            ApplyIniBool(changedValues, KEY_EXG_PromptOnSave, ref promptOnSave);
            ApplyIniBool(changedValues, KEY_EXG_Autosave, ref autosaveEnabled);
            ApplyIniBool(changedValues, KEY_HPV_PreviewByDefault, ref previewByDefault);
            ApplyIniBool(changedValues, KEY_HPV_ShowPreviewDiff, ref showPreviewDiff);
            ApplyIniBool(changedValues, KEY_SCR_ReassertDriftedFields, ref reassertDriftedFields);
            ApplyIniBool(changedValues, KEY_EXP_LiveSubHandlingData, ref liveSubHandlingData);
            if (changedValues.TryGetValue(KEY_DBG_LoggingMode, out s))
            {
                if (int.TryParse(s, out n) && n >= LOGGING_MODE_MIN && n <= LOGGING_MODE_MAX) loggingMode = n;
                else WarnIniRejection(KEY_DBG_LoggingMode, Dbg.IniInvalidValueRange(KEY_DBG_LoggingMode, s, LOGGING_MODE_MIN + "-" + LOGGING_MODE_MAX));
            }
            ApplyIniBool(changedValues, KEY_DBG_AlwaysClearLogOnReload, ref alwaysClearLogOnReload);
        }

        // The values ApplyMenuPosition and ApplyMenuTheme read. A reload compares them before and after, so each applies once and only when its own values changed
        private string MenuPositionKey() { return menuAlignLeft + "," + menuMaxRows; }
        private string MenuThemeKey() { return menuBannerR + "," + menuBannerG + "," + menuBannerB; }

        private void ApplyChangedMenuAppearance(string previousPositionKey, string previousThemeKey)
        {
            if (MenuPositionKey() != previousPositionKey) ApplyMenuPosition();
            if (MenuThemeKey() != previousThemeKey) ApplyMenuTheme();
        }

        // Adds missing entries, enforces canonical structure, preserves user-edited values
        private void ValidateConfigFile()
        {
            try
            {
                var userValues = ParseIniScalarLines(File.ReadAllLines(iniPath), true);
                // Captured before defaults wipe the fields so a failed parse can be restored to the last working value
                List<KeyValuePair<string, string>> lastGoodValues = iniLastGoodAvailable ? BuildSettingsSnapshot() : null;
                ApplyIniDefaults();
                // Exact case, so a case-sensitive value like the cheat string keeps the casing it was typed with
                var changedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in userValues)
                {
                    string defaultText;
                    if (!defaultIniValues.TryGetValue(kv.Key, out defaultText) || !string.Equals(defaultText, kv.Value, StringComparison.Ordinal)) changedValues[kv.Key] = kv.Value;
                }
                iniRejectedKeys.Clear();
                ApplyValues(changedValues);
                if (keyToggleMenu == Keys.None && string.IsNullOrEmpty(enableCheatString)) RejectEmptyMenuEntryPoints(lastGoodValues);
                RestoreRejectedValues(lastGoodValues);
                File.WriteAllText(iniPath, BuildIniEntries());
                ReadIni();
                iniLastGoodAvailable = true;
                logger.Log(Dbg.Level.Info, Dbg.IniRegenerated(changedValues.Count));
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.ValidateConfigFileFailed(ex.Message)); }
        }

        // The menu needs a toggle key or a cheat string. When a load leaves both empty, each of the 2 keys whose value changed since the last load is rejected
        // RestoreRejectedValues then puts back its last value, and with no last value the cheat string falls back to its compiled default
        private void RejectEmptyMenuEntryPoints(List<KeyValuePair<string, string>> lastGoodValues)
        {
            bool rejected = false;
            if (lastGoodValues != null)
            {
                for (int i = 0; i < lastGoodValues.Count; i++)
                {
                    string key = lastGoodValues[i].Key;
                    string current = key == KEY_CTL_KeyToggleMenu ? FormatToggleKey(keyToggleMenu) : key == KEY_CTL_EnableCheatString ? enableCheatString : null;
                    if (current == null || string.Equals(current, lastGoodValues[i].Value, StringComparison.Ordinal)) continue;
                    WarnIniRejection(key, Dbg.IniMenuEntryPointsEmpty(key));
                    rejected = true;
                }
            }
            if (rejected) return;
            WarnIniRejection(KEY_CTL_EnableCheatString, Dbg.IniMenuEntryPointsDefaulted(KEY_CTL_EnableCheatString, DEFAULT_CHEAT_STRING));
            enableCheatString = DEFAULT_CHEAT_STRING;
        }

        // Restores every rejected key to the value it held before this load
        // A null snapshot means no valid state existed yet, so the compiled default stands
        private void RestoreRejectedValues(List<KeyValuePair<string, string>> lastGoodValues)
        {
            if (iniRejectedKeys.Count == 0 || lastGoodValues == null) return;
            var restore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lastGoodValues.Count; i++)
            {
                if (iniRejectedKeys.Contains(lastGoodValues[i].Key)) restore[lastGoodValues[i].Key] = lastGoodValues[i].Value;
            }
            if (restore.Count == 0) return;
            ApplyValues(restore);
            for (int i = 0; i < iniRejectedKeys.Count; i++)
            {
                string restored;
                if (restore.TryGetValue(iniRejectedKeys[i], out restored)) logger.Log(Dbg.Level.Warn, Dbg.IniValueRetained(iniRejectedKeys[i], restored));
            }
        }

        // Writes the INI from the current in-memory values, which RunSettingsReset sets to the compiled defaults first
        private void ResetScalarDefaults()
        {
            try { File.WriteAllText(iniPath, BuildIniEntries()); }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.ResetScalarDefaultsFailed(ex.Message)); }
        }

        // Single registration point for the ResetAllSettings sequence
        // Shared by the constructor and the Settings menu, so a menu-driven reset runs immediately
        // It also applies the menu position and theme the reset changed
        private void PerformSettingsReset()
        {
            string previousPositionKey = MenuPositionKey();
            string previousThemeKey = MenuThemeKey();
            RunSettingsReset();
            ApplyChangedMenuAppearance(previousPositionKey, previousThemeKey);
        }

        // The reset itself, without the menu position and theme apply, which RefreshIni runs once after the whole reload settles
        private void RunSettingsReset()
        {
            List<KeyValuePair<string, string>> previousSnapshot = lastSettingsSnapshot;
            ApplyIniDefaults();
            ResetScalarDefaults();
            ReadIni();
            logger.Log(Dbg.Level.Info, Dbg.IniReset());
            List<KeyValuePair<string, string>> resetSnapshot = BuildSettingsSnapshot();
            LogSettingsDiff(previousSnapshot, resetSnapshot);
            lastSettingsSnapshot = resetSnapshot;
            SyncSettingsMenuItems();
            notifications.Notify(NOTIFICATION_KEY_SETTINGS_RESET, NOTIF_SETTINGS_RESET);
            try
            {
                iniLastWriteTime = File.GetLastWriteTime(iniPath);
                iniLastContent = File.ReadAllText(iniPath);
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.IniSnapshotFailed(ex.Message)); }
        }

        private void RefreshIni()
        {
            // A file deleted live is rewritten from the current in-memory settings on the spot rather than waiting for the next startup
            if (!File.Exists(iniPath))
            {
                WriteDefaultIni();
                logger.Log(Dbg.Level.Warn, Dbg.IniRestored());
                return;
            }
            try
            {
                DateTime lastWrite = File.GetLastWriteTime(iniPath);
                if (lastWrite <= iniLastWriteTime) return;
                iniLastWriteTime = lastWrite;
                string content = File.ReadAllText(iniPath);
                if (content == iniLastContent) return;
                // A save that only touched formatting or comments still needs the structural rewrite but shouldn't notify
                bool settingsChanged = IniValuesDiffer(iniLastContent, content);
                iniLastContent = content;
                iniRejectionOccurred = false;
                string previousPositionKey = MenuPositionKey();
                string previousThemeKey = MenuThemeKey();
                ValidateConfigFile();
                try
                {
                    iniLastWriteTime = File.GetLastWriteTime(iniPath);
                    iniLastContent = File.ReadAllText(iniPath);
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.IniSnapshotFailed(ex.Message)); }
                // The reset posts its own toast, so the reload toast below stays quiet for it
                bool settingsWereReset = resetAllSettings;
                if (settingsWereReset) RunSettingsReset();
                ApplyChangedMenuAppearance(previousPositionKey, previousThemeKey);
                List<KeyValuePair<string, string>> refreshedSnapshot = BuildSettingsSnapshot();
                LogSettingsDiff(lastSettingsSnapshot, refreshedSnapshot);
                lastSettingsSnapshot = refreshedSnapshot;
                SyncSettingsMenuItems();
                if (!settingsChanged && !iniRejectionOccurred)
                {
                    logger.Log(Dbg.Level.Info, Dbg.IniStructureRestored());
                    return;
                }
                logger.Log(Dbg.Level.Info, Dbg.IniReloaded(loggingMode));
                if (!settingsWereReset) notifications.Notify(NOTIFICATION_KEY_INI_RELOAD, NOTIF_INI_RELOADED);
                if (iniRejectionOccurred) notifications.PostNotificationOnce(NOTIFICATION_KEY_INI_INVALID, NOTIF_INI_REJECTED);
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.RefreshIniFailed(ex.Message)); }
        }

        // True when any key's value differs between two versions, ignoring comments, blanks, key order and whitespace
        private static bool IniValuesDiffer(string previous, string current)
        {
            if (previous == null) return true;
            Dictionary<string, string> previousValues = ParseIniScalarLines(SplitLines(previous), true);
            Dictionary<string, string> currentValues = ParseIniScalarLines(SplitLines(current), true);
            if (previousValues.Count != currentValues.Count) return true;
            foreach (var kv in currentValues)
            {
                string previousValue;
                if (!previousValues.TryGetValue(kv.Key, out previousValue)) return true;
                if (!string.Equals(previousValue, kv.Value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private List<KeyValuePair<string, string>> BuildSettingsSnapshot()
        {
            return ParseIniScalarPairs(SplitLines(BuildIniEntries()), true);
        }

        private void LogSettingsSnapshot(List<KeyValuePair<string, string>> snapshot)
        {
            if (!logger.IsInfoEnabled()) return;
            logger.Log(Dbg.Level.Info, Dbg.SettingsHeader());
            for (int i = 0; i < snapshot.Count; i++) logger.Log(Dbg.Level.Info, Dbg.SettingsEntry(snapshot[i].Key, snapshot[i].Value));
        }

        private void LogSettingsDiff(List<KeyValuePair<string, string>> previous, List<KeyValuePair<string, string>> current)
        {
            if (!logger.IsInfoEnabled() || previous == null) return;
            var previousValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < previous.Count; i++) previousValues[previous[i].Key] = previous[i].Value;
            bool headerLogged = false;
            for (int i = 0; i < current.Count; i++)
            {
                string previousValue;
                if (previousValues.TryGetValue(current[i].Key, out previousValue) && string.Equals(previousValue, current[i].Value, StringComparison.Ordinal)) continue;
                if (!headerLogged)
                {
                    logger.Log(Dbg.Level.Info, Dbg.SettingsHeader());
                    headerLogged = true;
                }
                logger.Log(Dbg.Level.Info, Dbg.SettingsChanged(current[i].Key, previousValue ?? "(new)", current[i].Value));
            }
        }
    }
}
