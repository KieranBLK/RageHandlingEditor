using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GTA;
using GTA.Native;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        private int unloadPollTick;

        // One entry per model waiting for the game to evict it, keyed by model hash so 2 editors on different models unload independently
        private class PendingModelUnload
        {
            public Model Model;
            public string ModelName;
            public int StartGameTime;
            // The game time the model was first read as unloaded. 0 until then, and reset to 0 if it reads loaded again during the settle
            public int UnloadedGameTime;
            // The plugin's eviction exports, all null while eviction is unavailable for this unload
            public FragmentEvictionFunction RequestEviction;
            public FragmentEvictionFunction ReadEvictionOutcome;
            public FragmentEvictionFunction CancelEviction;
            // FragmentEvicted once the fragment reads removed or already unloaded, EvictionEnded once an outcome stops the attempts without that
            // EvictionOutstanding is True from a request until its outcome is read, so an unload that ends in between cancels it
            public bool EvictionRequested;
            public bool EvictionOutstanding;
            public bool FragmentEvicted;
            public bool EvictionEnded;
            public FragmentEvictionOutcome LastEvictionOutcome;
            // The vehicle the player sat in at the confirm, captured before the delete. Null when the player was not in it, so nothing respawns
            public RespawnSnapshot Respawn;
        }

        private readonly Dictionary<int, PendingModelUnload> pendingModelUnloads = new Dictionary<int, PendingModelUnload>();

        // An unload that ended in time with its fragment evicted, waiting for the fresh model load before the vehicle is created again
        private class PendingUnloadRespawn
        {
            public Model Model;
            public string ModelName;
            public RespawnSnapshot Snapshot;
            public int StartGameTime;
        }

        private readonly List<PendingUnloadRespawn> pendingUnloadRespawns = new List<PendingUnloadRespawn>();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FragmentEvictionFunction(uint modelHash);

        private void TriggerUnloadVehicle()
        {
            ArmOrConfirm(unloadVehicleItem, mainMenu, "UnloadVehicle", UnloadVehicle);
        }

        // The next spawn after eviction reloads the model and applies edits that are only read at model load
        private void UnloadVehicle()
        {
            Vehicle vehicle = activeContext.TargetVehicle;
            if (Entity.IsNullOrNotExisting(vehicle))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_UNLOAD, NOTIF_NO_VEHICLE_UNLOAD);
                return;
            }
            Model model = vehicle.Model;
            if (pendingModelUnloads.ContainsKey(model.Hash)) return;
            string modelName = modelNames.ResolveModelName(vehicle);
            // Only a vehicle the player sits in comes back on its own, and its state has to be read before the delete below
            Ped player = Game.LocalPlayerPed;
            Vehicle playerVehicle = Entity.IsNullOrNotExisting(player) ? null : player.CurrentVehicle;
            RespawnSnapshot respawn = !Entity.IsNullOrNotExisting(playerVehicle) && playerVehicle.Handle == vehicle.Handle ? CaptureRespawnSnapshot(vehicle, player) : null;
            Function.Call(Hash.SET_VEHICLE_MODEL_IS_SUPPRESSED, model.Hash, true);
            Vehicle[] instances = World.GetAllVehicles(model);
            for (int i = 0; i < instances.Length; i++) instances[i].Delete();
            // Releasing the model also drops the hold Respawn Vehicle leaves behind, since World.CreateVehicle requests the model and never releases it
            model.MarkAsNoLongerNeeded();
            if (pendingModelUnloads.Count == 0) SetUnloadSpawningPaused(true);
            FragmentEvictionFunction requestEviction;
            FragmentEvictionFunction readEvictionOutcome;
            FragmentEvictionFunction cancelEviction;
            string unavailableReason;
            bool evictionAvailable = ResolveFragmentEviction(out requestEviction, out readEvictionOutcome, out cancelEviction, out unavailableReason);
            pendingModelUnloads[model.Hash] = new PendingModelUnload { Model = model, ModelName = modelName, StartGameTime = Game.GameTime, RequestEviction = requestEviction, ReadEvictionOutcome = readEvictionOutcome, CancelEviction = cancelEviction, Respawn = respawn };
            logger.Log(Dbg.Level.Info, Dbg.UnloadSuppressed(modelName, model.Hash, instances.Length));
            if (!evictionAvailable) logger.Log(Dbg.Level.Info, Dbg.UnloadEvictionUnavailable(modelName, model.Hash, unavailableReason));
            notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + model.Hash, string.Format(NOTIF_UNLOAD_MARKED, modelName));
            SyncActiveActionEnablement();
        }

        private static void PlayUnloadSuccessSound() { Audio.PlaySoundFrontendAndForget(UNLOAD_SUCCESS_SOUND_NAME, UNLOAD_SUCCESS_SOUND_SET); }

        // An instance appearing before eviction aborts that model's unload, since the new instance keeps the model loaded with its old data
        // The model reading unloaded is not enough: Its fragment, which holds the baked physics, stays loaded until evicted, so the green waits for the eviction
        private void PollPendingModelUnloads()
        {
            var finished = new List<int>();
            foreach (KeyValuePair<int, PendingModelUnload> entry in pendingModelUnloads)
            {
                PendingModelUnload pending = entry.Value;
                if (World.GetAllVehicles(pending.Model).Length > 0)
                {
                    Function.Call(Hash.SET_VEHICLE_MODEL_IS_SUPPRESSED, pending.Model.Hash, false);
                    finished.Add(entry.Key);
                    CancelOutstandingEviction(pending);
                    logger.Log(Dbg.Level.Warn, Dbg.UnloadAborted(pending.ModelName, pending.Model.Hash));
                    notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + entry.Key, string.Format(NOTIF_UNLOAD_ABORTED, pending.ModelName));
                    continue;
                }
                if (pending.Model.IsLoaded)
                {
                    pending.UnloadedGameTime = 0;
                    continue;
                }
                if (pending.UnloadedGameTime == 0) pending.UnloadedGameTime = Game.GameTime;
                AdvanceFragmentEviction(pending);
                // An evicted fragment ends the unload at once, since the next load reads it fresh from disk and rebakes
                if (!pending.FragmentEvicted && Game.GameTime - pending.UnloadedGameTime < UNLOAD_SETTLE_MS) continue;
                Function.Call(Hash.SET_VEHICLE_MODEL_IS_SUPPRESSED, pending.Model.Hash, false);
                finished.Add(entry.Key);
                CancelOutstandingEviction(pending);
                // Without the plugin, or when it could not find the streaming functions, the unload ends via the old process
                bool refreshExpected = pending.RequestEviction == null || pending.FragmentEvicted || pending.LastEvictionOutcome == FragmentEvictionOutcome.Unavailable;
                if (refreshExpected)
                {
                    logger.Log(Dbg.Level.Info, Dbg.UnloadDone(pending.ModelName, pending.Model.Hash, Game.GameTime - pending.StartGameTime));
                    bool respawnsVehicle = pending.FragmentEvicted && pending.Respawn != null && Game.GameTime - pending.StartGameTime <= UNLOAD_AUTO_RESPAWN_WINDOW_MS;
                    if (respawnsVehicle) pendingUnloadRespawns.Add(new PendingUnloadRespawn { Model = pending.Model, ModelName = pending.ModelName, Snapshot = pending.Respawn, StartGameTime = pending.StartGameTime });
                    else
                    {
                        notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + entry.Key, string.Format(NOTIF_UNLOAD_COMPLETE, pending.ModelName));
                        PlayUnloadSuccessSound();
                    }
                }
                else
                {
                    logger.Log(Dbg.Level.Warn, Dbg.UnloadFragmentNotEvicted(pending.ModelName, pending.Model.Hash, Game.GameTime - pending.StartGameTime, pending.LastEvictionOutcome.ToString()));
                    notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + entry.Key, string.Format(NOTIF_UNLOAD_NOT_GUARANTEED, pending.ModelName));
                }
            }
            if (finished.Count == 0) return;
            for (int i = 0; i < finished.Count; i++) pendingModelUnloads.Remove(finished[i]);
            if (pendingModelUnloads.Count == 0) SetUnloadSpawningPaused(false);
            SyncActiveActionEnablement();
        }

        // The model is requested each poll until it loads, and a respawn that gives up releases the request, so none outlives its window
        // A player who died or got into another vehicle is never pulled out of it, so the respawn is skipped then
        private void PollPendingUnloadRespawns()
        {
            for (int i = pendingUnloadRespawns.Count - 1; i >= 0; i--)
            {
                PendingUnloadRespawn respawn = pendingUnloadRespawns[i];
                Ped player = Game.LocalPlayerPed;
                string skipReason = null;
                if (Entity.IsNullOrNotExisting(player) || player.IsDead) skipReason = "Player is dead";
                else if (player.IsInVehicle()) skipReason = "Player entered another vehicle";
                else if (!respawn.Model.IsLoaded)
                {
                    if (Game.GameTime - respawn.StartGameTime <= UNLOAD_AUTO_RESPAWN_WINDOW_MS)
                    {
                        respawn.Model.Request();
                        continue;
                    }
                    skipReason = "Model reload took longer than the window";
                }
                pendingUnloadRespawns.RemoveAt(i);
                if (skipReason == null)
                {
                    RespawnAfterUnload(respawn, player);
                    continue;
                }
                respawn.Model.MarkAsNoLongerNeeded();
                logger.Log(Dbg.Level.Info, Dbg.UnloadRespawnSkipped(respawn.ModelName, respawn.Model.Hash, skipReason));
                notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + respawn.Model.Hash, string.Format(NOTIF_UNLOAD_COMPLETE, respawn.ModelName));
                PlayUnloadSuccessSound();
            }
        }

        // The vehicle keeps its position and motion from the confirm, since UNLOAD_AUTO_RESPAWN_WINDOW_MS leaves the player no time to have moved on
        // No tree re-scopes onto it: The delete already cleared the dead target, and seat tracking retargets Current once the player is seated
        private void RespawnAfterUnload(PendingUnloadRespawn respawn, Ped player)
        {
            Vehicle newVehicle = World.CreateVehicle(respawn.Model, respawn.Snapshot.Position, respawn.Snapshot.Heading);
            if (Entity.IsNullOrNotExisting(newVehicle))
            {
                logger.Log(Dbg.Level.Error, Dbg.RespawnCreateFailed(respawn.ModelName));
                return;
            }
            RestoreRespawnSnapshot(respawn.Snapshot, newVehicle, player, null);
            logger.Log(Dbg.Level.Info, Dbg.UnloadRespawned(respawn.ModelName, respawn.Model.Hash, Game.GameTime - respawn.StartGameTime));
            notifications.Notify(NOTIFICATION_KEY_PREFIX_UNLOAD + respawn.Model.Hash, string.Format(NOTIF_UNLOAD_RESPAWNED, respawn.ModelName));
            PlayUnloadSuccessSound();
        }

        // A plugin built before the cancel export existed reads as having no eviction exports, so its unload ends without eviction
        private static bool ResolveFragmentEviction(out FragmentEvictionFunction requestEviction, out FragmentEvictionFunction readEvictionOutcome, out FragmentEvictionFunction cancelEviction, out string unavailableReason)
        {
            requestEviction = null;
            readEvictionOutcome = null;
            cancelEviction = null;
            unavailableReason = null;
            Delegate[] functions;
            string[] exports = { REQUEST_FRAGMENT_EVICTION_EXPORT, READ_FRAGMENT_EVICTION_OUTCOME_EXPORT, CANCEL_FRAGMENT_EVICTION_EXPORT };
            PluginBridge.BindResult result = PluginBridge.BindExports(RAGE_MEMORY_BRIDGE_MODULE_NAME, exports, typeof(FragmentEvictionFunction), out functions);
            if (result != PluginBridge.BindResult.Bound)
            {
                unavailableReason = PluginBridge.DescribeFailure(result, RAGE_MEMORY_BRIDGE_PLUGIN_NAME, "eviction");
                return false;
            }
            requestEviction = (FragmentEvictionFunction)functions[0];
            readEvictionOutcome = (FragmentEvictionFunction)functions[1];
            cancelEviction = (FragmentEvictionFunction)functions[2];
            return true;
        }

        // The plugin takes a request on its next frame, inside the game's own script update, since the removal frees memory through the game thread's allocator
        // The first unloaded poll only requests, since a read in the same poll could only read Pending
        // An outcome is handed out once and forgotten, so none left from an earlier unload answers this one
        private void AdvanceFragmentEviction(PendingModelUnload pending)
        {
            if (pending.RequestEviction == null || pending.FragmentEvicted || pending.EvictionEnded) return;
            uint modelHash = unchecked((uint)pending.Model.Hash);
            if (!pending.EvictionRequested)
            {
                QueueFragmentEviction(pending, modelHash);
                return;
            }
            var outcome = (FragmentEvictionOutcome)pending.ReadEvictionOutcome(modelHash);
            if (outcome == FragmentEvictionOutcome.Pending) return;
            pending.EvictionOutstanding = false;
            RecordEvictionOutcome(pending, outcome);
            if (outcome == FragmentEvictionOutcome.Removed || outcome == FragmentEvictionOutcome.NotLoaded) pending.FragmentEvicted = true;
            else if (outcome == FragmentEvictionOutcome.NotReady || outcome == FragmentEvictionOutcome.Refused) QueueFragmentEviction(pending, modelHash);
            else pending.EvictionEnded = true;
        }

        private void QueueFragmentEviction(PendingModelUnload pending, uint modelHash)
        {
            pending.EvictionRequested = true;
            pending.EvictionOutstanding = true;
            pending.RequestEviction(modelHash);
        }

        // An unload that ends with a request still unread cancels it, so the plugin keeps no outcome for the next unload of this model
        // The answer is dropped: A finished outcome leaves with the entry, and an attempt already running finishes on its own and is overwritten by the next request
        private static void CancelOutstandingEviction(PendingModelUnload pending)
        {
            if (!pending.EvictionOutstanding || pending.CancelEviction == null) return;
            pending.EvictionOutstanding = false;
            pending.CancelEviction(unchecked((uint)pending.Model.Hash));
        }

        // One INFO line per outcome change, since a retried NotReady would otherwise log every poll
        private void RecordEvictionOutcome(PendingModelUnload pending, FragmentEvictionOutcome outcome)
        {
            if (outcome == pending.LastEvictionOutcome) return;
            pending.LastEvictionOutcome = outcome;
            logger.Log(Dbg.Level.Info, Dbg.UnloadEvictionOutcome(pending.ModelName, pending.Model.Hash, outcome.ToString()));
        }

        // Parked and scenario car generators ignore model suppression when they reuse a model that is still loaded, and fixed-model generators never check it
        // For that reason every generator, map and scenario alike, is switched off while any unload waits
        // The game stores the switched-off box, so generators that stream in later start switched off too
        // Switching the same box back on removes only that stored entry, leaving any area another script switched off in place
        private void SetUnloadSpawningPaused(bool disabled)
        {
            // The vehicle population budget drops to 0 as well, which keeps the streamer over budget so it streams traffic models out every frame
            // No native reads the current budget back, so the restore always writes the game's default
            int vehicleBudget = disabled ? 0 : DEFAULT_VEHICLE_POPULATION_BUDGET;
            Function.Call(Hash.SET_VEHICLE_POPULATION_BUDGET, vehicleBudget);
            logger.Log(Dbg.Level.Info, Dbg.UnloadVehicleBudget(vehicleBudget));
            Function.Call(Hash.SET_ALL_VEHICLE_GENERATORS_ACTIVE_IN_AREA, -CAR_GENERATOR_BOX_HALF_SIZE, -CAR_GENERATOR_BOX_HALF_SIZE, -CAR_GENERATOR_BOX_HALF_SIZE, CAR_GENERATOR_BOX_HALF_SIZE, CAR_GENERATOR_BOX_HALF_SIZE, CAR_GENERATOR_BOX_HALF_SIZE, !disabled, false);
            logger.Log(Dbg.Level.Info, Dbg.UnloadCarGenerators(disabled));
        }

        // A reload drops the pending set, so every model still waiting is returned to traffic before the script unloads
        private void UnsuppressPendingModelUnloads()
        {
            for (int i = 0; i < pendingUnloadRespawns.Count; i++)
            {
                pendingUnloadRespawns[i].Model.MarkAsNoLongerNeeded();
                logger.Log(Dbg.Level.Info, Dbg.UnloadRespawnSkipped(pendingUnloadRespawns[i].ModelName, pendingUnloadRespawns[i].Model.Hash, "Script reload"));
            }
            pendingUnloadRespawns.Clear();
            foreach (PendingModelUnload pending in pendingModelUnloads.Values)
            {
                Function.Call(Hash.SET_VEHICLE_MODEL_IS_SUPPRESSED, pending.Model.Hash, false);
                CancelOutstandingEviction(pending);
                logger.Log(Dbg.Level.Info, Dbg.UnloadUnsuppressedOnReload(pending.ModelName, pending.Model.Hash));
            }
            if (pendingModelUnloads.Count == 0) return;
            SetUnloadSpawningPaused(false);
            // Posted with no tracked expiry, so it outlives the script instance and confirms every pending model was returned to traffic
            notifications.PostNotification(string.Format(NOTIF_RELOAD_UNLOADS_CANCELED, pendingModelUnloads.Count), 0);
            pendingModelUnloads.Clear();
        }
    }
}
