using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;
using GTA;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // What a highlighted Saved Handlings row allows. Each hint shows and each hotkey fires only for a source that holds its capability
        [Flags]
        private enum SavedHandlingCapabilities { None = 0, Apply = 1, Delete = 2, Rename = 4, Duplicate = 8, Overwrite = 16, CopyToExports = 32, Diff = 64 }

        // A list Saved Handlings can show: Exports, the Autosaves folder, or the vehicle search results
        // Folder is null for the results, which are not files, and their Subtitle is a format template that takes the typed text
        private class SavedHandlingSource
        {
            public readonly string Id;
            public readonly string Folder;
            public readonly string Banner;
            public readonly string Subtitle;
            public readonly SavedHandlingCapabilities Capabilities;
            public readonly Func<SavedHandlingKey[]> ListKeys;
            public readonly Func<SavedHandlingKey, HandlingPreviewCache> LoadPreview;
            public SavedHandlingSource(string id, string folder, string banner, string subtitle, SavedHandlingCapabilities capabilities, Func<SavedHandlingKey[]> listKeys, Func<SavedHandlingKey, HandlingPreviewCache> loadPreview)
            {
                Id = id;
                Folder = folder;
                Banner = banner;
                Subtitle = subtitle;
                Capabilities = capabilities;
                ListKeys = listKeys;
                LoadPreview = loadPreview;
            }

            public bool Has(SavedHandlingCapabilities capability) { return (Capabilities & capability) != 0; }
        }

        // One row of a Saved Handlings list
        // Name is a file's name without its extension, or a search result's model name, and ModelHash is that result's model (0 for a file)
        private struct SavedHandlingKey : IEquatable<SavedHandlingKey>
        {
            public readonly SavedHandlingSource Source;
            public readonly string Name;
            public readonly int ModelHash;
            public SavedHandlingKey(SavedHandlingSource source, string name) : this(source, name, 0) { }
            public SavedHandlingKey(SavedHandlingSource source, string name, int modelHash)
            {
                Source = source;
                Name = name;
                ModelHash = modelHash;
            }

            public bool IsValid { get { return Source != null; } }
            public bool Equals(SavedHandlingKey other) { return Source == other.Source && Name == other.Name && ModelHash == other.ModelHash; }
            public override bool Equals(object obj) { return obj is SavedHandlingKey && Equals((SavedHandlingKey)obj); }
            public override int GetHashCode() { return (Name != null ? Name.GetHashCode() : 0) * 31 + ModelHash; }
        }

        private SavedHandlingSource exportsSource;
        private SavedHandlingSource autosavesSource;
        private SavedHandlingSource searchSource;

        // Built in the constructor, since each source's list and preview delegates are instance methods
        private void InitSavedHandlingSources()
        {
            exportsSource = new SavedHandlingSource("Exports", EXPORTS_DIR, LABEL_SAVED_HANDLINGS, LABEL_EXPORTS, SavedHandlingCapabilities.Apply | SavedHandlingCapabilities.Delete | SavedHandlingCapabilities.Rename | SavedHandlingCapabilities.Duplicate | SavedHandlingCapabilities.Overwrite | SavedHandlingCapabilities.Diff, () => ListSavedHandlingFolderKeys(exportsSource), LoadFilePreview);
            autosavesSource = new SavedHandlingSource("Autosaves", EXPORTS_DIR + AUTOSAVES_SUBFOLDER + "\\", LABEL_SAVED_HANDLINGS, LABEL_AUTOSAVES, SavedHandlingCapabilities.Apply | SavedHandlingCapabilities.Delete | SavedHandlingCapabilities.CopyToExports | SavedHandlingCapabilities.Diff, () => ListSavedHandlingFolderKeys(autosavesSource), LoadFilePreview);
            searchSource = new SavedHandlingSource("Search", null, TITLE_SEARCH, SUBTITLE_SEARCH_RESULTS, SavedHandlingCapabilities.Apply, () => searchResultKeys, GetSearchPreviewCache);
            browsedSource = exportsSource;
        }

        private SavedHandlingSource ResolveSavedHandlingSourceById(string id)
        {
            if (id == exportsSource.Id) return exportsSource;
            if (id == autosavesSource.Id) return autosavesSource;
            return id == searchSource.Id ? searchSource : null;
        }

        private static string SavedHandlingPath(SavedHandlingKey key) { return key.Source.Folder + key.Name + ".meta"; }
        private SavedHandlingKey ExportKey(string name) { return new SavedHandlingKey(exportsSource, name); }

        // A restore failure is logged, not retried, since the caller has already reported the original operation as failed
        private void RestoreHandlingBackup(string path, string content)
        {
            try
            {
                File.WriteAllText(path, content);
                logger.Log(Dbg.Level.Warn, Dbg.HandlingRestoredAfterFailedWrite(path));
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.HandlingRestoreFailed(path, ex.Message)); }
        }

        // A name already ending in an underscore plus EXPORT_SUFFIX_DIGITS digits counts up from that number instead of gaining a second suffix
        // So NAME_01 resolves to NAME_02, not NAME_01_01
        // ownName is the export being renamed. Its own file never counts as taken, so NAME_01 renamed to a taken NAME resolves back to NAME_01
        private string ResolveFreeExportName(string requestedName, string ownName = null)
        {
            if (!IsExportNameTaken(requestedName, ownName)) return requestedName;
            string stem = requestedName;
            int suffix = 1;
            int underscore = requestedName.LastIndexOf('_');
            if (underscore > 0 && requestedName.Length - underscore - 1 == EXPORT_SUFFIX_DIGITS && IsAsciiDigits(requestedName.Substring(underscore + 1)))
            {
                stem = requestedName.Substring(0, underscore);
                suffix = int.Parse(requestedName.Substring(underscore + 1), CultureInfo.InvariantCulture) + 1;
            }
            string outName = stem + "_" + suffix.ToString("D2");
            while (IsExportNameTaken(outName, ownName)) outName = stem + "_" + (++suffix).ToString("D2");
            return outName;
        }

        private bool IsExportNameTaken(string name, string ownName)
        {
            return File.Exists(SavedHandlingPath(ExportKey(name))) && !string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiDigits(string text)
        {
            for (int i = 0; i < text.Length; i++) if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }

        // Refreshed on every Saved Handlings open, so hot paths read this instead of re-scanning the directory
        private SavedHandlingKey[] cachedSavedHandlingKeys = new SavedHandlingKey[0];

        private SavedHandlingKey[] ListSavedHandlingFolderKeys(SavedHandlingSource source)
        {
            try
            {
                if (!Directory.Exists(source.Folder)) return new SavedHandlingKey[0];
                string[] files = Directory.GetFiles(source.Folder, "*.meta");
                var keys = new SavedHandlingKey[files.Length];
                for (int i = 0; i < files.Length; i++) keys[i] = new SavedHandlingKey(source, Path.GetFileNameWithoutExtension(files[i]));
                return keys;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.GetExportsError(ex.Message));
                return new SavedHandlingKey[0];
            }
        }

        // Copies a saved handling into the Exports folder under a free name, so a colliding export is never overwritten
        // Returns False after logging why when the source is gone or the copy failed, and the caller posts the toast and refreshes the list
        private bool CopySavedHandlingToExports(SavedHandlingKey key, out string outName)
        {
            outName = null;
            string sourcePath = SavedHandlingPath(key);
            if (!File.Exists(sourcePath))
            {
                logger.Log(Dbg.Level.Warn, Dbg.HandlingFileNotFound(key.Name));
                return false;
            }
            try
            {
                outName = ResolveFreeExportName(key.Name);
                File.Copy(sourcePath, SavedHandlingPath(ExportKey(outName)));
                logger.Log(Dbg.Level.Info, Dbg.HandlingDuplicated(key.Name, outName));
                return true;
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.HandlingDuplicateError(key.Name, ex.Message));
                return false;
            }
        }

        private void CopyAutosaveToExports(SavedHandlingKey key)
        {
            string outName;
            if (CopySavedHandlingToExports(key, out outName)) notifications.Notify(NOTIFICATION_KEY_HANDLING_DUPLICATE, string.Format(NOTIF_COPIED_TO_EXPORTS, outName));
        }

        // Prompts separately for the handling name (written into the XML) and the file name (resolved to a free name on collision)
        // Canceling either aborts the save
        // The handling name prompt starts at ResolveHandlingName and the file name prompt at the model name (ResolveModelName)
        // PromptOnSave = False skips both prompts and uses those 2 names directly. A typed name keeps its casing exactly as typed
        private void SaveHandling()
        {
            // Fires from mainMenu/Saved Handlings, where no editor tree is visible, so it acts on the Active editor's vehicle
            Vehicle vehicle = activeContext.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_SAVE, NOTIF_NO_VEHICLE_SAVE);
                return;
            }
            string handlingName;
            string fileName;
            if (promptOnSave)
            {
                string typedHandlingName = Game.GetUserInput(WindowTitle.EnterMessage20, modelNames.ResolveHandlingName(vehicle), INPUT_MAX_LENGTH);
                // A name of only spaces counts as canceled, and the saved name never carries spaces around it
                handlingName = string.IsNullOrEmpty(typedHandlingName) ? "" : typedHandlingName.Trim();
                if (handlingName.Length == 0)
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
                fileName = GetValidatedUserInput(modelNames.ResolveModelName(vehicle), INPUT_MAX_LENGTH, IsUsableFileName);
                if (string.IsNullOrEmpty(fileName))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                    return;
                }
            }
            else
            {
                handlingName = modelNames.ResolveHandlingName(vehicle);
                fileName = modelNames.ResolveModelName(vehicle);
                if (!IsUsableFileName(fileName))
                {
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingSaveError(fileName, "model name is not a usable file name"));
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_HANDLING_SAVE_ERROR, NOTIF_HANDLING_SAVE_FAILED_NAME);
                    return;
                }
            }
            // Re-checked here since the prompts above can each block for an arbitrary duration while the player types
            if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_SAVE, NOTIF_NO_VEHICLE_SAVE);
                return;
            }
            string backupPath = null;
            string backupContent = null;
            try
            {
                if (!Directory.Exists(EXPORTS_DIR)) Directory.CreateDirectory(EXPORTS_DIR);
                // AlwaysOverwrite skips the free-name resolution and overwrites the colliding file in place
                bool overwroteExisting = alwaysOverwrite && File.Exists(SavedHandlingPath(ExportKey(fileName)));
                string outName = alwaysOverwrite ? fileName : ResolveFreeExportName(fileName);
                string outPath = SavedHandlingPath(ExportKey(outName));
                if (overwroteExisting)
                {
                    string[] existingMatches = Directory.GetFiles(EXPORTS_DIR, outName + ".meta");
                    backupPath = existingMatches.Length > 0 && Path.GetFileName(existingMatches[0]) != outName + ".meta" ? existingMatches[0] : outPath;
                    try { backupContent = File.ReadAllText(backupPath); }
                    catch (Exception ex)
                    {
                        logger.Log(Dbg.Level.Error, Dbg.HandlingBackupReadFailed(backupPath, ex.Message));
                        return;
                    }
                    // Windows matches an existing file case-insensitively but preserves its original directory-entry casing on overwrite
                    // Deleting the old entry first lets the new write land under outName's own correct casing instead of retaining the old one
                    if (backupPath != outPath) File.Delete(backupPath);
                }
                XDocument doc = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, handlingName, ResolveHandlingFieldGates(vehicle), ResolveExportOverlay(ResolveHandlingKey(vehicle)));
                MarkHandlingNameMismatch(doc, "SaveHandling", handlingName, vehicle);
                string content = doc.Declaration.ToString() + Environment.NewLine + Environment.NewLine + doc.Root.ToString();
                if (!WriteVerifiedHandling(outPath, content))
                {
                    if (backupPath != null) RestoreHandlingBackup(backupPath, backupContent);
                    logger.Log(Dbg.Level.Error, Dbg.HandlingVerifyFailed(outName));
                    return;
                }
                logger.Log(Dbg.Level.Info, Dbg.HandlingSaved(outName));
                notifications.Notify(NOTIFICATION_KEY_HANDLING_SAVE, overwroteExisting ? string.Format(NOTIF_HANDLING_OVERWROTE, outName) : string.Format(NOTIF_HANDLING_SAVED, outName));
                RunHandlingPostOperationRefresh(outName, () =>
                {
                    // Only refresh while Saved Handlings is open, since an unconditional refresh would force Preview visible with no way to dismiss it
                    if (savedHandlingsMenu.Visible) RefreshSavedHandlingsMenu();
                });
            }
            catch (Exception ex)
            {
                if (backupPath != null) RestoreHandlingBackup(backupPath, backupContent);
                logger.Log(Dbg.Level.Error, Dbg.HandlingSaveError(fileName, ex.Message));
            }
        }

        // Pure file operation, no vehicle required
        private void DuplicateHandling(SavedHandlingKey key)
        {
            string outName;
            if (!CopySavedHandlingToExports(key, out outName)) return;
            notifications.Notify(NOTIFICATION_KEY_HANDLING_DUPLICATE, string.Format(NOTIF_HANDLING_DUPLICATED, outName));
            RunHandlingPostOperationRefresh(outName, () => RefreshSavedHandlingsMenu(outName, -1));
        }

        // Pure file operation, no vehicle required, no XML content rewrite
        // The file name and the internal handlingName element are independent, so nothing inside the file needs to change
        private void RenameHandling(SavedHandlingKey key)
        {
            string oldName = key.Name;
            string sourcePath = SavedHandlingPath(key);
            if (!File.Exists(sourcePath))
            {
                logger.Log(Dbg.Level.Warn, Dbg.HandlingFileNotFound(oldName));
                return;
            }
            string newName = GetValidatedUserInput(oldName, INPUT_MAX_LENGTH, IsUsableFileName);
            if (string.IsNullOrEmpty(newName))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_ENTRY_CANCELED);
                return;
            }
            // An identical name is a no-op cancel, since File.Move to an identical path throws
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_NAME_UNCHANGED);
                return;
            }
            // A casing-only change names the same file to the file system, so it moves straight onto the new casing with no collision handling
            bool caseOnlyChange = string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase);
            string outPath = null;
            string backupContent = null;
            try
            {
                string outName = newName;
                bool overwroteExisting = false;
                if (!caseOnlyChange)
                {
                    // Matches SaveHandling's own collision handling: AlwaysOverwrite skips the free-name resolution and overwrites the colliding file in place
                    overwroteExisting = alwaysOverwrite && File.Exists(SavedHandlingPath(ExportKey(outName)));
                    if (!alwaysOverwrite) outName = ResolveFreeExportName(newName, oldName);
                }
                // The first free name can be the export's own current name (NAME_01 renamed to a taken NAME). Nothing moves then
                if (!caseOnlyChange && string.Equals(outName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_FIELD_ENTRY_CANCELED, NOTIF_NAME_UNCHANGED);
                    return;
                }
                outPath = SavedHandlingPath(ExportKey(outName));
                if (overwroteExisting)
                {
                    try { backupContent = File.ReadAllText(outPath); }
                    catch (Exception ex)
                    {
                        logger.Log(Dbg.Level.Error, Dbg.HandlingBackupReadFailed(outPath, ex.Message));
                        return;
                    }
                    // File.Move throws if outPath already exists, so the collision target is removed first
                    // The captured backup is restored by the catch block below if the move itself then fails
                    File.Delete(outPath);
                }
                File.Move(sourcePath, outPath);
                previewFieldListCache.Remove(key);
                logger.Log(Dbg.Level.Info, Dbg.HandlingRenamed(oldName, outName));
                notifications.Notify(NOTIFICATION_KEY_HANDLING_RENAME, overwroteExisting ? string.Format(NOTIF_HANDLING_RENAMED_OVERWROTE, outName) : string.Format(NOTIF_HANDLING_RENAMED, outName));
                RunHandlingPostOperationRefresh(outName, () => RefreshSavedHandlingsMenu(outName, -1));
            }
            catch (Exception ex)
            {
                if (backupContent != null) RestoreHandlingBackup(outPath, backupContent);
                logger.Log(Dbg.Level.Error, Dbg.HandlingRenameError(oldName, ex.Message));
            }
        }

        private void OverwriteHandling(NativeItem item, SavedHandlingKey key)
        {
            string name = key.Name;
            // Fires from mainMenu/Saved Handlings, where no editor tree is visible, so it acts on the Active editor's vehicle
            Vehicle vehicle = activeContext.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_OVERWRITE, NOTIF_NO_VEHICLE_OVERWRITE);
                return;
            }
            string path = SavedHandlingPath(key);
            // Captured before the write, so a failed verify below restores exactly what existed rather than leaving the slot empty
            string backupContent;
            try { backupContent = File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.HandlingBackupReadFailed(path, ex.Message));
                StartResultFlash(item, Color.Red);
                return;
            }
            // Neither the file name nor the internal handling name may change on an overwrite
            // The existing name is parsed from the content already read above
            // The item's title is only the save filename and can legitimately differ from it
            string existingHandlingName;
            try { existingHandlingName = backupContent != null ? ReadHandlingNameFromDocument(XDocument.Parse(backupContent)) : null; }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.HandlingOverwriteParseFailed(path, ex.Message));
                StartResultFlash(item, Color.Red);
                return;
            }
            if (string.IsNullOrEmpty(existingHandlingName))
            {
                logger.Log(Dbg.Level.Error, Dbg.HandlingOverwriteNameUnresolved(path));
                StartResultFlash(item, Color.Red);
                return;
            }
            try
            {
                XDocument doc = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, existingHandlingName, ResolveHandlingFieldGates(vehicle), ResolveExportOverlay(ResolveHandlingKey(vehicle)));
                MarkHandlingNameMismatch(doc, "OverwriteHandling", existingHandlingName, vehicle);
                string content = doc.Declaration.ToString() + Environment.NewLine + Environment.NewLine + doc.Root.ToString();
                if (!WriteVerifiedHandling(path, content))
                {
                    if (backupContent != null) RestoreHandlingBackup(path, backupContent);
                    logger.Log(Dbg.Level.Error, Dbg.HandlingVerifyFailed(name));
                    StartResultFlash(item, Color.Red);
                    return;
                }
                logger.Log(Dbg.Level.Info, Dbg.HandlingOverwritten(name));
                RunHandlingPostOperationRefresh(name, () =>
                {
                    // Refresh rebuilds the item list, orphaning the original item, so flash the freshly-rebuilt SelectedItem instead
                    RefreshSavedHandlingsMenu();
                    StartResultFlash(savedHandlingsMenu.SelectedItem, Color.LightGreen);
                });
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.HandlingSaveError(name, ex.Message));
                StartResultFlash(item, Color.Red);
            }
        }

        // A delete whose file removal runs on a worker thread, finished on the game thread by FinishHandlingDelete once the removal completes
        private class PendingHandlingDelete
        {
            public SavedHandlingKey Key;
            public Task Removal;
        }

        private PendingHandlingDelete pendingHandlingDelete;

        // Pure file removal. The arm and confirm UI flow lives in ArmedActions.cs alongside the highlighted item it colors
        // The recycle-bin call alone takes tens of milliseconds, so the removal runs on a worker thread and the frame never waits on it
        // One delete runs at a time, and a confirm while one is pending does nothing
        private void DeleteHandlingFile(SavedHandlingKey key)
        {
            if (pendingHandlingDelete != null) return;
            string path = SavedHandlingPath(key);
            pendingHandlingDelete = new PendingHandlingDelete { Key = key, Removal = Task.Run(() => RemoveHandlingFile(path)) };
        }

        // Runs on a worker thread, so it touches no game or menu state
        private static void RemoveHandlingFile(string path)
        {
            // FileSystem.DeleteFile throws FileNotFoundException on a missing path, unlike File.Delete's silent no-op
            // An already-gone file (e.g. removed externally while Saved Handlings was open) is a benign no-op here, not a failure
            if (!File.Exists(path)) return;
            // A locked-file probe runs before the recycle-bin call, since UIOption has no fully-silent option
            // A locked-file failure can surface a native Windows dialog that blocks the calling thread under exclusive fullscreen with no way to dismiss it
            // Probing turns that most likely failure into an ordinary caught exception
            // A genuinely unavailable Recycle Bin or a permissions failure can still reach FileSystem.DeleteFile's own dialog
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }

        // The cache removal, the log line, the toast, and the list refresh read game and menu state, so they wait for the game thread
        private void FinishHandlingDelete()
        {
            PendingHandlingDelete finished = pendingHandlingDelete;
            pendingHandlingDelete = null;
            string name = finished.Key.Name;
            if (finished.Removal.IsFaulted)
            {
                Exception failure = finished.Removal.Exception.InnerException ?? finished.Removal.Exception;
                logger.Log(Dbg.Level.Error, Dbg.HandlingDeleteError(name, failure.Message));
                return;
            }
            previewFieldListCache.Remove(finished.Key);
            logger.Log(Dbg.Level.Info, Dbg.HandlingDeleted(name));
            notifications.Notify(NOTIFICATION_KEY_HANDLING_DELETE, string.Format(NOTIF_DELETED, name));
            // The highlight moves up one, and a delete of the first export stays on the new first export, never reaching the Autosaves row
            int deletedIndex = Array.IndexOf(cachedSavedHandlingKeys, finished.Key);
            int focusRow = deletedIndex > 0 ? deletedIndex - 1 + SavedHandlingRowOffset : -1;
            RunHandlingPostOperationRefresh(name, () => RefreshSavedHandlingsMenu(null, focusRow));
        }

        // A handlingName that resolves to nothing or to another vehicle's entry risks a silent hard crash on game init when the export is pasted into handling.meta
        // A name from the ModelMetaData file already passed this check, so a mismatch means a typed name or a fallback
        // On a mismatch, a comment is written directly above the handlingName element, so the warning stays with the file after the notification is gone
        // notify and logWarning let a silent caller (Autosave) keep the comment while dropping the notification and repeated warnings
        private void MarkHandlingNameMismatch(XDocument doc, string operation, string handlingName, Vehicle vehicle, bool notify = true, bool logWarning = true)
        {
            HandlingData guessedHandling = HandlingData.GetByHash((int)GTA.StringHash.AtStringHash(handlingName));
            if (guessedHandling.MemoryAddress == vehicle.HandlingData.MemoryAddress) return;
            if (logWarning) logger.Log(Dbg.Level.Warn, Dbg.HandlingNameMismatch(operation, handlingName));
            if (notify) notifications.PostNotificationOnce(NOTIFICATION_KEY_HANDLING_NAME_MISMATCH, string.Format(NOTIF_HANDLING_NAME_MISMATCH, handlingName));
            XElement handlingNameElement = doc.Root.Element(HANDLING_DATA_TAG).Element(ITEM_TAG).Element(HANDLING_NAME_TAG);
            handlingNameElement.AddBeforeSelf(new XComment(HANDLING_NAME_MISMATCH_COMMENT));
        }

        // A refresh failure can never reach the operation's own catch, where it would roll back a verified write or report a finished operation as failed
        private void RunHandlingPostOperationRefresh(string savedHandlingName, Action refresh)
        {
            try { refresh(); }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.HandlingPostRefreshFailed(savedHandlingName, ex.Message)); }
        }

        // Writes directly to the final path, and a file that fails validation is removed before reporting failure
        private bool WriteVerifiedHandling(string path, string content)
        {
            bool writeCompleted = false;
            bool verified = false;
            try
            {
                File.WriteAllText(path, content);
                writeCompleted = true;
                XDocument written = XDocument.Load(path);
                if (written.Root == null || written.Root.Element(HANDLING_DATA_TAG) == null) return false;
                verified = true;
                return true;
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.HandlingVerifyError(ex.Message)); }
            finally
            {
                if (writeCompleted && !verified)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.HandlingUnverifiedFileRemovalFailed(ex.Message)); }
                }
            }
            return false;
        }

        // Rejects path traversal and invalid filesystem characters, since the name is user-entered
        private static bool IsUsableFileName(string fileName)
        {
            if (fileName.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (fileName.IndexOf(Path.DirectorySeparatorChar) >= 0 || fileName.IndexOf(Path.AltDirectorySeparatorChar) >= 0) return false;
            return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        // A file reads from disk, and a search result builds from its model's live handling, so everything after it applies either the same way
        // Null after logging when neither can be read, and an unreadable result also tells the player, since Enter would otherwise do nothing visible
        private XDocument ReadSavedHandlingDocument(SavedHandlingKey key)
        {
            if (key.Source.Folder == null)
            {
                XDocument built = BuildSearchResultDocument(key);
                if (built == null)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.SearchResultUnreadable(key.Name));
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_SEARCH_RESULT_UNREADABLE, NOTIF_SEARCH_RESULT_UNREADABLE);
                }
                return built;
            }
            string path = SavedHandlingPath(key);
            if (!File.Exists(path))
            {
                logger.Log(Dbg.Level.Warn, Dbg.HandlingFileNotFound(key.Name));
                return null;
            }
            return XDocument.Load(path);
        }

        private void ApplyHandling(SavedHandlingKey key)
        {
            string name = key.Name;
            // Fires from mainMenu/Saved Handlings, where no editor tree is visible, so it acts on the Active editor's vehicle
            Vehicle vehicle = activeContext.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle) || !vehicle.HandlingData.IsValid)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_APPLY, NOTIF_NO_VEHICLE_APPLY);
                return;
            }
            try
            {
                XDocument doc = ReadSavedHandlingDocument(key);
                if (doc == null) return;

                // Pre-apply state, captured before ApplyHandlingDocument touches anything so the apply undoes in one shot
                // Read once so the snapshot and the apply agree on which vehicle-gated fields take part
                HandlingFieldGates gates = ResolveHandlingFieldGates(vehicle);
                XDocument beforeSnapshot = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, name, gates, ExportOverlay.None);
                HandlingKey handlingKey = ResolveHandlingKey(vehicle);

                // Fields apply incrementally as the document is walked, so a failure inside the walk means some values already changed
                // The outer catch below only ever sees a clean failure
                // Both paths act on the replay flags collected from the fields already written
                int fieldsInvalid, fieldsAbsent;
                int liveRequestsQueued = 0;
                ReplayFlags replayFlags = ReplayFlags.None;
                try { liveRequestsQueued = ApplyHandlingDocument(doc, vehicle.HandlingData, vehicle, gates, LiveRequestOrigin.AppliedHandling, ref replayFlags, out fieldsInvalid, out fieldsAbsent); }
                catch (Exception ex)
                {
                    PushHandlingApply(handlingKey, beforeSnapshot);
                    SyncEditorContext(activeContext);
                    ConsumeReplayRefreshAndRespawn(vehicle, "HandlingApply", replayFlags);
                    logger.Log(Dbg.Level.Error, Dbg.PartiallyApplyHandling(name, ex.Message));
                    return;
                }
                // The same build after the apply shows whether anything changed. An identical apply writes no undo entry and asks for no respawn or refresh
                // A live request lands a frame later, so the build cannot show it yet, and any queued request counts as a change
                XDocument afterSnapshot = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, name, gates, ExportOverlay.None);
                if (liveRequestsQueued == 0 && afterSnapshot.ToString() == beforeSnapshot.ToString())
                {
                    logger.Log(Dbg.Level.Info, Dbg.HandlingApplyIdentical(name));
                    notifications.Notify(NOTIFICATION_KEY_HANDLING_APPLY, NOTIF_HANDLING_IDENTICAL);
                    return;
                }
                PushHandlingApply(handlingKey, beforeSnapshot);
                SyncEditorContext(activeContext);
                ConsumeReplayRefreshAndRespawn(vehicle, "HandlingApply", replayFlags);
                // A field can be skipped without the apply failing, so the count is reported either way rather than leaving the user to assume everything applied
                if (fieldsInvalid > 0 || fieldsAbsent > 0)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.HandlingAppliedPartially(name, fieldsInvalid, fieldsAbsent));
                    notifications.Notify(NOTIFICATION_KEY_HANDLING_APPLY, string.Format(NOTIF_HANDLING_APPLIED_SKIPPED, name, (fieldsInvalid + fieldsAbsent)));
                    return;
                }
                logger.Log(Dbg.Level.Info, Dbg.HandlingApplied(name));
                notifications.Notify(NOTIFICATION_KEY_HANDLING_APPLY, string.Format(NOTIF_HANDLING_APPLIED, name));
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.HandlingApplyError(name, ex.Message)); }
        }
    }
}
