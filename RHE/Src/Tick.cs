using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private int uptimeTicks;
        // Game time at which the fallback poll for external edits is due, read only for a file whose watcher could not start
        private int ownedFileExternalEditPollDueTime;
        private int descriptorsPollDueTime;
        // Set by the IniWatch stage for the stages after it in the same tick, True when that file changed outside the script this tick
        private bool iniChangedThisTick;
        private bool savedHandlingsChangedThisTick;
        private bool modelMetaDataChangedThisTick;
        private int lastTrailerCheckTick;
        // The cheat string the cached hash was computed from, and that hash
        private string cheatStringHashSource;
        private uint cheatStringHash;

        private void OnTick(object sender, EventArgs e)
        {
            uptimeTicks++;
            try
            {
                if (startupFailed) ServiceNotificationsAfterStartupFailure();
                else RunTickStages();
            }
            catch (Exception ex)
            {
                if (uptimeTicks < tickErrorSuppressUntil) return;
                tickErrorSuppressUntil = uptimeTicks + TICK_ERROR_COOLDOWN;
                logger.Log(Dbg.Level.Error, Dbg.TickFailed(ex.GetType().Name, ex.Message));
                logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
            }
            finally
            {
                // Neither lives longer than one tick. They clear here so a failure above never leaves one stuck True
                // A stuck True would make the Closed dispatcher reopen every menu a close touches
                navigation.NativeCloseExpected = false;
                selectModeArmedThisTick = false;
            }
        }

        private int tickErrorSuppressUntil;

        // Game.WasCheatStringJustEntered hashes its text on every call, so the hash is kept and recomputed only when enableCheatString changes
        private bool WasEnableCheatStringJustEntered()
        {
            if (!string.Equals(cheatStringHashSource, enableCheatString, StringComparison.Ordinal))
            {
                cheatStringHashSource = enableCheatString;
                cheatStringHash = GTA.StringHash.AtStringHashUtf8(enableCheatString);
            }
            return Function.Call<bool>(Hash.HAS_PC_CHEAT_WITH_HASH_BEEN_ACTIVATED, cheatStringHash);
        }

        // One value per exception boundary site: A tick stage or a key press handler. Names the subsystem in the log and keys its own cooldown
        private enum Subsystem
        {
            FrameStamp, StartupNotifications, SeatWeaponReenable, SavedHandlingsParentRestore, ResultFlash, MenuProcessing, WidthRecalc, RawMemoryWarnings, LogFlush, IniWatch, DescriptorsWatch, NotificationSweep, ModelUnloadPoll, LiveSubDataPoll, PlayerResolve, MenuState,
            MultipleMenusDiag, CheatString, SeatTracking, VehicleResolve, DeletedTargetBackOut, FieldSync, MenuRestore, SeatMerge, WidthAnimation, HintSync, HotkeySuppression, GlobalHotkeyPoll, PreviewReposition, SelectiveCopyFlagPoll, SavedHandlingFileCheck, HandlingDeleteFinish,
            ArmedFlash, ModelMetaDataRetry, Autosave, KeyCapturePoll, HandlingResolve, HandlingVisualization, DriftReassertion, AdditionalEditorTracking, WidthDirtyDrain, NavHoldAcceleration, AutoRespawn, VehicleSelectionHover,
            GlobalHotkeys, ArmedActionBackspace, WeaponSlotReorder, PhantomWeaponSlotReorder, PreviewFocusSwitch,
            StartupWarmUp
        }

        // Per-subsystem cooldown, indexed by the enum's underlying int value, on the clock of whichever caller owns the subsystem
        // That clock is ticks for a stage and keypresses for a key handler
        // A failure in one subsystem never starves any other for the cooldown window, and two subsystems failing in the same window are each still logged once
        private int[] subsystemErrorSuppressUntil = new int[Enum.GetValues(typeof(Subsystem)).Length];

        // Which caller an exception boundary serves. It picks the log line's area label
        private enum BoundarySource { Tick, KeyPress, MenuHandler }

        // A throw inside action is logged once per cooldown window and never prevents any *other* guarded block, or the tick's final cleanup, from still running
        // clock and cooldown are the owning caller's own time base and suppression window
        private void SafeCall(Action action, Subsystem subsystem, int clock, int cooldown, BoundarySource source)
        {
            try { action(); }
            catch (Exception ex)
            {
                int index = (int)subsystem;
                if (clock < subsystemErrorSuppressUntil[index]) return;
                subsystemErrorSuppressUntil[index] = clock + cooldown;
                LogBoundaryFailure(subsystem.ToString(), ex, source);
            }
        }

        // The log body every exception boundary shares, so a failure reads the same wherever it was caught
        private void LogBoundaryFailure(string boundaryName, Exception ex, BoundarySource source)
        {
            string exceptionTypeName = ex.GetType().Name;
            logger.Log(Dbg.Level.Error, source == BoundarySource.KeyPress ? Dbg.KeyDownFailed(boundaryName, exceptionTypeName, ex.Message) : source == BoundarySource.MenuHandler ? Dbg.HandlerFailed(boundaryName, exceptionTypeName, ex.Message) : Dbg.TickFailed(boundaryName, exceptionTypeName, ex.Message));
            logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
        }

        private void SafeCallTick(Action action, Subsystem subsystem) { SafeCall(action, subsystem, uptimeTicks, TICK_ERROR_COOLDOWN, BoundarySource.Tick); }

        // The tick count each menu handler stays silent until after it logs, keyed by handler name
        private readonly Dictionary<string, int> menuHandlerErrorSuppressUntil = new Dictionary<string, int>();

        // Wraps a menu event handler's whole body, so a throw in one handler never reaches the menu pool: The pool keeps processing every later menu this frame
        // The boundary cannot undo a write, so a throw between a value write and its undo record leaves the value written with no undo entry
        // Each handler name keys its own cooldown, so a failing handler logs once per window and never silences another
        private void Guard(Action handler, string handlerName)
        {
            try { handler(); }
            catch (Exception ex)
            {
                int suppressUntil;
                if (menuHandlerErrorSuppressUntil.TryGetValue(handlerName, out suppressUntil) && uptimeTicks < suppressUntil) return;
                menuHandlerErrorSuppressUntil[handlerName] = uptimeTicks + TICK_ERROR_COOLDOWN;
                LogBoundaryFailure(handlerName, ex, BoundarySource.MenuHandler);
            }
        }

        // One helper per menu event, each running the body behind Guard under the handler's name
        // A body that reads its event arguments takes them as its parameter
        private void SubscribeShown(NativeMenu menu, string handlerName, Action body) { menu.Shown += (s, e) => Guard(body, handlerName); }
        private void SubscribeClosed(NativeMenu menu, string handlerName, Action body) { menu.Closed += (s, e) => Guard(body, handlerName); }
        private void SubscribeOpening(NativeMenu menu, string handlerName, Action body) { menu.Opening += (s, e) => Guard(body, handlerName); }
        private void SubscribeSelectedIndexChanged(NativeMenu menu, string handlerName, Action<SelectedEventArgs> body) { menu.SelectedIndexChanged += (s, e) => Guard(() => body(e), handlerName); }
        private void SubscribeItemActivated(NativeMenu menu, string handlerName, Action<ItemActivatedArgs> body) { menu.ItemActivated += (s, e) => Guard(() => body(e), handlerName); }
        private void SubscribeActivated(NativeItem item, string handlerName, Action body) { item.Activated += (s, e) => Guard(body, handlerName); }
        private void SubscribeCheckboxChanged(NativeCheckboxItem item, string handlerName, Action body) { item.CheckboxChanged += (s, e) => Guard(body, handlerName); }
        private void SubscribeItemChanged<T>(NativeDynamicItem<T> item, string handlerName, Action<ItemChangedEventArgs<T>> body) { item.ItemChanged += (s, e) => Guard(() => body(e), handlerName); }
        private void SubscribeItemChanged<T>(NativeListItem<T> item, string handlerName, Action<ItemChangedEventArgs<T>> body) { item.ItemChanged += (s, e) => Guard(() => body(e), handlerName); }

        // One step of the per-tick pipeline. Run is built once, here, so no delegate is allocated per frame
        private sealed class TickStage
        {
            public readonly Subsystem Subsystem;
            public readonly Action Run;

            public TickStage(Subsystem subsystem, Action action, Func<bool> gate)
            {
                Subsystem = subsystem;
                Run = gate == null ? action : () => { if (gate()) action(); };
            }
        }

        private TickStage[] tickStages;

        // Set once per tick by the stages that produce them and read by the later stages that need them, never outside the tick that sets them
        // When a producing stage throws, the stages after it read what it last wrote, except where the producer clears its output first
        // TrackSeatedVehicle's per-tick call and UpdateVehicleSelectionHover read tickResolvedPlayer instead of each re-fetching Game.LocalPlayerPed
        // A fresh wrapper is constructed on every Game.LocalPlayerPed access
        private Ped tickResolvedPlayer;
        private bool tickAnyMenuVisible;
        // The visible menu whose hints the HintSync stage applied this tick, null when no hint host is visible. The suppression and poll stages read its applied mask
        private NativeMenu tickHintHost;
        // Written by the NavHoldAcceleration stage, False while no menu is visible. A pending auto respawn waits while this is True
        private bool navigationHeldThisTick;

        // The order is the pipeline: Each stage reacts to the fields the stages before it wrote
        private void InitTickStages()
        {
            var stages = new List<TickStage>();
            stages.Add(new TickStage(Subsystem.FrameStamp, StampFrame, null));
            stages.Add(new TickStage(Subsystem.StartupNotifications, notifications.FlushStartupNotifications, notifications.HasPendingStartupNotifications));
            stages.Add(new TickStage(Subsystem.SeatWeaponReenable, ReenableSeatWeaponAfterRelease, IsSeatWeaponReenableDue));
            stages.Add(new TickStage(Subsystem.SavedHandlingsParentRestore, RestoreSavedHandlingsParent, () => navigation.RestoreSavedHandlingsParent));
            // Runs right before the pool draws, so a selection change from last frame's input ends or resolves a flash before this frame shows the highlight
            stages.Add(new TickStage(Subsystem.ResultFlash, UpdateResultFlash, () => resultFlashEntries.Count > 0));
            // Menu processing runs before every stage that reacts to this frame's input
            stages.Add(new TickStage(Subsystem.MenuProcessing, ProcessMenus, () => pool != null));
            stages.Add(new TickStage(Subsystem.WidthRecalc, RecalculatePendingWidth, () => pendingWidthRecalc));
            stages.Add(new TickStage(Subsystem.RawMemoryWarnings, LogRawMemoryWarnings, () => RawMemory.HasPendingAccessWarnings));
            stages.Add(new TickStage(Subsystem.LogFlush, logger.FlushLogOnInterval, null));
            stages.Add(new TickStage(Subsystem.IniWatch, CheckOwnedFileEdits, null));
            stages.Add(new TickStage(Subsystem.DescriptorsWatch, CheckDescriptorsEdits, null));
            stages.Add(new TickStage(Subsystem.NotificationSweep, notifications.SweepExpiredNotifications, null));
            // Runs regardless of menu visibility, since the player waits for eviction while driving around
            stages.Add(new TickStage(Subsystem.ModelUnloadPoll, PollModelUnloads, () => pendingModelUnloads.Count > 0 || pendingUnloadRespawns.Count > 0));
            // Same reason, since the plugin answers a creation or deletion request on its own frame whether or not a menu is open
            stages.Add(new TickStage(Subsystem.LiveSubDataPoll, PollPendingLiveSubData, () => pendingLiveRequests.Count > 0 || pausedUndoAllRun != null));
            stages.Add(new TickStage(Subsystem.PlayerResolve, ResolveTickPlayer, null));
            stages.Add(new TickStage(Subsystem.MenuState, ResolveTickMenuState, null));
            // Menu-overlap diagnostic, gated behind IsInfoEnabled() so it never bypasses loggingMode, even at Warn level
            stages.Add(new TickStage(Subsystem.MultipleMenusDiag, CheckMultipleMenusVisible, () => tickAnyMenuVisible && logger.IsInfoEnabled()));
            stages.Add(new TickStage(Subsystem.CheatString, OpenMenuOnCheatString, () => pool != null && !tickAnyMenuVisible && !string.IsNullOrEmpty(enableCheatString)));
            // Current keeps tracking the seated vehicle every tick regardless of which tree has visual focus
            // Menu processing ran earlier this tick, so a handler on the tick a new seat is detected writes the vehicle its rows still show
            // The field sync below resyncs the rows later in the same tick
            stages.Add(new TickStage(Subsystem.SeatTracking, TrackCurrentSeatedVehicle, null));
            stages.Add(new TickStage(Subsystem.VehicleResolve, ResolveTickVehicle, null));
            stages.Add(new TickStage(Subsystem.DeletedTargetBackOut, HandleDeletedEditTarget, () => pool != null && currentContext.TargetVehicle != null && !currentContext.TargetVehicle.Exists()));
            stages.Add(new TickStage(Subsystem.FieldSync, SyncCurrentFieldValues, () => tickAnyMenuVisible && currentContext.TickVehicle != currentContext.LastSyncedVehicle));
            stages.Add(new TickStage(Subsystem.MenuRestore, RestoreMenuOnFirstTick, () => pendingMenuRestoreMenu.Position >= 0));
            stages.Add(new TickStage(Subsystem.SeatMerge, ShowSeatMergeTarget, () => pendingSeatMergeTargetMenu != null));
            stages.Add(new TickStage(Subsystem.WidthAnimation, AnimateMenuWidth, () => tickAnyMenuVisible));
            stages.Add(new TickStage(Subsystem.HintSync, SyncHostHints, () => tickAnyMenuVisible));
            stages.Add(new TickStage(Subsystem.HotkeySuppression, SuppressHotkeyControls, () => tickAnyMenuVisible));
            stages.Add(new TickStage(Subsystem.GlobalHotkeyPoll, PollGlobalHotkeys, () => tickAnyMenuVisible));
            // savedHandlingsMenu's width animates per content, so previewMenu's offset follows it every tick, not just on refresh
            stages.Add(new TickStage(Subsystem.PreviewReposition, RepositionPreviewMenu, () => tickAnyMenuVisible && previewMenu != null && (previewMenu.Visible || IsPreviewCategoryMenuVisible())));
            // Preview's flag checkboxes are permanently Enabled = false, so Selective Copy on them cannot go through the API's own Activated
            // It is polled directly here instead, scoped internally to the moment a flag category submenu is the visible, focused Preview menu
            stages.Add(new TickStage(Subsystem.SelectiveCopyFlagPoll, CheckSelectiveCopyFlagInput, () => navigation.PreviewFocused));
            // An INI change counts too, since the Autosaves row follows the Autosave setting
            stages.Add(new TickStage(Subsystem.SavedHandlingFileCheck, CheckSavedHandlingFileExists, () => savedHandlingsMenu != null && savedHandlingsMenu.Visible && (savedHandlingsChangedThisTick || iniChangedThisTick)));
            stages.Add(new TickStage(Subsystem.HandlingDeleteFinish, FinishHandlingDelete, () => pendingHandlingDelete != null && pendingHandlingDelete.Removal.IsCompleted));
            stages.Add(new TickStage(Subsystem.ArmedFlash, UpdateArmedFlash, () => armedItem != null));
            // Loads the ModelMetaData file once it appears, when it was missing at the first name lookup
            stages.Add(new TickStage(Subsystem.ModelMetaDataRetry, () => { if (modelNames.RetryMissingModelMetaData()) DropVehicleModelNameIndex(); }, () => modelNames.IsModelMetaDataMissing() && modelMetaDataChangedThisTick));
            // Writes every handling changed since the last tick. A held cycle only pushes on release, so a hold writes once
            stages.Add(new TickStage(Subsystem.Autosave, FlushPendingAutosaves, () => pendingAutosaveHandlings.Count > 0));
            // No-ops immediately unless an Action Modifier capture is armed (see PendingKeyCapture, MenuSettings.cs)
            stages.Add(new TickStage(Subsystem.KeyCapturePoll, PollActionModifierCapture, null));
            // One group of stages per tree. An extra editor first tracks its target and syncs its fields
            // An unmounted editor reads a null TickVehicle after tracking, and a dead vehicle unmounts it there, independent of Current's own deleted-target check
            for (int i = 0; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (context != currentContext)
                {
                    stages.Add(new TickStage(Subsystem.AdditionalEditorTracking, () => TrackAdditionalEditor(context), null));
                    stages.Add(new TickStage(Subsystem.FieldSync, () => SyncAdditionalEditorFields(context), () => tickAnyMenuVisible && context.TickVehicle != null && context.TickVehicle != context.LastSyncedVehicle));
                }
                stages.Add(new TickStage(Subsystem.HandlingResolve, () => ResolveTreeHandling(context), null));
                // Keeps drawing after the editor closes until the elements finish building back out
                stages.Add(new TickStage(Subsystem.HandlingVisualization, () => DrawTreeVisualization(context), () => showHandlingVisualization != HandlingVisualizationElements.Disabled &&
                    context.TickHandling != null && context.TickHandling.IsValid && (context.VisibleMenuThisTick != null || visualizer.HasBuildProgress(context.Index))));
                stages.Add(new TickStage(Subsystem.DriftReassertion, () => ReassertDriftedFields(context), () => reassertDriftedFields));
            }
            // Runs after every tree's TickHandling is current, so a menu marked dirty earlier this tick never resolves a stale HandlingData from before the change
            stages.Add(new TickStage(Subsystem.WidthDirtyDrain, DrainDirtyWidthMenus, () => tickAnyMenuVisible));
            stages.Add(new TickStage(Subsystem.NavHoldAcceleration, UpdateNavigationHold, null));
            // Runs after the cycle batches flush above, so a held cycle's pending spawn refresh, and the gear-count respawn it can request, resolve first
            stages.Add(new TickStage(Subsystem.AutoRespawn, FlushPendingAutoRespawn, () => pendingAutoRespawnVehicle != null && !navigationHeldThisTick && Game.GameTime >= pendingAutoRespawnDueTime));
            // mainMenu is deliberately hidden for the whole duration of select mode, so its Visible state is not a signal to check here
            stages.Add(new TickStage(Subsystem.VehicleSelectionHover, UpdateVehicleSelection, () => pool != null && selectingVehicle));
            // Last, so its frame budget comes after all of this tick's own work. A mount earlier in the tick already finished its tree through EnsureEditorBuilt
            stages.Add(new TickStage(Subsystem.StartupWarmUp, RunStartupWarmUp, () => startupWarmUpPending));
            tickStages = stages.ToArray();
        }

        // One job per tick, in order: The extra editor trees, the Search model list, then the precompile
        // A menu opened before a job finishes does that job's remaining work itself, so the warm-up only ever moves cost off the first open
        private void RunStartupWarmUp()
        {
            if (!WarmUpEditorTrees()) return;
            if (!AdvanceSearchResultKeyBuild(WARM_UP_FRAME_BUDGET_MS * System.Diagnostics.Stopwatch.Frequency / 1000)) return;
            if (!AdvanceMethodPrecompile(WARM_UP_PRECOMPILE_FRAME_BUDGET_MS * System.Diagnostics.Stopwatch.Frequency / 1000)) return;
            startupWarmUpPending = false;
        }

        // The runtime compiles a method on its first call, which made every menu's first open after a load hitch for tens of milliseconds
        // The precompile walks the script's own assembly and the menu library's, one assembly's type list per tick, then one type's methods at a time
        // The API's assembly is left out, since it holds thousands of methods the script never calls
        private System.Reflection.Assembly[] precompileAssemblies;
        private int precompileAssemblyIndex;
        private Type[] precompileTypes;
        private int precompileTypeIndex;
        private List<System.Reflection.MethodBase> precompileMethods = new List<System.Reflection.MethodBase>();
        private int precompileMethodIndex;
        private int precompilePrepared;
        private int precompileSkipped;
        private int precompileFailed;
        private double precompileMilliseconds;
        private int precompileTicks;

        // Prepares methods until budgetTicks passes, at least one step per call. Returns True once both assemblies are done
        private bool AdvanceMethodPrecompile(long budgetTicks)
        {
            if (precompileAssemblies == null) precompileAssemblies = new[] { typeof(RageHandlingEditor).Assembly, typeof(NativeMenu).Assembly };
            long callStart = System.Diagnostics.Stopwatch.GetTimestamp();
            precompileTicks++;
            try
            {
                while (true)
                {
                    if (precompileMethodIndex < precompileMethods.Count) PrepareMethodForFirstCall(precompileMethods[precompileMethodIndex++]);
                    else if (precompileTypes != null && precompileTypeIndex < precompileTypes.Length) ListPrecompileMethods(precompileTypes[precompileTypeIndex++]);
                    else if (precompileAssemblyIndex < precompileAssemblies.Length)
                    {
                        precompileTypes = LoadPrecompileTypes(precompileAssemblies[precompileAssemblyIndex++]);
                        precompileTypeIndex = 0;
                        return false;
                    }
                    else
                    {
                        logger.Log(Dbg.Level.Info, Dbg.PrecompileDone(precompilePrepared, precompileSkipped, precompileFailed, precompileMilliseconds + MillisecondsSince(callStart), precompileTicks));
                        return true;
                    }
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - callStart >= budgetTicks) return false;
                }
            }
            finally { precompileMilliseconds += MillisecondsSince(callStart); }
        }

        // A type that fails to load drops out of the list, and every type that did load is still walked
        private static Type[] LoadPrecompileTypes(System.Reflection.Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex) { return Array.FindAll(ex.Types, type => type != null); }
        }

        // Replaces the work list with the type's own methods and instance constructors the runtime can compile ahead
        // A generic type or method, an abstract method, or one implemented outside managed code has nothing to compile until it is called, so it counts as skipped
        private void ListPrecompileMethods(Type type)
        {
            precompileMethods.Clear();
            precompileMethodIndex = 0;
            System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            var members = new List<System.Reflection.MethodBase>(type.GetMethods(declared));
            members.AddRange(type.GetConstructors(declared & ~System.Reflection.BindingFlags.Static));
            foreach (System.Reflection.MethodBase member in members)
            {
                bool notManaged = (member.MethodImplementationFlags & (System.Reflection.MethodImplAttributes.InternalCall | System.Reflection.MethodImplAttributes.Runtime)) != 0 || (member.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0;
                if (type.ContainsGenericParameters || member.ContainsGenericParameters || member.IsAbstract || notManaged) precompileSkipped++;
                else precompileMethods.Add(member);
            }
        }

        // A failure is counted and the walk goes on, and only the first one is logged with its reason
        private void PrepareMethodForFirstCall(System.Reflection.MethodBase method)
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                precompilePrepared++;
            }
            catch (Exception ex)
            {
                precompileFailed++;
                if (precompileFailed == 1) logger.Log(Dbg.Level.Warn, Dbg.PrecompileMethodFailed(method.DeclaringType == null ? method.Name : method.DeclaringType.FullName + "." + method.Name, ex.GetType().Name, ex.Message));
            }
        }

        private static double MillisecondsSince(long startTimestamp) { return (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        // Runs every stage in order, each behind its own exception boundary
        private void RunTickStages()
        {
            for (int i = 0; i < tickStages.Length; i++) SafeCallTick(tickStages[i].Run, tickStages[i].Subsystem);
        }

        // A failed startup leaves a half-built menu tree, so no stage runs
        // The error toast the failure logged was queued before the first tick, and only these two steps post it and let it expire
        private void ServiceNotificationsAfterStartupFailure()
        {
            if (notifications.HasPendingStartupNotifications()) notifications.FlushStartupNotifications();
            notifications.SweepExpiredNotifications();
        }

        // Read before anything can show a menu this tick. A menu shown before this stage (the toggle key's OnKeyDown) carries the previous frame and draws at once
        private void StampFrame() { FrameGatedMenu.ProcessFrame = Game.FrameCount; }

        // Waits until the Confirm and Cancel buttons are released, since holding one several frames past the JustPressed edge would race a fresh discharge
        // pendingSeatWeaponReenable's own comment (VehicleTargeting.cs) says why a fixed one-tick defer is not enough
        private bool IsSeatWeaponReenableDue()
        {
            return pendingSeatWeaponReenable && !Game.IsControlPressed(GTA.Control.CursorAccept) && !Game.IsControlPressed(GTA.Control.FrontendAccept) && !Game.IsControlPressed(GTA.Control.CursorCancel);
        }

        private void ReenableSeatWeaponAfterRelease()
        {
            pendingSeatWeaponReenable = false;
            ReenableSeatWeaponSuppression();
        }

        // Restored before Process() runs so a fresh savedHandlingsMenu entry this frame never reads a blanked Parent
        private void RestoreSavedHandlingsParent()
        {
            savedHandlingsMenu.Parent = mainMenu;
            navigation.RestoreSavedHandlingsParent = false;
        }

        // The restore before the pass covers a mute made outside it, and the one in finally covers a pass that throws
        private void ProcessMenus()
        {
            RestoreMutedLeftRightSound();
            try { pool.Process(); }
            finally { RestoreMutedLeftRightSound(); }
        }

        // GoLeft/GoRight assigns SelectedItem only after ItemChanged returns, so a recalculation requested inside that handler measures the value one step behind
        // By the time pool.Process() returns for this frame, the assignment has already completed
        private void RecalculatePendingWidth()
        {
            HandlingData handling = pendingWidthRecalcHandling;
            try { RecalculateVisibleMenuWidth(handling); }
            finally
            {
                pendingWidthRecalc = false;
                pendingWidthRecalcHandling = null;
            }
        }

        private void LogRawMemoryWarnings()
        {
            List<string> warnings = RawMemory.TakeAccessWarnings();
            for (int i = 0; i < warnings.Count; i++) logger.Log(Dbg.Level.Warn, Dbg.RawMemoryAccessRejected(warnings[i]));
        }

        // A file whose watcher started waits for its change event, and any other file falls back to the timed poll
        private void CheckOwnedFileEdits()
        {
            int currentGameTime = Game.GameTime;
            bool pollFired = currentGameTime >= ownedFileExternalEditPollDueTime;
            if (pollFired) ownedFileExternalEditPollDueTime = currentGameTime + OWNED_FILE_EXTERNAL_EDIT_POLL_INTERVAL_MS;
            iniChangedThisTick = fileWatcher.IsWatching(OwnedFile.Ini) ? fileWatcher.TakeDueChange(OwnedFile.Ini, currentGameTime) : pollFired;
            savedHandlingsChangedThisTick = fileWatcher.IsWatching(OwnedFile.SavedHandlings) ? fileWatcher.TakeDueChange(OwnedFile.SavedHandlings, currentGameTime) : pollFired;
            modelMetaDataChangedThisTick = fileWatcher.IsWatching(OwnedFile.ModelMetaData) ? fileWatcher.TakeDueChange(OwnedFile.ModelMetaData, currentGameTime) : pollFired;
            if (iniChangedThisTick) RefreshIni();
        }

        private void CheckDescriptorsEdits()
        {
            int currentGameTime = Game.GameTime;
            if (fileWatcher.IsWatching(OwnedFile.Descriptors))
            {
                if (fileWatcher.TakeDueChange(OwnedFile.Descriptors, currentGameTime)) RefreshDescriptors();
                return;
            }
            if (currentGameTime < descriptorsPollDueTime) return;
            descriptorsPollDueTime = currentGameTime + DESCRIPTORS_POLL_INTERVAL_MS;
            RefreshDescriptors();
        }

        private void PollModelUnloads()
        {
            unloadPollTick++;
            if (unloadPollTick < UNLOAD_POLL_INTERVAL) return;
            unloadPollTick = 0;
            PollPendingModelUnloads();
            PollPendingUnloadRespawns();
        }

        // Resolved once per tick. Game.LocalPlayerPed constructs a fresh wrapper on every access
        private void ResolveTickPlayer() { tickResolvedPlayer = Game.LocalPlayerPed; }

        // Resolved once so the visualization stages share one result instead of each walking every tree's menus
        private void ResolveTickMenuState()
        {
            tickAnyMenuVisible = pool != null && pool.AreAnyVisible;
            // Any menu opened some other way supersedes the remembered one, so only a close by the toggle key is ever restored
            // With no menu open, a pause block left by a menu closed before Escape's release ends here, since SuppressHotkeyControls only runs while a menu is visible
            if (tickAnyMenuVisible)
            {
                navigation.RememberedMenu = null;
                navigation.RememberedShortcutReturn = null;
                navigation.RememberedPreview = null;
            }
            else navigation.EscapeBlock = EscapeBlockPhase.None;
            for (int i = 0; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                // With no menu open no tree can be visible, so the walk over every tree's menus is skipped while driving
                context.VisibleMenuThisTick = tickAnyMenuVisible ? ResolveVisibleTreeMenu(context) : null;
            }
        }

        private void OpenMenuOnCheatString()
        {
            if (!WasEnableCheatStringJustEntered()) return;
            // Select mode hides mainMenu deliberately, so no menu reads visible throughout it either
            // Without this check the cheat string would reopen mainMenu directly, leaving selectingVehicle stuck True while mainMenu is also visible
            if (selectingVehicle) CancelVehicleSelection();
            else OpenFromEntryPoint();
        }

        private void TrackCurrentSeatedVehicle() { TrackSeatedVehicle(tickResolvedPlayer); }
        private void ResolveTickVehicle() { currentContext.TickVehicle = currentContext.TargetVehicle; }

        // The scoped target no longer exists: With a handling menu on screen the tree closes and mainMenu shows, otherwise only the target and its rows reset
        // The tick's vehicle reads null afterward either way, so no later stage treats the dead target as the vehicle being edited
        private void HandleDeletedEditTarget()
        {
            try
            {
                if (ResolveVisibleTreeMenu(currentContext) != null) BackOutDeletedTarget();
                else ResetDeletedTarget();
            }
            finally { currentContext.TickVehicle = null; }
        }

        private void BackOutDeletedTarget()
        {
            LeaveTree(currentContext, mainMenu, false);
            currentContext.TargetVehicle = null;
            SyncFieldValues();
        }

        // No editor menu is on screen, so nothing is hidden or shown
        // The target, the rows, and a remembered editor menu reset so the next open cannot land inside the dead vehicle's tree
        // Saved Handlings reached by the Exports hotkey from that tree returns to the main menu instead, on Back or after a close and reopen
        private void ResetDeletedTarget()
        {
            currentContext.TargetVehicle = null;
            SyncFieldValues();
            if (navigation.RememberedMenu != null && Array.IndexOf(currentContext.AllOwnMenus, navigation.RememberedMenu) >= 0) navigation.RememberedMenu = null;
            LeaveTree(currentContext, mainMenu, false);
        }

        private void SyncCurrentFieldValues() { SyncFieldValues(null, currentContext.TickVehicle); }

        // The menu open when the script last unloaded reopens once, on the first tick. A menu the player opened first, or Select mode, cancels it
        private void RestoreMenuOnFirstTick()
        {
            if (pool != null && !tickAnyMenuVisible && !selectingVehicle) RestoreMenuAfterReload();
            else ClearPendingMenuRestore();
        }

        // Deferred until here so Current's own item lists (just resynced earlier this tick, if the vehicle changed) already reflect the new vehicle's real counts
        // That way the transplanted SelectedIndex is assigned against lists whose category, AdvancedData, and weapon slot counts match
        private void ShowSeatMergeTarget()
        {
            NativeMenu mergeTarget = pendingSeatMergeTargetMenu;
            int mergeIndex = pendingSeatMergeSelectedIndex;
            pendingSeatMergeTargetMenu = null;
            ShowMenu(mergeTarget);
            if (mergeTarget.Items.Count > 0) mergeTarget.SelectedIndex = Math.Min(mergeIndex, mergeTarget.Items.Count - 1);
        }

        // The handling is cleared first, so a throw while resolving leaves the stages after this one with no handling instead of the previous vehicle's
        private void ResolveTreeHandling(EditorContext context)
        {
            context.TickHandling = null;
            context.TickHandling = ResolveCachedHandling(context.TickVehicle, ref context.HandlingCacheHandle, ref context.HandlingCache);
        }

        private void DrawTreeVisualization(EditorContext context) { visualizer.DrawHandlingVisualization(context.Index, context.TickVehicle, context.TickHandling, ResolveShownVisualizationElements(context)); }

        // One row per category-gated group: The elements that draw only while the visible menu is that category's live menu or sits below it
        private struct VisualizationCategoryGate
        {
            public HandlingVisualizationElements Elements;
            // The elements stay while any one of these category menus is on the visible path
            public string[] Categories;
        }

        // Water Samples draws on boats and seaplanes, so either category's menu keeps it
        private static readonly VisualizationCategoryGate[] VisualizationCategoryGates =
        {
            new VisualizationCategoryGate { Elements = VISUALIZATION_BOAT_ELEMENTS & ~HandlingVisualizationElements.WaterSamples, Categories = new[] { CATEGORY_BOAT } },
            new VisualizationCategoryGate { Elements = HandlingVisualizationElements.WaterSamples, Categories = new[] { CATEGORY_BOAT, CATEGORY_SEA_PLANE } },
        };

        // None while no menu of the tree is visible, so every element builds out
        // Otherwise the setting's elements, minus each gated element whose category menu is not on the visible path
        // With Isolated Drawing on, only Bounding Box, Wheel Bounds, and the elements the highlighted row shapes stay
        // The center of mass and gravity also stay while they diverge, since a reset center of gravity is something the player needs to see wherever they are
        private HandlingVisualizationElements ResolveShownVisualizationElements(EditorContext context)
        {
            NativeMenu visibleMenu = context.VisibleMenuThisTick;
            if (visibleMenu == null) return HandlingVisualizationElements.Disabled;
            HandlingVisualizationElements shown = showHandlingVisualization;
            for (int i = 0; i < VisualizationCategoryGates.Length; i++)
            {
                if ((shown & VisualizationCategoryGates[i].Elements) != 0 && !IsAnyCategoryMenuOnVisiblePath(context, visibleMenu, VisualizationCategoryGates[i].Categories)) shown &= ~VisualizationCategoryGates[i].Elements;
            }
            if (!isolatedDrawing) return shown;
            HandlingVisualizationElements highlighted = HandlingVisualizationElements.Disabled;
            NativeItem selected = visibleMenu.SelectedItem;
            if (selected != null) visualizedRowElements.TryGetValue(selected, out highlighted);
            HandlingVisualizationElements isolated = HandlingVisualizationElements.Bounding | HandlingVisualizationElements.WheelBounds | highlighted;
            if (AreCentersDiverged(context)) isolated |= HandlingVisualizationElements.COM | HandlingVisualizationElements.CenterOfGravity;
            return shown & isolated;
        }

        // The same match test the visualizer uses to hide the center of mass axes under the center of gravity's
        private static bool AreCentersDiverged(EditorContext context)
        {
            Vehicle vehicle = context.TickVehicle;
            HandlingData handling = context.TickHandling;
            if (Entity.IsNullOrNotExisting(vehicle) || handling == null || !handling.IsValid) return false;
            return GTA.Math.Vector3.Distance(handling.CenterOfMassOffset, vehicle.CenterOfGravityOffset) > VISUALIZATION_COM_COG_MATCH_EPSILON;
        }

        private bool IsAnyCategoryMenuOnVisiblePath(EditorContext context, NativeMenu visibleMenu, string[] categoryNames)
        {
            for (int i = 0; i < categoryNames.Length; i++)
            {
                if (IsCategoryMenuOnVisiblePath(context, visibleMenu, categoryNames[i])) return true;
            }
            return false;
        }

        // True when the visible menu is the category's live menu, or a menu below it, found by climbing Parent
        private bool IsCategoryMenuOnVisiblePath(EditorContext context, NativeMenu visibleMenu, string categoryName)
        {
            SubHandlingCategory category = FindSubHandlingCategory(context, categoryName);
            if (category == null || category.LiveSubmenu == null) return false;
            NativeMenu level = visibleMenu;
            for (int depth = 0; level != null && depth < MENU_REACHABILITY_MAX_DEPTH; depth++)
            {
                if (level == category.LiveSubmenu.Menu) return true;
                level = level.Parent;
            }
            return false;
        }

        // Clears the tree's vehicle first, so its other stages read it as unmounted unless it is mounted with a vehicle that still exists
        private void TrackAdditionalEditor(EditorContext context)
        {
            context.TickVehicle = null;
            if (context.TargetVehicle == null) return;
            if (Entity.IsNullOrNotExisting(context.TargetVehicle))
            {
                // Leaves the whole tree instead of popping one level, since a vehicle that died 2+ menus deep would otherwise leave its stale fields on screen under the parent
                LeaveTree(context, mainMenu, false);
                UnmountAdditionalEditor(context);
                return;
            }
            context.TickVehicle = context.TargetVehicle;
        }

        // The synced vehicle is recorded first, so a throw during the sync is logged once instead of retrying every tick
        private void SyncAdditionalEditorFields(EditorContext context)
        {
            context.LastSyncedVehicle = context.TickVehicle;
            SyncEditorContext(context);
        }

        private void UpdateNavigationHold()
        {
            if (tickAnyMenuVisible)
            {
                // FrontendLeft/FrontendRight included so held value-field cycling speeds up the same way held menu navigation does
                bool navHeld = Game.IsControlPressed(GTA.Control.FrontendUp) || Game.IsControlPressed(GTA.Control.FrontendDown) ||
                    Game.IsControlPressed(GTA.Control.FrontendLeft) || Game.IsControlPressed(GTA.Control.FrontendRight);
                navigationHeldThisTick = navHeld;
                if (navHeld)
                {
                    // Accumulated frame time, not a tick count, so the threshold doesn't drift with framerate
                    navHoldSeconds += Game.LastFrameTime;
                    if (!navigationSpedUp && navHoldSeconds >= NAV_ACCEL_THRESHOLD_MS / 1000.0f) ApplyFastNavSpeed();
                }
                else
                {
                    navHoldSeconds = 0.0f;
                    if (navigationSpedUp) ResetNavSpeed();
                    FlushAllPendingCycleBatches();
                }
                return;
            }
            navigationHeldThisTick = false;
            if (navigationSpedUp)
            {
                navHoldSeconds = 0.0f;
                ResetNavSpeed();
                FlushAllPendingCycleBatches();
            }
        }

        private void UpdateVehicleSelection()
        {
            // Resolved once and shared by both calls below, since Ped.CurrentVehicle constructs a fresh Vehicle wrapper on every access
            Vehicle selectionHoverPlayerVehicle = !Entity.IsNullOrNotExisting(tickResolvedPlayer) ? tickResolvedPlayer.CurrentVehicle : null;
            UpdateVehicleSelectionHover(selectionHoverPlayerVehicle);
            SuppressSeatMountedWeapon(tickResolvedPlayer, selectionHoverPlayerVehicle);
            DrawCrosshair();
            if (selectModeArmedThisTick) return;
            if (Game.IsControlJustPressed(GTA.Control.CursorAccept) || Game.IsControlJustPressed(GTA.Control.FrontendAccept)) ConfirmVehicleSelection();
            else if (Game.IsControlJustPressed(GTA.Control.CursorCancel)) CancelVehicleSelection();
        }

        // Menu-overlap diagnostic. It logs once per occurrence and resets once the count drops back to at most one
        private bool multipleMenusWarned;
        private void CheckMultipleMenusVisible()
        {
            int visibleCount = 0;
            for (int i = 0; i < allMenus.Length; i++) { if (allMenus[i].Visible) visibleCount++; }
            if (visibleCount > 1)
            {
                if (!multipleMenusWarned)
                {
                    // Only built once the rare multi-visible case is confirmed, so every ordinary single-menu tick skips this string allocation entirely
                    string names = "";
                    for (int i = 0; i < allMenus.Length; i++) { if (allMenus[i].Visible) names += (names.Length > 0 ? ", " : "") + allMenus[i].Name; }
                    logger.Log(Dbg.Level.Warn, Dbg.MultipleMenusVisible(names));
                    multipleMenusWarned = true;
                }
            }
            else multipleMenusWarned = false;
        }

        // Vehicle.HandlingData reads the vehicle's own handling pointer on every read, while this cache refreshes only when the vehicle's handle changes
        private static HandlingData ResolveCachedHandling(Vehicle vehicle, ref int cachedHandle, ref HandlingData cached)
        {
            if (Entity.IsNullOrNotExisting(vehicle))
            {
                cachedHandle = 0;
                cached = null;
                return null;
            }
            if (cached == null || vehicle.Handle != cachedHandle)
            {
                cached = vehicle.HandlingData;
                cachedHandle = vehicle.Handle;
            }
            return cached;
        }

        // A field can be changed back by something outside the script, even while just driving after closing the menu
        // Undo and Redo update AssertedValue through fieldRegistry, so this only fights external resets, never deliberate reverts
        // Only this tree's own bindings are reasserted and reset, so an edit in one tree never reasserts onto another tree's identically-named field
        private void ReassertDriftedFields(EditorContext owner)
        {
            Vehicle vehicle = owner.TickVehicle;
            HandlingData handling = owner.TickHandling;
            // This reset is independent of SyncFieldValues' own LastSyncedVehicle reset, since reassertion runs every tick regardless of menu state
            if (vehicle != owner.LastReassertedVehicle)
            {
                owner.LastReassertedVehicle = vehicle;
                owner.LastColliderState = ColliderState.Unknown;
                ClearAssertingBindings(owner);
            }
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            if (handling == null || !handling.IsValid) return;
            for (int i = 0; i < assertingFloatBindings.Count; i++)
            {
                FloatFieldBinding binding = assertingFloatBindings[i];
                if (binding.Owner != owner) continue;
                if (Math.Abs(binding.Getter(handling) - binding.AssertedValue) > DRIFT_EPSILON) binding.Setter(vehicle, handling, binding.AssertedValue);
            }
            for (int i = 0; i < assertingIntBindings.Count; i++)
            {
                IntFieldBinding binding = assertingIntBindings[i];
                if (binding.Owner != owner) continue;
                if (binding.Getter(handling) != binding.AssertedValue) binding.Setter(vehicle, handling, binding.AssertedValue);
            }
            ReassertCenterOfGravity(owner, vehicle, handling);
        }

        // CenterOfGravityOffset resets once when an unlatched door switches the collider to articulated, and is pushed back only while the collider is rigid again
        // A push onto an articulated body also offsets the parts attached to its root, which shows as a shifted collision box
        // Bikes are skipped, since the game writes their center of gravity every physics update from the handling value plus lean
        // The type read and the push run back to back in one script update, which the physics update never interrupts, so a push always lands on the state just read
        private void ReassertCenterOfGravity(EditorContext owner, Vehicle vehicle, HandlingData handling)
        {
            if (vehicle.Type == VehicleType.Motorcycle || vehicle.Type == VehicleType.Bicycle) return;
            ColliderState state = RawMemory.ReadColliderState(vehicle);
            if (state != owner.LastColliderState)
            {
                owner.LastColliderState = state;
                if (logger.IsInfoEnabled()) logger.Log(Dbg.Level.Info, Dbg.ColliderStateChanged(vehicle.Handle, state.ToString(), vehicle.CenterOfGravityOffset, handling.CenterOfMassOffset));
            }
            if (state != ColliderState.Rigid) return;
            if (GTA.Math.Vector3.Distance(handling.CenterOfMassOffset, vehicle.CenterOfGravityOffset) <= DRIFT_EPSILON) return;
            if (logger.IsInfoEnabled()) logger.Log(Dbg.Level.Info, Dbg.CenterOfGravityReasserted(vehicle.Handle, vehicle.CenterOfGravityOffset, handling.CenterOfMassOffset));
            SyncCenterOfGravityOffset(handling, vehicle);
        }
    }
}
