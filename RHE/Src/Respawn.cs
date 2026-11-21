using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Auto Respawn's modes: No respawn, a respawn once the navigation hold ends, or one AUTO_RESPAWN_DELAY_MS after the last Left or Right step that asked for it
        // Every other edit respawns at once in both modes
        private enum AutoRespawnMode { Off, Instant, Delayed }
        // The vehicle a held cycle of a spawn-copied field last edited. It is refreshed once when the hold ends, not on every step
        private Vehicle pendingSpawnRefreshVehicle;
        // The follow-up work a replayed edit (Undo, Redo, Undo All, Selective Copy, applying a handling) asks of its caller
        // That is a refresh of a spawn-copied field, or a respawn for a field or flag the game only reads at spawn
        // Each applied value returns its own flags, and the replay path combines them and acts on the total with its own resolved vehicle
        [Flags]
        private enum ReplayFlags { None = 0, SpawnRefresh = 1, Respawn = 2 }
        // The vehicle an edit asked to respawn. It respawns once when no navigation key is held, so a held cycle respawns once on release
        private Vehicle pendingAutoRespawnVehicle;
        // The game time the pending respawn may fire at. It is the request time, or in Delayed mode the last Left or Right step plus AUTO_RESPAWN_DELAY_MS
        private int pendingAutoRespawnDueTime;
        // True while the pending respawn came only from Left or Right steps, so it can still cancel. Any edit that respawns at once clears it
        private bool pendingAutoRespawnFromCycle;
        // Each cycled field's value from before its first pending step, by field key. The pending respawn cancels once every one is back
        private readonly Dictionary<string, string> pendingAutoRespawnBaselines = new Dictionary<string, string>();
        // Set by OnAborted before its cycle batch flush, so an unload never requests or performs a respawn
        private bool autoRespawnBlockedByUnload;
        // The vehicle whose selected weapon a live slot edit replaced, and the hash that replaced it
        private int followedWeaponVehicleHandle;
        private VehicleWeaponHash followedVehicleWeapon;
        // A respawned vehicle's motion, written again on the next tick, since a mod that moves a fresh vehicle's bones in its first update can drop its speed
        // The second write also smooths the hand-off on a vehicle that keeps its speed
        // Null while nothing waits
        private Vehicle pendingMotionVehicle;
        private int pendingMotionFrame;
        private float pendingMotionSpeed;
        private GTA.Math.Vector3 pendingMotionVelocity;
        private GTA.Math.Vector3 pendingMotionAngularVelocity;

        // Respawns the Active editor's vehicle. The Respawn Vehicle row and hotkey fire from mainMenu, where no tree is visible to pick a vehicle
        private void RespawnVehicle()
        {
            RespawnVehicle(activeContext.TargetVehicle);
        }

        // Every value a respawn carries from the old vehicle to the new one, captured while the old vehicle still exists
        // An unload captures it at its confirm and restores it once the model reloads, long after that vehicle was deleted
        private sealed class RespawnSnapshot
        {
            public Model Model;
            public int Handle;
            public Vector3 Position;
            public float Heading;
            public Quaternion Quaternion;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float Throttle;
            public float Speed;
            public Dictionary<VehicleModType, int> ModIndices;
            public VehicleColor PrimaryColor;
            public VehicleColor SecondaryColor;
            public VehicleColor RimColor;
            public VehicleColor PearlescentColor;
            public VehicleColor TrimColor;
            public VehicleColor DashboardColor;
            public VehicleWheelType WheelType;
            public VehicleWindowTint WindowTint;
            public string Plate;
            public int Livery;
            public Color TireSmokeColor;
            public Color NeonLightsColor;
            public bool NeonLeftOn;
            public bool NeonRightOn;
            public bool NeonFrontOn;
            public bool NeonBackOn;
            public bool XenonHeadlightsInstalled;
            public bool HydraulicsInstalled;
            public bool CanTiresBurst;
            public bool HadRoof;
            public VehicleRoofState RoofState;
            public bool LightsOn;
            public bool HighBeamsOn;
            public Dictionary<VehicleExtraIndex, bool> ExtraStates;
            public bool WasPersistent;
            public float SpecialFlightCurrentRatio;
            public float SpecialFlightTargetRatio;
            public float SpecialFlightWingRatio;
            // -1 when the vehicle has no glider or its state could not be read
            public int GliderState;
            // Only valid while WingDeployedRatioCaptured is True
            public float WingDeployedRatio;
            public bool WingDeployedRatioCaptured;
            public float HeliBladesSpeed;
            public bool IsPlane;
            public bool PlaneHasLandingGear;
            public VehicleLandingGearState LandingGearState;
            public float NozzlePosition;
            public float EngineWarmUpTime;
            public bool PlaneEngineRunningInAir;
            // A fresh vehicle starts in car mode with its wheels out, so a submarine car's dive mode and a jet ski's tucked wheels would both be lost
            public bool WasInSubmarineMode;
            public bool HadWheelsRetracted;
            public bool PlayerWasInVehicle;
            public VehicleSeat PlayerSeat;
            public VehicleWeaponHash PlayerVehicleWeapon;
        }

        private void RespawnVehicle(Vehicle oldVehicle)
        {
            Ped player = Game.LocalPlayerPed;
            if (Entity.IsNullOrNotExisting(player) || Entity.IsNullOrNotExisting(oldVehicle))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_NO_VEHICLE_RESPAWN, NOTIF_NO_VEHICLE_RESPAWN);
                return;
            }
            // Deleting any carriage of a running train crashes the game, so the row, the hotkey, and an automatic respawn all stop here
            if (oldVehicle.IsTrain)
            {
                logger.Log(Dbg.Level.Info, Dbg.RespawnTrainRefused(modelNames.ResolveModelName(oldVehicle), oldVehicle.Model.Hash));
                notifications.Notify(NOTIFICATION_KEY_TRAIN_REFUSED, NOTIF_TRAIN_REFUSED);
                return;
            }
            RespawnSnapshot snapshot = CaptureRespawnSnapshot(oldVehicle, player);
            try
            {
                snapshot.Model.Request();
                if (!snapshot.Model.IsLoaded)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.RespawnModelNotLoaded());
                    notifications.PostNotificationOnce(NOTIFICATION_KEY_RESPAWN_LOADING, NOTIF_RESPAWN_MODEL_LOADING);
                    return;
                }

                // The replacement is created and confirmed before the original is destroyed
                // The two overlap for the rest of this frame, which beats losing the vehicle outright when creation fails
                Vehicle newVehicle = World.CreateVehicle(snapshot.Model, snapshot.Position, snapshot.Heading);
                if (Entity.IsNullOrNotExisting(newVehicle))
                {
                    logger.Log(Dbg.Level.Error, Dbg.RespawnCreateFailed(modelNames.ResolveModelName(oldVehicle)));
                    return;
                }

                // The tree tracking the old vehicle is matched by handle, not activeContext, since an auto respawn can target a vehicle that is not the Active one
                // The match must run before the delete: Delete zeroes the Handle of the wrapper it is called on, and the trees hold that same wrapper
                EditorContext respawnedContext = null;
                for (int i = 0; i < editorContexts.Length; i++)
                {
                    Vehicle treeVehicle = editorContexts[i].TargetVehicle;
                    if (treeVehicle != null && treeVehicle.Handle == snapshot.Handle)
                    {
                        respawnedContext = editorContexts[i];
                        break;
                    }
                }
                visualizer.CarryVisualizationStateToRespawn(snapshot.Handle, newVehicle.Handle);
                oldVehicle.Delete();
                RestoreRespawnSnapshot(snapshot, newVehicle, player, respawnedContext);
                logger.Log(Dbg.Level.Info, Dbg.RespawnDone(modelNames.ResolveModelName(newVehicle), snapshot.Model.Hash));
                notifications.Notify(NOTIFICATION_KEY_RESPAWN, string.Format(NOTIF_RESPAWNED, modelNames.ResolveModelName(newVehicle)));
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.RespawnFailed(ex.Message)); }
        }

        private RespawnSnapshot CaptureRespawnSnapshot(Vehicle oldVehicle, Ped player)
        {
            var snapshot = new RespawnSnapshot();
            snapshot.Model = oldVehicle.Model;
            snapshot.Handle = oldVehicle.Handle;
            snapshot.Position = oldVehicle.Position;
            snapshot.Heading = oldVehicle.Heading;
            snapshot.Quaternion = oldVehicle.Quaternion;
            snapshot.Velocity = oldVehicle.Velocity;
            snapshot.AngularVelocity = oldVehicle.LocalRotationVelocity;
            snapshot.Throttle = oldVehicle.Throttle;
            snapshot.Speed = oldVehicle.Speed;
            // VehicleModCollection.ToArray() filters by GET_NUM_VEHICLE_MODS > 0, which is confirmed unreliable for wheel slots
            // A real custom FrontWheel index can still read back as GET_NUM_VEHICLE_MODS == 0, silently excluding it from that filter
            // Enumerating every VehicleModType directly and reading .Index unconditionally bypasses that filter
            snapshot.ModIndices = new Dictionary<VehicleModType, int>();
            foreach (VehicleModType modType in Enum.GetValues(typeof(VehicleModType)))
            {
                int modIndex = oldVehicle.Mods[modType].Index;
                if (modIndex >= 0) snapshot.ModIndices[modType] = modIndex;
            }
            snapshot.PrimaryColor = oldVehicle.Mods.PrimaryColor;
            snapshot.SecondaryColor = oldVehicle.Mods.SecondaryColor;
            snapshot.RimColor = oldVehicle.Mods.RimColor;
            snapshot.PearlescentColor = oldVehicle.Mods.PearlescentColor;
            snapshot.TrimColor = oldVehicle.Mods.TrimColor;
            snapshot.DashboardColor = oldVehicle.Mods.DashboardColor;
            snapshot.WheelType = oldVehicle.Mods.WheelType;
            snapshot.WindowTint = oldVehicle.Mods.WindowTint;
            snapshot.Plate = oldVehicle.Mods.LicensePlate;
            snapshot.Livery = oldVehicle.Mods.Livery;
            snapshot.TireSmokeColor = oldVehicle.Mods.TireSmokeColor;
            snapshot.NeonLightsColor = oldVehicle.Mods.NeonLightsColor;
            snapshot.NeonLeftOn = oldVehicle.Mods.IsNeonLightsOn(VehicleNeonLight.Left);
            snapshot.NeonRightOn = oldVehicle.Mods.IsNeonLightsOn(VehicleNeonLight.Right);
            snapshot.NeonFrontOn = oldVehicle.Mods.IsNeonLightsOn(VehicleNeonLight.Front);
            snapshot.NeonBackOn = oldVehicle.Mods.IsNeonLightsOn(VehicleNeonLight.Back);
            snapshot.XenonHeadlightsInstalled = oldVehicle.Mods[VehicleToggleModType.XenonHeadlights].IsInstalled;
            snapshot.HydraulicsInstalled = oldVehicle.Mods[VehicleToggleModType.Hydraulics].IsInstalled;
            snapshot.CanTiresBurst = oldVehicle.CanTiresBurst;
            snapshot.HadRoof = oldVehicle.HasRoof;
            snapshot.RoofState = oldVehicle.RoofState;
            snapshot.LightsOn = oldVehicle.AreLightsOn;
            snapshot.HighBeamsOn = oldVehicle.AreHighBeamsOn;
            snapshot.ExtraStates = new Dictionary<VehicleExtraIndex, bool>();
            foreach (VehicleExtraIndex extraIndex in Enum.GetValues(typeof(VehicleExtraIndex)))
            {
                VehicleExtra extra = oldVehicle.Extras[extraIndex];
                if (extra != null) snapshot.ExtraStates[extraIndex] = extra.Enabled;
            }
            snapshot.WasPersistent = oldVehicle.IsPersistent;
            // Flight state a fresh vehicle starts without. Each getter reads 0 on a vehicle or build without that system
            snapshot.SpecialFlightCurrentRatio = oldVehicle.SpecialFlightModeCurrentRatio;
            snapshot.SpecialFlightTargetRatio = oldVehicle.SpecialFlightModeTargetRatio;
            snapshot.SpecialFlightWingRatio = oldVehicle.SpecialFlightModeWingRatio;
            // A new vehicle starts both at retracted, so the wings would replay their deploy from closed
            // Each is kept only when it reads inside its own range, so a misread never drives the restore
            snapshot.GliderState = -1;
            int gliderState;
            if (RawMemory.RawOffsetsSupported && RawMemory.HasGlider(oldVehicle) && RawMemory.ReadRawInt32(oldVehicle.MemoryAddress, VehicleLayout.GliderState, out gliderState) && gliderState >= 0 && gliderState <= GLIDER_STATE_MAX) snapshot.GliderState = gliderState;
            float wingDeployedRatio;
            snapshot.WingDeployedRatioCaptured = RawMemory.ReadRawFloat(oldVehicle.MemoryAddress, VehicleLayout.WingDeployedRatio, out wingDeployedRatio) && wingDeployedRatio >= WING_DEPLOYED_RATIO_RETRACTED && wingDeployedRatio <= WING_DEPLOYED_RATIO_DEPLOYED;
            snapshot.WingDeployedRatio = wingDeployedRatio;
            // 0 to 1, the same ratio SET_HELI_BLADES_SPEED takes, and 0 for anything that is not rotary-wing
            snapshot.HeliBladesSpeed = oldVehicle.HeliBladesSpeed;
            snapshot.IsPlane = oldVehicle.IsPlane;
            snapshot.PlaneHasLandingGear = snapshot.IsPlane && Function.Call<bool>(Hash.GET_VEHICLE_HAS_LANDING_GEAR, oldVehicle.Handle);
            snapshot.LandingGearState = snapshot.PlaneHasLandingGear ? oldVehicle.LandingGearState : VehicleLandingGearState.Deployed;
            snapshot.NozzlePosition = snapshot.IsPlane ? Function.Call<float>(Hash.GET_VEHICLE_FLIGHT_NOZZLE_POSITION, oldVehicle.Handle) : 0.0f;
            // A new plane restarts this at 0, and its thrust scales with it, so a respawn in the air would lose lift for up to 5 seconds
            snapshot.EngineWarmUpTime = snapshot.IsPlane ? RawMemory.ReadRawFloat(oldVehicle.MemoryAddress, VehicleLayout.EngineWarmUpTimer) : 0.0f;
            snapshot.PlaneEngineRunningInAir = snapshot.IsPlane && oldVehicle.IsInAir && oldVehicle.IsEngineRunning;
            // Each native is asked only of the one vehicle type it accepts, since the game rejects the call for any other
            // The retract getter reads True while the wheels move either way, so a respawn mid-transition lands on the side the game reports
            snapshot.WasInSubmarineMode = oldVehicle.IsSubmarineCar && Function.Call<bool>(Hash.IS_VEHICLE_IN_SUBMARINE_MODE, oldVehicle.Handle);
            snapshot.HadWheelsRetracted = oldVehicle.IsAmphibiousQuadBike && Function.Call<bool>(Hash.GET_IS_WHEELS_RETRACTED, oldVehicle.Handle);
            // Compares Handle, the vehicle's real identity, since both wrappers are fetched independently. The caller confirmed oldVehicle exists
            Vehicle currentPlayerVehicle = player.CurrentVehicle;
            snapshot.PlayerWasInVehicle = !Entity.IsNullOrNotExisting(currentPlayerVehicle) && currentPlayerVehicle.Handle == oldVehicle.Handle;
            // Captured now so the player is restored to the seat they actually occupied, not always the driver's
            // None only appears if IsInVehicle briefly disagrees with the handle match above, an edge case defaulted to Driver as the safest fallback
            snapshot.PlayerSeat = snapshot.PlayerWasInVehicle && player.SeatIndex != VehicleSeat.None ? player.SeatIndex : VehicleSeat.Driver;
            snapshot.PlayerVehicleWeapon = snapshot.PlayerWasInVehicle ? ResolveSelectedVehicleWeapon(oldVehicle, player) : VehicleWeaponHash.Invalid;
            return snapshot;
        }

        // The game builds a vehicle's weapons at spawn, so a slot edit leaves the player reading the replaced weapon, which the respawned vehicle no longer has
        // A followed hash the handling stopped listing (an undo of the edit) falls back to what the game reads
        private VehicleWeaponHash ResolveSelectedVehicleWeapon(Vehicle vehicle, Ped player)
        {
            if (followedWeaponVehicleHandle != vehicle.Handle) return player.VehicleWeapon;
            VehicleWeaponHandlingData weaponData = vehicle.HandlingData.VehicleWeaponHandlingData;
            return weaponData != null && Array.IndexOf(weaponData.WeaponHash, followedVehicleWeapon) >= 0 ? followedVehicleWeapon : player.VehicleWeapon;
        }

        // Only the vehicle the player sits in has a selected weapon. A slot cleared to unset stops following, so the respawn lands on the game's own pick
        private void FollowSelectedVehicleWeapon(Vehicle vehicle, VehicleWeaponHash oldHash, VehicleWeaponHash newHash)
        {
            Ped player = Game.LocalPlayerPed;
            if (Entity.IsNullOrNotExisting(player) || Entity.IsNullOrNotExisting(vehicle) || !player.IsInVehicle(vehicle)) return;
            if (ResolveSelectedVehicleWeapon(vehicle, player) != oldHash) return;
            followedWeaponVehicleHandle = IsUnsetWeaponHash(newHash) ? 0 : vehicle.Handle;
            followedVehicleWeapon = newHash;
        }

        // Everything after the delete runs in independently guarded stages
        // The seat and the editor's target come first, then the looks, then the engine and flight state, and the motion last
        // A throw in one stage costs only what that stage restores and never skips a later stage
        // A null respawnedContext skips the re-scope
        private void RestoreRespawnSnapshot(RespawnSnapshot snapshot, Vehicle newVehicle, Ped player, EditorContext respawnedContext)
        {
            try
            {
                // Full 3D orientation, not just yaw. Restored before the seat, same tier as position/heading
                newVehicle.Quaternion = snapshot.Quaternion;
                if (snapshot.PlayerWasInVehicle) player.SetIntoVehicle(newVehicle, snapshot.PlayerSeat);
            }
            catch (Exception seatEx) { logger.Log(Dbg.Level.Error, Dbg.RespawnStageFailed("Seat", seatEx.Message)); }

            // Whichever tree tracked the old instance re-scopes onto the new one even if the seat stage failed, since a tree left on the deleted wrapper unmounts as dead
            // Seat tracking catches a seated respawn on its own next tick, so Current's branch only covers the frozen-while-editing case
            try
            {
                if (respawnedContext != null)
                {
                    // Current's sync treats a vehicle other than its last synced one as a vehicle change, which would clear its asserted fields
                    if (respawnedContext == currentContext) currentContext.LastSyncedVehicle = newVehicle;
                    else respawnedContext.MainMenuItem.Title = string.Format(TITLE_EDIT_SELECTED, modelNames.ResolveModelName(newVehicle));
                    respawnedContext.TargetVehicle = newVehicle;
                    try { SyncEditorContext(respawnedContext); }
                    catch (Exception syncEx) { logger.Log(Dbg.Level.Warn, Dbg.RespawnSyncFailed(syncEx.Message)); }
                }
            }
            catch (Exception scopeEx) { logger.Log(Dbg.Level.Error, Dbg.RespawnStageFailed("Editor scope", scopeEx.Message)); }
            try
            {
                // A fresh vehicle needs a mod kit before its mod-index setters can apply values
                newVehicle.Mods.InstallModKit();
                newVehicle.Mods.PrimaryColor = snapshot.PrimaryColor;
                newVehicle.Mods.SecondaryColor = snapshot.SecondaryColor;
                newVehicle.Mods.RimColor = snapshot.RimColor;
                newVehicle.Mods.PearlescentColor = snapshot.PearlescentColor;
                newVehicle.Mods.TrimColor = snapshot.TrimColor;
                newVehicle.Mods.DashboardColor = snapshot.DashboardColor;
                newVehicle.Mods.WheelType = snapshot.WheelType;
                newVehicle.Mods.WindowTint = snapshot.WindowTint;
                newVehicle.Mods.LicensePlate = snapshot.Plate;
                newVehicle.Mods.Livery = snapshot.Livery;
                foreach (KeyValuePair<VehicleModType, int> modEntry in snapshot.ModIndices) newVehicle.Mods[modEntry.Key].Index = modEntry.Value;
                newVehicle.Mods.TireSmokeColor = snapshot.TireSmokeColor;
                newVehicle.Mods.NeonLightsColor = snapshot.NeonLightsColor;
                newVehicle.Mods.SetNeonLightsOn(VehicleNeonLight.Left, snapshot.NeonLeftOn);
                newVehicle.Mods.SetNeonLightsOn(VehicleNeonLight.Right, snapshot.NeonRightOn);
                newVehicle.Mods.SetNeonLightsOn(VehicleNeonLight.Front, snapshot.NeonFrontOn);
                newVehicle.Mods.SetNeonLightsOn(VehicleNeonLight.Back, snapshot.NeonBackOn);
                newVehicle.Mods[VehicleToggleModType.XenonHeadlights].IsInstalled = snapshot.XenonHeadlightsInstalled;
                newVehicle.Mods[VehicleToggleModType.Hydraulics].IsInstalled = snapshot.HydraulicsInstalled;
                newVehicle.CanTiresBurst = snapshot.CanTiresBurst;
                newVehicle.SetScriptedLightSetting(snapshot.LightsOn ? ScriptedVehicleLightSetting.SetVehicleLightsOn : ScriptedVehicleLightSetting.SetVehicleLightsOff);
                newVehicle.AreHighBeamsOn = snapshot.HighBeamsOn;
                // Only a convertible has a meaningful roof state to restore
                // RAISE_CONVERTIBLE_ROOF and LOWER_CONVERTIBLE_ROOF are unverified on a non-convertible, so gating here avoids relying on them
                if (snapshot.HadRoof) newVehicle.RoofState = snapshot.RoofState;
                foreach (KeyValuePair<VehicleExtraIndex, bool> extraState in snapshot.ExtraStates)
                {
                    VehicleExtra extra = newVehicle.Extras[extraState.Key];
                    if (extra != null) extra.Enabled = extraState.Value;
                }
                newVehicle.DirtLevel = 0.0f;
            }
            catch (Exception cosmeticsEx) { logger.Log(Dbg.Level.Error, Dbg.RespawnStageFailed("Cosmetics", cosmeticsEx.Message)); }
            try
            {
                // The seat and the extras toggle above both reset the center of gravity offset, so it is written after them
                HandlingData respawnHandling = newVehicle.HandlingData;
                if (snapshot.PlayerWasInVehicle && respawnHandling.IsValid) SyncCenterOfGravityOffset(respawnHandling, newVehicle);
                // Seating picks a weapon of its own, and the cosmetics stage's chassis, roof, or wing mods can pick again, so the selection is written after both
                if (snapshot.PlayerVehicleWeapon != VehicleWeaponHash.Invalid)
                {
                    bool weaponRestored = player.TrySetVehicleWeapon(snapshot.PlayerVehicleWeapon);
                    logger.Log(Dbg.Level.Info, Dbg.RespawnVehicleWeaponRestored(modelNames.ResolveModelName(newVehicle), snapshot.Model.Hash, snapshot.PlayerVehicleWeapon.ToString(), weaponRestored));
                }
                if (snapshot.WasPersistent) newVehicle.MarkAsMissionEntity();
                // The engine and flight state restore after seating, so entering the vehicle cannot reset them
                newVehicle.IsEngineRunning = true;
                // An instant engine start only reaches idle speed, so full speed is set and then settles to the throttle's own target at the normal rate
                if (snapshot.PlaneEngineRunningInAir) Function.Call(Hash.SET_HELI_BLADES_FULL_SPEED, newVehicle.Handle);
                // Only a value inside the timer's own range is copied, so a misread never writes a stray float into the plane
                if (snapshot.EngineWarmUpTime > 0.0f && snapshot.EngineWarmUpTime <= WARM_UP_MAX_SECONDS) RawMemory.WriteRawFloat(newVehicle.MemoryAddress, VehicleLayout.EngineWarmUpTimer, snapshot.EngineWarmUpTime);
                if (snapshot.HeliBladesSpeed > 0.0f) newVehicle.HeliBladesSpeed = snapshot.HeliBladesSpeed;
                // The property maps Retracted and Deployed to the instant commands, and a broken gear is left as the fresh vehicle has it
                if (snapshot.PlaneHasLandingGear && snapshot.LandingGearState != VehicleLandingGearState.Broken) newVehicle.LandingGearState = snapshot.LandingGearState;
                // The game ignores this on a plane without vertical flight mode
                if (snapshot.IsPlane) Function.Call(Hash.SET_VEHICLE_FLIGHT_NOZZLE_POSITION_IMMEDIATE, newVehicle.Handle, snapshot.NozzlePosition);
                bool hadSpecialFlight = snapshot.SpecialFlightCurrentRatio > 0.0f || snapshot.SpecialFlightTargetRatio > 0.0f || snapshot.SpecialFlightWingRatio > 0.0f;
                if (hadSpecialFlight)
                {
                    newVehicle.SpecialFlightModeCurrentRatio = snapshot.SpecialFlightCurrentRatio;
                    newVehicle.SpecialFlightModeTargetRatio = snapshot.SpecialFlightTargetRatio;
                    newVehicle.SpecialFlightModeWingRatio = snapshot.SpecialFlightWingRatio;
                }
                // The engine runs from above, since the game retracts a glider's wings whenever its engine is off
                bool hadGliderWingsOut = snapshot.GliderState == GLIDER_STATE_DEPLOYING_WINGS || snapshot.GliderState == GLIDER_STATE_GLIDING;
                if (hadGliderWingsOut) Function.Call(Hash.SET_GLIDER_ACTIVE, newVehicle.Handle, true);
                // The wings' drawn position does not jump with the hover wing ratio or the glider state above, so it is written after both
                if (snapshot.WingDeployedRatioCaptured && (hadSpecialFlight || hadGliderWingsOut))
                {
                    float wingDeployedRatio = snapshot.WingDeployedRatio > WING_DEPLOYED_RATIO_RETRACTED + WING_DEPLOYED_RATIO_NUDGE ? snapshot.WingDeployedRatio - WING_DEPLOYED_RATIO_NUDGE : snapshot.WingDeployedRatio;
                    RawMemory.WriteRawFloat(newVehicle.MemoryAddress, VehicleLayout.WingDeployedRatio, wingDeployedRatio);
                }
                if (hadSpecialFlight || hadGliderWingsOut || snapshot.HeliBladesSpeed > 0.0f || snapshot.IsPlane)
                {
                    string gearText = snapshot.PlaneHasLandingGear ? snapshot.LandingGearState.ToString() : "None";
                    logger.Log(Dbg.Level.Info, Dbg.RespawnFlightStateRestored(modelNames.ResolveModelName(newVehicle), snapshot.Model.Hash, snapshot.SpecialFlightCurrentRatio, snapshot.HeliBladesSpeed, gearText, snapshot.NozzlePosition, snapshot.GliderState, snapshot.WingDeployedRatioCaptured ? snapshot.WingDeployedRatio.ToString("F3") : "None"));
                }
                // Both switch at once with no transform animation
                if (snapshot.WasInSubmarineMode) Function.Call(Hash.TRANSFORM_TO_SUBMARINE, newVehicle.Handle, true);
                if (snapshot.HadWheelsRetracted) Function.Call(Hash.SET_WHEELS_RETRACTED_INSTANTLY, newVehicle.Handle);
                if (snapshot.WasInSubmarineMode || snapshot.HadWheelsRetracted) logger.Log(Dbg.Level.Info, Dbg.RespawnTransformRestored(modelNames.ResolveModelName(newVehicle), snapshot.Model.Hash, snapshot.WasInSubmarineMode ? "Submarine" : "WheelsRetracted"));
                EntityBone newTurretBone = newVehicle.Bones["turret_1base"];
                if (newTurretBone.Index != -1)
                {
                    // Tracks wherever the player is *currently* aiming, matching how a tank turret conventionally follows the aim direction during normal play
                    // Direction is a raw world-space read, so its heading is converted to the bone's hull-relative frame by subtracting the vehicle's heading
                    // It is then converted to radians, which SET_VEHICLE_TANK_TURRET_POSITION's position parameter takes
                    float cameraWorldHeading = GameplayCamera.Direction.ToHeading();
                    float turretTargetHeadingDegrees = cameraWorldHeading - newVehicle.Heading;
                    if (turretTargetHeadingDegrees > 180.0f) turretTargetHeadingDegrees -= 360.0f;
                    else if (turretTargetHeadingDegrees < -180.0f) turretTargetHeadingDegrees += 360.0f;
                    float turretHeadingRadians = turretTargetHeadingDegrees * DEG2RAD;
                    Function.Call(Hash.SET_VEHICLE_TANK_TURRET_POSITION, newVehicle.Handle, turretHeadingRadians, true);
                }
            }
            catch (Exception stateEx) { logger.Log(Dbg.Level.Error, Dbg.RespawnStageFailed("Engine and flight state", stateEx.Message)); }
            try
            {
                // Turning an extra on repairs the vehicle, and the game's repair zeroes any velocity above 80 m/s, so the motion is written after every other stage
                if (snapshot.PlayerWasInVehicle)
                {
                    newVehicle.CurrentGear = newVehicle.HighGear;
                    newVehicle.Throttle = snapshot.Throttle;
                    newVehicle.ForwardSpeed = snapshot.Speed;
                    newVehicle.Velocity = snapshot.Velocity;
                    newVehicle.LocalRotationVelocity = snapshot.AngularVelocity;
                    pendingMotionVehicle = newVehicle;
                    pendingMotionFrame = Game.FrameCount;
                    pendingMotionSpeed = snapshot.Speed;
                    pendingMotionVelocity = snapshot.Velocity;
                    pendingMotionAngularVelocity = snapshot.AngularVelocity;
                }
            }
            catch (Exception motionEx) { logger.Log(Dbg.Level.Error, Dbg.RespawnStageFailed("Motion", motionEx.Message)); }
        }

        // Runs first on every tick a motion waits, and writes it once the game has updated the vehicle at least once
        private void ReapplyRespawnMotion()
        {
            if (Game.FrameCount == pendingMotionFrame) return;
            Vehicle vehicle = pendingMotionVehicle;
            pendingMotionVehicle = null;
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            float speedBefore = vehicle.Speed;
            vehicle.ForwardSpeed = pendingMotionSpeed;
            vehicle.Velocity = pendingMotionVelocity;
            vehicle.LocalRotationVelocity = pendingMotionAngularVelocity;
            if (logger.IsInfoEnabled()) logger.Log(Dbg.Level.Info, Dbg.RespawnMotionReapplied(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash, speedBefore, vehicle.Speed));
        }

        // What the spawn refresh found: A kit already assigned, one it installed, or a model with no kit at all. The log names it with these words
        private enum ModKitState { Existing, Installed, None }

        // A vehicle with mod kits but none assigned gets kit 0, which changes nothing visible and lets the native re-apply the gearbox mod
        // fromCycle is True for the refresh a held Left or Right cycle queues, so its gear count respawn then waits and cancels like any other cycled edit
        private void RefreshSpawnCopiedFields(Vehicle vehicle, bool fromCycle)
        {
            if (Entity.IsNullOrNotExisting(vehicle)) return;
            ModKitState kitState = ModKitState.Existing;
            if (Function.Call<int>(Hash.GET_VEHICLE_MOD_KIT, vehicle.Handle) == NO_VEHICLE_MOD_KIT)
            {
                if (Function.Call<int>(Hash.GET_NUM_MOD_KITS, vehicle.Handle) > 0)
                {
                    vehicle.Mods.InstallModKit();
                    kitState = ModKitState.Installed;
                }
                else kitState = ModKitState.None;
            }
            vehicle.EnginePowerMultiplier = SPAWN_REFRESH_TOP_SPEED_PERCENT;
            Function.Call(Hash.SET_VEHICLE_GRAVITY, vehicle.Handle, true);
            logger.Log(Dbg.Level.Info, Dbg.SpawnRefreshDone(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash, kitState.ToString()));
            // With no mod kit the native cannot rebuild the gearbox, so a changed gear count only applies through a respawn
            if (kitState != ModKitState.None) return;
            int initialGears = vehicle.HandlingData.InitialDriveGears;
            if (fromCycle) RequestCycleAutoRespawn(vehicle, "InitialDriveGears", vehicle.HighGear.ToString(CultureInfo.InvariantCulture), initialGears.ToString(CultureInfo.InvariantCulture));
            else if (vehicle.HighGear != initialGears) RequestAutoRespawn(vehicle, "InitialDriveGears");
        }

        // The respawn fires once no navigation key is held. A request for a different vehicle respawns the earlier one first, so each respawns once
        private void RequestAutoRespawn(Vehicle vehicle, string trigger)
        {
            if (autoRespawnMode == AutoRespawnMode.Off || autoRespawnBlockedByUnload || Entity.IsNullOrNotExisting(vehicle)) return;
            bool alreadyPending = pendingAutoRespawnVehicle != null && pendingAutoRespawnVehicle.Handle == vehicle.Handle;
            if (pendingAutoRespawnVehicle != null && !alreadyPending) FlushPendingAutoRespawn();
            pendingAutoRespawnVehicle = vehicle;
            pendingAutoRespawnDueTime = Game.GameTime;
            pendingAutoRespawnFromCycle = false;
            pendingAutoRespawnBaselines.Clear();
            if (!alreadyPending) logger.Log(Dbg.Level.Info, Dbg.AutoRespawnRequested(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash, trigger, autoRespawnMode.ToString()));
        }

        // originalText is the field's value before the step and newText the value after it
        // A field's first pending step records originalText as its baseline, and the request cancels once every cycled field reads its baseline again
        // Delayed mode waits AUTO_RESPAWN_DELAY_MS after each step, and Instant mode waits only for the navigation hold to end
        // A respawn already asked for at once is never delayed
        private void RequestCycleAutoRespawn(Vehicle vehicle, string fieldKey, string originalText, string newText)
        {
            if (autoRespawnMode == AutoRespawnMode.Off || autoRespawnBlockedByUnload || Entity.IsNullOrNotExisting(vehicle)) return;
            bool alreadyPending = pendingAutoRespawnVehicle != null && pendingAutoRespawnVehicle.Handle == vehicle.Handle;
            if (alreadyPending && !pendingAutoRespawnFromCycle) return;
            if (pendingAutoRespawnVehicle != null && !alreadyPending) FlushPendingAutoRespawn();
            string baseline;
            if (!pendingAutoRespawnBaselines.TryGetValue(fieldKey, out baseline))
            {
                baseline = originalText;
                pendingAutoRespawnBaselines[fieldKey] = baseline;
            }
            if (CycleTextsMatch(baseline, newText))
            {
                pendingAutoRespawnBaselines.Remove(fieldKey);
                if (alreadyPending && pendingAutoRespawnBaselines.Count == 0) CancelPendingAutoRespawn(fieldKey);
                return;
            }
            pendingAutoRespawnVehicle = vehicle;
            pendingAutoRespawnFromCycle = true;
            pendingAutoRespawnDueTime = Game.GameTime + (autoRespawnMode == AutoRespawnMode.Delayed ? AUTO_RESPAWN_DELAY_MS : 0);
            if (!alreadyPending) logger.Log(Dbg.Level.Info, Dbg.AutoRespawnRequested(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash, fieldKey, autoRespawnMode.ToString()));
        }

        // Floats match within the drift tolerance, every other text exactly
        private static bool CycleTextsMatch(string first, string second)
        {
            float firstValue, secondValue;
            if (float.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out firstValue) && float.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out secondValue)) return Math.Abs(firstValue - secondValue) <= DRIFT_EPSILON;
            return string.Equals(first, second, StringComparison.Ordinal);
        }

        // Every undo and redo apply of a field calls this. When valueText puts a still-pending cycled field back to its baseline, the field leaves the baselines
        // The request cancels once none is left, and the True return tells the caller to discard the respawn request that apply raised
        private bool RevertsPendingCycleRespawn(string fieldKey, string valueText)
        {
            string baseline;
            if (pendingAutoRespawnVehicle == null || !pendingAutoRespawnFromCycle || !pendingAutoRespawnBaselines.TryGetValue(fieldKey, out baseline) || !CycleTextsMatch(baseline, valueText)) return false;
            pendingAutoRespawnBaselines.Remove(fieldKey);
            if (pendingAutoRespawnBaselines.Count == 0) CancelPendingAutoRespawn(fieldKey);
            return true;
        }

        private void CancelPendingAutoRespawn(string trigger)
        {
            Vehicle vehicle = pendingAutoRespawnVehicle;
            ClearPendingAutoRespawn();
            if (!Entity.IsNullOrNotExisting(vehicle)) logger.Log(Dbg.Level.Info, Dbg.AutoRespawnCanceled(modelNames.ResolveModelName(vehicle), vehicle.Model.Hash, trigger));
        }

        private void ClearPendingAutoRespawn()
        {
            pendingAutoRespawnVehicle = null;
            pendingAutoRespawnFromCycle = false;
            pendingAutoRespawnBaselines.Clear();
        }

        // A vehicle deleted since its request, or already respawned by hand, is skipped, so no stray vehicle is created
        private void FlushPendingAutoRespawn()
        {
            if (pendingAutoRespawnVehicle == null) return;
            Vehicle vehicle = pendingAutoRespawnVehicle;
            ClearPendingAutoRespawn();
            if (autoRespawnMode == AutoRespawnMode.Off || Entity.IsNullOrNotExisting(vehicle)) return;
            RespawnVehicle(vehicle);
        }

        // Held cycling requests instead of refreshing, so the refresh runs once when FlushAllPendingCycleBatches sees the hold end
        private void RequestSpawnRefresh(Vehicle vehicle)
        {
            if (pendingSpawnRefreshVehicle != null && pendingSpawnRefreshVehicle.Handle != vehicle.Handle) FlushPendingSpawnRefresh();
            pendingSpawnRefreshVehicle = vehicle;
        }

        private void FlushPendingSpawnRefresh()
        {
            if (pendingSpawnRefreshVehicle == null) return;
            Vehicle vehicle = pendingSpawnRefreshVehicle;
            pendingSpawnRefreshVehicle = null;
            RefreshSpawnCopiedFields(vehicle, true);
        }

        // The flags a changed field asks for: The refresh for a spawn-copied field, the respawn for a field the game only reads at spawn
        private static ReplayFlags ResolveReplayFlags(bool refreshesVehicle, bool requiresRespawn)
        {
            return (refreshesVehicle ? ReplayFlags.SpawnRefresh : ReplayFlags.None) | (requiresRespawn ? ReplayFlags.Respawn : ReplayFlags.None);
        }

        // Called by every replay path after it applies entries, with the vehicle that path resolved and the flags its entries combined
        // The refresh runs first, since it can request the respawn itself
        private void ConsumeReplayRefreshAndRespawn(Vehicle vehicle, string trigger, ReplayFlags flags)
        {
            if ((flags & ReplayFlags.SpawnRefresh) != 0) RefreshSpawnCopiedFields(vehicle, false);
            if ((flags & ReplayFlags.Respawn) != 0) RequestAutoRespawn(vehicle, trigger);
        }
    }
}
