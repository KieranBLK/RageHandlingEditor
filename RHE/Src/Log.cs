using System;
using System.Globalization;
using System.IO;
using System.Text;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private readonly ILogger logger;

        private interface ILogger
        {
            void Log(Dbg.Level level, string message, bool notify = true);
            bool IsInfoEnabled();
            void FlushLog();
            void FlushLogOnInterval();
            void StartSession(string iniFilePath);
        }

        // Holds the log's buffer and header state, so no other file can append to the buffer around the level labels
        // The logging mode is read through a delegate on every call, so a line logged before the INI loads follows the setting's default
        private sealed class Logger : ILogger
        {
            private readonly string logPath;
            private readonly Func<int> readLoggingMode;
            private readonly Action<string, string> postNotificationOnce;
            private readonly StringBuilder logBuffer = new StringBuilder();
            private int logFlushTick;
            private string logLoadedLine;
            private string logInfoLine;
            private string logApiVersionsLine;
            private string logSeparator;
            // Set by a failure inside PreReadAlwaysClearLogOnReload, which runs before InitLogHeader writes the [LOADED] banner
            // Logging it from there would land the warning above its own header on first flush, so StartSession logs it right after InitLogHeader
            private string logInitDeferredWarning;

            public Logger(string path, Func<int> loggingModeReader, Action<string, string> notificationPoster)
            {
                logPath = path;
                readLoggingMode = loggingModeReader;
                postNotificationOnce = notificationPoster;
            }

            // Warn/Error always written. Info/Perf gated by loggingMode (0 = Warn/Error only, 1 = All, 2 = +Perf, 3 = +Info)
            // notify False keeps an ERROR detail line (a stack trace, an inner exception) out of the red toast, since the failure's own first line already posted it
            public void Log(Dbg.Level level, string message, bool notify = true)
            {
                if (level == Dbg.Level.Info || level == Dbg.Level.Perf)
                {
                    int currentLoggingMode = readLoggingMode();
                    if (currentLoggingMode == 0) return;
                    if (currentLoggingMode == 2 && level == Dbg.Level.Info) return;
                    if (currentLoggingMode == 3 && level == Dbg.Level.Perf) return;
                }
                // Keyed by the error text itself, so only an identical repeat is throttled and unrelated errors inside the same window each still notify
                if (level == Dbg.Level.Error && notify) postNotificationOnce(NOTIFICATION_KEY_PREFIX_ERROR + message, string.Format(NOTIF_ERROR_MESSAGE, message));
                string label = level == Dbg.Level.Info ? "[INFO] -- " : level == Dbg.Level.Warn ? "[WARN] -- " : level == Dbg.Level.Error ? "[ERROR] -- " : "[PERF] -- ";
                logBuffer.Append('[').Append(DateTime.Now.ToString("h:mm:sstt")).Append("] ").Append(label).AppendLine(message);
            }

            // Gate expensive string builds behind this before calling Log()
            public bool IsInfoEnabled() { int currentLoggingMode = readLoggingMode(); return currentLoggingMode == 1 || currentLoggingMode == 3; }
            private void LogHeader(string line) { logBuffer.AppendLine(line); }

            public void FlushLog()
            {
                if (logBuffer.Length == 0) return;
                try { File.AppendAllText(logPath, logBuffer.ToString()); }
                catch { }
                // Cleared unconditionally, since a persistent write failure would otherwise grow this buffer without bound for the rest of the session
                logBuffer.Clear();
            }

            public void FlushLogOnInterval()
            {
                logFlushTick++;
                if (logFlushTick < LOG_FLUSH_INTERVAL) return;
                logFlushTick = 0;
                FlushLog();
            }

            public void StartSession(string iniFilePath)
            {
                bool alwaysClear = PreReadAlwaysClearLogOnReload(iniFilePath);
                InitLogHeader(alwaysClear);
                if (logInitDeferredWarning != null) Log(Dbg.Level.Warn, logInitDeferredWarning);
            }

            // Pre-reads this one key before the full INI parse so the log can be cleared before any entries are written
            private bool PreReadAlwaysClearLogOnReload(string iniFilePath)
            {
                if (!File.Exists(iniFilePath)) return false;
                try
                {
                    foreach (string raw in File.ReadAllLines(iniFilePath))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq < 1) continue;
                        if (string.Equals(line.Substring(0, eq).Trim(), KEY_DBG_AlwaysClearLogOnReload, StringComparison.OrdinalIgnoreCase))
                        {
                            bool parsed;
                            string rawValue = line.Substring(eq + 1).Trim();
                            if (bool.TryParse(rawValue, out parsed)) return parsed;
                            logInitDeferredWarning = Dbg.IniInvalidValue(KEY_DBG_AlwaysClearLogOnReload, rawValue);
                            break;
                        }
                    }
                }
                catch (Exception ex) { logInitDeferredWarning = Dbg.LogInitReadFailed(ex.Message); }
                return false;
            }

            private void InitLogHeader(bool alwaysClear)
            {
                logLoadedLine = "[LOADED] -- RageHandlingEditor v" + ScriptVersion;
                DateTime now = DateTime.Now;
                logInfoLine = "[INFO] -- | GameVersion: " + (Game.isEnhanced ? "Enhanced" : "Legacy") + " v" + Game.FileVersion + " | " + now.ToString("M/dd/yy") + " " + now.ToString("h:mm:sstt");
                logApiVersionsLine = "[INFO] -- | SHV: v" + ReadFileVersion(SHV_FILE_PATH) + " | SHVDN: v" + ReadFileVersion(SHVDN_FILE_PATH) + " | LemonUI: v" + ReadFileVersion(typeof(LemonUI.Menus.NativeMenu).Assembly.Location);
                logSeparator = new string('-', Math.Max(logInfoLine.Length, logApiVersionsLine.Length));
                if (alwaysClear || !File.Exists(logPath))
                {
                    try { File.WriteAllText(logPath, ""); } catch { }
                    // The versions line is the longest, so it sits directly above the separator, which spans it
                    LogHeader(logLoadedLine);
                    LogHeader(logInfoLine);
                    LogHeader(logApiVersionsLine);
                    LogHeader(logSeparator);
                    LogHeader(LOG_SESSION_MARKER + 1 + " ===");
                    FlushLog();
                    return;
                }
                LogHeader("");
                LogHeader(logLoadedLine);
                LogHeader(logInfoLine);
                LogHeader(logApiVersionsLine);
                LogHeader(logSeparator);
                FlushLog();
                int sessionId = 1;
                try
                {
                    foreach (string line in File.ReadAllLines(logPath))
                    {
                        string t = line.TrimStart();
                        if (!t.StartsWith(LOG_SESSION_MARKER)) continue;
                        int start = LOG_SESSION_MARKER.Length;
                        int end = t.IndexOf(' ', start);
                        int n;
                        if (end > start && int.TryParse(t.Substring(start, end - start), out n) && n >= sessionId) sessionId = n + 1;
                    }
                }
                catch (Exception ex) { Log(Dbg.Level.Warn, Dbg.LogInitSessionParseFailed(ex.Message)); }
                try { File.AppendAllText(logPath, LOG_SESSION_MARKER + sessionId + " ===" + Environment.NewLine); } catch { }
            }

            // A file version for the header, "unknown" when the file is missing or carries none, so a renamed or absent hook or library never stops the header
            private static string ReadFileVersion(string path)
            {
                try
                {
                    if (!File.Exists(path)) return "unknown";
                    string version = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.GetFullPath(path)).FileVersion;
                    return string.IsNullOrEmpty(version) ? "unknown" : version;
                }
                catch { return "unknown"; }
            }
        }

        private static class Dbg
        {
            public enum Level { Info, Warn, Error, Perf }
            // The label every model line carries, the name then the model hash in hex
            private static string FormatModelLabel(string name, int modelHash) { return name + " (0x" + modelHash.ToString("X8") + ")"; }
            // --Init--
            public static string LogInitReadFailed(string message) { return "LOG: Init | AlwaysClearLogOnReload read failed | Error=" + message; }
            public static string LogInitSessionParseFailed(string message) { return "LOG: Init | Session ID parse failed | Error=" + message; }
            public static string ConstructorFailed(string message) { return "STARTUP: The script failed to start | Error=" + message; }
            public static string ConstructorPhaseFailed(string phase, string message) { return "STARTUP: " + phase + " | Failed during startup | Error=" + message; }
            public static string MenuBuilt(int menuCount) { return "MENU: BuildMenu | Done | Menus=" + menuCount; }
            private static string TreeLabel(int contextIndex) { return contextIndex == 0 ? "Current" : "Editor " + contextIndex; }
            public static string EditorTreeBuildFailed(int contextIndex, int stepCount, string exceptionTypeName, string message) { return "MENU: Editor tree | Build failed, the editor will not mount | Tree=" + TreeLabel(contextIndex) + " | Steps=" + stepCount + " | " + exceptionTypeName + " | Error=" + message; }
            public static string EditorTreeMenuCountMismatch(int contextIndex, int menuCount, int expectedCount) { return "MENU: Editor tree | Menu count differs from Current's, so menu restore and seat merge can land on the wrong menu | Tree=" + TreeLabel(contextIndex) + " | Menus=" + menuCount + " | Expected=" + expectedCount; }
            public static string EditorTreeMountRefused(int contextIndex, string modelName) { return "MENU: Editor tree | Mount refused, the tree failed to build | Tree=" + TreeLabel(contextIndex) + " | Model=" + modelName; }
            public static string ArrowsFieldMissing() { return "MENU: Row arrows | The menu library's private arrows field is missing | Each row's arrows are set through the property"; }
            public static string RawOffsetsUnsupported(string version, string minimum) { return "RAWMEMORY: Raw offset fields | Disabled | Build=v" + version + " | Minimum=v" + minimum; }
            public static string SpecialFlightFlagsUnsupported(string version, string minimum) { return "RAWMEMORY: SpecialFlightHandlingFlags | Disabled | Build=v" + version + " | Minimum=v" + minimum; }
            public static string RawMemoryAccessRejected(string detail) { return "RAWMEMORY: Access rejected | " + detail + " | Field operation skipped"; }
            public static string Config(int loggingMode) { return "CONFIG: Loaded | LoggingMode=" + loggingMode; }
            // --Failure Boundaries--
            public static string TickFailed(string type, string message) { return "TICK: An update step failed | " + type + " | Error=" + message; }
            public static string TickFailed(string subsystem, string exceptionTypeName, string message) { return "TICK: " + subsystem + " | Update failed | " + exceptionTypeName + " | Error=" + message; }
            public static string KeyDownFailed(string type, string message) { return "KEYS: A key press failed | " + type + " | Error=" + message; }
            public static string KeyDownFailed(string subsystem, string exceptionTypeName, string message) { return "KEYS: " + subsystem + " | Key press failed | " + exceptionTypeName + " | Error=" + message; }
            public static string HandlerFailed(string handlerName, string exceptionTypeName, string message) { return "MENU: " + handlerName + " | Menu handler failed | " + exceptionTypeName + " | Error=" + message; }
            public static string ScriptAborted() { return "SCRIPT: Aborted"; }
            public static string AbortStepFailed(string stepName, string message) { return "ABORT: " + stepName + " | Failed | Error=" + message; }
            public static string UnhandledExceptionFired() { return "EXCEPTION: An unexpected error occurred"; }
            public static string UnhandledException(string type, string message) { return "EXCEPTION: " + type + " | Error=" + message; }
            public static string UnhandledExceptionStack(string trace) { return "EXCEPTION: Stack | " + trace; }
            public static string UnhandledExceptionInner(string message) { return "EXCEPTION: Inner | Error=" + message; }
            public static string UnhandledExceptionObject(object obj) { return "EXCEPTION: Non-exception object | Value=" + obj; }
            public static string MultipleMenusVisible(string names) { return "MENU: Multiple visible | Menus=" + names; }
            // --INI--
            public static string IniWritten() { return "INI: File | Generated"; }
            public static string IniSnapshotFailed(string message) { return "INI: Write time and content snapshot | Failed | Error=" + message; }
            public static string IniWriteFailed(string message) { return "INI: Could not save config file | Error=" + message; }
            public static string IniReadFailed(string message) { return "INI: Could not read config file | Error=" + message; }
            public static string IniReloaded(int mode) { return "INI: File | Reloaded | LoggingMode=" + mode; }
            public static string IniStructureRestored() { return "INI: File | Regenerated to canonical structure | No setting changed | Reload not announced"; }
            public static string RefreshIniFailed(string message) { return "INI: Could not reload config file | Error=" + message; }
            public static string IniRestored() { return "INI: File | Missing | Regenerated from current settings"; }
            public static string IniRegenerated(int count) { return "INI: File | Validated | UserChanges=" + count; }
            public static string ValidateConfigFileFailed(string message) { return "INI: Could not repair config file; untouched | Error=" + message; }
            public static string IniReset() { return "INI: ResetAllSettings | Reset to defaults"; }
            public static string ResetScalarDefaultsFailed(string message) { return "INI: Could not reset settings | Error=" + message; }
            public static string ResetToDefault(string row, string value) { return "SETTINGS: " + row + " | Reset to default | Value=\"" + value + "\""; }
            // --File Watching--
            public static string FileWatcherStarted(string folder, string filter) { return "FILE-WATCH: " + folder + " | Watching | Filter=" + filter; }
            public static string FileWatcherFailed(string folder, string filter, string message) { return "FILE-WATCH: " + folder + " | Could not start, polling instead | Filter=" + filter + " | Error=" + message; }
            public static string ScriptFolderCreateFailed(string folder, string message) { return "FILE-WATCH: " + folder + " | Could not create the script folder | Error=" + message; }
            // --INI Parse--
            public static string IniInvalidValue(string key, string value) { return "INI: " + key + " | Invalid value | Value=\"" + value + "\""; }
            public static string IniInvalidValueRange(string key, string value, string expected) { return "INI: " + key + " | Invalid value | Value=\"" + value + "\" | Expected=" + expected; }
            public static string IniInvalidKeyName(string key, string value) { return "INI: " + key + " | Invalid key name | Value=\"" + value + "\""; }
            public static string IniMenuEntryPointsEmpty(string key) { return "INI: " + key + " | Toggle key and cheat string both empty | Restoring last value"; }
            public static string IniMenuEntryPointsDefaulted(string key, string value) { return "INI: " + key + " | Toggle key and cheat string both empty | No last value | Value=\"" + value + "\""; }
            public static string IniValueRetained(string key, string value) { return "INI: " + key + " | Reverted to last valid value | Value=\"" + value + "\""; }
            // --Settings Snapshot--
            public static string SettingsHeader() { return "Settings:"; }
            public static string SettingsEntry(string key, string value) { return " " + key + " = " + value; }
            public static string SettingsChanged(string key, string oldValue, string newValue) { return " " + key + ": " + oldValue + " -> " + newValue; }
            // --Notifications--
            public static string NotificationRemoveFailed(string message) { return "NOTIFICATION: THEFEED_REMOVE_ITEM | Failed | Error=" + message; }
            public static string FrozenPostClearFailed(string message) { return "NOTIFICATION: THEFEED_CLEAR_FROZEN_POST | Failed | Error=" + message; }
            // --Weapon--
            public static string WeaponSlotActionFailed(string action, string message) { return "WEAPONSLOT: " + action + " | Failed | Error=" + message; }
            // --Respawn--
            public static string RespawnModelNotLoaded() { return "RESPAWN: Model | Failed to load"; }
            public static string RespawnCreateFailed(string modelName) { return "RESPAWN: " + modelName + " | Could not create the new vehicle"; }
            public static string RespawnDone(string displayName, int modelHash) { return "RESPAWN: " + FormatModelLabel(displayName, modelHash) + " | Done"; }
            public static string RespawnTrainRefused(string displayName, int modelHash) { return "RESPAWN: " + FormatModelLabel(displayName, modelHash) + " | Refused | Train"; }
            public static string RespawnSyncFailed(string message) { return "RESPAWN: SyncFieldValues | Failed | Error=" + message; }
            public static string RespawnFailed(string message) { return "RESPAWN: Respawn failed | Error=" + message; }
            public static string RespawnStageFailed(string stage, string message) { return "RESPAWN: " + stage + " | Stage failed | Error=" + message; }
            public static string SpawnRefreshDone(string displayName, int modelHash, string kitState) { return "REFRESH: " + FormatModelLabel(displayName, modelHash) + " | Transmission | Gravity | Kit=" + kitState; }
            public static string AutoRespawnRequested(string modelName, int modelHash, string trigger, string mode) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Auto | Requested | Trigger=" + trigger + " | Mode=" + mode; }
            public static string AutoRespawnCanceled(string modelName, int modelHash, string trigger) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Auto | Canceled | Trigger=" + trigger + " | Every changed field is back to its original value"; }
            public static string RespawnVehicleWeaponRestored(string modelName, int modelHash, string weapon, bool restored) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Vehicle weapon | " + weapon + " | Restored=" + (restored ? "True" : "False"); }
            public static string RespawnFlightStateRestored(string modelName, int modelHash, float hover, float rotor, string gear, float nozzle, int gliderState, string wingDeployedRatio) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Flight state | Hover=" + hover.ToString("F2") + " | Rotor=" + rotor.ToString("F2") + " | Gear=" + gear + " | Nozzle=" + nozzle.ToString("F2") + " | Glider=" + (gliderState < 0 ? "None" : gliderState.ToString()) + " | WingDeployedRatio=" + wingDeployedRatio; }
            public static string RespawnTransformRestored(string modelName, int modelHash, string mode) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Transform | Mode=" + mode; }
            public static string RespawnMotionReapplied(string modelName, int modelHash, float speedBefore, float speedAfter) { return "RESPAWN: " + FormatModelLabel(modelName, modelHash) + " | Motion reapplied after the first update | SpeedBefore=" + speedBefore.ToString("F2", CultureInfo.InvariantCulture) + " | SpeedAfter=" + speedAfter.ToString("F2", CultureInfo.InvariantCulture); }
            // --Visualization--
            public static string WheelBoundNotFound(string modelName, int modelHash, string wheel) { return "VISUALIZATION: " + FormatModelLabel(modelName, modelHash) + " | Wheel bound | Not found | Wheel=" + wheel; }
            public static string WaterSamplesNotFound(string modelName, int modelHash, string step) { return "VISUALIZATION: " + FormatModelLabel(modelName, modelHash) + " | Water samples | Not found | Step=" + step + " | Water Samples and Pontoon Samples skipped"; }
            public static string PontoonSamplesNotFound(string modelName, int modelHash, int sampleCount) { return "VISUALIZATION: " + FormatModelLabel(modelName, modelHash) + " | Pontoon samples | Not found on seaplane handling | Samples=" + sampleCount + " | Pontoon samples skipped"; }
            // --Unload--
            public static string UnloadSuppressed(string displayName, int modelHash, int deleted) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Suppressed | Deleted=" + deleted; }
            public static string UnloadAborted(string displayName, int modelHash) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Aborted | Instance spawned"; }
            public static string UnloadDone(string displayName, int modelHash, int elapsedMs) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Unloaded | Elapsed=" + elapsedMs + "ms"; }
            public static string UnloadTrainRefused(string displayName, int modelHash) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Refused | Train"; }
            public static string UnloadCarGenerators(bool disabled) { return "UNLOAD: Car generators | " + (disabled ? "Disabled map-wide" : "Enabled"); }
            public static string UnloadVehicleBudget(int level) { return "UNLOAD: Vehicle population budget | Level=" + level; }
            public static string UnloadUnsuppressedOnReload(string displayName, int modelHash) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Unsuppressed | Script reload"; }
            public static string UnloadRespawned(string displayName, int modelHash, int elapsedMs) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Respawned | Elapsed=" + elapsedMs + "ms"; }
            public static string UnloadRespawnSkipped(string displayName, int modelHash, string reason) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Respawn skipped | " + reason; }
            public static string UnloadEvictionUnavailable(string displayName, int modelHash, string reason) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Fragment eviction unavailable | " + reason; }
            public static string UnloadEvictionOutcome(string displayName, int modelHash, string outcome) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Fragment eviction | Outcome=" + outcome; }
            public static string UnloadFragmentNotEvicted(string displayName, int modelHash, int elapsedMs, string lastOutcome) { return "UNLOAD: " + FormatModelLabel(displayName, modelHash) + " | Unloaded | Elapsed=" + elapsedMs + "ms | Fragment still loaded | LastOutcome=" + lastOutcome; }
            // --LiveSubData--
            public static string LiveSubDataUnavailable(string category, string reason) { return "LIVESUBDATA: " + category + " | Live creation unavailable | " + reason; }
            public static string LiveSubDataRequested(string displayName, int modelHash, string category) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Requested"; }
            public static string LiveSubDataOutcome(string displayName, int modelHash, string category, string outcome, int elapsedMs) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Outcome=" + outcome + " | Elapsed=" + elapsedMs + "ms"; }
            public static string LiveSubDataApplied(string displayName, int modelHash, string category, bool vehicleAlive) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Applied | VehicleAlive=" + (vehicleAlive ? "True" : "False"); }
            public static string LiveSubDataFallback(string displayName, int modelHash, string category, string reason) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Falling back to export-only | " + reason; }
            public static string LiveSubDataDeletionRequested(string displayName, int modelHash, string category) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Deletion requested"; }
            public static string LiveSubDataDeletionOutcome(string displayName, int modelHash, string category, string outcome, int elapsedMs) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Deletion outcome=" + outcome + " | Elapsed=" + elapsedMs + "ms"; }
            public static string LiveSubDataDeleted(string displayName, int modelHash, string category) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Deleted"; }
            public static string LiveSubDataDeletionFailed(string displayName, int modelHash, string category, string reason) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | " + category + " | Not deleted | " + reason; }
            public static string LiveSubDataAppliedCategoryKept(string category, string reason) { return "LIVESUBDATA: " + category + " | Created by the applied handling | Kept after its undo | " + reason; }
            public static string LiveAdvancedDataRequested(string displayName, int modelHash, int capacity) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | AdvancedData | Capacity requested=" + capacity; }
            public static string LiveAdvancedDataOutcome(string displayName, int modelHash, int capacity, string outcome, int elapsedMs) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | AdvancedData | Capacity outcome=" + outcome + " | Capacity=" + capacity + " | Elapsed=" + elapsedMs + "ms"; }
            public static string LiveAdvancedDataApplied(string displayName, int modelHash, int count) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | AdvancedData | Applied | Entries=" + count; }
            public static string LiveAdvancedDataFailed(string displayName, int modelHash, int capacity, string reason) { return "LIVESUBDATA: " + FormatModelLabel(displayName, modelHash) + " | AdvancedData | Not grown | Capacity=" + capacity + " | " + reason; }
            public static string LiveReplayEntryNotMoved(string description, string reason) { return "LIVESUBDATA: " + description + " | Failed replay stays where it is | A later edit buried it | " + reason; }
            // --Menu--
            public static string MenuScrollWindowFieldMissing() { return "MENU: Scroll window | First-row field not found | Removal gap fix disabled"; }
            public static string MenuSelectedRectFieldMissing() { return "MENU: Highlight bar | The menu library's private highlight field is missing | Result flashes are disabled"; }
            public static string MenuItemTextFieldsMissing() { return "MENU: Row text | The menu library's private row text fields are missing | A flashing row keeps its own text colors"; }
            public static string MenuReopened(string menuName, string fellBackFromName) { return "MENU: Reopened | " + menuName + (fellBackFromName == null ? " | Remembered" : " | Fell back from " + fellBackFromName); }
            public static string AdvancedDataEntryMenuBuilt(int entryIndex, int contextIndex, AdvancedDataMenuKind kind) { return "ADVANCEDDATA: Entry " + (entryIndex + 1) + " | Menu built | Tree=" + TreeLabel(contextIndex) + " | Kind=" + kind; }
            // --Warm Up--
            public static string PrecompileDone(int prepared, int skipped, int failed, double milliseconds, int ticks) { return "WARMUP: Precompile | Done | Prepared=" + prepared + " | Skipped=" + skipped + " | Failed=" + failed + " | Time=" + milliseconds.ToString("F0") + "ms | Ticks=" + ticks; }
            public static string PrecompileMethodFailed(string method, string exceptionType, string message) { return "WARMUP: Precompile | Method failed | Method=" + method + " | " + exceptionType + ": " + message + " | Later failures are only counted"; }
            // --Perf--
            // --Teleport--
            public static string TeleportedToVehicle(string displayName, int modelHash, bool driverRemoved) { return "TELEPORT: " + FormatModelLabel(displayName, modelHash) + " | Done" + (driverRemoved ? " | Driver set out of the vehicle" : ""); }
            public static string DeletedVehicle(string displayName, int modelHash, int occupantsDeleted, bool onScreen) { return "DELETE: " + FormatModelLabel(displayName, modelHash) + " | Occupants=" + occupantsDeleted + " | " + (onScreen ? "OnScreen" : "OffScreen"); }
            // --Saved Handlings--
            public static string GetExportsError(string message) { return "HANDLING: Could not list the saved handlings | Error=" + message; }
            public static string HandlingSaved(string name) { return "HANDLING: " + name + " | Saved"; }
            public static string HandlingSaveError(string name, string message) { return "HANDLING: " + name + " | Could not save | Error=" + message; }
            public static string HandlingVerifyFailed(string name) { return "HANDLING: " + name + " | Saved file was unreadable"; }
            public static string HandlingUnverifiedFileRemovalFailed(string message) { return "HANDLING: Removing the file that failed to save correctly also failed | Error=" + message; }
            public static string HandlingVerifyError(string message) { return "HANDLING: Save verification | Read failed | Error=" + message; }
            public static string HandlingNameMismatch(string operation, string handlingName) { return "HANDLING: " + handlingName + " | " + operation + " | Not this vehicle's own handling entry | May not match its handlingId in vehicles.meta | Marked in the file"; }
            public static string HandlingBackupReadFailed(string path, string message) { return "HANDLING: " + Path.GetFileName(path) + " | Could not read the existing file | Error=" + message; }
            public static string HandlingOverwriteParseFailed(string path, string message) { return "HANDLING: " + Path.GetFileName(path) + " | Existing file is not a valid handling; not overwritten | Error=" + message; }
            public static string HandlingOverwriteNameUnresolved(string path) { return "HANDLING: " + Path.GetFileName(path) + " | Existing file has no handlingName; not overwritten"; }
            public static string HandlingRestoredAfterFailedWrite(string path) { return "HANDLING: " + path + " | Restored after a failed write"; }
            public static string HandlingRestoreFailed(string path, string message) { return "HANDLING: " + Path.GetFileName(path) + " | Could not restore the previous file | Error=" + message; }
            public static string HandlingFileNotFound(string name) { return "HANDLING: " + name + " | File not found"; }
            public static string HandlingApplied(string name) { return "HANDLING: " + name + " | Applied"; }
            public static string HandlingApplyIdentical(string name) { return "HANDLING: " + name + " | Identical to the current handling | Not applied"; }
            public static string HandlingAppliedPartially(string name, int invalid, int absent) { return "HANDLING: " + name + " | Applied | Invalid=" + invalid + " | NotInFile=" + absent + " | Those fields keep their current values"; }
            public static string HandlingApplyError(string name, string message) { return "HANDLING: " + name + " | Could not apply | Error=" + message; }
            public static string PartiallyApplyHandling(string name, string message) { return "HANDLING: " + name + " | Apply stopped partway; some fields were applied | Error=" + message; }
            public static string HandlingFieldInvalid(string field, string value) { return "HANDLING: " + field + " | Invalid value | Value=\"" + value + "\" | Field skipped"; }
            public static string HandlingRocketBoostSkipped() { return "HANDLING: RocketBoostCapacity, BoostMaxSpeed | Not applied | Vehicle has no rocket boost"; }
            public static string HandlingTurretPitchLimitsSkipped() { return "HANDLING: TurretPitchLimitData | Not applied | Vehicle has no rear mounted turret"; }
            public static string HandlingCategoryRowMissing(string category) { return "HANDLING: " + category + " | The mapper has no row for this category | Skipped by every save and apply"; }
            // --Model flags--
            public static string ModelFlagOffsetResolved(int offset, int apiOffset) { return "MODELFLAGS: Flag base offset | Resolved | Offset=0x" + offset.ToString("X") + " | ApiOffset=0x" + apiOffset.ToString("X"); }
            public static string ModelFlagOffsetUnresolved(string reason) { return "MODELFLAGS: Flag base offset | Unresolved | Turret pitch limits stay shown and saved, glider restore skipped | Reason=" + reason; }
            public static string GearRatioPatchResolved(string state, int gearCap) { return "GEARRATIOPATCH: Plugin | " + state + " | InitialDriveGearsCap=" + gearCap; }
            public static string ColliderStateChanged(string modelName, int modelHash, string state, GTA.Math.Vector3 centerOfGravity, GTA.Math.Vector3 centerOfMass) { return "COLLIDER: " + FormatModelLabel(modelName, modelHash) + " | State | " + state + " | CenterOfGravityOffset=" + FormatOffset(centerOfGravity) + " | CenterOfMassOffset=" + FormatOffset(centerOfMass); }
            public static string CenterOfGravityReasserted(string modelName, int modelHash, GTA.Math.Vector3 was, GTA.Math.Vector3 now) { return "COLLIDER: " + FormatModelLabel(modelName, modelHash) + " | CenterOfGravityOffset | Reasserted while rigid | Was=" + FormatOffset(was) + " | Now=" + FormatOffset(now); }
            private static string FormatOffset(GTA.Math.Vector3 offset) { return offset.X.ToString("F3", CultureInfo.InvariantCulture) + ", " + offset.Y.ToString("F3", CultureInfo.InvariantCulture) + ", " + offset.Z.ToString("F3", CultureInfo.InvariantCulture); }
            public static string FieldEntryMalformed(string category, string tag, int completeGroups, int halfSetGroups) { return "FIELDENTRY: " + category + "." + tag + " | Needs exactly 1 getter and setter pair and no other accessor | CompleteGroups=" + completeGroups + " | HalfSetGroups=" + halfSetGroups; }
            public static string FlagEntryMalformed(string table, string name, int bitIndex, string problem) { return "FLAGENTRY: " + table + "." + name + " | " + problem + " | BitIndex=" + bitIndex; }
            public static string HandlingSubHandlingTypeUnrecognized(string type) { return "HANDLING: SubHandlingData | Unrecognized type | Type=\"" + type + "\" | Subcategory skipped"; }
            public static string HandlingDuplicated(string source, string outName) { return "HANDLING: " + source + " | Duplicated | To=" + outName; }
            public static string HandlingDuplicateError(string name, string message) { return "HANDLING: " + name + " | Could not duplicate | Error=" + message; }
            public static string HandlingRenamed(string oldName, string newName) { return "HANDLING: " + oldName + " | Renamed | To=" + newName; }
            public static string HandlingRenameError(string name, string message) { return "HANDLING: " + name + " | Could not rename | Error=" + message; }
            public static string HandlingOverwritten(string name) { return "HANDLING: " + name + " | Overwritten"; }
            public static string HandlingDeleted(string name) { return "HANDLING: " + name + " | Deleted"; }
            public static string HandlingDeleteError(string name, string message) { return "HANDLING: " + name + " | Could not delete | Error=" + message; }
            public static string HandlingPostRefreshFailed(string name, string message) { return "HANDLING: " + name + " | File operation succeeded | Menu refresh failed | Error=" + message; }
            public static string ModelNameResolved(string name, int modelHash, string source) { return "MODELNAME: " + FormatModelLabel(name, modelHash) + " | Resolved | Source=" + source; }
            public static string HandlingNameResolved(string name, int modelHash, string source) { return "HANDLINGNAME: " + FormatModelLabel(name, modelHash) + " | Resolved | Source=" + source; }
            public static string ModelMetaDataLoaded(int count, int handlingIds, int skipped) { return "META: ModelMetaData file | Loaded | Entries=" + count + " | HandlingIds=" + handlingIds + " | Skipped=" + skipped; }
            public static string ModelMetaDataNotFound(string path) { return "META: ModelMetaData file | Not found | Path=" + path + " | Using the base-game model list and display names"; }
            public static string ModelMetaDataReadFailed(string message) { return "META: ModelMetaData file | Read failed | Error=" + message; }
            public static string HandlingNameHashUnverified(string handling, long address, int rawHash) { return "HANDLING KEY: " + handling + " (struct 0x" + address.ToString("X") + ") | Name hash not verified | " + (rawHash == 0 ? "The read returned 0" : "The game's lookup of 0x" + rawHash.ToString("X8") + " did not return this handling") + " | Left out of the reload state"; }
            public static string ReloadStateHandlingUnresolved(string section, string hashText) { return "PERSISTENCE: " + section + " | Handling name hash " + hashText + " | Names no handling | Every line of that hash skipped"; }
            // --Autosave--
            public static string AutosaveWritten(string name) { return "AUTOSAVE: " + name + " | Written"; }
            public static string AutosaveFailed(string name, string message) { return "AUTOSAVE: " + name + " | Could not write the autosave | Error=" + message; }
            public static string AutosaveNoVehicle(string handling) { return "AUTOSAVE: " + handling + " | No live vehicle of this handling | Skipped"; }
            public static string AutosaveUnusableName(string name) { return "AUTOSAVE: " + name + " | Not a usable file name | Skipped"; }
            public static string PreviewBuildFailed(string name, string message) { return "PREVIEW: " + name + " | Parse failed | Error=" + message; }
            public static string SubDataToggleFailed(string category, string message) { return "SUBDATA: " + category + " | Toggle failed | Error=" + message; }
            // --Descriptors--
            public static string DescriptorMissing(string fieldName) { return "DESCRIPTORS: " + fieldName + " | No entry | Using empty description"; }
            public static string DescriptorUnpairedTilde(string key) { return "DESCRIPTORS: " + key + " | Unpaired ~ | Text after it does not display"; }
            // --Undo/Redo--
            public static string UndoApplied(string fieldKey) { return "UNDO: " + fieldKey + " | Applied"; }
            public static string RedoApplied(string fieldKey) { return "REDO: " + fieldKey + " | Applied"; }
            public static string UndoAllBaselineCaptured(string handling) { return "UNDOALL: " + handling + " | Baseline captured"; }
            public static string UndoAllBaselineMissing(string handling) { return "UNDOALL: " + handling + " | No baseline | The history has entries but no handling to return to"; }
            public static string UndoAllApplied(int entries) { return "UNDOALL: Baseline applied | Entries=" + entries; }
            public static string UndoAllAtBaseline(int entries) { return "UNDOALL: Handling already matches its baseline | Entries=" + entries + " | History cleared, nothing applied"; }
            public static string UndoAllPaused(int pendingRequests) { return "UNDOALL: Paused | Waiting for live requests | Pending=" + pendingRequests; }
            public static string UndoAllResumed() { return "UNDOALL: Resumed | No live request is pending"; }
            public static string UndoAllStillWaiting(int pendingRequests) { return "UNDOALL: Not started | An earlier Undo All is still waiting for live requests | Pending=" + pendingRequests; }
            public static string UndoAllFinished(int entries, int notReverted) { return "UNDOALL: Finished | Entries=" + entries + " | NotReverted=" + notReverted; }
            public static string UndoWaitingForLiveRequest() { return "UNDO: Active model | Not started | A live category request is still pending"; }
            public static string RedoWaitingForLiveRequest() { return "REDO: Active model | Not started | A live category request is still pending"; }
            public static string UndoAllWaitingForLiveRequest() { return "UNDOALL: Not started | A live category request is still pending"; }
            public static string FieldRegistryKeyCollision(string fieldKey) { return "FIELDREGISTRY: " + fieldKey + " | Registered more than once | The earlier field's undo and handling-apply path is orphaned"; }
            public static string FlagConflictGroupLengthMismatch(int bindingsCount, int entriesCount) { return "FLAGS: Flag conflict groups not set up | Bindings=" + bindingsCount + " | Entries=" + entriesCount; }
            public static string WorksUpsideDownForced(string displayName, int modelHash) { return "FLAGS: " + FormatModelLabel(displayName, modelHash) + " | SF_WORKS_UPSIDE_DOWN forced on | Vehicle has no " + HOVER_SLIDE_BONE_NAME + " bone"; }
            public static string UndoNothingToApply() { return "UNDO: Active model | Nothing to undo"; }
            public static string RedoNothingToApply() { return "REDO: Active model | Nothing to redo"; }
            public static string UndoAllFailuresHeader() { return "UndoAll failures:"; }
            public static string UndoAllFailureEntry(string description, string reason) { return " " + description + ": " + reason; }
            public static string UndoEntryFailed(string description, string reason) { return "UNDO: " + description + " | Not applied | Reason=" + reason; }
            public static string RedoEntryFailed(string description, string reason) { return "REDO: " + description + " | Not applied | Reason=" + reason; }
            public static string UndoEntrySkipped(string description, string category) { return "UNDO: " + description + " | Skipped | Export-only " + category + " category now exists live"; }
            public static string AppliedHandlingUndone() { return "UNDO: Last applied handling | Reverted"; }
            public static string AppliedHandlingRedone() { return "REDO: Last undone handling | Reapplied"; }
            public static string ReloadStateWritten(string sections) { return "PERSISTENCE: ReloadState | Written | " + sections; }
            public static string ReloadStateWriteFailed(string section, string message) { return "PERSISTENCE: " + section + " | Write failed | Error=" + message; }
            public static string ReloadStateReadFailed(string message) { return "PERSISTENCE: ReloadState | Read failed | Error=" + message; }
            public static string ReloadStateDeleteFailed(string message) { return "PERSISTENCE: ReloadState | File delete failed after a read | Error=" + message; }
            public static string ReloadStateHeaderUnreadable(string cause) { return "PERSISTENCE: ReloadState | Header unreadable | Cause=" + cause + " | Treated as another game launch"; }
            public static string ReloadStateOtherLaunch() { return "PERSISTENCE: ReloadState | Discarded | Written by another game launch"; }
            public static string ReloadStateSectionLoaded(string section, int count) { return "PERSISTENCE: " + section + " | Loaded | Entries=" + count; }
            public static string ReloadStateSectionFailed(string section, string message) { return "PERSISTENCE: " + section + " | Load failed | Error=" + message; }
            public static string UndoHistoryLineUnrecognized(int rawLength) { return "PERSISTENCE: UndoHistory | Unrecognized line | Length=" + rawLength + " | Entry skipped"; }
            public static string UndoHistoryKindUnknown(string kind) { return "PERSISTENCE: UndoHistory | Unknown structural kind | Kind=" + kind + " | Entry skipped"; }
            public static string StructuralEditDecodeFailed(string message) { return "ELEMENT: Decode failed | Entry skipped | Error=" + message; }
            public static string PhantomFlagsUnparseable(string rawValue) { return "PHANTOM: strAdvancedFlags | Not valid hex | Value=\"" + rawValue + "\" | Treated as 0 | Other bits may be lost"; }
            public static string PreviewDiffSnapshotCaptured(string handling) { return "DIFF: " + handling + " | Baseline captured"; }
            public static string PreviewDiffSnapshotGateCheck(string handling, string modelName, bool alreadyCaptured) { return "DIFF: " + handling + " | Scoped=" + modelName + " | " + (alreadyCaptured ? "Already captured | Skipping" : "Not yet captured | Capturing"); }
            public static string VehicleSearchDone(string text, int totalMatches, int shown) { return "SEARCH: " + text + " | Matches=" + totalMatches + " | Shown=" + shown; }
            public static string SearchResultUnreadable(string modelName) { return "SEARCH: " + modelName + " | Handling not readable | Not applied"; }
            public static string VehicleSearchAll(int modelCount) { return "SEARCH: All models | Listed=" + modelCount; }
            public static string MenuRestoreSkipped(int savedCount, int currentCount) { return "PERSISTENCE: MenuRestore | Skipped | Saved menu count=" + savedCount + " | Current menu count=" + currentCount; }
            public static string PreviewPositionRestored(string menuName, int row) { return "PREVIEW: Position restored | " + menuName + " | Row=" + row; }
            public static string PreviewPositionSkipped(string reason) { return "PREVIEW: Position not restored | " + reason; }
            public static string MenuRestoreApplied(string menuName, int row) { return "PERSISTENCE: MenuRestore | Restored | " + menuName + " | Row=" + row; }
            public static string SelectiveCopyApplied(string fieldKey) { return "SELECTIVECOPY: " + fieldKey + " | Applied"; }
            public static string SelectiveCopyIdentical(string fieldKey) { return "SELECTIVECOPY: " + fieldKey + " | Identical to the current value | Not applied"; }
            public static string SelectiveCopyCategory(string category, int applied, int identical, int skipped) { return "SELECTIVECOPY: Category=" + category + " | Applied=" + applied + " | Identical=" + identical + " | Skipped=" + skipped; }
            public static string SelectiveCopyConflictsCleared(string fieldKey, string clearedFlags) { return "SELECTIVECOPY: " + fieldKey + " | Conflicting flags cleared | Flags=" + clearedFlags; }
            public static string SelectiveCopyFieldUnresolved(string fieldKey) { return "SELECTIVECOPY: " + fieldKey + " | No registered field | Not applied"; }
            public static string SelectiveCopyCategoryAbsent(string fieldKey, string category) { return "SELECTIVECOPY: " + fieldKey + " | Category=" + category + " | Absent on the live vehicle | Not applied"; }
            public static string SelectiveCopyEntryAbsent(string fieldKey, string category) { return "SELECTIVECOPY: " + fieldKey + " | Category=" + category + " | Entry absent in the category | Not applied"; }
            public static string SelectiveCopyRocketBoostAbsent(string fieldKey) { return "SELECTIVECOPY: " + fieldKey + " | Vehicle has no rocket boost | Not applied"; }
            public static string SelectiveCopyRearMountedTurretAbsent(string fieldKey) { return "SELECTIVECOPY: " + fieldKey + " | Vehicle has no rear mounted turret | Not applied"; }
        }
    }
}
