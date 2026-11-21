using System;
using System.IO;
using System.Reflection;
using GTA;
using LemonUI;

[assembly: AssemblyVersion(RHE.RageHandlingEditor.ScriptVersion)]
[assembly: AssemblyFileVersion(RHE.RageHandlingEditor.ScriptVersion)]
[assembly: AssemblyTitle("RageHandlingEditor")]
[assembly: AssemblyProduct("RageHandlingEditor")]
[assembly: AssemblyCompany("KieranBLK")]

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        internal const string ScriptVersion = "1.1.1";

        private ObjectPool pool;
        // Set when BuildMenu or the rest of the startup load throws, leaving a half-built instance
        // OnTick then runs no stage and OnKeyDown returns, so it cannot throw every frame
        private bool startupFailed;

        public RageHandlingEditor()
        {
            // The logger, the notification feed, and the name resolver come first, since every later step can log, post, or name a vehicle
            // The tick stages built below bind their methods, so these must exist before them
            // The logger reads the feed only when an ERROR logs, so the feed being built on the next line is never too late
            logger = new Logger("scripts\\RageHandlingEditor.log", () => loggingMode, (key, message) => notifications.PostNotificationOnce(key, message));
            notifications = new NotificationFeed(logger, () => uptimeTicks == 0);
            modelNames = new ModelNameResolver(logger);
            currentContext = editorContexts[0];
            activeContext = currentContext;
            // Built once so every OnTick frame reuses the same delegate instances instead of allocating fresh ones
            // Safe to build before pool/allMenus/etc exist below, since these are closures read at invocation time (OnTick, well after construction completes), not now
            // The contexts they capture exist already, empty until BuildMenu fills them
            InitTickStages();
            handlingMetaXml = new HandlingMetaXmlMapper(logger, () => weaponModTypeSupported, () => turretSlotCount);
            visualizer = new HandlingVisualizer(logger, modelNames, () => showHandlingVisualization, editorContexts.Length);
            InitSavedHandlingSources();
            InitHotkeyTable();
            reloadState = new ReloadStateFile(logger, modelNames, BuildReloadStateProviders());
            // RawMemory's state is static and outlives a script reload, so each load resets it here
            RawMemory.ResetForScriptLoad();
            iniPath = "scripts\\RageHandlingEditor.ini";
            try
            {
                Tick += OnTick;
                KeyDown += OnKeyDown;
                Aborted += OnAborted;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                logger.StartSession(iniPath);
                CaptureIniDefaults();
                if (!File.Exists(iniPath))
                {
                    ApplyIniDefaults();
                    WriteDefaultIni();
                    ReadIni();
                }
                else
                {
                    ReadIni();
                    ValidateConfigFile();
                    if (resetAllSettings) PerformSettingsReset();
                }
                try
                {
                    iniLastWriteTime = File.GetLastWriteTime(iniPath);
                    iniLastContent = File.ReadAllText(iniPath);
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.IniSnapshotFailed(ex.Message)); }
                logger.Log(Dbg.Level.Info, Dbg.Config(loggingMode));
                lastSettingsSnapshot = BuildSettingsSnapshot();
                LogSettingsSnapshot(lastSettingsSnapshot);
                // A first run has no script folder yet, and the watcher needs one to watch the Exports tree from
                try { if (!Directory.Exists(SCRIPT_FOLDER_PATH)) Directory.CreateDirectory(SCRIPT_FOLDER_PATH); }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.ScriptFolderCreateFailed(SCRIPT_FOLDER_PATH, ex.Message)); }
                fileWatcher = new OwnedFileWatcher(logger, iniPath);
                try { BuildMenu(); }
                catch (Exception ex)
                {
                    startupFailed = true;
                    logger.Log(Dbg.Level.Error, Dbg.ConstructorPhaseFailed("BuildMenu", ex.Message));
                }
                reloadState.ReadReloadState();
                reloadState.LoadReloadStateSection(ReloadStateSection.UndoHistory);
                reloadState.LoadReloadStateSection(ReloadStateSection.PhantomSubData);

                // The PreviewDiffSnapshot section loads before InitializePreviewDiffSnapshots: A script reload does not reset the game's shared, process-lifetime HandlingData
                // Restoring the disk-persisted true original first lets the sweep's "not yet captured" gate skip every handling already recovered from disk
                // Otherwise the sweep would re-derive from a live read a prior edit may have already changed
                reloadState.LoadReloadStateSection(ReloadStateSection.PreviewDiffSnapshot);
                try { InitializePreviewDiffSnapshots(); } catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.ConstructorPhaseFailed("InitializePreviewDiffSnapshots", ex.Message)); }
                reloadState.LoadReloadStateSection(ReloadStateSection.SelectedSlots);
                reloadState.LoadReloadStateSection(ReloadStateSection.MenuRestore);
                // Every section above has now loaded, so nothing left can race TrackSeatedVehicle's seat-change effects
                // Its live Diff capture, auto-merge, and trailer auto-mount are safe to resume from here on
                startupLoadInProgress = false;
                Interval = 0;
            }
            catch (Exception ex)
            {
                // A throw outside BuildMenu's own guard leaves the load half done, so the instance stays quiet the same way and seat tracking never waits on a stopped load
                startupFailed = true;
                startupLoadInProgress = false;
                logger.Log(Dbg.Level.Error, Dbg.ConstructorFailed(ex.Message));
            }
            finally
            {
                logger.FlushLog();
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            logger.FlushLog();
            logger.Log(Dbg.Level.Info, Dbg.ScriptAborted());
            // Each step runs behind its own failure boundary, so one throwing step never skips the autosave flush or the persistence writes after it
            if (fileWatcher != null) RunAbortStep(fileWatcher.Dispose, "DisposeFileWatcher");
            RunAbortStep(notifications.RemoveAllActiveNotifications, "RemoveAllActiveNotifications");
            RunAbortStep(UnsuppressPendingModelUnloads, "UnsuppressPendingModelUnloads");
            RunAbortStep(ReenableSeatWeaponSuppression, "ReenableSeatWeaponSuppression");
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            autoRespawnBlockedByUnload = true;
            RunAbortStep(FlushAllPendingCycleBatches, "FlushAllPendingCycleBatches");
            // A change made in the tick before a reload has not been written yet
            RunAbortStep(FlushPendingAutosaves, "FlushPendingAutosaves");
            RunAbortStep(reloadState.WriteReloadState, "WriteReloadState");
            // The steps above can produce log entries after the first flush ran
            logger.FlushLog();
        }

        private void RunAbortStep(Action step, string stepName)
        {
            try { step(); }
            catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.AbortStepFailed(stepName, ex.Message)); }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            logger.FlushLog();
            try
            {
                logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionFired());
                logger.FlushLog();
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null)
                {
                    logger.Log(Dbg.Level.Error, Dbg.UnhandledException(ex.GetType().Name, ex.Message), false);
                    logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
                    if (ex.InnerException != null) logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionInner(ex.InnerException.Message), false);
                }
                else logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionObject(e.ExceptionObject), false);
                logger.FlushLog();
            }
            catch { }
        }
    }
}
