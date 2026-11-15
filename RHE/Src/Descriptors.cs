using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private DateTime descriptorsLastWriteTime;
        private string descriptorsLastContent;
        private Dictionary<string, Dictionary<string, string>> descriptors = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> descriptorsMissingWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> descriptorsTildeWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The file's DisableUpdates value, set only through SetDisableDescriptorsUpdates. False until the file is first read
        private bool disableDescriptorsUpdates;
        // One record per tracked description. Prefix is null unless the description leads with a live value (a flag's hex, a speed's MPH) or a lock reason
        private class TrackedItemDescription { public NativeItem Item; public string Category; public string FieldName; public string Prefix; }
        // Menus whose own Description is descriptor-driven need the same live-refresh tracking as any NativeItem
        private class TrackedMenuDescription { public NativeMenu Menu; public string Category; public string FieldName; }
        private List<TrackedItemDescription> trackedItemDescriptions = new List<TrackedItemDescription>();
        private List<TrackedMenuDescription> trackedMenuDescriptions = new List<TrackedMenuDescription>();

        // The categories a null category falls back to. They share no field names with each other, unlike the sub-handling categories
        private static readonly string[] BaseDescriptorCategories = { "General", "Drivetrain", "Brakes", "Steering", "Traction", "Suspension", "Damage", "Misc", "Flags" };

        // Category is part of the identity because several sub-handling categories share field names
        // Every menu builder passes its own category explicitly, and null resolves against the base categories
        private string ResolveDescriptor(string category, string fieldName)
        {
            string value;
            Dictionary<string, string> fields;
            if (!string.IsNullOrEmpty(category))
            {
                if (descriptors.TryGetValue(category, out fields) && fields.TryGetValue(fieldName, out value)) return WarnIfUnpairedTilde(category + "." + fieldName, value);
                WarnDescriptorMissing(category + "." + fieldName);
                return "";
            }
            for (int i = 0; i < BaseDescriptorCategories.Length; i++)
            {
                if (descriptors.TryGetValue(BaseDescriptorCategories[i], out fields) && fields.TryGetValue(fieldName, out value)) return WarnIfUnpairedTilde(fieldName, value);
            }
            WarnDescriptorMissing(fieldName);
            return "";
        }

        private void WarnDescriptorMissing(string key) { if (descriptorsMissingWarned.Add(key)) logger.Log(Dbg.Level.Warn, Dbg.DescriptorMissing(key)); }

        // The game drops every character after an unpaired "~", so a description with an odd "~" count shows cut off. This warns once per key
        private string WarnIfUnpairedTilde(string key, string value)
        {
            int tildeCount = 0;
            for (int i = 0; i < value.Length; i++) if (value[i] == '~') tildeCount++;
            if (tildeCount % 2 != 0 && descriptorsTildeWarned.Add(key)) logger.Log(Dbg.Level.Warn, Dbg.DescriptorUnpairedTilde(key));
            return value;
        }

        private void RefreshTrackedDescriptions()
        {
            for (int i = 0; i < trackedItemDescriptions.Count; i++)
            {
                TrackedItemDescription tracked = trackedItemDescriptions[i];
                string resolved = ResolveDescriptor(tracked.Category, tracked.FieldName);
                tracked.Item.Description = tracked.Prefix == null ? resolved : tracked.Prefix + resolved;
            }
            for (int i = 0; i < trackedMenuDescriptions.Count; i++)
            {
                TrackedMenuDescription tracked = trackedMenuDescriptions[i];
                tracked.Menu.Description = ResolveDescriptor(tracked.Category, tracked.FieldName);
            }
        }

        // Records the item alongside the category it was built under, so a live reload re-resolves against the same one
        // Pass fieldName explicitly when Title carries display-only content not suitable as the JSON key
        // Returns the tracked entry, so a field whose description leads with its live value can update the entry's Prefix
        private void TrackDescription(NativeItem item, string category) { TrackDescription(item, category, item.Title, null); }
        private TrackedItemDescription TrackDescription(NativeItem item, string category, string fieldName, string prefix)
        {
            var tracked = new TrackedItemDescription { Item = item, Category = category, FieldName = fieldName, Prefix = prefix };
            trackedItemDescriptions.Add(tracked);
            return tracked;
        }

        private void TrackMenuDescription(NativeMenu menu, string category, string fieldName)
        {
            trackedMenuDescriptions.Add(new TrackedMenuDescription { Menu = menu, Category = category, FieldName = fieldName });
        }

        // disableUpdatesOverride replaces the file's DisableUpdates value. Null keeps the file's own, or False for a fresh file
        private bool LoadDescriptors(bool? disableUpdatesOverride = null)
        {
            try
            {
                string descriptorsDirectory = Path.GetDirectoryName(DESCRIPTORS_PATH);
                if (!string.IsNullOrEmpty(descriptorsDirectory) && !Directory.Exists(descriptorsDirectory)) Directory.CreateDirectory(descriptorsDirectory);
                string content;
                Dictionary<string, Dictionary<string, string>> parsed;
                if (!File.Exists(DESCRIPTORS_PATH))
                {
                    SetDisableDescriptorsUpdates(disableUpdatesOverride ?? false);
                    content = BuildDefaultDescriptors(null, null, null, disableDescriptorsUpdates);
                    parsed = WriteDescriptorsTransactional(content);
                    if (parsed != null) logger.Log(Dbg.Level.Info, Dbg.DescriptorsWritten());
                    else logger.Log(Dbg.Level.Error, Dbg.DescriptorsWriteVerifyFailed());
                }
                else content = ValidateDescriptorsFile(out parsed, disableUpdatesOverride);
                // A failed write already deletes the file (WriteDescriptorsTransactional's own cleanup), so this fallback throws
                // The outer catch below handles it the same way as any other read failure
                if (parsed == null)
                {
                    content = File.ReadAllText(DESCRIPTORS_PATH);
                    parsed = ParseDescriptorCategories(content);
                }
                CommitDescriptors(parsed, content);
                return true;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.DescriptorsLoadFailed(ex.Message));
                return false;
            }
        }

        // The loaded descriptors and the file state a refresh compares against, set together so the file reads as already loaded
        private void CommitDescriptors(Dictionary<string, Dictionary<string, string>> parsed, string content)
        {
            descriptors = parsed;
            descriptorsMissingWarned.Clear();
            descriptorsTildeWarned.Clear();
            descriptorsLastWriteTime = File.GetLastWriteTime(DESCRIPTORS_PATH);
            descriptorsLastContent = content;
        }

        // Per-category maps, since same-named fields in different categories must not overwrite each other
        private static Dictionary<string, Dictionary<string, string>> ParseDescriptorCategories(string json)
        {
            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> categories = serializer.Deserialize<Dictionary<string, object>>(json);
            var parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var categoryEntry in categories)
            {
                var fields = categoryEntry.Value as Dictionary<string, object>;
                if (fields == null) continue;
                var categoryFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fieldEntry in fields) categoryFields[fieldEntry.Key] = fieldEntry.Value != null ? fieldEntry.Value.ToString() : "";
                parsed[categoryEntry.Key] = categoryFields;
            }
            return parsed;
        }

        // A structurally broken file is salvaged, and the caller rewrites the canonical structure afterwards
        private Dictionary<string, Dictionary<string, string>> ParseOrSalvageDescriptors(string content, out bool salvaged)
        {
            salvaged = false;
            try { return ParseDescriptorCategories(content); }
            catch (Exception ex)
            {
                salvaged = true;
                logger.Log(Dbg.Level.Warn, Dbg.DescriptorsMalformed(ex.Message));
                return SalvageDescriptorCategories(content);
            }
        }

        // Line-based recovery for invalid JSON, reading intact category headers and field lines individually
        private static Dictionary<string, Dictionary<string, string>> SalvageDescriptorCategories(string content)
        {
            var parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> currentCategory = null;
            foreach (string raw in SplitLines(content))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '"') continue;
                int keyEnd = FindClosingQuote(line, 1);
                if (keyEnd < 0) continue;
                string key = Unescape(line.Substring(1, keyEnd - 1));
                int colon = line.IndexOf(':', keyEnd + 1);
                if (colon < 0) continue;
                string rest = line.Substring(colon + 1).Trim();
                if (rest.StartsWith("{"))
                {
                    currentCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    parsed[key] = currentCategory;
                    continue;
                }
                if (currentCategory == null || rest.Length == 0 || rest[0] != '"') continue;
                int valueEnd = FindClosingQuote(rest, 1);
                if (valueEnd < 0) continue;
                currentCategory[key] = Unescape(rest.Substring(1, valueEnd - 1));
            }
            return parsed;
        }

        // Index of the quote that closes the one at start - 1, skipping any escaped quote inside the string
        private static int FindClosingQuote(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }
                if (text[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string text)
        {
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if (current != '\\' || i + 1 >= text.Length)
                {
                    result.Append(current);
                    continue;
                }
                char escaped = text[++i];
                if (escaped == '"') result.Append('"');
                else if (escaped == '\\') result.Append('\\');
                else if (escaped == '/') result.Append('/');
                else if (escaped == 'b') result.Append('\b');
                else if (escaped == 'f') result.Append('\f');
                else if (escaped == 'n') result.Append('\n');
                else if (escaped == 'r') result.Append('\r');
                else if (escaped == 't') result.Append('\t');
                else if (escaped == 'u' && i + 4 < text.Length)
                {
                    int codePoint;
                    string hex = text.Substring(i + 1, 4);
                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint))
                    {
                        result.Append((char)codePoint);
                        i += 4;
                    }
                    else result.Append("\\u");
                }
                else result.Append('\\').Append(escaped);
            }
            return result.ToString();
        }

        private string DescriptorsBackupPath() { return Path.ChangeExtension(DESCRIPTORS_PATH, null) + DESCRIPTORS_DAMAGED_BACKUP_SUFFIX; }

        // Holds the damaged content only for the duration of the repair, so a repair that fails partway still leaves the original recoverable
        private bool BackupBrokenDescriptors(string content)
        {
            try
            {
                File.WriteAllText(DescriptorsBackupPath(), content);
                return true;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Warn, Dbg.DescriptorsBackupFailed(ex.Message));
                return false;
            }
        }

        private void RemoveBrokenDescriptorsBackup()
        {
            try { if (File.Exists(DescriptorsBackupPath())) File.Delete(DescriptorsBackupPath()); }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.DescriptorsBackupCleanupFailed(ex.Message)); }
        }

        // Reads the top-level "Version" and DisableUpdates entries directly, not via ParseDescriptorCategories, which only keeps category-object values
        // Returns 0 for a file with no version entry, or for any parse failure. A missing DisableUpdates reads as False
        // A DisableUpdates value that is not a bool keeps the current setting and logs a WARN
        private int ReadDescriptorsTopLevel(string json, out bool disableUpdates)
        {
            disableUpdates = disableDescriptorsUpdates;
            Dictionary<string, object> parsed;
            try { parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Info, Dbg.DescriptorsTopLevelUnreadable(ex.Message));
                return 0;
            }
            object disableValue;
            if (!parsed.TryGetValue(DESCRIPTORS_DISABLE_UPDATES_KEY, out disableValue)) disableUpdates = false;
            else if (disableValue is bool) disableUpdates = (bool)disableValue;
            else logger.Log(Dbg.Level.Warn, Dbg.DescriptorsDisableUpdatesInvalid(disableValue == null ? "null" : disableValue.ToString(), disableUpdates));
            object versionValue;
            try { if (parsed.TryGetValue(DESCRIPTORS_VERSION_KEY, out versionValue) && versionValue != null) return Convert.ToInt32(versionValue); }
            catch (Exception ex) { logger.Log(Dbg.Level.Info, Dbg.DescriptorsVersionUnreadable(ex.Message)); }
            return 0;
        }

        // The INI settings diff does not cover this file, so every change logs here. The first read logs its value with no previous one
        private void SetDisableDescriptorsUpdates(bool value)
        {
            bool firstRead = descriptorsLastContent == null;
            if (!firstRead && value == disableDescriptorsUpdates) return;
            logger.Log(Dbg.Level.Info, Dbg.DescriptorsDisableUpdatesChanged(firstRead ? "(new)" : disableDescriptorsUpdates.ToString(), value));
            disableDescriptorsUpdates = value;
        }

        // Never overwritten, never deleted. A taken backup name gets the next free 2-digit suffix
        private void BackupDescriptorsOnVersionChange(string existingContent)
        {
            string descriptorsDirectory = Path.GetDirectoryName(DESCRIPTORS_PATH);
            string outPath = Path.Combine(descriptorsDirectory, DESCRIPTORS_VERSION_BACKUP_NAME + DESCRIPTORS_FILE_EXTENSION);
            int suffix = 1;
            while (File.Exists(outPath)) outPath = Path.Combine(descriptorsDirectory, DESCRIPTORS_VERSION_BACKUP_NAME + "_" + suffix++.ToString("D2") + DESCRIPTORS_FILE_EXTENSION);
            File.WriteAllText(outPath, existingContent);
        }

        // Writes directly to the final path so that the game's mod archive tool registers changes to it instead of omitting it
        // A successfully written file that fails parsing is removed before the failure is reported
        // Returns the dictionary its own verify-parse produced from what actually landed on disk, or null on a failed verify
        // Callers must not log success unconditionally, since a failed verify silently deletes the file
        private Dictionary<string, Dictionary<string, string>> WriteDescriptorsTransactional(string content)
        {
            bool writeCompleted = false;
            Dictionary<string, Dictionary<string, string>> verified = null;
            try
            {
                File.WriteAllText(DESCRIPTORS_PATH, content);
                writeCompleted = true;
                verified = ParseDescriptorCategories(File.ReadAllText(DESCRIPTORS_PATH));
            }
            finally
            {
                try { if (writeCompleted && verified == null && File.Exists(DESCRIPTORS_PATH)) File.Delete(DESCRIPTORS_PATH); }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.DescriptorsUnreadableFileRemovalFailed(ex.Message)); }
            }
            return verified;
        }

        // The user's entries that hold a value and differ from the compiled default, which are the customizations a regenerate keeps
        private static Dictionary<string, Dictionary<string, string>> ExtractChangedDescriptorValues(Dictionary<string, Dictionary<string, string>> userValues, Dictionary<string, Dictionary<string, string>> defaultValues, out int changedCount)
        {
            var changedValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            changedCount = 0;
            foreach (var categoryEntry in userValues)
            {
                Dictionary<string, string> defaultFields;
                defaultValues.TryGetValue(categoryEntry.Key, out defaultFields);
                foreach (var fieldEntry in categoryEntry.Value)
                {
                    if (string.IsNullOrEmpty(fieldEntry.Value)) continue;
                    string defaultText;
                    if (defaultFields != null && defaultFields.TryGetValue(fieldEntry.Key, out defaultText) && string.Equals(defaultText, fieldEntry.Value, StringComparison.Ordinal)) continue;
                    Dictionary<string, string> categoryChanges = GetOrAdd(changedValues, categoryEntry.Key, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    categoryChanges[fieldEntry.Key] = fieldEntry.Value;
                    changedCount++;
                }
            }
            return changedValues;
        }

        // Returns the final canonical content (whatever the file now holds, written or not) with the matching parsed dictionary in result, or null on a genuine failure
        // A salvaged file whose backup cannot be written is left untouched, and result then holds its salvaged entries over the compiled defaults
        // The write is skipped when the regenerated content equals the file (result then carries the parsed userValues), so nothing is written or re-parsed
        // disableUpdatesOverride replaces the file's DisableUpdates value before the upgrade check. Null keeps the file's own
        private string ValidateDescriptorsFile(out Dictionary<string, Dictionary<string, string>> result, bool? disableUpdatesOverride = null)
        {
            result = null;
            try
            {
                string existing = File.ReadAllText(DESCRIPTORS_PATH);
                bool salvaged;
                var userValues = ParseOrSalvageDescriptors(existing, out salvaged);

                // Captured directly from the same typed literals BuildDefaultDescriptors' own AppendCategory calls build the string from
                // That avoids re-parsing the string back with a second JavaScriptSerializer.Deserialize pass
                var defaultValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                string compiledDefaults = BuildDefaultDescriptors(null, null, defaultValues);
                int changedCount;
                var changedValues = ExtractChangedDescriptorValues(userValues, defaultValues, out changedCount);
                if (salvaged && !BackupBrokenDescriptors(existing))
                {
                    // Without its backup a repair could lose the damaged file's entries, so the file is left as it is and this session uses the entries the salvage read
                    // The caller commits the file's own text, so a refresh retries the repair only once the file changes
                    logger.Log(Dbg.Level.Warn, Dbg.DescriptorsRepairSkipped());
                    SetDisableDescriptorsUpdates(disableUpdatesOverride ?? disableDescriptorsUpdates);
                    var sessionValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    BuildDefaultDescriptors(changedValues, null, sessionValues, disableDescriptorsUpdates);
                    result = sessionValues;
                    return existing;
                }
                bool repairPending = salvaged;

                // Read once. Drives the upgrade check here, and the version stamp and DisableUpdates value a regenerate keeps below
                // A file that failed to parse has no readable entries, so a salvaged file is never an out-of-date one and keeps the current DisableUpdates value
                bool fileDisableUpdates = disableDescriptorsUpdates;
                int existingVersion = salvaged ? DESCRIPTORS_VERSION : ReadDescriptorsTopLevel(existing, out fileDisableUpdates);
                SetDisableDescriptorsUpdates(disableUpdatesOverride ?? fileDisableUpdates);
                bool versionUpgradeNeeded = !disableDescriptorsUpdates && existingVersion < DESCRIPTORS_VERSION;
                if (versionUpgradeNeeded)
                {
                    BackupDescriptorsOnVersionChange(existing);
                    Dictionary<string, Dictionary<string, string>> written = WriteDescriptorsTransactional(compiledDefaults);
                    if (written != null)
                    {
                        logger.Log(Dbg.Level.Info, Dbg.DescriptorsVersionUpgraded(DESCRIPTORS_VERSION));
                        notifications.Notify(NOTIFICATION_KEY_DESCRIPTORS_VERSION, NOTIF_DESCRIPTORS_UPDATED);
                        if (repairPending) RemoveBrokenDescriptorsBackup();
                        result = written;
                        return compiledDefaults;
                    }
                    logger.Log(Dbg.Level.Error, Dbg.DescriptorsWriteVerifyFailed());
                    return null;
                }
                // A miss here (including a purely cosmetic difference from manual hand-editing) falls through to the same write-and-verify path below
                string regenerated = BuildDefaultDescriptors(changedValues, existingVersion, null, disableDescriptorsUpdates);
                if (string.Equals(regenerated, existing, StringComparison.Ordinal))
                {
                    logger.Log(Dbg.Level.Info, Dbg.DescriptorsUnchanged());
                    if (repairPending) RemoveBrokenDescriptorsBackup();
                    result = userValues;
                    return existing;
                }
                Dictionary<string, Dictionary<string, string>> regeneratedDict = WriteDescriptorsTransactional(regenerated);
                if (regeneratedDict != null)
                {
                    logger.Log(Dbg.Level.Info, Dbg.DescriptorsRegenerated(changedCount));
                    // The backup only exists to survive a repair that dies partway. Once the repaired file is on disk it has no further purpose, and the repair is reported
                    if (repairPending)
                    {
                        RemoveBrokenDescriptorsBackup();
                        notifications.PostNotificationOnce(NOTIFICATION_KEY_DESCRIPTORS_REPAIRED, NOTIF_DESCRIPTORS_REPAIRED);
                    }
                    result = regeneratedDict;
                    return regenerated;
                }
                logger.Log(Dbg.Level.Error, Dbg.DescriptorsWriteVerifyFailed());
                return null;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.ValidateDescriptorsFileFailed(ex.Message));
                return null;
            }
        }

        private void RefreshDescriptors()
        {
            // A file deleted live is rewritten from compiled defaults on the spot rather than waiting for the next startup, matching how the INI self-corrects
            if (!File.Exists(DESCRIPTORS_PATH))
            {
                if (descriptorsLastContent == null) return;
                if (!LoadDescriptors()) return;
                RefreshTrackedDescriptions();
                // Some tracked items (AIHandling, WeaponHash) compose a live-value suffix onto their resolved description in SyncFieldValues
                // The plain overwrite above would otherwise discard that suffix on every live descriptor reload
                // A descriptor reload affects every live tree at once, not just whichever one currently has visual focus
                SyncAllMountedEditors();
                SyncSettingsMenuItems();
                logger.Log(Dbg.Level.Warn, Dbg.DescriptorsRestored());
                return;
            }
            try
            {
                DateTime lastWrite = File.GetLastWriteTime(DESCRIPTORS_PATH);
                if (lastWrite <= descriptorsLastWriteTime) return;
                string content = File.ReadAllText(DESCRIPTORS_PATH);
                if (content == descriptorsLastContent)
                {
                    descriptorsLastWriteTime = lastWrite;
                    return;
                }
                // Resolved before committing any state either way. A file the validation cannot settle leaves the last good descriptors live and stays eligible for a retry
                Dictionary<string, Dictionary<string, string>> parsed;
                content = ValidateDescriptorsFile(out parsed);
                if (parsed == null)
                {
                    content = File.ReadAllText(DESCRIPTORS_PATH);
                    parsed = ParseDescriptorCategories(content);
                }
                CommitDescriptors(parsed, content);
                RefreshTrackedDescriptions();
                // A descriptor reload affects every live tree at once, not just whichever one currently has visual focus
                SyncAllMountedEditors();
                // The file's DisableUpdates value may have changed, and Settings shows it
                SyncSettingsMenuItems();
                logger.Log(Dbg.Level.Info, Dbg.DescriptorsReloaded());
                notifications.Notify(NOTIFICATION_KEY_DESCRIPTORS_RELOAD, NOTIF_DESCRIPTORS_RELOADED);
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.DescriptorsRefreshFailed(ex.Message)); }
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        private static KeyValuePair<string, string> DescriptorRow(string key, string text) { return new KeyValuePair<string, string>(key, text); }

        // A row takes the text customizations holds for its category and key, and its compiled text otherwise
        // captureInto, when passed, also fills a typed dictionary from the same texts the string is built from
        // This lets a caller capture BuildDefaultDescriptors' own output with no JSON round-trip
        private static void AppendCategory(StringBuilder sb, string categoryName, Dictionary<string, Dictionary<string, string>> customizations, KeyValuePair<string, string>[] fields, bool isLastCategory, Dictionary<string, Dictionary<string, string>> captureInto = null)
        {
            Dictionary<string, string> overrides = CategoryOverrides(customizations, categoryName);
            var texts = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++) texts[i] = OverrideOrDefault(overrides, fields[i].Key, fields[i].Value);
            sb.Append("  ");
            AppendJsonString(sb, categoryName);
            sb.AppendLine(": {");
            for (int i = 0; i < fields.Length; i++)
            {
                sb.Append("    ");
                AppendJsonString(sb, fields[i].Key);
                sb.Append(": ");
                AppendJsonString(sb, texts[i]);
                sb.AppendLine(i == fields.Length - 1 ? "" : ",");
            }
            sb.AppendLine(isLastCategory ? "  }" : "  },");
            if (captureInto != null)
            {
                var categoryFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Length; i++) categoryFields[fields[i].Key] = texts[i];
                captureInto[categoryName] = categoryFields;
            }
        }

        private static string OverrideOrDefault(Dictionary<string, string> overrides, string key, string compiledDefault)
        {
            string value;
            return overrides != null && overrides.TryGetValue(key, out value) ? value : compiledDefault;
        }

        private static Dictionary<string, string> CategoryOverrides(Dictionary<string, Dictionary<string, string>> customizations, string categoryName)
        {
            Dictionary<string, string> fields;
            return customizations != null && customizations.TryGetValue(categoryName, out fields) ? fields : null;
        }

        // versionOverride lets the per-field diff path regenerate the file's structure without bumping its version stamp forward. Null writes the current version
        // disableUpdates is the DisableUpdates value the file is written with
        // captureInto, when passed, receives the same data as a typed dictionary, so a caller captures the compiled defaults without a second JSON parse
        private string BuildDefaultDescriptors(Dictionary<string, Dictionary<string, string>> customizations = null, int? versionOverride = null, Dictionary<string, Dictionary<string, string>> captureInto = null, bool disableUpdates = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.Append("  \"").Append(DESCRIPTORS_VERSION_KEY).Append("\": ").Append(versionOverride ?? DESCRIPTORS_VERSION).AppendLine(",");
            sb.Append("  \"").Append(DESCRIPTORS_DISABLE_UPDATES_KEY).Append("\": ").Append(disableUpdates ? "true" : "false").AppendLine(",");
            AppendCategory(sb, "General", customizations, new[]
            {
                DescriptorRow("Mass", "Total weight in kilograms. Mostly affects collisions, pushing other entities, and damage. Heavier takes less and deals more. Traction, drive, brake, and spring forces all scale proportionally. Requires an unload to physically apply, except for collision damage scale"),
                DescriptorRow("InitialDragCoefficient", "Aerodynamic drag. Higher = stronger resistance. Requires an unload to recalculate the base air damping"),
                DescriptorRow("DownforceModifier", "Scales the amount of downforce relative to speed. 1 or less = standard linear downforce. Above 1, it increases by speed squared, peaking at 90% of InitialDriveMaxFlatVelocity multiplied by the value, so it can be weaker than standard below that. Above 100 in SP is a flat 30% of standard. No effect on motorcycles"),
                DescriptorRow("PopUpLightRotation", "Rotation of pop-up headlights in degrees. The sign sets the direction, any non-zero value opens them in 1 second. 0 uses the default of 7 degrees"),
                DescriptorRow("PercentSubmerged", "Percent of the bounding box that sits under water while floating at rest. Lower floats higher and sinks slower. 50 holds the vehicle up like an airtight hull, 100 sinks it like a flooded one, and negative sinks it outright, results vary by model. Above 100, it can't float and sinks once partly under (20% for cars). Requires an unload to apply"),
                DescriptorRow("CenterOfMassOffsetX", string.Format(DESC_AXIS_CENTER_OF_MASS_OFFSET, "X")),
                DescriptorRow("CenterOfMassOffsetY", string.Format(DESC_AXIS_CENTER_OF_MASS_OFFSET, "Y")),
                DescriptorRow("CenterOfMassOffsetZ", string.Format(DESC_AXIS_CENTER_OF_MASS_OFFSET, "Z")),
                DescriptorRow("InertiaMultiplierX", string.Format(DESC_AXIS_INERTIA_MULTIPLIER, "X")),
                DescriptorRow("InertiaMultiplierY", string.Format(DESC_AXIS_INERTIA_MULTIPLIER, "Y")),
                DescriptorRow("InertiaMultiplierZ", string.Format(DESC_AXIS_INERTIA_MULTIPLIER, "Z")),
            }, false, captureInto);
            AppendCategory(sb, "Drivetrain", customizations, new[]
            {
                DescriptorRow("DriveBiasFront", "InitialDriveForce distribution to each axle. Below 0.1 = RWD, above 0.9 = FWD, in between = AWD. Requires a respawn to apply. With CF_DIFF_CENTRE, it only picks which axles are powered, and the center differential sets the bias"),
                DescriptorRow("InitialDriveGears", "Number of gears, excluding reverse. Transmission upgrades add 1 total. Requires a respawn on a vehicle with no modkit assigned in its carvariations.meta. Max = 10 with GearRatioPatch installed, 6 without it"),
                DescriptorRow("InitialDriveForce", "Base engine torque applied to the drive axles, or wheels if CF_DIFF_CENTRE is set. The current gear and its ratio affect the output"),
                DescriptorRow("DriveInertia", "How fast transmission RPM rises and falls. Higher reacts faster for cars, reducing RPM lag after shifts. Engine sound can't rise faster than a value of 1 allows, and some sounds lag behind a fast fall. Boats are the opposite: Higher reacts slower, reducing throttle response, and values below 1 act like 1"),
                DescriptorRow("ClutchChangeRateUpShift", "How fast the clutch re-engages after an upshift. Higher = faster shifts"),
                DescriptorRow("ClutchChangeRateDownShift", "How fast the clutch re-engages after a downshift. Higher = faster shifts"),
                DescriptorRow("InitialDriveMaxFlatVelocity", "Target top speed in km/h. Vehicles may accelerate beyond or not reach this depending on InitialDriveForce, InitialDragCoefficient, TractionCurves/Bias, and DriveBias. Negative = no top speed limit"),
            }, false, captureInto);
            AppendCategory(sb, "Brakes", customizations, new[]
            {
                DescriptorRow("BrakeForce", "Max force per unit of weight when braking. Effectiveness influenced by TractionCurves"),
                DescriptorRow("BrakeBiasFront", "Share of BrakeForce on each axle. 1.0 = Front, 0.0 = Rear. Effectiveness influenced by TractionBiasFront"),
                DescriptorRow("HandbrakeForce", "Max force per unit of weight applied to the rear and middle wheels when handbraking, also cuts ABS and DriveForce. Eases to half strength over about a second while held. Effectiveness influenced by TractionCurves/Bias"),
            }, false, captureInto);
            AppendCategory(sb, "Steering", customizations, new[] { DescriptorRow("SteeringLock", "Maximum steering angle in degrees. Effectiveness influenced by TractionCurves/Bias"), }, false, captureInto);
            AppendCategory(sb, "Traction", customizations, new[]
            {
                DescriptorRow("TractionCurveMax", "Peak grip when the tire is not slipping. Also defines the bike burnout spin torque"),
                DescriptorRow("TractionCurveMin", "Grip when the tire is slipping. Grip moves from TractionCurveMax toward this, then drops to it"),
                DescriptorRow("TractionCurveLateral", "Slip angle in degrees at which cornering grip peaks before it decreases. Higher lets the tire slide at a wider angle before reaching peak grip, for a more gradual transition from TractionCurveMax to Min, while lower does the opposite"),
                DescriptorRow("TractionSpringDeltaMax", "Maximum distance in meters the tires can slide from their contact point before a spring pulls them back to it. Simulates tire sidewall bend and rebound, and holds the vehicle when stopped on an incline or when being pushed from the side. 0 or negative = no effect"),
                DescriptorRow("LowSpeedTractionLossMultiplier", "Artificial wheelspin during acceleration at full throttle from a standstill. Does not apply during wheelies or in Arena mode. 0.25 or lower = no effect"),
                DescriptorRow("CamberStiffness", "Sideways force applied to the wheels when on the ground above 5 m/s, pushing them outwards, towards the bodyroll direction. 0 or negative = no effect"),
                DescriptorRow("TractionBiasFront", "Distribution of grip between the front and rear axles, and a multiplier for each axle's camber force and slip ratios. Base balance influenced by SuspensionBiasFront. Boats and submarines use this to scale rudder response." + "\n~y~Note: 0 makes wheels vanish (respawn to fix), and 1 locks the vehicle in place"),
                DescriptorRow("TractionLossMultiplier", "How much the surface material affects grip, top speed, and drag. 1 = the material's values, 0 = every surface acts like road"),
            }, false, captureInto);
            AppendCategory(sb, "Suspension", customizations, new[]
            {
                DescriptorRow("SuspensionForce", "Spring stiffness per unit of mass. Lower = weaker resistance to downforce pressure. Also scales the body rock while revving the engine"),
                DescriptorRow("SuspensionCompressionDamping", "Damping while the suspension compresses. Higher makes compression harder, lower allows more compression under less pressure. Aircrafts ignore it and use SuspensionReboundDamping both ways"),
                DescriptorRow("SuspensionReboundDamping", "Damping while the suspension extends. Higher stays compressed for longer, and may not return to full resting height"),
                DescriptorRow("SuspensionUpperLimit", "How far up in meters the wheel and its suspension geometry can travel from its bone position. Requires a respawn to apply"),
                DescriptorRow("SuspensionLowerLimit", "How far down in meters the wheel and its suspension geometry can travel from its bone position. A lower value lets the wheel hang further and the vehicle sit lower on its springs. Requires a respawn to apply"),
                DescriptorRow("SuspensionRaise", "Meters the body is raised or lowered above the wheels. Hydraulics add their own raise on top. Vehicles with FLAG_DROP_SUSPENSION_WHEN_STOPPED drop by this value when stopped, and their ground clearance setup subtracts it. Bike wheel rendering adds it, scaled down as the tires lose health, when the model has no suspension bones. Requires a respawn to apply"),
                DescriptorRow("SuspensionBiasFront", "Distribution of SuspensionForce and Damping between front and rear axles. Stiffness split applies live, height split requires a respawn. Hover mode with SF_LIMIT_FORCE_DELTA uses it to split the hover force between axles. Note: Setting it to 0 is glitchy and causes wheels to vanish"),
                DescriptorRow("AntiRollBarForce", "Anti-roll bar stiffness per unit of mass: Amount of SuspensionForce added to a compressed wheel to reduce body roll. Also influences the engine rev body rock motion"),
                DescriptorRow("AntiRollBarBiasFront", "Distribution of AntiRollBarForce between the front and rear axles. 1.0 = Front, 0.0 = Rear. Influenced by SuspensionBiasFront"),
                DescriptorRow("RollCenterHeightFront", "Front lever fulcrum height in meters above each tire's ground contact point along the up axis that traction and suspension force use to roll and pitch the body. Higher = less body roll when turning, and less pitch under acceleration/braking. A value above the COM's height inverts both. Tracked vehicles only use this for suspension forces"),
                DescriptorRow("RollCenterHeightRear", "Rear lever fulcrum height in meters above each tire's ground contact point along the up axis that traction and suspension force use to roll and pitch the body. Higher = less body roll when turning, and less pitch under acceleration/braking. A value above the COM's height inverts both. Tracked vehicles only use this for suspension forces"),
            }, false, captureInto);
            AppendCategory(sb, "Damage", customizations, new[]
            {
                DescriptorRow("CollisionDamageMultiplier", "Scales overall damage from collisions, divided by Mass, so heavier vehicles take less"),
                DescriptorRow("WeaponDamageMultiplier", "Scales damage from weapons (plus melee), explosions, and fires. Ignored by weapons that deal damage based on the vehicle's max health (WeaponDamageScaledToVehHealthMult)"),
                DescriptorRow("DeformationDamageMultiplier", "Scales how much the body physically deforms from collisions. Heavier vehicles deform less, armor upgrades reduce it further"),
                DescriptorRow("EngineDamageMultiplier", "Scales engine damage from collisions only, bullets and explosions ignore it"),
            }, false, captureInto);
            AppendCategory(sb, "Misc", customizations, new[]
            {
                DescriptorRow("PetrolTankVolume", "Fuel tank capacity. Filled at spawn or repairs. Only drains by leaking, which starts once fuel tank health drops below 700. Under 10%, the engine misfires, and an empty tank makes the vehicle undriveable. At 0, the engine never starts. Electric vehicles never leak or misfire. Requires a respawn to reset the current tank"),
                DescriptorRow("OilVolume", "Engine oil capacity. Filled at spawn or repairs. Starts leaking once engine health drops below 200. Below 25%, a running engine takes damage, more at higher RPM. Electric vehicles never leak or take oil tank damage"),
                DescriptorRow("SeatOffsetDistanceX", "Horizontal offset in meters of the point of entry/exit from the vehicle the ped uses. Positive = outward. Setting this out of range results in the ped warping into the seat"),
                DescriptorRow("SeatOffsetDistanceY", "Longitudinal offset in meters of the point of entry/exit from the vehicle the ped uses. Positive = forward. Setting this out of range results in the ped warping into the seat"),
                DescriptorRow("SeatOffsetDistanceZ", "Vertical offset in meters of the point of entry/exit from the vehicle the ped uses. Positive = upward. Setting this out of range results in the ped warping into the seat"),
                DescriptorRow("MonetaryValue", "How much the vehicle is worth. Only used in GTA:O"),
                DescriptorRow("RocketBoostCapacity", "Max capacity of rocket boost, in seconds. Only triggers with a full charge for cars. Above 2.0, pressing boost again mid fire cancels it like the scramjet, and empties the remaining charge"),
                DescriptorRow("BoostMaxSpeed", "The speed at which rocket boost stops accelerating the vehicle. The default of 70 m/s is around 157 MPH. Using boost above this speed slows the vehicle down to it. Also scales the in-air recharge rate"),
            }, false, captureInto);
            AppendCategory(sb, "Flags", customizations, new[]
            {
                DescriptorRow("AIHandling", "Defines the preset used to determine how NPCs drive this vehicle"),
            }, false, captureInto);
            AppendCategory(sb, "ModelFlagBits", customizations, new[]
            {
                DescriptorRow("MF_ABS_STD", "Arcadey ABS. Removes brake slip so the wheels never lock. Requires a respawn to apply. A police pursuit can clear it until the next respawn"),
                DescriptorRow("MF_ABS_OPTION", "50% chance at spawn of the vehicle being fitted with ABS_STD, when ABS_STD itself is not set. A failed roll moves on to the ALT_ABS flags. Requires a respawn to apply"),
                DescriptorRow("MF_ABS_ALT_STD", "Pulsing ABS. The brake clamps briefly, then releases on a timer. Requires a respawn to apply. A police pursuit can clear it until the next respawn"),
                DescriptorRow("MF_ABS_ALT_OPTION", "50% chance at spawn of the vehicle being fitted with ABS_ALT_STD or none. Requires a respawn to apply"),
                DescriptorRow("MF_ATTACH_PED_TO_BODYSHELL", "No effect. Never read by the game"),
                DescriptorRow("MF_AXLE_F_MCPHERSON", "MacPherson independent suspension: Allows the front wheels and suspension (bone) meshes to tilt in/outwards. Requires a respawn to apply physically to the wheels"),
                DescriptorRow("MF_AXLE_R_MCPHERSON", "MacPherson independent suspension: Allows the rear wheels and suspension (bone) meshes to tilt in/outwards. Requires a respawn to apply physically to the wheels"),
                DescriptorRow("MF_AXLE_F_SOLID", "Rigid axle suspension: Makes the front wheels stay parallel to each other, while suspension (bone) meshes stretch vertically. Requires a respawn to apply physically to the wheels"),
                DescriptorRow("MF_AXLE_R_SOLID", "Rigid axle suspension: Makes the rear wheels stay parallel to each other, while suspension (bone) meshes stretch vertically. Requires a respawn to apply physically to the wheels"),
                DescriptorRow("MF_AXLE_F_TORSION", "Torsion beam suspension: Makes the front suspension mesh twist as the wheels compress/extend. Animation only, no effect on wheel motion"),
                DescriptorRow("MF_AXLE_R_TORSION", "Torsion beam suspension: Makes the rear suspension mesh twist as the wheels compress/extend. Animation only, no effect on wheel motion"),
                DescriptorRow("MF_CAN_ENTER_IF_NO_DOOR", "No effect. Never read by the game"),
                DescriptorRow("MF_DONT_FORCE_GRND_CLEARANCE", "Disables clipping of the bottom of the collision bounds by skipping the model's ground clearance setup when it's loaded into memory, allowing it to bottom out more accurately to the shape of the bounds. Ignored when IS_RC is set. No effect on planes, helicopters, boats, submarines, motorcycles, and bicycles. Requires an unload to apply"),
                DescriptorRow("MF_DONT_RENDER_STEER", "Stops wheels from visually turning. Requires a respawn to apply"),
                DescriptorRow("MF_DOUBLE_EXHAUST", "No effect. Never read by the game"),
                DescriptorRow("MF_DOUBLE_FRONT_WHEELS", "Marks this vehicle as having twin front wheels on its front axle. On models with separate rear wheel meshes, duplicates the skidmarks so both tires appear to create them. On models with extra wheel bounds, gives a broken-off front wheel a second collision bound for its inner wheel"),
                DescriptorRow("MF_DOUBLE_REAR_WHEELS", "Marks this vehicle as having twin rear wheels on its rear axle. On models with separate rear wheel meshes, duplicates the skidmarks so both tires appear to create them. On models with extra wheel bounds, gives a broken-off rear wheel a second collision bound for its inner wheel"),
                DescriptorRow("MF_EXTRA_CAMBER", "Increases wheel camber while the suspension is lowered, stopping the wheels from clipping the arches. Used by lowriders like the Slamvan Custom. Requires hydraulics and AXLE_F/R_MCPHERSON set"),
                DescriptorRow("MF_HAS_TRACKS", "For vehicles with tracks instead of tires: Sets up tracked wheels that ignore surface speed limits unless HF_STEER_ALL_WHEELS is set, drives the track animation and sound, and stops wheel bones from breaking off in explosions. Requires a respawn to apply"),
                DescriptorRow("MF_INDESTRUCTIBLE", "Makes the vehicle immune to all damage. Requires a respawn to apply. Scripts and missions can clear this on demand"),
                DescriptorRow("MF_IS_BIG", "Marks as a big vehicle. Weapon hits use the truck explosion, AI traffic offsets its path nodes only 0.15x as much, ambient traffic spawns it less and never passes slower cars, and peds it hits are not pushed over it. It keeps full grip when hit from behind and spins less in burnouts. Requires a respawn to apply"),
                DescriptorRow("MF_IS_BUS", "Marks as a bus and as big (IS_BIG). The 2 front doors open to 82.8 degrees instead of 72. Tow arm cars do not hook it automatically, and the player's head does not turn to the rear-view mirror. Requires a respawn to apply"),
                DescriptorRow("MF_IS_LOW", "No known effect. The game marks the vehicle as low at spawn, but nothing reads that marker"),
                DescriptorRow("MF_IS_RC", "Marks as remotely controlled: The ped turns invisible with collision off and cannot be pulled out, and the engine is forced on. Ground clearance is forced to at least 0.15 meters, even with DONT_FORCE_GRND_CLEARANCE, and air control strength is set to 2.5, replacing every other air control scale"),
                DescriptorRow("MF_IS_VAN", "For vans with double rear doors: The rear doors open wider (90 degrees). Requires a respawn to apply"),
                DescriptorRow("MF_NO_1STPERSON_LOOKBEHIND", "No effect. Never read by the game"),
                DescriptorRow("MF_NO_DOORS", "No effect. Never read by the game"),
                DescriptorRow("MF_NO_EXHAUST", "Disables the faint engine idle exhaust smoke, and nitrous flames. Backfire flames remain"),
                DescriptorRow("MF_NO_WHEEL_BREAK", "Makes wheel bones unbreakable due to damage, explosions excluded. Requires a respawn to apply"),
                DescriptorRow("MF_NO_WHEEL_BURST", "Forces bulletproof tires on this vehicle by default. They cannot be toggled off while this is set. Requires a respawn to apply"),
                DescriptorRow("MF_SIT_IN_BOAT", "No effect. Never read by the game"),
                DescriptorRow("MF_TANDEM_SEATING", "No effect. Never read by the game"),
            }, false, captureInto);
            // HF_LAST_AVAILABLE_FLAG omitted (nullsub)
            AppendCategory(sb, "HandlingFlagBits", customizations, new[]
            {
                DescriptorRow("HF_ALT_EXT_WHEEL_BOUNDS_BEH", "Only for models with grouped wheel collision bounds: Pushes those extra bounds forward for front wheels and backward for rear ones so they stick out past them. Also lets them collide with the ground and other surfaces at any angle, unless the vehicle has FLAG_HAS_RAMP (vehicles.meta). No effect on bikes, or with EXT_WHEEL_BOUNDS_COL set"),
                DescriptorRow("HF_ALT_EXT_WHEEL_BOUNDS_SHRINK", "Only for models with grouped wheel collision bounds: While a driver is in, makes those extra bounds much thinner and raises them by half the wheel's width. Pushes them further out with ALT_EXT_WHEEL_BOUNDS_BEH. No effect on bikes, or with EXT_WHEEL_BOUNDS_COL set"),
                DescriptorRow("HF_EXT_WHEEL_BOUNDS_COL", "Only for models with grouped wheel collision bounds: Stops peds from vaulting onto the exposed wheels from the front or back, lets those bounds collide with surfaces at any angle unless the vehicle has FLAG_HAS_RAMP (vehicles.meta), and includes them in steering when STEER_ALL_WHEELS is set. No effect on bikes. Requires a respawn to apply"),
                DescriptorRow("HF_ARMOURED", "Makes wheels/doors unbreakable from explosions or melee damage, and protects the fuel tank from bullets and collisions"),
                DescriptorRow("HF_CVT", "Continuously variable transmission: The ratio slides from 5.0 down to the gear's own ratio as speed rises, and eases off with less throttle. Typically used when InitialDriveGears is 1 or for electric vehicles"),
                DescriptorRow("HF_DONT_RAISE_BOUNDS_AT_SPEED", "Disables dynamic clipping of the bottom of the collision bounds based on speed. The car will not clip into the ground as much with this set. Applies even with hydraulic suspension. No effect on planes, helicopters, boats, submarines, motorcycles, and bicycles"),
                DescriptorRow("HF_ENABLE_LEAN", "Only on cars with BikeHandlingData. In the air, steering spins the vehicle instead of rolling it over. On the ground, it shifts the center of mass, adds wheelie force, and stabilizes braking from BikeHandlingData, and levels the vehicle sideways instead of leaning it into turns. Requires a respawn to apply"),
                DescriptorRow("HF_FORCE_NO_TC_OR_SC", "Disables traction and stability control for bikes"),
                DescriptorRow("HF_FREEWHEEL_NO_GAS", "Lowers the rolling friction multiplier of the wheels while coasting off the throttle from 0.08 to 0.02. Below about 3 MPH, up to 0.17 more friction is added so the vehicle still comes to a stop"),
                DescriptorRow("HF_HANDBRAKE_REARWHEELSTEER", "Steers the rear wheels along with the front ones when handbrake is held. Handbrake will not apply any brake force or act as a parking brake"),
                DescriptorRow("HF_STEER_ALL_WHEELS", "Steers all wheels. Rear wheels will respect SteeringLock as well. No effect on bikes"),
                DescriptorRow("HF_STEER_NO_WHEELS", "Disables steering of all wheels. Requires a respawn to apply"),
                DescriptorRow("HF_STEER_REARWHEELS", "Switches steering control to the rear/middle wheels only. Requires a respawn to fully apply"),
                DescriptorRow("HF_HAS_KERS", "Partially enables KERS. Disables the horn and shows the recharge bar below the minimap. KERS needs to be enabled via SET_VEHICLE_KERS_ALLOWED, the boost itself only works in GTA:O"),
                DescriptorRow("HF_HAS_RALLY_TYRES", "Inverts TractionCurves. Grip starts at TractionCurveMin and increases up to TractionCurveMax on slip"),
                DescriptorRow("HF_HEAVYARMOUR", "Stops a nearby high-damage explosion from exploding the vehicle instantly while its health is above 0. Damage still applies"),
                DescriptorRow("HF_IMPROVED_RIGHTING_FORCE", "Doubles air control and rollover righting force"),
                DescriptorRow("HF_REDUCED_RIGHTING_FORCE", "Reduces air control and rollover righting force to 45%"),
                DescriptorRow("HF_LESS_SNOW_SINK", "No effect. Never read by the game"),
                DescriptorRow("HF_LOW_SPEED_WHEELIES", "Adds extra control against extreme wheelie angles at low speed while the rear wheel(s) is on the ground, and lets motorcycles lean back into a wheelie without throttle. Used with CF_CAN_WHEELIE and on motorcycles"),
                DescriptorRow("HF_NO_HANDBRAKE", "Handbrake will act as a parking brake only: Will not slow the vehicle down from speed but still holds it at a standstill for revving"),
                DescriptorRow("HF_NO_REVERSE", "Disables the vehicle's reverse gear"),
                DescriptorRow("HF_OFFROAD_ABILITIES", "Increases the gravity on the vehicle by 10% (1.1x). Ignored by bikes and quads, which set their own gravity"),
                DescriptorRow("HF_OFFROAD_ABILITIES_X2", "Increases gravity by 20% (1.2x), gives 1.5x the air control and righting force, ignores drag from bushes, spins faster in burnouts, and auto-levels out while in the air"),
                DescriptorRow("HF_OFFROAD_INCREASED_GRAVITY_NO_FOLIAGE_DRAG", "Identical to HF_OFFROAD_ABILITIES_X2, but does not apply self-leveling in the air"),
                DescriptorRow("HF_REDUCED_DRIVE_OVER_DAMAGE", "Stops collisions with other vehicles from damaging this vehicle's fuel tank"),
                DescriptorRow("HF_REDUCED_MOD_MASS", "Reduces the mass (10kg → 1kg) given to upgrade parts within the vehicle model's collision bound weight distribution. Total Mass remains the same. Requires an unload to apply"),
                DescriptorRow("HF_SELF_RIGHTING_IN_WATER", "Automatically rolls a boat or amphibious vehicle back over if flipped in water. Requires an unload to apply"),
                DescriptorRow("HF_SMOOTHED_COMPRESSION", "Smooths suspension compression by blending it between each physics step's ground check, so the spring force changes gradually over bumps instead of snapping. Hardcoded (enabled) for every vehicle, toggling this has no effect"),
                DescriptorRow("HF_TYRES_CAN_CLIP", "Simulates tire compression by letting them clip into the ground by up to 85% of the gap between tire and rim when the suspension bottoms out, for improved handling. Rim size is defined by wheelScales in vehicles.meta, or from rimRadius in carcols.meta for custom wheels"),
                DescriptorRow("HF_TYRES_RAISE_SIDE_IMPACT_THRESHOLD", "Raises the wheel bounds side thresholds against other vehicles, so contact isn't missed when driving over them at an angle"),
            }, false, captureInto);
            AppendCategory(sb, "DamageFlagBits", customizations, new[]
            {
                DescriptorRow("DF_BONNET", "Marks the hood (bonnet) bone as unbreakable due to collisions. Requires a respawn to apply"),
                DescriptorRow("DF_BOOT", "Marks the trunk (boot) bones as unbreakable due to collisions. Requires a respawn to apply"),
                DescriptorRow("DF_DRIVER_PASSENGER_SIDE_FRONT_DOOR", "Marks the passenger-side front door (door_pside_f) bone as unbreakable due to collisions. Requires a respawn to apply"),
                DescriptorRow("DF_DRIVER_PASSENGER_SIDE_REAR_DOOR", "Marks the passenger-side rear door (door_pside_r) bone as unbreakable due to collisions. Requires a respawn to apply"),
                DescriptorRow("DF_DRIVER_SIDE_FRONT_DOOR", "Marks the driver-side front door (door_dside_f) bone as unbreakable due to collisions. Requires a respawn to apply"),
                DescriptorRow("DF_DRIVER_SIDE_REAR_DOOR", "Marks the driver-side rear door (door_dside_r) bone as unbreakable due to collisions. Requires a respawn to apply"),
            }, false, captureInto);
            AppendCategory(sb, "AdvancedFlagBits", customizations, new[]
            {
                DescriptorRow("CF_ALLOW_REDUCED_SUSPENSION_FORCE", "Allows scripts to lower the vehicle's stance. Requires FIX_OLD_BUGS"),
                DescriptorRow("CF_ALLOW_TURN_ON_SPOT", "Allows rotating on the spot by applying an independent amount of throttle to every wheel on one side of the vehicle"),
                DescriptorRow("CF_ALLOWS_EXTENDED_MODS", "Adds turbo-tuning mods for slot 20 (VMT_KNOB) and power-tuning mods for slot 22 (VMT_ICE), requires AdvancedData"),
                DescriptorRow("CF_ASSIST_STABILITY_CONTROL", "No effect. Never read by the game"),
                DescriptorRow("CF_ASSIST_TRACTION_CONTROL", "Disables traction control to allow for unrestricted drifting"),
                DescriptorRow("CF_BLOCK_INCREASED_ROT_VELOCITY_WITH_DRIVE_FORCE", "Limits how fast spinning wheels can speed up under power below 10 m/s (not during burnouts)"),
                DescriptorRow("CF_CAN_WHEELIE", "Allows the vehicle to wheelie after revving it via the handbrake from a standstill. Hardcoded on the Muscle class and Tornado6"),
                DescriptorRow("CF_CLOSE_RATIO_GEARBOX", "Extends 1st gear duration: Slower launch with reduced wheelspin. Requires a respawn to apply"),
                DescriptorRow("CF_DIFF_CENTRE", "Center differential: Sends more drive force to the axle whose wheels turn slower (except during a burnout), kept within MaxDriveBiasTransfer of DriveBiasFront"),
                DescriptorRow("CF_DIFF_FRONT", "Open front differential: Splits the front drive force between the 2 wheels by their speed, so a spinning wheel takes more of it"),
                DescriptorRow("CF_DIFF_REAR", "Open rear differential: Splits the rear drive force between the 2 wheels by their speed, so a spinning wheel takes more of it"),
                DescriptorRow("CF_DIFF_LIMITED_CENTRE", "Identical to DIFF_CENTRE but never enables the center split that sends more power to the slower axle, so it has no effect on its own. Use DIFF_CENTRE instead"),
                DescriptorRow("CF_DIFF_LIMITED_FRONT", "Limited-slip front differential. With DIFF_FRONT, it sends more power to the slower front wheel once one wheel turns about 1.6x faster than the other. With DIFF_LOCKING_FRONT, the lock starts once the speeds differ by 10% and is full at 20%"),
                DescriptorRow("CF_DIFF_LIMITED_REAR", "Limited-slip rear differential. With DIFF_REAR, it sends more power to the slower rear wheel once one wheel turns about 1.6x faster than the other. With DIFF_LOCKING_REAR, the lock starts once the speeds differ by 10% and is full at 20%"),
                DescriptorRow("CF_DIFF_LOCKING_CENTRE", "No effect. Never read by the game"),
                DescriptorRow("CF_DIFF_LOCKING_FRONT", "Locks the 2 front wheels to the same speed whenever both touch the ground. With DIFF_LIMITED_FRONT the lock only engages as their speeds differ"),
                DescriptorRow("CF_DIFF_LOCKING_REAR", "Locks the 2 rear wheels to the same speed whenever both touch the ground. With DIFF_LIMITED_REAR the lock only engages as their speeds differ"),
                DescriptorRow("CF_ENABLE_WHEEL_BLOCKER_SIDE_IMPACTS", "Only for models with grouped wheel collision bounds: Allows those bounds to collide with and slide off of tilted side surfaces, like curb bevels and slopes met side-on, while driven" + "\n~y~Note: I found no noticeable effect during testing"),
                DescriptorRow("CF_FIX_OLD_BUGS", "Forces stock-wheel bound clipping range for custom wheels, and prevents lowering the vehicle by shooting its wheels. Also switches to a newer suspension spring model. Required by ALLOW_REDUCED_SUSPENSION_FORCE"),
                DescriptorRow("CF_FORCE_SMOOTH_RPM", "Idles at half the normal RPM and slows how fast the RPM climbs near the top at full throttle so it doesn't spike over bumps"),
                DescriptorRow("CF_GEARBOX_DIRECT_SHIFT", "No effect. Never read by the game"),
                DescriptorRow("CF_GEARBOX_ELECTRIC", "No effect. Never read by the game. Electric behavior comes from FLAG_IS_ELECTRIC in vehicles.meta"),
                DescriptorRow("CF_GEARBOX_FULL_AUTO", "No effect. Never read by the game"),
                DescriptorRow("CF_GEARBOX_MANUAL", "Disables automatic gear shifting, enables gear shift controls, and sets the clutch value to 0.0 when idling" + "\n~y~Note: The game automatically forces HARD_REV_LIMIT on, as it's required for this to work properly"),
                DescriptorRow("CF_HARD_REV_LIMIT", "Adds an RPM limiter, causing the vehicle to upshift earlier with an automatic transmission. Hardcoded for Tuners DLC cars, and for the Viseris when its AdvancedFlags are 0"),
                DescriptorRow("CF_HOLD_GEAR_WITH_WHEELSPIN", "Shifts gears up later than normal, sometimes hitting the gear's RPM limit before shifting. Ignored with drift tires"),
                DescriptorRow("CF_INCREASE_SUSPENSION_FORCE_WITH_SPEED", "Dynamically increases suspension spring force with speed. Capped at an effective SuspensionForce of 6. Requires a respawn to apply"),
                DescriptorRow("CF_REDUCE_BODY_ROLL_WITH_SUSPENSION_MODS", "Reduces bodyroll per suspension upgrade equipped"),
                DescriptorRow("CF_REDUCED_SELF_RIGHTING_SPEED", "Caps how fast air control can spin the vehicle at a 5th of the normal limit, flipping back over is also slower, sometimes impossible"),
                DescriptorRow("CF_USE_DOWNFORCE_BIAS", "Changes downforce/spoiler tuning to the OpenWheel class configuration. Each mod needs AdvancedData values to affect downforce"),
            }, false, captureInto);
            AppendCategory(sb, "SpecialFlightFlagBits", customizations, new[]
            {
                DescriptorRow("SF_DONT_RETRACT_WHEELS", "No effect. Never read by the game"),
                DescriptorRow("SF_FORCE_MIN_THROTTLE_WHEN_TURNING", "Forces a minimum throttle (up to 30%) when steering at speed"),
                DescriptorRow("SF_FORCE_SPECIAL_FLIGHT_WHEN_DRIVEN", "Forces special flight on while driven, it cannot be toggled off. Mode still decides hover or glider"),
                DescriptorRow("SF_LIMIT_FORCE_DELTA", "Limits how different the hover force at each wheel can be from the average while touching the ground"),
                DescriptorRow("SF_MAINTAIN_HOVER_HEIGHT", "Enables adaptive hovering height for terrain surface level changes"),
                DescriptorRow("SF_REDUCE_DRAG_WITH_SPEED", "Blends the hover damping toward the Min damping values as forward speed rises, reducing drag at speed"),
                DescriptorRow("SF_STEER_TOWARDS_VELOCITY", "Enables StabilityAssist: Auto-recenters the vehicle toward its direction of travel after steering"),
                DescriptorRow("SF_WORKS_UPSIDE_DOWN", "Makes hover thrust keep working while upside down, extra drag enforces the hover top speed, the handbrake removes the remaining gravity, and hover height is measured along the body. Also disables animation of the thruster bones (misc_c/e)"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_CAR, customizations, new[]
            {
                DescriptorRow("BackEndPopUpCarImpulseMult", "Upward force applied to the rear end of the vehicle to lift it off the ground in a frontend vehicle to vehicle collision. Scales with the collision's force. No effect if BackEndPopUpMaxDeltaSpeed = 0"),
                DescriptorRow("BackEndPopUpBuildingImpulseMult", "Upward force applied to the rear end of the vehicle to lift it off the ground in a frontend vehicle to map collision. Scales with the collision's force. No effect if BackEndPopUpMaxDeltaSpeed = 0"),
                DescriptorRow("BackEndPopUpMaxDeltaSpeed", "Caps the impulse force on the rear end, in m/s. The force can never be stronger than one that would lift the whole vehicle at this speed"),
                DescriptorRow("ToeFront", "Adjusts the toe, in radians, of the vehicle's front wheels; rear if HF_STEER_REARWHEELS is set. Positive = inwards; outwards with HF_STEER_REARWHEELS"),
                DescriptorRow("ToeRear", "Adjusts the toe, in radians, of the vehicle's rear wheels; front if HF_STEER_REARWHEELS. Positive = inwards. No effect when HF_STEER_ALL_WHEELS is set, ToeFront is used for both axles instead"),
                DescriptorRow("CamberFront", "Adjusts the camber, in radians, of the front wheels. Requires a respawn to apply physically. Positive = inwards angle"),
                DescriptorRow("CamberRear", "Adjusts the camber, in radians, of the rear wheels. Requires a respawn to apply physically. Positive = inwards angle"),
                DescriptorRow("Castor", "Adjusts the caster angle, in radians, of steered wheels. Visual only"),
                DescriptorRow("EngineResistance", "Reduces engine power at partial throttle only (analog input), in forward gears above 10% RPM. No effect with throttle fully released or pressed, or when HF_CVT is set"),
                DescriptorRow("MaxDriveBiasTransfer", "Requires CF_DIFF_CENTRE. Affects differentials: transfers the drive force from the slipping wheels to the less-driven wheels. -1 = disabled, 0 locks the split, and higher values let it slide further. Any value above -1 powers every wheel, which requires a respawn to apply"),
                DescriptorRow("JumpForceScale", "Adjusts the force of jump boost for vehicles which have it enabled. Higher = more force"),
                DescriptorRow("IncreasedRammingForceScale", "Scales the sideways shove a ramming vehicle gives when its front hits another vehicle's side above 10 m/s. Only used by vehicles.meta's ramming flags and the Kuruma2, and only against police vehicles unless the vehicle has FLAG_HAS_INCREASED_RAMMING_FORCE_VS_ALL_VEHICLES"),
                DescriptorRow("AdvancedData", "Per-mod-slot tuning entries. What Value does depends on the slot:" + "\n- Wheelies (muscle class, Tornado6, or CF_CAN_WHEELIE): Torque multiplier" + "\n- If CF_USE_DOWNFORCE_BIAS: Downforce multiplier on the spoiler or front bumper" + "\n- If CF_ALLOWS_EXTENDED_MODS: HSW top speed percentage, or turbo power"),
                DescriptorRow("AdvancedDataSlot", "Vehicle mod slot this entry affects, in the game's own mod numbering, which differs from SET_VEHICLE_MOD past slot 10. -1 = any slot, which only downforce entries accept"),
                DescriptorRow("AdvancedDataIndex", "Which part (mod index) must be installed in the targeted slot for this entry to apply. Downforce entries use the part's index in the whole mod kit instead"),
                DescriptorRow("AdvancedDataValue", "Value used when that part is installed: Wheelie torque multiplier, downforce multiplier (spoiler/front bumper), HSW top speed percentage, or turbo power, depending on the slot"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_BIKE, customizations, new[]
            {
                DescriptorRow("LeanForwardCOMMultiplier", "How much the center of gravity is shifted by leaning forward"),
                DescriptorRow("LeanForwardForceMultiplier", "How much force is applied when leaning forward. Only applies while braking or with both wheels off the ground. On the ground, it fades in with speed"),
                DescriptorRow("LeanBackCOMMultiplier", "How much the center of gravity is shifted by leaning back"),
                DescriptorRow("LeanBackForceMultiplier", "How much force is applied when leaning back, to lift a wheelie. On the ground it's also scaled by throttle, unless HF_LOW_SPEED_WHEELIES is set. Bicycles in the air use half (0.5) of it, or a tenth (0.1) during a lateral jump"),
                DescriptorRow("MaxBankAngle", "Max lean angle of the bike when cornering, in degrees. The lean slows as it nears this value and the ped's lean follows it. Vehicles with HF_ENABLE_LEAN level sideways only while it is above 0, with a rider"),
                DescriptorRow("FullAnimAngle", "No effect. The rider's body lean uses MaxBankAngle instead"),
                DescriptorRow("DesLeanReturnFraction", "Smoothing of lean toward its target angle. Higher values make leaning respond slower. When the rider picks the bike up, it's replaced by 0"),
                DescriptorRow("StickLeanMultiplier", "Sideways force scaled by gravity from steering input. Below 50% of InitialDriveMaxFlatVelocity (75% for bicycles) the stick force fades with the square of the speed ratio toward standstill and the tire side force with the ratio itself. Negative values invert the lean direction"),
                DescriptorRow("BrakingStabilityMultiplier", "Yaw torque force that straightens the bike against sideways sliding while braking. Also limits the side acceleration from slip. Negative values straighten out more, positive pushes the rear out more"),
                DescriptorRow("InAirSteerMultiplier", "Air control strength. Sets the auto-level and heading assist while airborne, and scales the air control yaw torque from the lean input. 0 disables self-leveling"),
                DescriptorRow("WheelieBalancePoint", "Pitch angle in degrees the bike tries to hold during a wheelie, measured against the ground under the rear wheel. It only pushes the front down once past this angle, and does not lift the bike up to it"),
                DescriptorRow("StoppieBalancePoint", "Pitch angle in degrees the bike tries to hold during a stoppie. It only pushes the front up once past this angle, and does not tip the bike forward to it"),
                DescriptorRow("WheelieSteerMultiplier", "Steering strength while doing a wheelie or stoppie"),
                DescriptorRow("RearBalanceMultiplier", "How strongly the bike holds its wheelie balance point. Faded in with speed. The wheelie ability stat scales it on bicycles only when enabled by script"),
                DescriptorRow("FrontBalanceMultiplier", "Motorcycles only: How strongly the bike holds its stoppie balance point. Faded in with speed"),
                DescriptorRow("GroundSideFrictionMultiplier", "Friction of the bike's body against the ground while it slides on its side. Higher values stop sliding sooner"),
                DescriptorRow("WheelGroundSideFrictionMultiplier", "No effect. Never read by the game"),
                DescriptorRow("OnStandLeanAngle", "Lean angle in degrees when parked idle, or on the stand. Used by the pick-up task"),
                DescriptorRow("OnStandSteerAngle", "Handlebar angle in radians when parked on the stand"),
                DescriptorRow("JumpForce", "Force of the bike's jump (bunny hop). 0 or less = disabled"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_FLYING, customizations, BuildFlyingFieldDescriptors(), false, captureInto);
            // A vehicle can carry both entries at once (e.g. a VTOL plane), distinguished by a handlingType XML tag rather than a separate struct
            AppendCategory(sb, CATEGORY_VERTICAL_FLYING, customizations, BuildFlyingFieldDescriptors(), false, captureInto);
            AppendCategory(sb, CATEGORY_SPECIAL_FLIGHT, customizations, new[]
            {
                DescriptorRow("Mode", "0 = Glider, 1 = Hover. Glider mode requires FLAG_HAS_GLIDER in vehicles.meta to fly. Without it, special flight still toggles, but with no wings or lift"),
                DescriptorRow("AngularDampingX", string.Format(DESC_AXIS_ANGULAR_DAMPING, "X")),
                DescriptorRow("AngularDampingY", string.Format(DESC_AXIS_ANGULAR_DAMPING, "Y")),
                DescriptorRow("AngularDampingZ", string.Format(DESC_AXIS_ANGULAR_DAMPING, "Z")),
                DescriptorRow("AngularDampingMinX", string.Format(DESC_AXIS_ANGULAR_DAMPING_MIN, "X")),
                DescriptorRow("AngularDampingMinY", string.Format(DESC_AXIS_ANGULAR_DAMPING_MIN, "Y")),
                DescriptorRow("AngularDampingMinZ", string.Format(DESC_AXIS_ANGULAR_DAMPING_MIN, "Z")),
                DescriptorRow("LinearDampingX", "Sideways movement damping while hovering. With SF_REDUCE_DRAG_WITH_SPEED set, this blends with forward speed times throttle"),
                DescriptorRow("LinearDampingY", "Forward movement damping while hovering. With SF_REDUCE_DRAG_WITH_SPEED set, this blends toward LinearDampingMinY with forward speed"),
                DescriptorRow("LinearDampingZ", "Vertical movement damping while hovering"),
                DescriptorRow("LinearDampingMinX", string.Format(DESC_AXIS_LINEAR_DAMPING_MIN, "X")),
                DescriptorRow("LinearDampingMinY", string.Format(DESC_AXIS_LINEAR_DAMPING_MIN, "Y")),
                DescriptorRow("LinearDampingMinZ", "No effect. Never read by the game"),
                DescriptorRow("LiftCoefficient", "Wing lift strength, scaled with speed squared. Glider mode caps the speed at MaxLiftVelocity, hover mode doesn't"),
                DescriptorRow("CriticalLiftAngle", "Glider mode only. Wing angle of attack in degrees where lift peaks"),
                DescriptorRow("InitialLiftAngle", "Glider mode only. Built-in wing angle in degrees added to pitch, so the wing generates lift when level"),
                DescriptorRow("MaxLiftAngle", "Glider mode only. Extra wing angle of attack in degrees at full pitch input"),
                DescriptorRow("DragCoefficient", "Glider mode only. Wing drag"),
                DescriptorRow("BrakingDrag", "Glider mode only. Extra drag while braking"),
                DescriptorRow("MaxLiftVelocity", "Glider mode only. Cap on the speed (squared) used for lift"),
                DescriptorRow("MinLiftVelocity", "Glider mode only. Speed below which the wings generate no lift"),
                DescriptorRow("RollTorqueScale", "Roll control strength from the left-right stick, used in hover and glider mode. Glider roll only works while airborne"),
                DescriptorRow("MaxTorqueVelocity", "Glider mode only. Speed (squared) of full control strength"),
                DescriptorRow("MinTorqueVelocity", "Glider mode only. Speed (squared) where yaw control fades to 0 and stick pitch control to 25%. Speed is capped by MaxLiftVelocity first, so a value above it never completes the fade"),
                DescriptorRow("YawTorqueScale", "Yaw control strength. In hover mode it's the left-right steering force. In glider mode it's a weathervane that turns the nose into the airflow, and only takes effect in the air. Hover mode multiplies it by the vehicle's inertia and glider mode does not, so the same value is stronger for hover"),
                DescriptorRow("SelfLevelingPitchTorqueScale", "Glider mode only. Levels the nose with the airflow"),
                DescriptorRow("MaxPitchTorque", "Glider mode only. Cap on pitch rotation strength"),
                DescriptorRow("MaxSteeringRollTorque", "Glider mode only. Cap on roll rotation strength"),
                DescriptorRow("PitchTorqueScale", "Pitch control strength from the up-down stick. Used in hover and glider mode. Hover mode multiplies it by the vehicle's inertia and glider mode does not. In glider mode it adds to the self-leveling torque, and MaxPitchTorque caps the sum"),
                DescriptorRow("SteeringTorqueScale", "Glider mode only. Steering force while airborne"),
                DescriptorRow("MaxThrust", "Thrust force at full throttle. In glider mode, only while the engine is off"),
                DescriptorRow("TransitionDuration", "Time in seconds of the transformation between land and hover mode. 0 = instant"),
                DescriptorRow("HoverVelocityScale", "Top speed multiplier in hover mode based on InitialDriveMaxFlatVelocity. Ignored by the Oppressor Mk2"),
                DescriptorRow("StabilityAssist", "Requires SF_STEER_TOWARDS_VELOCITY. Yaw force that turns the vehicle toward its direction of travel"),
                DescriptorRow("MinSpeedForThrustFalloff", "Fraction of top speed where thrust starts fading out, reaching 0 at top speed. With 0 the fade starts at standstill instead of being off"),
                DescriptorRow("BrakingThrustScale", "Reverse thrust strength when braking. When above 0, the thrust is multiplied by this value and clamped so it can stop the vehicle but not reverse it within one step (at least 10% of MaxThrust stays available), 0 leaves the thrust unscaled and unclamped"),
                DescriptorRow("Flags", "Hover Mode flags: Alters hover mode behavior"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_BOAT, customizations, new[]
            {
                DescriptorRow("BoxFrontMultiplier", "How far forward, as a fraction of the bounding box's size, the water sample points reach. Requires an unload to apply"),
                DescriptorRow("BoxRearMultiplier", "How far back, as a fraction of the bounding box's size, the water sample points reach. Requires an unload to apply"),
                DescriptorRow("BoxSideMultiplier", "How far to each side, as a fraction of the bounding box's size, the water sample points reach. Requires an unload to apply"),
                DescriptorRow("SampleTop", "Height in meters where the water sample points start. Requires an unload to apply"),
                DescriptorRow("SampleBottom", "How far below the hull the water sample points sit in meters. Requires an unload to apply"),
                DescriptorRow("SampleBottomTestCorrection", "Raises the bottom of the hull check, as a fraction of the bounding box's height. Requires an unload to apply"),
                DescriptorRow("AquaplaneForce", "Planing lift: How much the hull rises out of the water at speed. Requires an unload to apply"),
                DescriptorRow("AquaplanePushWaterMultiplier", "How strongly planing pushes the water down under the hull"),
                DescriptorRow("AquaplanePushWaterCap", "Limit in m/s on how fast planing pushes the water down"),
                DescriptorRow("AquaplanePushWaterApply", "How strongly planing push is blended in"),
                DescriptorRow("RudderForce", "Rudder turning force"),
                DescriptorRow("RudderOffsetSubmerge", "Vertical offset in meters of the point, from the bone or bounding box center height, used to determine if the propeller is under water"),
                DescriptorRow("RudderOffsetForce", "Vertical offset in meters of the point where thrust is applied. A lower point pitches the bow up under power"),
                DescriptorRow("RudderOffsetForceZMultiplier", "Scales the height of the point where force is applied. Only used by a boat with no propeller bones. With propeller bones, the thrust point is the average of the bones and this does nothing"),
                DescriptorRow("WaveAudioMultiplier", "No effect. Never read by the game"),
                DescriptorRow("MoveResistanceX", "Horizontal water drag. Requires an unload to apply"),
                DescriptorRow("MoveResistanceY", "Drag that pushes up, so it acts while the hull sinks. Requires an unload to apply"),
                DescriptorRow("MoveResistanceZ", "Drag that pushes down, so it acts while the hull rises. Requires an unload to apply"),
                DescriptorRow("TurnResistanceX", string.Format(DESC_AXIS_BOAT_TURN_RESISTANCE, "X")),
                DescriptorRow("TurnResistanceY", string.Format(DESC_AXIS_BOAT_TURN_RESISTANCE, "Y")),
                DescriptorRow("TurnResistanceZ", string.Format(DESC_AXIS_BOAT_TURN_RESISTANCE, "Z")),
                DescriptorRow("LookLRCamHeight", "No effect. Never read by the game"),
                DescriptorRow("DragCoefficient", "Water drag of the hull, once it moves faster than about 0.3 m/s through the water. Up to 10x stronger near a waterfall"),
                DescriptorRow("KeelSphereSize", "When above 0, this adds keel spheres of this size that grip the water and help steering. 0 or less uses an older keel force model instead. Requires an unload to apply because the spheres are built only when the model is loaded for the first time, so a plain edit to this only switches between the 2 keel models"),
                DescriptorRow("PropRadius", "Propeller size in meters. Drive force scales with how much of this radius is below the water line at the propeller point, so thrust drops as it leaves the water"),
                DescriptorRow("LowLodAngOffset", "Pitch angle in radians that a distant boat sits at on calm water in low-lod buoyancy mode. 0 = level with the water"),
                DescriptorRow("LowLodDraughtOffset", "Height in meters of this boat's origin above the water level while it is far away in low-lod buoyancy mode"),
                DescriptorRow("ImpellerOffset", "Where the downwards suction point sits along the hull, as a fraction of the boat's rear length measured from the model origin. While accelerating, with the propeller in the water, it pulls the hull down at that point by ImpellerForceMultiplier. Higher moves it backwards. 0 = no suction. Requires DriveBiasFront at 0.9 or less"),
                DescriptorRow("ImpellerForceMultiplier", "Strength of the downward suction force at the impeller point"),
                DescriptorRow("DinghySphereBuoyConstant", "When above 0, this makes this boat an inflatable one, with extra buoyancy around its edges. Requires an unload to apply"),
                DescriptorRow("ProwRaiseMultiplier", "Player driven only: How much the bow lifts up at speed while it's in the water"),
                DescriptorRow("DeepWaterSampleBuoyancyMultiplier", "Buoyancy multiplier of parts of the hull that are fully under water"),
                DescriptorRow("TransmissionMultiplier", "Multiplies the boat's drive force"),
                DescriptorRow("TractionMultiplier", "Multiplies keel grip in the water, based on TractionCurveLateral. Requires an unload to apply"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_SEA_PLANE, customizations, new[]
            {
                DescriptorRow("LeftPontoonComponentId", "Component ID of the left pontoon's collision within a grouped chassis. Requires an unload to apply"),
                DescriptorRow("RightPontoonComponentId", "Component ID of the right pontoon's collision within a grouped chassis. Requires an unload to apply"),
                DescriptorRow("PontoonBuoyConstant", "Buoyancy of the pontoons. Requires an unload to apply"),
                DescriptorRow("PontoonSampleSizeFront", "Size of the front pontoon water samples. Requires an unload to apply"),
                DescriptorRow("PontoonSampleSizeMiddle", "Size of the middle pontoon water samples. Requires an unload to apply"),
                DescriptorRow("PontoonSampleSizeRear", "Size of the rear pontoon water samples. Requires an unload to apply"),
                DescriptorRow("PontoonLengthFractionForSamples", "How much of each pontoon's length is covered by water samples. Requires an unload to apply"),
                DescriptorRow("PontoonDragCoefficient", "Water drag of the pontoons, replacing the vehicle's own drag coefficient while they move through the water above a low speed"),
                DescriptorRow("PontoonVerticalDampingUp", "Vertical water damping of the pontoons while sinking"),
                DescriptorRow("PontoonVerticalDampingDown", "Vertical water damping of the pontoons while rising"),
                DescriptorRow("KeelSphereSize", "No effect. Never read by the game, only BoatHandlingData's KeelSphereSize is used"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_SUBMARINE, customizations, new[]
            {
                DescriptorRow("TurnResistanceX", DESC_SUBMARINE_TURN_RESISTANCE),
                DescriptorRow("TurnResistanceY", DESC_SUBMARINE_TURN_RESISTANCE),
                DescriptorRow("TurnResistanceZ", DESC_SUBMARINE_TURN_RESISTANCE),
                DescriptorRow("MoveResistanceXY", "Horizontal water drag. Requires an unload to apply"),
                DescriptorRow("MoveResistanceZ", "Vertical water drag. Its force can grow up to 10x larger against sinking than against rising. Requires an unload to apply"),
                DescriptorRow("PitchMultiplier", "Pitch control strength"),
                DescriptorRow("PitchAngle", "Old steering model only: Maximum pitch angle in degrees the sub aims for at full pitch input. Subs with both rudder and elevator_l bones use the new model and ignore this"),
                DescriptorRow("YawMultiplier", "Turning strength"),
                DescriptorRow("DiveSpeed", "How fast the sub dives and surfaces"),
                DescriptorRow("RollMultiplier", "New steering model only (subs with both rudder and elevator_l bones): Roll torque, how far the sub banks into a turn"),
                DescriptorRow("RollStabilize", "New steering model only: Torque that levels the roll"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_TRAILER, customizations, new[]
            {
                DescriptorRow("AttachLimitPitch", "Maximum pitch angle in degrees between the trailer and the truck while attached. 0 locks it, and negative removes the limit. Applies when the trailer attaches"),
                DescriptorRow("AttachLimitRoll", "Maximum roll angle in degrees between the trailer and the truck while attached. 0 locks it, and negative removes the limit. Applies when the trailer attaches"),
                DescriptorRow("AttachLimitYaw", "Maximum yaw angle in degrees between the trailer and the truck while attached. 0 locks it, and negative removes the limit. Applies when the trailer attaches"),
                DescriptorRow("UprightSpringConstant", "How strongly the trailer is kept upright relative to the truck while attached. Negative = more resistance to rolling. The game replaces the \"Trailers\" model's value with -36 once at startup, and later edits apply"),
                DescriptorRow("UprightDampingConstant", "Damping of the upright force while attached. Higher = smoother motion. The game replaces the trailers model's value with 30 once at startup, and later edits apply"),
                DescriptorRow("AttachedMaxDistance", "How far the trailer may pull away from the hitch when above 0, simulating a wired attachment. 0 or less uses a ball hitch. Applies when the trailer attaches"),
                DescriptorRow("AttachedMaxPenetration", "Extra slack added to both ends of the hitch distance when AttachedMaxDistance is above 0, letting the trailer push closer and pull farther. Applies when the trailer attaches"),
                DescriptorRow("AttachRaiseZ", "Only applies to a ball hitch, and ignored when AttachedMaxDistance is above 0. Each time the trailer collides with its tow vehicle, both hitch points are raised by this height in meters for a moment so the two separate"),
                DescriptorRow("PositionConstraintMassRatio", "Mass ratio used by the hitch: How much the trailer's motion can influence the truck. Applies when the trailer attaches. Lower = heavier"),
            }, false, captureInto);
            AppendCategory(sb, CATEGORY_WEAPON, customizations, new[]
            {
                DescriptorRow("WeaponHash", "Weapon this slot uses. Requires a respawn to apply"),
                DescriptorRow("WeaponSeat", "Vehicle seat the player has to be in to use the weapon. Requires a respawn to apply"),
                DescriptorRow("WeaponVehicleModType", "Vehicle modification slot the weapon is tied to/enabled by. Requires a respawn to apply"),
                DescriptorRow("TurretSpeed", "Turn speed of the weapon turret in radians per second. Requires a respawn to apply"),
                DescriptorRow("TurretPitchMin", "Minimum artificial pitch limit, in radians, within the bone rotation limits, of the turret's barrel while a player aims, and only when any of the 4 turret pitch fields below is non-zero. Ignored by NPCs unless BulletVelocityForGravity is above 0, which uses it to pick the bullet drop angle. Requires a respawn to apply"),
                DescriptorRow("TurretPitchMax", "Maximum artificial pitch limit, in radians, within the bone rotation limits, of the turret's barrel while a player aims, and only when any of the 4 turret pitch fields below is non-zero. Ignored by NPCs unless BulletVelocityForGravity is above 0, which uses it to pick the bullet drop angle. Requires a respawn to apply"),
                DescriptorRow("TurretCamPitchMin", "Only decides whether the turret pitch limit applies: Any of the 4 turret pitch fields being non-zero turns it on, and the limit then uses TurretPitchMin and TurretPitchMax. Requires a respawn to apply"),
                DescriptorRow("TurretCamPitchMax", "Only decides whether the turret pitch limit applies: Any of the 4 turret pitch fields being non-zero turns it on, and the limit then uses TurretPitchMin and TurretPitchMax. Requires a respawn to apply"),
                DescriptorRow("BulletVelocityForGravity", "Projectile speed in m/s that NPC gunners use to aim this turret to account for bullet drop. 0 = aims straight at the target. No effect on player-operated turrets. Requires a respawn to apply"),
                DescriptorRow("TurretPitchForwardMin", "Minimum pitch in radians while aiming forward. 0 = no effect. Above 0, the minimum pitch depends on the aim heading. Without vehicles.meta FLAG_HAS_REAR_MOUNTED_TURRET, the minimum blends linearly from this, straight ahead, to TurretPitchMin, straight behind. With it, the blend follows TurretPitchLimitData instead. Requires a respawn to apply"),
                DescriptorRow("ForwardAngleMin", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Heading in degrees from the vehicle's front, where the forward minimum pitch starts lowering. Requires a respawn to apply"),
                DescriptorRow("ForwardAngleMax", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Heading in degrees from the vehicle's front, where the forward minimum pitch finishes lowering. Requires a respawn to apply"),
                DescriptorRow("BackwardAngleMin", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Heading in degrees from the vehicle's front, where the rear minimum pitch starts rising. Requires a respawn to apply"),
                DescriptorRow("BackwardAngleMid", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Heading in degrees from the vehicle's front, where the rear minimum pitch peaks. Requires a respawn to apply"),
                DescriptorRow("BackwardAngleMax", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Heading in degrees from the vehicle's front, where the rear minimum pitch finishes lowering. Requires a respawn to apply"),
                DescriptorRow("BackwardForcedPitch", "Only used by vehicles with FLAG_HAS_REAR_MOUNTED_TURRET, while TurretPitchForwardMin is not 0, and a player is aiming. Minimum pitch, in radians, forced while aiming toward the rear. Requires a respawn to apply"),
                DescriptorRow("UvAnimationMult", "Speed of the track texture scroll, relative to how fast the tracks turn. 0 = no animation"),
                DescriptorRow("MiscGadgetVar", "How fast the misc_a/b/c/d cog bones turn, relative to the tracks. 0 = no animation. The Scarab turns 8 bones, misc_a to h, with c, d, g, and h at 3x the angle"),
                DescriptorRow("WheelImpactOffset", "Vertically offsets wheel collision bounds independently of the wheel model to modify the height at which the vehicle rests on the ground. The offset shrinks with tire health degradation, and steered wheels are skipped unless HF_STEER_ALL_WHEELS is set. Requires a respawn to apply"),
                DescriptorRow("WeaponDamageScaledToVehHealthMult", "Fraction of the vehicle's max health removed per hit by weapons that scale with vehicle health"),
            }, true, captureInto);
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static KeyValuePair<string, string>[] BuildFlyingFieldDescriptors()
        {
            return new[]
            {
                DescriptorRow("Thrust", "Engine thrust force output per unit of mass, 1.0 = thrust equal to weight. A plane with 0 thrust taxies on the ground solely from its yaw input. Engine upgrades overwrite this"),
                DescriptorRow("ThrustFallOff", "How fast thrust fades with speed. Engine upgrades overwrite this"),
                DescriptorRow("ThrustVectoring", "Helicopters only: Tilts the rotor force with pitch and roll input, resulting in a horizontal push"),
                DescriptorRow("InitialThrust", "Stock thrust output. If 0, the first time an engine upgrade is set, Thrust above is copied here. Every later engine upgrade scales output from this snapshot"),
                DescriptorRow("InitialThrustFallOff", "Stock ThrustFallOff. If 0, the first time an engine upgrade is set, ThrustFallOff above is copied here. Every later engine upgrade scales output from this snapshot"),
                DescriptorRow("YawMultiplier", "Yaw control strength, scaled with speed. Brake upgrades overwrite it. Should be negative, positive = inverted input"),
                DescriptorRow("YawStabilize", "Strength of the independent weathervane yaw force that steers the aircraft to stay pointing in the direction of the airflow. Scales with speed"),
                DescriptorRow("SideSlipMultiplier", "Sideways force that cancels drift when the aircraft flies at an angle, turning its travel toward where the nose points. Lower = more sideways slide and less forward speed when steering"),
                DescriptorRow("InitialYawMultiplier", "Stock YawMultiplier. If 0, the first time a brake upgrade is set, YawMultiplier above is copied here. Every later brake upgrade scales output from this snapshot"),
                DescriptorRow("RollMultiplier", "Roll control strength, scaled with speed. Brake upgrades overwrite it. Should be positive, negative = inverted input"),
                DescriptorRow("RollStabilize", "Torque that levels the wings or rotor"),
                DescriptorRow("InitialRollMultiplier", "Stock RollMultiplier. If 0, the first time a brake upgrade is set, RollMultiplier above is copied here. Every later brake upgrade scales output from this snapshot"),
                DescriptorRow("PitchMultiplier", "Pitch control strength, scaled with speed. Brake upgrades overwrite it. Should be positive, negative = inverted input"),
                DescriptorRow("PitchStabilize", "Strength of the independent weathervane pitch force that steers the aircraft to stay pointing in the direction of the airflow. Scales with speed"),
                DescriptorRow("InitialPitchMultiplier", "Stock PitchMultiplier. If 0, the first time a brake upgrade is set, PitchMultiplier above is copied here. Every later brake upgrade scales output from this snapshot"),
                DescriptorRow("FormLiftMultiplier", "Base lift factor independent from the wing's attack angle. For helicopters: Limit of the self-leveling offset"),
                DescriptorRow("AttackLiftMultiplier", "Lift factor per unit of the wing's attack angle. Upside down, AttackDiveMultiplier replaces it while the attack angle is negative. For helicopters: Strength of the self-leveling torque"),
                DescriptorRow("AttackDiveMultiplier", "Lift factor of the wing's attack angle, used in place of AttackLiftMultiplier only while the plane is upside down with a negative attack angle. No effect when right side up"),
                DescriptorRow("GearDownDragV", "Extra drag when the landing gear is down"),
                DescriptorRow("GearDownLiftMultiplier", "Lift multiplier when the landing gear is down. 1 = no reduction of lift"),
                DescriptorRow("WindMultiplier", "How much wind affects the aircraft. Scaled 1.5x in windy weather and 1.25x in rain, snow, or sandstorms, and none while landed or wrecked. Wind affects helicopters only while a player flies them. Separate from turbulence"),
                DescriptorRow("MoveResistance", "Linear velocity damping: Air resistance proportional to speed. Halved while a door is open. Separate from InitialDragCoefficient"),
                DescriptorRow("TurnResistanceX", string.Format(DESC_AXIS_FLYING_TURN_RESISTANCE, "X")),
                DescriptorRow("TurnResistanceY", string.Format(DESC_AXIS_FLYING_TURN_RESISTANCE, "Y") + DESC_FLYING_TURN_RESISTANCE_Y_PLANES),
                DescriptorRow("TurnResistanceZ", string.Format(DESC_AXIS_FLYING_TURN_RESISTANCE, "Z")),
                DescriptorRow("SpeedResistanceY", string.Format(DESC_AXIS_SPEED_RESISTANCE, "Y")),
                DescriptorRow("SpeedResistanceZ", string.Format(DESC_AXIS_SPEED_RESISTANCE, "Z")),
                DescriptorRow("SpeedResistanceX", string.Format(DESC_AXIS_SPEED_RESISTANCE, "X")),
                DescriptorRow("GearDoorFrontOpen", "Angle in degrees of the front landing gear door while retracted"),
                DescriptorRow("GearDoorRearOpen", "Angle in degrees of the rear landing gear doors (bones RL1 and RR1) while retracted"),
                DescriptorRow("GearDoorRearOpen2", "Angle in degrees of the second rear landing gear doors (bones RL2 and RR2, opposite sign) while retracted"),
                DescriptorRow("GearDoorRearMOpen", "Angle in degrees of the middle rear landing gear doors (bones RML and RMR) while retracted"),
                DescriptorRow("TurbulenceMagnitudeMax", "Maximum turbulence magnitude. Higher = more abrupt influence from turbulence"),
                DescriptorRow("TurbulenceForceMultiplier", "Turbulence push force multiplier"),
                DescriptorRow("TurbulenceRollTorqueMultiplier", "Turbulence roll shake force multiplier"),
                DescriptorRow("TurbulencePitchTorqueMultiplier", "Turbulence pitch shake force multiplier"),
                DescriptorRow("BodyDamageControlEffectMultiplier", "Random control shake as the aircraft's body takes damage"),
                DescriptorRow("InputSensitivityForDifficulty", "Player only. A low flying skill adds random offsets to control input, and this value is how fast they fade"),
                DescriptorRow("OnGroundYawBoostSpeedPeak", "Speed in m/s of the strongest on-ground turning boost while taxiing"),
                DescriptorRow("OnGroundYawBoostSpeedCap", "Speed in m/s where the on-ground turning boost ends"),
                DescriptorRow("EngineOffGlideMultiplier", "Lift multiplier while the engine is destroyed. Capped at 1, so values above 1 let a plane with a destroyed engine glide better"),
                DescriptorRow("AfterburnerEffectRadius", "Radius in meters of the engines that pushes peds, vehicles, and objects behind it. Only for aircraft with afterburners"),
                DescriptorRow("AfterburnerEffectDistance", "Length in meters of the jet blast. Only for aircraft with afterburners"),
                DescriptorRow("AfterburnerEffectForceMultiplier", "Strength of the jet blast. Only for aircraft with afterburners"),
                DescriptorRow("SubmergeLevelToPullHeliUnderwater", "How submerged a helicopter can get before it's pulled under the water"),
                DescriptorRow("ExtraLiftWithRoll", "Extra lift while banked. Allows for tighter turns"),
            };
        }
    }
}
