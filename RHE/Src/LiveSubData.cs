using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The numbers index LiveRequestExports, so the order never changes
        private enum LiveRequestKind
        {
            Creation = 0,
            Deletion = 1,
            AdvancedDataGrowth = 2
        }

        // Who asked for a request decides what its outcome pushes and whether the setting gates it
        // Player is a menu press or a Selective Copy, gated, and pushes the undo entry on success. AppliedHandling is a fresh apply, gated, and pushes nothing
        // History is an undo or redo and UndoAll is the baseline restore of an Undo All
        // Neither is gated or pushes anything, since the history already records the change
        private enum LiveRequestOrigin
        {
            Player,
            AppliedHandling,
            History,
            UndoAll
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SubHandlingRequestFunction(IntPtr handlingAddress, int category);

        // The request, read, and cancel export names of each kind, indexed by LiveRequestKind
        private static readonly string[][] LiveRequestExports =
        {
            new[] { REQUEST_SUBHANDLING_CREATION_EXPORT, READ_SUBHANDLING_CREATION_OUTCOME_EXPORT, CANCEL_SUBHANDLING_CREATION_EXPORT },
            new[] { REQUEST_SUBHANDLING_DELETION_EXPORT, READ_SUBHANDLING_DELETION_OUTCOME_EXPORT, CANCEL_SUBHANDLING_DELETION_EXPORT },
            new[] { REQUEST_ADVANCEDDATA_CAPACITY_EXPORT, READ_ADVANCEDDATA_CAPACITY_OUTCOME_EXPORT, CANCEL_ADVANCEDDATA_CAPACITY_EXPORT }
        };

        // The undo or redo entry a request finishes, with the direction it was applied in
        private sealed class LiveEditReplay
        {
            public readonly LiveCategoryEdit Edit;
            public readonly bool Redo;
            public LiveEditReplay(LiveCategoryEdit edit, bool redo)
            {
                Edit = edit;
                Redo = redo;
            }
        }

        // A request waiting on the plugin, holding the handling and vehicle from the press so a vehicle that dies meanwhile still gets a complete object
        // Address and Selector are the plugin's own keys: A handling address and a category number, or a Car object's address and a capacity
        private class LiveRequest
        {
            public LiveRequestKind Kind;
            public EditorContext Owner;
            public HandlingKey Key;
            public HandlingData Handling;
            public Vehicle Vehicle;
            public string Category;
            public IntPtr Address;
            public int Selector;
            public string ModelName;
            public int ModelHash;
            public int StartGameTime;
            public LiveRequestOrigin Origin;
            public SubHandlingRequestFunction ReadOutcome;
            public SubHandlingRequestFunction CancelRequest;
            // The cancel is asked once, and its answer settles the request
            // Canceled is the timeout, Pending keeps the poll reading, and any other outcome is handled as if it arrived in time
            public bool CancelSent;
            // A deletion holds the element captured before the request, so an undo can restore it
            public XElement Captured;
            // A growth holds the entries to write from StartIndex on, once the capacity holds them
            public int StartIndex;
            public List<AdvancedDataEntry> Entries;
            // The AdvancedData block of the element a created Car carried, kept until the growth that writes its entries lands
            public XElement SeedAdvancedData;
            // True once the game's change is in the script's records, so a later failure never records it twice
            public bool Recorded;
            // Null for every request no history entry waits on
            public LiveEditReplay Replay;
            // The Undo All this request belongs to, so its outcome lands in that run
            public UndoAllRun Run;
        }

        private readonly List<LiveRequest> pendingLiveRequests = new List<LiveRequest>();

        private static bool ResolveSubHandlingRequests(LiveRequestKind kind, out SubHandlingRequestFunction request, out SubHandlingRequestFunction readOutcome, out SubHandlingRequestFunction cancelRequest, out string unavailableReason)
        {
            request = null;
            readOutcome = null;
            cancelRequest = null;
            unavailableReason = null;
            Delegate[] functions;
            string[] exports = LiveRequestExports[(int)kind];
            PluginBridge.BindResult result = PluginBridge.BindExports(RAGE_MEMORY_BRIDGE_MODULE_NAME, exports, typeof(SubHandlingRequestFunction), out functions);
            if (result != PluginBridge.BindResult.Bound)
            {
                unavailableReason = PluginBridge.DescribeFailure(result, RAGE_MEMORY_BRIDGE_PLUGIN_NAME, exports[0]);
                return false;
            }
            request = (SubHandlingRequestFunction)functions[0];
            readOutcome = (SubHandlingRequestFunction)functions[1];
            cancelRequest = (SubHandlingRequestFunction)functions[2];
            return true;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SubHandlingSetupStateFunction();

        // Only a final answer is kept, since the plugin loads with the game and never appears or leaves afterward
        private static bool? livePluginAvailable;
        private static SubHandlingSetupStateFunction readSetupState;

        private static bool BindSetupState()
        {
            if (readSetupState != null) return true;
            Delegate[] functions;
            if (PluginBridge.BindExports(RAGE_MEMORY_BRIDGE_MODULE_NAME, new[] { READ_SUBHANDLING_SETUP_STATE_EXPORT }, typeof(SubHandlingSetupStateFunction), out functions) != PluginBridge.BindResult.Bound) return false;
            readSetupState = (SubHandlingSetupStateFunction)functions[0];
            return true;
        }

        // Every live export must resolve, so a plugin built before an export existed reads as missing instead of failing a request later
        // A plugin still in its setup reads available, since a request made before its first pass waits for it, and a setup that failed reads unavailable for good
        private bool IsLivePluginAvailable()
        {
            if (livePluginAvailable.HasValue) return livePluginAvailable.Value;
            bool available = true;
            for (int kind = 0; available && kind < LiveRequestExports.Length; kind++) available = PluginBridge.AreExportsPresent(RAGE_MEMORY_BRIDGE_MODULE_NAME, LiveRequestExports[kind]);
            if (!available || !BindSetupState())
            {
                livePluginAvailable = false;
                return false;
            }
            var setupState = (LivePluginSetupState)readSetupState();
            if (setupState == LivePluginSetupState.Pending) return true;
            livePluginAvailable = setupState == LivePluginSetupState.Ready;
            if (!livePluginAvailable.Value) logger.Log(Dbg.Level.Info, Dbg.LivePluginSetupUnavailable());
            return livePluginAvailable.Value;
        }

        private bool IsLiveBackendAvailable() { return RawMemory.RawOffsetsSupported && IsLivePluginAvailable(); }

        // A name no definition carries is never allowed, since the plugin has no number for it
        private static bool IsLiveCreationAllowed(string category, Vehicle vehicle)
        {
            CategoryDefinition definition = ResolveCategoryDefinition(category);
            if (definition == null || IsPhantomOnlyOnType(definition, vehicle.Type)) return false;
            if (definition.CandidateCheck != null && !definition.CandidateCheck(vehicle)) return false;
            return definition.CandidateTypes == null || ContainsVehicleType(definition.CandidateTypes, vehicle.Type);
        }

        private static bool IsPhantomOnlyOnType(CategoryDefinition definition, VehicleType vehicleType)
        {
            return definition.PhantomOnlyTypes != null && ContainsVehicleType(definition.PhantomOnlyTypes, vehicleType);
        }

        // The menu's descriptions and the requests read the same check, so a placeholder promises live creation only where a press requests it
        private bool IsLiveCreationOffered(string category, Vehicle vehicle)
        {
            return IsLiveBackendAvailable() && IsLiveCreationAllowed(category, vehicle);
        }

        private bool IsLiveAdvancedDataGrowthOffered(HandlingData handling)
        {
            return IsLiveBackendAvailable() && handling != null && handling.IsValid && handling.CarHandlingData != null;
        }

        private LiveRequest FindPendingLiveRequest(HandlingKey key, LiveRequestKind kind, string category)
        {
            for (int i = 0; i < pendingLiveRequests.Count; i++)
            {
                LiveRequest request = pendingLiveRequests[i];
                if (request.Key.Equals(key) && request.Kind == kind && request.Category == category) return request;
            }
            return null;
        }

        // The plugin keys a growth on the Car object's address and the capacity
        // FindPendingLiveRequest cannot tell 2 capacities apart, since every growth shares one category name
        private LiveRequest FindPendingLiveGrowth(IntPtr carAddress, int capacity)
        {
            for (int i = 0; i < pendingLiveRequests.Count; i++)
            {
                LiveRequest request = pendingLiveRequests[i];
                if (request.Kind == LiveRequestKind.AdvancedDataGrowth && request.Address == carAddress && request.Selector == capacity) return request;
            }
            return null;
        }

        // A request of any kind on the handling that the plugin has not answered yet
        // Undo, Redo, and Undo All wait on it, since the entry a request is about to push must land before the next press acts
        private int CountPendingLiveRequests(HandlingKey key)
        {
            int count = 0;
            for (int i = 0; i < pendingLiveRequests.Count; i++)
            {
                if (pendingLiveRequests[i].Key.Equals(key)) count++;
            }
            return count;
        }

        private bool HasPendingLiveRequest(HandlingKey key) { return CountPendingLiveRequests(key) > 0; }

        private LiveRequest BuildLiveRequest(LiveRequestKind kind, EditorContext context, HandlingKey key, HandlingData handling, Vehicle vehicle, string category, IntPtr address, int selector, LiveRequestOrigin origin)
        {
            bool vehicleKnown = !Entity.IsNullOrNotExisting(vehicle);
            return new LiveRequest
            {
                Kind = kind,
                Owner = context,
                Key = key,
                Handling = handling,
                Vehicle = vehicle,
                Category = category,
                Address = address,
                Selector = selector,
                ModelName = vehicleKnown ? modelNames.ResolveModelName(vehicle) : "Unknown vehicle",
                ModelHash = vehicleKnown ? vehicle.Model.Hash : 0,
                Origin = origin,
                Run = origin == LiveRequestOrigin.UndoAll ? undoAllRunInProgress : null
            };
        }

        // Returns False with the reason when the plugin cannot take the request, and the caller then keeps what it always did
        private bool StartLiveRequest(LiveRequest request, out string unavailableReason)
        {
            SubHandlingRequestFunction requestFunction;
            if (!ResolveSubHandlingRequests(request.Kind, out requestFunction, out request.ReadOutcome, out request.CancelRequest, out unavailableReason))
            {
                logger.Log(Dbg.Level.Info, Dbg.LiveSubDataUnavailable(request.Category, unavailableReason));
                return false;
            }
            if ((LiveSubDataOutcome)requestFunction(request.Address, request.Selector) == LiveSubDataOutcome.Unavailable)
            {
                unavailableReason = "Plugin refused the request";
                logger.Log(Dbg.Level.Info, Dbg.LiveSubDataUnavailable(request.Category, unavailableReason));
                return false;
            }
            request.StartGameTime = Game.GameTime;
            pendingLiveRequests.Add(request);
            if (request.Kind == LiveRequestKind.Creation) logger.Log(Dbg.Level.Info, Dbg.LiveSubDataRequested(request.ModelName, request.ModelHash, request.Category));
            else if (request.Kind == LiveRequestKind.Deletion) logger.Log(Dbg.Level.Info, Dbg.LiveSubDataDeletionRequested(request.ModelName, request.ModelHash, request.Category));
            else logger.Log(Dbg.Level.Info, Dbg.LiveAdvancedDataRequested(request.ModelName, request.ModelHash, request.Selector));
            return true;
        }

        // The reason a failure toast names, one plain phrase per outcome. The raw outcome name goes to the log only
        private static string DescribeLiveFailure(LiveSubDataOutcome outcome)
        {
            switch (outcome)
            {
                case LiveSubDataOutcome.PoolFull: return REASON_POOL_FULL;
                case LiveSubDataOutcome.Unavailable: return REASON_UNAVAILABLE;
                case LiveSubDataOutcome.NotPresent: return REASON_NOT_PRESENT;
                case LiveSubDataOutcome.Pending:
                case LiveSubDataOutcome.Canceled: return REASON_NO_ANSWER;
                default: return REASON_FAULTED;
            }
        }

        private bool BeginLiveCategoryCreation(EditorContext context, string category, Vehicle vehicle, LiveRequestOrigin origin, LiveEditReplay replay = null)
        {
            string refusal;
            return BeginLiveCategoryCreation(context, category, vehicle, origin, replay, out refusal);
        }

        // Returns False when live creation does not apply, and the caller then creates the export-only category as it always did
        // Returns True once a request is queued or already pending, so a second press while one waits does nothing
        // The request is for the vehicle's own handling, and a replay finishes with the request, so a failure moves its entry back to the stack it came from
        // refusal names the other spawned vehicle that rules a live creation out, and is null for every other False
        private bool BeginLiveCategoryCreation(EditorContext context, string category, Vehicle vehicle, LiveRequestOrigin origin, LiveEditReplay replay, out string refusal)
        {
            refusal = null;
            CategoryDefinition definition = ResolveCategoryDefinition(category);
            if (definition == null || !IsLiveCreationOffered(category, vehicle)) return false;
            HandlingData handling = vehicle.HandlingData;
            HandlingKey key = ResolveHandlingKey(handling);
            if (!key.IsValid) return false;
            if (FindPendingLiveRequest(key, LiveRequestKind.Creation, category) != null) return true;
            refusal = ResolveHandlingRefusal(key, definition, false);
            if (refusal != null)
            {
                logger.Log(Dbg.Level.Info, Dbg.LiveSubDataUnavailable(category, refusal));
                return false;
            }
            LiveRequest request = BuildLiveRequest(LiveRequestKind.Creation, context, key, handling, vehicle, category, key.Address, (int)definition.PluginCategory, origin);
            request.Replay = replay;
            string unavailableReason;
            return StartLiveRequest(request, out unavailableReason);
        }

        // Runs only while a request is pending, whether or not a menu is visible, since the plugin answers on its own frame
        // Past the timeout the plugin is asked once to cancel, and what it answers settles the request
        private void PollPendingLiveSubData()
        {
            for (int i = pendingLiveRequests.Count - 1; i >= 0; i--)
            {
                LiveRequest request = pendingLiveRequests[i];
                var outcome = (LiveSubDataOutcome)request.ReadOutcome(request.Address, request.Selector);
                int elapsed = Game.GameTime - request.StartGameTime;
                bool timedOut = false;
                if (outcome == LiveSubDataOutcome.Pending && elapsed > LIVE_SUBDATA_REQUEST_TIMEOUT_MS && !request.CancelSent)
                {
                    request.CancelSent = true;
                    outcome = (LiveSubDataOutcome)request.CancelRequest(request.Address, request.Selector);
                    timedOut = outcome == LiveSubDataOutcome.Canceled;
                }
                if (outcome == LiveSubDataOutcome.Pending) continue;
                pendingLiveRequests.RemoveAt(i);
                string outcomeName = timedOut ? "TimedOut" : outcome.ToString();
                if (request.Kind == LiveRequestKind.Creation) logger.Log(Dbg.Level.Info, Dbg.LiveSubDataOutcome(request.ModelName, request.ModelHash, request.Category, outcomeName, elapsed));
                else if (request.Kind == LiveRequestKind.Deletion) logger.Log(Dbg.Level.Info, Dbg.LiveSubDataDeletionOutcome(request.ModelName, request.ModelHash, request.Category, outcomeName, elapsed));
                else logger.Log(Dbg.Level.Info, Dbg.LiveAdvancedDataOutcome(request.ModelName, request.ModelHash, request.Selector, outcomeName, elapsed));
                FinishLiveRequestGuarded(request, outcome);
            }
            if (pausedUndoAllRun != null && !HasPendingLiveRequest(pausedUndoAllRun.Key)) ResumeUndoAll();
        }

        // The request is already off the list, so a throw here must not stop the requests still waiting or leave the game's change unrecorded
        private void FinishLiveRequestGuarded(LiveRequest request, LiveSubDataOutcome outcome)
        {
            try
            {
                FinishLiveRequest(request, outcome);
            }
            catch (Exception ex)
            {
                logger.Log(Dbg.Level.Error, Dbg.LiveRequestFinishFailed(request.ModelName, request.ModelHash, request.Category, ex.GetType().Name, ex.Message));
                logger.Log(Dbg.Level.Error, Dbg.UnhandledExceptionStack(ex.StackTrace), false);
                try
                {
                    SettleFailedLiveFinish(request, outcome);
                }
                catch (Exception settleException)
                {
                    logger.Log(Dbg.Level.Error, Dbg.LiveRequestFinishFailed(request.ModelName, request.ModelHash, request.Category, settleException.GetType().Name, settleException.Message));
                }
            }
        }

        // A change the plugin made is recorded even when the finish threw before it got that far, and the tree is refreshed over whatever state the game holds
        private void SettleFailedLiveFinish(LiveRequest request, LiveSubDataOutcome outcome)
        {
            if (!request.Recorded)
            {
                if (request.Kind == LiveRequestKind.Creation && outcome == LiveSubDataOutcome.Created) RecordLiveCategoryCreated(request);
                else if (request.Kind == LiveRequestKind.Deletion && outcome == LiveSubDataOutcome.Deleted) RecordLiveCategoryDeleted(request);
            }
            AddUndoAllFailure(request.Run, request.Category + " request", REASON_FINISH_FAILED);
            RefreshForHandling(request.Key);
        }

        private void FinishLiveRequest(LiveRequest request, LiveSubDataOutcome outcome)
        {
            if (request.Kind == LiveRequestKind.Creation)
            {
                if (outcome == LiveSubDataOutcome.Created) FinishLiveCategoryCreation(request);
                else if (outcome == LiveSubDataOutcome.AlreadyPresent && IsCategoryPresent(request.Handling, request.Category))
                {
                    // The category exists with its own values, so an element stored for this creation must not seed a later one
                    RemoveStoredCategoryElement(request.Key, request.Category);
                    RefreshForHandling(request.Key);
                }
                else FailLiveCategoryCreation(request, DescribeLiveFailure(outcome));
            }
            else if (request.Kind == LiveRequestKind.Deletion)
            {
                if (outcome == LiveSubDataOutcome.Deleted) FinishLiveCategoryDeletion(request);
                else if (outcome == LiveSubDataOutcome.NotPresent) RefreshForHandling(request.Key);
                else FailLiveCategoryDeletion(request, DescribeLiveFailure(outcome));
            }
            else
            {
                bool grown = outcome == LiveSubDataOutcome.Created || outcome == LiveSubDataOutcome.AlreadyPresent;
                if (grown && FinishAdvancedDataGrowth(request)) return;
                FailAdvancedDataGrowth(request, grown ? REASON_VALUES_NOT_WRITTEN : DescribeLiveFailure(outcome));
            }
        }

        // An Undo All collects every outcome of its requests, and the run reports them together once none is pending
        private static void AddUndoAllFailure(UndoAllRun run, string description, string reason)
        {
            if (run == null) return;
            run.NotReverted++;
            run.Failures.Add(new KeyValuePair<string, string>(description, reason));
        }

        // The entry moves back to the stack it came from, so the press can be tried again
        // An edit pushed meanwhile clears the redo stack and buries the entry, and then it stays where it is
        private void RollBackFailedReplay(LiveRequest request, string reason)
        {
            UndoHistory history;
            if (request.Replay == null || !undoHistories.TryGetValue(request.Key, out history)) return;
            LiveCategoryEdit replayedEdit = request.Replay.Edit;
            bool replayedRedo = request.Replay.Redo;
            List<UndoEntry> wentTo = replayedRedo ? history.UndoStack : history.RedoStack;
            List<UndoEntry> cameFrom = replayedRedo ? history.RedoStack : history.UndoStack;
            if (wentTo.Count == 0 || !ReferenceEquals(wentTo[wentTo.Count - 1].Edit, replayedEdit))
            {
                logger.Log(Dbg.Level.Warn, Dbg.LiveReplayEntryNotMoved(replayedEdit.Description, reason));
                return;
            }
            UndoEntry entry = wentTo[wentTo.Count - 1];
            wentTo.RemoveAt(wentTo.Count - 1);
            cameFrom.Add(entry);
            logger.Log(Dbg.Level.Warn, replayedRedo ? Dbg.RedoEntryFailed(replayedEdit.Description, reason) : Dbg.UndoEntryFailed(replayedEdit.Description, reason));
            notifications.PostNotificationOnce(NOTIFICATION_KEY_UNDO_UNAPPLIED, replayedRedo ? NOTIF_REDO_FAILED : NOTIF_UNDO_FAILED);
        }

        private void RemoveStoredCategoryElement(HandlingKey key, string category)
        {
            Dictionary<string, XElement> categoryMap;
            if (pendingSubDataElements.TryGetValue(key, out categoryMap)) categoryMap.Remove(category);
        }

        // The category exists in the game, so everything that tracked it as export-only or as a coming creation is settled here before any step that can fail
        // The element is used up, so a creation after a later delete starts from defaults and only an undo brings back the deleted values
        // The undo entry is pushed last, so a second run after a throw only repeats it when the push never ran
        private void RecordLiveCategoryCreated(LiveRequest request)
        {
            RemoveStoredCategoryElement(request.Key, request.Category);
            ReleaseAssertionsForHandling(request.Key);
            SetSubDataToggle(request.Key, request.Category, false);
            HashSet<string> adopted;
            if (adoptedPhantomCategories.TryGetValue(request.Key, out adopted)) adopted.Remove(request.Category);
            MarkAutosavePending(request.Key);
            // The captured block stays empty, since the entry's undo captures the category itself immediately before it removes it
            if (request.Origin == LiveRequestOrigin.Player) PushStructuralEdit(new LiveCategoryEdit(StructuralKind.LiveCategoryCreate, request.Category, null) { Handling = request.Key });
            request.Recorded = true;
        }

        // The category stays, holding the game's default values, so the entry and the tree match what the game holds
        private void KeepLiveCategoryWithDefaults(LiveRequest request, string reason)
        {
            logger.Log(Dbg.Level.Warn, Dbg.LiveSubDataDefaultsKept(request.ModelName, request.ModelHash, request.Category, reason));
            if (request.Origin == LiveRequestOrigin.UndoAll) AddUndoAllFailure(request.Run, request.Category + " values", reason);
            else notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(NOTIF_LIVE_CATEGORY_CREATED_DEFAULTS, request.Category, reason));
            RefreshForHandling(request.Key);
        }

        // The element is the category's export-only one when earlier edits made it, so those carry over to the new object
        // The apply uses the held handling, so a vehicle that died since the press still gets a complete object and only skips its respawn
        // The record comes before the apply, since a failed or throwing apply leaves the category in the game all the same
        private void FinishLiveCategoryCreation(LiveRequest request)
        {
            HandlingData handling = request.Handling;
            Vehicle vehicle = Entity.IsNullOrNotExisting(request.Vehicle) ? ResolveLiveVehicleOfHandling(request.Key) : request.Vehicle;
            XElement element = GetOrCreatePhantomElement(request.Key, request.Category);
            RecordLiveCategoryCreated(request);
            var replayFlags = ReplayFlags.None;
            bool worksUpsideDownForced = false;
            if (!handling.IsValid || !handlingMetaXml.ApplyCategoryElement(request.Category, element, handling, vehicle, ResolveHandlingFieldGates(vehicle), ref replayFlags, out worksUpsideDownForced))
            {
                KeepLiveCategoryWithDefaults(request, REASON_VALUES_NOT_WRITTEN);
                return;
            }
            if (request.Category == CATEGORY_WEAPON) ApplyLiveWeaponBaseFields(handling, element);
            if (request.Category == CATEGORY_CAR) GrowAdvancedDataFromElement(request, handling, vehicle, element);
            RefreshForHandling(request.Key);
            bool vehicleAlive = !Entity.IsNullOrNotExisting(vehicle);
            logger.Log(Dbg.Level.Info, Dbg.LiveSubDataApplied(request.ModelName, request.ModelHash, request.Category, vehicleAlive));
            if (worksUpsideDownForced) ReportWorksUpsideDownForced(vehicle);
            CreationEffect creationEffect = ResolveCategoryDefinition(request.Category).CreationEffect;
            bool needsRespawn = creationEffect == CreationEffect.Respawn;
            if (needsRespawn) replayFlags |= ReplayFlags.Respawn;
            bool bakedAtModelLoad = creationEffect == CreationEffect.ModelLoad;
            if (request.Origin == LiveRequestOrigin.UndoAll)
            {
                // The run respawns once at its end, and only a category baked at model load needs the player to act
                if (request.Run != null) request.Run.Flags |= replayFlags;
                if (bakedAtModelLoad) notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(NOTIF_LIVE_CATEGORY_CREATED_UNLOAD, request.Category));
                return;
            }
            if (vehicleAlive) ConsumeReplayRefreshAndRespawn(vehicle, "LiveSubData:" + request.Category, replayFlags);
            if (needsRespawn)
            {
                // With no automatic respawn coming, the player has to be told the new category waits on one
                bool respawnComing = vehicleAlive && autoRespawnMode != AutoRespawnMode.Off;
                notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(respawnComing ? NOTIF_LIVE_CATEGORY_CREATED : NOTIF_LIVE_CATEGORY_CREATED_RESPAWN, request.Category));
                return;
            }
            notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(bakedAtModelLoad ? NOTIF_LIVE_CATEGORY_CREATED_UNLOAD : NOTIF_LIVE_CATEGORY_CREATED, request.Category));
            if (request.Category == CATEGORY_SPECIAL_FLIGHT) notifications.Notify(NOTIFICATION_KEY_LIVE_SUBDATA_HOVER_HINT, NOTIF_LIVE_CATEGORY_CREATED_HOVER_HINT);
        }

        // The game's constructor leaves the weapon mod types unset and the base damage scale outside the category, so both are written here
        // A mod type of 0 is a real mod type, and the game's own schema default for an empty slot is -1
        private void ApplyLiveWeaponBaseFields(HandlingData handling, XElement element)
        {
            float damageScale = WEAPON_DAMAGE_SCALED_TO_VEH_HEALTH_MULT_DEFAULT;
            string damageText = GetValAttr(element, "fWeaponDamageScaledToVehHealthMult");
            if (damageText != null) ParseFiniteFloat(damageText, out damageScale);
            RawMemory.WriteRawFloat(handling.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult, damageScale);
            if (!weaponModTypeSupported) return;
            VehicleWeaponHandlingData weaponData = handling.VehicleWeaponHandlingData;
            VehicleWeaponHash[] hashes = weaponData.WeaponHash;
            VehicleModType[] modTypes = weaponData.WeaponVehicleModType;
            for (int i = 0; i < modTypes.Length && i < hashes.Length; i++)
            {
                if (IsUnsetWeaponHash(hashes[i])) modTypes[i] = VehicleModType.None;
            }
            weaponData.WeaponVehicleModType = modTypes;
        }

        // What a failed creation leaves behind depends on who asked
        // A replayed entry creates nothing and goes back to its stack, since the entry already records the change
        // A player's press creates the export-only category unless it already exists, and the tree must still show the same handling
        // An applied handling, an apply entry's replay, and an Undo All adopt the category from the element they stored
        private void FailLiveCategoryCreation(LiveRequest request, string reason)
        {
            logger.Log(Dbg.Level.Warn, Dbg.LiveSubDataFallback(request.ModelName, request.ModelHash, request.Category, reason));
            if (request.Origin == LiveRequestOrigin.History && request.Replay != null)
            {
                // The restored element was only stored for this creation, so it must not seed a later one
                RemoveStoredCategoryElement(request.Key, request.Category);
                RollBackFailedReplay(request, reason);
                RefreshForHandling(request.Key);
                return;
            }
            if (request.Origin == LiveRequestOrigin.UndoAll) AddUndoAllFailure(request.Run, request.Category + " creation", reason);
            else notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(NOTIF_LIVE_CATEGORY_CREATE_FAILED, request.Category, reason));
            // A category the handling already holds live is never toggled, since the toggle would mark that real category excluded
            bool presentLive = request.Handling.IsValid && IsCategoryPresent(request.Handling, request.Category);
            if (request.Origin != LiveRequestOrigin.Player && !presentLive)
            {
                if (AdoptPhantomFromAppliedXml(request.Key, request.Category, GetOrCreatePhantomElement(request.Key, request.Category), request.Vehicle)) ReportWorksUpsideDownForced(request.Vehicle);
                RefreshForHandling(request.Key);
                return;
            }
            if (!ResolveHandlingKey(request.Owner.TargetVehicle).Equals(request.Key)) return;
            HashSet<string> toggled;
            bool alreadyExportOnly = pendingSubDataToggle.TryGetValue(request.Key, out toggled) && toggled.Contains(request.Category);
            if (presentLive || alreadyExportOnly)
            {
                RefreshForHandling(request.Key);
                return;
            }
            ActivatePhantomCategory(request.Owner, request.Category);
        }

        private static bool IsLiveDeletionRefused(string category, Vehicle vehicle)
        {
            CategoryDefinition definition = ResolveCategoryDefinition(category);
            if (definition == null) return false;
            if (definition.RequiredTypes != null && ContainsVehicleType(definition.RequiredTypes, vehicle.Type)) return true;
            return definition.RequiredModelHashes != null && definition.RequiredModelHashes.Contains(vehicle.Model.Hash);
        }

        // The refusal holds for every origin, since the game reads a required category unguarded whoever asks to remove it
        private bool IsLiveDeletionOffered(string category, Vehicle vehicle)
        {
            return IsLiveCreationOffered(category, vehicle) && !IsLiveDeletionRefused(category, vehicle);
        }

        // Returns False when live deletion does not apply, with reason naming why, and the caller then excludes the category from the save instead
        // Returns True once a request is queued or already pending, so a second press while one waits does nothing
        // A replayed edit takes the element captured here as its block, so the opposite direction restores exactly what this removed
        // The request is for the vehicle's own handling
        private bool BeginLiveCategoryDeletion(EditorContext context, string category, Vehicle vehicle, LiveEditReplay replay, LiveRequestOrigin origin, out string reason)
        {
            reason = "Live deletion not available for this category";
            CategoryDefinition definition = ResolveCategoryDefinition(category);
            if (definition == null || !IsLiveDeletionOffered(category, vehicle))
            {
                if (definition != null && IsLiveDeletionRefused(category, vehicle)) reason = REASON_REQUIRED_CATEGORY;
                return false;
            }
            HandlingData handling = vehicle.HandlingData;
            HandlingKey key = ResolveHandlingKey(handling);
            if (!key.IsValid) return false;
            if (FindPendingLiveRequest(key, LiveRequestKind.Deletion, category) != null) return true;
            if (!IsCategoryPresent(handling, category))
            {
                reason = "Category not present";
                return false;
            }
            string handlingRefusal = ResolveHandlingRefusal(key, definition, true);
            if (handlingRefusal != null)
            {
                reason = handlingRefusal;
                return false;
            }
            XElement captured = handlingMetaXml.BuildCategoryElement(category, handling);
            if (captured == null)
            {
                reason = "Category not readable";
                return false;
            }
            // The base damage scale lives outside the Weapon category, so it rides in the element the way an export-only Weapon element carries it
            if (category == CATEGORY_WEAPON) SetVal(captured, "fWeaponDamageScaledToVehHealthMult", RawMemory.ReadRawFloat(handling.MemoryAddress, HandlingDataLayout.WeaponDamageScaledToVehHealthMult));
            LiveRequest request = BuildLiveRequest(LiveRequestKind.Deletion, context, key, handling, vehicle, category, key.Address, (int)definition.PluginCategory, origin);
            request.Captured = captured;
            request.Replay = replay;
            if (!StartLiveRequest(request, out reason)) return false;
            if (replay != null) replay.Edit.EncodedBlock = EncodeElement(captured);
            reason = null;
            return true;
        }

        // Creation and deletion change the handling entry every model on it shares, so every spawned vehicle on it is judged, once per request and never per tick
        // A model that shares the handling but is not spawned cannot be checked
        // A SeaPlane deletion is refused while any of them is on or near water, since a model keeps the pontoon samples it baked at load
        // A deletion is also refused for a vehicle that needs the category and a creation for one the category is phantom only on, and null means nothing refuses
        private static string ResolveHandlingRefusal(HandlingKey key, CategoryDefinition definition, bool deletion)
        {
            bool checkWater = deletion && definition.Name == CATEGORY_SEA_PLANE;
            bool checkNeeded = deletion && (definition.RequiredTypes != null || definition.RequiredModelHashes != null);
            bool checkPhantomOnly = !deletion && definition.PhantomOnlyTypes != null;
            if (!checkWater && !checkNeeded && !checkPhantomOnly) return null;
            string refusal = null;
            foreach (Vehicle candidate in World.GetAllVehicles())
            {
                if (!ResolveHandlingKey(candidate).Equals(key)) continue;
                if (checkWater && IsVehicleOnOrNearWater(candidate)) return SEA_PLANE_NEAR_WATER_REASON;
                if (checkNeeded && IsLiveDeletionRefused(definition.Name, candidate)) refusal = REASON_SHARED_REQUIRED;
                else if (checkPhantomOnly && IsPhantomOnlyOnType(definition, candidate.Type)) refusal = REASON_SHARED_PHANTOM;
            }
            return refusal;
        }

        // A vehicle on a handling that lost SeaPlane data crashes the game once it touches water
        // A helicopter floating on its pontoons does not report itself in water, so a vehicle whose underside sits just above the water counts too
        private static bool IsVehicleOnOrNearWater(Vehicle vehicle)
        {
            if (vehicle.IsInWater) return true;
            GTA.Math.Vector3 position = vehicle.Position;
            float waterHeight;
            if (!Water.GetWaterLevel(position, out waterHeight)) return false;
            GTA.Math.Vector3 min, max;
            vehicle.Model.GetDimensions(out min, out max);
            return position.Z + min.Z - waterHeight <= SEA_PLANE_NEAR_WATER_HEIGHT;
        }

        // A redo can delete a category the user excluded since, and an absent category's set toggle would read as created for saving, so the toggle is cleared
        // The user's own delete pushes the undo entry only once the plugin confirms, since a request that fails changed nothing
        private void RecordLiveCategoryDeleted(LiveRequest request)
        {
            ReleaseAssertionsForHandling(request.Key);
            SetSubDataToggle(request.Key, request.Category, false);
            MarkAutosavePending(request.Key);
            if (request.Origin == LiveRequestOrigin.Player) PushStructuralEdit(new LiveCategoryEdit(StructuralKind.LiveCategoryDelete, request.Category, EncodeElement(request.Captured)) { Handling = request.Key });
            request.Recorded = true;
        }

        // The vehicle keeps whatever the game built from the category when it spawned, so every deletion asks for a respawn
        private void FinishLiveCategoryDeletion(LiveRequest request)
        {
            RecordLiveCategoryDeleted(request);
            RefreshForHandling(request.Key);
            logger.Log(Dbg.Level.Info, Dbg.LiveSubDataDeleted(request.ModelName, request.ModelHash, request.Category));
            // Only an unload drops the baked pontoon samples, so the warning posts on every origin, Undo All included
            if (request.Category == CATEGORY_SEA_PLANE) notifications.Notify(NOTIFICATION_KEY_SEA_PLANE_UNLOAD_WARNING, NOTIF_SEA_PLANE_DELETED_UNLOAD);
            if (request.Origin == LiveRequestOrigin.UndoAll)
            {
                if (request.Run != null) request.Run.Flags |= ReplayFlags.Respawn;
                return;
            }
            Vehicle vehicle = Entity.IsNullOrNotExisting(request.Vehicle) ? ResolveLiveVehicleOfHandling(request.Key) : request.Vehicle;
            bool vehicleAlive = !Entity.IsNullOrNotExisting(vehicle);
            if (vehicleAlive) ConsumeReplayRefreshAndRespawn(vehicle, "LiveSubData:" + request.Category + ":Deleted", ReplayFlags.Respawn);
            // With no automatic respawn coming, the player has to be told the vehicle still holds the deleted category's effects
            bool respawnComing = vehicleAlive && autoRespawnMode != AutoRespawnMode.Off;
            notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(respawnComing ? NOTIF_LIVE_CATEGORY_DELETED : NOTIF_LIVE_CATEGORY_DELETED_RESPAWN, request.Category));
        }

        private void FailLiveCategoryDeletion(LiveRequest request, string reason)
        {
            logger.Log(Dbg.Level.Warn, Dbg.LiveSubDataDeletionFailed(request.ModelName, request.ModelHash, request.Category, reason));
            if (request.Origin == LiveRequestOrigin.History && request.Replay != null) RollBackFailedReplay(request, reason);
            else if (request.Origin == LiveRequestOrigin.UndoAll) AddUndoAllFailure(request.Run, request.Category + " deletion", reason);
            else notifications.Notify(NOTIFICATION_KEY_PREFIX_LIVE_SUBDATA + request.Category, string.Format(NOTIF_LIVE_CATEGORY_DELETE_FAILED, request.Category, reason));
        }

        // Both directions of a live category edit end here
        // A creation direction restores the captured element as the category's export-only element, so the creation applies it
        // The plugin answers on its own frame, so the entry reports applied once the request is queued, and a failure moves it back
        // A refusal before any request is queued leaves the entry on its own stack when the player can clear its cause, so the next press retries it
        private bool ApplyLiveCategoryEdit(LiveCategoryEdit edit, bool redo, HandlingData handling, out string reason)
        {
            reason = null;
            edit.RefusalRetryable = false;
            Vehicle vehicle = ResolveLiveVehicleOfHandling(edit.Handling);
            EditorContext owner = ResolveEditorContextOfHandling(edit.Handling);
            if (vehicle == null || owner == null || handling == null || !handling.IsValid)
            {
                reason = "handling no longer has a vehicle in an editor";
                return false;
            }
            bool creating = (edit.Kind == StructuralKind.LiveCategoryCreate) == redo;
            if (!creating)
            {
                if (BeginLiveCategoryDeletion(owner, edit.Category, vehicle, new LiveEditReplay(edit, redo), LiveRequestOrigin.History, out reason)) return true;
                edit.RefusalRetryable = IsClearableRefusal(reason);
                return false;
            }
            XElement restored = DecodeElement(edit.EncodedBlock);
            if (restored == null)
            {
                reason = "no captured category";
                return false;
            }
            Dictionary<string, XElement> categoryMap = GetOrAdd(pendingSubDataElements, edit.Handling);
            categoryMap[edit.Category] = restored;
            string creationRefusal;
            if (BeginLiveCategoryCreation(owner, edit.Category, vehicle, LiveRequestOrigin.History, new LiveEditReplay(edit, redo), out creationRefusal)) return true;
            // The element was stored only for this creation, so a refused one must not leave it for a later creation to seed from
            RemoveStoredCategoryElement(edit.Handling, edit.Category);
            reason = creationRefusal ?? "live creation not available";
            edit.RefusalRetryable = IsClearableRefusal(creationRefusal);
            return false;
        }

        // Water and another spawned vehicle on the handling pass once the player moves the vehicle or removes the other one
        // Every other refusal (no plugin, a required category on the vehicle itself) lasts, so its entry moves aside and never pins the stack
        private static bool IsClearableRefusal(string reason)
        {
            return reason == SEA_PLANE_NEAR_WATER_REASON || reason == REASON_SHARED_REQUIRED || reason == REASON_SHARED_PHANTOM;
        }

        // An applied handling asks for each category it carries that the vehicle lacks to be created live, with the applied element as its values
        // The vehicle must be a candidate for the category, by its type and the category's own check, and the request needs a tree editing the handling to own it
        // Without both the caller adopts the category as phantom
        private bool RequestAppliedCategoryCreation(HandlingKey key, HandlingData handling, Vehicle vehicle, string category, XElement appliedElement, XElement handlingItem, LiveRequestOrigin origin)
        {
            if (Entity.IsNullOrNotExisting(vehicle) || !handling.IsValid) return false;
            EditorContext owner = ResolveEditorContextOfHandling(key);
            if (owner == null || !IsLiveCreationOffered(category, vehicle)) return false;
            StoreAppliedCategoryElement(key, category, appliedElement);
            if (category == CATEGORY_WEAPON) SeedAdoptedWeaponDamageField(key, handlingItem);
            return BeginLiveCategoryCreation(owner, category, vehicle, origin);
        }

        // Each category is removed through the live deletion, since the apply's own entry or the Undo All already holds the undo
        // Returns how many could not be removed, which the caller counts as skipped fields. A category that stays still holds the applied values
        // An Undo All lists each kept category with its own reason, since one count cannot say which category stayed or why
        private int RemoveCategoriesCreatedByApply(HandlingData handling, Vehicle vehicle, List<string> categories, LiveRequestOrigin origin)
        {
            int notRemoved = 0;
            EditorContext owner = ResolveEditorContextOfHandling(ResolveHandlingKey(handling)) ?? currentContext;
            foreach (string category in categories)
            {
                string reason = "no vehicle";
                if (!Entity.IsNullOrNotExisting(vehicle) && BeginLiveCategoryDeletion(owner, category, vehicle, null, origin, out reason)) continue;
                notRemoved++;
                logger.Log(Dbg.Level.Warn, Dbg.LiveSubDataAppliedCategoryKept(category, reason));
                if (origin == LiveRequestOrigin.UndoAll) AddUndoAllFailure(undoAllRunInProgress, category + " removal", reason);
            }
            return notRemoved;
        }

        // ===================
        // AdvancedData growth
        // ===================
        // A Car object's AdvancedData array has a fixed capacity, so a longer list needs the plugin to grow it first
        // A growth request holds the entries to write once the capacity holds, which land from StartIndex on and set the count to StartIndex plus their number
        private static int ReadAdvancedDataEntryInt(XElement item, string name)
        {
            XElement child = item.Element(name);
            XAttribute value = child != null ? child.Attribute("value") : null;
            int parsed;
            return value != null && int.TryParse(value.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        // A member the item omits or garbles reads as 0, the same as an entry the apply writes past the old count
        private static AdvancedDataEntry ParseAdvancedDataEntry(XElement item)
        {
            XElement valueChild = item.Element("Value");
            XAttribute valueAttribute = valueChild != null ? valueChild.Attribute("value") : null;
            float parsedValue;
            if (valueAttribute == null || !ParseFiniteFloat(valueAttribute.Value, out parsedValue)) parsedValue = 0.0f;
            return new AdvancedDataEntry { Slot = ReadAdvancedDataEntryInt(item, "Slot"), Index = ReadAdvancedDataEntryInt(item, "Index"), Value = parsedValue };
        }

        private static List<AdvancedDataEntry> ParseAdvancedDataEntries(XElement[] items, int first, int endExclusive)
        {
            var entries = new List<AdvancedDataEntry>();
            for (int i = first; i < endExclusive; i++) entries.Add(ParseAdvancedDataEntry(items[i]));
            return entries;
        }

        // Returns False when live growth does not apply, with reason naming why, and the caller then keeps what it always did
        // Returns True once the request is queued, and the poll writes the entries when the plugin has made the room
        // A growth is refused while a deletion of the same Car category waits, since the plugin would grow an object the deletion is destroying
        // run names the Undo All a follow-up request belongs to, and seed is the block a created Car's element carried, which a failed growth keeps as pending
        private bool BeginAdvancedDataGrowth(EditorContext context, Vehicle vehicle, HandlingData handling, int capacity, LiveRequestOrigin origin, int startIndex, List<AdvancedDataEntry> entries, out string reason, UndoAllRun run = null, XElement seed = null)
        {
            reason = REASON_LIVE_UNAVAILABLE;
            if (!IsLiveAdvancedDataGrowthOffered(handling) || capacity < 1 || capacity > ADVANCEDDATA_COUNT_SANITY_CEILING) return false;
            HandlingKey key = ResolveHandlingKey(handling);
            if (!key.IsValid) return false;
            if (FindPendingLiveRequest(key, LiveRequestKind.Deletion, CATEGORY_CAR) != null)
            {
                reason = REASON_CAR_DELETE_PENDING;
                return false;
            }
            IntPtr carAddress = handling.CarHandlingData.MemoryAddress;
            if (FindPendingLiveGrowth(carAddress, capacity) != null)
            {
                reason = REASON_GROWTH_PENDING;
                return false;
            }
            LiveRequest request = BuildLiveRequest(LiveRequestKind.AdvancedDataGrowth, context, key, handling, vehicle, "AdvancedData", carAddress, capacity, origin);
            request.StartIndex = startIndex;
            request.Entries = entries;
            request.SeedAdvancedData = seed;
            if (run != null) request.Run = run;
            string unavailableReason;
            return StartLiveRequest(request, out unavailableReason);
        }

        // The count is raised before any entry is written, since a write past the old count is refused
        // Entries land from the lower of startIndex and the count the array holds now, since an entry removed since the press leaves nothing to write a gap over
        // A failed write puts the old count back, and writtenFrom is the index the entries landed at
        private bool WriteAdvancedDataEntries(HandlingData handling, int startIndex, List<AdvancedDataEntry> entries, out int writtenFrom)
        {
            int oldCount = RawMemory.GetAdvancedDataCount(handling);
            writtenFrom = Math.Min(startIndex, oldCount);
            int newCount = writtenFrom + entries.Count;
            if (newCount > RawMemory.GetAdvancedDataCapacity(handling)) return false;
            ReleaseAdvancedDataAssertions();
            if (!RawMemory.SetAdvancedDataCount(handling, newCount)) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (RawMemory.WriteAdvancedDataEntry(handling, writtenFrom + i, entries[i])) continue;
                RawMemory.SetAdvancedDataCount(handling, oldCount);
                return false;
            }
            return true;
        }

        // A player's press pushes the entry that undoes it: A request from the first slot creates the block, and any later one adds a slot
        // An apply or a restore pushes nothing, since its own entry already covers the entries it writes
        private bool FinishAdvancedDataGrowth(LiveRequest request)
        {
            HandlingData handling = request.Handling;
            if (!handling.IsValid || handling.CarHandlingData == null || handling.CarHandlingData.MemoryAddress != request.Address) return false;
            int writtenFrom;
            if (!WriteAdvancedDataEntries(handling, request.StartIndex, request.Entries, out writtenFrom)) return false;
            if (request.Origin == LiveRequestOrigin.Player)
            {
                AdvancedDataStore liveStore = AdvancedDataStores[(int)AdvancedDataMenuKind.Live];
                if (writtenFrom == 0) PushStructuralEdit(new AdvancedDataBlockEdit(liveStore, liveStore.BlockCreateKind, null) { Handling = request.Key });
                else PushStructuralEdit(new AdvancedDataEntryEdit(liveStore, liveStore.AddSlotKind, writtenFrom, null) { Handling = request.Key });
            }
            MarkAutosavePending(request.Key);
            RefreshForHandling(request.Key);
            logger.Log(Dbg.Level.Info, Dbg.LiveAdvancedDataApplied(request.ModelName, request.ModelHash, writtenFrom + request.Entries.Count));
            return true;
        }

        // A created Car's element is gone once the category exists, so its AdvancedData entries are kept as the pending export-only block when their growth fails
        // An existing block or live entries win, and the entries are not merged
        private void KeepAdvancedDataSeed(HandlingKey key, HandlingData handling, XElement seed, string modelName, int modelHash)
        {
            if (seed == null) return;
            int entries = ListItemElements(seed).Length;
            if (pendingAdvancedData.ContainsKey(key) || (handling.IsValid && RawMemory.GetAdvancedDataCount(handling) > 0))
            {
                logger.Log(Dbg.Level.Warn, Dbg.LiveAdvancedDataSeedDropped(modelName, modelHash, entries));
                return;
            }
            pendingAdvancedData[key] = seed;
            logger.Log(Dbg.Level.Warn, Dbg.LiveAdvancedDataSeedKept(modelName, modelHash, entries));
            RefreshPendingAdvancedData(key);
        }

        // The press on the placeholder falls back to the pending export-only entry, and every other request only reports why it did not land
        private void FailAdvancedDataGrowth(LiveRequest request, string reason)
        {
            logger.Log(Dbg.Level.Warn, Dbg.LiveAdvancedDataFailed(request.ModelName, request.ModelHash, request.Selector, reason));
            KeepAdvancedDataSeed(request.Key, request.Handling, request.SeedAdvancedData, request.ModelName, request.ModelHash);
            if (request.Origin == LiveRequestOrigin.UndoAll)
            {
                AddUndoAllFailure(request.Run, "AdvancedData growth", reason);
                return;
            }
            bool placeholderPress = request.Origin == LiveRequestOrigin.Player && request.StartIndex == 0;
            notifications.Notify(NOTIFICATION_KEY_LIVE_ADVANCEDDATA, string.Format(placeholderPress ? NOTIF_LIVE_ADVANCEDDATA_CREATE_FAILED : NOTIF_LIVE_ADVANCEDDATA_FAILED, reason));
            if (!placeholderPress || request.Owner == null || Entity.IsNullOrNotExisting(request.Owner.TargetVehicle)) return;
            if (!ResolveHandlingKey(request.Owner.TargetVehicle).Equals(request.Key) || RawMemory.GetAdvancedDataCount(request.Handling) > 0) return;
            ResolveXmlAdvancedDataStore(AdvancedDataMenuKind.Pending).PressCreate(this, request.Owner);
        }

        // A Car created from an element that carries AdvancedData starts with an empty array, so the element's entries need the capacity first
        // It is a follow-up of the creation, so it never needs the setting and pushes nothing, and an Undo All's creation keeps its follow-up in the same run
        private void GrowAdvancedDataFromElement(LiveRequest creation, HandlingData handling, Vehicle vehicle, XElement element)
        {
            XElement advancedData = element.Element(ADVANCED_DATA_TAG);
            if (advancedData == null) return;
            XElement[] items = ListItemElements(advancedData);
            int wanted = Math.Min(items.Length, ADVANCEDDATA_COUNT_SANITY_CEILING);
            if (wanted == 0) return;
            LiveRequestOrigin origin = creation.Origin == LiveRequestOrigin.UndoAll ? LiveRequestOrigin.UndoAll : LiveRequestOrigin.History;
            var seed = new XElement(advancedData);
            string reason;
            if (BeginAdvancedDataGrowth(creation.Owner, vehicle, handling, wanted, origin, 0, ParseAdvancedDataEntries(items, 0, wanted), out reason, creation.Run, seed)) return;
            logger.Log(Dbg.Level.Warn, Dbg.LiveAdvancedDataFailed(creation.ModelName, creation.ModelHash, wanted, reason));
            KeepAdvancedDataSeed(creation.Key, handling, seed, creation.ModelName, creation.ModelHash);
        }

        // The entries past the array's capacity are the ones the apply could not write, and the count they raise becomes the document's own
        // Returns True when the request is queued, which covers the entries the apply would otherwise report as skipped
        private bool RequestAppliedAdvancedDataGrowth(HandlingKey key, HandlingData handling, Vehicle vehicle, XElement[] items, int writtenCount, LiveRequestOrigin origin)
        {
            int wanted = Math.Min(items.Length, ADVANCEDDATA_COUNT_SANITY_CEILING);
            if (wanted <= writtenCount) return false;
            string reason;
            if (BeginAdvancedDataGrowth(ResolveEditorContextOfHandling(key), vehicle, handling, wanted, origin, writtenCount, ParseAdvancedDataEntries(items, writtenCount, wanted), out reason)) return true;
            if (reason == REASON_LIVE_UNAVAILABLE) return false;
            bool vehicleKnown = !Entity.IsNullOrNotExisting(vehicle);
            logger.Log(Dbg.Level.Warn, Dbg.LiveAdvancedDataFailed(vehicleKnown ? modelNames.ResolveModelName(vehicle) : "Unknown vehicle", vehicleKnown ? vehicle.Model.Hash : 0, wanted, reason));
            return false;
        }
    }
}
